// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Diagnostics;
using Nuke.Common.IO;
using SwiftBindings.Build.Helpers;
using SwiftBindings.Build.Models;

partial class Build
{
    internal const int VisionKitWarmupSdkLookupBudgetSeconds = 30;
    internal const int VisionKitWarmupCompileBudgetSeconds = 120;
    internal const int VisionKitWarmupCodesignBudgetSeconds = 30;
    internal const int VisionKitWarmupInstallBudgetSeconds = 120;
    internal const int VisionKitWarmupLaunchBudgetSeconds = 60;
    // The daemon's first-run migration took up to 356 s on a loaded host; this bound is the
    // warm-up's own and does not come out of the managed suite's budgets.
    internal const int VisionKitWarmupRunBudgetSeconds = 600;
    internal const int VisionKitWarmupPhaseBudgetSeconds =
        VisionKitWarmupSdkLookupBudgetSeconds
        + VisionKitWarmupCompileBudgetSeconds
        + VisionKitWarmupCodesignBudgetSeconds
        + VisionKitWarmupInstallBudgetSeconds
        + VisionKitWarmupLaunchBudgetSeconds
        + VisionKitWarmupRunBudgetSeconds;

    // Simulator boots this process has already warmed (VisionKitWarmup.BootKey). A boot the key
    // cannot identify is warmed every time, which is cheap once the daemon is up.
    static readonly HashSet<string> s_visionKitWarmedBoots = new(StringComparer.Ordinal);
    static readonly object s_visionKitWarmupGate = new();

    /// <summary>
    /// Once per simulator boot, before the managed VisionKit app is installed, run one throwaway
    /// pure-Swift image analysis and wait for it, so mediaanalysisd's first-run migration is paid
    /// here, under its own bound, and not inside the managed suite's first analysis. No-op for
    /// every other library and platform. Throws with the daemon's log when the analysis does not
    /// complete in time.
    /// </summary>
    void EnsureVisionKitDaemonWarm(string library, string platform, string simulatorUdid, Action<string>? onLine)
    {
        if (!VisionKitWarmup.AppliesTo(library, platform))
            return;

        void Info(string line)
        {
            if (onLine is null) Serilog.Log.Information("{Line}", line);
            else onLine(line);
        }

        lock (s_visionKitWarmupGate)
        {
            var bootKey = ReadSimulatorBootKey(simulatorUdid);
            if (bootKey is not null && s_visionKitWarmedBoots.Contains(bootKey))
            {
                Info($"VisionKit warm-up: simulator {simulatorUdid} was already warmed during this boot ({bootKey}).");
                return;
            }

            var receipt = RunVisionKitWarmup(simulatorUdid, Info);
            Info($"VisionKit warm-up: analysis daemon ready on {simulatorUdid} after {receipt.Milliseconds} ms " +
                 $"(outcome={receipt.Outcome}{(receipt.Detail.Length == 0 ? "" : ", " + receipt.Detail)}); " +
                 "proceeding to the managed app.");
            // Read again: a boot key taken only after the warm-up still names the boot it warmed.
            bootKey ??= ReadSimulatorBootKey(simulatorUdid);
            if (bootKey is not null)
                s_visionKitWarmedBoots.Add(bootKey);
        }
    }

    VisionKitWarmup.Receipt RunVisionKitWarmup(string simulatorUdid, Action<string> info)
    {
        var warmupDir = RootDirectory / "apple-frameworks" / "VisionKit" / "tests" / "warmup";
        var source = warmupDir / "App.swift";
        var plist = warmupDir / "VKWarmup-Info.plist";
        if (!File.Exists(source) || !File.Exists(plist))
            throw new InvalidOperationException($"VisionKit warm-up sources are missing under {warmupDir}.");

        var scratch = (AbsolutePath)Path.Combine(Path.GetTempPath(),
            "swiftbindings-visionkit-warmup", Guid.NewGuid().ToString("N"));
        var appPath = scratch / "VKWarmup.app";
        Directory.CreateDirectory(appPath);
        try
        {
            var sdk = CaptureStoreKitProcess("/usr/bin/xcrun", ["--sdk", "iphonesimulator", "--show-sdk-path"],
                TimeSpan.FromSeconds(VisionKitWarmupSdkLookupBudgetSeconds));
            if (sdk.ExitCode != 0 || string.IsNullOrWhiteSpace(sdk.Stdout))
                throw new InvalidOperationException($"VisionKit warm-up SDK lookup failed: {sdk.Combined.Trim()}");

            var compile = CaptureStoreKitProcess("/usr/bin/xcrun",
            [
                "swiftc", "-target", "arm64-apple-ios16.0-simulator", "-sdk", sdk.Stdout.Trim(),
                "-parse-as-library", source, "-o", appPath / "VKWarmup",
            ], TimeSpan.FromSeconds(VisionKitWarmupCompileBudgetSeconds));
            if (compile.ExitCode != 0)
                throw new InvalidOperationException($"VisionKit warm-up compile failed: {compile.Combined.Trim()}");
            File.Copy(plist, appPath / "Info.plist");

            var sign = CaptureStoreKitProcess("/usr/bin/codesign", ["--force", "--sign", "-", appPath],
                TimeSpan.FromSeconds(VisionKitWarmupCodesignBudgetSeconds));
            if (sign.ExitCode != 0)
                throw new InvalidOperationException($"VisionKit warm-up signing failed: {sign.Combined.Trim()}");

            var sim = new SimctlClient(simulatorUdid);
            sim.InstallApp(appPath, TimeSpan.FromSeconds(VisionKitWarmupInstallBudgetSeconds));
            sim.CleanStaleConsoleFifos();

            info($"VisionKit warm-up: one throwaway analysis on {simulatorUdid} " +
                 $"(bound {VisionKitWarmupRunBudgetSeconds}s) before the managed VisionKit app");
            var daemonLines = new List<string>();
            using var logStream = StartSimulatorLogStream(simulatorUdid, VisionKitWarmup.DaemonLogPredicate, line =>
            {
                lock (daemonLines) daemonLines.Add(line.Trim());
                if (VisionKitWarmup.IsMigrationStartLine(line) || VisionKitWarmup.IsCompletionLine(line))
                    info($"VisionKit warm-up daemon: {line.Trim()}");
            });
            TestRunResult result;
            try
            {
                result = StdoutWatcher.RunAsync(
                        sim.BuildLaunchPsi(VisionKitWarmup.BundleId),
                        TimeSpan.FromSeconds(VisionKitWarmupRunBudgetSeconds),
                        TimeSpan.FromSeconds(VisionKitWarmupLaunchBudgetSeconds),
                        onLine: info)
                    .GetAwaiter().GetResult();
            }
            finally
            {
                sim.TerminateApp(VisionKitWarmup.BundleId);
                sim.CleanStaleConsoleFifos(TimeSpan.Zero);
                StopProcess(logStream);
                UninstallBestEffort(simulatorUdid, VisionKitWarmup.BundleId, info);
            }

            List<string> snapshot;
            lock (daemonLines) snapshot = [.. daemonLines];
            if (result.Status != TestRunStatus.Success)
                throw new InvalidOperationException(VisionKitWarmup.NotWarmMessage(
                    simulatorUdid, VisionKitWarmupRunBudgetSeconds, result.Status.ToString(), result.Reason, snapshot)
                    + "\nWarm-up app output:\n" + result.Output);
            var receipt = VisionKitWarmup.RequireReady(result.Output, simulatorUdid);
            info($"VisionKit warm-up daemon log: {snapshot.Count} line(s), migration " +
                 $"{(snapshot.Any(VisionKitWarmup.IsMigrationStartLine) ? "ran during this warm-up" : "not seen (already done)")}, " +
                 $"completion line {(snapshot.Any(VisionKitWarmup.IsCompletionLine) ? "seen" : "not seen")}");
            return receipt;
        }
        finally
        {
            if (Directory.Exists(scratch))
                scratch.DeleteDirectory();
        }
    }

    static string? ReadSimulatorBootKey(string simulatorUdid)
    {
        try
        {
            var ps = CaptureStoreKitProcess("/bin/ps", ["-axo", "pid=,lstart=,command="], TimeSpan.FromSeconds(15));
            return ps.ExitCode == 0 ? VisionKitWarmup.BootKey(simulatorUdid, ps.Stdout) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// <c>simctl spawn &lt;udid&gt; log stream</c> filtered by <paramref name="predicate"/>, each line handed to
    /// <paramref name="onLine"/> as it arrives. The caller stops it with <see cref="StopProcess"/>.
    /// </summary>
    static Process StartSimulatorLogStream(string simulatorUdid, string predicate, Action<string> onLine)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo("/usr/bin/xcrun")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            },
        };
        foreach (var argument in new[]
                 {
                     "simctl", "spawn", simulatorUdid, "log", "stream", "--style", "compact",
                     "--level", "default", "--predicate", predicate,
                 })
            process.StartInfo.ArgumentList.Add(argument);
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) onLine(e.Data); };
        process.ErrorDataReceived += (_, _) => { };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
    }

    static void StopProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(10_000);
            }
        }
        catch (Exception)
        {
            // Best effort: the stream is evidence only.
        }
    }

    static void UninstallBestEffort(string simulatorUdid, string bundleId, Action<string> info)
    {
        try
        {
            var r = CaptureStoreKitProcess("/usr/bin/xcrun", ["simctl", "uninstall", simulatorUdid, bundleId],
                TimeSpan.FromSeconds(60));
            if (r.ExitCode != 0)
                info($"VisionKit warm-up: uninstall of {bundleId} returned {r.ExitCode}: {r.Combined.Trim()}");
        }
        catch (Exception ex)
        {
            info($"VisionKit warm-up: uninstall of {bundleId} did not finish: {ex.Message}");
        }
    }
}
