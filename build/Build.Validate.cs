using Nuke.Common;
using Nuke.Common.IO;
using Serilog;
using SwiftBindings.Build.Helpers;
using SwiftBindings.Build.Models;

partial class Build
{
    /// <summary>
    /// Install + launch a sim test app on an iOS Simulator and parse its
    /// stdout via <see cref="StdoutWatcher"/>. Direct port of
    /// <c>scripts/validate-sim.sh</c>.
    ///
    /// <para>
    /// Resolves <c>bin/Debug/net10.0-ios/&lt;rid&gt;/&lt;App&gt;.app</c>
    /// (where <c>&lt;rid&gt;</c> defaults to <c>iossimulator-arm64</c> and can
    /// be overridden via <c>--runtime-identifier</c>), records the host's
    /// crash-log count BEFORE install, drives <c>simctl install</c> +
    /// <c>simctl launch --console</c>, and exits 0 only on <c>TEST SUCCESS</c>.
    /// Crash logs that appear after the run are surfaced and the run is
    /// failed.
    /// </para>
    /// </summary>
    Target ValidateSim => _ => _
        .Description("Install and validate the test app on an iOS simulator (--library Foo [--device-udid X] [--timeout N])")
        .Requires(() => Library)
        .Executes(() =>
        {
            var result = ValidateSimFor(Library!, Timeout, DeviceUdid, ResolveRid("iossimulator-arm64"));
            ReportTerminalStatus(result, Library!);
        });

    /// <summary>
    /// Pure-function validate-sim core. Returns the <see cref="TestRunResult"/>
    /// instead of throwing on non-success — callers that want exception-on-fail
    /// behavior wrap with <see cref="ReportTerminalStatus"/>. Used by both
    /// <see cref="ValidateSim"/> and the regression-validate orchestrator.
    /// </summary>
    TestRunResult ValidateSimFor(string library, int timeoutSeconds, string? deviceUdid, string rid,
        Action<string>? onLine = null)
    {
        var (testDir, appName, bundleId) = ResolveTestNames(library);
        var appPath = ResolveAppPath(testDir, "Debug", "ios", rid, appName);
        if (!Directory.Exists(appPath))
            throw new InvalidOperationException(
                $"Error: App not found at {appPath}\nRun BuildTestApp --library {library} first.");

        return ValidateSimAt(appPath, appName, bundleId,
            ResolveRunBudgetSeconds(library, timeoutSeconds), deviceUdid, onLine,
            storeKitConfiguration: RequireStoreKitSandboxConfiguration(library, "ios", bundleId));
    }

    /// <summary>
    /// Install + launch an already-located <c>.app</c> on the simulator.
    ///
    /// <para>Split out of <see cref="ValidateSimFor"/> so a lane that builds the
    /// same test app into a different output tree — the release-consumption lane
    /// redirects <c>BaseOutputPath</c> to keep its package-mode restore out of
    /// the tree the ProjectReference lanes use — runs through exactly the same
    /// install/launch/crash-log path rather than a parallel copy of it.</para>
    /// </summary>
    TestRunResult ValidateSimAt(AbsolutePath appPath, string appName, string bundleId,
        int timeoutSeconds, string? deviceUdid, Action<string>? onLine = null,
        StoreKitSandboxReadiness.Configuration? storeKitConfiguration = null)
    {
        void Info(string line) { if (onLine is null) Log.Information("{Line}", line); else onLine(line); }
        void Err(string line) { if (onLine is null) Log.Error("{Line}", line); else onLine(line); }

        // Resolve a concrete iOS-family simulator rather than installing to
        // simctl's `booted` alias. That alias picks a booted device without
        // regard to device family, so one Apple TV (or watch) left booted on the
        // host makes every iOS install fail with "This app was not built to
        // support this device family" — a red that has nothing to do with the
        // code under test. EnsureReusable is the same resolution the fleet
        // already uses: it reuses the first booted iPhone (so a host with one
        // booted iPhone picks exactly the device it always did), probes it for
        // readiness, and boots one only when nothing is booted.
        if (string.IsNullOrEmpty(deviceUdid))
        {
            deviceUdid = new SimulatorFleet().EnsureReusable();
            Info($"Resolved iOS simulator {deviceUdid} (no --device-udid given)");
        }

        var sim = new SimctlClient(deviceUdid,
            storeKitConfiguration?.Platform == "tvos" ? SimulatorFamily.TvOs : SimulatorFamily.Ios);
        var crashDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Library", "Logs", "DiagnosticReports");
        var beforeCrashCount = CountCrashLogs(crashDir, appName);

        if (storeKitConfiguration is not null)
            RunStoreKitSandboxNativeControl(storeKitConfiguration, sim.Device, onLine);

        sim.InstallApp(appPath);
        sim.CleanStaleConsoleFifos();

        var managedLaunchArguments = storeKitConfiguration is null
            ? Array.Empty<string>()
            : new[]
            {
                StoreKitSandboxReadiness.ProductIdArgument, storeKitConfiguration.ProductId,
            };

        var launchBudget = LaunchBudgetSeconds;
        TestRunResult result;
        var priorAttempts = new List<string>();
        for (var attempt = 1; ; attempt++)
        {
            Info($"Launching {appName} on {sim.Device} " +
                 $"(launch budget: {launchBudget}s, run budget: {timeoutSeconds}s, attempt {attempt})");
            var psi = sim.BuildLaunchPsi(bundleId, managedLaunchArguments);
            result = StdoutWatcher
                .RunAsync(psi, TimeSpan.FromSeconds(timeoutSeconds), TimeSpan.FromSeconds(launchBudget), onLine: Info)
                .GetAwaiter().GetResult();

            if (result.Status != TestRunStatus.LauncherAborted)
                break;

            // The app was never entered, so there is nothing to report about the code
            // under test — re-attempt the launch (and only the launch) a bounded number
            // of times before reporting the abort as itself. Every attempt's transcript
            // is kept: the re-launch replaces the verdict, never the evidence.
            if (LaunchDiagnostics.LauncherAbortBudgetExhausted(attempt))
            {
                Err($"The launcher aborted before {appName} started, {attempt} time(s): {result.Reason}");
                break;
            }

            priorAttempts.Add(result.Output);
            var settle = LaunchDiagnostics.SettleDelayAfterAbort(attempt);
            Info($"Launcher aborted before {appName} started ({result.Reason}) — " +
                 $"settling {settle.TotalSeconds:F0}s and re-launching");
            sim.TerminateApp(bundleId);
            Thread.Sleep(settle);
        }

        result = LaunchDiagnostics.WithPriorAttempts(result, priorAttempts);
        sim.TerminateApp(bundleId);

        var afterCrashCount = CountCrashLogs(crashDir, appName);
        if (afterCrashCount > beforeCrashCount)
        {
            var newest = NewestCrashLog(crashDir, appName);
            Err("=== CRASH LOG DETECTED ===");
            if (newest is not null)
                Err($"{newest}\n{HeadOfFile(newest, 50)}");
            return new TestRunResult(TestRunStatus.Crashed, "Crash log appeared after sim test run", result.Output, result.CrashLogs);
        }

        Info("=== APP OUTPUT ===\n" + result.Output);
        return result;
    }

    /// <summary>
    /// Install and validate a tvOS test app on a tvOS Simulator.
    /// </summary>
    Target ValidateTvOs => _ => _
        .Description("Install and validate the test app on a tvOS simulator (--library Foo [--timeout N])")
        .Requires(() => Library)
        .Executes(() =>
        {
            var (result, unavailable) = ValidateTvOsFor(Library!, Timeout);
            if (unavailable is not null)
                throw new InvalidOperationException(
                    $"=== TVOS UNAVAILABLE ({Library}) === {unavailable}");
            ReportTerminalStatus(result!, Library!);
        });

    /// <summary>
    /// Build-adjacent tvOS runtime validation: locate the built tvOS app,
    /// find (and if necessary boot) a tvOS simulator, then run it through the
    /// same install/launch/crash-log path as the iOS simulator lane.
    ///
    /// <para>Returns <c>(null, reason)</c> — never a pass, never a fail — when
    /// the host has no tvOS runtime installed. tvOS packages SHIP, so silently
    /// downgrading their cell to "built fine" claims coverage that does not
    /// exist; and failing a matrix because a developer has not downloaded a
    /// multi-gigabyte Xcode runtime would make the gate about the machine
    /// rather than the code. Naming the leg unavailable is the honest third
    /// answer.</para>
    /// </summary>
    (TestRunResult? Result, string? Unavailable) ValidateTvOsFor(
        string library, int timeoutSeconds, Action<string>? onLine = null)
    {
        void Info(string line) { if (onLine is null) Log.Information("{Line}", line); else onLine(line); }

        timeoutSeconds = ResolveRunBudgetSeconds(library, timeoutSeconds);
        var (testDir, appName, bundleId) = ResolveTestNames(library);

        // The tvOS sim build is not given an explicit RID, so the SDK picks the
        // host's — probe both rather than hard-coding Apple silicon.
        var rids = string.IsNullOrEmpty(RuntimeIdentifier)
            ? new[] { "tvossimulator-arm64", "tvossimulator-x64" }
            : new[] { RuntimeIdentifier };
        var candidates = rids.Select(r => ResolveAppPath(testDir, "Debug", "tvos", r, appName)).ToList();
        var appPath = candidates.FirstOrDefault(c => Directory.Exists(c));
        if (appPath is null)
            throw new InvalidOperationException(
                "tvOS app bundle not found. Checked:\n  " + string.Join("\n  ", candidates) + "\n" +
                $"Run BuildTestApp --library {library} --platform tvos first.");

        var fleet = new SimulatorFleet();
        var tv = fleet.FindTvOsSimulator();
        if (tv is null)
            return (null, "no tvOS simulator is available on this host (install a tvOS runtime in Xcode)");

        Info($"tvOS simulator: {tv.Name} [{tv.Runtime}] {tv.Udid} (booted={tv.WasBooted})");
        if (tv.WasBooted)
            return (ValidateSimAt(appPath, appName, bundleId, timeoutSeconds, tv.Udid, onLine,
                RequireStoreKitSandboxConfiguration(library, "tvos", bundleId)), null);

        // A tvOS sim we booted ourselves must not outlive this leg. Other lanes
        // install with `simctl install booted`, which becomes ambiguous once a
        // non-iPhone device is also booted and fails with "This app is not made
        // for this device" — a leaked tvOS sim turns every later iOS install on
        // the host red. A sim that was already booted belongs to whoever booted
        // it, so leave that one running.
        fleet.BootAndWaitForReady(tv.Udid);
        try
        {
            return (ValidateSimAt(appPath, appName, bundleId, timeoutSeconds, tv.Udid, onLine,
                RequireStoreKitSandboxConfiguration(library, "tvos", bundleId)), null);
        }
        finally
        {
            try { fleet.ShutdownOne(tv.Udid); }
            catch (Exception ex) { Info($"tvOS simulator {tv.Udid} could not be shut down: {ex.Message}"); }
        }
    }

    /// <summary>
    /// Install + launch a sim test app on a physical device via
    /// <c>devicectl</c>. Direct port of <c>scripts/validate-device.sh</c>.
    /// Honors <c>--aot</c> for the bin path.
    /// </summary>
    Target ValidateDevice => _ => _
        .Description("Install and validate the test app on a physical iOS device (--library Foo [--aot] [--device-udid X])")
        .Requires(() => Library)
        .Executes(() =>
        {
            var result = ValidateDeviceFor(Library!, Timeout, DeviceUdid, Aot, ResolveRid("ios-arm64"));
            ReportTerminalStatus(result, Library!);
        });

    /// <summary>
    /// Pure-function validate-device core. Returns the <see cref="TestRunResult"/>
    /// instead of throwing — callers wrap with <see cref="ReportTerminalStatus"/>.
    /// Auto-detects the device UDID if <paramref name="deviceUdid"/> is null/empty.
    /// </summary>
    TestRunResult ValidateDeviceFor(string library, int timeoutSeconds, string? deviceUdid, bool aot, string rid,
        Action<string>? onLine = null, TestAppDescriptor? resolvedApp = null)
    {
        void Info(string line) { if (onLine is null) Log.Information("{Line}", line); else onLine(line); }

        timeoutSeconds = ResolveRunBudgetSeconds(library, timeoutSeconds);
        var app = resolvedApp ?? ResolveTestApp(library, forDevice: true);
        var (testDir, appName, bundleId) = (app.TestDir, app.AppName, app.BundleId);
        var storeKitConfiguration = RequireStoreKitSandboxConfiguration(library, "ios", bundleId);

        var config = aot ? "Release" : "Debug";
        var appPath = ResolveAppPath(testDir, config, "ios", rid, appName);
        if (!Directory.Exists(appPath))
        {
            var aotFlag = aot ? " --aot" : "";
            throw new InvalidOperationException(
                $"Error: App not found at {appPath}\nRun BuildTestApp --library {library} --device{aotFlag} first.");
        }

        var udid = deviceUdid;
        if (string.IsNullOrEmpty(udid))
        {
            udid = DevicectlClient.AutoDetectDevice();
            if (string.IsNullOrEmpty(udid))
                throw new InvalidOperationException(
                    "Error: No connected device found.\n" +
                    "Connect a device and try again, or specify a device UDID.\n" +
                    "Available devices: xcrun devicectl list devices");
            Info($"Auto-detected device: {udid}");
        }

        VerifyActivityKitPushCapability(app, appPath, udid, onLine);

        var dev = new DevicectlClient(udid);
        dev.InstallApp(appPath);

        var launchBudget = LaunchBudgetSeconds;
        var managedLaunchArguments = storeKitConfiguration is null
            ? Array.Empty<string>()
            : new[]
            {
                StoreKitSandboxReadiness.ProductIdArgument, storeKitConfiguration.ProductId,
            };
        TestRunResult result;
        var priorAttempts = new List<string>();
        for (var attempt = 1; ; attempt++)
        {
            Info($"Launching {appName} on {udid} " +
                 $"(launch budget: {launchBudget}s, run budget: {timeoutSeconds}s, attempt {attempt})");
            var psi = dev.BuildLaunchPsi(bundleId, managedLaunchArguments);
            result = StdoutWatcher
                .RunAsync(psi, TimeSpan.FromSeconds(timeoutSeconds), TimeSpan.FromSeconds(launchBudget), onLine: Info)
                .GetAwaiter().GetResult();

            if (result.Status != TestRunStatus.LauncherAborted)
                break;

            // devicectl aborting before it sends the launch request leaves the app image
            // unentered; that is a launcher condition, not a verdict, so it is the one
            // thing worth re-attempting here. A transcript with loader or crash evidence
            // never classifies as an abort, so a device-side startup crash — which has no
            // host crash log to fall back on — is reported, not re-launched. Every attempt's
            // transcript is kept.
            if (LaunchDiagnostics.LauncherAbortBudgetExhausted(attempt))
            {
                Info($"The launcher aborted before {appName} started, {attempt} time(s): {result.Reason}");
                break;
            }

            priorAttempts.Add(result.Output);
            var settle = LaunchDiagnostics.SettleDelayAfterAbort(attempt);
            Info($"Launcher aborted before {appName} started ({result.Reason}) — " +
                 $"settling {settle.TotalSeconds:F0}s and re-launching");
            Thread.Sleep(settle);
        }

        result = LaunchDiagnostics.WithPriorAttempts(result, priorAttempts);
        Info("=== APP OUTPUT ===\n" + result.Output);
        return result;
    }

    /// <summary>
    /// Compute <c>(testDir, appName, bundleId)</c> from a library name.
    /// Convention: <c>SwiftBindings.{Name}.Tests</c> /
    /// <c>com.swiftbindings.{name}.tests</c>, co-located under the
    /// library or apple-framework directory.
    /// </summary>
    (AbsolutePath TestDir, string AppName, string BundleId) ResolveTestNames(string library)
    {
        var app = ResolveTestApp(library, forDevice: false);
        return (app.TestDir, app.AppName, app.BundleId);
    }

    static int CountCrashLogs(string crashDir, string appName)
    {
        if (!Directory.Exists(crashDir)) return 0;
        try
        {
            return Directory.GetFiles(crashDir, $"{appName}*.ips").Length;
        }
        catch { return 0; }
    }

    static string? NewestCrashLog(string crashDir, string appName)
    {
        if (!Directory.Exists(crashDir)) return null;
        try
        {
            return new DirectoryInfo(crashDir)
                .EnumerateFiles($"{appName}*.ips")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .FirstOrDefault()?.FullName;
        }
        catch { return null; }
    }

    static string HeadOfFile(string path, int lines)
    {
        try
        {
            using var sr = new StreamReader(path);
            var sb = new System.Text.StringBuilder();
            for (var i = 0; i < lines; i++)
            {
                var line = sr.ReadLine();
                if (line is null) break;
                sb.AppendLine(line);
            }
            return sb.ToString();
        }
        catch (Exception ex) { return $"(failed to read crash log: {ex.Message})"; }
    }

    /// <summary>
    /// Validate a Mac test app by running the built binary and watching
    /// stdout for <c>TEST SUCCESS</c> / <c>TEST FAILED</c>. Much simpler than
    /// the iOS sim path — no install step, no simulator management.
    /// </summary>
    Target ValidateMac => _ => _
        .Description("Validate a Mac test app (--library X [--timeout N])")
        .Requires(() => Library)
        .Executes(() =>
        {
            var result = ValidateMacFor(Library!, Timeout, ResolveRid("osx-arm64"));
            ReportTerminalStatus(result, Library!);
        });

    /// <summary>
    /// Pure-function validate-mac core. Returns the <see cref="TestRunResult"/>
    /// instead of throwing — callers wrap with <see cref="ReportTerminalStatus"/>.
    /// </summary>
    TestRunResult ValidateMacFor(string library, int timeoutSeconds, string rid, Action<string>? onLine = null)
    {
        void Info(string line) { if (onLine is null) Log.Information("{Line}", line); else onLine(line); }

        timeoutSeconds = ResolveRunBudgetSeconds(library, timeoutSeconds);
        var (testDir, appName, _) = ResolveTestNames(library);

        var config = "Debug";
        var ridBase = testDir / "bin" / config / "net10.0-macos" / rid;
        var binBase = testDir / "bin" / config / "net10.0-macos";

        string? binaryPath = null;
        var candidates = new[]
        {
            ridBase / $"{appName}.app" / "Contents" / "MacOS" / appName,
            ridBase / appName,
            binBase / $"{appName}.app" / "Contents" / "MacOS" / appName,
            binBase / appName,
        };
        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                binaryPath = candidate;
                break;
            }
        }
        if (binaryPath is null)
            throw new InvalidOperationException(
                "Mac binary not found. Checked:\n  " +
                string.Join("\n  ", candidates) + "\n" +
                $"Run BuildTestApp --library {library} --platform macos first.");

        Info($"Launching Mac test: {binaryPath} " +
             $"(launch budget: {LaunchBudgetSeconds}s, run budget: {timeoutSeconds}s)");

        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = binaryPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        var result = StdoutWatcher
            .RunAsync(psi, TimeSpan.FromSeconds(timeoutSeconds), TimeSpan.FromSeconds(LaunchBudgetSeconds), onLine: Info)
            .GetAwaiter().GetResult();

        Info("=== APP OUTPUT ===\n" + result.Output);
        return result;
    }

    /// <summary>
    /// Validate a MacCatalyst test app by running the bundled binary and
    /// watching stdout for <c>TEST SUCCESS</c> / <c>TEST FAILED</c>. Catalyst
    /// produces a UIKit-on-Mac <c>.app</c> bundle; the executable inside
    /// <c>Contents/MacOS/&lt;AppName&gt;</c> writes Console output via the
    /// unified logging system, which still pipes to the parent shell when the
    /// binary is launched directly. (Launching via <c>open</c> would detach
    /// the process and lose stdout.)
    /// </summary>
    Target ValidateMacCatalyst => _ => _
        .Description("Validate a MacCatalyst test app (--library X [--timeout N])")
        .Requires(() => Library)
        .Executes(() =>
        {
            var result = ValidateMacCatalystFor(Library!, Timeout, ResolveRid("maccatalyst-arm64"));
            ReportTerminalStatus(result, Library!);
        });

    /// <summary>
    /// Pure-function validate-maccatalyst core. Mirrors
    /// <see cref="ValidateMacFor"/> but resolves the bundled binary inside
    /// <c>&lt;App&gt;.app/Contents/MacOS/&lt;App&gt;</c>. Returns the
    /// <see cref="TestRunResult"/> instead of throwing — callers wrap with
    /// <see cref="ReportTerminalStatus"/>.
    /// </summary>
    TestRunResult ValidateMacCatalystFor(string library, int timeoutSeconds, string rid,
        Action<string>? onLine = null)
    {
        void Info(string line) { if (onLine is null) Log.Information("{Line}", line); else onLine(line); }

        timeoutSeconds = ResolveRunBudgetSeconds(library, timeoutSeconds);
        var (testDir, appName, _) = ResolveTestNames(library);
        var appPath = ResolveAppPath(testDir, "Debug", "maccatalyst", rid, appName);
        var binaryPath = appPath / "Contents" / "MacOS" / appName;

        if (!File.Exists(binaryPath))
            throw new InvalidOperationException(
                $"MacCatalyst binary not found at {binaryPath}\n" +
                $"Run BuildTestApp --library {library} --platform maccatalyst first.");

        Info($"Launching MacCatalyst test: {binaryPath} " +
             $"(launch budget: {LaunchBudgetSeconds}s, run budget: {timeoutSeconds}s)");

        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = binaryPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        var result = StdoutWatcher
            .RunAsync(psi, TimeSpan.FromSeconds(timeoutSeconds), TimeSpan.FromSeconds(LaunchBudgetSeconds), onLine: Info)
            .GetAwaiter().GetResult();

        Info("=== APP OUTPUT ===\n" + result.Output);
        return result;
    }

    static void ReportTerminalStatus(TestRunResult result, string library)
    {
        switch (result.Status)
        {
            case TestRunStatus.Success:
                Log.Information("=== VALIDATION PASSED ({Library}) ===", library);
                return;
            case TestRunStatus.Failed:
                throw new InvalidOperationException($"=== TEST FAILED ({library}) === {result.Reason}");
            case TestRunStatus.Crashed:
                throw new InvalidOperationException($"=== CRASH DETECTED ({library}) === {result.Reason}");
            case TestRunStatus.Timeout:
                throw new InvalidOperationException($"=== TIMEOUT ({library}) === {result.Reason}");
            case TestRunStatus.Exited:
                throw new InvalidOperationException($"=== APP EXITED without success marker ({library}) === {result.Reason}");
            case TestRunStatus.LauncherAborted:
                // Still terminal and still loud — but named for what it is. The app was never
                // entered, so this run says nothing about the library either way, and reading
                // it as a test failure sends the next person hunting for a defect the evidence
                // does not support.
                throw new InvalidOperationException(
                    $"=== LAUNCHER ABORTED ({library}) === {result.Reason}; the app never ran, so this run carries no verdict");
            default:
                throw new InvalidOperationException($"Unexpected terminal status: {result.Status}");
        }
    }
}
