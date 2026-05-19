using System.Diagnostics;
using Serilog;

namespace SwiftBindings.Build.Helpers;

/// <summary>
/// Thin wrapper around <c>xcrun simctl</c> verbs the validators need.
/// Equivalent of the inline calls in <c>scripts/validate-sim.sh:57–93</c>.
/// </summary>
public sealed class SimctlClient
{
    /// <summary>
    /// Default device target. Mirrors <c>validate-sim.sh</c>'s default of
    /// <c>"booted"</c> when no UDID is supplied.
    /// </summary>
    public const string DefaultDevice = "booted";

    private readonly string _device;

    public SimctlClient(string? deviceUdid = null)
    {
        _device = string.IsNullOrEmpty(deviceUdid) ? DefaultDevice : deviceUdid;
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

        // Limit cleanup to booted device(s) when a UDID isn't pinned —
        // shutdown sims aren't going to launch console-FIFOs anyway and
        // there's no value in scanning their tmp directories.
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
    public ProcessStartInfo BuildLaunchPsi(string bundleId)
    {
        var psi = new ProcessStartInfo { FileName = "xcrun", UseShellExecute = false };
        psi.ArgumentList.Add("simctl");
        psi.ArgumentList.Add("launch");
        psi.ArgumentList.Add("--console");
        psi.ArgumentList.Add("--terminate-running-process");
        psi.ArgumentList.Add(_device);
        psi.ArgumentList.Add(bundleId);
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

    private static (int Exit, string Output) Run(IEnumerable<string> args, TimeSpan timeout)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "xcrun",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("simctl");
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start xcrun simctl");
        var sb = new System.Text.StringBuilder();
        p.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (sb) sb.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (sb) sb.AppendLine(e.Data); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();

        if (!p.WaitForExit((int)timeout.TotalMilliseconds))
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException($"xcrun simctl {string.Join(' ', args)} timed out after {timeout.TotalSeconds}s");
        }
        p.WaitForExit();
        return (p.ExitCode, sb.ToString());
    }
}
