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
