using System.Diagnostics;
using System.Text;
using SwiftBindings.Build.Models;

namespace SwiftBindings.Build.Helpers;

/// <summary>
/// Async line-by-line stdout state machine for sim/device test apps.
///
/// <para>
/// Replaces the <c>grep -q "TEST SUCCESS" | grep -q "TEST FAILED" |
/// grep -v '\[SKIP\]' | grep -qE "SIGABRT|..."</c> polling loop in
/// <c>scripts/validate-sim.sh:67–89</c> and <c>scripts/validate-device.sh:102–136</c>.
/// </para>
///
/// <para>State transitions: <c>Running → Success | Failed | Crashed | Timeout | Exited</c>.</para>
///
/// <para>Match rules — substring semantics, mirroring the bash <c>grep</c>
/// pipelines exactly:</para>
/// <list type="bullet">
///   <item>
///     <b>Success</b> — line contains <c>TEST SUCCESS</c>. Substring match
///     because <c>simctl launch --console</c> prefixes every line with
///     <c>&lt;timestamp&gt; &lt;ProcessName&gt;[pid:tid]</c> from the iOS
///     unified logging system. The bash uses <c>grep -q "TEST SUCCESS"</c> at
///     <c>validate-sim.sh:70</c>. An exact-line matcher would never fire under
///     the syslog prefix and the run would always look like a timeout.
///   </item>
///   <item>
///     <b>Failed</b> — line contains <c>TEST FAILED</c>. The current sim test
///     apps emit <c>TEST FAILED: {N} failures</c>
///     (<c>tests/Nuke.SimTests/Program.cs:241</c>) and the existing bash uses
///     <c>grep -q "TEST FAILED"</c> at <c>validate-sim.sh:74</c>. Substring
///     match handles both the colon suffix and the syslog timestamp prefix.
///   </item>
///   <item>
///     <b>Crashed</b> — line contains any of <c>SIGABRT</c>, <c>SIGSEGV</c>,
///     <c>SIGBUS</c>, <c>Fatal error</c>, <c>EXC_BAD_ACCESS</c>, BUT only when
///     the line does NOT contain <c>[SKIP]</c>. The bash version pipes
///     <c>grep -v '\[SKIP\]' | grep -qE ...</c> over the entire output buffer
///     repeatedly, which fails when a non-skip line near the bottom mentions
///     a signal name in passing. The C# state machine evaluates one line at a
///     time and is robust against that failure mode.
///   </item>
/// </list>
///
/// <para>The <c>[SKIP]</c> check uses <c>Contains</c> rather than
/// <c>StartsWith</c> for the same syslog-prefix reason — the bash <c>grep -v</c>
/// excludes any line containing <c>[SKIP]</c>, so the C# port must too.</para>
/// </summary>
public static class StdoutWatcher
{
    /// <summary>Crash signal substrings checked per-line (skipping <c>[SKIP]</c> prefix).</summary>
    public static readonly string[] CrashSignals =
    {
        "SIGABRT",
        "SIGSEGV",
        "SIGBUS",
        "Fatal error",
        "EXC_BAD_ACCESS",
    };

    private const string SkipPrefix = "[SKIP]";
    private const string SuccessLine = "TEST SUCCESS";
    private const string FailedPrefix = "TEST FAILED";

    /// <summary>
    /// Classify a single stdout line. Returns <see cref="TestRunStatus.Running"/>
    /// when the line is not a terminal marker. Pure function — exposed for unit
    /// tests of the state machine without process plumbing.
    /// </summary>
    public static (TestRunStatus Status, string? Reason) Classify(string line)
    {
        var status = ClassifyImpl(line, out var reason);
        return (status, reason);
    }

    /// <summary>
    /// Internal helper that returns the reason via out-param so the caller
    /// can avoid tuple deconstruction overhead inside the hot per-line loop.
    /// </summary>
    public static TestRunStatus ClassifyImpl(string line, out string? reason)
    {
        // Trim only trailing whitespace; we want to preserve leading content so
        // syslog-prefixed lines like
        //   "2026-04-07 02:50:27.705 NukeSimTests[23887:20068668] TEST SUCCESS"
        // still match via Contains. The sim test apps log via Console.WriteLine
        // (no syslog prefix), but `simctl launch --console` reroutes stdout
        // through iOS unified logging, which prepends a timestamp + process tag
        // to every line. Substring matching mirrors bash `grep -q "TEST SUCCESS"`
        // and handles both code paths.
        var trimmed = line.TrimEnd();

        if (trimmed.Contains(SuccessLine, StringComparison.Ordinal))
        {
            reason = "TEST SUCCESS";
            return TestRunStatus.Success;
        }

        if (trimmed.Contains(FailedPrefix, StringComparison.Ordinal))
        {
            reason = trimmed;
            return TestRunStatus.Failed;
        }

        // [SKIP] lines may legitimately mention signal names — skip the
        // whole crash check so we don't trip a false positive. Contains rather
        // than StartsWith because the syslog prefix pushes [SKIP] off the line
        // start, just like the success/failure markers.
        if (trimmed.Contains(SkipPrefix, StringComparison.Ordinal))
        {
            reason = null;
            return TestRunStatus.Running;
        }

        foreach (var signal in CrashSignals)
        {
            if (trimmed.Contains(signal, StringComparison.Ordinal))
            {
                reason = $"crash signal in stdout: {signal}";
                return TestRunStatus.Crashed;
            }
        }

        reason = null;
        return TestRunStatus.Running;
    }

    /// <summary>
    /// Run a process and watch its merged stdout/stderr until a terminal state
    /// is reached or the deadline elapses.
    ///
    /// <para>
    /// Behaviour notes:
    /// </para>
    /// <list type="bullet">
    ///   <item>stdout and stderr are interleaved in the captured output buffer
    ///         in the order events arrive — matches the bash redirection
    ///         <c>simctl launch ... &gt; "$OUTPUT_FILE" 2&gt;&amp;1</c>.</item>
    ///   <item>On terminal state, the process is killed (best effort) so the
    ///         caller doesn't have to.</item>
    ///   <item>On timeout, the watcher returns <see cref="TestRunStatus.Timeout"/>
    ///         WITHOUT killing the process — the caller decides whether to
    ///         terminate, kill, or leave it for the next retry.</item>
    /// </list>
    /// </summary>
    public static async Task<TestRunResult> RunAsync(
        ProcessStartInfo psi,
        TimeSpan timeout,
        Action<string>? onLine = null,
        bool killOnTerminal = true,
        CancellationToken cancellationToken = default)
    {
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.UseShellExecute = false;

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start process: {psi.FileName}");

        var buffer = new StringBuilder();
        var bufferLock = new object();
        var status = TestRunStatus.Running;
        string? reason = null;
        var statusLock = new object();

        void Handle(string? data)
        {
            if (data is null) return;
            lock (bufferLock) { buffer.AppendLine(data); }
            onLine?.Invoke(data);

            if (status != TestRunStatus.Running) return;
            var classified = ClassifyImpl(data, out var lineReason);
            if (classified == TestRunStatus.Running) return;
            lock (statusLock)
            {
                if (status == TestRunStatus.Running)
                {
                    status = classified;
                    reason = lineReason;
                }
            }
        }

        process.OutputDataReceived += (_, e) => Handle(e.Data);
        process.ErrorDataReceived += (_, e) => Handle(e.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (status != TestRunStatus.Running)
                break;

            if (process.HasExited)
            {
                // Brief drain to let any final lines flush through the
                // BeginOutputReadLine pump (mirrors validate-device.sh:122).
                try { await Task.Delay(500, cancellationToken).ConfigureAwait(false); } catch { }
                lock (statusLock)
                {
                    if (status == TestRunStatus.Running)
                    {
                        status = TestRunStatus.Exited;
                        reason = $"process exited with code {process.ExitCode} (no marker)";
                    }
                }
                break;
            }

            if (DateTime.UtcNow >= deadline)
            {
                lock (statusLock)
                {
                    if (status == TestRunStatus.Running)
                    {
                        status = TestRunStatus.Timeout;
                        reason = $"no marker after {timeout.TotalSeconds:F0}s";
                    }
                }
                break;
            }

            try
            {
                await Task.Delay(200, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
        }

        if (killOnTerminal && !process.HasExited)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
        }

        // Drain any remaining output even if we killed the process.
        try { process.WaitForExit(2000); } catch { }

        string output;
        lock (bufferLock) { output = buffer.ToString(); }

        return new TestRunResult(status, reason, output, Array.Empty<string>());
    }
}
