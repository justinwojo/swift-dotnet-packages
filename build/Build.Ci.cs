using System.Text.Json;
using System.Text.RegularExpressions;
using Nuke.Common;
using Nuke.Common.IO;
using Serilog;
using SwiftBindings.Build.Helpers;
using SwiftBindings.Build.Models;

partial class Build
{
    [Parameter("Base SHA for ListChangedLibraries diff")]
    readonly string? BaseSha;

    [Parameter("Head SHA for ListChangedLibraries diff")]
    readonly string? HeadSha;

    [Parameter("ListChangedLibraries: ignore the diff and return every library")]
    readonly bool All;

    [Parameter("ListChangedLibraries: emit GHA matrix JSON instead of newline-delimited names")]
    readonly bool Json;

    /// <summary>
    /// Compute which libraries to build for a CI run. Replaces the inline
    /// bash detection at <c>ci.yml:27–97</c> of the post-Session-3 file with a
    /// single Nuke target. Two modes:
    /// <list type="bullet">
    ///   <item><c>--all</c> — return every library that has a valid
    ///         <c>library.json</c> (manual <c>workflow_dispatch</c> path).</item>
    ///   <item><c>--base-sha &lt;X&gt; --head-sha &lt;Y&gt;</c> — diff-based path. If
    ///         any shared-infra path changed (<c>scripts/</c>, <c>templates/</c>,
    ///         <c>Directory.Build.props</c>, <c>global.json</c>,
    ///         <c>.github/workflows/ci.yml</c>, plus the Nuke harness paths
    ///         (<c>build/</c>, <c>.nuke/</c>, <c>.config/dotnet-tools.json</c>,
    ///         <c>build.sh</c>)), return all libraries — same expansion
    ///         semantics as <c>ci.yml:45–52</c>.
    ///         Otherwise extract <c>^(libraries|apple-frameworks)/&lt;name&gt;</c>,
    ///         dedupe, and filter to entries that actually exist on disk.</item>
    /// </list>
    ///
    /// <para>Output: with <c>--json</c>, prints the GHA matrix JSON
    /// (<c>{"include":[{"library":"Nuke"}, ...]}</c>) on stdout. Without
    /// <c>--json</c>, prints one library name per line. Both forms also
    /// emit a brief summary to the Nuke log on stderr so CI logs stay
    /// human-readable.</para>
    /// </summary>
    Target ListChangedLibraries => _ => _
        .Description("Compute the CI matrix of libraries to build (--all | --base-sha X --head-sha Y) [--json]")
        .Executes(() =>
        {
            var libs = ResolveChangedLibraries();
            EmitMatrix(libs);
        });

    /// <summary>
    /// A library entry with its resolved kind for the CI matrix.
    /// </summary>
    record MatrixEntry(string Library, LibraryKind Kind)
    {
        public string KindLabel => Kind switch
        {
            LibraryKind.ThirdParty => "third-party",
            LibraryKind.AppleFramework => "apple-framework",
            _ => "unknown",
        };
    }

    /// <summary>
    /// Pure resolver for <see cref="ListChangedLibraries"/>. Split out so the
    /// logic is unit-testable and so dependent targets can call it directly
    /// without re-parsing parameters.
    /// </summary>
    IReadOnlyList<MatrixEntry> ResolveChangedLibraries()
    {
        var allEntries = DiscoverAllEntries();

        if (All || (string.IsNullOrEmpty(BaseSha) && string.IsNullOrEmpty(HeadSha)))
        {
            Log.Information("ListChangedLibraries: returning all {Count} entries (--all or no SHAs)", allEntries.Count);
            return allEntries;
        }

        if (string.IsNullOrEmpty(BaseSha) || string.IsNullOrEmpty(HeadSha))
            throw new InvalidOperationException(
                "ListChangedLibraries requires both --base-sha and --head-sha (or --all).");

        var changed = GitDiffNameOnly(BaseSha!, HeadSha!);
        Log.Information("ListChangedLibraries: {Count} changed file(s)", changed.Count);
        foreach (var f in changed)
            Log.Information("  - {File}", f);

        // Shared-infra paths — files whose changes invalidate every entry
        // and force a full rebuild.
        bool IsSharedInfra(string path) =>
            path.StartsWith("scripts/", StringComparison.Ordinal)
            || path.StartsWith("templates/", StringComparison.Ordinal)
            || path.StartsWith("build/", StringComparison.Ordinal)
            || path.StartsWith(".nuke/", StringComparison.Ordinal)
            || path == ".config/dotnet-tools.json"
            || path == "build.sh"
            || path == "Directory.Build.props"
            || path == "Directory.Build.tests.props"
            || path == "libraries/Directory.Build.props"
            || path == "apple-frameworks/Directory.Build.props"
            || path == "global.json"
            || path == ".github/workflows/ci.yml";

        if (changed.Any(IsSharedInfra))
        {
            Log.Information("Shared infrastructure changed — building all {Count} entries", allEntries.Count);
            return allEntries;
        }

        // Match paths under libraries/ and apple-frameworks/ (tests are co-located).
        var pathRegex = new Regex(@"^(libraries|apple-frameworks)/([^/]+)", RegexOptions.Compiled);
        var entryMap = allEntries.ToDictionary(e => e.Library, e => e, StringComparer.Ordinal);
        var hits = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var path in changed)
        {
            var m = pathRegex.Match(path);
            if (!m.Success)
                continue;
            var name = m.Groups[2].Value;
            if (!entryMap.ContainsKey(name))
            {
                Log.Information("Skipping '{Lib}' — no matching library or framework", name);
                continue;
            }
            hits.Add(name);
        }

        var result = hits.Select(n => entryMap[n]).ToList();
        Log.Information("ListChangedLibraries: {Count} entry/entries to build: {Libs}",
            result.Count, string.Join(", ", result.Select(e => $"{e.Library} ({e.KindLabel})")));
        return result;
    }

    /// <summary>
    /// Discover all libraries (third-party) and Apple frameworks, returning
    /// them as <see cref="MatrixEntry"/> items sorted by name.
    /// </summary>
    IReadOnlyList<MatrixEntry> DiscoverAllEntries()
    {
        var entries = new List<MatrixEntry>();

        // Third-party libraries: directories with library.json
        foreach (var lib in DiscoverLibraries())
            entries.Add(new MatrixEntry(lib, LibraryKind.ThirdParty));

        // Apple frameworks: directories with SwiftBindings.*.csproj
        foreach (var fw in DiscoverAppleFrameworks())
            entries.Add(new MatrixEntry(fw, LibraryKind.AppleFramework));

        return entries.OrderBy(e => e.Library, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// Discover Apple framework names by globbing
    /// <c>apple-frameworks/*/SwiftBindings.*.csproj</c>.
    /// </summary>
    IEnumerable<string> DiscoverAppleFrameworks()
    {
        if (!Directory.Exists(AppleFrameworksDir))
            return Enumerable.Empty<string>();

        return Directory.EnumerateDirectories(AppleFrameworksDir)
            .Where(d => Directory.EnumerateFiles(d, "SwiftBindings.*.csproj").Any())
            .Select(Path.GetFileName)
            .OfType<string>()
            .OrderBy(n => n, StringComparer.Ordinal);
    }

    /// <summary>
    /// Run <c>git diff --name-only base..head</c> and return the list of
    /// changed files. Mirrors the bash <c>git diff</c> at <c>ci.yml:39</c>.
    /// </summary>
    static List<string> GitDiffNameOnly(string baseSha, string headSha)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("git")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("diff");
        psi.ArgumentList.Add("--name-only");
        psi.ArgumentList.Add($"{baseSha}...{headSha}");

        using var process = System.Diagnostics.Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start git");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"git diff exited with {process.ExitCode}: {stderr}");
        return stdout
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .ToList();
    }

    /// <summary>
    /// Print the resolved entry list in the requested form. With
    /// <c>--json</c>, the GHA matrix shape is emitted on stdout so the
    /// workflow can pipe it directly into <c>$GITHUB_OUTPUT</c>; without it,
    /// one entry per line for human / shell consumption.
    /// </summary>
    void EmitMatrix(IReadOnlyList<MatrixEntry> entries)
    {
        if (Json)
        {
            // GHA matrix shape: {"include":[{"library":"Nuke","kind":"third-party"}, ...]}
            var matrix = new
            {
                include = entries.Select(e => new { library = e.Library, kind = e.KindLabel }).ToList(),
            };
            var json = JsonSerializer.Serialize(matrix);
            Console.WriteLine(json);
        }
        else
        {
            foreach (var e in entries)
                Console.WriteLine($"{e.Library}\t{e.KindLabel}");
        }
    }

    /// <summary>
    /// Smoke-test target. Walks <c>libraries/*/library.json</c>, prints library
    /// name, mode, product count, and whether any product is internal. Used
    /// during Session 1 validation to confirm <c>library.json</c> parsing
    /// covers all 8 libraries before any actual build runs.
    /// </summary>
    Target ListLibraries => _ => _
        .Description("Enumerate every library in libraries/ and print mode + product summary")
        .Executes(() =>
        {
            var libs = DiscoverLibraries().ToList();
            Log.Information("Found {Count} libraries:", libs.Count);

            foreach (var lib in libs)
            {
                try
                {
                    var config = LibraryConfigLoader.Load(LibraryConfigPath(lib));
                    var hasInternal = config.Products.Any(p => p.Internal);
                    Log.Information(
                        "  {Library,-12} mode={Mode,-7} products={Count,2}{Internal}",
                        lib,
                        config.Mode,
                        config.Products.Count,
                        hasInternal ? "  [has internal products]" : "");
                }
                catch (Exception ex)
                {
                    Log.Error("  {Library,-12} FAILED to load: {Message}", lib, ex.Message);
                    throw;
                }
            }
        });

    // ─── RunCiSimTest ─────────────────────────────────────────────────────────
    //
    // Direct port of scripts/ci/ci_ios_test.py:run_pipeline (564 lines).
    //
    // Preserves the six resilience features the design doc calls out:
    //   1. Parallel boot + build  (Task.WhenAll, ci_ios_test.py:354–384)
    //   2. Test-timeout retry     (deadline-aware, ci_ios_test.py:166–230)
    //   3. Infra-failure retry    (is_infra_failure, ci_ios_test.py:329–438)
    //   4. Diagnostics on failure (sim_manager.collect_diagnostics)
    //   5. Deadline budgeting     (subprocess timeout arithmetic 169–195)
    //   6. INSTALL_OVERHEAD = 480 (empirically tuned for GHA macOS-26)

    /// <summary>
    /// Empirically-tuned simctl install overhead added to the inner test
    /// timeout. <c>simctl install</c> is pathologically slow on GHA macOS-26.
    /// Mirrors <c>INSTALL_OVERHEAD = 480</c> at <c>ci_ios_test.py:163</c>.
    /// </summary>
    public const int InstallOverheadSeconds = 480;

    /// <summary>
    /// Patterns in error messages that indicate retryable infrastructure
    /// failures. Mirror of <c>INFRA_FAILURE_PATTERNS</c> at
    /// <c>ci_ios_test.py:54–66</c>.
    /// </summary>
    public static readonly string[] InfraFailurePatterns =
    {
        "failed to boot",
        "unable to boot",
        "unable to lookup in current state",
        "CoreSimulatorService connection interrupted",
        "timed out waiting",
        "launchd",
        "bootstrap",
        "SimulatorBootTimeout",
        "SimulatorReadinessTimeout",
        "domain error",
        "Unable to negotiate with CoreSimulatorService",
    };

    /// <summary>Classifier mirroring <c>is_infra_failure</c>.</summary>
    public static bool IsInfraFailure(Exception e)
    {
        if (e is SimulatorBootTimeout or SimulatorReadinessTimeout) return true;
        var msg = e.Message?.ToLowerInvariant() ?? "";
        return InfraFailurePatterns.Any(p => msg.Contains(p.ToLowerInvariant(), StringComparison.Ordinal));
    }

    /// <summary>
    /// Outcome of running the inner test phase. Distinct from
    /// <see cref="TestRunStatus"/> because the orchestrator's retry decision
    /// only cares about three states: success, retryable timeout, hard failure.
    /// </summary>
    enum TestPhaseOutcome { Success, Timeout, Failed }

    /// <summary>
    /// CI test execution target. Composes:
    ///   parallel(boot sim || build test app)
    ///     → run inner test (with subprocess timeout arithmetic + retry on TIMEOUT)
    ///     → on infra failure, collect diagnostics + retry pipeline
    ///     → cleanup on terminal exit
    /// </summary>
    Target RunCiSimTest => _ => _
        .Description("CI orchestrator: parallel boot+build + sim test with retries (--library Foo [--reuse-sim])")
        .Requires(() => Library)
        .Executes(() =>
        {
            var library = Library!;
            var (testDir, appName, bundleId) = ResolveTestNames(library);

            var pipelineStart = DateTime.UtcNow;
            var deadline = pipelineStart.AddSeconds(StepTimeout);

            Log.Information(
                "RunCiSimTest {Library}: timeout={Timeout}s, step-timeout={Step}s, max-test-retries={Tr}, max-infra-retries={Ir}, reuse-sim={Reuse}, diag-dir={Diag}",
                library, Timeout, StepTimeout, MaxTestRetries, MaxInfraRetries, ReuseSim, DiagDir);

            var fleet = new SimulatorFleet();
            string? createdUdid = null;
            string? deviceUdid = DeviceUdid;
            var skipBuild = false;

            for (var attempt = 1; attempt <= MaxInfraRetries + 1; attempt++)
            {
                if (attempt > 1)
                {
                    Log.Information("");
                    Log.Information("============================================================");
                    Log.Information("RETRY attempt {Attempt}/{Max} (infrastructure failure)", attempt, MaxInfraRetries + 1);
                    Log.Information("============================================================");
                }

                try
                {
                    // ── Phase 1: Parallel boot + build ─────────────────────
                    if (deviceUdid is null)
                    {
                        if (skipBuild)
                        {
                            // Sequential prepare-only path (rare — only after the
                            // build half of a previous parallel attempt succeeded
                            // before the sim half failed).
                            GhaGroup("Prepare iOS Simulator");
                            createdUdid = ReuseSim
                                ? fleet.EnsureReusable()
                                : PrepareFreshSimulator(fleet);
                            deviceUdid = createdUdid;
                            GhaEndGroup();
                        }
                        else
                        {
                            // Parallel: ci_ios_test.py:354–384.
                            GhaGroup("Parallel: Boot Simulator + Build Test App");
                            using var cts = new CancellationTokenSource();
                            var simTask = Task.Run(() =>
                            {
                                return ReuseSim
                                    ? fleet.EnsureReusable()
                                    : PrepareFreshSimulator(fleet);
                            }, cts.Token);

                            var buildTask = Task.Run(() =>
                            {
                                BuildTestAppForRunCi(library, testDir);
                            }, cts.Token);

                            // Block until both finish so we can match the Python
                            // collect-then-prioritize-error semantics.
                            try
                            {
                                Task.WaitAll(new Task[] { simTask, buildTask }, TimeSpan.FromSeconds(InstallOverheadSeconds + 60));
                            }
                            catch
                            {
                                // Swallow — inspected per-task below.
                            }

                            // Match Python: prefer simulator error (potentially retryable)
                            // over build error.
                            if (simTask.IsFaulted)
                            {
                                cts.Cancel();
                                throw UnwrapAggregate(simTask.Exception);
                            }

                            // CRITICAL: if simTask is still running here (because
                            // buildTask faulted early or WaitAll's bounded budget
                            // expired while sim was still booting), we MUST give it
                            // a bounded final wait so we can capture its UDID for
                            // cleanup. cts.Token does NOT actually interrupt
                            // in-progress simctl invocations — once `simctl create`
                            // / `boot` / `bootstatus` are running, they will run
                            // to completion regardless of cancellation. Throwing
                            // now would leave the fresh sim alive with `createdUdid`
                            // still null, and the finally block would never see it.
                            // Mirrors ci_ios_test.py:354–384 which collects BOTH
                            // futures before deciding which error to surface.
                            if (!simTask.IsCompleted)
                            {
                                Log.Information(
                                    "Parallel phase: simulator boot still in progress after WaitAll budget expired — waiting bounded extra time to capture UDID for cleanup");
                                try
                                {
                                    simTask.Wait(TimeSpan.FromSeconds(InstallOverheadSeconds + 60));
                                }
                                catch
                                {
                                    // Inspected via task state below.
                                }
                                if (simTask.IsFaulted)
                                {
                                    cts.Cancel();
                                    throw UnwrapAggregate(simTask.Exception);
                                }
                            }

                            // Record the fresh simulator's UDID as SOON as simTask is
                            // known to have produced one — BEFORE we inspect buildTask
                            // for failures or the overall wait for timeout. Without
                            // this, a "sim boot succeeded, test-app build failed"
                            // outcome would throw with `createdUdid` still null and
                            // the finally block would never clean up the fresh sim.
                            if (simTask.IsCompletedSuccessfully)
                            {
                                createdUdid = simTask.Result;
                                deviceUdid = createdUdid;
                            }

                            if (buildTask.IsFaulted)
                            {
                                cts.Cancel();
                                throw UnwrapAggregate(buildTask.Exception);
                            }
                            if (!simTask.IsCompleted || !buildTask.IsCompleted)
                            {
                                // Sim still hasn't completed even after the second
                                // bounded wait — we can't clean it up. Surface the
                                // timeout so the run fails loudly; an orphan sim
                                // here is unavoidable because we have no way to
                                // interrupt simctl.
                                throw new TimeoutException("Parallel boot+build did not complete within budget");
                            }

                            // createdUdid / deviceUdid were already assigned above in
                            // the IsCompletedSuccessfully branch.
                            skipBuild = true;
                            GhaEndGroup();
                        }
                        Log.Information("Simulator UDID: {Udid}", deviceUdid);
                    }
                    else
                    {
                        Log.Information("Using provided simulator: {Udid}", deviceUdid);
                    }

                    // ── Phase 2: Build (sequential fallback) ──────────────
                    if (!skipBuild)
                    {
                        GhaGroup("Build Test App");
                        BuildTestAppForRunCi(library, testDir);
                        GhaEndGroup();
                        skipBuild = true;
                    }

                    // ── Phase 3: Run tests with retry-on-timeout ──────────
                    GhaGroup("Run iOS Simulator Tests");
                    var outcome = RunInnerTestWithRetry(
                        appName, bundleId, testDir, deviceUdid!, deadline);
                    GhaEndGroup();

                    if (outcome == TestPhaseOutcome.Success)
                        return;

                    // Test failure (real or out-of-budget timeout) — terminal,
                    // not retryable.
                    CollectDiagnosticsBestEffort(fleet, deviceUdid, appName);
                    throw new InvalidOperationException(
                        outcome == TestPhaseOutcome.Timeout
                            ? "Inner test timed out (no budget for retry)"
                            : "Inner test failed (real failure or crash)");
                }
                catch (Exception ex)
                {
                    Log.Error("Pipeline error: {Message}", ex.Message);

                    if (IsInfraFailure(ex) && attempt <= MaxInfraRetries)
                    {
                        GhaWarning($"Infrastructure failure (attempt {attempt}): {ex.Message}");
                        Log.Information("Collecting diagnostics before retry...");
                        if (createdUdid is not null)
                        {
                            CollectDiagnosticsBestEffort(fleet, createdUdid, appName);
                            CleanupSimBestEffort(fleet, createdUdid);
                        }
                        createdUdid = null;
                        deviceUdid = null;
                        // Re-build on next attempt — the previous build artifacts
                        // are still on disk, but the Python port also rebuilds.
                        // Mirror that for consistency.
                        skipBuild = false;
                        continue;
                    }

                    // Terminal: collect diagnostics and rethrow.
                    GhaError($"Pipeline failed: {ex.Message}");
                    if (createdUdid is not null)
                        CollectDiagnosticsBestEffort(fleet, createdUdid, appName);
                    throw;
                }
                finally
                {
                    // Cleanup the simulator we created whenever we're exiting
                    // this try block with one still alive — on success (return),
                    // on terminal non-retry failure (throw), and on the terminal
                    // last-retry failure. Matches the Python `finally` at
                    // ci_ios_test.py:450–457, which cleans up on every terminal
                    // path, not just the exhausted-retries case.
                    //
                    // The retry path (in the catch above) explicitly nulls
                    // createdUdid before `continue`, so this won't double-clean
                    // on retries. The earlier `attempt > MaxInfraRetries` guard
                    // here was wrong: on a first-attempt success (attempt=1,
                    // MaxInfraRetries=1) the condition was false and the fresh
                    // simulator was leaked.
                    if (createdUdid is not null && !ReuseSim)
                    {
                        GhaGroup("Cleanup Simulator");
                        CleanupSimBestEffort(fleet, createdUdid);
                        GhaEndGroup();
                    }
                }
            }
        });

    /// <summary>
    /// Phase 3 of the orchestrator: run the inner test with deadline-aware
    /// timeout arithmetic and one retry on TIMEOUT (and only on TIMEOUT).
    /// Mirrors <c>run_tests</c> at <c>ci_ios_test.py:143–244</c>.
    /// </summary>
    TestPhaseOutcome RunInnerTestWithRetry(
        string appName,
        string bundleId,
        AbsolutePath testDir,
        string udid,
        DateTime deadline)
    {
        var rid = ResolveRid("iossimulator-arm64");
        var appPath = ResolveAppPath(testDir, "Debug", "ios", rid, appName);
        if (!Directory.Exists(appPath))
            throw new InvalidOperationException($"App not found at {appPath}");

        var sim = new SimctlClient(udid);
        var crashDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Library", "Logs", "DiagnosticReports");

        for (var attempt = 1; attempt <= MaxTestRetries + 1; attempt++)
        {
            if (attempt > 1)
            {
                // Deadline arithmetic — ci_ios_test.py:169–178.
                var minRetryTime = Timeout + 30;
                var remaining = (deadline - DateTime.UtcNow).TotalSeconds;
                if (remaining < minRetryTime)
                {
                    Log.Warning("Only {Rem:F0}s remaining (need {Need}s for retry) — skipping retry",
                        remaining, minRetryTime);
                    return TestPhaseOutcome.Timeout;
                }
                Log.Information("{Rem:F0}s remaining — enough for retry (need {Need}s)", remaining, minRetryTime);

                // Settle before the retry attempt — ci_ios_test.py:181–188.
                sim.TerminateApp(bundleId);
                Thread.Sleep(2000);
                Log.Information("=== TESTS: Retry attempt {Attempt} (previous run timed out) ===", attempt);
                GhaWarning($"Test retry attempt {attempt} after timeout/hang");
            }

            // Deadline-aware subprocess timeout — exact mirror of
            // ci_ios_test.py:192–197. This is the OUTER wall-clock cap on the
            // entire install+launch+watch sequence, equivalent to Python's
            //   subprocess.run([... "validate-sim.sh", str(timeout), udid],
            //                  timeout=subprocess_timeout)
            // The inner test timeout (`Timeout` param) still bounds the
            // per-line watcher loop; this outer cap protects against an
            // unresponsive `simctl install` consuming all remaining budget.
            double subprocessTimeoutSeconds;
            var rem = (deadline - DateTime.UtcNow).TotalSeconds;
            if (rem > 0)
            {
                subprocessTimeoutSeconds = Math.Min(
                    Timeout + InstallOverheadSeconds,
                    Math.Max(rem - 30, Timeout + 60));
            }
            else
            {
                subprocessTimeoutSeconds = Timeout + InstallOverheadSeconds;
            }
            var subprocessDeadline = DateTime.UtcNow.AddSeconds(subprocessTimeoutSeconds);

            Log.Information(
                "=== TESTS: Running inner sim test (timeout={Timeout}s, attempt={Attempt}, subprocess_timeout={Sub:F0}s) ===",
                Timeout, attempt, subprocessTimeoutSeconds);

            // Mirror validate-sim.sh: install + launch + parse stdout.
            var beforeCrashCount = CountCrashLogs(crashDir, appName);
            try
            {
                // Cap install at min(InstallOverhead, time remaining in the
                // subprocess envelope). This is the half of the
                // subprocess_timeout enforcement that protects against a
                // hung install on slow GHA macOS-26 runners.
                var installCap = TimeSpan.FromSeconds(Math.Max(1,
                    Math.Min(InstallOverheadSeconds,
                             (subprocessDeadline - DateTime.UtcNow).TotalSeconds)));
                sim.InstallApp(appPath, installCap);
            }
            catch (Exception installEx)
            {
                // Install failures are usually infra (CoreSim glitches) — bubble
                // up so the outer infra-retry layer can handle them.
                throw new InvalidOperationException($"simctl install failed: {installEx.Message}", installEx);
            }

            var psi = sim.BuildLaunchPsi(bundleId);
            // Watcher cap = min(user test timeout, time remaining in the
            // subprocess envelope). The inner Timeout matches bash's
            // `validate-sim.sh <TIMEOUT>` arg; the second clause enforces
            // the OUTER subprocess_timeout from ci_ios_test.py:192–197.
            var watcherTimeout = TimeSpan.FromSeconds(Math.Max(1,
                Math.Min(Timeout, (subprocessDeadline - DateTime.UtcNow).TotalSeconds)));

            var result = StdoutWatcher
                .RunAsync(psi, watcherTimeout, onLine: line => Log.Information("{Line}", line))
                .GetAwaiter().GetResult();

            sim.TerminateApp(bundleId);

            // Crash log post-check.
            var afterCrashCount = CountCrashLogs(crashDir, appName);
            if (afterCrashCount > beforeCrashCount)
            {
                var newest = NewestCrashLog(crashDir, appName);
                Log.Error("=== CRASH LOG DETECTED ===");
                if (newest is not null)
                    Log.Error("{Path}\n{Head}", newest, HeadOfFile(newest, 50));
                return TestPhaseOutcome.Failed;
            }

            switch (result.Status)
            {
                case TestRunStatus.Success:
                    Log.Information("=== TESTS: PASSED ===");
                    return TestPhaseOutcome.Success;
                case TestRunStatus.Failed:
                case TestRunStatus.Crashed:
                case TestRunStatus.Exited:
                    // Real failure — do NOT retry. Matches ci_ios_test.py:228–230.
                    Log.Error("=== TESTS: FAILED ({Status}: {Reason}) ===", result.Status, result.Reason);
                    Log.Information("=== APP OUTPUT ===\n{Output}", result.Output);
                    return TestPhaseOutcome.Failed;
                case TestRunStatus.Timeout:
                    Log.Warning("Tests timed out — will retry if budget permits");
                    if (attempt > MaxTestRetries)
                    {
                        Log.Error("=== TESTS: TIMED OUT (no retries left) ===");
                        Log.Information("=== APP OUTPUT ===\n{Output}", result.Output);
                        return TestPhaseOutcome.Timeout;
                    }
                    continue;
            }
        }
        return TestPhaseOutcome.Timeout;
    }

    void BuildTestAppForRunCi(string library, AbsolutePath testDir)
    {
        // RunCiSimTest only ever wants the iOS simulator path. Pass -f to
        // select the iOS TFM only for multi-TFM projects (e.g. Apple
        // framework tests declare net10.0-ios;net10.0-macos;...).
        // Single-TFM legacy projects may use versioned TFMs like
        // net10.0-ios26.2, so passing -f net10.0-ios would fail to match.
        Log.Information("=== BUILD: Building {Library} test app for simulator ===", library);
        var args = new List<string> { "build", (string)testDir, "-c", "Debug" };
        if (IsMultiTfmTestProject(testDir))
            args.AddRange(new[] { "-f", ResolveTfm("ios") });
        // Honor --runtime-identifier so the path used by RunInnerTestWithRetry
        // to locate the built .app agrees with the one dotnet build produces.
        if (!string.IsNullOrEmpty(RuntimeIdentifier))
            args.Add($"-p:RuntimeIdentifier={RuntimeIdentifier}");
        var exit = RunDotnet(args.ToArray());
        if (exit != 0)
            throw new InvalidOperationException($"dotnet build (sim) failed with exit {exit}");
    }

    void CollectDiagnosticsBestEffort(SimulatorFleet fleet, string udid, string appName)
    {
        try
        {
            fleet.CollectDiagnostics(udid, DiagDir, appName);
        }
        catch (Exception ex)
        {
            Log.Warning("Diagnostics collection failed (non-fatal): {Message}", ex.Message);
        }
    }

    void CleanupSimBestEffort(SimulatorFleet fleet, string udid)
    {
        try
        {
            // Mirror SimManager.cleanup: shut down THEN delete only the
            // simulator we booted, leaving any unrelated sims (on a developer
            // machine) untouched. Both steps are scoped to `udid`.
            //
            // The earlier version only called ShutdownOne() and deliberately
            // skipped delete on the theory that "the developer machine doesn't
            // want the sim image gone." That was correct while PrepareFreshSimulator
            // might reuse a pre-existing shutdown iPhone, but now that the
            // non-reuse path ALWAYS creates a brand-new `ci-sim-<ts>-<pid>`
            // device, deleting it after shutdown is both safe (we know we
            // created it) and necessary (otherwise every local fresh-sim run
            // leaves another zombie in `simctl list`).
            fleet.ShutdownOne(udid);
            fleet.DeleteOne(udid);
        }
        catch (Exception ex)
        {
            Log.Warning("Cleanup failed (non-fatal): {Message}", ex.Message);
        }
    }

    string PrepareFreshSimulator(SimulatorFleet fleet)
    {
        // Mirror sim_manager.py:prepare_simulator(create_fresh=True): always
        // create a fresh simulator and always wait for it to be responsive.
        // Explicitly does NOT reuse an existing booted or shutdown iPhone —
        // that's --reuse-sim's job via EnsureReusable(). Keeping "fresh sim"
        // runs actually fresh prevents dirty state from previous runs and
        // races against a nominally-booted-but-not-responsive simulator, both
        // of which regressed the non-reuse path relative to the original
        // Python orchestrator.
        Log.Information("Creating fresh simulator (non-reuse mode)");
        var udid = fleet.CreateSimulator(runtimePrefix: null, deviceName: null);
        // BootAndWaitForReady performs the Boot → WaitUntilBooted →
        // WaitUntilResponsive chain, so the caller gets a live sim back.
        fleet.BootAndWaitForReady(udid);
        return udid;
    }

    static Exception UnwrapAggregate(AggregateException? ae)
    {
        if (ae is null) return new InvalidOperationException("unknown error");
        var inner = ae.InnerException;
        return inner ?? ae;
    }

    // ── GitHub Actions log helpers ───────────────────────────────────────────

    static bool InGitHubActions =>
        string.Equals(Environment.GetEnvironmentVariable("GITHUB_ACTIONS"), "true", StringComparison.OrdinalIgnoreCase);

    static void GhaGroup(string title)
    {
        if (InGitHubActions) Console.WriteLine($"::group::{title}");
        Log.Information("=== {Title} ===", title);
    }

    static void GhaEndGroup()
    {
        if (InGitHubActions) Console.WriteLine("::endgroup::");
    }

    static void GhaWarning(string msg)
    {
        if (InGitHubActions) Console.WriteLine($"::warning::{msg}");
        Log.Warning("{Message}", msg);
    }

    static void GhaError(string msg)
    {
        if (InGitHubActions) Console.WriteLine($"::error::{msg}");
        Log.Error("{Message}", msg);
    }
}
