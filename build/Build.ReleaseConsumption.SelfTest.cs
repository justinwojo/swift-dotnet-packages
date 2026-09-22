using System.Buffers.Binary;
using Nuke.Common;
using Nuke.Common.IO;
using Serilog;
using SwiftBindings.Build.Helpers;
using SwiftBindings.Build.Models;

partial class Build
{
    /// <summary>
    /// In-process behavioral tests for the release-consumption lane and the
    /// lane-evidence gate.
    ///
    /// <para>Everything the lane decides before it touches a simulator is pure:
    /// which packages a test csproj consumes, what the generated lane props
    /// says, whether a restore graph actually shows package consumption, and
    /// whether a run's own output backs the lane's claim. Those decisions are
    /// what make the lane worth having — a lane that silently degrades to a
    /// ProjectReference build still goes green — so they are tested here rather
    /// than being observed once by eye on the day they were written.</para>
    ///
    /// <para>Assertions are on behavior (which id/version a reference resolves
    /// to, which inputs throw, which failure text names the cause) rather than
    /// on exact generated XML, so formatting changes don't red the suite.</para>
    /// </summary>
    Target TestReleaseConsumption => _ => _
        .Description("Run in-process tests for the release-consumption lane and the lane-evidence gate")
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
                    var missing = expectedFragments
                        .Where(f => !ex.Message.Contains(f, StringComparison.OrdinalIgnoreCase)).ToList();
                    if (missing.Count > 0)
                        failures.Add($"  ✗ {name}: message did not mention {string.Join(", ", missing)} — got: {ex.Message}");
                    else { passed++; Log.Information("  ✓ {Name}", name); }
                }
            }

            // ── ParseConsumeVersionMap ──────────────────────────────────────
            Assert("version map: blank yields no overrides",
                ParseConsumeVersionMap(null).Count == 0 && ParseConsumeVersionMap("  ").Count == 0,
                "blank input produced overrides");

            var map = ParseConsumeVersionMap("SwiftBindings.A=1.2.3; SwiftBindings.B=4.5.6");
            Assert("version map: parses semicolon-separated entries",
                map.Count == 2 && map["SwiftBindings.A"] == "1.2.3" && map["SwiftBindings.B"] == "4.5.6",
                $"got {map.Count} entries");

            Assert("version map: ids are case-insensitive",
                ParseConsumeVersionMap("swiftbindings.a=1.0").TryGetValue("SwiftBindings.A", out var ci) && ci == "1.0",
                "lookup was case-sensitive");

            AssertThrows("version map: a malformed entry throws instead of silently defaulting",
                () => ParseConsumeVersionMap("SwiftBindings.A"), "SwiftBindings.A", "Id=Version");

            AssertThrows("version map: a duplicated id throws",
                () => ParseConsumeVersionMap("A=1.0;A=2.0"), "listed twice");

            // ── ResolveConsumedPackages ─────────────────────────────────────
            const string oneRef = """
                <Project Sdk="Microsoft.NET.Sdk">
                  <ItemGroup>
                    <ProjectReference Include="../SwiftBindings.Apple.TipKit.csproj" />
                  </ItemGroup>
                </Project>
                """;

            var swaps = ResolveConsumedPackages(oneRef, "26.2.14", ParseConsumeVersionMap(null));
            Assert("consumed packages: package id is the csproj file name",
                swaps.Count == 1 && swaps[0].PackageId == "SwiftBindings.Apple.TipKit",
                $"got [{string.Join(", ", swaps.Select(s => s.PackageId))}]");
            Assert("consumed packages: version defaults to the release under test",
                swaps.Count == 1 && swaps[0].Version == "26.2.14",
                "default version was not applied");
            Assert("consumed packages: the Include is carried verbatim for the Remove item spec",
                swaps.Count == 1 && swaps[0].Include == "../SwiftBindings.Apple.TipKit.csproj",
                $"got '{(swaps.Count == 1 ? swaps[0].Include : "(none)")}'");

            var overridden = ResolveConsumedPackages(
                oneRef, "26.2.14", ParseConsumeVersionMap("SwiftBindings.Apple.TipKit=99.0.0"));
            Assert("consumed packages: an explicit override wins over the default version",
                overridden[0].Version == "99.0.0", $"got {overridden[0].Version}");

            const string twoRefs = """
                <Project>
                  <ItemGroup>
                    <ProjectReference Include="..\SwiftBindings.BlinkIDUX.csproj" />
                    <ProjectReference Include="../../BlinkID/SwiftBindings.BlinkID.csproj" />
                    <ProjectReference Include="../SwiftBindings.BlinkIDUX.csproj" />
                  </ItemGroup>
                </Project>
                """;
            var cross = ResolveConsumedPackages(twoRefs, "1.0.0", ParseConsumeVersionMap("SwiftBindings.BlinkID=7.4.0"));
            Assert("consumed packages: a cross-library reference takes its own version",
                cross.Count == 2
                && cross.Any(c => c.PackageId == "SwiftBindings.BlinkIDUX" && c.Version == "1.0.0")
                && cross.Any(c => c.PackageId == "SwiftBindings.BlinkID" && c.Version == "7.4.0"),
                $"got [{string.Join(", ", cross.Select(c => $"{c.PackageId} {c.Version}"))}]");
            Assert("consumed packages: a repeated reference is not swapped twice",
                cross.Count == 2, $"got {cross.Count} swaps");

            AssertThrows("consumed packages: a non-SwiftBindings project reference is a hard error",
                () => ResolveConsumedPackages(
                    """<ProjectReference Include="../Some.Helper.csproj" />""", "1.0.0", ParseConsumeVersionMap(null)),
                "Some.Helper.csproj", "no package to substitute");

            Assert("consumed packages: a csproj with no project references yields nothing to swap",
                ResolveConsumedPackages("<Project />", "1.0.0", ParseConsumeVersionMap(null)).Count == 0,
                "found swaps where there are no references");

            // ── PlanFeedResolution ──────────────────────────────────────────
            var twoSwaps = new List<(string Include, string PackageId, string Version)>
            {
                ("../SwiftBindings.BlinkIDUX.csproj", "SwiftBindings.BlinkIDUX", "1.4.0"),
                ("../../BlinkID/SwiftBindings.BlinkID.csproj", "SwiftBindings.BlinkID", "1.4.0"),
            };
            string? Pin(string include) => include.Contains("BlinkID/", StringComparison.Ordinal) ? "7.4.0" : null;

            var (plannedCross, stagedCross) = PlanFeedResolution(
                twoSwaps, new[] { "SwiftBindings.BlinkIDUX.1.4.0.nupkg" }, Pin, "feed");
            Assert("feed plan: the package under release is consumed from the staged feed",
                stagedCross.Contains("SwiftBindings.BlinkIDUX") && stagedCross.Count == 1,
                $"staged=[{string.Join(", ", stagedCross)}]");
            Assert("feed plan: a sibling this release does not stage falls back to its own pin",
                plannedCross.Any(p => p.PackageId == "SwiftBindings.BlinkID" && p.Version == "7.4.0"),
                $"got [{string.Join(", ", plannedCross.Select(p => $"{p.PackageId} {p.Version}"))}]");

            AssertThrows("feed plan: a staged package at a different version is a hard error, not a fallback",
                () => PlanFeedResolution(
                    new List<(string, string, string)> { ("../SwiftBindings.Apple.TipKit.csproj", "SwiftBindings.Apple.TipKit", "26.2.14") },
                    new[] { "SwiftBindings.Apple.TipKit.26.2.13.nupkg" }, _ => "26.2.13", "feed"),
                "26.2.14", "not being published");

            AssertThrows("feed plan: an unstaged package with no version pin is a hard error, not a guess",
                () => PlanFeedResolution(
                    new List<(string, string, string)> { ("../SwiftBindings.Other.csproj", "SwiftBindings.Other", "1.0.0") },
                    new[] { "SwiftBindings.Apple.TipKit.26.2.14.nupkg" }, _ => null, "feed"),
                "declares no version to fall back on");

            AssertThrows("feed plan: a run that stages nothing it consumes proves nothing about the release",
                () => PlanFeedResolution(
                    new List<(string, string, string)> { ("../SwiftBindings.Other.csproj", "SwiftBindings.Other", "1.0.0") },
                    Array.Empty<string>(), _ => "1.0.0", "feed"),
                "say nothing about the release");

            // ── RenderLanePropsXml ──────────────────────────────────────────
            var propsXml = RenderLanePropsXml(swaps, "/scratch/LaneEvidence.g.cs");
            Assert("lane props: removes the ProjectReference by its verbatim item spec",
                propsXml.Contains("""<ProjectReference Remove="../SwiftBindings.Apple.TipKit.csproj" />""", StringComparison.Ordinal),
                propsXml);
            Assert("lane props: adds the matching PackageReference",
                propsXml.Contains("""<PackageReference Include="SwiftBindings.Apple.TipKit" Version="26.2.14" />""", StringComparison.Ordinal),
                propsXml);
            Assert("lane props: injects the evidence probe as a Compile item",
                propsXml.Contains("""<Compile Include="/scratch/LaneEvidence.g.cs" />""", StringComparison.Ordinal),
                propsXml);

            var nativeProps = RenderLanePropsXml(
                swaps, "/scratch/LaneEvidence.g.cs",
                workingTreeRoot: "/repo", laneScratchRoot: "/repo/artifacts/release-consumption/TipKit");
            Assert("lane props: package mode drops the NativeReference items that resolve into the checkout",
                nativeProps.Contains("_SwiftBindingsLaneDropWorkingTreeNativeReferences", StringComparison.Ordinal)
                && nativeProps.Contains("""<NativeReference Remove="@(_SwiftBindingsLaneWorkingTreeNative)" />""", StringComparison.Ordinal),
                nativeProps);
            Assert("lane props: the drop exempts the lane's own scratch tree (where resources.zip is unpacked)",
                nativeProps.Contains("_SwiftBindingsLaneScratchRoot>/repo/artifacts/release-consumption/TipKit/<", StringComparison.Ordinal)
                && nativeProps.Contains("!$([System.String]::Copy('%(FullPath)').StartsWith('$(_SwiftBindingsLaneScratchRoot)'))", StringComparison.Ordinal),
                nativeProps);
            Assert("lane props: the roots carry a trailing separator so a sibling directory cannot half-match",
                nativeProps.Contains("_SwiftBindingsLaneWorkingTreeRoot>/repo/<", StringComparison.Ordinal),
                nativeProps);
            Assert("lane props: without roots the drop target is not emitted at all",
                !RenderLanePropsXml(swaps, "/scratch/LaneEvidence.g.cs")
                    .Contains("_SwiftBindingsLaneDropWorkingTreeNativeReferences", StringComparison.Ordinal),
                "the native drop leaked into a caller that did not ask for it");

            var evidenceOnly = RenderLanePropsXml(Array.Empty<(string, string, string)>(), "/scratch/LaneEvidence.g.cs");
            Assert("lane props: the evidence-only shape rewrites no references",
                !evidenceOnly.Contains("ProjectReference", StringComparison.Ordinal)
                && !evidenceOnly.Contains("PackageReference", StringComparison.Ordinal)
                && evidenceOnly.Contains("Compile Include=", StringComparison.Ordinal),
                evidenceOnly);

            Assert("lane props: the probe source compiles a module initializer that prints the gate's prefix",
                LaneEvidenceSource().Contains("ModuleInitializer", StringComparison.Ordinal)
                && LaneEvidenceSource().Contains(LaneEvidenceGate.LinePrefix.TrimEnd(), StringComparison.Ordinal),
                "the generated probe would not produce lines the gate can read");

            // ── AssetsFailures ──────────────────────────────────────────────
            var laneCache = (AbsolutePath)Path.Combine(Path.GetTempPath(), "swiftbindings-consume-selftest", "packages");
            var underTest = new List<(string Include, string PackageId, string Version)>
            {
                ("../SwiftBindings.Apple.TipKit.csproj", "SwiftBindings.Apple.TipKit", "26.2.14"),
            };

            string Assets(string libraries, string? packageFolders) =>
                $$"""
                {
                  "version": 3,
                  "libraries": { {{libraries}} }
                  {{(packageFolders is null ? "" : $", \"packageFolders\": {{ {packageFolders} }}")}}
                }
                """;

            var goodAssets = Assets(
                """ "SwiftBindings.Apple.TipKit/26.2.14": { "type": "package" } """,
                $"\"{laneCache}\": {{}}");
            Assert("restore graph: a package-mode restore from the lane cache passes",
                AssetsFailures(goodAssets, underTest, laneCache).Count == 0,
                string.Join("; ", AssetsFailures(goodAssets, underTest, laneCache)));

            var projectAssets = Assets(
                """ "SwiftBindings.Apple.TipKit/26.2.14": { "type": "project" } """,
                $"\"{laneCache}\": {{}}");
            Assert("restore graph: a ProjectReference build is rejected (the lane's core false-green)",
                AssetsFailures(projectAssets, underTest, laneCache)
                    .Any(f => f.Contains("project", StringComparison.OrdinalIgnoreCase)),
                "a type:project entry passed the gate");

            var wrongVersionAssets = Assets(
                """ "SwiftBindings.Apple.TipKit/26.2.13": { "type": "package" } """,
                $"\"{laneCache}\": {{}}");
            Assert("restore graph: the previously published version does not satisfy the staged one",
                AssetsFailures(wrongVersionAssets, underTest, laneCache).Count > 0,
                "an older package version passed the gate");

            var emptyAssets = Assets("", $"\"{laneCache}\": {{}}");
            Assert("restore graph: an absent package is reported as absent",
                AssetsFailures(emptyAssets, underTest, laneCache)
                    .Any(f => f.Contains("absent", StringComparison.OrdinalIgnoreCase)),
                "a missing package did not fail the gate");

            var sharedCacheAssets = Assets(
                """ "SwiftBindings.Apple.TipKit/26.2.14": { "type": "package" } """,
                "\"/Users/someone/.nuget/packages\": {}");
            Assert("restore graph: a machine-wide package folder fails cache isolation",
                AssetsFailures(sharedCacheAssets, underTest, laneCache)
                    .Any(f => f.Contains("isolated cache", StringComparison.OrdinalIgnoreCase)),
                "the shared NuGet cache passed as isolated");

            var noFoldersAssets = Assets(
                """ "SwiftBindings.Apple.TipKit/26.2.14": { "type": "package" } """, null);
            Assert("restore graph: no packageFolders section fails rather than passing unverified",
                AssetsFailures(noFoldersAssets, underTest, laneCache).Count > 0,
                "cache isolation was assumed rather than checked");

            // ── LaneEvidenceGate: runtime ───────────────────────────────────
            const string monoBanner =
                "[lane-evidence] runtime: MonoRuntime=True DynamicCodeSupported=False Framework=\".NET 10.0.7\"";
            const string aotBanner =
                "[lane-evidence] runtime: MonoRuntime=False DynamicCodeSupported=False Framework=\".NET 10.0.7\"";

            Assert("runtime gate: Mono evidence satisfies a Mono lane",
                LaneEvidenceGate.JudgeRuntime(monoBanner, expectMono: true) == LaneEvidenceGate.Verdict.Match,
                "expected Match");
            Assert("runtime gate: NativeAOT evidence contradicts a Mono lane",
                LaneEvidenceGate.JudgeRuntime(aotBanner, expectMono: true) == LaneEvidenceGate.Verdict.Mismatch,
                "a NativeAOT run passed as Mono full-AOT");
            Assert("runtime gate: Mono evidence contradicts a NativeAOT lane",
                LaneEvidenceGate.JudgeRuntime(monoBanner, expectMono: false) == LaneEvidenceGate.Verdict.Mismatch,
                "a Mono run passed as NativeAOT");
            Assert("runtime gate: output with no banner yields no verdict",
                LaneEvidenceGate.JudgeRuntime("TEST SUCCESS\n", expectMono: true) == LaneEvidenceGate.Verdict.NoEvidence,
                "a verdict was invented from nothing");

            AssertThrows("runtime gate: a silent run fails the lane (the probe did not compile in)",
                () => AssertRuntimeFlavor("TEST SUCCESS\n", expectMono: true, "selftest/lane"),
                "no lane-evidence runtime line");
            AssertThrows("runtime gate: a masquerading run fails the lane",
                () => AssertRuntimeFlavor(aotBanner, expectMono: true, "selftest/lane"),
                "contradicts the lane");
            AssertRuntimeFlavor(monoBanner, expectMono: true, "selftest/lane");
            passed++;
            Log.Information("  ✓ runtime gate: an agreeing run passes");

            // ── LaneEvidenceGate: assembly identity ─────────────────────────
            const string loaded =
                "[lane-evidence] assembly: SwiftBindings.Apple.TipKit version=26.2.14.0 " +
                "informational=26.2.14+5dd91034 location=/x/SwiftBindings.Apple.TipKit.dll";

            Assert("identity gate: the staged version loaded in-process passes",
                LaneEvidenceGate.JudgeAssemblyIdentity(loaded, "SwiftBindings.Apple.TipKit", "26.2.14") is null,
                LaneEvidenceGate.JudgeAssemblyIdentity(loaded, "SwiftBindings.Apple.TipKit", "26.2.14") ?? "");
            Assert("identity gate: a different version in-process fails",
                LaneEvidenceGate.JudgeAssemblyIdentity(loaded, "SwiftBindings.Apple.TipKit", "26.2.13") is not null,
                "the wrong assembly version passed the gate");
            Assert("identity gate: never loading the assembly is a failure, not a pass",
                LaneEvidenceGate.JudgeAssemblyIdentity("TEST SUCCESS\n", "SwiftBindings.Apple.TipKit", "26.2.14")
                    is { } miss && miss.Contains("never reported loading", StringComparison.OrdinalIgnoreCase),
                "absence of evidence passed as evidence of presence");

            Assert("identity gate: four-part file version matches a three-part package version",
                LaneEvidenceGate.VersionsAgree("26.2.14.0", "26.2.14"),
                "padding defeated the comparison");
            Assert("identity gate: SemVer build metadata is tolerated",
                LaneEvidenceGate.VersionsAgree("26.2.14+abcdef0", "26.2.14"),
                "+sha defeated the comparison");
            Assert("identity gate: a prerelease suffix must match",
                LaneEvidenceGate.VersionsAgree("1.0.0-preview.1", "1.0.0-preview.1")
                && !LaneEvidenceGate.VersionsAgree("1.0.0", "1.0.0-preview.1"),
                "a stable build passed as its prerelease");
            Assert("identity gate: a meaningful revision is not trimmed away",
                !LaneEvidenceGate.VersionsAgree("18.1.1.1", "18.1.1"),
                "a real revision component was discarded");

            // ── Device bundle shape ─────────────────────────────────────────
            var shapeRoot = Path.Combine(Path.GetTempPath(), "swiftbindings-consume-selftest", "shape");
            if (Directory.Exists(shapeRoot)) Directory.Delete(shapeRoot, recursive: true);
            var monoApp = (AbsolutePath)Path.Combine(shapeRoot, "Mono.app");
            var aotApp = (AbsolutePath)Path.Combine(shapeRoot, "Aot.app");
            Directory.CreateDirectory(monoApp);
            Directory.CreateDirectory(aotApp);
            File.WriteAllText(monoApp / "System.Private.CoreLib.dll", "");
            File.WriteAllText(aotApp / "TestApp", "");

            AssertDeviceAppFlavor(monoApp, expectMono: true, "selftest/mono");
            AssertDeviceAppFlavor(aotApp, expectMono: false, "selftest/aot");
            passed += 2;
            Log.Information("  ✓ bundle shape: each lane accepts its own bundle");

            AssertThrows("bundle shape: a NativeAOT bundle cannot be installed by the Mono lane",
                () => AssertDeviceAppFlavor(aotApp, expectMono: true, "selftest/mono"), "WRONG SHAPE");
            AssertThrows("bundle shape: a Mono bundle cannot be installed by the NativeAOT lane",
                () => AssertDeviceAppFlavor(monoApp, expectMono: false, "selftest/aot"), "WRONG SHAPE");
            AssertThrows("bundle shape: a missing bundle fails rather than being treated as either lane",
                () => AssertDeviceAppFlavor((AbsolutePath)Path.Combine(shapeRoot, "Absent.app"), true, "selftest/mono"),
                "no app bundle");

            Directory.Delete(shapeRoot, recursive: true);

            // ── Matrix coverage record: declared minimum OS vs. this host ───
            const string perTfmMinOs = """
                <PropertyGroup>
                  <SupportedOSPlatformVersion Condition="$(TargetFramework.Contains('-ios'))">15.0</SupportedOSPlatformVersion>
                  <SupportedOSPlatformVersion Condition="$(TargetFramework.Contains('-macos'))">12.0</SupportedOSPlatformVersion>
                </PropertyGroup>
                """;
            Assert("min-OS: the per-TFM condition selects the platform's own minimum",
                ReadMinimumOs(perTfmMinOs, "ios") == "15.0" && ReadMinimumOs(perTfmMinOs, "macos") == "12.0",
                $"ios={ReadMinimumOs(perTfmMinOs, "ios")}, macos={ReadMinimumOs(perTfmMinOs, "macos")}");
            Assert("min-OS: an unconditional declaration applies to every platform",
                ReadMinimumOs("<SupportedOSPlatformVersion>26.0</SupportedOSPlatformVersion>", "tvos") == "26.0",
                "unconditional minimum was not picked up");
            Assert("min-OS: a project declaring none reports none",
                ReadMinimumOs("<Project />", "ios") is null,
                "invented a minimum");

            var hostRuntimes = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
            {
                ["ios-sim"] = new[] { "26.2", "26.0" },
                ["tvos"] = Array.Empty<string>(),
            };

            var gaps = MinOsGaps(
                new[]
                {
                    ("TipKit", "ios-sim", (string?)"15.0"),
                    ("FamilyControls", "ios-sim", (string?)"26.0"),
                    ("Nuke", "tvos", (string?)"15.0"),
                    ("NoDeclaration", "ios-sim", (string?)null),
                    ("Elsewhere", "ios-device", (string?)"15.0"),
                },
                hostRuntimes);

            Assert("min-OS: a minimum no installed runtime can serve is recorded as a gap",
                gaps.Any(g => g.Library == "TipKit" && g.MinimumOs == "15.0"),
                $"gaps=[{string.Join(", ", gaps.Select(g => $"{g.Library} {g.Platform} {g.MinimumOs}"))}]");
            Assert("min-OS: a minimum the host can actually run is not a gap",
                !gaps.Any(g => g.Library == "FamilyControls"),
                "a runnable minimum was reported as a gap");
            Assert("min-OS: a platform with no installed runtime at all is named as such",
                gaps.Any(g => g.Library == "Nuke" && g.Available.Contains("no runtime installed", StringComparison.Ordinal)),
                "an absent runtime was not distinguished");
            Assert("min-OS: a project that declares no minimum contributes no gap",
                !gaps.Any(g => g.Library == "NoDeclaration"), "invented a gap");
            Assert("min-OS: an un-enumerated platform is not reported as missing",
                !gaps.Any(g => g.Library == "Elsewhere"),
                "'not enumerated' was reported as 'not available'");

            // ── Booted-device family filter ─────────────────────────────────
            // simctl's `booted` alias is family-blind: with an Apple TV booted
            // alongside the iPhones it can resolve to the Apple TV, and an iOS
            // install onto it dies with "This app was not built to support this
            // device family". These cases pin the selector that replaced it.
            const string mixedFleet = """
                {
                  "devices": {
                    "com.apple.CoreSimulator.SimRuntime.tvOS-26-0": [
                      { "name": "Apple TV 4K (3rd generation)", "udid": "TV-BOOTED", "state": "Booted", "isAvailable": true }
                    ],
                    "com.apple.CoreSimulator.SimRuntime.iOS-26-0": [
                      { "name": "iPad Pro 13-inch (M4)", "udid": "IPAD-BOOTED", "state": "Booted", "isAvailable": true },
                      { "name": "iPhone 17 Pro", "udid": "IPHONE-BOOTED", "state": "Booted", "isAvailable": true },
                      { "name": "iPhone 16", "udid": "IPHONE-DOWN", "state": "Shutdown", "isAvailable": true }
                    ]
                  }
                }
                """;

            Assert("family filter: an iOS install never lands on a booted Apple TV",
                SimulatorFleet.SelectBootedUdid(mixedFleet, SimulatorFamily.Ios) != "TV-BOOTED",
                "picked the tvOS device for an iOS app");
            Assert("family filter: a booted iPhone wins over a booted iPad",
                SimulatorFleet.SelectBootedUdid(mixedFleet, SimulatorFamily.Ios) == "IPHONE-BOOTED",
                $"got {SimulatorFleet.SelectBootedUdid(mixedFleet, SimulatorFamily.Ios) ?? "<null>"}");
            Assert("family filter: a tvOS install resolves to the Apple TV",
                SimulatorFleet.SelectBootedUdid(mixedFleet, SimulatorFamily.TvOs) == "TV-BOOTED",
                $"got {SimulatorFleet.SelectBootedUdid(mixedFleet, SimulatorFamily.TvOs) ?? "<null>"}");

            const string tvOnlyFleet = """
                {
                  "devices": {
                    "com.apple.CoreSimulator.SimRuntime.tvOS-26-0": [
                      { "name": "Apple TV 4K (3rd generation)", "udid": "TV-BOOTED", "state": "Booted", "isAvailable": true }
                    ],
                    "com.apple.CoreSimulator.SimRuntime.iOS-26-0": [
                      { "name": "iPhone 17 Pro", "udid": "IPHONE-DOWN", "state": "Shutdown", "isAvailable": true }
                    ]
                  }
                }
                """;

            Assert("family filter: nothing booted for the family borrows no other family",
                SimulatorFleet.SelectBootedUdid(tvOnlyFleet, SimulatorFamily.Ios) is null,
                $"borrowed {SimulatorFleet.SelectBootedUdid(tvOnlyFleet, SimulatorFamily.Ios)}");
            Assert("family filter: a Shutdown device is never selected",
                SimulatorFleet.SelectBootedUdid(tvOnlyFleet, SimulatorFamily.Ios) != "IPHONE-DOWN",
                "selected a device that is not booted");

            const string iPadOnlyFleet = """
                {
                  "devices": {
                    "com.apple.CoreSimulator.SimRuntime.iOS-26-0": [
                      { "name": "iPad Pro 13-inch (M4)", "udid": "IPAD-BOOTED", "state": "Booted", "isAvailable": true }
                    ]
                  }
                }
                """;

            Assert("family filter: an iPad is still an iOS device when no iPhone is booted",
                SimulatorFleet.SelectBootedUdid(iPadOnlyFleet, SimulatorFamily.Ios) == "IPAD-BOOTED",
                $"got {SimulatorFleet.SelectBootedUdid(iPadOnlyFleet, SimulatorFamily.Ios) ?? "<null>"}");
            Assert("family filter: an empty fleet resolves to nothing rather than a guess",
                SimulatorFleet.SelectBootedUdid("""{"devices":{}}""", SimulatorFamily.Ios) is null,
                "invented a device");

            // ── Mach-O identity ─────────────────────────────────────────────
            // The native analogue of the MVID check. Pinned on synthetic bytes
            // rather than on a checked-in binary so the parse is tested for what
            // it claims — fat and thin headers, a truncated file, a file that is
            // not Mach-O at all — without depending on which Xcode is installed.
            static byte[] ThinMachO(string uuidHex)
            {
                var bytes = new byte[32 + 24];
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0), 0xFEEDFACFu);   // MH_MAGIC_64
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), 1u);           // ncmds
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(20), 24u);          // sizeofcmds
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(32), 0x1Bu);        // LC_UUID
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(36), 24u);          // cmdsize
                Convert.FromHexString(uuidHex).CopyTo(bytes.AsSpan(40));
                return bytes;
            }

            static byte[] FatMachO(params byte[][] slices)
            {
                var headerSize = 8 + (20 * slices.Length);
                var offsets = new int[slices.Length];
                var cursor = headerSize;
                for (var i = 0; i < slices.Length; i++) { offsets[i] = cursor; cursor += slices[i].Length; }

                var bytes = new byte[cursor];
                BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(0), 0xCAFEBABEu);      // FAT_MAGIC
                BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(4), (uint)slices.Length);
                for (var i = 0; i < slices.Length; i++)
                {
                    var entry = 8 + (20 * i);
                    BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(entry + 8), (uint)offsets[i]);
                    BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(entry + 12), (uint)slices[i].Length);
                    slices[i].CopyTo(bytes.AsSpan(offsets[i]));
                }
                return bytes;
            }

            const string armUuidHex = "16437E19796D31AD9CB74A22A72968D8";
            const string x64UuidHex = "B87FCF87313E3F77995949368B5B1F31";
            const string armUuid = "16437E19-796D-31AD-9CB7-4A22A72968D8";
            const string x64Uuid = "B87FCF87-313E-3F77-9959-49368B5B1F31";

            var thinUuids = MachOImage.ReadUuids(new MemoryStream(ThinMachO(armUuidHex)));
            Assert("mach-o: a thin image yields its LC_UUID in dwarfdump's canonical form",
                thinUuids.Count == 1 && thinUuids[0] == armUuid,
                $"got [{string.Join(", ", thinUuids)}]");

            var fatUuids = MachOImage.ReadUuids(new MemoryStream(FatMachO(ThinMachO(x64UuidHex), ThinMachO(armUuidHex))));
            Assert("mach-o: a fat image yields every slice's LC_UUID",
                fatUuids.Count == 2 && fatUuids.Contains(armUuid) && fatUuids.Contains(x64Uuid),
                $"got [{string.Join(", ", fatUuids)}]");

            Assert("mach-o: a static archive (no LC_UUID) yields nothing rather than throwing",
                MachOImage.ReadUuids(new MemoryStream("!<arch>\ndebian-binary   1234"u8.ToArray())).Count == 0,
                "invented an identity for a non-Mach-O file");

            Assert("mach-o: a truncated image yields nothing rather than throwing",
                MachOImage.ReadUuids(new MemoryStream(ThinMachO(armUuidHex)[..36])).Count == 0,
                "read a UUID out of bytes that were not there");

            Assert("mach-o: an unreadable path yields nothing rather than throwing",
                MachOImage.ReadUuids(Path.Combine(Path.GetTempPath(), "swiftbindings-no-such-binary")).Count == 0,
                "a missing file produced an identity");

            // ── Native provenance ───────────────────────────────────────────
            Assert("native slices: only an iOS simulator slice is one the sim lane must embed",
                IsIosSimulatorSlice("ios-arm64_x86_64-simulator")
                && !IsIosSimulatorSlice("ios-arm64")
                && !IsIosSimulatorSlice("tvos-arm64_x86_64-simulator")
                && !IsIosSimulatorSlice("ios-arm64_x86_64-maccatalyst"),
                "slice classification does not separate the iOS simulator from its neighbours");

            Assert("resources.zip: a framework binary entry is recognised with its slice",
                ParseFrameworkBinaryEntry("MapLibre.xcframework/ios-arm64_x86_64-simulator/MapLibre.framework/MapLibre")
                    == ("ios-arm64_x86_64-simulator", "MapLibre"),
                "the framework binary inside a binding package's resources.zip was not recognised");
            Assert("resources.zip: a resource beside the binary is not mistaken for it",
                ParseFrameworkBinaryEntry("MapLibre.xcframework/ios-arm64/MapLibre.framework/Info.plist").Slice is null
                && ParseFrameworkBinaryEntry("manifest").Slice is null
                && ParseFrameworkBinaryEntry("X.xcframework/ios-arm64/X.framework/Headers/X.h").Slice is null,
                "a non-binary entry was indexed as a framework identity");

            var tipKitSim = new PackageNativeImage(
                "SwiftBindings.Apple.TipKit", "26.2.14", "TipKitSwiftBindings", "ios-arm64_x86_64-simulator",
                "runtimes/ios-arm64/native/TipKitSwiftBindings.xcframework/ios-arm64_x86_64-simulator",
                new[] { armUuid, x64Uuid });
            var tipKitDevice = new PackageNativeImage(
                "SwiftBindings.Apple.TipKit", "26.2.14", "TipKitSwiftBindings", "ios-arm64",
                "runtimes/ios-arm64/native/TipKitSwiftBindings.xcframework/ios-arm64",
                new[] { "32C051D0-9C19-349E-9024-65EACCBE2CED" });
            var runtimeSim = new PackageNativeImage(
                "SwiftBindings.Runtime", "0.19.4", "SwiftBindingsRuntime", "ios-arm64_x86_64-simulator",
                "native/SwiftBindingsRuntime.xcframework/ios-arm64_x86_64-simulator",
                new[] { "B1A598D6-D454-3B8F-922F-0AB085FB8D8E" });
            var packageImages = new[] { tipKitSim, tipKitDevice, runtimeSim };
            var staged = new[] { ("SwiftBindings.Apple.TipKit", "26.2.14") };
            var resolved = new[] { ("SwiftBindings.Apple.TipKit", "26.2.14"), ("SwiftBindings.Runtime", "0.19.4") };

            // The restore graph is the default universe for these cases; the
            // ones that vary it pass their own.
            IReadOnlyList<string> Provenance(
                IReadOnlyList<EmbeddedNativeImage> bundle,
                IReadOnlyList<PackageNativeImage> images,
                (string, string)[] stagedPkgs,
                (string, string)[]? resolvedPkgs = null) =>
                NativeProvenanceFailures(bundle, images, resolvedPkgs ?? resolved, stagedPkgs);

            var goodBundle = new[]
            {
                new EmbeddedNativeImage("TipKitSwiftBindings", "/app/Frameworks/TipKitSwiftBindings.framework/TipKitSwiftBindings", new[] { armUuid }),
                new EmbeddedNativeImage("SwiftBindingsRuntime", "/app/Frameworks/SwiftBindingsRuntime.framework/SwiftBindingsRuntime", new[] { "B1A598D6-D454-3B8F-922F-0AB085FB8D8E" }),
            };
            Assert("native provenance: a bundle whose frameworks are the packages' own builds passes",
                Provenance(goodBundle, packageImages, staged).Count == 0,
                string.Join("; ", Provenance(goodBundle, packageImages, staged)));

            // The lane's core native false-green: the source build left a
            // working-tree xcframework and the app embedded that instead.
            var workingTreeBundle = new[]
            {
                new EmbeddedNativeImage("TipKitSwiftBindings", "/app/Frameworks/TipKitSwiftBindings.framework/TipKitSwiftBindings", new[] { "DEADBEEF-0000-0000-0000-000000000001" }),
                goodBundle[1],
            };
            Assert("native provenance: a framework no package ships is rejected (the working-tree artifact)",
                Provenance(workingTreeBundle, packageImages, staged)
                    .Any(f => f.Contains("matches no framework", StringComparison.OrdinalIgnoreCase)),
                "a framework of unknown origin passed the gate");

            Assert("native provenance: a framework with no readable identity is rejected, not assumed",
                Provenance(
                        new[] { new EmbeddedNativeImage("Mystery", "/app/Frameworks/Mystery.framework", Array.Empty<string>()) },
                        packageImages, staged)
                    .Any(f => f.Contains("no Mach-O LC_UUID", StringComparison.OrdinalIgnoreCase)),
                "an unidentifiable binary passed as package-provided");

            Assert("native provenance: a staged package's simulator framework must actually be embedded",
                Provenance(new[] { goodBundle[1] }, packageImages, staged)
                    .Any(f => f.Contains("no framework of that build is embedded", StringComparison.OrdinalIgnoreCase)),
                "the package's own native code never reached the app and the gate said nothing");

            Assert("native provenance: the device slice of a staged package is not demanded of a simulator bundle",
                !Provenance(goodBundle, packageImages, staged)
                    .Any(f => f.Contains("ios-arm64/TipKitSwiftBindings", StringComparison.Ordinal)),
                "a device-only slice was required in a simulator app");

            Assert("native provenance: a staged package that ships no identifiable native asset fails rather than passing vacuously",
                Provenance(goodBundle, packageImages,
                        new[] { ("SwiftBindings.Apple.TipKit", "26.2.14"), ("SwiftBindings.Ghost", "1.0.0") })
                    .Any(f => f.Contains("vacuously", StringComparison.OrdinalIgnoreCase)),
                "a package with no native content silently satisfied the native gate");

            var matterResolved = new[] { ("SwiftBindings.Apple.Matter", "26.2.15"), ("SwiftBindings.Runtime", "0.19.4") };
            Assert("native provenance: an Apple framework package with no wrapper is a system binding",
                Provenance(new[] { goodBundle[1] }, packageImages,
                        new[] { ("SwiftBindings.Apple.Matter", "26.2.15") }, matterResolved).Count == 0,
                "a system-framework binding was required to ship a wrapper xcframework");

            // The lane's cache is kept between runs and only the version under
            // test is evicted, so a previous release of the same package is
            // still sitting in it. Its frameworks are not what this run ships.
            var tipKitPreviousSim = new PackageNativeImage(
                "SwiftBindings.Apple.TipKit", "26.2.13", "TipKitSwiftBindings", "ios-arm64_x86_64-simulator",
                "runtimes/ios-arm64/native/TipKitSwiftBindings.xcframework/ios-arm64_x86_64-simulator",
                new[] { "0FF0FF0F-1111-2222-3333-444444444444" });
            var cacheWithOldVersion = packageImages.Append(tipKitPreviousSim).ToArray();

            Assert("native provenance: a retained older version of a staged package is not demanded in the bundle",
                Provenance(goodBundle, cacheWithOldVersion, staged).Count == 0,
                string.Join("; ", Provenance(goodBundle, cacheWithOldVersion, staged)));

            Assert("native provenance: a retained older version cannot stand in for the version under release",
                Provenance(
                        new[]
                        {
                            new EmbeddedNativeImage("TipKitSwiftBindings",
                                "/app/Frameworks/TipKitSwiftBindings.framework/TipKitSwiftBindings",
                                new[] { "0FF0FF0F-1111-2222-3333-444444444444" }),
                            goodBundle[1],
                        },
                        cacheWithOldVersion, staged)
                    .Any(f => f.Contains("26.2.14", StringComparison.Ordinal)
                              && f.Contains("no framework of that build is embedded", StringComparison.OrdinalIgnoreCase)),
                "the app shipped the previous release's framework and the gate accepted it");

            Assert("native provenance: a static framework slice (no LC_UUID) is not demanded in the bundle",
                Provenance(
                    goodBundle,
                    packageImages.Append(new PackageNativeImage(
                        "SwiftBindings.Apple.TipKit", "26.2.14", "StaticHelper", "ios-arm64_x86_64-simulator",
                        "runtimes/ios-arm64/native/StaticHelper.xcframework/ios-arm64_x86_64-simulator",
                        Array.Empty<string>())).ToArray(),
                    staged).Count == 0,
                "a static archive was required to appear as an embedded framework");

            // A dependency the release does not stage still has to be the
            // version the restore chose. The cache holds the previous one, so
            // without the resolved-version filter an app that linked the old
            // runtime would attribute cleanly and pass.
            var runtimePreviousSim = new PackageNativeImage(
                "SwiftBindings.Runtime", "0.16.0", "SwiftBindingsRuntime", "ios-arm64_x86_64-simulator",
                "native/SwiftBindingsRuntime.xcframework/ios-arm64_x86_64-simulator",
                new[] { "8764190E-4706-3D6B-9884-F70EA8775F1A" });
            Assert("native provenance: an unstaged dependency at a version the restore did not choose is rejected",
                Provenance(
                        new[]
                        {
                            goodBundle[0],
                            new EmbeddedNativeImage("SwiftBindingsRuntime",
                                "/app/Frameworks/SwiftBindingsRuntime.framework/SwiftBindingsRuntime",
                                new[] { "8764190E-4706-3D6B-9884-F70EA8775F1A" }),
                        },
                        packageImages.Append(runtimePreviousSim).ToArray(), staged)
                    .Any(f => f.Contains("matches no framework shipped", StringComparison.OrdinalIgnoreCase)),
                "the app linked a cached older dependency version and the gate accepted it");

            // ── Restore-graph and version coordinates ───────────────────────
            Assert("resolved packages: the restore graph's package entries are the attribution universe",
                ResolvedPackages(goodAssets).Count == 1
                && ResolvedPackages(goodAssets)[0] == ("SwiftBindings.Apple.TipKit", "26.2.14"),
                $"got [{string.Join(", ", ResolvedPackages(goodAssets).Select(p => $"{p.PackageId} {p.Version}"))}]");

            Assert("resolved packages: a project entry in the graph is not a package the app may link",
                ResolvedPackages(projectAssets).Count == 0,
                "a ProjectReference entry was treated as a resolved package");

            Assert("package version: SemVer build metadata is dropped, the way NuGet drops it",
                NormalizePackageVersion("13.0.7+ci.1") == "13.0.7"
                && NormalizePackageVersion(" 13.0.7 ") == "13.0.7"
                && NormalizePackageVersion("1.0.0-preview.3") == "1.0.0-preview.3",
                $"got '{NormalizePackageVersion("13.0.7+ci.1")}'");

            var (metaPlanned, metaStaged) = PlanFeedResolution(
                new List<(string, string, string)> { ("../SwiftBindings.Nuke.csproj", "SwiftBindings.Nuke", "13.0.7+ci.1") },
                new[] { "SwiftBindings.Nuke.13.0.7.nupkg" },
                _ => null,
                "feed");
            Assert("feed plan: a version carrying build metadata still matches the archive pack wrote",
                metaStaged.Contains("SwiftBindings.Nuke") && metaPlanned[0].Version == "13.0.7",
                $"staged=[{string.Join(", ", metaStaged)}] version={metaPlanned[0].Version}");

            // ── Staged-archive provenance ───────────────────────────────────
            // The lane's NuGet.config lists nuget.org alongside the staged feed
            // (the dependency closure needs it), and NuGet selects on id and
            // version alone. So an already-published id+version can satisfy the
            // restore from the published bytes while every downstream check
            // still passes. Only the archive itself separates the two.
            var feedRoot = (AbsolutePath)Path.Combine(
                Path.GetTempPath(), "swiftbindings-consume-selftest", "staged-archive");
            if (Directory.Exists(feedRoot)) Directory.Delete(feedRoot, recursive: true);
            var stagedFeed = feedRoot / "feed";
            var stagedCache = feedRoot / "packages";
            var archiveDir = stagedCache / "swiftbindings.apple.tipkit" / "26.2.14";
            stagedFeed.CreateDirectory();
            archiveDir.CreateDirectory();
            File.WriteAllText(stagedFeed / "SwiftBindings.Apple.TipKit.26.2.14.nupkg", "bytes this release built");

            var stagedOnly = new[] { ("SwiftBindings.Apple.TipKit", "26.2.14") };
            var cachedArchive = archiveDir / "swiftbindings.apple.tipkit.26.2.14.nupkg";

            AssertThrows("staged archive: a package never extracted into the lane cache is not called consumed",
                () => AssertStagedPackagesCameFromTheFeed(stagedFeed, stagedCache, stagedOnly, "self-test", _ => { }),
                "holds no archive");

            File.WriteAllText(cachedArchive, "bytes nuget.org already published");
            AssertThrows("staged archive: an archive restored from somewhere other than the staged feed is rejected",
                () => AssertStagedPackagesCameFromTheFeed(stagedFeed, stagedCache, stagedOnly, "self-test", _ => { }),
                "is not the one the release staged");

            File.WriteAllText(cachedArchive, "bytes this release built");
            var stagedArchiveLines = new List<string>();
            AssertStagedPackagesCameFromTheFeed(stagedFeed, stagedCache, stagedOnly, "self-test", stagedArchiveLines.Add);
            Assert("staged archive: the archive the release staged is the archive the lane restored",
                stagedArchiveLines.Any(l => l.Contains("restored from the staged archive", StringComparison.Ordinal)),
                "the matching case did not report the identity it verified");

            File.Delete(stagedFeed / "SwiftBindings.Apple.TipKit.26.2.14.nupkg");
            AssertThrows("staged archive: a package the lane called staged but the feed does not hold is a hard error",
                () => AssertStagedPackagesCameFromTheFeed(stagedFeed, stagedCache, stagedOnly, "self-test", _ => { }),
                "holds no SwiftBindings.Apple.TipKit.26.2.14.nupkg");

            Assert("lane NuGet.config: a feed path with an XML metacharacter stays well-formed",
                ConsumptionNuGetConfig((AbsolutePath)"/tmp/staged&A").Contains("value=\"/tmp/staged&amp;A\"", StringComparison.Ordinal),
                "the feed path was interpolated raw and would produce a malformed NuGet.config");

            // ── Verdict ─────────────────────────────────────────────────────
            if (failures.Count > 0)
            {
                Log.Error("Release-consumption self-test failures:");
                foreach (var f in failures) Log.Error("{Failure}", f);
                throw new Exception($"{failures.Count} release-consumption test case(s) failed ({passed} passed)");
            }

            Log.Information("All {Count} release-consumption test cases passed.", passed);
        });
}
