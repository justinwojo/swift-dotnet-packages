namespace SwiftBindings.Build.Models;

/// <summary>
/// Result of running a sim/device test app and parsing its stdout via
/// <see cref="Helpers.StdoutWatcher"/>. Replaces the implicit string
/// state in <c>scripts/validate-sim.sh</c> and <c>scripts/validate-device.sh</c>.
/// </summary>
public enum TestRunStatus
{
    /// <summary>State machine still observing stdout — not a terminal value.</summary>
    Running,

    /// <summary>Saw a line equal to <c>TEST SUCCESS</c>.</summary>
    Success,

    /// <summary>Saw a line starting with <c>TEST FAILED</c> (substring match — see Session 3 spec).</summary>
    Failed,

    /// <summary>
    /// Saw a crash signal token in a line that does NOT start with <c>[SKIP]</c>.
    /// Per-line check kills the bash skip-message false-positive footgun.
    /// </summary>
    Crashed,

    /// <summary>Watcher reached its deadline before any terminal marker.</summary>
    Timeout,

    /// <summary>App process exited (only relevant for <c>devicectl</c> launches).</summary>
    Exited,
}

/// <summary>
/// Typed result returned by <see cref="Helpers.StdoutWatcher.RunAsync"/>.
/// </summary>
/// <param name="Status">Terminal state of the watcher.</param>
/// <param name="Reason">Optional human-readable explanation (e.g. matched line, signal token).</param>
/// <param name="Output">Captured stdout from the process (full buffer for diagnostics).</param>
/// <param name="CrashLogs">Crash log paths discovered after the run, if any.</param>
public sealed record TestRunResult(
    TestRunStatus Status,
    string? Reason,
    string Output,
    IReadOnlyList<string> CrashLogs);
