using Nuke.Common;
using Nuke.Common.IO;
using Serilog;

partial class Build
{
    // ═══════════════════════════════════════════════════════════════════════
    //  In-process self-tests for the ReleaseWave planning layer.
    //
    //  Everything covered here is pure or filesystem-scratch — no network, no
    //  simulator, no dotnet build — so `dotnet nuke TestHarness` stays fast
    //  enough to run on every change. The orchestration itself (Phase 1/3) is
    //  exercised by the acceptance runs, not here.
    //
    //  Conventions follow Build.RegressionValidate.SelfTest.cs: a local Assert
    //  collecting failures, scratch dirs under a gitignored sentinel prefix,
    //  try/finally cleanup, one aggregated throw at the end.
    // ═══════════════════════════════════════════════════════════════════════

    const string ReleaseWaveSelfTestPrefix = ".selftest-wave-";

    Target TestReleaseWave => _ => _
        .Description("In-process self-tests for ReleaseWave planning (spec parsing, versions, graph, pins)")
        .Executes(() =>
        {
            var failures = new List<string>();
            var passed = 0;

            void Assert(string name, bool condition, string detail)
            {
                if (condition) { passed++; return; }
                failures.Add($"{name}: {detail}");
                Log.Error("  FAIL {Name} — {Detail}", name, detail);
            }

            // ── wave-spec parsing ────────────────────────────────────────────
            var spec = ParseWaveSpec("Nuke=13.0.10, Stripe=26.0.1 ,Matter=26.2.9");
            Assert("spec.count", spec.Count == 3, $"got {spec.Count}");
            Assert("spec.order", spec[0].Library == "Nuke" && spec[2].Library == "Matter",
                $"got {string.Join(",", spec.Select(s => s.Library))}");
            Assert("spec.trims", spec[1] == ("Stripe", "26.0.1"), $"got {spec[1]}");

            Assert("spec.prerelease-ok", ParseWaveSpec("A=1.0.0-beta.1")[0].Version == "1.0.0-beta.1",
                "prerelease version rejected");

            foreach (var (bad, why) in new[]
                     {
                         ("", "empty spec"),
                         ("Nuke", "missing '='"),
                         ("Nuke=", "empty version"),
                         ("=1.0.0", "empty library"),
                         ("Nuke=abc", "non-semver version"),
                         ("Nuke=1.0.0,Nuke=1.0.1", "duplicate library"),
                         ("Nuke=1.0.0=2.0.0", "two '=' signs"),
                         // Not SemVer, and NuGet would normalize or reject them —
                         // either way the version the wave TAGS with would stop
                         // matching the version it PACKED.
                         ("Nuke=1.0.0-01", "leading-zero numeric prerelease identifier"),
                         ("Nuke=1.0.0-alpha.01", "leading-zero in a later identifier"),
                         ("Nuke=1.0.0-alpha..1", "empty prerelease dot component"),
                         ("Nuke=1.0.0-", "empty prerelease"),
                         ("Nuke=1.0.0-alpha.", "trailing dot in prerelease"),
                         ("Nuke=1.0.0+", "empty build metadata"),
                         ("Nuke=1.0.0+a..b", "empty build-metadata component"),
                     })
            {
                var threw = false;
                try { ParseWaveSpec(bad); } catch (InvalidOperationException) { threw = true; }
                Assert($"spec.rejects[{why}]", threw, $"'{bad}' was accepted");
            }

            // …and the SemVer shapes that ARE legal must stay legal — tightening
            // the pattern must not amputate valid prerelease forms.
            foreach (var (good, why) in new[]
                     {
                         ("A=1.0.0-0.3.7", "numeric-zero identifier is legal alone"),
                         ("A=1.0.0-0alpha", "leading zero is fine in an alphanumeric id"),
                         ("A=1.0.0-alpha-1", "hyphens inside an identifier"),
                         ("A=1.0.0-rc.1+build.5", "prerelease plus build metadata"),
                         ("A=1.0.0+20260730.sha.abc", "build metadata only"),
                     })
            {
                var accepted = true;
                try { ParseWaveSpec(good); } catch (InvalidOperationException) { accepted = false; }
                Assert($"spec.accepts[{why}]", accepted, $"'{good}' was rejected");
            }

            // ── version comparison ───────────────────────────────────────────
            Assert("cmp.patch", CompareVersions("1.2.3", "1.2.4") < 0, "1.2.3 !< 1.2.4");
            Assert("cmp.minor", CompareVersions("1.3.0", "1.2.99") > 0, "1.3.0 !> 1.2.99");
            Assert("cmp.major-numeric", CompareVersions("10.0.0", "9.0.0") > 0,
                "10.0.0 !> 9.0.0 (string compare leaked through)");
            Assert("cmp.equal", CompareVersions("26.2.9", "26.2.9") == 0, "26.2.9 != 26.2.9");
            Assert("cmp.release-beats-prerelease", CompareVersions("1.2.3", "1.2.3-beta") > 0,
                "release did not outrank prerelease");
            Assert("cmp.prerelease-ordinal", CompareVersions("1.0.0-alpha", "1.0.0-beta") < 0,
                "alpha !< beta");
            Assert("cmp.build-metadata-ignored", CompareVersions("1.2.3+abc", "1.2.3") == 0,
                "build metadata affected precedence");
            // SemVer 2.0 §11.4 — the shapes an ordinal tail compare gets wrong.
            // Reachable through --auto-version for any package whose highest
            // published versions are prereleases (Nuke/Lottie carry historical
            // *-preview.* tags).
            Assert("cmp.prerelease-numeric", CompareVersions("1.0.0-rc.10", "1.0.0-rc.2") > 0,
                "rc.10 !> rc.2 (ordinal tail compare)");
            Assert("cmp.prerelease-numeric-below-alpha", CompareVersions("1.0.0-1", "1.0.0-alpha") < 0,
                "a numeric identifier did not rank below an alphanumeric one");
            Assert("cmp.prerelease-prefix-shorter", CompareVersions("1.0.0-alpha", "1.0.0-alpha.1") < 0,
                "a shorter prerelease prefix did not rank below the longer one");
            Assert("cmp.prerelease-equal", CompareVersions("1.0.0-rc.1", "1.0.0-rc.1") == 0,
                "identical prereleases did not compare equal");
            Assert("cmp.short-form", CompareVersions("1.2", "1.2.0") == 0, "missing patch != 0");
            // Numeric prerelease identifiers have no width limit in SemVer, so
            // int.TryParse silently fails on a long one and drops it to a STRING
            // compare — where "9" > "10000000000000000000". BigInteger keeps it
            // numeric no matter how long.
            Assert("cmp.prerelease-bignum",
                CompareVersions("1.0.0-rc.10000000000000000000", "1.0.0-rc.9") > 0,
                "an over-int64 numeric prerelease identifier fell back to a string compare");
            Assert("cmp.prerelease-bignum-equal",
                CompareVersions("1.0.0-rc.99999999999999999999", "1.0.0-rc.99999999999999999999") == 0,
                "equal over-int64 identifiers did not compare equal");

            // ── patch arithmetic ─────────────────────────────────────────────
            Assert("next.simple", NextPatchVersion("13.0.9") == "13.0.10", NextPatchVersion("13.0.9"));
            Assert("next.rollover-none", NextPatchVersion("1.2.99") == "1.2.100", NextPatchVersion("1.2.99"));
            Assert("next.drops-prerelease", NextPatchVersion("1.2.3-beta") == "1.2.4",
                NextPatchVersion("1.2.3-beta"));

            // ── third-party version convention ───────────────────────────────
            // Rebuild of the same upstream → patch-bump the published version.
            // This is the case where the NuGet patch legitimately runs ahead of
            // the upstream patch (Lottie upstream 4.6.0 → NuGet 4.6.5).
            Assert("resolve.rebuild", ResolveThirdPartyVersion("4.6.0", "4.6.4") == "4.6.5",
                ResolveThirdPartyVersion("4.6.0", "4.6.4"));
            // New upstream release → ship AT the upstream version.
            Assert("resolve.new-upstream", ResolveThirdPartyVersion("26.0.1", "25.17.1") == "26.0.1",
                ResolveThirdPartyVersion("26.0.1", "25.17.1"));
            // Upstream == published → still a rebuild.
            Assert("resolve.equal", ResolveThirdPartyVersion("6.4.1", "6.4.1") == "6.4.2",
                ResolveThirdPartyVersion("6.4.1", "6.4.1"));
            // Never published → first release ships at upstream.
            Assert("resolve.never-published", ResolveThirdPartyVersion("18.1.0", null) == "18.1.0",
                ResolveThirdPartyVersion("18.1.0", null));
            // No upstream (Apple-style) but published → patch bump.
            Assert("resolve.no-upstream", ResolveThirdPartyVersion(null, "26.2.8") == "26.2.9",
                ResolveThirdPartyVersion(null, "26.2.8"));
            var resolveThrew = false;
            try { ResolveThirdPartyVersion(null, null); } catch (InvalidOperationException) { resolveThrew = true; }
            Assert("resolve.neither-throws", resolveThrew, "no-upstream + no-published was accepted");

            // ── dependency graph ─────────────────────────────────────────────
            var owners = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["SwiftBindings.Apple.Matter"] = "Matter",
                ["SwiftBindings.Apple.MatterSupport"] = "MatterSupport",
                ["SwiftBindings.Apple.RealityFoundation"] = "RealityFoundation",
                ["SwiftBindings.Apple.RealityKit"] = "RealityKit",
                ["SwiftBindings.BlinkID"] = "BlinkID",
                ["SwiftBindings.BlinkIDUX"] = "BlinkIDUX",
                ["SwiftBindings.Stripe.Core"] = "Stripe",
                ["SwiftBindings.Stripe.Payments"] = "Stripe",
            };

            var refs = new List<CrossPackageRef>
            {
                new("MatterSupport", "/x/MatterSupport.csproj", "SwiftBindings.Apple.Matter", "26.2.8",
                    WaveRefKind.PackageReference),
                // BlinkIDUX reaches BlinkID three different ways at once; the
                // graph must see ONE edge, and the version-carrying forms must
                // both be re-pinnable.
                new("BlinkIDUX", "/x/BlinkIDUX.csproj", "SwiftBindings.BlinkID", null,
                    WaveRefKind.ProjectReference),
                new("BlinkIDUX", "/x/BlinkIDUX.csproj", "SwiftBindings.BlinkID", "7.8.0",
                    WaveRefKind.SwiftFrameworkDependency),
                // Intra-library: Stripe.Payments → Stripe.Core, same directory.
                new("Stripe", "/x/StripePayments.csproj", "SwiftBindings.Stripe.Core", "26.0.0",
                    WaveRefKind.SwiftFrameworkDependency),
                // Third-party package that this repo does not produce.
                new("Stripe", "/x/StripePayments.csproj", "SwiftBindings.Unknown.Thing", "1.0.0",
                    WaveRefKind.PackageReference),
            };

            var wave = new[] { "Matter", "MatterSupport", "BlinkID", "BlinkIDUX", "Stripe" };
            var (deps, external) = BuildWaveDeps(wave, refs, owners);

            Assert("graph.packageref-edge", deps["MatterSupport"].SequenceEqual(new[] { "Matter" }),
                $"got [{string.Join(",", deps["MatterSupport"])}]");
            Assert("graph.projectref-and-sfd-edge",
                deps["BlinkIDUX"].SequenceEqual(new[] { "BlinkID" }),
                $"got [{string.Join(",", deps["BlinkIDUX"])}] — ProjectReference/SFD edges must order the wave, " +
                "otherwise two libraries build into one tree concurrently");
            Assert("graph.no-duplicate-edge", deps["BlinkIDUX"].Count == 1,
                $"three refs collapsed to {deps["BlinkIDUX"].Count} edges");
            Assert("graph.intra-library-dropped", deps["Stripe"].Count == 0,
                $"got [{string.Join(",", deps["Stripe"])}] — a library must not depend on itself");
            Assert("graph.base-has-no-deps", deps["Matter"].Count == 0 && deps["BlinkID"].Count == 0,
                "a base library picked up edges");
            Assert("graph.unknown-package-ignored", !deps.Values.Any(v => v.Contains("Unknown")),
                "a non-repo package became an edge");
            Assert("graph.external-empty", external.Count == 0,
                $"got {external.Count} external refs when every base is in the wave");

            // Base NOT in the wave: only the feed-restoring kind is reported,
            // because only that kind can fail with NU1102.
            var (_, external2) = BuildWaveDeps(new[] { "MatterSupport", "BlinkIDUX" }, refs, owners);
            Assert("graph.external-packageref-only",
                external2.Count == 1 && external2[0].PackageId == "SwiftBindings.Apple.Matter",
                $"got [{string.Join(",", external2.Select(e => $"{e.PackageId}/{e.Kind}"))}]");

            // ── topological levels ───────────────────────────────────────────
            var levels = TopoLevels(deps);
            Assert("topo.level-count", levels.Count == 2, $"got {levels.Count}");
            Assert("topo.level0", levels[0].SequenceEqual(new[] { "BlinkID", "Matter", "Stripe" }),
                $"got [{string.Join(",", levels[0])}] (must be ordinal-sorted for reproducibility)");
            Assert("topo.level1", levels[1].SequenceEqual(new[] { "BlinkIDUX", "MatterSupport" }),
                $"got [{string.Join(",", levels[1])}]");

            var singleton = TopoLevels(new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
                { ["CryptoKit"] = Array.Empty<string>() });
            Assert("topo.singleton", singleton.Count == 1 && singleton[0][0] == "CryptoKit",
                "single-library wave did not produce one level");

            // A dependency on a library outside the wave is not an ordering
            // constraint — it can't be, there's nothing to wait for.
            var outOfWave = TopoLevels(new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
                { ["MatterSupport"] = new[] { "Matter" } });
            Assert("topo.ignores-out-of-wave-dep", outOfWave.Count == 1,
                $"got {outOfWave.Count} levels for a wave whose only dep is external");

            var cycleThrew = false;
            string cycleMessage = "";
            try
            {
                TopoLevels(new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
                {
                    ["A"] = new[] { "B" },
                    ["B"] = new[] { "C" },
                    ["C"] = new[] { "A" },
                });
            }
            catch (InvalidOperationException ex) { cycleThrew = true; cycleMessage = ex.Message; }
            Assert("topo.cycle-throws", cycleThrew, "a 3-cycle produced an order");
            Assert("topo.cycle-names-nodes",
                cycleMessage.Contains("A") && cycleMessage.Contains("B") && cycleMessage.Contains("C"),
                $"cycle error did not name the members: {cycleMessage}");

            // ── intra-library publish tiers ──────────────────────────────────
            // A library's OWN packages are dependency-ordered at push time too.
            // Without this, all 14 Stripe packages race: a Core push can fail in
            // the same instant its dependents publish, leaving live packages
            // declaring a dependency on a version that never shipped.
            var stripeIds = new[]
            {
                "SwiftBindings.Stripe.Core",
                "SwiftBindings.Stripe.PaymentSheet",
                "SwiftBindings.Stripe.Payments",
            };
            var stripeRefs = new List<CrossPackageRef>
            {
                new("Stripe", "/x/SwiftBindings.Stripe.Payments.csproj", "SwiftBindings.Stripe.Core",
                    "26.0.0", WaveRefKind.SwiftFrameworkDependency),
                new("Stripe", "/x/SwiftBindings.Stripe.PaymentSheet.csproj", "SwiftBindings.Stripe.Payments",
                    "26.0.0", WaveRefKind.SwiftFrameworkDependency),
                new("Stripe", "/x/SwiftBindings.Stripe.PaymentSheet.csproj", "SwiftBindings.Stripe.Core",
                    "26.0.0", WaveRefKind.SwiftFrameworkDependency),
                // Internal product: built, never packed, so it can never be a
                // publish-ordering constraint.
                new("Stripe", "/x/SwiftBindings.Stripe.Payments.csproj", "SwiftBindings.Stripe.CameraCore",
                    "26.0.0", WaveRefKind.SwiftFrameworkDependency),
                // A different library's refs must not leak into these tiers.
                new("BlinkIDUX", "/x/SwiftBindings.BlinkIDUX.csproj", "SwiftBindings.BlinkID",
                    "7.8.0", WaveRefKind.SwiftFrameworkDependency),
                // A csproj referencing its own package id is not a cycle.
                new("Stripe", "/x/SwiftBindings.Stripe.Core.csproj", "SwiftBindings.Stripe.Core",
                    "26.0.0", WaveRefKind.SwiftFrameworkDependency),
            };

            var tiers = ComputeIntraLibraryPublishTiers("Stripe", stripeIds, stripeRefs);
            Assert("tiers.count", tiers.Count == 3, $"got {tiers.Count} tier(s)");
            Assert("tiers.base-first",
                tiers.Count == 3 && tiers[0].SequenceEqual(new[] { "SwiftBindings.Stripe.Core" }),
                $"tier 0 = [{string.Join(",", tiers.Count > 0 ? tiers[0] : Array.Empty<string>())}]");
            Assert("tiers.middle",
                tiers.Count == 3 && tiers[1].SequenceEqual(new[] { "SwiftBindings.Stripe.Payments" }),
                $"tier 1 = [{string.Join(",", tiers.Count > 1 ? tiers[1] : Array.Empty<string>())}]");
            Assert("tiers.leaf-last",
                tiers.Count == 3 && tiers[2].SequenceEqual(new[] { "SwiftBindings.Stripe.PaymentSheet" }),
                $"tier 2 = [{string.Join(",", tiers.Count > 2 ? tiers[2] : Array.Empty<string>())}]");
            Assert("tiers.covers-every-package",
                tiers.SelectMany(t => t).OrderBy(x => x, StringComparer.Ordinal)
                    .SequenceEqual(stripeIds.OrderBy(x => x, StringComparer.Ordinal)),
                $"tiers dropped or invented packages: [{string.Join(",", tiers.SelectMany(t => t))}]");
            Assert("tiers.internal-not-a-tier",
                !tiers.SelectMany(t => t).Contains("SwiftBindings.Stripe.CameraCore"),
                "a non-packed internal product entered the publish tiers");

            // The common case: independent packages all push together.
            var flatTiers = ComputeIntraLibraryPublishTiers(
                "Apple", new[] { "SwiftBindings.Apple.CryptoKit" }, stripeRefs);
            Assert("tiers.single-package", flatTiers.Count == 1 && flatTiers[0].Count == 1,
                $"got {flatTiers.Count} tier(s) for a one-package library");

            var independent = ComputeIntraLibraryPublishTiers(
                "Stripe", stripeIds, Array.Empty<CrossPackageRef>());
            Assert("tiers.no-refs-one-tier",
                independent.Count == 1 && independent[0].Count == 3,
                $"got {independent.Count} tier(s) with no sibling refs");

            var tierCycleThrew = false;
            var tierCycleMessage = "";
            try
            {
                ComputeIntraLibraryPublishTiers("Stripe",
                    new[] { "SwiftBindings.Stripe.A", "SwiftBindings.Stripe.B" },
                    new[]
                    {
                        new CrossPackageRef("Stripe", "/x/SwiftBindings.Stripe.A.csproj",
                            "SwiftBindings.Stripe.B", "1.0.0", WaveRefKind.SwiftFrameworkDependency),
                        new CrossPackageRef("Stripe", "/x/SwiftBindings.Stripe.B.csproj",
                            "SwiftBindings.Stripe.A", "1.0.0", WaveRefKind.SwiftFrameworkDependency),
                    });
            }
            catch (InvalidOperationException ex) { tierCycleThrew = true; tierCycleMessage = ex.Message; }
            Assert("tiers.cycle-throws", tierCycleThrew, "a 2-cycle between siblings produced an order");
            Assert("tiers.cycle-names-library", tierCycleMessage.Contains("Stripe"),
                $"cycle error did not say which library: {tierCycleMessage}");

            // ── planned-nupkg allow-list ─────────────────────────────────────
            // Re-using --wave-output must not let a previous run's file be
            // staged into local-packages/ or pushed to nuget.org.
            var stripePlan = new WaveLibraryPlan(
                "Stripe", LibraryKind.ThirdParty, "26.0.1", stripeIds, Array.Empty<string>(), 0);
            var expectedNames = WaveExpectedNupkgNames(stripePlan);
            Assert("expected.count", expectedNames.Count == 3, $"got {expectedNames.Count}");
            Assert("expected.name-shape",
                expectedNames.Contains("SwiftBindings.Stripe.Core.26.0.1.nupkg"),
                $"got [{string.Join(",", expectedNames)}]");
            Assert("expected.rejects-other-version",
                !expectedNames.Contains("SwiftBindings.Stripe.Core.26.0.0.nupkg"),
                "a stale-version nupkg matched the allow-list");
            Assert("expected.rejects-other-package",
                !expectedNames.Contains("SwiftBindings.Nuke.26.0.1.nupkg"),
                "an unrelated package matched the allow-list");

            var pkgScratch = TemporaryDirectory / $"{ReleaseWaveSelfTestPrefix}pkgs";
            try
            {
                var libPkgDir = pkgScratch / "packages" / "Stripe";
                libPkgDir.CreateDirectory();
                foreach (var name in new[]
                         {
                             "SwiftBindings.Stripe.Core.26.0.1.nupkg",       // planned
                             "SwiftBindings.Stripe.Payments.26.0.1.nupkg",   // planned
                             "SwiftBindings.Stripe.Core.26.0.0.nupkg",       // stale prior run
                             "SwiftBindings.Nuke.13.0.11.nupkg",            // stray
                             "notes.txt",                                    // not a package at all
                         })
                    (libPkgDir / name).WriteAllText("x");

                var planned = WavePlannedNupkgs(stripePlan, pkgScratch)
                    .Select(p => p.Name).ToList();
                Assert("planned.filters-to-plan",
                    planned.SequenceEqual(new[]
                    {
                        "SwiftBindings.Stripe.Core.26.0.1.nupkg",
                        "SwiftBindings.Stripe.Payments.26.0.1.nupkg",
                    }),
                    $"got [{string.Join(",", planned)}] — a stale or stray nupkg would have been staged/pushed");

                var missingDir = WavePlannedNupkgs(
                    stripePlan with { Name = "NotPacked" }, pkgScratch);
                Assert("planned.missing-dir-empty", missingDir.Count == 0,
                    $"got {missingDir.Count} nupkg(s) for a library with no output directory");
            }
            finally
            {
                if (Directory.Exists(pkgScratch)) Directory.Delete(pkgScratch, recursive: true);
            }

            // ── pin bumps ────────────────────────────────────────────────────
            var waveVersions = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Matter"] = "26.2.9",
                ["MatterSupport"] = "26.2.9",
                ["BlinkID"] = "7.8.4",
                ["BlinkIDUX"] = "7.8.4",
                ["Stripe"] = "26.0.1",
            };
            var bumps = ComputePinBumps(refs, owners, waveVersions);

            Assert("bump.inter-library-packageref",
                bumps.Any(b => b.ConsumerLibrary == "MatterSupport"
                            && b.PackageId == "SwiftBindings.Apple.Matter"
                            && b is { FromVersion: "26.2.8", ToVersion: "26.2.9", Kind: WaveRefKind.PackageReference }),
                "MatterSupport's Matter pin was not bumped");
            Assert("bump.inter-library-sfd",
                bumps.Any(b => b.ConsumerLibrary == "BlinkIDUX"
                            && b is { FromVersion: "7.8.0", ToVersion: "7.8.4", Kind: WaveRefKind.SwiftFrameworkDependency }),
                "BlinkIDUX's cross-library SFD PackageVersion was not bumped");
            // The one that would otherwise ship Payments 26.0.1 declaring a
            // dependency on Core 26.0.0: intra-library sibling pins must move
            // with the library's own version, since /p:Version does not touch them.
            Assert("bump.intra-library-sfd",
                bumps.Any(b => b.ConsumerLibrary == "Stripe"
                            && b.PackageId == "SwiftBindings.Stripe.Core"
                            && b is { FromVersion: "26.0.0", ToVersion: "26.0.1" }),
                "Stripe's intra-library sibling PackageVersion was not bumped — " +
                "the package would ship declaring a dependency on the PREVIOUS sibling version");
            Assert("bump.projectref-never",
                bumps.All(b => b.Kind != WaveRefKind.ProjectReference),
                "a ProjectReference produced a version bump (it has no version)");
            Assert("bump.unknown-package-ignored",
                bumps.All(b => b.PackageId != "SwiftBindings.Unknown.Thing"),
                "a non-repo package was bumped");

            // Idempotence: pins already at the wave version produce nothing.
            var settled = ComputePinBumps(refs, owners, new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["MatterSupport"] = "26.2.9", ["Matter"] = "26.2.8",
                ["BlinkIDUX"] = "7.8.4", ["BlinkID"] = "7.8.0",
                ["Stripe"] = "26.0.0",
            });
            Assert("bump.idempotent", settled.Count == 0,
                $"got {settled.Count} bumps when every pin already matched: " +
                string.Join(", ", settled.Select(b => $"{b.PackageId} {b.FromVersion}→{b.ToVersion}")));

            // A base outside the wave is left alone — the wave is not
            // republishing it, so its pin is still correct.
            var partial = ComputePinBumps(refs, owners, new Dictionary<string, string>(StringComparer.Ordinal)
                { ["MatterSupport"] = "26.2.9" });
            Assert("bump.base-not-in-wave-untouched", partial.Count == 0,
                $"got {partial.Count} bumps for a base that is not being released");

            // ── ApplyPinBumps round-trip on real csproj text ─────────────────
            var scratch = LibrariesDir / $"{ReleaseWaveSelfTestPrefix}pins";
            try
            {
                scratch.CreateDirectory();
                var csproj = scratch / "SwiftBindings.Scratch.csproj";
                csproj.WriteAllText("""
                    <Project Sdk="SwiftBindings.Sdk/0.18.0">
                      <ItemGroup>
                        <PackageReference Include="SwiftBindings.Apple.Matter" Version="26.2.8" />
                        <PackageReference Include="SwiftBindings.Unrelated" Version="1.0.0" />
                        <SwiftFrameworkDependency Include="../BlinkID/BlinkID.xcframework"
                                                  PackageId="SwiftBindings.BlinkID"
                                                  PackageVersion="7.8.0" />
                      </ItemGroup>
                    </Project>
                    """);

                ApplyPinBumps(new[]
                {
                    new WavePinBump("Scratch", (string)csproj, "SwiftBindings.Apple.Matter",
                        "26.2.8", "26.2.9", WaveRefKind.PackageReference),
                    new WavePinBump("Scratch", (string)csproj, "SwiftBindings.BlinkID",
                        "7.8.0", "7.8.4", WaveRefKind.SwiftFrameworkDependency),
                });

                var text = csproj.ReadAllText();
                Assert("apply.packageref",
                    text.Contains("""Include="SwiftBindings.Apple.Matter" Version="26.2.9" """.TrimEnd()),
                    $"PackageReference not rewritten:\n{text}");
                Assert("apply.sfd-multiline",
                    text.Contains("""PackageVersion="7.8.4" """.TrimEnd()),
                    $"multi-line SwiftFrameworkDependency PackageVersion not rewritten:\n{text}");
                Assert("apply.leaves-others",
                    text.Contains("""Include="SwiftBindings.Unrelated" Version="1.0.0" """.TrimEnd()),
                    $"an unrelated PackageReference was modified:\n{text}");
                Assert("apply.sfd-include-untouched",
                    text.Contains("""Include="../BlinkID/BlinkID.xcframework" """.TrimEnd()),
                    $"the SFD Include path was damaged:\n{text}");

                // Re-applying the same bump list must be a no-op (the versions
                // no longer match FromVersion, but ApplyPinBumps writes ToVersion
                // unconditionally, so the file must be byte-identical).
                var before = csproj.ReadAllText();
                ApplyPinBumps(new[]
                {
                    new WavePinBump("Scratch", (string)csproj, "SwiftBindings.Apple.Matter",
                        "26.2.8", "26.2.9", WaveRefKind.PackageReference),
                });
                Assert("apply.idempotent", csproj.ReadAllText() == before,
                    "re-applying a bump changed the file");

                // --wave-spec accepts prerelease versions, so a bump TO one must
                // apply, and a pin that already IS one must be readable and
                // re-writable. A digits-only version group fails both: the
                // rewrite succeeds but the applied-check can't see it (spurious
                // abort), and a prerelease pin is invisible to discovery.
                ApplyPinBumps(new[]
                {
                    new WavePinBump("Scratch", (string)csproj, "SwiftBindings.Apple.Matter",
                        "26.2.9", "26.3.0-beta.1", WaveRefKind.PackageReference),
                    new WavePinBump("Scratch", (string)csproj, "SwiftBindings.BlinkID",
                        "7.8.4", "7.9.0-rc.2", WaveRefKind.SwiftFrameworkDependency),
                });
                var pre = csproj.ReadAllText();
                Assert("apply.prerelease-packageref",
                    pre.Contains("""Version="26.3.0-beta.1" """.TrimEnd()),
                    $"PackageReference not rewritten to a prerelease:\n{pre}");
                Assert("apply.prerelease-sfd",
                    pre.Contains("""PackageVersion="7.9.0-rc.2" """.TrimEnd()),
                    $"SFD PackageVersion not rewritten to a prerelease:\n{pre}");

                // …and back out of a prerelease pin, which requires READING it.
                ApplyPinBumps(new[]
                {
                    new WavePinBump("Scratch", (string)csproj, "SwiftBindings.Apple.Matter",
                        "26.3.0-beta.1", "26.3.0", WaveRefKind.PackageReference),
                });
                Assert("apply.prerelease-source-pin-readable",
                    csproj.ReadAllText().Contains("""Version="26.3.0" """.TrimEnd()),
                    $"a prerelease pin could not be rewritten back to a release:\n{csproj.ReadAllText()}");

                // A planned bump whose tag is not present must ABORT, not be
                // silently dropped. Silent-drop is the dangerous shape: the plan
                // advertises a pin rewrite, the wave carries on, and the package
                // ships pinned to the old sibling version.
                before = csproj.ReadAllText();
                Exception? notApplied = null;
                try
                {
                    ApplyPinBumps(new[]
                    {
                        new WavePinBump("Scratch", (string)csproj, "SwiftBindings.Nope.Missing",
                            "1.0.0", "2.0.0", WaveRefKind.PackageReference),
                    });
                }
                catch (Exception ex) { notApplied = ex; }
                Assert("apply.fails-when-nothing-rewritten",
                    notApplied is InvalidOperationException && notApplied.Message.Contains("SwiftBindings.Nope.Missing"),
                    $"expected an abort naming the un-applied bump, got: {notApplied?.Message ?? "(no exception)"}");
                Assert("apply.failed-bump-left-file-alone",
                    csproj.ReadAllText() == before,
                    "the csproj was modified by a bump that could not be applied");

                // Multi-FILE atomicity: a bump that cannot be applied in the
                // SECOND csproj must leave the FIRST one untouched as well.
                // Writing file 1 and then aborting is the worst outcome — the
                // wave stops with the working tree half-rewritten, so a re-run
                // computes its bumps from an inconsistent starting state.
                var other = scratch / "SwiftBindings.Other.csproj";
                other.WriteAllText("""
                    <Project Sdk="SwiftBindings.Sdk/0.18.0">
                      <ItemGroup>
                        <PackageReference Include="SwiftBindings.Apple.Matter" Version="26.2.8" />
                      </ItemGroup>
                    </Project>
                    """);
                var firstBefore = csproj.ReadAllText();
                var otherBefore = other.ReadAllText();
                Exception? atomicEx = null;
                try
                {
                    ApplyPinBumps(new[]
                    {
                        // applies cleanly…
                        new WavePinBump("Scratch", (string)csproj, "SwiftBindings.BlinkID",
                            "7.9.0-rc.2", "7.9.1", WaveRefKind.SwiftFrameworkDependency),
                        // …but this one cannot, in a different file
                        new WavePinBump("Other", (string)other, "SwiftBindings.Nope.Missing",
                            "1.0.0", "2.0.0", WaveRefKind.PackageReference),
                    });
                }
                catch (Exception ex) { atomicEx = ex; }
                Assert("apply.multi-file-aborts", atomicEx is InvalidOperationException,
                    $"expected an abort, got: {atomicEx?.Message ?? "(no exception)"}");
                Assert("apply.multi-file-atomic-first-file",
                    csproj.ReadAllText() == firstBefore,
                    "the FIRST csproj was written even though a later file's bump failed — " +
                    "the working tree is now half-rewritten");
                Assert("apply.multi-file-atomic-second-file",
                    other.ReadAllText() == otherBefore,
                    "the failing csproj was modified");

                // …and the same list minus the bad bump must actually write.
                ApplyPinBumps(new[]
                {
                    new WavePinBump("Scratch", (string)csproj, "SwiftBindings.BlinkID",
                        "7.9.0-rc.2", "7.9.1", WaveRefKind.SwiftFrameworkDependency),
                    new WavePinBump("Other", (string)other, "SwiftBindings.Apple.Matter",
                        "26.2.8", "26.2.9", WaveRefKind.PackageReference),
                });
                Assert("apply.multi-file-writes-all",
                    csproj.ReadAllText().Contains("""PackageVersion="7.9.1" """.TrimEnd())
                    && other.ReadAllText().Contains("""Version="26.2.9" """.TrimEnd()),
                    "a valid multi-file bump list did not rewrite both files");
            }
            finally
            {
                if (Directory.Exists(scratch)) Directory.Delete(scratch, recursive: true);
            }

            // ── repo-wide sanity: the live tree must be plannable ────────────
            // These run against the ACTUAL csprojs, so a future edit that
            // introduces an unreadable pin, a package-id collision, or a
            // dependency cycle fails TestHarness rather than a release wave.
            var liveOwners = DiscoverPackageOwners();
            Assert("live.owners-nonempty", liveOwners.Count > 20, $"only {liveOwners.Count} packages discovered");

            var liveRefs = DiscoverCrossPackageRefs();
            Assert("live.refs-parse", liveRefs.Count > 0, "no intra-repo references discovered at all");

            var allLibs = DiscoverAllEntries().Select(e => e.Library).ToList();
            var (liveDeps, _) = BuildWaveDeps(allLibs, liveRefs, liveOwners);
            var liveLevels = TopoLevels(liveDeps);   // throws on a cycle
            Assert("live.no-cycle", liveLevels.Count >= 1, "topo produced no levels");
            Assert("live.known-edges",
                liveDeps.TryGetValue("MatterSupport", out var ms) && ms.Contains("Matter")
                && liveDeps.TryGetValue("RealityKit", out var rk) && rk.Contains("RealityFoundation")
                && liveDeps.TryGetValue("BlinkIDUX", out var bux) && bux.Contains("BlinkID"),
                "the three known inter-library edges were not all detected: " +
                string.Join("; ", liveDeps.Where(kv => kv.Value.Count > 0)
                    .Select(kv => $"{kv.Key}→[{string.Join(",", kv.Value)}]")));

            Assert("live.sdk-version-single", DiscoverPinnedSdkVersion().Length > 0,
                "csprojs pin more than one SDK version (or none)");

            Assert("live.do-not-ship-rejected",
                DoNotShipLibraries.Contains("AppIntents", StringComparer.Ordinal),
                "AppIntents is no longer on the do-not-ship list");

            // ── report ───────────────────────────────────────────────────────
            if (failures.Count > 0)
                throw new InvalidOperationException(
                    $"TestReleaseWave: {failures.Count} assertion(s) failed:\n  " + string.Join("\n  ", failures));

            Log.Information("TestReleaseWave: {Passed} assertions passed.", passed);
        });
}
