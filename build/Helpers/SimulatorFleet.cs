using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Serilog;

namespace SwiftBindings.Build.Helpers;

/// <summary>
/// iOS Simulator lifecycle manager. Direct port of <c>scripts/ci/sim_manager.py</c>:
/// enumeration, picking the best iPhone, booting + waiting for ready, reusing
/// already-booted sims, shutdown, and CI diagnostics collection.
///
/// <para>
/// Designed for GHA macOS runners where <c>simctl bootstatus -b</c> hangs:
/// the implementation polls <c>list devices -j</c> for state changes and uses
/// <c>spawn launchctl print system</c> as a phase-2 readiness probe.
/// </para>
/// </summary>
public sealed class SimulatorFleet
{
    // ── Knobs (mirror SimConfig in sim_manager.py) ───────────────────────────
    public int CommandMaxRetries { get; init; } = 3;
    public double CommandBackoffBaseSeconds { get; init; } = 2.0;
    public double CommandBackoffMaxSeconds { get; init; } = 8.0;
    public TimeSpan CommandTimeout { get; init; } = TimeSpan.FromSeconds(60);

    public TimeSpan BootPollInterval { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan BootTimeout { get; init; } = TimeSpan.FromSeconds(180);

    public TimeSpan ReadinessPollInterval { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan ReadinessTimeout { get; init; } = TimeSpan.FromSeconds(120);
    public TimeSpan ReadinessProbeTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Preferred device names, newest first. Mirrors <c>SimConfig.preferred_devices</c>.</summary>
    public IReadOnlyList<string> PreferredDevices { get; init; } = new[]
    {
        "iPhone 16", "iPhone 16 Pro", "iPhone 15 Pro", "iPhone 15",
    };

    /// <summary>Preferred runtime prefixes, newest first.</summary>
    public IReadOnlyList<string> PreferredRuntimes { get; init; } = new[]
    {
        "iOS-19", "iOS-18", "iOS-17",
    };

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Find the best already-available iPhone simulator (not currently booted)
    /// and return its UDID. Honors <see cref="PreferredDevices"/> with the
    /// "any iPhone" fallback from <c>sim_manager.py:265–278</c>. Returns
    /// <c>null</c> if no shutdown iPhone is available.
    /// </summary>
    public string? FindExistingShutdownIPhone(string? runtimePrefix = null)
    {
        var devices = ListDevices();
        if (!devices.RootElement.TryGetProperty("devices", out var byRuntime))
            return null;

        foreach (var runtime in byRuntime.EnumerateObject())
        {
            if (!runtime.Name.Contains("iOS", StringComparison.Ordinal)) continue;
            if (runtimePrefix is not null && !runtime.Name.Contains(runtimePrefix, StringComparison.Ordinal))
                continue;

            // Preferred names first
            foreach (var pref in PreferredDevices)
            {
                foreach (var d in runtime.Value.EnumerateArray())
                {
                    if (DeviceMatches(d, pref) && IsAvailable(d) && State(d) != "Booted")
                        return Udid(d);
                }
            }
            // Fallback: any iPhone
            foreach (var d in runtime.Value.EnumerateArray())
            {
                var name = d.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                if (name.Contains("iPhone", StringComparison.Ordinal)
                    && IsAvailable(d) && State(d) != "Booted")
                    return Udid(d);
            }
        }
        return null;
    }

    /// <summary>
    /// Find an already-booted iPhone simulator UDID, if any. Used by
    /// <see cref="EnsureReusable"/> to honor <c>--reuse-sim</c>.
    /// </summary>
    public string? FindBootedIPhone()
    {
        var devices = ListDevices();
        if (!devices.RootElement.TryGetProperty("devices", out var byRuntime))
            return null;

        foreach (var runtime in byRuntime.EnumerateObject())
        {
            if (!runtime.Name.Contains("iOS", StringComparison.Ordinal)) continue;
            foreach (var d in runtime.Value.EnumerateArray())
            {
                var name = d.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                if (name.Contains("iPhone", StringComparison.Ordinal) && State(d) == "Booted")
                    return Udid(d);
            }
        }
        return null;
    }

    /// <summary>
    /// Reuse-or-boot. Returns the UDID of an iPhone simulator in the
    /// <c>Booted</c> state. Equivalent of <c>SimManager.prepare_simulator(..., create_fresh=False)</c>.
    /// Drives the <c>--reuse-sim</c> CI flag.
    /// </summary>
    public string EnsureReusable(string? runtimePrefix = null)
    {
        var booted = FindBootedIPhone();
        if (booted is not null)
        {
            Log.Information("Reusing already-booted simulator: {Udid}", booted);
            // Even reused sims need a readiness probe — they may have been
            // booted moments ago by the previous CI step.
            WaitUntilResponsive(booted);
            return booted;
        }

        var existing = FindExistingShutdownIPhone(runtimePrefix);
        if (existing is null)
        {
            existing = CreateSimulator(runtimePrefix, deviceName: null);
        }
        BootAndWaitForReady(existing);
        return existing;
    }

    /// <summary>
    /// Send <c>simctl boot</c> + wait for <c>Booted</c> state + readiness probe.
    /// Mirror of <c>SimManager.boot_and_wait</c>.
    /// </summary>
    public void BootAndWaitForReady(string udid, TimeSpan? bootTimeout = null)
    {
        Boot(udid);
        WaitUntilBooted(udid, bootTimeout ?? BootTimeout);
        WaitUntilResponsive(udid);
    }

    /// <summary>
    /// Best-effort shutdown of every booted simulator known to the host.
    /// Operates fleet-wide so the <c>ShutdownSim</c> Nuke target has a
    /// meaningful no-arg shape for developer use.
    /// </summary>
    public void ShutdownAll()
    {
        try
        {
            RunSimctl(new[] { "shutdown", "all" }, retries: 1, timeout: TimeSpan.FromSeconds(30), check: false);
            Log.Information("simctl shutdown all complete");
        }
        catch (Exception ex)
        {
            Log.Warning("simctl shutdown all failed (non-fatal): {Message}", ex.Message);
        }
    }

    /// <summary>
    /// Best-effort shutdown of a single simulator by UDID. Mirrors
    /// <c>SimManager.cleanup</c>'s targeted behavior: only the simulator
    /// the orchestrator booted is touched, leaving any other booted sims
    /// (e.g. on a developer machine) intact.
    /// </summary>
    public void ShutdownOne(string udid)
    {
        try
        {
            RunSimctl(new[] { "shutdown", udid }, retries: 1, timeout: TimeSpan.FromSeconds(30), check: false);
            Log.Information("simctl shutdown {Udid} complete", udid);
        }
        catch (Exception ex)
        {
            Log.Warning("simctl shutdown {Udid} failed (non-fatal): {Message}", udid, ex.Message);
        }
    }

    /// <summary>
    /// Best-effort delete of a single simulator by UDID. Pair with
    /// <see cref="ShutdownOne"/> for fresh-sim runs so one-shot <c>ci-sim-*</c>
    /// devices don't accumulate in <c>simctl list</c> on local developer
    /// machines. On ephemeral CI runners this is a no-op-shaped cost; on a
    /// developer box it's what keeps the sim list from slowly filling up as
    /// the non-<c>--reuse-sim</c> path creates a new simulator every run.
    /// </summary>
    public void DeleteOne(string udid)
    {
        try
        {
            RunSimctl(new[] { "delete", udid }, retries: 1, timeout: TimeSpan.FromSeconds(30), check: false);
            Log.Information("simctl delete {Udid} complete", udid);
        }
        catch (Exception ex)
        {
            Log.Warning("simctl delete {Udid} failed (non-fatal): {Message}", udid, ex.Message);
        }
    }

    /// <summary>
    /// Collect diagnostic snapshots into <paramref name="outputDir"/>. Direct
    /// port of <c>SimManager.collect_diagnostics</c> — every entry written
    /// here ends up in the GHA artifact uploaded by the
    /// <c>actions/upload-artifact@v4</c> step at <c>ci.yml:180–187</c>.
    /// </summary>
    public IReadOnlyList<string> CollectDiagnostics(string udid, string outputDir, string? appName = null)
    {
        Directory.CreateDirectory(outputDir);
        var collected = new List<string>();

        // 1. Device list snapshot
        try
        {
            var (exit, stdout, _) = RunSimctl(new[] { "list", "devices", "-j" },
                retries: 1, timeout: TimeSpan.FromSeconds(10));
            if (exit == 0)
            {
                var path = Path.Combine(outputDir, "simctl-devices.json");
                File.WriteAllText(path, stdout);
                collected.Add(path);
            }
        }
        catch (Exception ex) { Log.Warning("Failed to collect device list: {Message}", ex.Message); }

        // 2. Runtime list
        try
        {
            var (exit, stdout, _) = RunSimctl(new[] { "list", "runtimes", "-j" },
                retries: 1, timeout: TimeSpan.FromSeconds(10));
            if (exit == 0)
            {
                var path = Path.Combine(outputDir, "simctl-runtimes.json");
                File.WriteAllText(path, stdout);
                collected.Add(path);
            }
        }
        catch (Exception ex) { Log.Warning("Failed to collect runtime list: {Message}", ex.Message); }

        // 3. Simulator device log (last 5 minutes), filtered by app if known
        try
        {
            var args = new List<string> { "spawn", udid, "log", "show", "--last", "5m", "--style", "compact" };
            if (!string.IsNullOrEmpty(appName))
            {
                args.Add("--predicate");
                args.Add($"process == \"{appName}\" OR (process == \"ReportCrash\" AND eventMessage CONTAINS \"{appName}\")");
            }
            var (_, stdout, _) = RunSimctl(args, retries: 1, timeout: TimeSpan.FromSeconds(30), check: false);
            if (!string.IsNullOrEmpty(stdout))
            {
                var path = Path.Combine(outputDir, "device-log.txt");
                File.WriteAllText(path, stdout);
                collected.Add(path);
            }
        }
        catch (Exception ex) { Log.Warning("Failed to collect device log: {Message}", ex.Message); }

        // 4. Process snapshot — anything mentioning Simulator/CoreSimulator/simctl/runtime
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "ps",
                ArgumentList = { "aux" },
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };
            using var p = Process.Start(psi);
            if (p is not null)
            {
                var stdout = p.StandardOutput.ReadToEnd();
                p.WaitForExit(5000);
                var lines = stdout.Split('\n')
                    .Where(l => l.Contains("simulator", StringComparison.OrdinalIgnoreCase)
                              || l.Contains("coresim", StringComparison.OrdinalIgnoreCase)
                              || l.Contains("simctl", StringComparison.OrdinalIgnoreCase)
                              || l.Contains("runtime", StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (lines.Count > 0)
                {
                    var path = Path.Combine(outputDir, "simulator-processes.txt");
                    File.WriteAllText(path, string.Join('\n', lines));
                    collected.Add(path);
                }
            }
        }
        catch (Exception ex) { Log.Warning("Failed to collect process list: {Message}", ex.Message); }

        // 5. Crash logs (latest 3)
        try
        {
            var crashDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Library", "Logs", "DiagnosticReports");
            if (Directory.Exists(crashDir))
            {
                var pattern = appName is null ? "*.ips" : $"{appName}*.ips";
                var crashFiles = new DirectoryInfo(crashDir)
                    .EnumerateFiles(pattern)
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .Take(3);
                foreach (var cf in crashFiles)
                {
                    var dest = Path.Combine(outputDir, cf.Name);
                    File.Copy(cf.FullName, dest, overwrite: true);
                    collected.Add(dest);
                }
            }
        }
        catch (Exception ex) { Log.Warning("Failed to collect crash logs: {Message}", ex.Message); }

        // 6. Xcode version
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "xcodebuild",
                ArgumentList = { "-version" },
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };
            using var p = Process.Start(psi);
            if (p is not null)
            {
                var stdout = p.StandardOutput.ReadToEnd();
                p.WaitForExit(5000);
                var path = Path.Combine(outputDir, "xcode-version.txt");
                File.WriteAllText(path, stdout);
                collected.Add(path);
            }
        }
        catch { /* ignore */ }

        Log.Information("Collected {Count} diagnostic files in {Dir}", collected.Count, outputDir);
        return collected;
    }

    // ── Listing helpers ───────────────────────────────────────────────────────

    public JsonDocument ListDevices()
    {
        var (_, stdout, _) = RunSimctl(new[] { "list", "devices", "-j" }, retries: 2);
        return JsonDocument.Parse(stdout);
    }

    public JsonDocument ListRuntimes()
    {
        var (_, stdout, _) = RunSimctl(new[] { "list", "runtimes", "-j" }, retries: 2);
        return JsonDocument.Parse(stdout);
    }

    /// <summary>
    /// Pretty-print every iOS simulator visible to <c>simctl</c>. Used by
    /// <c>nuke ListSims</c>.
    /// </summary>
    public IReadOnlyList<string> ListIPhonesSummary()
    {
        var devices = ListDevices();
        var lines = new List<string>();
        if (!devices.RootElement.TryGetProperty("devices", out var byRuntime))
            return lines;

        foreach (var runtime in byRuntime.EnumerateObject())
        {
            if (!runtime.Name.Contains("iOS", StringComparison.Ordinal)) continue;
            foreach (var d in runtime.Value.EnumerateArray())
            {
                var name = d.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                if (!name.Contains("iPhone", StringComparison.Ordinal)) continue;
                lines.Add($"{State(d),-10} {Udid(d)}  {name}  ({runtime.Name.Replace("com.apple.CoreSimulator.SimRuntime.", "")})");
            }
        }
        return lines;
    }

    // ── Internal lifecycle ───────────────────────────────────────────────────

    /// <summary>
    /// Create a fresh iPhone simulator (no boot). Mirror of
    /// <c>SimManager.create_simulator</c>. Used by <see cref="EnsureReusable"/>
    /// and the CI orchestrator's fresh-simulator path on clean runners.
    /// </summary>
    public string CreateSimulator(string? runtimePrefix, string? deviceName)
    {
        var runtimeId = FindRuntime(runtimePrefix);
        var deviceTypeId = FindDeviceType(deviceName);
        var simName = $"ci-sim-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}-{Environment.ProcessId}";

        Log.Information("Creating simulator: {Name} (type={Type}, runtime={Runtime})", simName, deviceTypeId, runtimeId);
        // RunSimctl (not RunSimctlRaw) — fails loudly with the simctl stderr
        // on non-zero exit, and honors the same retry/backoff every other
        // fleet call uses. The previous RunSimctlRaw path silently discarded
        // the exit code and stderr and trusted stdout as a UDID, turning a
        // real creation failure into a confusing downstream boot/readiness
        // error with no root cause.
        var (_, stdout, _) = RunSimctl(
            new[] { "create", simName, deviceTypeId, runtimeId },
            timeout: CommandTimeout);
        var udid = stdout.Trim();
        // simctl prints exactly one line — a UUID of the form
        // xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx (36 chars, 4 dashes).
        // Anything else (empty, multiple lines, non-UUID) is a smell we
        // want to fail on here, not later at boot time.
        if (udid.Length != 36 || udid.Count(c => c == '-') != 4)
            throw new InvalidOperationException(
                $"simctl create returned a malformed UDID: '{udid}' (expected 36-char UUID with 4 dashes)");
        Log.Information("Created simulator {Udid}", udid);
        return udid;
    }

    private string FindRuntime(string? prefix)
    {
        var runtimes = ListRuntimes();
        var available = new List<JsonElement>();
        foreach (var r in runtimes.RootElement.GetProperty("runtimes").EnumerateArray())
        {
            if (r.TryGetProperty("isAvailable", out var avail) && avail.GetBoolean()
                && r.TryGetProperty("name", out var n) && (n.GetString() ?? "").Contains("iOS", StringComparison.Ordinal))
                available.Add(r);
        }
        if (available.Count == 0)
            throw new InvalidOperationException("No available iOS runtimes found");

        var prefixes = prefix is not null ? new[] { prefix } : PreferredRuntimes.ToArray();
        foreach (var pref in prefixes)
        {
            var matches = available
                .Where(r =>
                {
                    var name = r.GetProperty("name").GetString() ?? "";
                    var id = r.TryGetProperty("identifier", out var i) ? i.GetString() ?? "" : "";
                    return name.Contains(pref.Replace("-", "."), StringComparison.Ordinal)
                           || id.Contains(pref, StringComparison.Ordinal);
                })
                .OrderByDescending(r => r.TryGetProperty("identifier", out var id) ? id.GetString() ?? "" : "")
                .ToList();
            if (matches.Count > 0)
                return matches[0].GetProperty("identifier").GetString()!;
        }

        // Fallback: newest available
        return available
            .OrderByDescending(r => r.TryGetProperty("identifier", out var id) ? id.GetString() ?? "" : "")
            .First().GetProperty("identifier").GetString()!;
    }

    private string FindDeviceType(string? name)
    {
        var (_, stdout, _) = RunSimctl(new[] { "list", "devicetypes", "-j" }, retries: 2);
        using var doc = JsonDocument.Parse(stdout);
        var types = doc.RootElement.GetProperty("devicetypes");

        var namesToCheck = name is not null ? new[] { name } : PreferredDevices.ToArray();
        foreach (var n in namesToCheck)
        {
            foreach (var dt in types.EnumerateArray())
            {
                if (dt.GetProperty("name").GetString() == n)
                    return dt.GetProperty("identifier").GetString()!;
            }
        }
        // Fallback: any iPhone
        foreach (var dt in types.EnumerateArray())
        {
            if ((dt.GetProperty("name").GetString() ?? "").Contains("iPhone", StringComparison.Ordinal))
                return dt.GetProperty("identifier").GetString()!;
        }
        throw new InvalidOperationException("No iPhone device type found");
    }

    private void Boot(string udid)
    {
        var state = GetDeviceState(udid);
        if (state == "Booted")
        {
            Log.Information("Simulator {Udid} already booted", udid);
            return;
        }
        Log.Information("Booting simulator {Udid} (current state: {State})", udid, state ?? "<unknown>");
        try
        {
            RunSimctl(new[] { "boot", udid }, CommandMaxRetries, CommandTimeout);
        }
        catch (Exception ex) when (ex.Message.Contains("Booted", StringComparison.Ordinal))
        {
            Log.Information("Simulator {Udid} was already booting/booted", udid);
        }
    }

    private void WaitUntilBooted(string udid, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        Log.Information("Waiting for {Udid} to reach Booted state (timeout: {Sec}s)", udid, (int)timeout.TotalSeconds);
        while (DateTime.UtcNow < deadline)
        {
            var state = GetDeviceState(udid);
            if (state == "Booted")
            {
                Log.Information("Simulator {Udid} reached Booted state", udid);
                return;
            }
            Thread.Sleep(BootPollInterval);
        }
        var finalState = GetDeviceState(udid);
        throw new SimulatorBootTimeout(
            $"Simulator {udid} did not boot within {timeout.TotalSeconds}s (final state: {finalState ?? "unknown"})");
    }

    private void WaitUntilResponsive(string udid)
    {
        var deadline = DateTime.UtcNow + ReadinessTimeout;
        Log.Information("Probing {Udid} responsiveness (timeout: {Sec}s)", udid, (int)ReadinessTimeout.TotalSeconds);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                RunSimctl(new[] { "spawn", udid, "launchctl", "print", "system" },
                    retries: 1,
                    timeout: ReadinessProbeTimeout);
                Log.Information("Simulator {Udid} is responsive", udid);
                return;
            }
            catch (Exception ex)
            {
                Log.Debug("Readiness probe failed: {Message}", ex.Message);
                Thread.Sleep(ReadinessPollInterval);
            }
        }
        throw new SimulatorReadinessTimeout(
            $"Simulator {udid} not responsive within {ReadinessTimeout.TotalSeconds}s");
    }

    private string? GetDeviceState(string udid)
    {
        try
        {
            var devices = ListDevices();
            if (!devices.RootElement.TryGetProperty("devices", out var byRuntime)) return null;
            foreach (var runtime in byRuntime.EnumerateObject())
            {
                foreach (var d in runtime.Value.EnumerateArray())
                {
                    if (Udid(d) == udid)
                        return State(d);
                }
            }
        }
        catch { /* ignore */ }
        return null;
    }

    // ── Low-level simctl runner with retries ─────────────────────────────────

    /// <summary>
    /// Run an <c>xcrun simctl</c> command with retry. Throws
    /// <see cref="SimctlCommandError"/> after exhausted retries when
    /// <paramref name="check"/> is true.
    /// </summary>
    private (int Exit, string Stdout, string Stderr) RunSimctl(
        IEnumerable<string> args,
        int? retries = null,
        TimeSpan? timeout = null,
        bool check = true)
    {
        var argList = args.ToList();
        var max = retries ?? CommandMaxRetries;
        var t = timeout ?? CommandTimeout;
        Exception? last = null;

        for (var attempt = 1; attempt <= max; attempt++)
        {
            try
            {
                var (exit, stdout, stderr) = RunSimctlRaw(argList, t);
                if (check && exit != 0)
                    throw new SimctlCommandError(argList, exit, stderr);
                return (exit, stdout, stderr);
            }
            catch (TimeoutException tex)
            {
                last = new SimctlCommandError(argList, -1, $"timed out after {t.TotalSeconds}s");
                Log.Warning("simctl {Verb} timed out (attempt {Attempt}/{Max}): {Msg}", argList[0], attempt, max, tex.Message);
            }
            catch (SimctlCommandError sce)
            {
                last = sce;
                Log.Warning("simctl {Verb} failed (attempt {Attempt}/{Max}): {Msg}", argList[0], attempt, max, sce.Stderr);
            }

            if (attempt < max)
            {
                var backoff = Math.Min(Math.Pow(CommandBackoffBaseSeconds, attempt), CommandBackoffMaxSeconds);
                Log.Information("Retrying in {Sec:F1}s...", backoff);
                Thread.Sleep(TimeSpan.FromSeconds(backoff));
            }
        }
        throw last ?? new SimctlCommandError(argList.ToList(), -1, "unknown error");
    }

    private static (int Exit, string Stdout, string Stderr) RunSimctlRaw(
        IEnumerable<string> args,
        TimeSpan timeout)
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

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        p.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (stdout) stdout.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (stderr) stderr.AppendLine(e.Data); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();

        if (!p.WaitForExit((int)timeout.TotalMilliseconds))
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException($"xcrun simctl {string.Join(' ', args)} timed out after {timeout.TotalSeconds}s");
        }
        p.WaitForExit();
        return (p.ExitCode, stdout.ToString(), stderr.ToString());
    }

    // ── Tiny JSON shape helpers ──────────────────────────────────────────────

    private static string? Udid(JsonElement d) =>
        d.TryGetProperty("udid", out var u) ? u.GetString() : null;

    private static string? State(JsonElement d) =>
        d.TryGetProperty("state", out var s) ? s.GetString() : null;

    private static bool IsAvailable(JsonElement d) =>
        d.TryGetProperty("isAvailable", out var a) && a.GetBoolean();

    private static bool DeviceMatches(JsonElement d, string name) =>
        d.TryGetProperty("name", out var n) && n.GetString() == name;
}

/// <summary>simctl command failed (after retries) — equivalent of <c>SimctlCommandError</c> in Python.</summary>
public sealed class SimctlCommandError : Exception
{
    public IReadOnlyList<string> Args { get; }
    public int ReturnCode { get; }
    public string Stderr { get; }

    public SimctlCommandError(IReadOnlyList<string> args, int returnCode, string stderr)
        : base($"simctl {string.Join(' ', args)} failed (rc={returnCode}): {stderr.Trim()}")
    {
        Args = args;
        ReturnCode = returnCode;
        Stderr = stderr;
    }
}

/// <summary>Simulator did not reach Booted state in time. Classified as infra failure for retry.</summary>
public sealed class SimulatorBootTimeout : Exception
{
    public SimulatorBootTimeout(string message) : base(message) { }
}

/// <summary>Simulator booted but did not become responsive. Classified as infra failure for retry.</summary>
public sealed class SimulatorReadinessTimeout : Exception
{
    public SimulatorReadinessTimeout(string message) : base(message) { }
}
