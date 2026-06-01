using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Nuke.Common;
using Nuke.Common.IO;
using Serilog;
using SwiftBindings.Build.Helpers;
using SwiftBindings.Build.Models;

partial class Build
{
    [Parameter("RegressionValidate: subset of platforms to run (comma list, default: all). Values: ios-sim, ios-device, macos, maccatalyst, tvos")]
    readonly string? Platforms;

    [Parameter("RegressionValidate: max parallel cells (default 4, capped at 16). NativeAOT publish + concurrent app-bundle builds can push a 32 GB box into swap before CPU saturates — bump only after observing actual memory headroom.")]
    readonly int RegressionJobs;

    [Parameter("RegressionValidate: run cells sequentially (equivalent to --regression-jobs 1).")]
    readonly bool RegressionSerial;

    /// <summary>
    /// Pre-release regression: build + validate every in-scope package across
    /// every TFM its test csproj declares. Cells run in parallel under a
    /// <c>--regression-jobs</c>-sized semaphore; the physical-iPhone lane is
    /// additionally serialized via a dedicated single-slot. iOS sim runs
    /// MonoJIT against an N-sized booted simulator fleet, iOS device runs
    /// NativeAOT publish. macOS validates via direct binary run. MacCatalyst
    /// and tvOS build only — no validator yet, recorded as <c>BUILD-ONLY</c>.
    /// </summary>
    Target RegressionValidate => _ => _
        .Description("Build + validate every package across every declared TFM (--version X.Y.Z --apple-version NN.N.N [--filter Foo] [--platforms ios-sim,macos] [--regression-jobs N] [--regression-serial])")
        .Requires(() => Version)
        .Requires(() => AppleVersion)
        .Executes(async () =>
        {
            var version = Version!;
            var appleVersion = AppleVersion!;
            var requestedPlatforms = ParsePlatformsParam(Platforms);
            var packages = ResolvePackages(Filter);

            Log.Information("=== regression-validate SDK={Version} Apple={AppleVersion} ===", version, appleVersion);
            Log.Information("Packages: {Count} ({Names})", packages.Count, string.Join(", ", packages));
            Log.Information("Platforms: {Platforms}", string.Join(", ", requestedPlatforms.Select(PlatformLabel)));

            var cells = EnumerateCells(packages, requestedPlatforms);
            Log.Information("Cells to run: {Count}", cells.Count);

            Preflight(version, appleVersion, cells);

            // --- Resolve worker count ---
            // Default 4 is conservative vs. `nuke validate`'s cores-2: this target
            // also drives CoreSimulator install/launch + NativeAOT publish on top
            // of the compile/generate work, and NativeAOT publish is the RAM-heaviest
            // path in the matrix. Bump only after observing actual memory headroom.
            int maxJobs;
            if (RegressionSerial) maxJobs = 1;
            else if (RegressionJobs > 0) maxJobs = RegressionJobs;
            else maxJobs = 4;
            maxJobs = Math.Clamp(maxJobs, 1, 16);
            Log.Information("Workers: {Jobs} (serial={Serial})", maxJobs, RegressionSerial);

            // --- Pre-restore phase ---
            // Run one `dotnet restore` per in-scope test csproj before any cell
            // dispatches its parallel `dotnet build`/`publish` (which now pass
            // --no-restore). Parallel restores on the same csproj race on shared
            // obj/project.assets.json / obj/*.nuget.cache writes even though the
            // SDK splits _SwiftBindingIntermediateDir per TFM.
            PreRestoreCells(cells);

            // --- Inter-library obj/ producer deps ---
            // Some test csprojs reference a sibling library's intermediate
            // obj/.../*.xcframework directly (BlinkIDUX/tests → BlinkID/obj/…).
            // Two parallel `dotnet build` processes that transitively build the
            // producer write to the same obj/ path and race; the consumer can
            // see a partial framework and fail MT158. Gate consumer cells on
            // their producer cell of the same platform.
            var libObjDeps = InferInterLibraryObjDeps(cells);

            // Cache resolved device UDID once so each device cell doesn't re-detect.
            string? deviceUdid = cells.Any(c => c.Platform == CellPlatform.IosDevice)
                ? (DeviceUdid ?? DevicectlClient.AutoDetectDevice())
                : null;

            // --- Boot sim fleet ---
            // Cap fleet size at min(maxJobs, simCellCount). With device + sim
            // lanes both draining the same semaphore, max simultaneous sim
            // cells is min(maxJobs, totalSimCells); over-provisioning sims
            // just leaves expensive CoreSimulator boots idle.
            var simCellCount = cells.Count(c => c.Platform == CellPlatform.IosSim);
            SimulatorFleet? fleet = null;
            IReadOnlyList<SimulatorFleet.FleetMember> fleetMembers = Array.Empty<SimulatorFleet.FleetMember>();
            ConcurrentQueue<string>? simUdidQueue = null;
            if (simCellCount > 0)
            {
                fleet = new SimulatorFleet();
                var fleetSize = Math.Min(maxJobs, simCellCount);
                fleetMembers = fleet.EnsureFleet(fleetSize);
                simUdidQueue = new ConcurrentQueue<string>(fleetMembers.Select(m => m.Udid));
            }

            // --- Longest-first scheduling ---
            // Read the prior `regression-validate-{Version}.json` if present and
            // sum per-cell durations into a (library, platform) key. Schedule the
            // heaviest cells first so the tail isn't a 75s Stripe cell waiting
            // behind a fast cell that got picked first. Falls back to manifest
            // order for cells with no prior data.
            var sortedCells = SortLongestFirst(cells, version);

            // --- Per-cell tee directory ---
            var teeDir = (AbsolutePath)Path.Combine(Path.GetTempPath(), $"regression-cells-{version}");
            Directory.CreateDirectory(teeDir);
            // Wipe stale cell logs from a prior run so users following file paths
            // don't read pre-fix output thinking it's the current run.
            foreach (var stale in Directory.EnumerateFiles(teeDir, "*.log"))
                try { File.Delete(stale); } catch { /* best effort */ }

            // --- Concurrency primitives ---
            var outcomes = new ConcurrentDictionary<Cell, CellOutcome>();
            var pipelineStart = DateTime.UtcNow;
            var semaphore = new SemaphoreSlim(maxJobs);
            // Single physical iPhone — install + launch must be exclusive. Drawn
            // from the same pool as general cells: running general=N + device=1
            // would put N+1 cells under RAM pressure simultaneously when the
            // NativeAOT publish is the heaviest path.
            var deviceLock = new SemaphoreSlim(1, 1);
            // One TCS per cell so dependents can await their producer of the
            // same platform without ordering assumptions in sortedCells. We
            // TrySetResult unconditionally in the finally so a producer that
            // fails (or never reaches its outcome) doesn't deadlock dependents.
            var cellComplete = sortedCells.ToDictionary(c => c, _ => new TaskCompletionSource<CellOutcome>());

            try
            {
                await Task.WhenAll(sortedCells.Select(async cell =>
                {
                    // Wait for inter-library producer cells of the same platform.
                    if (libObjDeps.TryGetValue(cell.Library, out var depLibs))
                    {
                        var depTasks = depLibs
                            .Select(d => new Cell(d, cell.Platform))
                            .Where(cellComplete.ContainsKey)
                            .Select(d => cellComplete[d].Task)
                            .ToArray();
                        if (depTasks.Length > 0)
                            await Task.WhenAll(depTasks);
                    }

                    // Device cell waits for its own library's sim cell so that
                    // the device-side `dotnet publish` (which does an implicit
                    // restore that overwrites the sim-RID assets.json — see
                    // PreRestoreCells / BuildTestAppNativeAot) cannot race a
                    // concurrent sim cell that's still mid-build under
                    // --no-restore. A library without a paired sim cell just
                    // skips this wait.
                    if (cell.Platform == CellPlatform.IosDevice)
                    {
                        var simSibling = new Cell(cell.Library, CellPlatform.IosSim);
                        if (cellComplete.TryGetValue(simSibling, out var simTcs))
                            await simTcs.Task;
                    }

                    // Acquire ORDER: device-lock FIRST, THEN global semaphore.
                    // Reverse ordering would let queued device cells consume
                    // global slots while blocked on the single-device lock —
                    // 4 device cells sorted first under --regression-jobs 4
                    // would fill the pool, one would run, three would block
                    // holding 3/4 slots, and sim/mac/tv cells would starve.
                    // Putting device-lock first means a waiting device cell
                    // only blocks OTHER device cells (correct serialization);
                    // sim/mac/tv cells still drain the global pool freely.
                    // No deadlock risk: only device cells take device-lock,
                    // so there's no reverse-ordering acquisition path.
                    bool tookDeviceLock = false;
                    bool tookSemaphore = false;
                    string? cellSimUdid = null;
                    CellOutcome? cellOutcomeForTcs = null;
                    try
                    {
                        if (cell.Platform == CellPlatform.IosDevice)
                        {
                            await deviceLock.WaitAsync();
                            tookDeviceLock = true;
                        }
                        await semaphore.WaitAsync();
                        tookSemaphore = true;
                        if (cell.Platform == CellPlatform.IosSim)
                        {
                            if (simUdidQueue is null || !simUdidQueue.TryDequeue(out cellSimUdid))
                                throw new InvalidOperationException(
                                    "Sim fleet exhausted — semaphore allowed more sim cells than the fleet has UDIDs.");
                        }

                        var label = $"{cell.Library}-{PlatformLabel(cell.Platform)}";
                        var logPath = teeDir / $"{label}.log";
                        var cellSw = Stopwatch.StartNew();

                        Log.Information("→ START {Label}{Udid}", label,
                            cellSimUdid is null ? "" : $" (sim={cellSimUdid[..8]})");

                        CellOutcome outcome;
                        using (var writer = new StreamWriter(logPath, append: false) { AutoFlush = true })
                        {
                            writer.WriteLine($"=== [{label}] starting at {DateTime.UtcNow:O} ===");
                            Action<string> sink = line => writer.WriteLine(line);
                            try
                            {
                                outcome = await Task.Run(() => RunCell(cell, cellSimUdid, deviceUdid, cellSw, sink));
                            }
                            catch (Exception ex)
                            {
                                writer.WriteLine($"EXCEPTION: {ex}");
                                outcome = new CellOutcome(cell, CellResult.Error, ex.Message, cellSw.Elapsed);
                            }
                            writer.WriteLine($"=== [{label}] result {outcome.Result} in {outcome.Duration.TotalSeconds:F1}s ===");
                        }

                        outcomes[cell] = outcome;
                        cellOutcomeForTcs = outcome;
                        Log.Information("← {Label} → {Result} ({Duration:F1}s) log={Log}{Reason}",
                            label, outcome.Result, outcome.Duration.TotalSeconds, logPath,
                            outcome.Reason is null ? "" : $" — {outcome.Reason}");
                    }
                    finally
                    {
                        // Reverse of acquisition order: UDID → semaphore → device-lock.
                        // tookSemaphore/tookDeviceLock guard against partial-acquire
                        // exceptions (e.g. if WaitAsync ever throws before returning).
                        if (cellSimUdid is not null) simUdidQueue!.Enqueue(cellSimUdid);
                        if (tookSemaphore) semaphore.Release();
                        if (tookDeviceLock) deviceLock.Release();
                        // Always complete the TCS so dependents don't hang on a
                        // producer that threw before recording an outcome.
                        cellComplete[cell].TrySetResult(
                            cellOutcomeForTcs ?? new CellOutcome(cell, CellResult.Error, "no outcome", TimeSpan.Zero));
                    }
                }));
            }
            finally
            {
                // Cleanup fleet members WE created (don't evict the developer's
                // pre-existing iPhone sim from their `simctl list`).
                if (fleet is not null)
                {
                    foreach (var m in fleetMembers.Where(m => m.WasCreated))
                    {
                        fleet.ShutdownOne(m.Udid);
                        fleet.DeleteOne(m.Udid);
                    }
                }
            }

            var totalDuration = DateTime.UtcNow - pipelineStart;

            // Replay per-cell summaries in stable manifest order so the main log
            // ends with a coherent read even after parallel interleave above.
            Log.Information("");
            Log.Information("─── Per-cell results (manifest order) ───");
            var outcomeList = new List<CellOutcome>(cells.Count);
            foreach (var cell in cells)
            {
                if (outcomes.TryGetValue(cell, out var outcome))
                {
                    outcomeList.Add(outcome);
                    Log.Information("  [{Lib} {Plat}] → {Result} ({Duration:F1}s){Reason}",
                        cell.Library, PlatformLabel(cell.Platform), outcome.Result, outcome.Duration.TotalSeconds,
                        outcome.Reason is null ? "" : $" — {outcome.Reason}");
                }
                else
                {
                    var stub = new CellOutcome(cell, CellResult.Error, "no outcome recorded", TimeSpan.Zero);
                    outcomeList.Add(stub);
                    Log.Error("  [{Lib} {Plat}] → no outcome recorded",
                        cell.Library, PlatformLabel(cell.Platform));
                }
            }

            PrintMatrix(packages, requestedPlatforms, outcomeList, totalDuration);
            WriteJsonArtifact(version, requestedPlatforms, outcomeList, totalDuration);

            var failures = outcomeList.Count(o => o.Result is CellResult.Fail or CellResult.Timeout or CellResult.Crashed or CellResult.Error);
            if (failures > 0)
                throw new InvalidOperationException($"regression-validate: {failures} cell(s) failed");
        });

    [Parameter("RegressionValidate: single package filter (parity with --library)")]
    readonly string? Filter;

    enum CellPlatform { IosSim, IosDevice, MacOs, MacCatalyst, TvOs }

    enum CellResult { Pass, Fail, BuildOnly, Skip, Timeout, Crashed, Error }

    sealed record Cell(string Library, CellPlatform Platform);

    sealed record CellOutcome(
        Cell Cell,
        CellResult Result,
        string? Reason,
        TimeSpan Duration);

    static readonly CellPlatform[] AllPlatforms =
    {
        CellPlatform.IosSim,
        CellPlatform.IosDevice,
        CellPlatform.MacOs,
        CellPlatform.MacCatalyst,
        CellPlatform.TvOs,
    };

    static string PlatformLabel(CellPlatform p) => p switch
    {
        CellPlatform.IosSim => "ios-sim",
        CellPlatform.IosDevice => "ios-device",
        CellPlatform.MacOs => "macos",
        CellPlatform.MacCatalyst => "maccatalyst",
        CellPlatform.TvOs => "tvos",
        _ => p.ToString(),
    };

    static string PlatformTfm(CellPlatform p) => p switch
    {
        CellPlatform.IosSim or CellPlatform.IosDevice => "net10.0-ios",
        CellPlatform.MacOs => "net10.0-macos",
        CellPlatform.MacCatalyst => "net10.0-maccatalyst",
        CellPlatform.TvOs => "net10.0-tvos",
        _ => throw new ArgumentOutOfRangeException(nameof(p)),
    };

    static IReadOnlyList<CellPlatform> ParsePlatformsParam(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return AllPlatforms;
        var result = new List<CellPlatform>();
        foreach (var token in raw.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var t = token.Trim().ToLowerInvariant();
            CellPlatform p = t switch
            {
                "ios-sim" or "ios" or "iossim" => CellPlatform.IosSim,
                "ios-device" or "device" or "iosdevice" => CellPlatform.IosDevice,
                "macos" or "mac" => CellPlatform.MacOs,
                "maccatalyst" or "catalyst" => CellPlatform.MacCatalyst,
                "tvos" => CellPlatform.TvOs,
                _ => throw new InvalidOperationException(
                    $"Unknown platform '{token}'. Valid: ios-sim, ios-device, macos, maccatalyst, tvos"),
            };
            if (!result.Contains(p)) result.Add(p);
        }
        return result;
    }

    IReadOnlyList<string> ResolvePackages(string? filter)
    {
        if (!string.IsNullOrWhiteSpace(filter))
            return new[] { filter };

        var packages = new List<string>();
        if (Directory.Exists(LibrariesDir))
            foreach (var d in Directory.EnumerateDirectories(LibrariesDir))
                if (Directory.Exists(Path.Combine(d, "tests")))
                    packages.Add(Path.GetFileName(d)!);
        if (Directory.Exists(AppleFrameworksDir))
            foreach (var d in Directory.EnumerateDirectories(AppleFrameworksDir))
                if (Directory.Exists(Path.Combine(d, "tests")))
                    packages.Add(Path.GetFileName(d)!);

        packages.Sort(StringComparer.Ordinal);
        return packages;
    }

    IReadOnlyList<Cell> EnumerateCells(
        IReadOnlyList<string> packages,
        IReadOnlyList<CellPlatform> platforms)
    {
        var cells = new List<Cell>();
        foreach (var lib in packages)
        {
            var testDir = TestDir(lib);
            if (!Directory.Exists(testDir))
            {
                Log.Warning("Skipping {Library}: no tests/ directory", lib);
                continue;
            }
            var declared = ParseTestProjectTfms(testDir);
            foreach (var platform in platforms)
            {
                var tfm = PlatformTfm(platform);
                if (declared.Contains(tfm))
                    cells.Add(new Cell(lib, platform));
            }
        }
        return cells;
    }

    void Preflight(string version, string appleVersion, IReadOnlyList<Cell> cells)
    {
        Log.Information("--- pre-flight ---");

        // Run every read-only validation FIRST so a typo or missing
        // precondition (e.g. wrong version, no booted sim, missing codesign
        // config) fails fast without dirtying the working tree. The csproj
        // stamp + obj wipe at the end is the only side-effecting step, and
        // BumpSdkVersionInternal writes 34 csprojs and rm -rfs sibling obj/
        // directories — running it before validating the nupkg meant a bad
        // --version argument left the repo dirty before pre-flight failed.

        // 1. SDK nupkg present
        var nupkg = LocalPackagesDir / $"SwiftBindings.Sdk.{version}.nupkg";
        if (!File.Exists(nupkg))
            throw new InvalidOperationException(
                $"Pre-flight: missing SDK nupkg at {nupkg}\n" +
                $"Drop SwiftBindings.Sdk.{version}.nupkg into local-packages/ before invoking.");
        Log.Information("  SDK nupkg: {Path}", nupkg);

        var needsSim = cells.Any(c => c.Platform == CellPlatform.IosSim);
        var needsDevice = cells.Any(c => c.Platform == CellPlatform.IosDevice);

        // 2. Booted simulator if any sim cells
        if (needsSim)
        {
            var booted = ListBootedSims();
            if (booted.Count == 0)
                throw new InvalidOperationException(
                    "Pre-flight: no booted iOS simulator. Run `dotnet nuke BootSim` first.");
            Log.Information("  Booted simulator(s): {Count}", booted.Count);
        }

        // 3. Connected device if any device cells
        if (needsDevice)
        {
            var udid = DeviceUdid ?? DevicectlClient.AutoDetectDevice();
            if (string.IsNullOrEmpty(udid))
                throw new InvalidOperationException(
                    "Pre-flight: no connected iPhone. Connect a device or pass --device-udid X.");
            Log.Information("  Device UDID: {Udid}", udid);

            // 4. Codesign config (env vars OR Directory.Build.tests.props.local)
            var hasEnv = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CODESIGN_IDENTITY"))
                && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PROVISIONING_PROFILE"))
                && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TEAM_ID"));
            var hasLocal = File.Exists(RootDirectory / "Directory.Build.tests.props.local");
            if (!hasEnv && !hasLocal)
                throw new InvalidOperationException(
                    "Pre-flight: NativeAOT device builds need codesign config.\n" +
                    "  Either set CODESIGN_IDENTITY + PROVISIONING_PROFILE + TEAM_ID,\n" +
                    "  or create Directory.Build.tests.props.local at the repo root.");
            Log.Information("  Codesign: {Source}", hasEnv ? "env vars" : "Directory.Build.tests.props.local");
        }

        // 5. (Side-effecting) Stamp every package csproj to the target SDK
        //    version. Without this, csprojs may still pin the previous SDK
        //    and silently resolve the OLD package from cache or nuget.org —
        //    the matrix then validates the wrong version.
        BumpSdkVersionInternal(version);

        // 6. (Side-effecting) Stamp every apple-framework csproj's own
        //    <Version> to the Apple train (e.g. 26.2.3). The Apple
        //    supplement is versioned independently of the SDK lane and
        //    isn't covered by step 5. The package version doesn't gate
        //    resolution during the matrix itself, but skipping it leaves
        //    the repo with stale apple-framework versions after a
        //    successful run — exactly the cleanup workflow this pre-flight
        //    is meant to eliminate.
        //
        //    BumpAppleVersionInternal ALSO rewrites cross-framework
        //    <PackageReference Include="SwiftBindings.Apple.*"> pins (e.g.
        //    RealityKit→RealityFoundation, MatterSupport→Matter) to the same
        //    Apple train, so a consumer regenerated by the current generator
        //    doesn't compile against an older published sibling whose proxy
        //    ABI lacks newer ctor arguments.
        BumpAppleVersionInternal(appleVersion);

        // 7. Coherence guard. After the bump, no cross-framework
        //    SwiftBindings.Apple.* PackageReference should lag the Apple
        //    train. A surviving stale pin means the bump regex missed a
        //    malformed reference — fail fast rather than validate a mixed
        //    ABI/API shape where the consumer calls a constructor the
        //    referenced package doesn't expose.
        AssertCrossFrameworkCoherence(appleVersion);

        // 8. (Side-effecting) Pack each cross-framework dependency at the
        //    Apple train into local-packages/ so dependents restore the
        //    coherent version. The pre-release Apple supplement isn't on
        //    nuget.org yet, and the consumer's bumped pin (step 6) now
        //    points at it — without a local nupkg the restore would fall
        //    back to an older published sibling (the exact skew this whole
        //    step exists to prevent).
        PackCrossFrameworkDependencies(appleVersion);

        Log.Information("--- pre-flight OK ---");
    }

    /// <summary>
    /// Scan every SDK-importing apple-framework csproj for cross-framework
    /// <c>SwiftBindings.Apple.*</c> package references. Returns one entry per
    /// reference: the consumer csproj path, the dependency framework name
    /// (the part after <c>SwiftBindings.Apple.</c>), and the pinned version.
    ///
    /// Uses the same <see cref="AppleCrossRefTagPattern"/> /
    /// <see cref="AppleCrossRefIncludePattern"/> / <see cref="AppleCrossRefVersionPattern"/>
    /// that <see cref="BumpAppleVersionInternal"/> rewrites with — so packing sees
    /// exactly the pins the bump touches. Mirrors the bump's enumeration + SDK
    /// filter too (generated binding csprojs under obj/ don't exist at pre-flight —
    /// BumpSdkVersionInternal wiped obj/ in step 5). A tag in child-element
    /// <c>&lt;Version&gt;</c> form carries no version attribute and is skipped here
    /// because this method only feeds packing, which needs a concrete version. That
    /// skip is SAFE only because <see cref="AssertCrossFrameworkCoherence"/> runs
    /// first and hard-fails on any such unparseable form — so a child-element pin
    /// can never reach packing silently.
    /// </summary>
    IReadOnlyList<(string Csproj, string DepName, string Version)> DiscoverCrossFrameworkRefs()
    {
        var refs = new List<(string, string, string)>();
        if (!Directory.Exists(AppleFrameworksDir)) return refs;
        foreach (var csproj in Directory.EnumerateFiles(AppleFrameworksDir, "*.csproj", SearchOption.AllDirectories))
        {
            var content = File.ReadAllText(csproj);
            if (!content.Contains("SwiftBindings.Sdk/", StringComparison.Ordinal))
                continue;
            foreach (Match tag in AppleCrossRefTagPattern.Matches(content))
            {
                var nameM = AppleCrossRefIncludePattern.Match(tag.Value);
                var verM = AppleCrossRefVersionPattern.Match(tag.Value);
                if (!nameM.Success || !verM.Success)
                    continue;
                refs.Add((csproj, nameM.Groups["name"].Value, verM.Groups["ver"].Value));
            }
        }
        return refs;
    }

    /// <summary>
    /// Throw if any cross-framework <c>SwiftBindings.Apple.*</c> reference is in
    /// a form the bump/pack path can't handle, or still lags
    /// <paramref name="appleVersion"/> after the Apple bump.
    ///
    /// Deliberately STRICTER than <see cref="DiscoverCrossFrameworkRefs"/>:
    /// Discover only feeds packing (which needs a concrete version), so it quietly
    /// skips a tag it can't read a version from. The guard must instead REJECT such
    /// a tag. <see cref="BumpAppleVersionInternal"/>'s broad <c>&lt;Version&gt;</c>
    /// regex WOULD rewrite a child-element pin's version — creating a new
    /// <paramref name="appleVersion"/> requirement — while Discover and packing
    /// never see it. Letting that pass would report "clean" on exactly the pin the
    /// packer fails to produce: a false negative in the safety net. Only the
    /// attribute form
    /// (<c>&lt;PackageReference Include="…" Version="N.N.N" /&gt;</c>) is supported;
    /// any other form fails loudly so it gets converted, never silently skewed.
    /// Scans tags directly (not via Discover) so the unparseable forms it must
    /// reject aren't filtered out before it sees them — but uses the same
    /// <see cref="AppleCrossRefTagPattern"/> / <see cref="AppleCrossRefVersionPattern"/>
    /// the bump rewrites with, so the guard and the bump agree on what a pin is.
    /// </summary>
    void AssertCrossFrameworkCoherence(string appleVersion)
    {
        if (!Directory.Exists(AppleFrameworksDir))
            return;

        var unsupported = new List<string>();
        var stale = new List<string>();
        foreach (var csproj in Directory.EnumerateFiles(AppleFrameworksDir, "*.csproj", SearchOption.AllDirectories))
        {
            var content = File.ReadAllText(csproj);
            if (!content.Contains("SwiftBindings.Sdk/", StringComparison.Ordinal))
                continue;
            foreach (Match tag in AppleCrossRefTagPattern.Matches(content))
            {
                // The tag pattern already requires Include="SwiftBindings.Apple.X",
                // so the name sub-match always succeeds here.
                var name = AppleCrossRefIncludePattern.Match(tag.Value).Groups["name"].Value;
                var verM = AppleCrossRefVersionPattern.Match(tag.Value);
                if (!verM.Success)
                {
                    unsupported.Add($"  {Path.GetFileName(csproj)} → SwiftBindings.Apple.{name} (no Version attribute)");
                    continue;
                }
                if (verM.Groups["ver"].Value != appleVersion)
                    stale.Add($"  {Path.GetFileName(csproj)} → SwiftBindings.Apple.{name} {verM.Groups["ver"].Value} (expected {appleVersion})");
            }
        }

        if (unsupported.Count > 0)
            throw new InvalidOperationException(
                $"Pre-flight: {unsupported.Count} cross-framework SwiftBindings.Apple.* PackageReference(s) are not in the " +
                "supported Version-attribute form. The bump's broad <Version> regex may rewrite a child-element pin's " +
                "version while discovery and packing never see it, silently skewing the packed train:\n" +
                string.Join("\n", unsupported) +
                "\nConvert each to <PackageReference Include=\"SwiftBindings.Apple.X\" Version=\"N.N.N\" /> form.");

        if (stale.Count > 0)
            throw new InvalidOperationException(
                $"Pre-flight: {stale.Count} cross-framework PackageReference(s) lag the Apple train {appleVersion}:\n" +
                string.Join("\n", stale) +
                "\nBumpAppleVersionInternal should have rewritten these — check the csproj's PackageReference formatting.");
    }

    /// <summary>
    /// Build + pack each distinct cross-framework dependency at
    /// <paramref name="appleVersion"/> into <see cref="LocalPackagesDir"/> so
    /// dependents restore the coherent train. Evicts any stale same-version
    /// extraction from the global NuGet cache first — NuGet won't re-expand a
    /// version folder that already exists, which would silently mask the
    /// freshly packed content.
    ///
    /// ENFORCES single-level dependency edges (the only ones in the repo today
    /// are RealityKit→RealityFoundation and MatterSupport→Matter, both leaf
    /// dependencies). A deeper chain would need topological packing, so rather
    /// than pack alphabetically and risk building a dependency against a stale
    /// sibling, this hard-fails up front (see the non-leaf check below) if any
    /// dependency is itself a consumer — converting the latent assumption into a
    /// loud error the moment a multi-level chain is introduced.
    /// </summary>
    void PackCrossFrameworkDependencies(string appleVersion)
    {
        var refs = DiscoverCrossFrameworkRefs();
        var depNames = refs
            .Select(r => r.DepName)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
        if (depNames.Count == 0)
        {
            Log.Information("  no cross-framework dependencies to pack");
            return;
        }

        // Fail-fast on a multi-level chain. This packer builds each dependency in
        // plain alphabetical order, which is only safe when every dependency is a
        // leaf (carries no cross-framework pin of its own). If a dependency is ALSO
        // a consumer, building it during packing could restore a stale sibling that
        // a later loop iteration hasn't repacked yet. Surface that as a hard error
        // rather than silently packing against a stale sibling — the consumer's
        // framework name is the directory holding its csproj.
        var consumerNames = refs
            .Select(r => Path.GetFileName(Path.GetDirectoryName(r.Csproj)!))
            .ToHashSet(StringComparer.Ordinal);
        var nonLeaf = depNames.Where(consumerNames.Contains).ToList();
        if (nonLeaf.Count > 0)
            throw new InvalidOperationException(
                "Pre-flight: cross-framework dependency packing assumes leaf dependencies, but these are " +
                $"themselves consumers of other Apple frameworks: {string.Join(", ", nonLeaf)}. " +
                "Add topological ordering to PackCrossFrameworkDependencies before introducing a multi-level chain.");

        var globalCache = GlobalNuGetPackagesDir();
        foreach (var dep in depNames)
        {
            var dir = AppleFrameworksDir / dep;
            if (!Directory.Exists(dir))
                throw new InvalidOperationException(
                    $"Pre-flight: cross-framework dependency SwiftBindings.Apple.{dep} is referenced but " +
                    $"apple-frameworks/{dep}/ does not exist. Cannot pack a coherent {appleVersion} package.");

            if (globalCache is not null)
            {
                var cached = globalCache / $"swiftbindings.apple.{dep.ToLowerInvariant()}" / appleVersion;
                if (Directory.Exists(cached))
                {
                    Directory.Delete(cached, recursive: true);
                    Log.Information("  evicted stale cache: {Path}", cached);
                }
            }

            Log.Information("  packing cross-framework dependency SwiftBindings.Apple.{Dep} @ {Version}", dep, appleVersion);
            BuildAndPackAppleFramework(dir, appleVersion, LocalPackagesDir);
        }
    }

    /// <summary>
    /// One `dotnet restore` per in-scope test csproj — serial — before the
    /// parallel cell loop. Without this, parallel `dotnet build`/`publish` calls
    /// on the same csproj race on shared <c>obj/project.assets.json</c> /
    /// <c>obj/*.nuget.cache</c> writes even though the SDK splits
    /// <c>_SwiftBindingIntermediateDir</c> per TFM. Per-cell builds then pass
    /// <c>--no-restore</c> so the warm restore is a one-time cost.
    ///
    /// Pre-restore is sim-RID only. Trying a multi-RID restore via
    /// <c>-p:RuntimeIdentifiers=iossimulator-arm64%3Bios-arm64</c> fails on
    /// the .NET 10 SDK: the plural value bleeds into the singular
    /// <c>RuntimeIdentifier</c> slot and trips NETSDK1083. Device cells
    /// therefore do their own restore at publish time, gated behind the
    /// same library's sim cell so a device-side assets.json overwrite
    /// can't race a sim-side <c>--no-restore</c> read.
    /// </summary>
    /// <summary>
    /// Scan each library's test csproj for cross-library references that
    /// trigger a transitive build of the sibling library's csproj. Two such
    /// shapes are equivalent for the parallel-cell race:
    /// (1) <c>NativeReference Include="../../&lt;lib&gt;/obj/.../foo.xcframework"</c>
    ///     — consumes the sibling's intermediate build output; surfaces as
    ///     MT158 when both cells race the producer's obj/ tree.
    /// (2) <c>ProjectReference Include="../../&lt;lib&gt;/SwiftBindings.Apple.&lt;lib&gt;.csproj"</c>
    ///     — the canonical MSBuild trigger; surfaces as "No API definition
    ///     file specified" or similar mid-build wreckage when the sibling
    ///     cell rewrites that csproj's obj/ concurrently.
    /// Static-file references like <c>Include="../../BlinkID/BlinkID.xcframework"</c>
    /// (no <c>obj/</c>, no <c>.csproj</c>) point at a pre-shipped artifact
    /// that the sibling's build does not produce — those don't race.
    /// Returns library → set of libraries it must run after.
    /// </summary>
    Dictionary<string, HashSet<string>> InferInterLibraryObjDeps(IReadOnlyList<Cell> cells)
    {
        var pattern = new System.Text.RegularExpressions.Regex(
            @"Include\s*=\s*""\.\./\.\./([A-Za-z0-9_.]+)/(?:obj/|[^""]*\.csproj"")",
            System.Text.RegularExpressions.RegexOptions.Compiled);

        var libraries = new HashSet<string>(
            cells.Select(c => c.Library), StringComparer.Ordinal);

        var result = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var lib in libraries)
        {
            var dir = TestDir(lib);
            if (!Directory.Exists(dir)) continue;
            var deps = new HashSet<string>(StringComparer.Ordinal);
            foreach (var csproj in Directory.EnumerateFiles(dir, "*.csproj"))
            {
                var text = File.ReadAllText(csproj);
                foreach (System.Text.RegularExpressions.Match m in pattern.Matches(text))
                {
                    var depLib = m.Groups[1].Value;
                    // Only record deps to libraries we're actually scheduling; a
                    // reference to a library outside the current run shape isn't a
                    // gating concern.
                    if (!string.Equals(depLib, lib, StringComparison.Ordinal)
                        && libraries.Contains(depLib))
                        deps.Add(depLib);
                }
            }
            if (deps.Count > 0) result[lib] = deps;
        }
        return result;
    }

    void PreRestoreCells(IReadOnlyList<Cell> cells)
    {
        if (cells.Count == 0) return;

        // De-dup csprojs across cells: one library may have N platform cells
        // pointing at the same csproj. We only need to restore once per csproj.
        // The pre-restore is sim-RID by default (the SDK fills the singular
        // RuntimeIdentifier from the csproj's iossimulator-arm64 default), so
        // every sim / macos / maccatalyst / tvos cell can use --no-restore.
        // Device cells (ios-arm64) do their OWN restore at publish time —
        // see the cell-dispatch loop, which serializes a device cell behind
        // the same library's sim cell so the device's assets.json overwrite
        // doesn't race a sim cell's --no-restore read. Trying to land both
        // RIDs in one assets.json via `-p:RuntimeIdentifiers=A%3BB` was the
        // first attempt; the .NET 10 SDK bleeds that plural value into the
        // singular `RuntimeIdentifier` slot and trips NETSDK1083, so two
        // restores per csproj (sim now, device on-demand) is the only path.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var passes = new List<AbsolutePath>();
        foreach (var lib in cells.Select(c => c.Library).Distinct(StringComparer.Ordinal))
        {
            var testDir = TestDir(lib);
            if (!Directory.Exists(testDir)) continue;
            foreach (var csproj in Directory.EnumerateFiles(testDir, "*.csproj"))
            {
                if (seen.Add(csproj))
                    passes.Add((AbsolutePath)csproj);
            }
        }

        if (passes.Count == 0)
        {
            Log.Information("--- pre-restore: nothing to do ---");
            return;
        }

        Log.Information("--- pre-restore: {Count} test csproj(s) ---", passes.Count);
        var sw = Stopwatch.StartNew();
        foreach (var csproj in passes)
        {
            var exit = RunDotnet(new[] { "restore", (string)csproj });
            if (exit != 0)
                throw new InvalidOperationException($"pre-restore failed for {csproj} (exit {exit})");
        }
        Log.Information("--- pre-restore OK ({Sec:F1}s) ---", sw.Elapsed.TotalSeconds);
    }

    /// <summary>
    /// Read <c>artifacts/regression-validate-{version}.json</c> (if present) to
    /// build a (library, platform) → duration map, then return <paramref name="cells"/>
    /// sorted descending by that duration so the heaviest cells start first.
    /// Cells with no prior data fall to the end in their original manifest order.
    /// This is the same shape <c>nuke validate</c> uses for binding-validation
    /// targets — line drift + the longest tail dominates wall clock once the
    /// pool is wide enough.
    /// </summary>
    IReadOnlyList<Cell> SortLongestFirst(IReadOnlyList<Cell> cells, string version)
    {
        var artifactPath = RootDirectory / "artifacts" / $"regression-validate-{version}.json";
        if (!File.Exists(artifactPath))
        {
            Log.Debug("Longest-first scheduling: no prior artifact at {Path}, falling back to manifest order", artifactPath);
            return cells;
        }

        var durations = new Dictionary<(string Lib, string Plat), double>();
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(artifactPath));
            if (!doc.RootElement.TryGetProperty("cells", out var cellsArr)) return cells;
            foreach (var el in cellsArr.EnumerateArray())
            {
                if (!el.TryGetProperty("library", out var libEl) ||
                    !el.TryGetProperty("platform", out var platEl) ||
                    !el.TryGetProperty("durationSeconds", out var durEl))
                    continue;
                var lib = libEl.GetString();
                var plat = platEl.GetString();
                if (lib is null || plat is null) continue;
                durations[(lib, plat)] = durEl.GetDouble();
            }
        }
        catch (Exception ex)
        {
            Log.Warning("Longest-first scheduling: failed to parse {Path}: {Message}", artifactPath, ex.Message);
            return cells;
        }

        // Index in original order so cells with no prior data fall through deterministically.
        var indexed = cells.Select((c, i) => (Cell: c, Index: i)).ToList();
        var sorted = indexed
            .OrderByDescending(t => durations.TryGetValue((t.Cell.Library, PlatformLabel(t.Cell.Platform)), out var d) ? d : double.MinValue)
            .ThenBy(t => t.Index)
            .Select(t => t.Cell)
            .ToList();

        var withData = sorted.Count(c => durations.ContainsKey((c.Library, PlatformLabel(c.Platform))));
        Log.Information("Longest-first scheduling: {WithData}/{Total} cells have prior duration data", withData, cells.Count);
        return sorted;
    }

    /// <summary>
    /// The machine's NuGet global-packages folder, honoring the
    /// <c>NUGET_PACKAGES</c> override and otherwise <c>~/.nuget/packages</c>.
    /// Null only if the user profile can't be resolved.
    /// </summary>
    static AbsolutePath? GlobalNuGetPackagesDir()
    {
        var env = Environment.GetEnvironmentVariable("NUGET_PACKAGES");
        if (!string.IsNullOrEmpty(env))
            return (AbsolutePath)env;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrEmpty(home) ? null : (AbsolutePath)home / ".nuget" / "packages";
    }

    static IReadOnlyList<string> ListBootedSims()
    {
        var psi = new ProcessStartInfo("xcrun")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("simctl");
        psi.ArgumentList.Add("list");
        psi.ArgumentList.Add("devices");
        psi.ArgumentList.Add("booted");
        using var p = Process.Start(psi)!;
        // Drain stdout async + bound WaitForExit so a wedged CoreSimulator
        // can't hang the regression preflight on the way in.
        var sb = new System.Text.StringBuilder();
        p.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (sb) sb.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { /* discard stderr */ };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        if (!p.WaitForExit(10_000))
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException("xcrun simctl list devices booted timed out after 10s during preflight");
        }
        p.WaitForExit();
        var booted = new List<string>();
        foreach (var line in sb.ToString().Split('\n'))
        {
            var m = System.Text.RegularExpressions.Regex.Match(line, @"\(([0-9A-F-]{36})\) \(Booted\)");
            if (m.Success) booted.Add(m.Groups[1].Value);
        }
        return booted;
    }

    /// <summary>
    /// Build + validate a single cell. Each platform dispatches to its
    /// matching build helper and validator (or build-only for catalyst/tvos).
    /// All process output for the cell is routed through <paramref name="sink"/>
    /// so the parallel harness can tee it into a per-cell log file.
    /// </summary>
    CellOutcome RunCell(Cell cell, string? simUdid, string? deviceUdid, Stopwatch sw, Action<string> sink)
    {
        var (testDir, _, _) = ResolveTestNames(cell.Library);
        switch (cell.Platform)
        {
            case CellPlatform.IosSim:
            {
                BuildTestAppSimulator(cell.Library, testDir, "ios", onLine: sink, noRestore: true);
                var result = ValidateSimFor(cell.Library, Timeout, simUdid, "iossimulator-arm64", onLine: sink);
                return ToOutcome(cell, result, sw.Elapsed);
            }
            case CellPlatform.IosDevice:
            {
                // Device cell does its own restore — see PreRestoreCells:
                // a multi-RID assets.json can't be produced cleanly in .NET 10
                // (NETSDK1083), so this publish does an implicit restore that
                // overwrites the sim-RID assets file. The same-library sim
                // cell has already finished (see ScheduleProducerWaits below)
                // so the overwrite can't race a --no-restore reader.
                BuildTestAppNativeAot(cell.Library, testDir, onLine: sink, noRestore: false);
                var result = ValidateDeviceFor(cell.Library, Timeout, deviceUdid, aot: true, "ios-arm64", onLine: sink);
                return ToOutcome(cell, result, sw.Elapsed);
            }
            case CellPlatform.MacOs:
            {
                BuildTestAppSimulator(cell.Library, testDir, "macos", onLine: sink, noRestore: true);
                var result = ValidateMacFor(cell.Library, Timeout, "osx-arm64", onLine: sink);
                return ToOutcome(cell, result, sw.Elapsed);
            }
            case CellPlatform.MacCatalyst:
            {
                BuildTestAppSimulator(cell.Library, testDir, "maccatalyst", onLine: sink, noRestore: true);
                var result = ValidateMacCatalystFor(cell.Library, Timeout, "maccatalyst-arm64", onLine: sink);
                return ToOutcome(cell, result, sw.Elapsed);
            }
            case CellPlatform.TvOs:
            {
                BuildTestAppSimulator(cell.Library, testDir, "tvos", onLine: sink, noRestore: true);
                return new CellOutcome(cell, CellResult.BuildOnly, "no tvos validator yet", sw.Elapsed);
            }
            default:
                return new CellOutcome(cell, CellResult.Error, $"unhandled platform {cell.Platform}", sw.Elapsed);
        }
    }

    static CellOutcome ToOutcome(Cell cell, TestRunResult result, TimeSpan duration) =>
        result.Status switch
        {
            TestRunStatus.Success => new CellOutcome(cell, CellResult.Pass, null, duration),
            TestRunStatus.Failed => new CellOutcome(cell, CellResult.Fail, result.Reason, duration),
            TestRunStatus.Crashed => new CellOutcome(cell, CellResult.Crashed, result.Reason, duration),
            TestRunStatus.Timeout => new CellOutcome(cell, CellResult.Timeout, result.Reason, duration),
            TestRunStatus.Exited => new CellOutcome(cell, CellResult.Fail, result.Reason ?? "app exited without TEST SUCCESS", duration),
            _ => new CellOutcome(cell, CellResult.Error, $"unexpected status {result.Status}", duration),
        };

    static string CellLabel(CellResult r) => r switch
    {
        CellResult.Pass => "PASS",
        CellResult.Fail => "FAIL",
        CellResult.BuildOnly => "BUILD-ONLY",
        CellResult.Skip => "SKIP",
        CellResult.Timeout => "TIMEOUT",
        CellResult.Crashed => "CRASH",
        CellResult.Error => "ERROR",
        _ => r.ToString(),
    };

    void PrintMatrix(
        IReadOnlyList<string> packages,
        IReadOnlyList<CellPlatform> platforms,
        IReadOnlyList<CellOutcome> outcomes,
        TimeSpan totalDuration)
    {
        // Index outcomes by (library, platform) for fast lookup.
        var byCell = outcomes.ToDictionary(o => (o.Cell.Library, o.Cell.Platform));

        var libWidth = Math.Max(20, packages.Max(p => p.Length) + 2);
        var colWidths = platforms.ToDictionary(p => p, p => Math.Max(PlatformLabel(p).Length, "BUILD-ONLY".Length) + 2);

        var sb = new System.Text.StringBuilder();
        sb.AppendLine();
        sb.AppendLine($"=== regression-validate ({totalDuration.TotalMinutes:F1} min) ===");
        sb.AppendLine();

        // Header
        sb.Append(new string(' ', libWidth));
        foreach (var p in platforms)
            sb.Append(PlatformLabel(p).PadRight(colWidths[p]));
        sb.AppendLine();

        sb.Append(new string('─', libWidth));
        foreach (var p in platforms)
            sb.Append(new string('─', colWidths[p]));
        sb.AppendLine();

        foreach (var lib in packages)
        {
            sb.Append(lib.PadRight(libWidth));
            foreach (var p in platforms)
            {
                var label = byCell.TryGetValue((lib, p), out var o) ? CellLabel(o.Result) : "—";
                sb.Append(label.PadRight(colWidths[p]));
            }
            sb.AppendLine();
        }
        sb.AppendLine();

        // Verdict
        var counts = outcomes.GroupBy(o => o.Result).ToDictionary(g => g.Key, g => g.Count());
        var pass = counts.GetValueOrDefault(CellResult.Pass);
        var fail = counts.GetValueOrDefault(CellResult.Fail)
                 + counts.GetValueOrDefault(CellResult.Timeout)
                 + counts.GetValueOrDefault(CellResult.Crashed)
                 + counts.GetValueOrDefault(CellResult.Error);
        var buildOnly = counts.GetValueOrDefault(CellResult.BuildOnly);
        var skip = counts.GetValueOrDefault(CellResult.Skip);
        sb.AppendLine($"Verdict: {fail} FAIL, {pass} PASS, {buildOnly} BUILD-ONLY, {skip} SKIP");

        if (fail > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Failures:");
            foreach (var o in outcomes.Where(o => o.Result is CellResult.Fail or CellResult.Timeout or CellResult.Crashed or CellResult.Error))
                sb.AppendLine($"  {o.Cell.Library} {PlatformLabel(o.Cell.Platform)}: {o.Reason}");
        }

        Console.WriteLine(sb.ToString());
    }

    void WriteJsonArtifact(
        string version,
        IReadOnlyList<CellPlatform> platforms,
        IReadOnlyList<CellOutcome> outcomes,
        TimeSpan totalDuration)
    {
        var artifactDir = RootDirectory / "artifacts";
        Directory.CreateDirectory(artifactDir);
        var path = artifactDir / $"regression-validate-{version}.json";

        var payload = new
        {
            version,
            timestamp = DateTime.UtcNow.ToString("o"),
            totalDurationSeconds = totalDuration.TotalSeconds,
            platforms = platforms.Select(PlatformLabel).ToArray(),
            cells = outcomes.Select(o => new
            {
                library = o.Cell.Library,
                platform = PlatformLabel(o.Cell.Platform),
                result = CellLabel(o.Result),
                reason = o.Reason,
                durationSeconds = o.Duration.TotalSeconds,
            }).ToArray(),
        };
        File.WriteAllText(path, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
        Log.Information("Wrote artifact: {Path}", path);
    }
}
