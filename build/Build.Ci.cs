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

    /// <summary>
    /// Shared by two targets with the same meaning ("everything, not a subset"),
    /// scoped differently by each: <see cref="ListChangedLibraries"/> returns every
    /// library in the repo; <see cref="ReleaseWave"/> selects every ALREADY
    /// PUBLISHED library (see <see cref="ResolvePublishedCatalogAsync"/>) so a
    /// deliberately unpublished one is never swept into a release.
    /// </summary>
    [Parameter("ListChangedLibraries: every library. ReleaseWave: every already-published library")]
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
        // Sequential ReadToEnd on stdout then stderr can deadlock when git
        // fills the stderr pipe while we block on stdout. Drain both async
        // and bound the wait so a hung git can't freeze CI.
        var stdoutSb = new System.Text.StringBuilder();
        var stderrSb = new System.Text.StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (stdoutSb) stdoutSb.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (stderrSb) stderrSb.AppendLine(e.Data); };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        if (!process.WaitForExit(30_000))
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException("git diff --name-only timed out after 30s");
        }
        process.WaitForExit();
        var stdout = stdoutSb.ToString();
        var stderr = stderrSb.ToString();
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
    //   2. Deadline-aware install capping, so the run budget is never shortened
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
    /// Floor under the install cap. The install is the part of an attempt that yields to the
    /// step deadline, but it can never be squeezed to nothing — an install given no time fails
    /// as an infrastructure error instead of running the tests.
    /// </summary>
    public const int MinInstallCapSeconds = 60;

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
        // SimctlCommandError emits "timed out after Ns" when a single simctl
        // invocation exceeds CommandTimeout. On busy macos-26 runners cold
        // `simctl list devices -j` legitimately needs >60s, so classify these
        // as retryable infra rather than terminal release failures.
        "timed out after",
        "launchd",
        "bootstrap",
        "SimulatorBootTimeout",
        "SimulatorReadinessTimeout",
        "domain error",
        "Unable to negotiate with CoreSimulatorService",
        // The launcher quit before the app image was entered (see LaunchDiagnostics).
        // No product signal was produced, so the pipeline may re-attempt it — and only it.
        "launcher aborted before the app started",
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
    /// <see cref="TestRunStatus"/> because the orchestrator only cares about three
    /// states: success, a run that produced no verdict, and a reported failure. All
    /// three are terminal — the distinction shapes the message, not a retry.
    /// </summary>
    enum TestPhaseOutcome { Success, Timeout, Failed }

    /// <summary>
    /// CI test execution target. Composes:
    ///   parallel(boot sim || build test app)
    ///     → run inner test (one attempt, under an install cap + launch/run budgets)
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
            // Reject a missing StoreKit product before simulator preparation or the test-app build.
            var storeKitConfiguration = RequireStoreKitSandboxConfiguration(library, "ios", bundleId);

            var pipelineStart = DateTime.UtcNow;
            var deadline = pipelineStart.AddSeconds(StepTimeout);

            var runBudget = ResolveRunBudgetSeconds(library, Timeout);
            var launchBudget = LaunchBudgetSeconds;

            Log.Information(
                "RunCiSimTest {Library}: run-budget={Run}s (requested {Timeout}s), launch-budget={Launch}s, step-timeout={Step}s, max-infra-retries={Ir}, reuse-sim={Reuse}, diag-dir={Diag}",
                library, runBudget, Timeout, launchBudget, StepTimeout, MaxInfraRetries, ReuseSim, DiagDir);

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
                    // ── Phase 1: Prepare simulator (sequential) ────────────
                    // The previous parallel boot+build pattern (mirror of
                    // ci_ios_test.py:354–384) starved both sides on macos-26's
                    // 3-vCPU Apple Silicon runners when shipping heavy ObjC
                    // bindings like Matter (~92k LoC of generated C#). simctl
                    // would time out at 60s on `list devices -j` while the
                    // SDK generator was saturating CPU. Serializing costs
                    // ~one sim boot of wall-clock (60s) and removes the flake.
                    if (deviceUdid is null)
                    {
                        GhaGroup("Prepare iOS Simulator");
                        createdUdid = ReuseSim
                            ? fleet.EnsureReusable()
                            : PrepareFreshSimulator(fleet);
                        deviceUdid = createdUdid;
                        GhaEndGroup();
                        Log.Information("Simulator UDID: {Udid}", deviceUdid);
                    }
                    else
                    {
                        Log.Information("Using provided simulator: {Udid}", deviceUdid);
                    }

                    // ── Phase 2: Build test app ───────────────────────────
                    if (!skipBuild)
                    {
                        GhaGroup("Build Test App");
                        BuildTestAppForRunCi(library, testDir);
                        GhaEndGroup();
                        skipBuild = true;
                    }

                    // ── Phase 3: Run the tests (one attempt) ──────────────
                    GhaGroup("Run iOS Simulator Tests");
                    var outcome = RunInnerTest(
                        library, appName, bundleId, testDir, deviceUdid!, deadline, runBudget, launchBudget,
                        storeKitConfiguration);
                    GhaEndGroup();

                    if (outcome == TestPhaseOutcome.Success)
                        return;

                    // Test failure or timeout — terminal, not retryable.
                    CollectDiagnosticsBestEffort(fleet, deviceUdid, appName);
                    throw new InvalidOperationException(
                        outcome == TestPhaseOutcome.Timeout
                            ? "Inner test timed out before the app produced output"
                            : "Inner test failed (real failure, crash, or mid-run timeout)");
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
    /// Phase 3 of the orchestrator: install the app, watch it under the launch and run
    /// budgets, and report what happened. Exactly one attempt.
    ///
    /// <para>The predecessor (<c>run_tests</c> at <c>ci_ios_test.py:143–244</c>) retried once
    /// on any timeout. A timeout is not evidence of an infrastructure fault: it is silence,
    /// and silence is what a hung app and a stalled runner have in common. Re-running it is
    /// how an intermittent hang becomes a green, so a timeout is terminal here in both phases.
    /// The one condition that carries no verdict — the launcher aborting before the app was
    /// ever entered — is raised as an infrastructure failure, and the outer loop answers it by
    /// re-preparing a simulator rather than by re-running the same one.</para>
    /// </summary>
    TestPhaseOutcome RunInnerTest(
        string library,
        string appName,
        string bundleId,
        AbsolutePath testDir,
        string udid,
        DateTime deadline,
        int runBudgetSeconds,
        int launchBudgetSeconds,
        StoreKitSandboxReadiness.Configuration? storeKitConfiguration)
    {
        // The watcher spends at most launch + run: the first budget bounds the wait for
        // the app's first line, the second bounds the suite from there. Every envelope
        // below is computed from that sum rather than from the run budget alone, so a
        // slow cold start cannot eat the time the tests were given.
        var watchBudgetSeconds = runBudgetSeconds + launchBudgetSeconds;

        var rid = ResolveRid("iossimulator-arm64");
        var appPath = ResolveAppPath(testDir, "Debug", "ios", rid, appName);
        if (!Directory.Exists(appPath))
            throw new InvalidOperationException($"App not found at {appPath}");

        var sim = new SimctlClient(udid);
        var crashDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Library", "Logs", "DiagnosticReports");

        // Deadline-aware caps for the install+launch+watch sequence. The predecessor
        // (ci_ios_test.py:192–197) computed ONE subprocess timeout covering all of it;
        // that is what this replaces, because a single number cannot say which phase
        // was allowed to be slow.
        // The install and the watch happen one after the other, so they get sequential
        // caps rather than two draws on one countdown. The watcher's launch+run budgets
        // are reserved first — they are the only part of this that produces a verdict —
        // and the install gets whatever the step deadline leaves, bounded above by the
        // empirical overhead and below by MinInstallCapSeconds. A slow install then
        // delays the run instead of shortening it; sharing one countdown let it eat the
        // run budget, which is the mid-suite kill the budgets exist to prevent.
        var rem = (deadline - DateTime.UtcNow).TotalSeconds;
        var nativeControlBudgetSeconds = storeKitConfiguration is null
            ? 0
            : ResolveNativeControlBudgetSeconds(library);
        var reservedBudgetSeconds = watchBudgetSeconds + nativeControlBudgetSeconds;
        var installCapSeconds = ResolveInstallCapSeconds(rem, reservedBudgetSeconds);
        var attemptEnvelopeSeconds = installCapSeconds + reservedBudgetSeconds;

        // Preparation and the build come out of the same step budget, so they can leave less
        // than one attempt needs. The attempt still runs at full budget and says so: shortening
        // it would trade a late verdict for no verdict, which is the failure mode this whole
        // arrangement exists to remove. The overrun is visible here rather than arriving later
        // as an unexplained job kill.
        if (rem < MinInstallCapSeconds + reservedBudgetSeconds)
            Log.Warning(
                "Only {Rem:F0}s of the {Step}s step budget remain, less than the {Need}s an attempt needs "
                + "(managed install floor + native readiness + managed launch + run) — this run will "
                + "overrun the step deadline rather than be cut short",
                rem, StepTimeout, MinInstallCapSeconds + reservedBudgetSeconds);

        Log.Information(
            "=== TESTS: Running inner sim test (launch budget={Launch}s, run budget={Run}s, native readiness budget={Native}s, managed install cap={Install:F0}s, envelope={Env:F0}s) ===",
            launchBudgetSeconds, runBudgetSeconds, nativeControlBudgetSeconds,
            installCapSeconds, attemptEnvelopeSeconds);

        // Mirror validate-sim.sh: install + launch + parse stdout.
        var beforeCrashCount = CountCrashLogs(crashDir, appName);
        if (storeKitConfiguration is not null)
            RunStoreKitSandboxNativeControl(storeKitConfiguration, sim.Device,
                line => Log.Information("{Line}", line));
        try
        {
            // Protects against a hung install on slow GHA macOS-26 runners without
            // borrowing from the watcher's budgets.
            sim.InstallApp(appPath, TimeSpan.FromSeconds(installCapSeconds));
        }
        catch (Exception installEx)
        {
            // Install failures are usually infra (CoreSim glitches) — bubble
            // up so the outer infra-retry layer can handle them.
            throw new InvalidOperationException($"simctl install failed: {installEx.Message}", installEx);
        }

        var managedLaunchArguments = storeKitConfiguration is null
            ? Array.Empty<string>()
            : new[]
            {
                StoreKitSandboxReadiness.ProductIdArgument, storeKitConfiguration.ProductId,
            };
        var psi = sim.BuildLaunchPsi(bundleId, managedLaunchArguments);
        // The watcher gets its budgets whole: it bounds itself at launch budget until
        // the app speaks and run budget after, so the watch phase is already capped at
        // their sum. Trimming them by whatever the install left over is what made a
        // library's declared run budget stop applying on a slow runner.
        var watcherRunTimeout = TimeSpan.FromSeconds(runBudgetSeconds);
        var watcherLaunchTimeout = TimeSpan.FromSeconds(launchBudgetSeconds);

        var result = StdoutWatcher
            .RunAsync(psi, watcherRunTimeout, watcherLaunchTimeout,
                onLine: line => Log.Information("{Line}", line))
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
            case TestRunStatus.LauncherAborted:
                // The validator path retries the launch itself; by the time one of
                // these reaches here the app has still never been entered. Raise it
                // as an infrastructure condition so the outer pipeline re-prepares a
                // simulator, rather than recording a test verdict that was never taken.
                Log.Error("=== LAUNCH ABORTED ({Reason}) — no verdict was produced ===", result.Reason);
                Log.Information("=== LAUNCHER OUTPUT ===\n{Output}", result.Output);
                throw new InvalidOperationException(
                    $"launcher aborted before the app started: {result.Reason}");
            case TestRunStatus.Timeout:
                // Both phases are terminal. Mid-run silence is a verdict about the code
                // under test; silence before the app ever speaks is a hang whose cause the
                // run cannot name — an app deadlocked in startup and a stalled runner look
                // identical from here, and re-running either is how one of them turns green.
                // The launcher's own aborts are the identified infrastructure case and are
                // handled above.
                Log.Error(result.IsLaunchPhaseTimeout
                        ? "=== TESTS: TIMED OUT DURING LAUNCH ({Reason}) — not retried ==="
                        : "=== TESTS: TIMED OUT MID-RUN ({Reason}) — not retried ===",
                    result.Reason);
                Log.Information("=== APP OUTPUT ===\n{Output}", result.Output);
                return result.IsLaunchPhaseTimeout ? TestPhaseOutcome.Timeout : TestPhaseOutcome.Failed;
        }

        throw new InvalidOperationException(
            $"the watcher returned the non-terminal status {result.Status} — no verdict was produced");
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
        // Honor --runtime-identifier so the path used by RunInnerTest
        // to locate the built .app agrees with the one dotnet build produces.
        if (!string.IsNullOrEmpty(RuntimeIdentifier))
            args.Add($"-p:RuntimeIdentifier={RuntimeIdentifier}");
        // Emit a binary log so SDK generator failures (e.g. `Swift.Bindings.dll
        // exit 1` with no stderr) are diagnosable post-hoc. DiagDir is
        // /tmp/sim-diagnostics by default and is uploaded as an artifact on
        // failure via the workflow's `Upload simulator diagnostics` step.
        Directory.CreateDirectory(DiagDir);
        var binlogPath = Path.Combine(DiagDir, $"testapp-build-{library}.binlog");
        args.Add($"-bl:{binlogPath}");
        var exit = RunDotnet(args.ToArray());
        if (exit != 0)
            throw new InvalidOperationException(
                $"dotnet build (sim) failed with exit {exit} — see binlog at {binlogPath}");
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
