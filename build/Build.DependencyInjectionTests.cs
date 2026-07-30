using Nuke.Common;
using Nuke.Common.IO;
using Serilog;
using SwiftBindings.Build.Helpers;
using SwiftBindings.Build.Models;

partial class Build
{
    /// <summary>
    /// In-process behavioral tests for the ProjectReference injection pass —
    /// <see cref="ProjectRefInjectionPlanner"/> and the
    /// <c>InjectProjectRefsForLibrary</c> tail that commits its plan.
    ///
    /// <para>
    /// The behavior under test is a cross-product one: what happens to product
    /// B when product A cannot be scanned. It used to be "nothing happens to
    /// anyone" — the freshness pass ran over every product before any write, so
    /// one never-built product cancelled the whole library's injection while
    /// the SwiftFrameworkDependency injector (which writes per-product) carried
    /// on. That is how a csproj ends up with an SFD block and no PR block.
    /// These cases pin the per-product semantics, the fail-closed report, and
    /// the guarantee that a skipped product's csproj is left byte-identical.
    /// </para>
    ///
    /// <para>
    /// Everything is written under a temp directory (plus, for the end-to-end
    /// case, a sentinel <c>.selftest-projectrefs</c> library that is deleted
    /// afterwards) so the repo is never mutated.
    /// </para>
    /// </summary>
    Target TestProjectRefInjection => _ => _
        .Description("Run in-process tests for the per-product ProjectReference injection plan")
        .Executes(() =>
        {
            var failures = new List<string>();

            void Assert(string name, bool condition, string detail)
            {
                if (!condition)
                    failures.Add($"  ✗ {name}: {detail}");
                else
                    Log.Information("  ✓ {Name}", name);
            }

            // ── Fixture helpers ─────────────────────────────────────────────

            static string Csproj(string packageId, string? body = null) => $"""
                <Project Sdk="SwiftBindings.Sdk/0.18.0">

                  <PropertyGroup>
                    <TargetFramework>net10.0-ios</TargetFramework>
                    <PackageId>{packageId}</PackageId>
                  </PropertyGroup>
                {body ?? ""}
                </Project>
                """;

            // Lay out one product directory: csproj + (optionally) a fresh
            // swift-binding/ output holding generated C# that mentions the
            // given sibling modules.
            static AbsolutePath WriteProduct(
                AbsolutePath libDir,
                string framework,
                string packageId,
                string? csprojBody,
                bool fresh,
                params string[] referencedModules)
            {
                var dir = libDir / framework;
                Directory.CreateDirectory(dir);
                var csproj = dir / $"SwiftBindings.{packageId}.csproj";
                File.WriteAllText(csproj, Csproj(packageId, csprojBody));

                if (!fresh)
                    return csproj; // no stamp ⇒ Freshness.CheckFresh throws

                var sbDir = dir / "obj" / "Debug" / "net10.0-ios" / "swift-binding";
                Directory.CreateDirectory(sbDir);
                var body = string.Join("\n", referencedModules.Select(m =>
                    $"public static class Use{m} {{ public static object? F() => typeof({m}.Anchor); }}"));
                File.WriteAllText(sbDir / "Generated.cs", $"// generated\n{body}\n");
                var stamp = sbDir / "swift-binding.stamp";
                File.WriteAllText(stamp, "");
                // The stamp must be newer than the csproj; writing them in the
                // same second would otherwise depend on filesystem granularity.
                File.SetLastWriteTimeUtc(stamp, DateTime.UtcNow.AddMinutes(5));
                return csproj;
            }

            static ProjectRefTarget TargetFor(AbsolutePath libDir, string framework, AbsolutePath csproj) =>
                new(framework,
                    csproj,
                    libDir / framework / $"{framework}.xcframework" / "Info.plist",
                    libDir / framework / "obj" / "Debug" / "net10.0-ios" / "swift-binding");

            var scratch = (AbsolutePath)Path.Combine(
                Path.GetTempPath(), $"swiftbindings-projectref-selftest-{Guid.NewGuid():N}");

            try
            {
                // ── Case 1: one un-scannable product does not cancel the rest ──
                // The stale product is listed FIRST on purpose: under the old
                // all-or-nothing freshness pass the throw happened before any
                // other product had even been scanned.
                {
                    var libDir = scratch / "case1";
                    var stale = WriteProduct(libDir, "Alpha", "Test.Alpha", null, fresh: false);
                    var fresh = WriteProduct(libDir, "Beta", "Test.Beta", null, fresh: true, "Alpha");

                    var siblings = new[]
                    {
                        new DependencyHit("Alpha", "Alpha", stale, "Alpha"),
                        new DependencyHit("Beta", "Beta", fresh, "Beta"),
                    };
                    var targets = new[]
                    {
                        TargetFor(libDir, "Alpha", stale),
                        TargetFor(libDir, "Beta", fresh),
                    };

                    var staleBefore = File.ReadAllText(stale);
                    var plan = ProjectRefInjectionPlanner.Plan(targets, siblings);

                    Assert(
                        "Every product appears in the plan",
                        plan.Count == 2,
                        $"expected 2 entries, got {plan.Count}");
                    Assert(
                        "Un-scannable product is reported as skipped",
                        plan[0].Skipped && plan[0].SkipReason!.Contains("Missing freshness marker"),
                        $"expected a freshness skip for Alpha, got: {plan[0].SkipReason ?? "(not skipped)"}");
                    Assert(
                        "A skipped product still produces no write",
                        plan[0].WriteOp is null,
                        "a product that could not be scanned was queued for rewrite anyway");
                    Assert(
                        "The fresh product behind it is still scanned",
                        !plan[1].Skipped && plan[1].DetectedModules.SequenceEqual(new[] { "Alpha" }),
                        "the fresh product's scan was cancelled by its stale sibling — "
                            + $"detected [{string.Join(", ", plan[1].DetectedModules)}], skip={plan[1].SkipReason ?? "none"}");
                    Assert(
                        "The fresh product's rewrite is prepared",
                        plan[1].WriteOp is not null,
                        "no rewrite prepared for the fresh product");

                    plan[1].WriteOp!.Commit();
                    Assert(
                        "Committed rewrite injects the detected reference",
                        File.ReadAllText(fresh).Contains("../Alpha/SwiftBindings.Test.Alpha.csproj"),
                        "the ProjectReference was not written");
                    Assert(
                        "Skipped product's csproj is left byte-identical",
                        File.ReadAllText(stale) == staleBefore,
                        "a skipped product's csproj was modified");
                }

                // ── Case 2: a skipped product keeps the block it already had ──
                // Combines with the rewriter contract: skipping must never be a
                // back door to deletion.
                {
                    var libDir = scratch / "case2";
                    var existingBlock = $"""

                        {CsprojRewriter.PrBeginMarker}
                          <ItemGroup>
                            <ProjectReference Include="../Beta/SwiftBindings.Test.Beta.csproj" />
                          </ItemGroup>
                          {CsprojRewriter.PrEndMarker}

                        """;
                    var stale = WriteProduct(libDir, "Alpha", "Test.Alpha", existingBlock, fresh: false);
                    var fresh = WriteProduct(libDir, "Beta", "Test.Beta", null, fresh: true);

                    var siblings = new[]
                    {
                        new DependencyHit("Alpha", "Alpha", stale, "Alpha"),
                        new DependencyHit("Beta", "Beta", fresh, "Beta"),
                    };
                    var plan = ProjectRefInjectionPlanner.Plan(
                        new[] { TargetFor(libDir, "Alpha", stale), TargetFor(libDir, "Beta", fresh) },
                        siblings);

                    foreach (var op in plan.Where(e => e.WriteOp is not null))
                        op.WriteOp!.Commit();

                    Assert(
                        "Skipped product retains its existing ProjectReference block",
                        File.ReadAllText(stale).Contains("../Beta/SwiftBindings.Test.Beta.csproj"),
                        "the skipped product's existing reference was dropped");
                    Assert(
                        "Fresh product with no cross-module use gets no block",
                        !File.ReadAllText(fresh).Contains(CsprojRewriter.PrBeginMarker),
                        "an empty ProjectReference block was injected");
                }

                // ── Case 3: all products fresh ⇒ nothing is skipped ──
                {
                    var libDir = scratch / "case3";
                    var a = WriteProduct(libDir, "Alpha", "Test.Alpha", null, fresh: true);
                    var b = WriteProduct(libDir, "Beta", "Test.Beta", null, fresh: true, "Alpha");
                    var siblings = new[]
                    {
                        new DependencyHit("Alpha", "Alpha", a, "Alpha"),
                        new DependencyHit("Beta", "Beta", b, "Beta"),
                    };
                    var plan = ProjectRefInjectionPlanner.Plan(
                        new[] { TargetFor(libDir, "Alpha", a), TargetFor(libDir, "Beta", b) },
                        siblings);
                    Assert(
                        "No skips when every product is fresh",
                        plan.All(e => !e.Skipped),
                        $"unexpected skips: {string.Join("; ", plan.Where(e => e.Skipped).Select(e => e.Target.Framework))}");
                    Assert(
                        "Self-reference is never detected",
                        plan[0].DetectedModules.Count == 0,
                        $"Alpha detected [{string.Join(", ", plan[0].DetectedModules)}]");
                }

                // ── Case 4: end-to-end — InjectProjectRefsForLibrary writes the ──
                //   fresh product, leaves the stale one alone, and fails closed.
                {
                    var libName = ".selftest-projectrefs";
                    var libDir = LibrariesDir / libName;
                    if (Directory.Exists(libDir))
                        Directory.Delete(libDir, recursive: true);
                    Directory.CreateDirectory(libDir);
                    try
                    {
                        File.WriteAllText(libDir / "library.json", """
                            {
                              "repository": "https://example.invalid/selftest.git",
                              "version": "0.0.0",
                              "mode": "manual",
                              "products": [
                                { "framework": "Alpha", "subdirectory": "Alpha" },
                                { "framework": "Beta", "subdirectory": "Beta" }
                              ]
                            }
                            """);
                        var stale = WriteProduct(libDir, "Alpha", "Test.Alpha", null, fresh: false);
                        var fresh = WriteProduct(libDir, "Beta", "Test.Beta", null, fresh: true, "Alpha");
                        var staleBefore = File.ReadAllText(stale);

                        Exception? thrown = null;
                        try
                        {
                            InjectProjectRefsForLibrary(libName, Array.Empty<string>(), allProducts: true);
                        }
                        catch (Exception ex)
                        {
                            thrown = ex;
                        }

                        Assert(
                            "Injection fails closed when a product could not be processed",
                            thrown is InvalidOperationException && thrown.Message.Contains("Alpha"),
                            $"expected a skip report naming Alpha, got: {thrown?.Message ?? "(no exception)"}");
                        Assert(
                            "…but the fresh product was still written first",
                            File.ReadAllText(fresh).Contains("../Alpha/SwiftBindings.Test.Alpha.csproj"),
                            "the fresh product's ProjectReference block was not written — "
                                + "one un-built product cancelled the whole library again");
                        Assert(
                            "…and the skipped product's csproj is untouched",
                            File.ReadAllText(stale) == staleBefore,
                            "the skipped product's csproj was modified");
                    }
                    finally
                    {
                        if (Directory.Exists(libDir))
                            Directory.Delete(libDir, recursive: true);
                    }
                }
            }
            finally
            {
                if (Directory.Exists(scratch))
                    Directory.Delete(scratch, recursive: true);
            }

            if (failures.Count > 0)
            {
                Log.Error("ProjectReference injection test failures:");
                foreach (var f in failures)
                    Log.Error("{Failure}", f);
                throw new Exception($"{failures.Count} ProjectReference injection test case(s) failed");
            }

            Log.Information("All ProjectReference injection test cases passed.");
        });

    /// <summary>
    /// Aggregate entry point for the harness's in-process self-tests. Cheap
    /// (no simulator, no device, no packages) and safe to run on every change
    /// to <c>build/</c>.
    /// </summary>
    Target TestHarness => _ => _
        .Description(
            "Run every in-process harness self-test " +
            "(rewriter + dependency injection + regression artifacts + release-wave planning)")
        .DependsOn(TestCsprojRewriter, TestProjectRefRewriter, TestProjectRefInjection, TestRegressionArtifacts,
            TestReleaseWave)
        .Executes(() => Log.Information("Harness self-tests complete."));
}
