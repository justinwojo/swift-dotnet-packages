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
            var library = Library!;
            var (testDir, appName, bundleId) = ResolveTestNames(library);
            var rid = ResolveRid("iossimulator-arm64");
            var appPath = testDir / "bin" / "Debug" / "net10.0-ios" / rid / $"{appName}.app";
            if (!Directory.Exists(appPath))
                throw new InvalidOperationException(
                    $"Error: App not found at {appPath}\nRun BuildTestApp --library {library} first.");

            var sim = new SimctlClient(DeviceUdid);
            var crashDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Library", "Logs", "DiagnosticReports");
            var beforeCrashCount = CountCrashLogs(crashDir, appName);

            sim.InstallApp(appPath);

            Log.Information("Launching {App} on {Device} (timeout: {Timeout}s)", appName, sim.Device, Timeout);
            var psi = sim.BuildLaunchPsi(bundleId);
            var result = StdoutWatcher
                .RunAsync(psi, TimeSpan.FromSeconds(Timeout), onLine: line => Log.Information("{Line}", line))
                .GetAwaiter().GetResult();

            sim.TerminateApp(bundleId);

            // Crash-log post-check (matches validate-sim.sh:96–103)
            var afterCrashCount = CountCrashLogs(crashDir, appName);
            if (afterCrashCount > beforeCrashCount)
            {
                var newest = NewestCrashLog(crashDir, appName);
                Log.Error("=== CRASH LOG DETECTED ===");
                if (newest is not null)
                    Log.Error("{Path}\n{Head}", newest, HeadOfFile(newest, 50));
                throw new InvalidOperationException("Crash log appeared after sim test run");
            }

            Log.Information("=== APP OUTPUT ===\n{Output}", result.Output);
            ReportTerminalStatus(result, library);
        });

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
            var library = Library!;
            var (testDir, appName, bundleId) = ResolveTestNames(library);

            var config = Aot ? "Release" : "Debug";
            var rid = ResolveRid("ios-arm64");
            var appPath = testDir / "bin" / config / "net10.0-ios" / rid / $"{appName}.app";
            if (!Directory.Exists(appPath))
            {
                var aotFlag = Aot ? " --aot" : "";
                throw new InvalidOperationException(
                    $"Error: App not found at {appPath}\nRun BuildTestApp --library {library} --device{aotFlag} first.");
            }

            var udid = DeviceUdid;
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

            Log.Information("Launching {App} on {Device} (timeout: {Timeout}s)", appName, udid, Timeout);
            var psi = dev.BuildLaunchPsi(bundleId);
            var result = StdoutWatcher
                .RunAsync(psi, TimeSpan.FromSeconds(Timeout), onLine: line => Log.Information("{Line}", line))
                .GetAwaiter().GetResult();

            Log.Information("=== APP OUTPUT ===\n{Output}", result.Output);
            ReportTerminalStatus(result, library);
        });

    /// <summary>
    /// Compute <c>(testDir, appName, bundleId)</c> from a library name. Mirrors
    /// the basename derivation in both validate scripts:
    /// <c>tests/Foo.SimTests</c> → <c>FooSimTests</c> / <c>com.swiftbindings.foosimtests</c>.
    /// </summary>
    (AbsolutePath TestDir, string AppName, string BundleId) ResolveTestNames(string library)
    {
        var testDir = TestDir(library);
        if (!Directory.Exists(testDir))
            throw new InvalidOperationException($"Test directory not found: {testDir}");
        var appName = $"{library}SimTests";
        var bundleId = $"com.swiftbindings.{library.ToLowerInvariant()}simtests";
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
