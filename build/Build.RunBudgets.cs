using Nuke.Common;

partial class Build
{
    // ═══════════════════════════════════════════════════════════════════════
    //  Run budgets
    //
    //  Every validator watches a test app through two budgets rather than one:
    //
    //    * the LAUNCH budget — how long the app may take to print its first
    //      line. This is install/boot/dyld/runtime-init cost. It moves with
    //      whatever else the host is doing and says nothing about the tests.
    //
    //    * the RUN budget — how long the suite may take to reach a marker,
    //      measured from that first line. This is a property of the tests: a
    //      library whose app awaits several bounded operations in sequence
    //      needs the sum of those bounds plus reporting, no matter how fast
    //      the host is.
    //
    //  Sharing one number forces a choice between two wrong answers: set it
    //  from the slowest suite and a hung launch burns minutes before it is
    //  called; set it from launch cost and a suite whose own bounds exceed it
    //  is killed mid-run, so its assertions never report at all — the failure
    //  reads as "no marker after Ns" and carries no product signal.
    // ═══════════════════════════════════════════════════════════════════════

    [Parameter("Seconds a test app may take to print its FIRST line before the run is called a launch timeout (default: 120)")]
    readonly int LaunchTimeout = DefaultLaunchBudgetSeconds;

    /// <summary>
    /// Default launch budget. Measured first-marker times across the corpus sit around 30s,
    /// with the slowest observed cells straddling that on a loaded host; 120s leaves room for
    /// a cold simulator under parallel dispatch without letting a genuinely hung launch sit.
    /// </summary>
    internal const int DefaultLaunchBudgetSeconds = 120;

    /// <summary>Floor under any launch budget, so a stray small <c>--launch-timeout</c> cannot make every cell a timeout.</summary>
    internal const int MinLaunchBudgetSeconds = 30;

    /// <summary>
    /// Per-library run-budget floors, in seconds. The requested budget comes from
    /// <c>--timeout</c>; an entry here raises it (never lowers it) for a library whose test app
    /// awaits more than the default allows. Each number is the sum of that app's own sequential
    /// time bounds plus startup and reporting — if you change the app's bounds, change this too.
    ///
    /// <list type="bullet">
    ///   <item><b>MapLibre</b> — its app waits up to 60s for the map/style/render/region
    ///   callbacks to settle, then runs behaviour probes whose own bounded waits add 45s
    ///   (offline-style snapshot) + 20s + 15s + 10s + 45s (a second map's style/load) before it
    ///   reports: ~195s of sequential waiting in the all-suppressed worst case, before the
    ///   unbounded-but-fast probes and the final report.</item>
    ///   <item><b>StoreKit2</b> — worst-case sequential waits are 120s (AppTransaction.shared)
    ///   + 120s + 120s (the two finite transaction sequences) + 30s (Storefront) + 30s
    ///   (Transaction.updates cancellation) + 30s (Product.products) ≈ 450s of bounded awaits
    ///   before any of the unbounded-but-fast probes. Anything below that kills the process
    ///   mid-suite, so the assertions those bounds exist to make never report either way.</item>
    /// </list>
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, int> LibraryRunBudgetSeconds =
        new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["MapLibre"] = 300,
            ["StoreKit2"] = 600,
        };

    /// <summary>
    /// The run budget for a library: the requested value, raised to the library's floor when it
    /// has one. Never lowers a caller's request — a longer <c>--timeout</c> always wins.
    /// </summary>
    internal static int ResolveRunBudgetSeconds(string library, int requestedSeconds) =>
        Math.Max(requestedSeconds, LibraryRunBudgetSeconds.TryGetValue(library, out var floor) ? floor : 0);

    /// <summary>The launch budget for a run: the requested value, floored at <see cref="MinLaunchBudgetSeconds"/>.</summary>
    internal static int ResolveLaunchBudgetSeconds(int requestedSeconds) =>
        Math.Max(requestedSeconds, MinLaunchBudgetSeconds);

    /// <summary>The launch budget this invocation runs with.</summary>
    int LaunchBudgetSeconds => ResolveLaunchBudgetSeconds(LaunchTimeout);

    /// <summary>
    /// How long an <c>simctl install</c> may take before the attempt gives up on it, given the
    /// time left in the step budget and the launch+run budgets the watcher still needs.
    ///
    /// <para>The install and the watch run one after the other, so they get sequential caps
    /// rather than two draws on one countdown: the watcher's budgets are reserved first — they
    /// are the only part of an attempt that produces a verdict — and the install takes what is
    /// left, bounded above by the empirical overhead and below by
    /// <see cref="MinInstallCapSeconds"/> so a tight deadline cannot cap it at zero and turn a
    /// healthy install into an infrastructure failure.</para>
    /// </summary>
    internal static int ResolveInstallCapSeconds(double remainingSeconds, int watchBudgetSeconds) =>
        (int)Math.Max(MinInstallCapSeconds,
            Math.Min(InstallOverheadSeconds, remainingSeconds - watchBudgetSeconds));
}
