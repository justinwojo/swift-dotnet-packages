using System.Diagnostics;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Nuke.Common;
using Nuke.Common.IO;
using Serilog;
using SwiftBindings.Build.Helpers;
using SwiftBindings.Build.Models;

partial class Build
{
    // ═══════════════════════════════════════════════════════════════════════
    //  Release consumption lane.
    //
    //  Every other test lane in this repo builds the test app through
    //  <ProjectReference>, so a green release proved that the SOURCE in the
    //  working tree works — never that the .nupkg the release is about to push
    //  restores, links, loads and runs. PackValidate proves packing succeeds,
    //  which is a different claim again.
    //
    //  This lane closes that gap: it restores the EXACT staged .nupkg files
    //  into the same test app, from a feed that contains only them (plus
    //  nuget.org for the SDK/runtime dependencies a real consumer also gets),
    //  into a package cache of its own, builds it in PackageReference mode,
    //  runs it on the simulator, and then proves — statically from the restore
    //  graph and the deployed binary, and dynamically from the app's own
    //  output — that the assembly which ran came out of the package rather than
    //  a sibling project's bin/.
    //
    //  Isolation is by redirected obj/ + bin/ rather than by copying the test
    //  app: several test csprojs reach sideways with relative paths
    //  (../X.xcframework, ../../BlinkID/obj/…), which a copy at a different
    //  depth would silently break. Redirecting the output paths keeps every
    //  relative path meaning exactly what it means in a normal build, while
    //  keeping the package-mode restore out of the tree the ordinary lanes use.
    //
    //  Those same sideways paths are NativeReference items, which is a second
    //  road into the app that a managed reference swap does not touch — see
    //  Build.ReleaseConsumption.NativeProvenance.cs for how the lane keeps the
    //  checkout's frameworks out and proves the package's frameworks are what
    //  the bundle carries.
    // ═══════════════════════════════════════════════════════════════════════

    [Parameter("ReleaseConsume: directory holding the staged .nupkg files to consume (default: local-packages/)")]
    readonly string? ConsumeFeed;

    [Parameter("ReleaseConsume: per-package version overrides, \"Id=Version;Id2=Version2\" (default: --version for every referenced package)")]
    readonly string? ConsumeVersions;

    [Parameter("ReleaseConsume: scratch root for the isolated obj/bin/package cache (default: artifacts/release-consumption/)")]
    readonly string? ConsumeOutput;

    AbsolutePath ReleaseConsumptionRoot =>
        string.IsNullOrEmpty(ConsumeOutput)
            ? RootDirectory / "artifacts" / "release-consumption"
            : (AbsolutePath)ConsumeOutput;

    /// <summary>
    /// Restore the staged packages into the library's own test app, build it in
    /// PackageReference mode against an isolated package cache, run it on the
    /// simulator, and verify the identity of what actually loaded.
    /// </summary>
    Target ReleaseConsume => _ => _
        .Description("Consume the staged .nupkg from a clean feed + cache and run the test app on the simulator " +
                     "(--library X --version V [--consume-feed dir] [--consume-versions \"Id=V;…\"] [--timeout N] [--device-udid UDID])")
        .Requires(() => Library)
        .Requires(() => Version)
        .Executes(() =>
        {
            var feed = string.IsNullOrEmpty(ConsumeFeed) ? LocalPackagesDir : (AbsolutePath)ConsumeFeed;
            RunReleaseConsumptionLeg(Library!, Version!, feed, Timeout, DeviceUdid, onLine: null);
        });

    /// <summary>
    /// The lane itself, callable from <c>ReleaseWave</c> (which passes its own
    /// per-library log sink and the simulator it already leased). Throws on any
    /// failure — a caller that reaches the next line has a release-consumable
    /// package.
    /// </summary>
    internal void RunReleaseConsumptionLeg(
        string library, string version, AbsolutePath feed, int timeoutSeconds,
        string? simUdid, Action<string>? onLine,
        IReadOnlyDictionary<string, string>? versionOverrides = null)
    {
        void Info(string line) { if (onLine is null) Log.Information("{Line}", line); else onLine(line); }

        var sw = Stopwatch.StartNew();
        var (testDir, appName, bundleId) = ResolveTestNames(library);
        // Package restore/build is expensive and mutating; reject missing StoreKit Sandbox intent
        // before resolving or evicting anything in the isolated consumption lane.
        var storeKitConfiguration = RequireStoreKitSandboxConfiguration(library, "ios", bundleId);
        var laneLabel = $"release-consume/{library}";

        Info($"=== {laneLabel}: consuming staged packages from {feed} ===");

        // 1. Which packages does this test app consume, and at what version?
        var csproj = Directory.EnumerateFiles(testDir, "*.csproj").OrderBy(p => p, StringComparer.Ordinal).FirstOrDefault()
            ?? throw new InvalidOperationException($"{laneLabel}: no test csproj under {testDir}");
        // A caller (ReleaseWave) knows the version of every package in the wave, not just
        // this library's; a co-released dependency has to resolve at ITS wave version or
        // the restore silently falls back to the last published one. An explicit
        // --consume-versions on the CLI wins over the caller's map so a human can pin.
        var overrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (versionOverrides is not null)
            foreach (var (id, ver) in versionOverrides)
                overrides[id] = ver;
        foreach (var (id, ver) in ParseConsumeVersionMap(ConsumeVersions))
            overrides[id] = ver;
        var swaps = ResolveConsumedPackages(File.ReadAllText(csproj), version, overrides);
        if (swaps.Count == 0)
            throw new InvalidOperationException(
                $"{laneLabel}: {Path.GetFileName(csproj)} declares no SwiftBindings ProjectReference, so there is " +
                "nothing to re-point at a package. A test app that references no binding cannot establish " +
                "release consumability — fix the csproj rather than skipping the lane.");

        foreach (var (include, id, ver) in swaps)
            Info($"  {include} → PackageReference {id} {ver}");

        // 2. Reconcile what the lane wants to consume against what the release
        //    actually staged. A package under release MUST come out of the feed
        //    — restoring it from nuget.org would validate the previously
        //    published bytes and call that a green for the release.
        var feedFiles = Directory.Exists(feed)
            ? Directory.EnumerateFiles(feed, "*.nupkg").Select(Path.GetFileName).OfType<string>().ToList()
            : new List<string>();

        (swaps, var stagedIds) = PlanFeedResolution(
            swaps, feedFiles, include => ReadPinnedVersion(testDir, include), $"{laneLabel}: the staged feed {feed}");

        foreach (var (_, id, ver) in swaps)
            Info($"  {id} {ver} — {(stagedIds.Contains(id) ? "staged for this release" : "already published (restored from nuget.org)")}");

        // The exact (id, version) pairs under release. The provenance gates key
        // on the pair rather than the id: the lane's package cache is kept
        // between runs, so an earlier version of the same package is still in
        // it, and a check that only knew the id would either demand that old
        // version's artifacts or let them stand in for this one's.
        var stagedPackages = swaps
            .Where(s => stagedIds.Contains(s.PackageId))
            .Select(s => (s.PackageId, s.Version))
            .ToList();

        // 3. Isolated scratch: obj/, bin/ and the package cache all live here,
        //    so the lane cannot write into (or read stale state out of) the
        //    tree the ProjectReference lanes use, and two lanes can run against
        //    the same test app without racing on project.assets.json.
        var scratch = ReleaseConsumptionRoot / library;
        var packagesDir = scratch / "packages";
        var laneDir = scratch / "lane";
        var objDir = scratch / "obj";
        var binDir = scratch / "bin";

        // The cache is kept between runs (restoring the .NET/iOS dependency
        // closure from scratch is minutes), but the packages UNDER TEST are
        // evicted every time: NuGet keys an extract on id+version alone, so a
        // re-pack of the same version would otherwise be shadowed by the
        // previous run's extract and the lane would validate the old bytes.
        EvictFromPackagesDir(packagesDir, stagedPackages, Info);

        // obj/ is wiped rather than reused: a stale assets file from a previous
        // shape (or from a run whose swaps differed) is exactly the input that
        // makes a package-mode assertion pass without a package-mode restore.
        if (Directory.Exists(objDir)) Directory.Delete(objDir, recursive: true);
        if (Directory.Exists(binDir)) Directory.Delete(binDir, recursive: true);

        // 4. Feed + lane props. The NuGet.config carries <clear/> and is passed
        //    as --configfile, so neither the repo's local-packages/ nor a
        //    user-level source can satisfy the restore behind the lane's back.
        laneDir.CreateDirectory();
        var nugetConfig = laneDir / "NuGet.config";
        File.WriteAllText(nugetConfig, ConsumptionNuGetConfig(feed));
        // The lane props also drops the NativeReference items that resolve into
        // the checkout, so the frameworks the app links come from the package
        // rather than from whatever the preceding source build left in the tree.
        // The scratch root is exempt: the lane's own obj/ is where the iOS SDK
        // unpacks a binding package's resources.zip, and those ARE the package's
        // frameworks.
        var laneProps = WriteLaneProps(
            laneDir, swaps, withEvidence: true,
            workingTreeRoot: RootDirectory, laneScratchRoot: scratch);

        var sharedProps = new[]
        {
            $"-p:SwiftBindingsLaneProps={laneProps}",
            $"-p:RestorePackagesPath={packagesDir}",
            $"-p:BaseIntermediateOutputPath={objDir}{Path.DirectorySeparatorChar}",
            $"-p:BaseOutputPath={binDir}{Path.DirectorySeparatorChar}",
        };

        // 5. Restore from the staged feed only, into the isolated cache.
        var restoreArgs = new List<string> { "restore", (string)csproj, "--configfile", (string)nugetConfig };
        restoreArgs.AddRange(sharedProps);
        var restoreExit = RunDotnet(restoreArgs, onLine);
        if (restoreExit != 0)
            throw new InvalidOperationException(
                $"{laneLabel}: package-mode restore failed with exit {restoreExit}. " +
                "A consumer cannot install this package as published.");

        // 6. Build the app in package mode.
        BuildTestAppSimulator(library, testDir, "ios", onLine, noRestore: true, extraArgs: sharedProps);

        // 7. Static identity — the restore graph, the archive that fed it, and
        //    the binaries the app actually ships.
        AssertPackageModeRestore(objDir / "project.assets.json", swaps, packagesDir, laneLabel);
        AssertStagedPackagesCameFromTheFeed(feed, packagesDir, stagedPackages, laneLabel, Info);
        var resolvedPackages = ResolvedPackages(File.ReadAllText(objDir / "project.assets.json"));

        var rid = ResolveRid("iossimulator-arm64");
        var appPath = ResolveAppPath(scratch, "Debug", "ios", rid, appName);
        if (!Directory.Exists(appPath))
            throw new InvalidOperationException(
                $"{laneLabel}: package-mode build produced no app bundle at {appPath}.");
        AssertDeployedAssembliesCameFromPackages(appPath, swaps, packagesDir, laneLabel, Info);
        AssertEmbeddedFrameworksCameFromPackages(
            appPath, packagesDir, resolvedPackages, stagedPackages, laneLabel, Info);

        // 8. Runtime — install, launch, and require the suite to pass. Given no
        //    --device-udid, ValidateSimAt resolves a concrete iOS-family
        //    simulator and logs which one, so the lane inherits that resolution
        //    rather than repeating it (a release must never install through
        //    simctl's family-blind `booted` alias).
        //    The run budget is the library's own floor when it has one, so a suite whose
        //    bounded awaits exceed the lane's --timeout reports a verdict rather than being
        //    killed mid-run.
        var result = ValidateSimAt(appPath, appName, bundleId,
            ResolveRunBudgetSeconds(library, timeoutSeconds), simUdid, Info,
            storeKitConfiguration);
        if (result.Status != TestRunStatus.Success)
            throw new InvalidOperationException(
                $"{laneLabel}: the packaged consumer failed on the simulator — {result.Status}" +
                (result.Reason is null ? "" : $" ({result.Reason})"));

        // 9. Runtime identity — what the process itself says it loaded.
        AssertRuntimeFlavor(result.Output, expectMono: true, laneLabel);
        var identityFailures = swaps
            .Select(s => LaneEvidenceGate.JudgeAssemblyIdentity(result.Output, s.PackageId, s.Version))
            .Where(f => f is not null)
            .ToList();
        if (identityFailures.Count > 0)
            throw new InvalidOperationException(
                $"{laneLabel}: the app's own output contradicts the packages the lane staged:\n  " +
                string.Join("\n  ", identityFailures));

        Info($"=== {laneLabel}: OK — {swaps.Count} package(s) restored, linked, loaded and validated " +
             $"in {sw.Elapsed.TotalSeconds:F0}s ===");
    }

    // ── Pure helpers (covered by TestReleaseConsumption) ────────────────────

    static readonly Regex ProjectReferencePattern =
        new(@"<ProjectReference\s+Include\s*=\s*""(?<include>[^""]+)""", RegexOptions.CultureInvariant);

    /// <summary>
    /// Parse a <c>"Id=Version;Id2=Version2"</c> override list. Empty/blank
    /// yields an empty map. A malformed entry throws rather than being skipped:
    /// a typo that silently fell back to the wave version would consume the
    /// wrong package and still report success.
    /// </summary>
    internal static IReadOnlyDictionary<string, string> ParseConsumeVersionMap(string? raw)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(raw)) return map;

        foreach (var token in raw.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = token.Split('=');
            if (parts.Length != 2 || parts[0].Trim().Length == 0 || parts[1].Trim().Length == 0)
                throw new InvalidOperationException(
                    $"--consume-versions: '{token}' is not a valid Id=Version entry.");
            var id = parts[0].Trim();
            if (map.ContainsKey(id))
                throw new InvalidOperationException($"--consume-versions: '{id}' listed twice.");
            map[id] = parts[1].Trim();
        }
        return map;
    }

    /// <summary>
    /// Map a test csproj's sibling <c>ProjectReference</c> items onto the
    /// packages a consumer would reference instead. Package id is the csproj
    /// file name, which is the invariant this repo packs under
    /// (<c>SwiftBindings.Apple.TipKit.csproj</c> → <c>SwiftBindings.Apple.TipKit</c>).
    ///
    /// <para>Version defaults to the release version under test, because the
    /// common case — a single library, its own products, an Apple wave — ships
    /// every referenced package at one version. A cross-library reference at a
    /// different version (BlinkIDUX → BlinkID) is named explicitly via
    /// <paramref name="overrides"/>.</para>
    ///
    /// <para>A non-SwiftBindings ProjectReference is a hard error: it has no
    /// package to become, and silently leaving it as a project reference would
    /// make the lane a partial-consumption test wearing a full-consumption
    /// name.</para>
    /// </summary>
    internal static IReadOnlyList<(string Include, string PackageId, string Version)> ResolveConsumedPackages(
        string csprojText, string defaultVersion, IReadOnlyDictionary<string, string> overrides)
    {
        var result = new List<(string, string, string)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match m in ProjectReferencePattern.Matches(csprojText))
        {
            var include = m.Groups["include"].Value;
            var id = Path.GetFileNameWithoutExtension(include.Replace('\\', '/'));

            if (!id.StartsWith("SwiftBindings.", StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"Release consumption: the test csproj references '{include}', which is not a " +
                    "SwiftBindings package project. The lane has no package to substitute for it.");

            if (!seen.Add(id)) continue;
            result.Add((include, id, overrides.TryGetValue(id, out var v) ? v : defaultVersion));
        }

        return result;
    }

    static readonly Regex CsprojVersionPattern =
        new(@"<(?:PackageVersion|Version)>(?<v>[^<]+)</(?:PackageVersion|Version)>", RegexOptions.CultureInvariant);

    /// <summary>
    /// The version a referenced binding project pins for itself, or null when it
    /// declares none. Used only for a package that this release does NOT stage.
    /// </summary>
    internal static string? ReadPinnedVersion(AbsolutePath testDir, string include)
    {
        var path = Path.GetFullPath(Path.Combine(testDir, include.Replace('\\', '/')));
        if (!File.Exists(path)) return null;
        var m = CsprojVersionPattern.Match(File.ReadAllText(path));
        return m.Success ? m.Groups["v"].Value.Trim() : null;
    }

    /// <summary>
    /// Decide, per referenced package, whether it comes out of the staged feed
    /// or off nuget.org — and refuse the cases where that question has no honest
    /// answer.
    ///
    /// <para>A release stages one library's packages; a test app may reference a
    /// sibling library that this release does not touch (BlinkIDUX → BlinkID).
    /// Demanding the sibling from the feed would red every such release, while
    /// letting the package UNDER RELEASE fall through to nuget.org would validate
    /// the last published build and call it a green for the new one. So:</para>
    /// <list type="bullet">
    /// <item>the exact <c>id.version.nupkg</c> in the feed ⇒ consumed from the
    /// feed, and every later gate checks it;</item>
    /// <item>the id absent from the feed entirely ⇒ not part of this release:
    /// re-pointed at the version its own project pins, which a consumer restores
    /// from nuget.org (a version that does not exist there fails the restore);</item>
    /// <item>the id present at a DIFFERENT version ⇒ hard error. The release
    /// staged this package, so consuming another version of it is a mismatch
    /// between the lane and the release, not a fallback;</item>
    /// <item>an unstaged package with no version pin ⇒ hard error rather than a
    /// guess.</item>
    /// </list>
    /// <para>Finally, at least one package must come from the feed: a run in
    /// which everything resolved off nuget.org establishes nothing about the
    /// artifact being released.</para>
    ///
    /// <para>Versions are normalized on the way in, so every downstream
    /// coordinate — the feed file name, the cache path, the restore graph key —
    /// is the one NuGet itself uses. See
    /// <see cref="NormalizePackageVersion"/>.</para>
    /// </summary>
    internal static (IReadOnlyList<(string Include, string PackageId, string Version)> Swaps,
                     IReadOnlySet<string> StagedIds) PlanFeedResolution(
        IReadOnlyList<(string Include, string PackageId, string Version)> swaps,
        IReadOnlyCollection<string> feedFileNames,
        Func<string, string?> pinLookup,
        string context)
    {
        var staged = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var planned = new List<(string, string, string)>();
        var failures = new List<string>();

        foreach (var (include, id, rawVersion) in swaps)
        {
            var version = NormalizePackageVersion(rawVersion);
            if (feedFileNames.Contains($"{id}.{version}.nupkg", StringComparer.OrdinalIgnoreCase))
            {
                staged.Add(id);
                planned.Add((include, id, version));
                continue;
            }

            var otherVersions = feedFileNames
                .Where(f => f.StartsWith(id + ".", StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f, StringComparer.Ordinal)
                .ToList();
            if (otherVersions.Count > 0)
            {
                failures.Add(
                    $"{id} is staged as [{string.Join(", ", otherVersions)}] but the lane was asked for " +
                    $"{version}. Consuming a different version of a package this release stages would " +
                    "validate bytes that are not being published — pass --consume-versions to name the " +
                    "staged version, or fix the version under release.");
                continue;
            }

            var pinned = pinLookup(include);
            if (string.IsNullOrWhiteSpace(pinned))
            {
                failures.Add(
                    $"{id} is not staged by this release and its project ({include}) declares no version to " +
                    "fall back on. Add a <Version> to that project or name the version with --consume-versions.");
                continue;
            }

            planned.Add((include, id, NormalizePackageVersion(pinned!)));
        }

        // Only the packages this lane asked about are worth listing: a shared
        // local feed holds hundreds of nupkgs, and dumping all of them buries
        // the one line that says what went wrong.
        string Relevant()
        {
            var ids = swaps.Select(s => s.PackageId).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var mine = feedFileNames
                .Where(f => ids.Any(id => f.StartsWith(id + ".", StringComparison.OrdinalIgnoreCase)))
                .OrderBy(f => f, StringComparer.Ordinal)
                .ToList();
            var others = feedFileNames.Count - mine.Count;
            return mine.Count == 0
                ? $"no versions of {string.Join(", ", ids)} ({others} unrelated .nupkg present)"
                : $"{string.Join(", ", mine)} ({others} unrelated .nupkg present)";
        }

        if (failures.Count > 0)
        {
            throw new InvalidOperationException(
                $"{context} cannot satisfy this lane:\n  " + string.Join("\n  ", failures) +
                $"\n  feed contains: {Relevant()}");
        }

        if (staged.Count == 0)
        {
            var present = Relevant();
            throw new InvalidOperationException(
                $"{context} stages none of the packages this test app references " +
                $"({string.Join(", ", swaps.Select(s => $"{s.PackageId} {s.Version}"))}). " +
                "Every package would come from nuget.org, so the run would say nothing about the release.\n" +
                $"  feed contains: {present}");
        }

        return (planned, staged);
    }

    /// <summary>
    /// The lane's NuGet.config: the staged feed, plus nuget.org for the
    /// dependency closure a real consumer also pulls from there (the SDK, the
    /// runtime, the Apple supplement). <c>&lt;clear /&gt;</c> is what keeps the
    /// machine's own sources out; that nuget.org is still listed is why
    /// <see cref="AssertStagedPackagesCameFromTheFeed"/> exists.
    ///
    /// <para>The feed path is XML-escaped: a directory containing <c>&amp;</c>
    /// would otherwise produce a malformed config, and restore would fail
    /// before it could validate anything.</para>
    /// </summary>
    internal static string ConsumptionNuGetConfig(AbsolutePath feed) =>
        $"""
        <?xml version="1.0" encoding="utf-8"?>
        <!-- Generated by the Nuke harness (build/Build.ReleaseConsumption.cs). Do not edit. -->
        <configuration>
          <packageSources>
            <clear />
            <add key="staged" value="{Attr((string)feed)}" />
            <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
          </packageSources>
        </configuration>

        """;

    /// <summary>
    /// Assert that each package under release was restored from the staged
    /// feed, by comparing the <c>.nupkg</c> in the lane's cache with the
    /// <c>.nupkg</c> the feed holds.
    ///
    /// <para>The restore can reach nuget.org — it has to, for the dependency
    /// closure — and NuGet selects on id and version alone. So an id+version
    /// that is already published satisfies the restore from the published
    /// bytes, and every downstream check (assets graph, package type, cache
    /// isolation, managed MVID, runtime version) then passes against a package
    /// this release never produced. Comparing archives is what separates
    /// "resolved the right coordinates" from "consumed the artifact under
    /// release". If the two are byte-identical the distinction is empty and the
    /// lane's claim holds either way.</para>
    /// </summary>
    internal static void AssertStagedPackagesCameFromTheFeed(
        AbsolutePath feed,
        AbsolutePath packagesDir,
        IReadOnlyCollection<(string PackageId, string Version)> stagedPackages,
        string laneLabel,
        Action<string> info)
    {
        var failures = new List<string>();

        foreach (var (id, version) in stagedPackages)
        {
            var fileName = $"{id}.{version}.nupkg";

            // NuGet's global-packages layout keeps the archive it extracted
            // beside the extract, under lower-cased id/version.
            var cached = packagesDir / id.ToLowerInvariant() / version.ToLowerInvariant()
                         / $"{id.ToLowerInvariant()}.{version.ToLowerInvariant()}.nupkg";
            if (!File.Exists(cached))
            {
                failures.Add($"{id} {version}: the lane's cache holds no archive at {cached}, so there is nothing to " +
                             "compare against the staged feed.");
                continue;
            }

            var staged = Directory.Exists(feed)
                ? Directory.EnumerateFiles(feed, "*.nupkg")
                    .FirstOrDefault(f => string.Equals(Path.GetFileName(f), fileName, StringComparison.OrdinalIgnoreCase))
                : null;
            if (staged is null)
            {
                failures.Add($"{id} {version}: the staged feed {feed} holds no {fileName}, yet the lane treated this " +
                             "package as staged for the release.");
                continue;
            }

            var cachedHash = Sha256OfFile(cached);
            var stagedHash = Sha256OfFile(staged);
            if (!string.Equals(cachedHash, stagedHash, StringComparison.Ordinal))
                failures.Add(
                    $"{id} {version}: the archive the lane restored ({cachedHash[..16]}…) is not the one the release " +
                    $"staged ({stagedHash[..16]}…). NuGet satisfied the reference from somewhere else — nuget.org " +
                    "already publishes this id and version — so this run validated bytes the release did not build.");
            else
                info($"  {id} {version} was restored from the staged archive (sha256 {cachedHash[..16]}…)");
        }

        if (failures.Count > 0)
            throw new InvalidOperationException(
                $"{laneLabel}: the packages under release are not the packages the lane consumed:\n  " +
                string.Join("\n  ", failures));
    }

    /// <summary>
    /// The version string NuGet will actually use as a package coordinate.
    ///
    /// <para>A release branch may carry SemVer build metadata
    /// (<c>13.0.7+ci.1</c>), and the release workflow passes that version
    /// straight through to this lane. NuGet drops build metadata when it
    /// normalizes a version, so pack writes <c>Id.13.0.7.nupkg</c>, the cache
    /// extracts to <c>13.0.7/</c> and the restore graph keys on
    /// <c>Id/13.0.7</c>. A lane still holding the raw string would look for
    /// coordinates that do not exist and red a correct release.</para>
    ///
    /// <para>Build metadata is the only divergence the release branch grammar
    /// admits — versions here are <c>X.Y.Z</c> with an optional prerelease
    /// label, which NuGet leaves alone — so that is what this handles.</para>
    /// </summary>
    internal static string NormalizePackageVersion(string version)
    {
        var trimmed = version.Trim();
        var plus = trimmed.IndexOf('+', StringComparison.Ordinal);
        return plus < 0 ? trimmed : trimmed[..plus];
    }

    /// <summary>
    /// The (id, version) pairs the restore actually resolved, read from the
    /// assets graph.
    ///
    /// <para>This is the universe a framework in the app bundle is allowed to
    /// have come from. The lane's package cache is deliberately kept between
    /// runs, so it also holds versions this restore did not choose; attributing
    /// an embedded binary to one of those would accept native code the app was
    /// never supposed to link.</para>
    /// </summary>
    internal static IReadOnlyList<(string PackageId, string Version)> ResolvedPackages(string assetsJson)
    {
        using var doc = JsonDocument.Parse(assetsJson);
        if (!doc.RootElement.TryGetProperty("libraries", out var libraries))
            return Array.Empty<(string, string)>();

        return libraries.EnumerateObject()
            .Where(p => p.Value.TryGetProperty("type", out var t)
                        && string.Equals(t.GetString(), "package", StringComparison.OrdinalIgnoreCase))
            .Select(p => p.Name.Split('/'))
            .Where(parts => parts.Length == 2)
            .Select(parts => (parts[0], parts[1]))
            .ToList();
    }

    static string Sha256OfFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
    }

    /// <summary>
    /// Delete the isolated cache's extract of each (id, version) under test.
    /// The rest of the cache is left alone — re-downloading the whole .NET/iOS
    /// dependency closure per run would cost minutes and prove nothing.
    /// </summary>
    internal static void EvictFromPackagesDir(
        AbsolutePath packagesDir, IEnumerable<(string Id, string Version)> entries, Action<string> info)
    {
        foreach (var (id, version) in entries)
        {
            var dir = packagesDir / id.ToLowerInvariant() / version.ToLowerInvariant();
            if (!Directory.Exists(dir)) continue;
            try
            {
                Directory.Delete(dir, recursive: true);
                info($"  evicted stale extract: {dir}");
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Release consumption: could not evict {dir} ({ex.Message}). Continuing would let the " +
                    $"previous run's extract shadow the freshly staged {id} {version}.", ex);
            }
        }
    }

    /// <summary>
    /// Assert the restore graph resolved every package under test AS A PACKAGE
    /// at the staged version, out of the lane's own cache. This is the check
    /// that cannot be satisfied by a ProjectReference build: MSBuild records a
    /// project reference as <c>"type": "project"</c>, and a package restored
    /// from anywhere else would not list the lane's package folder.
    /// </summary>
    internal void AssertPackageModeRestore(
        AbsolutePath assetsPath,
        IReadOnlyList<(string Include, string PackageId, string Version)> swaps,
        AbsolutePath packagesDir,
        string laneLabel)
    {
        if (!File.Exists(assetsPath))
            throw new InvalidOperationException(
                $"{laneLabel}: no project.assets.json at {assetsPath} — the isolated restore did not happen.");

        var failures = AssetsFailures(File.ReadAllText(assetsPath), swaps, packagesDir);
        if (failures.Count > 0)
            throw new InvalidOperationException(
                $"{laneLabel}: the restore graph does not show package consumption:\n  " +
                string.Join("\n  ", failures));

        Log.Information("{Lane}: restore graph confirms {Count} package reference(s) from {Dir}",
            laneLabel, swaps.Count, packagesDir);
    }

    /// <summary>Pure core of <see cref="AssertPackageModeRestore"/>.</summary>
    internal static IReadOnlyList<string> AssetsFailures(
        string assetsJson,
        IReadOnlyList<(string Include, string PackageId, string Version)> swaps,
        AbsolutePath packagesDir)
    {
        var failures = new List<string>();
        using var doc = JsonDocument.Parse(assetsJson);

        if (!doc.RootElement.TryGetProperty("libraries", out var libraries))
            return new[] { "project.assets.json has no libraries section." };

        foreach (var (_, id, version) in swaps)
        {
            var key = $"{id}/{version}";
            if (!libraries.TryGetProperty(key, out var entry))
            {
                var projectEntry = libraries.EnumerateObject()
                    .FirstOrDefault(p => p.Name.StartsWith(id + "/", StringComparison.OrdinalIgnoreCase));
                failures.Add(projectEntry.Value.ValueKind == JsonValueKind.Object
                    ? $"{id} resolved as '{projectEntry.Name}' (type " +
                      $"{(projectEntry.Value.TryGetProperty("type", out var t) ? t.GetString() : "?")}), not as package {version}."
                    : $"{key} is absent from the restore graph.");
                continue;
            }

            var type = entry.TryGetProperty("type", out var typeEl) ? typeEl.GetString() : null;
            if (!string.Equals(type, "package", StringComparison.OrdinalIgnoreCase))
                failures.Add($"{key} resolved with type '{type}' — the lane requires a package reference.");
        }

        if (doc.RootElement.TryGetProperty("packageFolders", out var folders))
        {
            var normalized = ((string)packagesDir).TrimEnd(Path.DirectorySeparatorChar);
            var listed = folders.EnumerateObject()
                .Select(p => p.Name.TrimEnd(Path.DirectorySeparatorChar))
                .ToList();
            if (!listed.Any(f => string.Equals(f, normalized, StringComparison.Ordinal)))
                failures.Add(
                    $"the restore used package folder(s) [{string.Join(", ", listed)}] rather than the lane's " +
                    $"isolated cache {normalized} — the run may be reading a machine-wide cached extract.");
        }
        else
        {
            failures.Add("project.assets.json has no packageFolders section — cannot confirm cache isolation.");
        }

        return failures;
    }

    /// <summary>
    /// Assert the managed assembly the app actually ships is the one inside the
    /// package, by module version id. The MVID identifies a specific
    /// compilation, so this separates "restored the package" from "recompiled
    /// the same source" in a way a version-string comparison cannot.
    /// </summary>
    internal static void AssertDeployedAssembliesCameFromPackages(
        AbsolutePath appPath,
        IReadOnlyList<(string Include, string PackageId, string Version)> swaps,
        AbsolutePath packagesDir,
        string laneLabel,
        Action<string> info)
    {
        var failures = new List<string>();

        foreach (var (_, id, version) in swaps)
        {
            var deployed = Directory
                .EnumerateFiles(appPath, $"{id}.dll", SearchOption.AllDirectories)
                .OrderBy(p => p, StringComparer.Ordinal)
                .FirstOrDefault();
            if (deployed is null)
            {
                failures.Add($"{id}.dll is not present in the built app bundle at {appPath}.");
                continue;
            }

            var packageRoot = packagesDir / id.ToLowerInvariant() / version.ToLowerInvariant();
            if (!Directory.Exists(packageRoot))
            {
                failures.Add($"no extracted package at {packageRoot} — the lane's cache does not hold {id} {version}.");
                continue;
            }

            var candidates = Directory
                .EnumerateFiles(packageRoot / "lib", $"{id}.dll", SearchOption.AllDirectories)
                .ToList();
            if (candidates.Count == 0)
            {
                failures.Add($"{id} {version} ships no lib/**/{id}.dll — nothing to compare the deployed assembly to.");
                continue;
            }

            var deployedMvid = ReadMvid(deployed);
            var packageMvids = candidates.Select(ReadMvid).ToList();
            if (deployedMvid is null)
            {
                failures.Add($"could not read a module version id from the deployed {deployed}.");
                continue;
            }

            if (!packageMvids.Any(m => m == deployedMvid))
            {
                failures.Add(
                    $"the deployed {id}.dll (mvid {deployedMvid}) matches none of the {candidates.Count} " +
                    $"assemblies inside {id} {version} — the app is shipping a different build of the binding " +
                    "than the package under release.");
                continue;
            }

            info($"  {id}.dll in the app bundle is the package's own build (mvid {deployedMvid})");
        }

        if (failures.Count > 0)
            throw new InvalidOperationException(
                $"{laneLabel}: the built app does not carry the packaged assemblies:\n  " +
                string.Join("\n  ", failures));
    }

    /// <summary>Module version id of a managed assembly, or null if unreadable.</summary>
    internal static Guid? ReadMvid(string assemblyPath)
    {
        try
        {
            using var stream = File.OpenRead(assemblyPath);
            using var peReader = new PEReader(stream);
            if (!peReader.HasMetadata) return null;
            var reader = peReader.GetMetadataReader();
            return reader.GetGuid(reader.GetModuleDefinition().Mvid);
        }
        catch
        {
            return null;
        }
    }
}
