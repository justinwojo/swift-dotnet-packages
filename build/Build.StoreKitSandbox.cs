// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Nuke.Common.IO;
using SwiftBindings.Build.Helpers;
using SwiftBindings.Build.Models;

partial class Build
{
    const string StoreKit2Library = "StoreKit2";
    const string StoreKit2BundleId = "com.swiftbindings.storekit2.tests";
    internal const int StoreKitNativeSdkLookupBudgetSeconds = 30;
    internal const int StoreKitNativeCompileBudgetSeconds = 120;
    internal const int StoreKitNativeCodesignBudgetSeconds = 30;
    internal const int StoreKitNativeInstallBudgetSeconds = 120;
    internal const int StoreKitNativeLaunchBudgetSeconds = 30;
    internal const int StoreKitNativeRunBudgetSeconds = 75;
    internal const int StoreKitNativePhaseBudgetSeconds =
        StoreKitNativeSdkLookupBudgetSeconds
        + StoreKitNativeCompileBudgetSeconds
        + StoreKitNativeCodesignBudgetSeconds
        + StoreKitNativeInstallBudgetSeconds
        + StoreKitNativeLaunchBudgetSeconds
        + StoreKitNativeRunBudgetSeconds;

    string? EffectiveStoreKitSandboxProductId =>
        !string.IsNullOrWhiteSpace(StoreKitSandboxProductId)
            ? StoreKitSandboxProductId
            : Environment.GetEnvironmentVariable(StoreKitSandboxReadiness.ProductIdEnvironmentVariable);

    StoreKitSandboxReadiness.Configuration? RequireStoreKitSandboxConfiguration(
        string library,
        string platform,
        string bundleId)
    {
        if (!string.Equals(library, StoreKit2Library, StringComparison.Ordinal)
            || platform is not ("ios" or "tvos"))
            return null;
        return StoreKitSandboxReadiness.RequireConfiguration(
            EffectiveStoreKitSandboxProductId, platform, bundleId);
    }

    /// <summary>
    /// The runtime-lane counterpart of <see cref="RequireStoreKitSandboxConfiguration"/>, used by
    /// the validators the regression matrix drives. Returns <c>null</c> for any library other than
    /// StoreKit2. A missing product id yields an offline-only lane instead of throwing, so the
    /// offline suite still runs and the verdict still cannot be a pass; a malformed id throws.
    /// CI and the release-consumption lane keep the fail-fast
    /// <see cref="RequireStoreKitSandboxConfiguration"/>: a single-verdict release gate gains
    /// nothing from spending a build and a launch to reach the same non-pass.
    /// </summary>
    StoreKitSandboxReadiness.Lane? ResolveStoreKitSandboxLane(string library, string platform, string bundleId) =>
        string.Equals(library, StoreKit2Library, StringComparison.Ordinal)
            ? StoreKitSandboxReadiness.ResolveLane(EffectiveStoreKitSandboxProductId, platform, bundleId)
            : null;

    static string StoreKitLanePlatform(CellPlatform platform) => platform switch
    {
        CellPlatform.IosSim or CellPlatform.IosDevice or CellPlatform.IosDeviceMonoAot => "ios",
        CellPlatform.TvOs => "tvos",
        CellPlatform.MacOs => "macos",
        CellPlatform.MacCatalyst => "maccatalyst",
        _ => throw new InvalidOperationException($"no StoreKit lane platform for {platform}"),
    };

    static string[] StoreKitManagedLaunchArguments(StoreKitSandboxReadiness.Lane? lane) =>
        lane?.Configuration is { } configuration
            ? [StoreKitSandboxReadiness.ProductIdArgument, configuration.ProductId]
            : [];

    static TestRunResult ApplyStoreKitLaneVerdict(TestRunResult result, StoreKitSandboxReadiness.Lane? lane) =>
        lane is null ? result : StoreKitSandboxReadiness.ApplyLaneVerdict(result, lane);

    /// <summary>
    /// Build and run the pure-Swift four-operation control on the exact simulator and bundle id
    /// the managed StoreKit app will use. The managed app is not installed until this method has
    /// parsed a fresh Sandbox receipt for the current token.
    /// </summary>
    StoreKitSandboxReadiness.Receipt RunStoreKitSandboxNativeControl(
        StoreKitSandboxReadiness.Configuration configuration,
        string simulatorUdid,
        Action<string>? onLine)
    {
        void Info(string line)
        {
            if (onLine is null) Serilog.Log.Information("{Line}", line);
            else onLine(line);
        }

        var controlDir = RootDirectory / "apple-frameworks" / "StoreKit2" / "tests" / "native-control";
        var source = controlDir / "App.swift";
        var plistTemplate = controlDir / "SKControl-Info.plist";
        if (!File.Exists(source) || !File.Exists(plistTemplate))
            throw new InvalidOperationException(
                $"StoreKit Sandbox native-control sources are missing under {controlDir}.");

        var sdkName = configuration.Platform == "ios" ? "iphonesimulator" : "appletvsimulator";
        var target = configuration.Platform == "ios"
            ? "arm64-apple-ios16.0-simulator"
            : "arm64-apple-tvos16.0-simulator";
        var supportedPlatform = configuration.Platform == "ios" ? "iPhoneSimulator" : "AppleTVSimulator";
        var deviceFamily = configuration.Platform == "ios" ? "1" : "3";

        var scratch = (AbsolutePath)(Path.Combine(Path.GetTempPath(),
            "swiftbindings-storekit-control", Guid.NewGuid().ToString("N")));
        var appPath = scratch / "SKControl.app";
        Directory.CreateDirectory(appPath);
        try
        {
            var sdk = CaptureStoreKitProcess("/usr/bin/xcrun",
                ["--sdk", sdkName, "--show-sdk-path"],
                TimeSpan.FromSeconds(StoreKitNativeSdkLookupBudgetSeconds));
            if (sdk.ExitCode != 0 || string.IsNullOrWhiteSpace(sdk.Stdout))
                throw new InvalidOperationException(
                    $"StoreKit Sandbox native-control SDK lookup failed for {sdkName}: {sdk.Combined.Trim()}");

            var compile = CaptureStoreKitProcess("/usr/bin/xcrun",
            [
                "swiftc", "-target", target, "-sdk", sdk.Stdout.Trim(), "-parse-as-library",
                source, "-o", appPath / "SKControl",
            ], TimeSpan.FromSeconds(StoreKitNativeCompileBudgetSeconds));
            if (compile.ExitCode != 0)
                throw new InvalidOperationException(
                    $"StoreKit Sandbox native-control compile failed: {compile.Combined.Trim()}");

            var plist = File.ReadAllText(plistTemplate)
                .Replace("__BUNDLE_ID__", configuration.BundleId, StringComparison.Ordinal)
                .Replace("__SUPPORTED_PLATFORM__", supportedPlatform, StringComparison.Ordinal)
                .Replace("__PLATFORM_NAME__", sdkName, StringComparison.Ordinal)
                .Replace("__MINIMUM_OS__", "16.0", StringComparison.Ordinal)
                .Replace("__DEVICE_FAMILY__", deviceFamily, StringComparison.Ordinal);
            File.WriteAllText(appPath / "Info.plist", plist);

            var sign = CaptureStoreKitProcess("/usr/bin/codesign",
                ["--force", "--sign", "-", appPath],
                TimeSpan.FromSeconds(StoreKitNativeCodesignBudgetSeconds));
            if (sign.ExitCode != 0)
                throw new InvalidOperationException(
                    $"StoreKit Sandbox native-control signing failed: {sign.Combined.Trim()}");

            var runToken = Guid.NewGuid().ToString("N");
            var sim = new SimctlClient(simulatorUdid,
                configuration.Platform == "ios" ? SimulatorFamily.Ios : SimulatorFamily.TvOs);
            sim.InstallApp(appPath, TimeSpan.FromSeconds(StoreKitNativeInstallBudgetSeconds));
            sim.CleanStaleConsoleFifos();
            var launchArguments = new[]
            {
                "--storekit-platform", configuration.Platform,
                "--storekit-bundle-id", configuration.BundleId,
                StoreKitSandboxReadiness.ProductIdArgument, configuration.ProductId,
                StoreKitSandboxReadiness.RunTokenArgument, runToken,
            };
            Info($"StoreKit Sandbox native readiness control: platform={configuration.Platform} " +
                 $"device={simulatorUdid} bundle={configuration.BundleId} product={configuration.ProductId}");
            TestRunResult result;
            try
            {
                result = StdoutWatcher.RunAsync(
                        sim.BuildLaunchPsi(configuration.BundleId, launchArguments),
                        TimeSpan.FromSeconds(StoreKitNativeRunBudgetSeconds),
                        TimeSpan.FromSeconds(StoreKitNativeLaunchBudgetSeconds),
                        onLine: Info)
                    .GetAwaiter().GetResult();
            }
            finally
            {
                sim.TerminateApp(configuration.BundleId);
                // The native and managed apps share this bundle and launch back-to-back. Once the
                // native watcher has exited and the app is terminated, every remaining console FIFO
                // for this exact simulator is orphaned regardless of age.
                sim.CleanStaleConsoleFifos(TimeSpan.Zero);
            }
            if (result.Status != TestRunStatus.Success)
                throw new InvalidOperationException(
                    $"StoreKit Sandbox native readiness control did not pass ({result.Status}): {result.Reason}\n{result.Output}");

            var receipt = StoreKitSandboxReadiness.RequireFreshNativeReceipt(
                result.Output, configuration, runToken);
            RetainStoreKitSandboxEvidence(configuration, simulatorUdid, receipt, result.Output);
            Info("StoreKit Sandbox native readiness established for this run; proceeding to managed launch.");
            return receipt;
        }
        finally
        {
            if (Directory.Exists(scratch))
                scratch.DeleteDirectory();
        }
    }

    void RetainStoreKitSandboxEvidence(
        StoreKitSandboxReadiness.Configuration configuration,
        string simulatorUdid,
        StoreKitSandboxReadiness.Receipt receipt,
        string output)
    {
        var evidenceDir = RootDirectory / "artifacts" / "storekit-sandbox-readiness" /
            $"{DateTime.UtcNow:yyyyMMdd-HHmmss.fffZ}-p{Environment.ProcessId}-{configuration.Platform}";
        Directory.CreateDirectory(evidenceDir);
        File.WriteAllText(evidenceDir / "native-control.log", output);
        var outputHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(output))).ToLowerInvariant();
        File.WriteAllText(evidenceDir / "summary.txt",
            $"platform={configuration.Platform}\n" +
            $"simulator_udid={simulatorUdid}\n" +
            $"bundle_id={configuration.BundleId}\n" +
            $"product_id={configuration.ProductId}\n" +
            $"run_token={receipt.RunToken}\n" +
            $"app_transaction_environment=sandbox\n" +
            $"products={receipt.ProductCount}\n" +
            $"current_entitlements={receipt.CurrentEntitlementsCount}\n" +
            $"all_transactions={receipt.AllTransactionsCount}\n" +
            $"native_control_sha256={outputHash}\n");
    }

    static (int ExitCode, string Stdout, string Stderr, string Combined) CaptureStoreKitProcess(
        string fileName,
        IReadOnlyList<string> arguments,
        TimeSpan timeout)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
            process.StartInfo.ArgumentList.Add(argument);
        process.Start();
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit((int)timeout.TotalMilliseconds))
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            process.WaitForExit();
            var timedOutStdout = stdoutTask.GetAwaiter().GetResult();
            var timedOutStderr = stderrTask.GetAwaiter().GetResult();
            throw new TimeoutException(
                $"StoreKit native-control host command '{fileName}' timed out after " +
                $"{timeout.TotalSeconds:0}s:\n{timedOutStdout}\n{timedOutStderr}");
        }
        process.WaitForExit();
        var stdout = stdoutTask.GetAwaiter().GetResult();
        var stderr = stderrTask.GetAwaiter().GetResult();
        return (process.ExitCode, stdout, stderr, stdout + "\n" + stderr);
    }
}
