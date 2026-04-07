using Nuke.Common;
using Serilog;
using SwiftBindings.Build.Helpers;

partial class Build
{
    /// <summary>
    /// Pretty-print every iOS iPhone simulator visible to <c>simctl</c>.
    /// Equivalent of <c>xcrun simctl list devices iPhone</c> filtered to
    /// available iOS runtimes.
    /// </summary>
    Target ListSims => _ => _
        .Description("List every iPhone simulator visible to simctl")
        .Executes(() =>
        {
            var fleet = new SimulatorFleet();
            foreach (var line in fleet.ListIPhonesSummary())
                Log.Information("{Line}", line);
        });

    /// <summary>
    /// Reuse-or-boot an iPhone simulator and print its UDID. Drives
    /// <c>--reuse-sim</c> when called via <see cref="RunCiSimTest"/> but is
    /// also useful as a standalone CI step (matches the
    /// <c>ci_ios_test.py --prepare-only</c> mode).
    /// </summary>
    Target BootSim => _ => _
        .Description("Boot an iPhone simulator (reuses booted sim if --reuse-sim) and print its UDID")
        .Executes(() =>
        {
            var fleet = new SimulatorFleet();
            string udid;
            if (ReuseSim)
            {
                udid = fleet.EnsureReusable();
            }
            else
            {
                udid = fleet.FindBootedIPhone()
                    ?? fleet.FindExistingShutdownIPhone()
                    ?? throw new InvalidOperationException(
                        "No iPhone simulator available to boot. Create one with `xcrun simctl create`.");
                fleet.BootAndWaitForReady(udid);
            }
            Log.Information("Simulator UDID: {Udid}", udid);
            Console.WriteLine(udid);
        });

    /// <summary>
    /// Best-effort <c>simctl shutdown all</c>. Mirrors the cleanup path used
    /// by the bash CI when a previous step left sims in a weird state.
    /// </summary>
    Target ShutdownSim => _ => _
        .Description("Shut down every booted iOS simulator (best effort)")
        .Executes(() =>
        {
            var fleet = new SimulatorFleet();
            fleet.ShutdownAll();
        });
}
