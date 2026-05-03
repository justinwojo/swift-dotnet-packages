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
    TestRunResult ValidateSimFor(string library, int timeoutSeconds, string? deviceUdid, string rid)
    {
        var (testDir, appName, bundleId) = ResolveTestNames(library);
        var appPath = ResolveAppPath(testDir, "Debug", "ios", rid, appName);
        if (!Directory.Exists(appPath))
            throw new InvalidOperationException(
                $"Error: App not found at {appPath}\nRun BuildTestApp --library {library} first.");

        var sim = new SimctlClient(deviceUdid);
        var crashDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Library", "Logs", "DiagnosticReports");
        var beforeCrashCount = CountCrashLogs(crashDir, appName);

        sim.InstallApp(appPath);

        Log.Information("Launching {App} on {Device} (timeout: {Timeout}s)", appName, sim.Device, timeoutSeconds);
        var psi = sim.BuildLaunchPsi(bundleId);
        var result = StdoutWatcher
            .RunAsync(psi, TimeSpan.FromSeconds(timeoutSeconds), onLine: line => Log.Information("{Line}", line))
            .GetAwaiter().GetResult();

        sim.TerminateApp(bundleId);

        var afterCrashCount = CountCrashLogs(crashDir, appName);
        if (afterCrashCount > beforeCrashCount)
        {
            var newest = NewestCrashLog(crashDir, appName);
            Log.Error("=== CRASH LOG DETECTED ===");
            if (newest is not null)
                Log.Error("{Path}\n{Head}", newest, HeadOfFile(newest, 50));
            return new TestRunResult(TestRunStatus.Crashed, "Crash log appeared after sim test run", result.Output, result.CrashLogs);
        }

        Log.Information("=== APP OUTPUT ===\n{Output}", result.Output);
        return result;
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
    TestRunResult ValidateDeviceFor(string library, int timeoutSeconds, string? deviceUdid, bool aot, string rid)
    {
        var (testDir, appName, bundleId) = ResolveTestNames(library);

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
            Log.Information("Auto-detected device: {Udid}", udid);
        }

        var dev = new DevicectlClient(udid);
        dev.InstallApp(appPath);

        Log.Information("Launching {App} on {Device} (timeout: {Timeout}s)", appName, udid, timeoutSeconds);
        var psi = dev.BuildLaunchPsi(bundleId);
        var result = StdoutWatcher
            .RunAsync(psi, TimeSpan.FromSeconds(timeoutSeconds), onLine: line => Log.Information("{Line}", line))
            .GetAwaiter().GetResult();

        Log.Information("=== APP OUTPUT ===\n{Output}", result.Output);
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
        var testDir = LibraryDir(library) / "tests";
        if (!Directory.Exists(testDir))
            throw new InvalidOperationException(
                $"Test directory not found for '{library}'. Expected: {testDir}");

        var appName = $"SwiftBindings.{library}.Tests";
        var bundleId = $"com.swiftbindings.{library.ToLowerInvariant()}.tests";
        return (testDir, appName, bundleId);
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
    TestRunResult ValidateMacFor(string library, int timeoutSeconds, string rid)
    {
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

        Log.Information("Launching Mac test: {Binary} (timeout: {Timeout}s)", binaryPath, timeoutSeconds);

        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = binaryPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        var result = StdoutWatcher
            .RunAsync(psi, TimeSpan.FromSeconds(timeoutSeconds), onLine: line => Log.Information("{Line}", line))
            .GetAwaiter().GetResult();

        Log.Information("=== APP OUTPUT ===\n{Output}", result.Output);
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
    TestRunResult ValidateMacCatalystFor(string library, int timeoutSeconds, string rid)
    {
        var (testDir, appName, _) = ResolveTestNames(library);
        var appPath = ResolveAppPath(testDir, "Debug", "maccatalyst", rid, appName);
        var binaryPath = appPath / "Contents" / "MacOS" / appName;

        if (!File.Exists(binaryPath))
            throw new InvalidOperationException(
                $"MacCatalyst binary not found at {binaryPath}\n" +
                $"Run BuildTestApp --library {library} --platform maccatalyst first.");

        Log.Information("Launching MacCatalyst test: {Binary} (timeout: {Timeout}s)", binaryPath, timeoutSeconds);

        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = binaryPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        var result = StdoutWatcher
            .RunAsync(psi, TimeSpan.FromSeconds(timeoutSeconds), onLine: line => Log.Information("{Line}", line))
            .GetAwaiter().GetResult();

        Log.Information("=== APP OUTPUT ===\n{Output}", result.Output);
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
            default:
                throw new InvalidOperationException($"Unexpected terminal status: {result.Status}");
        }
    }
}
