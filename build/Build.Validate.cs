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
            var appPath = ResolveAppPath(testDir, "Debug", "ios", rid, appName);
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
            var appPath = ResolveAppPath(testDir, config, "ios", rid, appName);
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
    /// Compute <c>(testDir, appName, bundleId)</c> from a library name.
    /// New convention: <c>SwiftBindings.{Name}.Tests</c> /
    /// <c>com.swiftbindings.{name}.tests</c>. Falls back to the legacy
    /// <c>{Name}SimTests</c> convention if the new <c>tests/</c> dir doesn't
    /// exist but the old <c>tests/{Name}.SimTests/</c> does.
    /// </summary>
    (AbsolutePath TestDir, string AppName, string BundleId) ResolveTestNames(string library)
    {
        // New convention: co-located under the library/framework dir
        var newTestDir = LibraryDir(library) / "tests";
        if (Directory.Exists(newTestDir))
        {
            var appName = $"SwiftBindings.{library}.Tests";
            var bundleId = $"com.swiftbindings.{library.ToLowerInvariant()}.tests";
            return (newTestDir, appName, bundleId);
        }

        // Legacy convention: flat tests/<Name>.SimTests/
        var legacyTestDir = TestsDir / $"{library}.SimTests";
        if (Directory.Exists(legacyTestDir))
        {
            var appName = $"{library}SimTests";
            var bundleId = $"com.swiftbindings.{library.ToLowerInvariant()}simtests";
            return (legacyTestDir, appName, bundleId);
        }

        throw new InvalidOperationException(
            $"Test directory not found for '{library}'. " +
            $"Checked: {newTestDir} and {legacyTestDir}");
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
            var library = Library!;
            var (testDir, appName, _) = ResolveTestNames(library);

            // Mac console apps land at bin/<config>/net10.0-macos/<appName>
            // (no .app bundle for console executables).
            var config = "Debug";
            var binBase = testDir / "bin" / config / "net10.0-macos";

            // Try the console binary path first, then the .app bundle path
            string? binaryPath = null;
            var consolePath = binBase / appName;
            var appBundlePath = binBase / $"{appName}.app" / "Contents" / "MacOS" / appName;

            if (File.Exists(consolePath))
                binaryPath = consolePath;
            else if (File.Exists(appBundlePath))
                binaryPath = appBundlePath;
            else
                throw new InvalidOperationException(
                    $"Mac binary not found. Checked:\n  {consolePath}\n  {appBundlePath}\n" +
                    $"Run BuildTestApp --library {library} --platform macos first.");

            Log.Information("Launching Mac test: {Binary} (timeout: {Timeout}s)", binaryPath, Timeout);

            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = binaryPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            var result = StdoutWatcher
                .RunAsync(psi, TimeSpan.FromSeconds(Timeout), onLine: line => Log.Information("{Line}", line))
                .GetAwaiter().GetResult();

            Log.Information("=== APP OUTPUT ===\n{Output}", result.Output);
            ReportTerminalStatus(result, library);
        });

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
