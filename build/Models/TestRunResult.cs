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

    /// <summary>
    /// The launcher process (<c>simctl</c>/<c>devicectl</c>) gave up before the app was
    /// entered: it printed one of its own abort messages, never confirmed a start, and no
    /// app output was captured. Distinct from <see cref="Exited"/>, which means the app
    /// itself ran and stopped without printing a marker — that one is a product result and
    /// stays fail-loud. A launcher abort names its own cause and carries no verdict about the
    /// code under test, so it is the one status a caller may re-attempt. Nothing else is:
    /// a <see cref="Timeout"/> is silence, which a hung app and a stalled host produce alike,
    /// and re-running silence is how an intermittent hang is recorded as a pass.
    /// </summary>
    LauncherAborted,

    /// <summary>
    /// Saw a line containing <c>TEST UNQUALIFIED</c>: the app ran every check it was allowed to
    /// run and none failed, but a qualification section that needs an owner-held capability the
    /// lane could not attest (for example a StoreKit Sandbox account) did not run. Never a pass
    /// and never retryable; a regression cell records it as ERROR, which still fails the run.
    /// </summary>
    Unqualified,
}

/// <summary>
/// Typed result returned by <see cref="Helpers.StdoutWatcher.RunAsync"/>.
/// </summary>
/// <param name="Status">Terminal state of the watcher.</param>
/// <param name="Reason">Optional human-readable explanation (e.g. matched line, signal token).</param>
/// <param name="Output">Captured stdout from the process (full buffer for diagnostics).</param>
/// <param name="CrashLogs">Crash log paths discovered after the run, if any.</param>
/// <param name="ExitCode">
/// Exit code of the watched process when it ended on its own; <c>null</c> when the watcher
/// reached a terminal state first and killed it. Together with
/// <see cref="TestRunStatus.LauncherAborted"/> this is what separates "the launcher quit" from
/// "the app quit".
/// </param>
/// <param name="TimeToFirstOutput">
/// How long the run took to produce the APP's first line, or <c>null</c> when the app never
/// spoke — lines the launcher printed itself do not count (see
/// <see cref="LaunchDiagnostics.IsLauncherOwnedLine"/>). Recorded so a cold-start cost can be
/// read off a run rather than guessed at — the launch budget is set from these.
/// </param>
public sealed record TestRunResult(
    TestRunStatus Status,
    string? Reason,
    string Output,
    IReadOnlyList<string> CrashLogs,
    int? ExitCode = null,
    TimeSpan? TimeToFirstOutput = null)
{
    /// <summary>
    /// Which phase the timeout expired in: <c>true</c> when the app had not produced a line yet,
    /// <c>false</c> when it was already reporting and stopped short of a marker.
    ///
    /// <para>Reporting only. Both are terminal — the distinction says where to look (install,
    /// dyld and startup, versus a suite that stalled part-way), not whether the run may be
    /// repeated.</para>
    /// </summary>
    public bool IsLaunchPhaseTimeout => Status == TestRunStatus.Timeout && TimeToFirstOutput is null;
}
