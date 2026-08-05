using Nuke.Common;
using Serilog;

partial class Build
{
    /// <summary>
    /// In-process behavioral tests for the two-train Apple version split: the
    /// core <c>SwiftBindings.Apple</c> supplement this repo REFERENCES
    /// (<c>--apple-version</c>) versus the per-framework
    /// <c>SwiftBindings.Apple.*</c> bindings it PRODUCES
    /// (<c>--apple-bindings-version</c>).
    ///
    /// <para>
    /// The behavior under test is destructive by nature — it rewrites csprojs —
    /// so every case runs against a throwaway tree under the OS temp directory,
    /// never <c>apple-frameworks/</c>. The one case that does touch the real tree
    /// only READS it (copying the csprojs into scratch) and asserts afterwards
    /// that the originals are byte-identical, which is the property the whole
    /// suite has to preserve to be safe to run at any time.
    /// </para>
    ///
    /// <para>
    /// Same shape as <c>TestRegressionArtifacts</c>: pure assertions, no external
    /// test framework. Assertions are on behavior (which pin carries which train's
    /// version, which invocations throw) rather than on exact generated text, so a
    /// formatting change in the rewritten csproj doesn't red the suite.
    /// </para>
    /// </summary>
    Target TestAppleVersionSplit => _ => _
        .Description("Run in-process tests for the Apple core-supplement / per-framework-bindings version split")
        .Executes(() =>
        {
            var failures = new List<string>();
            var passed = 0;

            void Assert(string name, bool condition, string detail)
            {
                if (!condition) failures.Add($"  ✗ {name}: {detail}");
                else { passed++; Log.Information("  ✓ {Name}", name); }
            }

            void AssertThrows(string name, Action act, params string[] expectedFragments)
            {
                try
                {
                    act();
                    failures.Add($"  ✗ {name}: expected a throw, none happened");
                }
                catch (InvalidOperationException ex)
                {
                    var missing = expectedFragments.Where(f => !ex.Message.Contains(f, StringComparison.Ordinal)).ToList();
                    if (missing.Count > 0)
                        failures.Add($"  ✗ {name}: message did not mention {string.Join(", ", missing)} — got: {ex.Message}");
                    else { passed++; Log.Information("  ✓ {Name}", name); }
                }
            }

            var scratchRoot = Path.Combine(Path.GetTempPath(), "swiftbindings-apple-version-split-selftest");

            // One apple-framework-shaped csproj. `pins` is dropped in verbatim so a
            // case can express any pin form it needs (attribute, child element,
            // single-quoted, reordered attributes, or none at all).
            static string Csproj(string packageId, string? ownVersion, string pins = "") =>
                $"""
                <Project Sdk="SwiftBindings.Sdk/0.18.1">
                  <PropertyGroup>
                    <PackageId>{packageId}</PackageId>
                    {(ownVersion is null ? "" : $"<Version>{ownVersion}</Version>")}
                  </PropertyGroup>
                  <ItemGroup>
                {pins}
                  </ItemGroup>
                </Project>
                """;

            string Write(string dir, string fileName, string content)
            {
                var full = Path.Combine(scratchRoot, dir);
                Directory.CreateDirectory(full);
                var path = Path.Combine(full, fileName);
                File.WriteAllText(path, content);
                return path;
            }

            void ResetScratch()
            {
                if (Directory.Exists(scratchRoot)) Directory.Delete(scratchRoot, recursive: true);
                Directory.CreateDirectory(scratchRoot);
            }

            const string SiblingPin =
                @"    <PackageReference Include=""SwiftBindings.Apple.RealityFoundation"" Version=""{0}"" />";
            const string CorePin =
                @"    <PackageReference Include=""SwiftBindings.Apple"" Version=""{0}"" />";

            try
            {
                // ── Case 1: the defaulting rule ──
                // Omitting --apple-bindings-version must reproduce pre-split
                // behavior exactly, which is only true if it resolves to
                // --apple-version. Whitespace counts as omitted: Nuke hands
                // through an empty string for `--apple-bindings-version ""`, and
                // stamping "" into 17 csprojs would be worse than ignoring it.
                {
                    Assert("omitted bindings version falls back to --apple-version",
                        ResolveAppleBindingsVersion(null, "26.2.8") == "26.2.8",
                        ResolveAppleBindingsVersion(null, "26.2.8") ?? "<null>");
                    Assert("blank bindings version falls back to --apple-version",
                        ResolveAppleBindingsVersion("   ", "26.2.8") == "26.2.8",
                        ResolveAppleBindingsVersion("   ", "26.2.8") ?? "<null>");
                    Assert("an explicit bindings version wins",
                        ResolveAppleBindingsVersion("26.2.10", "26.2.8") == "26.2.10",
                        ResolveAppleBindingsVersion("26.2.10", "26.2.8") ?? "<null>");
                    Assert("neither train given resolves to nothing (the caller must fail loud)",
                        ResolveAppleBindingsVersion(null, null) is null,
                        ResolveAppleBindingsVersion(null, null) ?? "<null>");
                }

                // ── Case 2: one --apple-version still moves all three things ──
                // The compatibility contract: an invocation written before the
                // split must land the same tree it always did.
                {
                    ResetScratch();
                    var realityKit = Write("RealityKit", "SwiftBindings.Apple.RealityKit.csproj",
                        Csproj("SwiftBindings.Apple.RealityKit", "26.2.8",
                            string.Format(SiblingPin, "26.2.8") + "\n" + string.Format(CorePin, "26.2.8")));
                    Write("RealityFoundation", "SwiftBindings.Apple.RealityFoundation.csproj",
                        Csproj("SwiftBindings.Apple.RealityFoundation", "26.2.8"));

                    var single = ResolveAppleBindingsVersion(null, "26.2.9")!;
                    BumpAppleVersionInternal(single, "26.2.9", scratchRoot);

                    var text = File.ReadAllText(realityKit);
                    Assert("single-value bump moves the produced <Version>",
                        text.Contains("<Version>26.2.9</Version>", StringComparison.Ordinal), text);
                    Assert("single-value bump moves the sibling pin",
                        text.Contains(@"Include=""SwiftBindings.Apple.RealityFoundation"" Version=""26.2.9""",
                            StringComparison.Ordinal), text);
                    Assert("single-value bump moves the core supplement pin",
                        text.Contains(@"Include=""SwiftBindings.Apple"" Version=""26.2.9""",
                            StringComparison.Ordinal), text);
                    AssertNoThrow("single-value tree is coherent",
                        () => AssertCrossFrameworkCoherence("26.2.9", "26.2.9", scratchRoot), failures, ref passed);
                }

                // ── Case 3: the trains move independently ──
                // The whole point: a wave that ships new per-framework bindings
                // against an unchanged supplement must not restamp the supplement
                // reference, and must not leave the produced packages on the
                // supplement's already-published number.
                {
                    ResetScratch();
                    var realityKit = Write("RealityKit", "SwiftBindings.Apple.RealityKit.csproj",
                        Csproj("SwiftBindings.Apple.RealityKit", "26.2.9",
                            string.Format(SiblingPin, "26.2.9") + "\n" + string.Format(CorePin, "26.2.8")));
                    Write("RealityFoundation", "SwiftBindings.Apple.RealityFoundation.csproj",
                        Csproj("SwiftBindings.Apple.RealityFoundation", "26.2.9"));

                    BumpAppleVersionInternal("26.2.10", "26.2.8", scratchRoot);

                    var text = File.ReadAllText(realityKit);
                    Assert("produced <Version> follows the bindings train",
                        text.Contains("<Version>26.2.10</Version>", StringComparison.Ordinal), text);
                    Assert("sibling pin follows the bindings train",
                        text.Contains(@"Include=""SwiftBindings.Apple.RealityFoundation"" Version=""26.2.10""",
                            StringComparison.Ordinal), text);
                    Assert("core supplement pin stays on the core train",
                        text.Contains(@"Include=""SwiftBindings.Apple"" Version=""26.2.8""",
                            StringComparison.Ordinal), text);
                    Assert("no pin was crossed onto the other train",
                        !text.Contains(@"Include=""SwiftBindings.Apple"" Version=""26.2.10""", StringComparison.Ordinal)
                        && !text.Contains(@"Include=""SwiftBindings.Apple.RealityFoundation"" Version=""26.2.8""",
                            StringComparison.Ordinal),
                        text);
                    AssertNoThrow("split tree is coherent against its two trains",
                        () => AssertCrossFrameworkCoherence("26.2.8", "26.2.10", scratchRoot), failures, ref passed);

                    // Re-running must not touch a single byte — the bump is a
                    // pre-flight step and a spurious rewrite would churn mtimes
                    // (and, in the real tree, the release-candidate diff).
                    var before = File.ReadAllText(realityKit);
                    BumpAppleVersionInternal("26.2.10", "26.2.8", scratchRoot);
                    Assert("bump is idempotent across both trains",
                        File.ReadAllText(realityKit) == before, "re-running the bump changed the file");
                }

                // ── Case 4: the guard catches a pin left on the WRONG train ──
                // Both directions matter. A sibling stuck on the core number is
                // the failure the split introduces (it resolves to a published
                // package with different content); a core ref dragged onto the
                // bindings number is the failure a naive split introduces.
                {
                    ResetScratch();
                    Write("RealityKit", "SwiftBindings.Apple.RealityKit.csproj",
                        Csproj("SwiftBindings.Apple.RealityKit", "26.2.10",
                            string.Format(SiblingPin, "26.2.8")));
                    AssertThrows("guard rejects a sibling pin left on the core train",
                        () => AssertCrossFrameworkCoherence("26.2.8", "26.2.10", scratchRoot),
                        "SwiftBindings.Apple.RealityFoundation", "26.2.8", "expected 26.2.10",
                        "--apple-bindings-version");

                    ResetScratch();
                    Write("RealityKit", "SwiftBindings.Apple.RealityKit.csproj",
                        Csproj("SwiftBindings.Apple.RealityKit", "26.2.10",
                            string.Format(CorePin, "26.2.10")));
                    AssertThrows("guard rejects a core supplement pin dragged onto the bindings train",
                        () => AssertCrossFrameworkCoherence("26.2.8", "26.2.10", scratchRoot),
                        "expected 26.2.8", "--apple-version");
                }

                // ── Case 5: unreadable pins still fail loud, on either train ──
                // A child-element <Version> pin is rewritten by the bump's broad
                // element regex — with the BINDINGS version, whichever train the
                // pin belongs to — while discovery and packing never see it. That
                // must stay a hard error, not a silent skew.
                {
                    ResetScratch();
                    Write("RealityKit", "SwiftBindings.Apple.RealityKit.csproj",
                        Csproj("SwiftBindings.Apple.RealityKit", "26.2.10",
                            """
                                <PackageReference Include="SwiftBindings.Apple.RealityFoundation">
                                  <Version>26.2.10</Version>
                                </PackageReference>
                            """));
                    AssertThrows("guard rejects a sibling pin with no Version attribute",
                        () => AssertCrossFrameworkCoherence("26.2.8", "26.2.10", scratchRoot),
                        "SwiftBindings.Apple.RealityFoundation", "no Version attribute");

                    ResetScratch();
                    Write("RealityKit", "SwiftBindings.Apple.RealityKit.csproj",
                        Csproj("SwiftBindings.Apple.RealityKit", "26.2.10",
                            """
                                <PackageReference Include="SwiftBindings.Apple">
                                  <Version>26.2.8</Version>
                                </PackageReference>
                            """));
                    AssertThrows("guard rejects a core supplement pin with no Version attribute",
                        () => AssertCrossFrameworkCoherence("26.2.8", "26.2.10", scratchRoot),
                        "SwiftBindings.Apple", "no Version attribute");
                }

                // ── Case 6: a bindings-only bump leaves core references alone ──
                // `nuke BumpAppleVersion --apple-bindings-version X` names no core
                // train. Inventing one would silently repoint a reference at a
                // supplement version the caller never asked for.
                {
                    ResetScratch();
                    var realityKit = Write("RealityKit", "SwiftBindings.Apple.RealityKit.csproj",
                        Csproj("SwiftBindings.Apple.RealityKit", "26.2.9",
                            string.Format(SiblingPin, "26.2.9") + "\n" + string.Format(CorePin, "26.2.8")));
                    BumpAppleVersionInternal(ResolveAppleBindingsVersion("26.2.10", null)!, null, scratchRoot);

                    var text = File.ReadAllText(realityKit);
                    Assert("bindings-only bump moves the produced train",
                        text.Contains("<Version>26.2.10</Version>", StringComparison.Ordinal)
                        && text.Contains(@"Include=""SwiftBindings.Apple.RealityFoundation"" Version=""26.2.10""",
                            StringComparison.Ordinal),
                        text);
                    Assert("bindings-only bump leaves the core supplement pin untouched",
                        text.Contains(@"Include=""SwiftBindings.Apple"" Version=""26.2.8""", StringComparison.Ordinal),
                        text);
                }

                // ── Case 7: the missing-<Version> invariant survives the split ──
                {
                    ResetScratch();
                    Write("Broken", "SwiftBindings.Apple.Broken.csproj",
                        Csproj("SwiftBindings.Apple.Broken", ownVersion: null));
                    AssertThrows("a csproj with no <Version> element still hard-fails the bump",
                        () => BumpAppleVersionInternal("26.2.10", "26.2.8", scratchRoot),
                        "no <Version>NN.N.N</Version> element");
                }

                // ── Case 8: packing discovery never sees the core supplement ──
                // PackCrossFrameworkDependencies builds apple-frameworks/{DepName};
                // the core supplement has no such directory (it ships from the SDK
                // repo), so a core ref reaching discovery would send the packer at
                // a path that cannot exist.
                {
                    ResetScratch();
                    Write("RealityKit", "SwiftBindings.Apple.RealityKit.csproj",
                        Csproj("SwiftBindings.Apple.RealityKit", "26.2.10",
                            string.Format(SiblingPin, "26.2.10") + "\n" + string.Format(CorePin, "26.2.8")));
                    var discovered = DiscoverCrossFrameworkRefs(scratchRoot);
                    Assert("discovery returns the sibling dependency",
                        discovered.Any(r => r.DepName == "RealityFoundation" && r.Version == "26.2.10"),
                        string.Join(", ", discovered.Select(r => $"{r.DepName}@{r.Version}")));
                    Assert("discovery excludes the core supplement ref",
                        discovered.All(r => !string.IsNullOrEmpty(r.DepName)) && discovered.Count == 1,
                        string.Join(", ", discovered.Select(r => $"'{r.DepName}'@{r.Version}")));
                }

                // ── Case 9: the real apple-frameworks tree, split, in a copy ──
                // Shape-level proof on the actual release-candidate csprojs (17
                // frameworks, 2 sibling edges) without writing to them: they are
                // copied into scratch, bumped there, and the originals are hashed
                // before and after to show the suite cannot dirty the tree.
                {
                    ResetScratch();
                    var sources = Directory
                        .EnumerateFiles(AppleFrameworksDir, "*.csproj", SearchOption.AllDirectories)
                        .Where(f => File.ReadAllText(f).Contains("SwiftBindings.Sdk/", StringComparison.Ordinal))
                        .ToList();
                    var before = sources.ToDictionary(f => f, File.ReadAllText, StringComparer.Ordinal);

                    foreach (var src in sources)
                    {
                        var rel = Path.GetRelativePath(AppleFrameworksDir, src);
                        var dst = Path.Combine(scratchRoot, rel);
                        Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                        File.Copy(src, dst);
                    }

                    // Sentinel versions no published package uses, so a stray
                    // real-tree value can't make an assertion pass by accident.
                    BumpAppleVersionInternal("99.9.2", "99.9.1", scratchRoot);
                    AssertNoThrow("the real framework set is coherent after a split bump",
                        () => AssertCrossFrameworkCoherence("99.9.1", "99.9.2", scratchRoot), failures, ref passed);

                    var copies = Directory.EnumerateFiles(scratchRoot, "*.csproj", SearchOption.AllDirectories)
                        .Select(File.ReadAllText).ToList();
                    Assert("every real framework landed on the bindings train",
                        copies.Count == sources.Count && copies.All(c => c.Contains("<Version>99.9.2</Version>", StringComparison.Ordinal)),
                        $"{copies.Count(c => c.Contains("<Version>99.9.2</Version>", StringComparison.Ordinal))}/{sources.Count} stamped");
                    Assert("the real sibling edges landed on the bindings train",
                        DiscoverCrossFrameworkRefs(scratchRoot).Count >= 2
                        && DiscoverCrossFrameworkRefs(scratchRoot).All(r => r.Version == "99.9.2"),
                        string.Join(", ", DiscoverCrossFrameworkRefs(scratchRoot).Select(r => $"{r.DepName}@{r.Version}")));
                    Assert("the real apple-frameworks csprojs are byte-identical afterwards",
                        before.All(kv => File.ReadAllText(kv.Key) == kv.Value),
                        "a real csproj was modified by the self-test");
                }
            }
            finally
            {
                if (Directory.Exists(scratchRoot)) Directory.Delete(scratchRoot, recursive: true);
            }

            if (failures.Count > 0)
                throw new InvalidOperationException(
                    $"TestAppleVersionSplit: {failures.Count} assertion(s) failed:\n" + string.Join("\n", failures));

            Log.Information("TestAppleVersionSplit: {Passed} assertions passed.", passed);
        });

    /// <summary>
    /// Assert that an action completes without throwing. Separate from the
    /// inline <c>Assert</c> closures because a coherence guard signals success by
    /// silence, and an escaping exception would abort the whole suite with a
    /// stack trace instead of a named failure.
    /// </summary>
    static void AssertNoThrow(string name, Action act, List<string> failures, ref int passed)
    {
        try
        {
            act();
            passed++;
            Log.Information("  ✓ {Name}", name);
        }
        catch (Exception ex)
        {
            failures.Add($"  ✗ {name}: threw {ex.GetType().Name}: {ex.Message}");
        }
    }
}
