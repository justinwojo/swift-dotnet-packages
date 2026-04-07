using System.Diagnostics;
using System.Text.Json;
using Serilog;

namespace SwiftBindings.Build.Helpers;

/// <summary>
/// Thin wrapper around <c>xcrun devicectl</c> verbs the device validator needs.
/// Equivalent of the inline calls in <c>scripts/validate-device.sh:62–96</c>,
/// including the wired-then-localNetwork auto-detection at lines 62–87.
/// </summary>
public sealed class DevicectlClient
{
    private readonly string _device;

    public DevicectlClient(string deviceUdid)
    {
        if (string.IsNullOrEmpty(deviceUdid))
            throw new ArgumentException("device UDID is required", nameof(deviceUdid));
        _device = deviceUdid;
    }

    public string Device => _device;

    /// <summary>
    /// Auto-detect the first connected device. Replicates the Python block in
    /// <c>validate-device.sh:62–87</c>: prefer wired transport, fall back to
    /// <c>localNetwork</c>. Returns <c>null</c> when no device is connected.
    /// </summary>
    public static string? AutoDetectDevice()
    {
        var tmp = Path.GetTempFileName();
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "xcrun",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add("devicectl");
            psi.ArgumentList.Add("list");
            psi.ArgumentList.Add("devices");
            psi.ArgumentList.Add("--json-output");
            psi.ArgumentList.Add(tmp);

            using var p = Process.Start(psi);
            if (p is null) return null;
            if (!p.WaitForExit(15_000))
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                return null;
            }
            if (p.ExitCode != 0) return null;

            using var doc = JsonDocument.Parse(File.ReadAllText(tmp));
            if (!doc.RootElement.TryGetProperty("result", out var result)) return null;
            if (!result.TryGetProperty("devices", out var devices)) return null;

            string? FindByTransport(string transport)
            {
                foreach (var d in devices.EnumerateArray())
                {
                    if (!d.TryGetProperty("connectionProperties", out var props)) continue;
                    if (!props.TryGetProperty("transportType", out var t)) continue;
                    if (t.GetString() != transport) continue;
                    if (d.TryGetProperty("identifier", out var id))
                        return id.GetString();
                }
                return null;
            }

            return FindByTransport("wired") ?? FindByTransport("localNetwork");
        }
        catch (Exception ex)
        {
            Log.Debug("devicectl auto-detect failed: {Message}", ex.Message);
            return null;
        }
        finally
        {
            try { File.Delete(tmp); } catch { }
        }
    }

    /// <summary>
    /// <c>xcrun devicectl device install app --device &lt;udid&gt; &lt;app&gt;</c>.
    /// Throws on non-zero exit.
    /// </summary>
    public void InstallApp(string appPath)
    {
        Log.Information("devicectl install {Device} {App}", _device, appPath);
        var (exit, output) = Run(
            new[] { "device", "install", "app", "--device", _device, appPath },
            TimeSpan.FromMinutes(8));
        if (exit != 0)
            throw new InvalidOperationException($"devicectl install failed (exit {exit}):\n{output}");
    }

    /// <summary>
    /// Build a <see cref="ProcessStartInfo"/> for
    /// <c>devicectl device process launch --device &lt;udid&gt; --terminate-existing
    /// --console &lt;bundle&gt;</c>.
    /// </summary>
    public ProcessStartInfo BuildLaunchPsi(string bundleId)
    {
        var psi = new ProcessStartInfo { FileName = "xcrun", UseShellExecute = false };
        psi.ArgumentList.Add("devicectl");
        psi.ArgumentList.Add("device");
        psi.ArgumentList.Add("process");
        psi.ArgumentList.Add("launch");
        psi.ArgumentList.Add("--device");
        psi.ArgumentList.Add(_device);
        psi.ArgumentList.Add("--terminate-existing");
        psi.ArgumentList.Add("--console");
        psi.ArgumentList.Add(bundleId);
        return psi;
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
        psi.ArgumentList.Add("devicectl");
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start xcrun devicectl");
        var sb = new System.Text.StringBuilder();
        p.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (sb) sb.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (sb) sb.AppendLine(e.Data); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();

        if (!p.WaitForExit((int)timeout.TotalMilliseconds))
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException($"xcrun devicectl {string.Join(' ', args)} timed out after {timeout.TotalSeconds}s");
        }
        p.WaitForExit();
        return (p.ExitCode, sb.ToString());
    }
}
