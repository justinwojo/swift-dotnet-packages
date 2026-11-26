// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Nuke.Common;
using SwiftBindings.Build.Models;

partial class Build
{
    /// <summary>
    /// Self-test for the VisionKit daemon warm-up policy (<c>Models/VisionKitWarmup.cs</c>): the
    /// verdict line it requires, the per-boot identity that decides whether a simulator still
    /// needs warming, and the failure it reports.
    /// </summary>
    Target TestVisionKitWarmup => _ => _
        .Description("Self-test the VisionKit analysis-daemon warm-up policy")
        .Executes(() =>
        {
            const string udid = "0A1B2C3D-0000-4000-8000-00000000ABCD";

            // Only the iOS simulator VisionKit lane warms.
            Assert.True(VisionKitWarmup.AppliesTo("VisionKit", "ios"));
            Assert.False(VisionKitWarmup.AppliesTo("VisionKit", "tvos"));
            Assert.False(VisionKitWarmup.AppliesTo("visionkit", "ios"));
            Assert.False(VisionKitWarmup.AppliesTo("StoreKit2", "ios"));

            // The verdict line: the last READY line wins; a refusal still means a warm daemon.
            var analyzed = VisionKitWarmup.RequireReady(
                "[VK-WARMUP] start supported=true\n[VK-WARMUP] waiting ms=10004\n" +
                "[VK-WARMUP] READY schema=1 supported=true outcome=analyzed ms=356211 transcript=\"HELLO 12345\"\n" +
                "[VK-WARMUP] TEST SUCCESS\n", udid);
            Assert.True(analyzed.Supported && analyzed.Outcome == "analyzed" && analyzed.Milliseconds == 356211);
            Assert.True(analyzed.Detail.Contains("HELLO 12345", StringComparison.Ordinal));
            var refused = VisionKitWarmup.RequireReady(
                "2026-11-21 [VK-WARMUP] READY schema=1 supported=true outcome=refused ms=12 error=\"x\"\n", udid);
            Assert.True(refused.Outcome == "refused" && refused.Milliseconds == 12);
            var unsupported = VisionKitWarmup.RequireReady("[VK-WARMUP] READY schema=1 supported=false ms=0\n", udid);
            Assert.True(!unsupported.Supported && unsupported.Outcome == "unsupported");

            var missing = ExpectWarmupInvalidOperation(() =>
                VisionKitWarmup.RequireReady("[VK-WARMUP] start supported=true\n[VK-WARMUP] TEST SUCCESS\n", udid));
            Assert.True(missing.Message.Contains(udid, StringComparison.Ordinal));
            Assert.True(missing.Message.Contains("was not run", StringComparison.Ordinal));
            ExpectWarmupInvalidOperation(() =>
                VisionKitWarmup.RequireReady("[VK-WARMUP] READY schema=1 supported=true ms=5\n", udid));
            ExpectWarmupInvalidOperation(() =>
                VisionKitWarmup.RequireReady("[VK-WARMUP] READY schema=2 supported=true outcome=analyzed ms=5\n", udid));

            // Boot identity: this simulator's launchd_sim, not another's; a reboot changes it.
            string Ps(int pid, string started) =>
                "  101 Fri Nov 13 22:58:55 2026     launchd_sim /Users/u/Library/Developer/CoreSimulator/Devices/" +
                "9182DCE4-30BE-49C1-AB9A-F90E6E646372/data/var/run/launchd_bootstrap.plist\n" +
                $"{pid,5} {started}     launchd_sim /Users/u/Library/Developer/CoreSimulator/Devices/{udid}/data/var/run/launchd_bootstrap.plist\n" +
                $"  300 Sat Nov 21 02:00:00 2026     /usr/bin/xcrun simctl spawn {udid} log stream\n";
            var first = VisionKitWarmup.BootKey(udid, Ps(4242, "Sat Nov 21  2:43:25 2026"));
            Assert.True(first == $"{udid}|4242|Sat Nov 21 2:43:25 2026", $"boot key: {first}");
            Assert.True(VisionKitWarmup.BootKey(udid.ToLowerInvariant(), Ps(4242, "Sat Nov 21  2:43:25 2026")) is not null);
            Assert.True(VisionKitWarmup.BootKey(udid, Ps(4243, "Sat Nov 21  2:51:07 2026")) != first);
            Assert.True(VisionKitWarmup.BootKey(udid, "  300 Sat Nov 21 02:00:00 2026  /usr/bin/xcrun simctl spawn " + udid) is null);
            Assert.True(VisionKitWarmup.BootKey("FFFFFFFF-0000-4000-8000-000000000000", Ps(4242, "Sat Nov 21  2:43:25 2026")) is null);

            // The failure names the bound, the simulator and what the daemon log showed.
            var message = VisionKitWarmup.NotWarmMessage(udid, 600, "Timeout", "no TEST marker", new[]
            {
                "02:01:00.000 mediaanalysisd: [FaceVersion] Resetting processing state",
                "02:01:01.000 mediaanalysisd: [MACDMigration] First daemon run after boot",
            });
            Assert.True(message.Contains("600s", StringComparison.Ordinal) && message.Contains(udid, StringComparison.Ordinal));
            Assert.True(message.Contains("migration started=yes", StringComparison.Ordinal));
            Assert.True(message.Contains("seen=no", StringComparison.Ordinal));
            Assert.True(message.Contains("Resetting processing state", StringComparison.Ordinal));
            var silent = VisionKitWarmup.NotWarmMessage(udid, 600, "Crash", null, Array.Empty<string>());
            Assert.True(silent.Contains("no mediaanalysisd or VisionKit lines", StringComparison.Ordinal));
            Assert.True(VisionKitWarmup.IsCompletionLine("VisionKit: Mad processing complete for requestID: 1, duration: 2.29"));
            Assert.True(VisionKitWarmup.IsCompletionLine("mediaanalysisd: [FaceModelBump][SPL] Migration finished"));

            Serilog.Log.Information("VisionKit warm-up self-test passed.");
        });

    static InvalidOperationException ExpectWarmupInvalidOperation(Action action)
    {
        try
        {
            action();
        }
        catch (InvalidOperationException ex)
        {
            return ex;
        }
        throw new Exception("expected InvalidOperationException");
    }
}
