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
        .Executes(() => WithAbortExitCode(async () =>
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

            // A run that plans zero cells must never read as a pass. Before this,
            // `--filter X --platforms Y` with no matching declared TFM exited 0
            // having validated nothing — a vacuous green a release gate would
            // happily consume.
            if (cells.Count == 0)
            {
                RegressionExitCodeOverride = RegressionExitAborted;
                throw new InvalidOperationException(
                    "regression-validate: 0 cells to run — nothing was validated, so this is not a pass.\n" +
                    $"  packages: {string.Join(", ", packages)}\n" +
                    $"  platforms: {string.Join(", ", requestedPlatforms.Select(PlatformLabel))}\n" +
                    "  No in-scope test csproj declares a TFM for any requested platform. " +
                    "Check --filter / --platforms against the packages' declared TFMs.");
            }

            // --- Run-scoped evidence directory ---
            // Every run gets its own directory keyed by start time + HEAD sha, so
            // no run can overwrite another run's logs, matrix, or JSON verdict.
            // (Before this, per-cell logs lived in a single /tmp dir that was
            // wiped on entry and the JSON verdict was a single clobbered slot —
            // a 1-cell spot check silently replaced a full matrix's evidence.)
            var runStartUtc = DateTime.UtcNow;
            var runId = ComposeRunId(runStartUtc, TryGitShortSha());
            var runDir = RegressionRunsDir(version) / runId;
            var cellLogDir = runDir / "cells";
            Log.Information("Run directory: {Dir}", runDir);

            Preflight(version, appleVersion, cells);

            // Created only once pre-flight passes: a pre-flight abort writes no
            // cells and no verdict, so it should leave no empty evidence dir
            // behind to be mistaken for a run that happened.
            Directory.CreateDirectory(cellLogDir);

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
            // Lives inside the run directory: cell logs are the evidence behind a
            // cell's verdict, so they must survive the next run rather than being
            // wiped by it.
            var teeDir = cellLogDir;

            // --- Concurrency primitives ---
            var outcomes = new ConcurrentDictionary<Cell, CellOutcome>();
            var pipelineStart = DateTime.UtcNow;
            // Set when the harness itself (not a cell) fails — e.g. sim-fleet
            // exhaustion. Recorded in the artifact instead of throwing straight
            // out of the target, so an aborted run still leaves a machine-readable
            // record of which cells were reached.
            string? abortReason = null;
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
            catch (Exception ex)
            {
                // A harness-level failure (not a cell failure — RunCell exceptions
                // are already caught per-cell). Record it and fall through to the
                // reporting tail: the run is a FAIL either way, but the operator
                // still gets the matrix, the artifact, and the cell logs that did
                // complete instead of a bare stack trace.
                abortReason = ex is AggregateException agg
                    ? string.Join(" | ", agg.Flatten().InnerExceptions.Select(e => e.Message))
                    : ex.Message;
                Log.Error(ex, "regression-validate: run aborted — {Reason}", abortReason);
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
            var unreachedCells = new List<Cell>();
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
                    unreachedCells.Add(cell);
                    Log.Error("  [{Lib} {Plat}] → no outcome recorded",
                        cell.Library, PlatformLabel(cell.Platform));
                }
            }

            var matrixReport = RenderMatrix(packages, requestedPlatforms, outcomeList, totalDuration);
            Console.WriteLine(matrixReport);
            File.WriteAllText(runDir / "matrix.txt", matrixReport);

            // F3 scaffolding: snapshot each binding's already-emitted
            // {Module}.api-surface.md / .api-manifest.json into the run directory
            // and, when a checked-in baseline exists, report drift. Informational
            // only — no gate, no exit-code effect (see ApiSurfaceBaselineDir).
            var apiSurface = CollectApiSurfaceSnapshots(packages, runDir / "api-surface");

            var written = WriteRegressionArtifacts(
                version: version,
                runId: runId,
                runDir: runDir,
                platforms: requestedPlatforms,
                filter: Filter,
                packages: packages,
                cellsPlanned: cells.Count,
                unreached: unreachedCells,
                outcomes: outcomeList,
                abortReason: abortReason,
                totalDuration: totalDuration,
                inputs: BuildInputsBlock(version, appleVersion, packages),
                apiSurface: apiSurface);

            // --- Verdict ---
            // Named, machine-readable, and non-clobberable: the exit code says
            // *why* (2 = cells failed, 3 = harness abort), the console names every
            // failing cell with its log path, and the artifact carries the same
            // list under `failedCells` for a release gate to consume.
            var failedCells = outcomeList.Where(IsFailure).ToList();
            if (failedCells.Count > 0 || abortReason is not null)
            {
                var sb = new System.Text.StringBuilder();
                sb.AppendLine();
                sb.AppendLine("─── FAILING CELLS ───");
                foreach (var o in failedCells)
                    sb.AppendLine($"  {CellLabel(o.Result),-10} {o.Cell.Library} {PlatformLabel(o.Cell.Platform)}" +
                                  $"{(o.Reason is null ? "" : $" — {o.Reason}")}" +
                                  $"\n             log: {cellLogDir / $"{o.Cell.Library}-{PlatformLabel(o.Cell.Platform)}.log"}");
                if (abortReason is not null)
                    sb.AppendLine($"  ABORTED    harness — {abortReason}");
                sb.AppendLine($"  artifact:  {written.RunArtifact}");
                Console.WriteLine(sb.ToString());

                RegressionExitCodeOverride = abortReason is not null
                    ? RegressionExitAborted
                    : RegressionExitCellsFailed;

                var named = string.Join(", ", failedCells
                    .Take(12)
                    .Select(o => $"{o.Cell.Library}/{PlatformLabel(o.Cell.Platform)}={CellLabel(o.Result)}"));
                if (failedCells.Count > 12) named += $", +{failedCells.Count - 12} more";

                throw new InvalidOperationException(
                    $"regression-validate: {failedCells.Count} of {cells.Count} cell(s) failed" +
                    (abortReason is null ? "" : $"; run ABORTED ({abortReason})") +
                    (failedCells.Count > 0 ? $": {named}" : "") +
                    $"\n  verdict artifact: {written.RunArtifact}" +
                    $"\n  cell logs:        {cellLogDir}" +
                    $"\n  exit code {(int)RegressionExitCodeOverride!}" +
                    $" ({(abortReason is not null ? "harness abort" : "cell failures")})");
            }

            if (!written.Complete)
                Log.Warning(
                    "regression-validate: run is GREEN but INCOMPLETE (complete=false) — {Reasons}. " +
                    "A release gate must not treat this as a full-matrix pass.",
                    string.Join("; ", written.IncompleteReasons));
        }));

    /// <summary>
    /// Run the matrix body, guaranteeing every escape path carries a
    /// reason-specific exit code. Paths that already chose one (cell failures
    /// = 2, deliberate abort = 3) keep it; anything else that throws — a
    /// pre-flight failure such as a missing SDK nupkg, an unparseable
    /// <c>--platforms</c> value, an unexpected harness exception — is an abort
    /// and gets 3 rather than Nuke's blanket 255. Without this, a pre-flight
    /// failure was indistinguishable from any other build failure to a release
    /// gate, even though "the matrix never ran" and "the matrix ran and went
    /// red" demand opposite responses.
    ///
    /// <para>
    /// A pre-flight abort deliberately writes no verdict artifact and no run
    /// directory: nothing was validated, so there is no evidence to record,
    /// and an empty run dir would read as a run that happened. The exit code
    /// alone (3, with the thrown message) carries it.
    /// </para>
    /// </summary>
    static async Task WithAbortExitCode(Func<Task> body)
    {
        try
        {
            await body();
        }
        catch
        {
            RegressionExitCodeOverride ??= RegressionExitAborted;
            throw;
        }
    }

    // Exit codes distinguishable from Nuke's generic 255 so a release gate can
    // branch on the reason without parsing the log. Surfaced through Main via
    // RegressionExitCodeOverride.
    internal const int RegressionExitCellsFailed = 2;
    internal const int RegressionExitAborted = 3;

    /// <summary>
    /// Set by <c>RegressionValidate</c> immediately before it throws, so
    /// <see cref="Main"/> can return a reason-specific exit code instead of
    /// Nuke's blanket 255. Never set on a passing run.
    /// </summary>
    internal static int? RegressionExitCodeOverride;

    static bool IsFailure(CellOutcome o) =>
        o.Result is CellResult.Fail or CellResult.Timeout or CellResult.Crashed or CellResult.Error;

    [Parameter("RegressionValidate: package filter — one name or a comma-separated list (parity with --library)")]
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

    /// <summary>
    /// Resolve the in-scope package set. <paramref name="filter"/> accepts a
    /// single name or a comma-separated list, so a re-verification run can
    /// target exactly the failed surface of a prior matrix in ONE run — one
    /// verdict artifact covering the whole retested set, instead of N
    /// fragmentary partials each paying pre-flight again. Unknown names throw
    /// rather than silently narrowing the run.
    /// </summary>
    IReadOnlyList<string> ResolvePackages(string? filter)
    {
        if (!string.IsNullOrWhiteSpace(filter))
        {
            var named = filter
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            var unknown = named
                .Where(n => !Directory.Exists(LibrariesDir / n) && !Directory.Exists(AppleFrameworksDir / n))
                .ToList();
            if (unknown.Count > 0)
                throw new InvalidOperationException(
                    $"--filter names unknown package(s): {string.Join(", ", unknown)}. " +
                    "A typo would otherwise silently shrink the retested surface.");

            return named;
        }

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

        // 1b. Evict the global-cache extracts of the packages under test.
        //     A regression run re-packs the SAME version with DIFFERENT content
        //     (that is the whole point — validate the fixed build at 0.18.0).
        //     NuGet keys its extracted copy on id+version alone, so a stale
        //     extract from the previous build silently wins over the fresh
        //     nupkg in local-packages/ and the matrix validates the OLD code
        //     while reporting the new version — a false green of exactly the
        //     class this harness exists to catch. The cross-framework Apple
        //     dependency loop below evicts only the packages IT repacks; these
        //     three are the ones every cell resolves.
        EvictGlobalCacheEntries(
            (SdkPackageId, version),
            (RuntimePackageId, version),
            (ApplePackageId, appleVersion));

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
        // Prefer the canonical (complete-run) artifact; fall back to the most
        // recent run-scoped artifact, which is the only record a filtered or
        // aborted run leaves in the canonical slot's place.
        var artifactPath = RootDirectory / "artifacts" / $"regression-validate-{version}.json";
        if (!File.Exists(artifactPath))
        {
            var runsDir = RegressionRunsDir(version);
            var latestRun = Directory.Exists(runsDir)
                ? Directory.EnumerateDirectories(runsDir)
                    .Select(d => (AbsolutePath)d / "regression-validate.json")
                    .Where(p => File.Exists(p))
                    .OrderByDescending(p => (string)p, StringComparer.Ordinal)
                    .FirstOrDefault()
                : null;
            if (latestRun is null)
            {
                Log.Debug("Longest-first scheduling: no prior artifact at {Path}, falling back to manifest order", artifactPath);
                return cells;
            }
            artifactPath = latestRun;
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

    // The packages every cell resolves out of local-packages/. Named here so
    // pre-flight's cache eviction and the self-test agree on one list.
    internal const string SdkPackageId = "SwiftBindings.Sdk";
    internal const string RuntimePackageId = "SwiftBindings.Runtime";
    internal const string ApplePackageId = "SwiftBindings.Apple";

    /// <summary>
    /// Delete the NuGet global-packages extract for each (id, version) pair, so
    /// a freshly-packed nupkg of an already-cached version is actually the code
    /// under test. Returns the directories it removed (for logging/assertions).
    /// Absent entries are not an error — a clean machine simply has nothing to
    /// evict — but a present-and-undeletable one is, because silently
    /// proceeding would validate stale code.
    /// </summary>
    internal static IReadOnlyList<string> EvictGlobalCacheEntries(
        params (string Id, string Version)[] entries)
    {
        var cache = GlobalNuGetPackagesDir();
        var evicted = new List<string>();
        if (cache is null)
        {
            Log.Warning(
                "  cannot locate the NuGet global-packages folder — skipping cache eviction. " +
                "A stale extract of a re-packed version could shadow the build under test.");
            return evicted;
        }

        foreach (var (id, version) in entries)
        {
            // NuGet lowercases both segments on disk.
            var dir = cache / id.ToLowerInvariant() / version.ToLowerInvariant();
            if (!Directory.Exists(dir))
                continue;

            try
            {
                Directory.Delete(dir, recursive: true);
                evicted.Add(dir);
                Log.Information("  evicted stale cache: {Path}", dir);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Pre-flight: could not evict the stale NuGet cache entry {dir} ({ex.Message}). " +
                    "Continuing would let the previously-cached build shadow the freshly-packed " +
                    $"{id} {version} and produce a verdict for the wrong code.", ex);
            }
        }
        return evicted;
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

    /// <summary>
    /// Render the per-cell verdict table. Returned as a string (rather than
    /// printed in place) so the exact text the operator sees is also persisted
    /// to <c>matrix.txt</c> in the run directory — console scrollback is not
    /// evidence.
    /// </summary>
    string RenderMatrix(
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
            foreach (var o in outcomes.Where(IsFailure))
                sb.AppendLine($"  {o.Cell.Library} {PlatformLabel(o.Cell.Platform)}: {o.Reason}");
        }

        return sb.ToString();
    }

    // ── F1: self-describing, un-clobberable matrix artifact ─────────────────

    /// <summary>Root of the per-run evidence tree for a given SDK version.</summary>
    AbsolutePath RegressionRunsDir(string version) =>
        RootDirectory / "artifacts" / "regression-validate" / version;

    /// <summary>
    /// Optional checked-in API-surface baseline (F3). Absent by design today —
    /// the collector reports "no baseline" rather than failing, so seeding the
    /// baseline is a deliberate later act.
    /// </summary>
    AbsolutePath ApiSurfaceBaselineDir => RootDirectory / "api-surface";

    /// <summary>
    /// Run identity: UTC start (to the millisecond) + owning process id + short
    /// HEAD sha. Sorts lexicographically by time — every component is
    /// fixed-width or comes after the timestamp — and names the tree state the
    /// run validated. The millisecond and pid components are what keep two runs
    /// started in the same second on the same commit from sharing a directory
    /// and interleaving their cell logs.
    /// </summary>
    static string ComposeRunId(DateTime utcStart, string? shortSha, int? processId = null) =>
        $"{utcStart:yyyyMMdd-HHmmss.fff}Z-p{processId ?? Environment.ProcessId}" +
        $"-{(string.IsNullOrEmpty(shortSha) ? "nogit" : shortSha)}";

    /// <summary>
    /// One drifted API-surface snapshot. Lowercase member names are deliberate:
    /// the artifact is serialized with default (non-remapping) options, so the
    /// record's names ARE the JSON keys.
    /// </summary>
    sealed record ApiSurfaceDriftEntry(string path, string status, int linesRemoved);

    sealed record RegressionArtifactResult(
        AbsolutePath RunArtifact,
        AbsolutePath? CanonicalArtifact,
        AbsolutePath? PartialArtifact,
        bool Complete,
        IReadOnlyList<string> IncompleteReasons);

    /// <summary>
    /// Decide whether a run is a *complete* matrix: unfiltered, all platforms,
    /// every planned cell reached an outcome, and no harness abort. Only a
    /// complete run may claim the canonical <c>regression-validate-{version}.json</c>
    /// slot — a filtered or aborted run writes <c>.partial-N.json</c> instead, so
    /// a 1-cell spot check can never overwrite a full matrix's record (the exact
    /// hazard that left 0.17.0 with a 1-cell release artifact).
    /// </summary>
    static (bool Complete, List<string> Reasons) EvaluateCompleteness(
        string? filter,
        IReadOnlyList<CellPlatform> platforms,
        int cellsPlanned,
        IReadOnlyList<Cell> unreached,
        string? abortReason)
    {
        var reasons = new List<string>();
        if (!string.IsNullOrWhiteSpace(filter))
            reasons.Add($"--filter {filter} restricts the matrix to a package subset");
        var missingPlatforms = AllPlatforms.Where(p => !platforms.Contains(p)).ToList();
        if (missingPlatforms.Count > 0)
            reasons.Add($"platform subset — not run: {string.Join(", ", missingPlatforms.Select(PlatformLabel))}");
        if (abortReason is not null)
            reasons.Add($"run aborted: {abortReason}");
        if (unreached.Count > 0)
            reasons.Add($"{unreached.Count} of {cellsPlanned} planned cell(s) produced no outcome: " +
                        string.Join(", ", unreached.Take(8).Select(c => $"{c.Library}/{PlatformLabel(c.Platform)}")) +
                        (unreached.Count > 8 ? ", …" : ""));
        return (reasons.Count == 0, reasons);
    }

    /// <summary>
    /// Write the run-scoped verdict artifact (always) plus the top-level pointer:
    /// the canonical slot for a complete run, an indexed <c>.partial-N.json</c>
    /// otherwise. The run-scoped copy is the durable record; the canonical file
    /// exists so the skill and <see cref="SortLongestFirst"/> keep a stable path.
    /// </summary>
    RegressionArtifactResult WriteRegressionArtifacts(
        string version,
        string runId,
        AbsolutePath runDir,
        IReadOnlyList<CellPlatform> platforms,
        string? filter,
        IReadOnlyList<string> packages,
        int cellsPlanned,
        IReadOnlyList<Cell> unreached,
        IReadOnlyList<CellOutcome> outcomes,
        string? abortReason,
        TimeSpan totalDuration,
        object inputs,
        object apiSurface)
    {
        var artifactDir = RootDirectory / "artifacts";
        Directory.CreateDirectory(artifactDir);
        Directory.CreateDirectory(runDir);

        var (complete, incompleteReasons) = EvaluateCompleteness(
            filter, platforms, cellsPlanned, unreached, abortReason);

        var failed = outcomes.Where(IsFailure).ToList();
        var verdict = abortReason is not null ? "ABORTED" : failed.Count > 0 ? "FAIL" : "PASS";

        var payload = new
        {
            schemaVersion = 2,
            version,
            runId,
            timestamp = DateTime.UtcNow.ToString("o"),
            totalDurationSeconds = totalDuration.TotalSeconds,

            // --- verdict block (F2: machine-readable enough to gate a release) ---
            verdict,
            complete,
            incompleteReasons = incompleteReasons.ToArray(),
            abortReason,
            exitCode = verdict switch
            {
                "ABORTED" => RegressionExitAborted,
                "FAIL" => RegressionExitCellsFailed,
                _ => 0,
            },
            counts = new
            {
                planned = cellsPlanned,
                recorded = outcomes.Count,
                unreached = unreached.Count,
                pass = outcomes.Count(o => o.Result == CellResult.Pass),
                fail = failed.Count,
                buildOnly = outcomes.Count(o => o.Result == CellResult.BuildOnly),
                skip = outcomes.Count(o => o.Result == CellResult.Skip),
            },
            failedCells = failed.Select(o => new
            {
                library = o.Cell.Library,
                platform = PlatformLabel(o.Cell.Platform),
                result = CellLabel(o.Result),
                reason = o.Reason,
            }).ToArray(),

            // --- run shape ---
            filter,
            packages = packages.ToArray(),
            platforms = platforms.Select(PlatformLabel).ToArray(),
            platformsComplete = AllPlatforms.All(platforms.Contains),
            cellsPlanned,

            // --- what was actually under test ---
            inputs,
            apiSurface,
            artifacts = new
            {
                runDir = (string)runDir,
                cellLogs = (string)(runDir / "cells"),
                matrix = (string)(runDir / "matrix.txt"),
            },

            cells = outcomes.Select(o => new
            {
                library = o.Cell.Library,
                platform = PlatformLabel(o.Cell.Platform),
                result = CellLabel(o.Result),
                reason = o.Reason,
                durationSeconds = o.Duration.TotalSeconds,
                log = (string)(runDir / "cells" / $"{o.Cell.Library}-{PlatformLabel(o.Cell.Platform)}.log"),
            }).ToArray(),
        };

        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });

        var runArtifact = runDir / "regression-validate.json";
        File.WriteAllText(runArtifact, json);
        Log.Information("Wrote run artifact: {Path}", runArtifact);

        AbsolutePath? canonical = null;
        AbsolutePath? partial = null;
        if (complete)
        {
            canonical = artifactDir / $"regression-validate-{version}.json";
            File.WriteAllText(canonical, json);
            Log.Information("Wrote canonical artifact (complete run): {Path}", canonical);
        }
        else
        {
            partial = NextPartialArtifactPath(artifactDir, version);
            File.WriteAllText(partial, json);
            Log.Warning(
                "Incomplete run — wrote {Path} and left regression-validate-{Version}.json untouched. Reasons: {Reasons}",
                partial, version, string.Join("; ", incompleteReasons));
        }

        return new RegressionArtifactResult(runArtifact, canonical, partial, complete, incompleteReasons);
    }

    /// <summary>Lowest free <c>regression-validate-{version}.partial-N.json</c>.</summary>
    static AbsolutePath NextPartialArtifactPath(AbsolutePath artifactDir, string version)
    {
        for (var n = 1; ; n++)
        {
            var candidate = artifactDir / $"regression-validate-{version}.partial-{n}.json";
            if (!File.Exists(candidate)) return candidate;
        }
    }

    /// <summary>
    /// The <c>inputs</c> block: everything that decides what the matrix actually
    /// exercised. Without it, two artifacts with the same version string can
    /// describe different runs (different library pins, different Xcode) and a
    /// baseline comparison silently confounds them.
    /// </summary>
    object BuildInputsBlock(string version, string appleVersion, IReadOnlyList<string> packages)
    {
        var libraries = new SortedDictionary<string, object?>(StringComparer.Ordinal);
        foreach (var lib in packages)
        {
            var configPath = LibrariesDir / lib / "library.json";
            if (File.Exists(configPath))
            {
                string? libVersion = null, mode = null;
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(configPath));
                    if (doc.RootElement.TryGetProperty("version", out var v)) libVersion = v.GetString();
                    if (doc.RootElement.TryGetProperty("mode", out var m)) mode = m.GetString();
                }
                catch (Exception ex)
                {
                    Log.Warning("inputs: could not read {Path}: {Message}", configPath, ex.Message);
                }
                libraries[lib] = new { kind = "third-party", version = libVersion, mode };
            }
            else
            {
                // apple-frameworks/* carry no library.json — they ride the Apple train.
                libraries[lib] = new { kind = "apple-framework", version = appleVersion, mode = (string?)null };
            }
        }

        return new
        {
            sdkVersion = version,
            appleVersion,
            xcode = TryCapture("xcodebuild", new[] { "-version" })?.Replace("\n", " / "),
            swift = TryCapture("swift", new[] { "--version" })?.Split('\n').FirstOrDefault(),
            dotnet = TryCapture("dotnet", new[] { "--version" }),
            gitSha = TryGitShortSha(),
            gitDirty = TryCapture("git", new[] { "-C", (string)RootDirectory, "status", "--porcelain" })?.Length > 0,
            host = Environment.MachineName,
            libraries,
        };
    }

    string? TryGitShortSha() =>
        TryCapture("git", new[] { "-C", (string)RootDirectory, "rev-parse", "--short", "HEAD" });

    /// <summary>
    /// Run a short-lived command and return its trimmed stdout, or null if it
    /// can't be started / exits non-zero / doesn't finish in 15s. Never throws:
    /// a missing tool must degrade the artifact's <c>inputs</c> block, not fail
    /// the matrix run that produced it.
    /// </summary>
    static string? TryCapture(string file, IReadOnlyList<string> args)
    {
        try
        {
            var psi = new ProcessStartInfo(file)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p is null) return null;
            var sb = new System.Text.StringBuilder();
            p.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (sb) sb.AppendLine(e.Data); };
            p.ErrorDataReceived += (_, e) => { /* discard */ };
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            if (!p.WaitForExit(15_000))
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                return null;
            }
            p.WaitForExit();
            return p.ExitCode == 0 ? sb.ToString().Trim() : null;
        }
        catch
        {
            return null;
        }
    }

    // ── F3 scaffolding: per-library API-surface snapshots ───────────────────

    /// <summary>
    /// Copy every <c>{Module}.api-surface.md</c> / <c>{Module}.api-manifest.json</c>
    /// the SDK already emitted under each in-scope package's <c>obj/**/swift-binding/</c>
    /// into the run directory, and — when <see cref="ApiSurfaceBaselineDir"/> holds
    /// a checked-in copy at the same relative path — report per-file drift
    /// (including how many surface lines disappeared).
    ///
    /// <para>
    /// SCAFFOLDING ONLY, by design: it collects and reports, it does not gate. The
    /// F3 ratchet (surface shrink ⇒ red) needs a seeded, reviewed baseline; seeding
    /// one from a run whose generator is under investigation would freeze the very
    /// shrinkage it is meant to catch. With no baseline present this reports
    /// <c>baselinePresent: false</c> and nothing else changes.
    /// </para>
    /// </summary>
    object CollectApiSurfaceSnapshots(IReadOnlyList<string> packages, AbsolutePath snapshotDir)
    {
        var drift = new List<ApiSurfaceDriftEntry>();
        var copied = 0;
        var baselinePresent = Directory.Exists(ApiSurfaceBaselineDir);

        foreach (var lib in packages)
        {
            AbsolutePath libDir;
            try { libDir = LibraryDir(lib); }
            catch { continue; }
            if (!Directory.Exists(libDir)) continue;

            foreach (var file in Directory.EnumerateFiles(libDir, "*.api-*", SearchOption.AllDirectories))
            {
                var name = Path.GetFileName(file);
                if (!name.EndsWith(".api-surface.md", StringComparison.Ordinal) &&
                    !name.EndsWith(".api-manifest.json", StringComparison.Ordinal))
                    continue;

                var rel = Path.Combine(lib, Path.GetRelativePath(libDir, file));
                var dest = snapshotDir / rel;
                Directory.CreateDirectory(Path.GetDirectoryName((string)dest)!);
                File.Copy(file, dest, overwrite: true);
                copied++;

                if (!baselinePresent) continue;
                var baseline = ApiSurfaceBaselineDir / rel;
                if (!File.Exists(baseline))
                {
                    drift.Add(new ApiSurfaceDriftEntry(rel, "new", 0));
                    continue;
                }
                var oldLines = File.ReadAllLines(baseline);
                var newLines = File.ReadAllLines(file);
                var removed = oldLines.Except(newLines, StringComparer.Ordinal).Count();
                if (removed > 0 || !oldLines.SequenceEqual(newLines, StringComparer.Ordinal))
                    drift.Add(new ApiSurfaceDriftEntry(rel, removed > 0 ? "shrunk" : "changed", removed));
            }
        }

        if (copied > 0)
            Log.Information("API-surface snapshots: {Count} file(s) → {Dir}", copied, snapshotDir);
        var shrunk = drift.Count(d => d.status == "shrunk");
        if (baselinePresent && drift.Count > 0)
            Log.Warning("API-surface drift vs baseline: {Drift} file(s), {Shrunk} shrunk (informational — F3 gate not enabled)",
                drift.Count, shrunk);

        return new
        {
            snapshotDir = (string)snapshotDir,
            filesCollected = copied,
            baselineDir = (string)ApiSurfaceBaselineDir,
            baselinePresent,
            gateEnabled = false,
            drift = drift.ToArray(),
        };
    }
}
