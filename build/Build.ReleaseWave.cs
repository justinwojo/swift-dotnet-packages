using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Nuke.Common;
using Nuke.Common.IO;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using SwiftBindings.Build.Helpers;
using SwiftBindings.Build.Models;

partial class Build
{
    // ═══════════════════════════════════════════════════════════════════════
    //  ReleaseWave — run the whole GitHub-Actions release pipeline locally
    //
    //  ci.yml + release.yml, in one process, for 1..N libraries:
    //    preflight → (build → pack → stage → sim-validate)* → publish → report
    //
    //  Two things make this more than a loop over BuildAndPackRelease:
    //
    //  1. NU1102 is fixed STRUCTURALLY, not worked around. A dependent library
    //     cannot start until its base has finished and its .nupkg is staged in
    //     local-packages/ — which NuGet.config lists ahead of nuget.org. The
    //     dependent then restores the base from disk, so nuget.org index
    //     propagation never enters the build path at all. The manual
    //     "dispatch the base first, wait, then re-dispatch the dependent"
    //     dance in CLAUDE.md's Release Process becomes unnecessary.
    //
    //  2. Parallelism is bounded AND dependency-aware. Libraries that share a
    //     source tree (via ProjectReference or a cross-library
    //     SwiftFrameworkDependency) are serialized against each other, because
    //     two concurrent builds writing one obj/ is a data race no amount of
    //     retry logic fixes.
    //
    //  HARD CONSTRAINT: one Nuke process. Every step below is an in-process
    //  method call into the same helpers the individual targets use. Shelling
    //  out to a nested `dotnet nuke` would silently skip on the build.log lock.
    // ═══════════════════════════════════════════════════════════════════════

    [Parameter("ReleaseWave: explicit library=version assignments, e.g. \"Nuke=13.0.10,Stripe=26.0.1\"")]
    readonly string? WaveSpec;

    [Parameter("ReleaseWave: libraries to release (comma-separated); needs --auto-version")]
    readonly string[] Libraries = Array.Empty<string>();

    [Parameter("ReleaseWave: compute each version from nuget.org + library.json per the documented convention")]
    readonly bool AutoVersion;

    [Parameter("ReleaseWave: how many libraries to build concurrently (default 3)")]
    readonly int? MaxParallel;

    [Parameter("ReleaseWave: output directory (default artifacts/release-wave/<runId>)")]
    readonly string? WaveOutput;

    [Parameter("ReleaseWave: publish the libraries that succeeded even though others failed")]
    readonly bool PublishPartial;

    /// <summary>Exit code when one or more libraries failed to build/pack/validate.</summary>
    internal const int WaveExitLibrariesFailed = 2;

    /// <summary>Exit code when preflight or the orchestrator itself aborted.</summary>
    internal const int WaveExitAborted = 3;

    /// <summary>Exit code when every library succeeded but a nuget push failed.</summary>
    internal const int WaveExitPublishFailed = 4;

    const int WaveDefaultMaxParallel = 3;

    int ResolveMaxParallel() => Math.Max(1, MaxParallel ?? WaveDefaultMaxParallel);

    // ── Result models ───────────────────────────────────────────────────────

    enum WaveVerdict { Success, Failed, Skipped }

    sealed record WaveLibraryResult(
        string Library,
        string Version,
        WaveVerdict Verdict,
        string? Reason,
        IReadOnlyList<string> Nupkgs,
        double BuildSeconds,
        double ValidateSeconds,
        string LogFile);

    /// <param name="Attempted">
    /// False for a package that was never pushed because an EARLIER level failed.
    /// It is listed so <c>wave-manifest.json</c> is a complete "what still needs
    /// to ship" inventory, but it must not be counted as a push failure — the
    /// push never ran.
    /// </param>
    sealed record WavePublishResult(
        string PackageId, string Version, bool Pushed, string? Error, bool Attempted = true);

    /// <summary>
    /// Everything phase 3 learned, beyond the per-package push verdicts.
    /// </summary>
    /// <param name="IndexTimeouts">
    /// Packages that were pushed but had not appeared on the flat-container index
    /// when the wave stopped waiting. NOT failures (see <see cref="WaitForIndexAsync"/>) —
    /// recorded so a later "where is my package?" question has an answer.
    /// </param>
    /// <param name="StagedUnpublished">
    /// <c>.nupkg</c> filenames sitting in <c>local-packages/</c> that this wave put
    /// there and did NOT publish. They outrank nuget.org for local restores, so the
    /// operator has to decide their fate; the wave never deletes them, because on a
    /// partially-failed real wave they are exactly what <c>--publish-partial</c>
    /// recovery re-pushes.
    /// </param>
    sealed record WavePublishOutcome(
        IReadOnlyList<WavePublishResult> Results,
        IReadOnlyList<(string Id, string Version)> IndexTimeouts,
        IReadOnlyList<string> StagedUnpublished)
    {
        public static readonly WavePublishOutcome Empty = new(
            Array.Empty<WavePublishResult>(),
            Array.Empty<(string, string)>(),
            Array.Empty<string>());
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Target
    // ═══════════════════════════════════════════════════════════════════════

    Target ReleaseWave => _ => _
        .Description(
            "Run the full local release pipeline (build → pack → sim-validate → publish) for 1..N libraries. " +
            "--wave-spec \"Lib=1.2.3,…\" | --libraries A,B --auto-version | --all --auto-version. " +
            "Dry run by DEFAULT — pass --dry-run false to publish.")
        .Executes(() => WithAbortExitCode(RunReleaseWaveAsync));

    async Task RunReleaseWaveAsync()
    {
        var dryRun = DryRun ?? true;   // publishing is opt-IN, always
        var started = DateTime.UtcNow;
        var runId = ComposeRunId(started, TryGitShortSha(), Environment.ProcessId);
        var waveDir = string.IsNullOrWhiteSpace(WaveOutput)
            ? RootDirectory / "artifacts" / "release-wave" / runId
            : (AbsolutePath)Path.GetFullPath(WaveOutput!);
        waveDir.CreateDirectory();
        (waveDir / "logs").CreateDirectory();

        Log.Information("=== ReleaseWave {RunId} ===", runId);
        Log.Information("output: {Dir}", waveDir);

        // ── Phase 0: preflight ──────────────────────────────────────────────
        var selection = await ResolveWaveSelectionAsync();
        var plan = await BuildWavePlanAsync(selection, dryRun);

        // The API key is read but NEVER logged, and never written to the
        // manifest. Only its presence is reported.
        string? apiKey = null;
        if (!dryRun)
        {
            apiKey = NuGetApiKey ?? Environment.GetEnvironmentVariable("NUGET_API_KEY");
            if (string.IsNullOrWhiteSpace(apiKey))
                throw new InvalidOperationException(
                    "ReleaseWave is not a dry run but no NuGet API key is available. " +
                    "Set NUGET_API_KEY or pass --nuget-api-key. (Re-run without --dry-run false to build only.)");
        }

        Log.Information("{Plan}", RenderWavePlan(plan));
        WavePreflight(plan, waveDir, dryRun);

        if (plan.PinBumps.Count > 0)
        {
            Log.Information("Applying {Count} pin bump(s)…", plan.PinBumps.Count);
            ApplyPinBumps(plan.PinBumps);
        }

        // ── Phase 1+2: build/pack/validate, dependency-ordered, bounded ─────
        var results = await RunWaveLibrariesAsync(plan, waveDir);

        // ── Phase 3: publish ────────────────────────────────────────────────
        var publish = await PublishWaveAsync(plan, results, waveDir, dryRun, apiKey);
        var publishResults = publish.Results;

        // ── Phase 4: report ─────────────────────────────────────────────────
        WriteWaveManifest(plan, results, publish, waveDir, runId, started, dryRun);
        Log.Information("{Summary}", RenderWaveSummary(plan, results, publish, waveDir, dryRun));

        var failed = results.Values.Count(r => r.Verdict != WaveVerdict.Success);
        // Only pushes that RAN and failed count here. Packages skipped because an
        // earlier level failed are inventory, not failures — the real failure in
        // that level already trips this.
        var pushFailed = publishResults.Count(p => p.Attempted && !p.Pushed && p.Error is not null);
        var notAttempted = publishResults.Count(p => !p.Attempted);

        if (failed > 0)
        {
            RegressionExitCodeOverride = WaveExitLibrariesFailed;
            throw new InvalidOperationException(
                $"ReleaseWave: {failed}/{plan.Libraries.Count} librar(ies) did not succeed — see {waveDir}/wave-manifest.json");
        }
        if (pushFailed > 0)
        {
            RegressionExitCodeOverride = WaveExitPublishFailed;
            throw new InvalidOperationException(
                $"ReleaseWave: {pushFailed} package push(es) failed" +
                (notAttempted > 0 ? $" ({notAttempted} further package(s) never attempted)" : "") +
                $" — see {waveDir}/wave-manifest.json. " +
                "Re-run the same wave: --skip-duplicate makes already-published packages a no-op.");
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Phase 0 — preflight
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Side-effecting checks that must pass before any build starts. Deliberately
    /// mirrors <see cref="Preflight"/>'s SDK-availability + cache-eviction shape,
    /// because a wave has the same two hazards: building against an SDK that
    /// isn't there, and re-packing a version whose stale extract is already in the
    /// NuGet global cache (where it would shadow the fresh nupkg).
    /// </summary>
    void WavePreflight(WavePlan plan, AbsolutePath waveDir, bool dryRun)
    {
        Log.Information("--- pre-flight ---");

        if (plan.GitDirty)
            Log.Warning("Working tree is DIRTY — the wave will build uncommitted changes. " +
                        "For a real release, commit first so the artifacts are reproducible from git.");

        // 1. The SDK the csprojs pin must be resolvable. local-packages/ first
        //    (a locally built SDK under test), then the global cache, then
        //    nuget.org — the same order NuGet itself would use.
        var sdkNupkg = LocalPackagesDir / $"{SdkPackageId}.{plan.SdkVersion}.nupkg";
        var globalCache = GlobalNuGetPackagesDir();
        var inGlobalCache = globalCache is not null &&
                            Directory.Exists(globalCache / SdkPackageId.ToLowerInvariant() / plan.SdkVersion);
        if (File.Exists(sdkNupkg))
            Log.Information("SDK {Id}/{Ver}: local-packages/", SdkPackageId, plan.SdkVersion);
        else if (inGlobalCache)
            Log.Information("SDK {Id}/{Ver}: NuGet global cache", SdkPackageId, plan.SdkVersion);
        else
            Log.Warning("SDK {Id}/{Ver} is in neither local-packages/ nor the global cache — " +
                        "restore will try nuget.org. If it isn't published, every build will fail.",
                SdkPackageId, plan.SdkVersion);

        // 2. Evict any stale extract of a version this wave is about to (re)pack.
        //    Without this, a dependent restoring the base could bind to the
        //    PREVIOUS build of the same version number from the global cache
        //    instead of the nupkg the wave just staged — the exact shadowing
        //    hazard RegressionValidate's preflight guards against.
        var toEvict = plan.Libraries
            .SelectMany(l => l.PackageIds.Select(id => (id, l.Version)))
            .ToArray();
        var evicted = EvictGlobalCacheEntries(toEvict);
        if (evicted.Count > 0)
            Log.Information("Evicted {Count} stale global-cache extract(s): {Entries}",
                evicted.Count, string.Join(", ", evicted));

        // 3. A booted simulator is not required up front — the fleet boots what
        //    it needs — but a simulator runtime must exist at all.
        LocalPackagesDir.CreateDirectory();

        Log.Information("publish: {Mode}", dryRun
            ? "DISABLED (dry run)"
            : "ENABLED — packages WILL be pushed to nuget.org");
        Log.Information("--- pre-flight OK ---");

        (waveDir / "wave-plan.json").WriteAllText(JsonSerializer.Serialize(new
        {
            plan.SdkVersion,
            plan.GitSha,
            plan.GitDirty,
            plan.DryRun,
            plan.MaxParallel,
            levels = plan.Levels,
            libraries = plan.Libraries.Select(l => new
            {
                l.Name, kind = l.Kind.ToString(), l.Version, l.Level,
                packages = l.PackageIds, dependsOn = l.DependsOn,
            }),
            pinBumps = plan.PinBumps.Select(b => new
            {
                b.ConsumerLibrary, b.PackageId, b.FromVersion, b.ToVersion,
                kind = b.Kind.ToString(),
                csproj = Path.GetRelativePath(RootDirectory, b.Csproj),
            }),
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Phase 1+2 — per-library pipeline, dependency-ordered
    // ═══════════════════════════════════════════════════════════════════════

    async Task<IReadOnlyDictionary<string, WaveLibraryResult>> RunWaveLibrariesAsync(
        WavePlan plan, AbsolutePath waveDir)
    {
        var maxParallel = plan.MaxParallel;
        var libCount = plan.Libraries.Count;
        var results = new ConcurrentDictionary<string, WaveLibraryResult>(StringComparer.Ordinal);

        // One TCS per library, completed in a `finally` so a failed producer can
        // never deadlock its dependents. This is the depTasks pattern from
        // Build.RegressionValidate.cs, with one deliberate difference: it
        // completes after the WHOLE library unit (pack AND sim-validate), not
        // just after packing.
        //
        // Why the whole unit: a dependent's TEST app reaches the base by
        // ProjectReference (apple-frameworks/MatterSupport/tests references
        // ../../Matter/SwiftBindings.Apple.Matter.csproj), so building the
        // dependent's test app writes into the base library's obj/ and bin/. If
        // the gate opened at "packed", that write would race the base's own
        // still-running sim build. Gating on completion costs a little
        // parallelism and buys determinism.
        var complete = plan.Libraries.ToDictionary(
            l => l.Name, _ => new TaskCompletionSource<WaveVerdict>(), StringComparer.Ordinal);

        var slots = new SemaphoreSlim(maxParallel);

        // Lease one simulator per concurrent slot. EnsureFleet adopts already
        // booted sims first, then shutdown ones, then creates fresh — and tells
        // us which it created so only those get torn down.
        SimulatorFleet? fleet = null;
        IReadOnlyList<SimulatorFleet.FleetMember> fleetMembers = Array.Empty<SimulatorFleet.FleetMember>();
        var simQueue = new ConcurrentQueue<string>();
        var fleetSize = Math.Min(maxParallel, libCount);
        try
        {
            fleet = new SimulatorFleet();
            fleetMembers = fleet.EnsureFleet(fleetSize);
            foreach (var m in fleetMembers) simQueue.Enqueue(m.Udid);
            Log.Information("Simulator fleet: {Count} booted ({Udids})",
                fleetMembers.Count, string.Join(", ", fleetMembers.Select(m => m.Udid[..8])));
        }
        catch (Exception ex)
        {
            RegressionExitCodeOverride = WaveExitAborted;
            throw new InvalidOperationException(
                $"ReleaseWave: could not lease {fleetSize} simulator(s): {ex.Message}", ex);
        }

        // Node reuse is disabled for the whole wave. Concurrent `dotnet build`
        // invocations otherwise share long-lived MSBuild worker nodes, and a
        // node reused across two libraries carries the first one's cached
        // project state into the second. This is cheap insurance; the cost is
        // one process spawn per build, which is noise next to an xcframework
        // compile.
        //
        // These are process-global, so the originals are captured and restored
        // in the `finally` below — a session that runs ReleaseWave and then
        // another target in the same Nuke process must not silently inherit the
        // wave's build settings.
        var envBackup = new[] { "MSBUILDDISABLENODEREUSE", "DOTNET_SKIP_FIRST_TIME_EXPERIENCE", "DOTNET_NOLOGO" }
            .ToDictionary(k => k, Environment.GetEnvironmentVariable, StringComparer.Ordinal);
        Environment.SetEnvironmentVariable("MSBUILDDISABLENODEREUSE", "1");
        Environment.SetEnvironmentVariable("DOTNET_SKIP_FIRST_TIME_EXPERIENCE", "1");
        Environment.SetEnvironmentVariable("DOTNET_NOLOGO", "1");

        // Route ambient Serilog output into whichever library's log file the
        // calling task belongs to, so parallel builds don't interleave. Must be
        // installed before any library task starts and removed after all finish.
        var originalLogger = Log.Logger;
        var router = new WaveLogRouter(originalLogger);
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.Sink(router)
            .CreateLogger();

        try
        {
            await Task.WhenAll(plan.Libraries.Select(lib => Task.Run(async () =>
            {
                var logFile = waveDir / "logs" / $"{lib.Name}.log";

                // Wait for every in-wave base to finish completely. If any base
                // did not succeed, this library never builds — building it would
                // either pick up a stale base package or race a half-written tree.
                var depVerdicts = await Task.WhenAll(
                    lib.DependsOn.Where(complete.ContainsKey).Select(d => complete[d].Task));
                var badDep = lib.DependsOn.Where(complete.ContainsKey)
                    .Zip(depVerdicts)
                    .FirstOrDefault(p => p.Second != WaveVerdict.Success);
                if (badDep.First is not null)
                {
                    var reason = $"dependency {badDep.First} did not succeed ({badDep.Second})";
                    Log.Warning("[{Library}] SKIPPED — {Reason}", lib.Name, reason);
                    results[lib.Name] = new WaveLibraryResult(
                        lib.Name, lib.Version, WaveVerdict.Skipped, reason,
                        Array.Empty<string>(), 0, 0, (string)logFile);
                    complete[lib.Name].TrySetResult(WaveVerdict.Skipped);
                    return;
                }

                await slots.WaitAsync();
                string? udid = null;
                var verdict = WaveVerdict.Failed;
                try
                {
                    if (!simQueue.TryDequeue(out udid))
                        throw new InvalidOperationException(
                            "No simulator available from the fleet — this indicates a leasing bug " +
                            "(the queue must hold one UDID per concurrency slot).");

                    results[lib.Name] = await RunOneWaveLibraryAsync(
                        lib, waveDir, logFile, udid, router, plan.DryRun);
                    verdict = results[lib.Name].Verdict;
                }
                catch (Exception ex)
                {
                    Log.Error("[{Library}] FAILED — {Message}", lib.Name, ex.Message);
                    results[lib.Name] = new WaveLibraryResult(
                        lib.Name, lib.Version, WaveVerdict.Failed, ex.Message,
                        Array.Empty<string>(), 0, 0, (string)logFile);
                }
                finally
                {
                    if (udid is not null) simQueue.Enqueue(udid);
                    slots.Release();
                    complete[lib.Name].TrySetResult(verdict);
                }
            })));
        }
        finally
        {
            Log.Logger = originalLogger;
            router.Dispose();

            foreach (var (key, original) in envBackup)
                Environment.SetEnvironmentVariable(key, original);

            foreach (var m in fleetMembers.Where(m => m.WasCreated))
            {
                try { fleet!.ShutdownOne(m.Udid); fleet.DeleteOne(m.Udid); }
                catch (Exception ex) { Log.Warning("Could not clean up sim {Udid}: {Message}", m.Udid, ex.Message); }
            }
        }

        return results;
    }

    /// <summary>
    /// The per-library pipeline: clean → build+pack at Release → stage → validate
    /// on the simulator. Everything it and its callees log lands in
    /// <paramref name="logFile"/>; the console gets two lines.
    /// </summary>
    async Task<WaveLibraryResult> RunOneWaveLibraryAsync(
        WaveLibraryPlan lib, AbsolutePath waveDir, AbsolutePath logFile, string udid,
        WaveLogRouter router, bool dryRun)
    {
        await using var writer = new StreamWriter(logFile, append: false) { AutoFlush = true };
        var gate = new object();
        void Sink(string line) { lock (gate) writer.WriteLine(line); }
        using var _ = router.RouteTo(Sink);

        router.Console.Information("[{Library}] → START {Version} on sim {Udid}",
            lib.Name, lib.Version, udid[..8]);
        Sink($"=== ReleaseWave {lib.Name} {lib.Version} ===");

        var buildSw = Stopwatch.StartNew();

        // 1. Wipe every bin/ and obj/ under the library root, RECURSIVELY.
        //    Multi-product libraries keep their products in subdirectories
        //    (libraries/Stripe/StripeCore/…), so a root-only wipe would leave
        //    exactly the stale NativeReference framework slices CLAUDE.md warns
        //    about after a version bump.
        var (_, libDir) = ResolveLibrary(lib.Name);
        WipeBuildOutput(libDir, Sink);

        // 2. Build at -c Release and pack every non-internal product at the
        //    assigned version. Each library packs into its OWN directory:
        //    GenerateReleaseManifest globs *.nupkg from the output dir, so a
        //    shared directory would make every library's release-manifest.json
        //    list every other library's packages and overwrite the previous one.
        //
        //    The directory is emptied first. --wave-output can point at a reused
        //    path, and everything downstream (staging, publish,
        //    GenerateReleaseManifest) globs *.nupkg out of it — so a leftover
        //    nupkg from a previous run would be staged into the local feed and
        //    pushed to nuget.org as if this wave had produced it.
        var packDir = waveDir / "packages" / lib.Name;
        if (Directory.Exists(packDir))
        {
            var stale = packDir.GlobFiles("*.nupkg").ToList();
            if (stale.Count > 0)
                Sink($"clearing {stale.Count} pre-existing nupkg(s) from {packDir}: " +
                     string.Join(", ", stale.Select(p => p.Name)));
            Directory.Delete(packDir, recursive: true);
        }
        packDir.CreateDirectory();

        BuildAndPackReleaseForLibrary(lib.Name, lib.Version, packDir, dryRun, onLine: Sink);
        buildSw.Stop();

        // Belt and braces on top of the wipe: accept ONLY the exact
        // id+version files this library was planned to produce. A stray file —
        // from a concurrent writer, a hand-copy, or a pack that emitted an
        // unexpected id — can then never be staged or published.
        var expected = WaveExpectedNupkgNames(lib);
        var produced = packDir.GlobFiles("*.nupkg").OrderBy(p => p.Name, StringComparer.Ordinal).ToList();
        var nupkgs = produced.Where(p => expected.Contains(p.Name)).ToList();

        var unexpected = produced.Where(p => !expected.Contains(p.Name)).ToList();
        if (unexpected.Count > 0)
            Sink($"WARNING: ignoring {unexpected.Count} unplanned nupkg(s) in {packDir}: " +
                 string.Join(", ", unexpected.Select(p => p.Name)));

        if (nupkgs.Count == 0)
            throw new InvalidOperationException(
                $"{lib.Name}: pack produced none of the planned package(s) in {packDir}. " +
                $"Expected: {string.Join(", ", expected.OrderBy(n => n, StringComparer.Ordinal))}. " +
                $"Found: {(produced.Count == 0 ? "(nothing)" : string.Join(", ", produced.Select(p => p.Name)))}");

        // 3. Stage into local-packages/ — THIS is the NU1102 fix. NuGet.config
        //    lists `local` ahead of nuget.org, so a dependent that starts after
        //    this point restores the base from disk and never consults the
        //    nuget.org index. Staging happens before the TCS completes, so the
        //    ordering guarantee and the feed guarantee are the same guarantee.
        //
        //    Rollback is a full RESTORE, not just a delete. Overwriting a
        //    pre-existing nupkg of the same id+version destroys it just as
        //    surely as creating a new one poisons the feed — a second dry run of
        //    the same version is the common case — so the previous bytes are
        //    parked in a sidecar and put back if this library fails.
        LocalPackagesDir.CreateDirectory();
        var staged = new List<(AbsolutePath Dest, AbsolutePath? Backup)>();

        // local-packages/ is listed AHEAD of nuget.org in NuGet.config, so an
        // unvalidated nupkg left behind by a red wave would silently outrank the
        // real package for every later restore on this machine. Same-wave
        // dependents are already safe (they gate on full completion); this is
        // about not poisoning the feed for later processes.
        void UnstageOnFailure()
        {
            foreach (var (dest, backup) in staged)
            {
                try
                {
                    if (backup is not null)
                    {
                        File.Copy(backup, dest, overwrite: true);
                        File.Delete(backup);
                        Sink($"restored previous local-packages/{dest.Name}");
                    }
                    else if (File.Exists(dest))
                    {
                        File.Delete(dest);
                        Sink($"un-staged local-packages/{dest.Name}");
                    }
                }
                catch (Exception ex) { Sink($"WARNING: could not un-stage {dest.Name}: {ex.Message}"); }
            }
        }

        // The staging loop is itself inside the guarded region: a copy that
        // throws half-way through must not leave the earlier copies behind.
        try
        {
            foreach (var nupkg in nupkgs)
            {
                var dest = LocalPackagesDir / nupkg.Name;
                AbsolutePath? backup = null;
                if (File.Exists(dest))
                {
                    backup = LocalPackagesDir / $".{nupkg.Name}.wave-backup";
                    File.Copy(dest, backup, overwrite: true);
                }
                staged.Add((dest, backup));
                File.Copy(nupkg, dest, overwrite: true);
                Sink($"staged → local-packages/{nupkg.Name}" + (backup is null ? "" : " (previous copy backed up)"));
            }
        }
        catch
        {
            UnstageOnFailure();
            throw;
        }

        // 4. Sim-validate, same pair RegressionValidate's ios-sim cell uses.
        //    Not RunCiSimTest: that target hardcodes its own fleet handling and
        //    reads the global --timeout, and the wave needs a per-library
        //    timeout with a fleet lease it already holds. The one thing worth
        //    keeping from RunCiSimTest is retry-on-timeout, which is below.
        var validateSw = Stopwatch.StartNew();
        string? validationNote = null;
        try
        {
            var timeout = Math.Max(Timeout, WaveTimeoutOverrides.TryGetValue(lib.Name, out var o) ? o : 0);
            var (testDir, _, _) = ResolveTestNames(lib.Name);

            if (!Directory.Exists(testDir))
            {
                validationNote = "no test app — validation skipped";
                Sink($"WARNING: {validationNote}");
            }
            else
            {
                BuildTestAppSimulator(lib.Name, testDir, "ios", Sink);

                var rid = ResolveRid("iossimulator-arm64");
                var result = ValidateSimFor(lib.Name, timeout, udid, rid, Sink);

                // Retry once, and only on a timeout — the same rule
                // RunInnerTestWithRetry applies (a real assertion failure is not
                // flaky and must not be retried into a false pass).
                if (result.Status == TestRunStatus.Timeout)
                {
                    Sink("Test timed out — retrying once");
                    router.Console.Warning("[{Library}] test timed out, retrying once", lib.Name);
                    result = ValidateSimFor(lib.Name, timeout, udid, rid, Sink);
                }

                if (result.Status != TestRunStatus.Success)
                    throw new InvalidOperationException(
                        $"{lib.Name}: simulator validation {result.Status}" +
                        (result.Reason is null ? "" : $" ({result.Reason})"));
            }
        }
        catch
        {
            UnstageOnFailure();
            throw;
        }
        validateSw.Stop();

        // The library is good: the staged nupkgs stay, so the rollback sidecars
        // are dead weight. Leaving them would litter local-packages/ with a
        // `.…wave-backup` per re-run (harmless to NuGet, which globs *.nupkg,
        // but confusing to a human reading the feed).
        foreach (var (_, backup) in staged.Where(s => s.Backup is not null))
        {
            try { File.Delete(backup!); }
            catch (Exception ex) { Sink($"WARNING: could not remove backup {backup!.Name}: {ex.Message}"); }
        }

        router.Console.Information(
            "[{Library}] ← OK {Version} — {Packages} package(s), build {Build:F0}s, validate {Validate:F0}s{Note}",
            lib.Name, lib.Version, nupkgs.Count,
            buildSw.Elapsed.TotalSeconds, validateSw.Elapsed.TotalSeconds,
            validationNote is null ? "" : $" ({validationNote})");

        return new WaveLibraryResult(
            lib.Name, lib.Version, WaveVerdict.Success, validationNote,
            nupkgs.Select(p => p.Name).ToList(),
            buildSw.Elapsed.TotalSeconds, validateSw.Elapsed.TotalSeconds, (string)logFile);
    }

    /// <summary>
    /// Delete every <c>bin/</c> and <c>obj/</c> under a library root, at any
    /// depth, skipping <c>tests/</c>-external trees only if they are outside the
    /// root (they aren't — test apps are co-located and their stale output is
    /// just as dangerous).
    /// </summary>
    static void WipeBuildOutput(AbsolutePath libraryDir, Action<string> sink)
    {
        var wiped = 0;
        foreach (var dir in Directory.EnumerateDirectories(libraryDir, "*", SearchOption.AllDirectories)
                     .Where(d => Path.GetFileName(d) is "bin" or "obj")
                     .OrderByDescending(d => d.Length)) // deepest first: never walk into a deleted parent
        {
            if (!Directory.Exists(dir)) continue;
            try { Directory.Delete(dir, recursive: true); wiped++; }
            catch (Exception ex) { sink($"WARNING: could not delete {dir}: {ex.Message}"); }
        }
        sink($"wiped {wiped} bin/obj director(ies) under {libraryDir}");
    }

    /// <summary>
    /// The exact <c>.nupkg</c> filenames this library is planned to produce —
    /// <c>&lt;PackageId&gt;.&lt;Version&gt;.nupkg</c>, the naming
    /// <c>dotnet pack</c> guarantees and <see cref="Pack"/> already relies on.
    /// Used as an allow-list so nothing outside the plan can be staged or pushed.
    /// </summary>
    static HashSet<string> WaveExpectedNupkgNames(WaveLibraryPlan lib) =>
        new(lib.PackageIds.Select(id => $"{id}.{lib.Version}.nupkg"), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The nupkgs in this library's output directory that the plan actually calls
    /// for. A bare <c>*.nupkg</c> glob would also pick up leftovers from a previous
    /// run into the same <c>--wave-output</c>; filtering by
    /// <see cref="WaveExpectedNupkgNames"/> means a stray file can be staged or
    /// pushed by no code path at all.
    /// </summary>
    static IReadOnlyList<AbsolutePath> WavePlannedNupkgs(WaveLibraryPlan lib, AbsolutePath waveDir)
    {
        var dir = waveDir / "packages" / lib.Name;
        if (!Directory.Exists(dir)) return Array.Empty<AbsolutePath>();
        var expected = WaveExpectedNupkgNames(lib);
        return dir.GlobFiles("*.nupkg")
            .Where(p => expected.Contains(p.Name))
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .ToList();
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Phase 3 — publish
    // ═══════════════════════════════════════════════════════════════════════

    async Task<WavePublishOutcome> PublishWaveAsync(
        WavePlan plan,
        IReadOnlyDictionary<string, WaveLibraryResult> results,
        AbsolutePath waveDir,
        bool dryRun,
        string? apiKey)
    {
        // Every nupkg this wave copied into local-packages/. Staging is how the
        // NU1102 fix works, so these exist whether or not a publish follows —
        // and when no publish follows they are a live hazard worth naming.
        var stagedLocally = results.Values
            .Where(r => r.Verdict == WaveVerdict.Success)
            .SelectMany(r => r.Nupkgs)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        if (dryRun)
        {
            Log.Information("DRY RUN — skipping publish. {Count} package(s) are staged under {Dir}.",
                results.Values.Sum(r => r.Nupkgs.Count), waveDir / "packages");

            // "Dry run" means "no publish", NOT "no side effects". Say so out
            // loud: a successful dry run leaves versions in the preferred local
            // feed that do not exist on nuget.org, so a later restore on this
            // machine can resolve a version nobody can download.
            WarnStagedUnpublished(stagedLocally, "this was a dry run");
            return WavePublishOutcome.Empty with { StagedUnpublished = stagedLocally };
        }

        var failed = results.Values.Where(r => r.Verdict != WaveVerdict.Success).ToList();
        if (failed.Count > 0 && !PublishPartial)
        {
            Log.Error("PUBLISH SKIPPED — {Count} librar(ies) failed: {Libs}. " +
                      "Pass --publish-partial to push the successful ones anyway.",
                failed.Count, string.Join(", ", failed.Select(f => f.Library)));

            // The successful libraries still packed AND staged before the wave
            // gave up, so a REAL wave that publishes nothing leaves the same
            // local-feed hazard a dry run does — and silently, until now. These
            // files are deliberately NOT deleted: they are precisely what a
            // `--publish-partial` re-run pushes, and re-packing them costs a full
            // build + sim validation.
            WarnStagedUnpublished(stagedLocally,
                $"publishing was skipped because {failed.Count} librar(ies) failed");
            return WavePublishOutcome.Empty with { StagedUnpublished = stagedLocally };
        }

        var indexTimeouts = new List<(string Id, string Version)>();

        // Serial first-run pre-warm before ANY parallel push. Concurrent cold
        // `dotnet` processes race on the NuGet-Migrations named mutex, and the
        // loser dies with an IOException before it pushes — that's how the
        // umbrella SwiftBindings.Stripe package got silently dropped from a wave
        // while its 13 siblings published (CLAUDE.md, Release Process).
        Log.Information("Pre-warming dotnet first-run state (NuGet-Migrations mutex race guard)…");
        RunDotnet(new[] { "nuget", "locals", "all", "--list" }, onLine: null);

        var published = new List<WavePublishResult>();
        var libPlans = plan.Libraries.ToDictionary(l => l.Name, StringComparer.Ordinal);
        var allRefs = DiscoverCrossPackageRefs();

        // Publish level by level. Within a level nothing depends on anything
        // else, so those pushes are safe to parallelize; across levels a base's
        // packages must be live before its dependents' are announced.
        var levelIndex = -1;
        foreach (var level in plan.Levels)
        {
            levelIndex++;
            var levelLibs = level
                .Where(l => results.TryGetValue(l, out var r) && r.Verdict == WaveVerdict.Success)
                .ToList();
            if (levelLibs.Count == 0) continue;

            // Within one level, order each library's OWN packages by their
            // intra-library sibling refs and push tier by tier. Libraries are
            // independent of each other here, so their tier 0s are all pushed
            // together, then all their tier 1s, and so on — the cross-library
            // ordering is already handled by the enclosing level loop.
            var perLibTiers = levelLibs.ToDictionary(
                l => l,
                l => ComputeIntraLibraryPublishTiers(l, libPlans[l].PackageIds, allRefs),
                StringComparer.Ordinal);
            var tierCount = perLibTiers.Values.Max(t => t.Count);

            var levelPlanned = levelLibs.Sum(l => WavePlannedNupkgs(libPlans[l], waveDir).Count);
            Log.Information("Publishing level [{Libs}] — {Count} package(s) in {Tiers} tier(s)…",
                string.Join(", ", levelLibs), levelPlanned, tierCount);

            var levelResults = new ConcurrentBag<WavePublishResult>();
            var tierFailed = false;

            for (var tier = 0; tier < tierCount && !tierFailed; tier++)
            {
                // Only files the plan says this library produces are eligible —
                // the same allow-list staging uses, so a stray nupkg in the
                // output directory can never be pushed to nuget.org.
                var tierNupkgs = levelLibs
                    .SelectMany(l =>
                    {
                        var tiers = perLibTiers[l];
                        if (tier >= tiers.Count) return Enumerable.Empty<AbsolutePath>();
                        var idsInTier = new HashSet<string>(tiers[tier], StringComparer.OrdinalIgnoreCase);
                        return WavePlannedNupkgs(libPlans[l], waveDir)
                            .Where(p => idsInTier.Contains(ExtractPackageId(p.Name)));
                    })
                    .OrderBy(p => p.Name, StringComparer.Ordinal)
                    .ToList();
                if (tierNupkgs.Count == 0) continue;

                if (tierCount > 1)
                    Log.Information("  tier {Tier}/{Total} — {Count} package(s): {Ids}",
                        tier + 1, tierCount, tierNupkgs.Count,
                        string.Join(", ", tierNupkgs.Select(p => ExtractPackageId(p.Name))));

                var tierResults = new ConcurrentBag<WavePublishResult>();
                await Parallel.ForEachAsync(
                    tierNupkgs,
                    new ParallelOptions { MaxDegreeOfParallelism = 5 },
                    (nupkg, _) =>
                    {
                        var id = ExtractPackageId(nupkg.Name);
                        var version = results[levelLibs.First(l =>
                            File.Exists(waveDir / "packages" / l / nupkg.Name))].Version;
                        try
                        {
                            // --skip-duplicate: re-running a wave after a partial
                            // failure must be a no-op for what already shipped.
                            // `secrets` scrubs the key from RunDotnet's argv echo
                            // and from anything the child prints. Without it the
                            // very first real publish would put the credential in
                            // the terminal scrollback.
                            var exit = RunDotnet(new[]
                            {
                                "nuget", "push", (string)nupkg,
                                "--source", "https://api.nuget.org/v3/index.json",
                                "--api-key", apiKey!,
                                "--skip-duplicate",
                            }, onLine: null, secrets: new[] { apiKey! });
                            tierResults.Add(exit == 0
                                ? new WavePublishResult(id, version, true, null)
                                : new WavePublishResult(id, version, false, $"dotnet nuget push exited {exit}"));
                        }
                        catch (Exception ex)
                        {
                            tierResults.Add(new WavePublishResult(id, version, false, ex.Message));
                        }
                        return ValueTask.CompletedTask;
                    });

                foreach (var r in tierResults) levelResults.Add(r);

                var failedInTier = tierResults.Where(r => !r.Pushed).ToList();
                if (failedInTier.Count > 0)
                {
                    tierFailed = true;
                    Log.Error("{Count} push(es) failed in tier {Tier}: {Ids} — " +
                              "not publishing packages that depend on them.",
                        failedInTier.Count, tier + 1, string.Join(", ", failedInTier.Select(f => f.PackageId)));

                    // Later tiers of the SAME level are now skipped too: they
                    // declare a dependency on something that did not go live.
                    var skippedTiers = levelLibs
                        .SelectMany(l =>
                        {
                            var tiers = perLibTiers[l];
                            var laterIds = new HashSet<string>(
                                tiers.Skip(tier + 1).SelectMany(t => t), StringComparer.OrdinalIgnoreCase);
                            return WavePlannedNupkgs(libPlans[l], waveDir)
                                .Where(p => laterIds.Contains(ExtractPackageId(p.Name)))
                                .Select(p => new WavePublishResult(
                                    ExtractPackageId(p.Name), results[l].Version, false,
                                    $"not attempted — tier {tier + 1} of {l} failed", Attempted: false));
                        })
                        .OrderBy(p => p.PackageId, StringComparer.Ordinal)
                        .ToList();
                    foreach (var r in skippedTiers) levelResults.Add(r);
                    if (skippedTiers.Count > 0)
                        Log.Error("{Count} package(s) in later tiers were not attempted: {Ids}",
                            skippedTiers.Count, string.Join(", ", skippedTiers.Select(p => p.PackageId)));
                    break;
                }

                // A base must be visible before the tier that declares a
                // dependency on it is pushed — same reason as between levels.
                var hasLaterTier = tier + 1 < tierCount;
                if (hasLaterTier)
                    indexTimeouts.AddRange(
                        await WaitForIndexAsync(tierResults.Select(r => (r.PackageId, r.Version)).ToList()));
            }

            published.AddRange(levelResults);

            var levelFailed = levelResults.Where(r => !r.Pushed).ToList();
            if (levelFailed.Count > 0)
            {
                Log.Error("{Count} push(es) failed or were skipped in this level: {Ids} — " +
                          "not publishing dependent levels.",
                    levelFailed.Count, string.Join(", ", levelFailed.Select(f => f.PackageId)));

                // Record every package that will now never be attempted, so
                // wave-manifest.json is a COMPLETE "what still needs to ship"
                // inventory rather than only a log of what was tried. Without
                // this, a multi-level wave that dies at level 0 leaves the
                // operator to reconstruct the remainder by hand — the exact
                // failure mode that made the GHA partial-publish recovery
                // painful for a 14-package library like Stripe.
                var unattempted = plan.Levels.Skip(levelIndex + 1).SelectMany(l => l)
                    .Where(l => results.TryGetValue(l, out var r) && r.Verdict == WaveVerdict.Success)
                    .SelectMany(l => WavePlannedNupkgs(libPlans[l], waveDir)
                        .Select(p => new WavePublishResult(
                            ExtractPackageId(p.Name), results[l].Version, false,
                            "not attempted — an earlier level failed", Attempted: false)))
                    .OrderBy(p => p.PackageId, StringComparer.Ordinal)
                    .ToList();
                if (unattempted.Count > 0)
                {
                    published.AddRange(unattempted);
                    Log.Error("{Count} package(s) in later levels were not attempted: {Ids}",
                        unattempted.Count, string.Join(", ", unattempted.Select(p => p.PackageId)));
                }
                break;
            }

            // Wait for the index to catch up before publishing dependents.
            // Build-time NU1102 is already impossible (dependents restored from
            // local-packages/), so this only protects CONSUMERS who install the
            // dependent before the base is visible. Index lag is normal, not a
            // failure — warn and continue.
            var isLastLevel = ReferenceEquals(level, plan.Levels[^1]);
            if (!isLastLevel)
                indexTimeouts.AddRange(await WaitForIndexAsync(
                    levelResults.Where(r => r.Pushed).Select(r => (r.PackageId, r.Version)).ToList()));
        }

        // Anything this wave staged locally but never got onto nuget.org is still
        // shadowing the real feed for local restores. Name it — the operator needs
        // to either re-run the wave (--skip-duplicate makes the published half a
        // no-op) or delete the files.
        var pushedNames = new HashSet<string>(
            published.Where(p => p.Pushed).Select(p => $"{p.PackageId}.{p.Version}.nupkg"),
            StringComparer.OrdinalIgnoreCase);
        var stagedUnpublished = stagedLocally.Where(n => !pushedNames.Contains(n)).ToList();
        if (stagedUnpublished.Count > 0)
            WarnStagedUnpublished(stagedUnpublished, "their push failed or was never attempted");

        return new WavePublishOutcome(published, indexTimeouts, stagedUnpublished);
    }

    /// <summary>
    /// One loud, uniform end-of-run warning for nupkgs the wave put in
    /// <c>local-packages/</c> without publishing them. Never deletes: on a
    /// partially-failed real wave these are the recovery artifacts.
    /// </summary>
    static void WarnStagedUnpublished(IReadOnlyList<string> staged, string why)
    {
        if (staged.Count == 0) return;
        Log.Warning(
            "local-packages/ was MODIFIED — {Count} nupkg(s) staged there and NOT published ({Why}): {Names}. " +
            "They now outrank nuget.org for local restores. Re-run the wave to ship them " +
            "(--skip-duplicate makes anything already published a no-op), or delete them.",
            staged.Count, why, string.Join(", ", staged));
    }

    /// <summary>
    /// Poll the flat-container index until every (id, version) is visible, or
    /// ~15 minutes elapse. Returns whatever was still invisible at the deadline.
    /// </summary>
    /// <remarks>
    /// A timeout WARNS and proceeds rather than failing, and that is deliberate:
    /// a successful push that hasn't propagated is the documented normal case
    /// (CLAUDE.md, "Index lag is not a failure"), propagation can take 10–15
    /// minutes, and the only downside of continuing is a consumer who installs
    /// the dependent in that window. Blocking the wave would strand publishable
    /// packages over a condition that resolves itself, and re-running the wave to
    /// finish the job is cheap because every push is `--skip-duplicate`. The
    /// timeout is not silent: it is warned on the console AND recorded per package
    /// in <c>wave-manifest.json</c> so it can be correlated after the fact.
    /// </remarks>
    static async Task<IReadOnlyList<(string Id, string Version)>> WaitForIndexAsync(
        IReadOnlyList<(string Id, string Version)> packages)
    {
        if (packages.Count == 0) return Array.Empty<(string, string)>();
        var deadline = DateTime.UtcNow.AddMinutes(15);
        var pending = packages.ToList();
        Log.Information("Waiting for {Count} package(s) to appear on the nuget.org index…", pending.Count);

        while (pending.Count > 0 && DateTime.UtcNow < deadline)
        {
            var stillPending = new List<(string, string)>();
            foreach (var (id, version) in pending)
            {
                bool live;
                try { live = await IsPublishedAsync(id, version); }
                catch { live = false; }        // transient index read — retry next round
                if (!live) stillPending.Add((id, version));
            }
            pending = stillPending;
            if (pending.Count == 0) break;
            await Task.Delay(TimeSpan.FromSeconds(20));
        }

        if (pending.Count == 0)
            Log.Information("All packages visible on the index.");
        else
            Log.Warning("{Count} package(s) not yet visible after 15 min: {Ids}. " +
                        "Index propagation lag is normal and is NOT a publish failure — " +
                        "confirm via the push output above rather than the index. " +
                        "Continuing; recorded under indexTimeouts in wave-manifest.json.",
                pending.Count, string.Join(", ", pending.Select(p => $"{p.Item1} {p.Item2}")));

        return pending;
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Phase 4 — report
    // ═══════════════════════════════════════════════════════════════════════

    void WriteWaveManifest(
        WavePlan plan,
        IReadOnlyDictionary<string, WaveLibraryResult> results,
        WavePublishOutcome publish,
        AbsolutePath waveDir,
        string runId,
        DateTime started,
        bool dryRun)
    {
        var publishResults = publish.Results;
        var manifest = new
        {
            runId,
            startedUtc = started.ToString("O"),
            finishedUtc = DateTime.UtcNow.ToString("O"),
            durationSeconds = (DateTime.UtcNow - started).TotalSeconds,
            dryRun,
            publishPartial = PublishPartial,
            maxParallel = plan.MaxParallel,
            sdkVersion = plan.SdkVersion,
            git = new { sha = plan.GitSha, dirty = plan.GitDirty },
            libraries = plan.Libraries.Select(l =>
            {
                results.TryGetValue(l.Name, out var r);
                return new
                {
                    library = l.Name,
                    kind = l.Kind.ToString(),
                    version = l.Version,
                    level = l.Level,
                    dependsOn = l.DependsOn,
                    packageIds = l.PackageIds,
                    verdict = (r?.Verdict ?? WaveVerdict.Skipped).ToString(),
                    reason = r?.Reason,
                    nupkgs = r?.Nupkgs ?? Array.Empty<string>(),
                    buildSeconds = r?.BuildSeconds ?? 0,
                    validateSeconds = r?.ValidateSeconds ?? 0,
                    logFile = r is null ? null : Path.GetRelativePath(waveDir, r.LogFile),
                    packagesDir = $"packages/{l.Name}",
                };
            }),
            pinBumps = plan.PinBumps.Select(b => new
            {
                b.ConsumerLibrary, b.PackageId, b.FromVersion, b.ToVersion,
                kind = b.Kind.ToString(),
                csproj = Path.GetRelativePath(RootDirectory, b.Csproj),
            }),
            publish = publishResults.Select(p => new { p.PackageId, p.Version, p.Pushed, p.Attempted, p.Error }),
            // Pushed, but not visible on the flat-container index when the wave
            // stopped waiting. By design NOT a failure (index lag is normal) —
            // recorded so "where is my package?" has an answer after the fact.
            indexTimeouts = publish.IndexTimeouts.Select(t => new { packageId = t.Id, version = t.Version }),
            // In local-packages/, put there by this wave, never published. They
            // outrank nuget.org for local restores until re-run or deleted.
            stagedUnpublished = publish.StagedUnpublished,
        };

        (waveDir / "wave-manifest.json").WriteAllText(
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
    }

    static string RenderWaveSummary(
        WavePlan plan,
        IReadOnlyDictionary<string, WaveLibraryResult> results,
        WavePublishOutcome publish,
        AbsolutePath waveDir,
        bool dryRun)
    {
        var publishResults = publish.Results;
        var sb = new System.Text.StringBuilder();
        sb.AppendLine();
        sb.AppendLine("=== ReleaseWave summary ===");

        var nameWidth = Math.Max(12, plan.Libraries.Max(l => l.Name.Length) + 2);
        sb.AppendLine($"  {"LIBRARY".PadRight(nameWidth)}{"VERSION".PadRight(12)}{"VERDICT".PadRight(10)}" +
                      $"{"PKGS".PadRight(6)}{"BUILD".PadRight(9)}{"VALIDATE".PadRight(10)}NOTE");
        sb.AppendLine($"  {new string('─', nameWidth + 60)}");

        foreach (var lib in plan.Libraries)
        {
            results.TryGetValue(lib.Name, out var r);
            var verdict = (r?.Verdict ?? WaveVerdict.Skipped).ToString().ToUpperInvariant();
            sb.AppendLine(
                $"  {lib.Name.PadRight(nameWidth)}{lib.Version.PadRight(12)}{verdict.PadRight(10)}" +
                $"{(r?.Nupkgs.Count ?? 0).ToString().PadRight(6)}" +
                $"{$"{r?.BuildSeconds ?? 0:F0}s".PadRight(9)}" +
                $"{$"{r?.ValidateSeconds ?? 0:F0}s".PadRight(10)}" +
                $"{r?.Reason ?? ""}");
        }

        var ok = results.Values.Count(r => r.Verdict == WaveVerdict.Success);
        sb.AppendLine();
        sb.AppendLine($"  {ok}/{plan.Libraries.Count} librar(ies) succeeded.");

        if (dryRun)
            sb.AppendLine($"  DRY RUN — nothing published. Packages: {waveDir / "packages"}");
        else
        {
            var pushed = publishResults.Count(p => p.Pushed);
            sb.AppendLine($"  Published {pushed}/{publishResults.Count} package(s) to nuget.org.");
            foreach (var f in publishResults.Where(p => !p.Pushed))
                sb.AppendLine($"    PUSH FAILED: {f.PackageId} {f.Version} — {f.Error}");
            foreach (var t in publish.IndexTimeouts)
                sb.AppendLine($"    INDEX LAG (not a failure): {t.Id} {t.Version} not visible yet");
        }

        if (publish.StagedUnpublished.Count > 0)
        {
            sb.AppendLine($"  {publish.StagedUnpublished.Count} nupkg(s) STAGED IN local-packages/ but NOT published:");
            foreach (var n in publish.StagedUnpublished)
                sb.AppendLine($"    {n}");
            sb.AppendLine("    They outrank nuget.org for local restores. Re-run the wave to ship them, or delete them.");
        }

        if (plan.PinBumps.Count > 0)
            sb.AppendLine($"  {plan.PinBumps.Count} csproj pin(s) were rewritten — review `git diff` and commit.");

        sb.AppendLine($"  Manifest: {waveDir / "wave-manifest.json"}");
        sb.AppendLine($"  Logs:     {waveDir / "logs"}");
        return sb.ToString();
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Ambient log routing
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A Serilog sink that sends each event to whichever per-library file the
    /// CURRENT async flow has claimed, falling back to the console logger when
    /// no library owns the flow.
    ///
    /// <para>Why this exists: the wave calls deep helpers
    /// (<c>BuildXcframeworkForLibrary</c>, <c>InjectProjectRefsForLibrary</c>, the
    /// SDK two-pass build) that log through the static <c>Log</c> class rather
    /// than an injected sink. Threading an <c>Action&lt;string&gt;</c> through all
    /// of them would be a large, invasive change to code that several other
    /// targets depend on. An ambient route keyed by
    /// <see cref="AsyncLocal{T}"/> gets the same result — no interleaved
    /// console output — without touching those call chains.</para>
    ///
    /// <para>The isolation is real because each library runs inside its own
    /// <c>Task.Run</c>, which forks the ExecutionContext: a route set inside one
    /// library's task is invisible to every other task and to the caller.</para>
    /// </summary>
    internal sealed class WaveLogRouter : ILogEventSink, IDisposable
    {
        static readonly AsyncLocal<Action<string>?> Route = new();
        readonly ILogger _console;

        public WaveLogRouter(ILogger console) => _console = console;

        /// <summary>The unrouted logger, for the handful of lines that must reach
        /// the operator's terminal (START / OK / FAILED per library).</summary>
        public ILogger Console => _console;

        /// <summary>Claim the current async flow for <paramref name="sink"/>.
        /// Dispose restores whatever was set before.</summary>
        public IDisposable RouteTo(Action<string> sink)
        {
            var previous = Route.Value;
            Route.Value = sink;
            return new Restore(() => Route.Value = previous);
        }

        public void Emit(LogEvent logEvent)
        {
            var sink = Route.Value;
            if (sink is null) { _console.Write(logEvent); return; }

            var text = logEvent.RenderMessage();
            if (logEvent.Level >= LogEventLevel.Warning)
                text = $"[{logEvent.Level}] {text}";
            if (logEvent.Exception is not null)
                text += Environment.NewLine + logEvent.Exception;

            try { sink(text); }
            catch { /* a closed per-library writer must never abort the wave */ }
        }

        public void Dispose() => Route.Value = null;

        sealed class Restore : IDisposable
        {
            readonly Action _onDispose;
            public Restore(Action onDispose) => _onDispose = onDispose;
            public void Dispose() => _onDispose();
        }
    }
}
