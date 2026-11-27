using System.Diagnostics;
using Serilog;
using SwiftBindings.Build.Models;

namespace SwiftBindings.Build.Helpers;

/// <summary>
/// Thin wrapper around <c>xcrun simctl</c> verbs the validators need.
/// Equivalent of the inline calls in <c>scripts/validate-sim.sh:57–93</c>.
/// </summary>
public sealed class SimctlClient
{
    /// <summary>
    /// simctl's own device alias. No longer this class's default: it is
    /// family-blind, so it is kept only as the name of the thing we stopped
    /// relying on (see the constructor).
    /// </summary>
    public const string DefaultDevice = "booted";

    private readonly string _device;

    /// <summary>
    /// Target a specific simulator, or — when <paramref name="deviceUdid"/> is
    /// null/empty — resolve a booted simulator of <paramref name="family"/>.
    ///
    /// <para>Defaulting used to be simctl's <c>booted</c> alias, which picks a
    /// booted device without regard to device family. On a host where an Apple
    /// TV is booted alongside the iPhones, an iOS install through that alias
    /// fails with "This app was not built to support this device family; app is
    /// compatible with (1) but this device supports (…)" — a validator reporting
    /// a red that has nothing to do with the code under test. Resolving a
    /// concrete UDID of the right family removes the ambiguity, and a host with
    /// nothing booted for that family now gets a message that says so instead of
    /// simctl's.</para>
    /// </summary>
    public SimctlClient(string? deviceUdid = null, SimulatorFamily family = SimulatorFamily.Ios)
    {
        if (!string.IsNullOrEmpty(deviceUdid))
        {
            _device = deviceUdid;
            return;
        }

        _device = new SimulatorFleet().FindBootedUdid(family)
            ?? throw new InvalidOperationException(
                $"No booted {SimulatorFleet.RuntimeKeyword(family)} simulator to install onto. " +
                "Boot one (or let the caller lease one from SimulatorFleet), or pass an explicit " +
                "--device-udid. Simulators of other families that happen to be booted are deliberately " +
                "not used: installing onto the wrong device family fails at install time.");
        Log.Debug("simctl: resolved booted {Family} simulator {Udid}", family, _device);
    }

    public string Device => _device;

    /// <summary>
    /// <c>xcrun simctl install &lt;device&gt; &lt;app&gt;</c>. Throws on non-zero exit
    /// or if the install does not complete within <paramref name="timeout"/>.
    /// </summary>
    /// <param name="timeout">
    /// Hard wall-clock cap. Default 8 minutes (480s) matches
    /// <c>INSTALL_OVERHEAD</c> in <c>ci_ios_test.py</c>. The CI orchestrator
    /// passes a deadline-aware tighter cap so the combined install+watch step
    /// stays inside the Python <c>subprocess_timeout</c> envelope.
    /// </param>
    public void InstallApp(string appPath, TimeSpan? timeout = null)
    {
        var cap = timeout ?? TimeSpan.FromMinutes(8);
        Log.Information("simctl install {Device} {App} (cap={Cap:F0}s)", _device, appPath, cap.TotalSeconds);
        var (exit, output) = Run(new[] { "install", _device, appPath }, cap);
        if (exit != 0)
            throw new InvalidOperationException($"simctl install failed (exit {exit}):\n{output}");
    }

    /// <summary>
    /// Delete stale <c>launch_console-*</c> FIFOs accumulated under
    /// <c>~/Library/Developer/CoreSimulator/Devices/&lt;UDID&gt;/data/tmp/</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>simctl launch --console</c> creates a per-launch FIFO in the
    /// target sim's <c>data/tmp/</c>. If a prior launch was killed
    /// mid-stream (timeout, crash, Ctrl-C) the FIFO is orphaned. Apple's
    /// simctl picks a new launch its naming sometimes collides with an
    /// orphan, producing
    /// <c>simctl spawn Error 17 EEXIST (Unable to establish FIFO ... File exists)</c>
    /// — the BlinkID sim cell has hit this intermittently. Cleaning the
    /// FIFOs that are older than the cutoff before each launch removes
    /// the collision surface without touching any in-flight launch.
    /// </para>
    /// <para>
    /// Best-effort: deletion errors are logged at Debug and swallowed, so
    /// a permissions hiccup or a FIFO held open by another simctl
    /// invocation never blocks the validation flow.
    /// </para>
    /// </remarks>
    public void CleanStaleConsoleFifos(TimeSpan? olderThan = null)
    {
        var cutoff = DateTime.UtcNow - (olderThan ?? TimeSpan.FromMinutes(1));
        var devicesRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Library", "Developer", "CoreSimulator", "Devices");
        if (!Directory.Exists(devicesRoot)) return;

        // The device is always a concrete UDID (the constructor resolves one),
        // so sweep just that sim's tmp dir. The booted-device enumeration is
        // kept as the fallback for the alias, which no longer reaches here but
        // is still a legal value for a caller that passes it explicitly.
        var targetUdids = _device == DefaultDevice
            ? ListBootedUdids()
            : new[] { _device };

        var swept = 0;
        foreach (var udid in targetUdids)
        {
            var tmp = Path.Combine(devicesRoot, udid, "data", "tmp");
            if (!Directory.Exists(tmp)) continue;
            string[] entries;
            try
            {
                // GetFiles materializes the enumeration eagerly, so any
                // directory-walk exception (tmp vanishing or becoming
                // unreadable between Exists and the walk — other simctl
                // invocations rotate it) is caught here, not later inside
                // the foreach where EnumerateFiles' lazy MoveNext could throw.
                entries = Directory.GetFiles(tmp, "launch_console-*");
            }
            catch (Exception ex)
            {
                Log.Debug("simctl stale-fifo enumerate skip {Path}: {Message}", tmp, ex.Message);
                continue;
            }
            foreach (var fifo in entries)
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(fifo) < cutoff)
                    {
                        File.Delete(fifo);
                        swept++;
                    }
                }
                catch (Exception ex)
                {
                    Log.Debug("simctl stale-fifo sweep skip {Path}: {Message}", fifo, ex.Message);
                }
            }
        }
        if (swept > 0)
            Log.Information("simctl stale-FIFO sweep: removed {Count} launch_console-* file(s)", swept);
    }

    static string[] ListBootedUdids()
    {
        try
        {
            var psi = new ProcessStartInfo("xcrun")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add("simctl");
            psi.ArgumentList.Add("list");
            psi.ArgumentList.Add("devices");
            psi.ArgumentList.Add("booted");
            using var p = Process.Start(psi);
            if (p is null) return Array.Empty<string>();
            // Drain stdout async so a pipe-full child can't deadlock the
            // WaitForExit below; bound the whole call so a wedged CoreSimulator
            // can't hang the entire regression sweep.
            var sb = new System.Text.StringBuilder();
            p.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (sb) sb.AppendLine(e.Data); };
            p.ErrorDataReceived += (_, e) => { /* discard stderr */ };
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            if (!p.WaitForExit(10_000))
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                Log.Debug("simctl list booted: timed out after 10s, treating as no booted devices");
                return Array.Empty<string>();
            }
            p.WaitForExit();
            var booted = new List<string>();
            foreach (var line in sb.ToString().Split('\n'))
            {
                var m = System.Text.RegularExpressions.Regex.Match(line, @"\(([0-9A-F-]{36})\) \(Booted\)");
                if (m.Success) booted.Add(m.Groups[1].Value);
            }
            return booted.ToArray();
        }
        catch (Exception ex)
        {
            Log.Debug("simctl list booted (non-fatal): {Message}", ex.Message);
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// Build a <see cref="ProcessStartInfo"/> for <c>simctl launch --console
    /// --terminate-running-process &lt;device&gt; &lt;bundle&gt;</c>. The watcher
    /// owns the process so we return the PSI rather than the launched process.
    /// </summary>
    public ProcessStartInfo BuildLaunchPsi(string bundleId, IEnumerable<string>? launchArguments = null)
    {
        var psi = new ProcessStartInfo { FileName = "xcrun", UseShellExecute = false };
        psi.ArgumentList.Add("simctl");
        psi.ArgumentList.Add("launch");
        psi.ArgumentList.Add("--console");
        psi.ArgumentList.Add("--terminate-running-process");
        psi.ArgumentList.Add(_device);
        psi.ArgumentList.Add(bundleId);
        if (launchArguments is not null)
            foreach (var argument in launchArguments)
                psi.ArgumentList.Add(argument);
        return psi;
    }

    /// <summary>
    /// <c>xcrun simctl terminate &lt;device&gt; &lt;bundle&gt;</c> — best effort,
    /// never throws. Used for cleanup between retries.
    /// </summary>
    public void TerminateApp(string bundleId)
    {
        try
        {
            Run(new[] { "terminate", _device, bundleId }, TimeSpan.FromSeconds(10));
        }
        catch (Exception ex)
        {
            Log.Debug("simctl terminate (non-fatal): {Message}", ex.Message);
        }
    }

    /// <summary>
    /// Answer, before launch, every privacy prompt the app at <paramref name="appPath"/> could
    /// raise (see <see cref="SimulatorPrivacy"/>): grant each service its Info.plist declares a
    /// usage string for, through <c>simctl privacy</c> where simctl knows the service and through
    /// the simulator's TCC database where it does not. Throws when an answer cannot be recorded,
    /// or a declared service has no known answer: an alert the app raises unanswered stays on
    /// this shared simulator after the app is gone, and only a reboot takes it down.
    /// </summary>
    internal SimulatorPrivacy.Plan PreAnswerPrivacyPrompts(string appPath, string bundleId, Action<string> info)
    {
        var plist = Path.Combine(appPath, "Info.plist");
        var (plExit, plXml) = RunTool("/usr/bin/plutil", ["-convert", "xml1", "-o", "-", plist], TimeSpan.FromSeconds(30));
        if (plExit != 0)
            throw new InvalidOperationException($"Cannot read {plist} to pre-answer its privacy prompts:\n{plXml}");
        var plan = SimulatorPrivacy.PlanFor(SimulatorPrivacy.UsageKeysFromPlistXml(plXml));
        if (plan.Unanswered.Count > 0)
            throw new InvalidOperationException(
                $"{bundleId} declares {string.Join(", ", plan.Unanswered)}, which the harness cannot pre-answer on a " +
                "simulator (Models/SimulatorPrivacy.cs). Launching it could leave a system alert up on a shared " +
                "simulator, so the launch is refused.");
        if (!plan.HasAnswers)
            return plan;

        foreach (var service in plan.SimctlServices)
        {
            var (exit, output) = Run(["privacy", _device, "grant", service, bundleId], TimeSpan.FromSeconds(60));
            if (exit != 0)
                throw new InvalidOperationException(
                    $"simctl privacy grant {service} {bundleId} on {_device} failed (exit {exit}):\n{output}");
        }
        if (plan.DatabaseServices.Count > 0)
        {
            var db = TccDatabasePath();
            if (!File.Exists(db))
                throw new InvalidOperationException(
                    $"Cannot pre-answer {string.Join(", ", plan.DatabaseServices)} for {bundleId}: " +
                    $"no TCC database at {db} (is {_device} booted?)");
            var sql = string.Join("\n", plan.DatabaseServices.Select(s => SimulatorPrivacy.GrantSql(s, bundleId)));
            var (exit, output) = RunTool("/usr/bin/sqlite3", [db, ".timeout 10000", sql], TimeSpan.FromSeconds(30));
            if (exit != 0)
                throw new InvalidOperationException(
                    $"Recording {string.Join(", ", plan.DatabaseServices)} for {bundleId} in {db} failed (exit {exit}):\n{output}");
        }
        info($"Privacy prompts pre-answered on {_device} for {bundleId}: granted " +
             string.Join(", ", plan.SimctlServices.Concat(plan.DatabaseServices)));
        return plan;
    }

    /// <summary>
    /// After the run: take back every answer <see cref="PreAnswerPrivacyPrompts"/> recorded
    /// (<c>simctl privacy reset all</c> also clears the rows it wrote to the TCC database).
    /// Best effort; never throws.
    /// </summary>
    internal void ResetPrivacyAnswers(string bundleId, SimulatorPrivacy.Plan plan, Action<string> info)
    {
        if (!plan.HasAnswers)
            return;
        try
        {
            var (exit, output) = Run(["privacy", _device, "reset", "all", bundleId], TimeSpan.FromSeconds(60));
            if (exit != 0)
                info($"simctl privacy reset all {bundleId} on {_device} returned {exit}: {output.Trim()}");
        }
        catch (Exception ex)
        {
            info($"simctl privacy reset all {bundleId} on {_device} did not finish: {ex.Message}");
        }
    }

    string TccDatabasePath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Library", "Developer", "CoreSimulator", "Devices", _device, "data", "Library", "TCC", "TCC.db");

    private static (int Exit, string Output) Run(IEnumerable<string> args, TimeSpan timeout) =>
        RunTool("xcrun", ["simctl", .. args], timeout);

    private static (int Exit, string Output) RunTool(string fileName, IEnumerable<string> args, TimeSpan timeout)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start {fileName}");
        var sb = new System.Text.StringBuilder();
        p.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (sb) sb.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (sb) sb.AppendLine(e.Data); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();

        if (!p.WaitForExit((int)timeout.TotalMilliseconds))
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException($"{fileName} {string.Join(' ', args)} timed out after {timeout.TotalSeconds}s");
        }
        p.WaitForExit();
        return (p.ExitCode, sb.ToString());
    }
}
