using System.Text.RegularExpressions;

namespace SwiftBindings.Build.Models;

/// <summary>
/// Tells apart the two very different things a run without a verdict can mean:
///
/// <list type="number">
///   <item>the app RAN and did not print a success marker — a product result, and</item>
///   <item>the LAUNCHER (<c>simctl</c> / <c>devicectl</c>) gave up before the app process
///         was ever entered — no product signal was produced at all.</item>
/// </list>
///
/// <para>The distinction matters because reporting (2) in (1)'s wording sends the reader
/// hunting for a binding or marshalling defect the evidence does not support, and because
/// (2) is the only one of the two that may be re-attempted: re-running a real failure is
/// how a red turns into a false green.</para>
///
/// <para>The evidence is weighed in a fixed order, and product evidence always wins. A
/// native-loader or crash line (dyld failing to load a framework, a missing symbol, a
/// termination reason) is the app image being entered and dying — the very defect a binding
/// gate exists to catch — even though the launcher usually prints its own abort wording
/// right after it. App banners and any non-launcher output also prevent retry. Generic
/// launcher errors identify who wrote a line, but do not prove a transport or boot failure;
/// only the narrow infrastructure diagnostics below authorize another attempt.</para>
/// </summary>
public static class LaunchDiagnostics
{
    /// <summary>The exact UIKitMacHelper assertion caused by bypassing LaunchServices.</summary>
    public const string UIKitMacHelperSceneBootstrapAssertion =
        "_mainSceneIdentifier should have been set by now!";

    /// <summary>
    /// The launcher's own confirmation that it handed the process to the OS. Past this point a
    /// failure belongs to the app.
    /// </summary>
    static readonly string[] LauncherStartedApp =
    {
        "Launched application with",                 // devicectl
        "Waiting for the application to terminate",  // devicectl --console
    };

    /// <summary>
    /// Launcher-owned diagnostics. These identify line ownership for the launch budget;
    /// generic error domains and launch refusal messages do not authorize retries.
    /// </summary>
    public static readonly string[] LauncherAbortPatterns =
    {
        "The application failed to launch",                 // devicectl
        "com.apple.dt.CoreDeviceError",                     // devicectl (domain alone is ambiguous)
        "Unable to lookup in current state",                // simctl (device shut down mid-run)
        "FBSOpenApplicationServiceErrorDomain",             // simctl (SpringBoard refused the open)
        "The request to open",                              // simctl
        "An error was encountered processing the command",  // simctl
        "Unable to boot device",                            // simctl
        "Invalid device",                                   // simctl
    };

    // Only explicit simulator boot/state or launcher transport failures justify retry.
    // Error domain/code alone is ambiguous (including packaging and entitlement failures).
    static readonly string[] RetryableInfrastructurePatterns =
    {
        "Unable to lookup in current state: Shutdown",
        "Unable to boot device",
        "CoreSimulatorService connection interrupted",
        "CoreSimulatorService connection invalidated",
    };

    static bool IsRetryableInfrastructureLine(string line)
        => ContainsAny(line, RetryableInfrastructurePatterns)
            || (line.Contains("com.apple.dt.CoreDeviceError", StringComparison.OrdinalIgnoreCase)
                && line.Contains("tunnel connection interrupted", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Evidence that the app image WAS entered and failed on its own: the dynamic loader could
    /// not satisfy the binary (a framework the binding packaged wrong, a symbol a wrapper does
    /// not export), or the process crashed and the OS wrote its termination summary. These are
    /// product results and outrank every launcher pattern — the launcher reports "failed to
    /// launch" for exactly these cases too. Exposed so the self-test can assert each one is
    /// weighed ahead of the abort wording.
    /// </summary>
    public static readonly string[] ProductFailurePatterns =
    {
        "dyld: Library not loaded",       // loader: missing framework/dylib
        "dyld[",                          // loader diagnostics (dyld4 prefix: `dyld[pid]: ...`)
        "Library not loaded:",
        "Symbol not found:",
        "Termination Reason:",            // crash report / launchd summary
        "EXC_BAD_ACCESS",
        "EXC_CRASH",
        "EXC_BREAKPOINT",
        "SIGABRT",
        "SIGSEGV",
        "Unhandled Exception",            // .NET unhandled managed exception banner
        "Unhandled managed exception",
        "DllNotFoundException",
        "EntryPointNotFoundException",
    };

    /// <summary>
    /// Markers the test apps themselves print. Any of these means the app demonstrably ran.
    /// </summary>
    static readonly string[] AppProducedOutput =
    {
        "TEST SUCCESS",
        "TEST FAILED",
        "TEST UNQUALIFIED",
        "[PASS]",
        "[FAIL]",
        "[SKIP]",
        "[INFO]",
    };

    /// <summary>
    /// The per-framework banner prefix every Apple-framework test app logs with
    /// (<c>[STOREKIT2-TEST]</c>, <c>[MUSICKIT-TEST]</c>, …). Matched by shape so a new test app
    /// is recognised without editing this list.
    /// </summary>
    static readonly Regex AppBannerPrefix = new(@"\[[A-Za-z0-9._-]+-TEST\]", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// True when the transcript carries native-loader or crash evidence: the app was entered and
    /// died. Such a run is a product failure whatever the launcher printed afterwards, and must
    /// never be re-attempted.
    /// </summary>
    public static bool ShowsProductFailure(string? output)
        => !string.IsNullOrEmpty(output) && ContainsAny(output!, ProductFailurePatterns);

    /// <summary>
    /// True when the transcript carries one of the app's own banners: the app demonstrably ran.
    /// </summary>
    public static bool ShowsAppOutput(string? output)
        => !string.IsNullOrEmpty(output) && (ContainsAny(output!, AppProducedOutput) || AppBannerPrefix.IsMatch(output!));

    /// <summary>
    /// True when the evidence says the launcher never got the app running: the launcher printed
    /// a specific transport/boot failure, never confirmed a start, the app produced no output,
    /// and nothing shows the app image being entered and failing on its own. The watcher passes
    /// its observed first-output state so ordinary app lines cannot be lost in classification.
    /// </summary>
    public static bool LauncherNeverStartedApp(string? output, bool appOutputObserved = false)
    {
        if (appOutputObserved || string.IsNullOrEmpty(output)) return false;
        if (ShowsProductFailure(output)) return false;
        if (ShowsAppOutput(output)) return false;
        if (ContainsAny(output!, LauncherStartedApp)) return false;
        var lines = output!.Split('\n');
        if (lines.Any(line => !IsLauncherOwnedLine(line))) return false;
        return lines.Any(IsRetryableInfrastructureLine);
    }

    /// <summary>
    /// True only for the retained Mac Catalyst direct-launch scene-bootstrap crash before the
    /// tokenized managed lifecycle marker. This is the sole product-crash-shaped transcript that
    /// the Catalyst runner may follow with a LaunchServices attempt; the original attempt remains
    /// recorded as <see cref="TestRunStatus.Crashed"/>.
    /// </summary>
    public static bool IsUIKitMacHelperPreManagedEntryFailure(
        TestRunResult result, string managedEntryMarker)
    {
        if (result.Status != TestRunStatus.Crashed || string.IsNullOrEmpty(result.Output) ||
            result.Output.Contains(managedEntryMarker, StringComparison.Ordinal))
            return false;

        return result.Output.Contains(UIKitMacHelperSceneBootstrapAssertion, StringComparison.Ordinal) &&
            result.Output.Contains("UIKitMacHelper", StringComparison.Ordinal) &&
            (result.Output.Contains("SIGABRT", StringComparison.Ordinal) ||
             result.Output.Contains("Abort trap: 6", StringComparison.Ordinal));
    }

    /// <summary>
    /// True when the line came from the LAUNCHER rather than from the app under test: it is
    /// blank, or it carries the launcher's own start confirmation or abort wording and none of
    /// the app's markers or failure evidence.
    ///
    /// <para>The watcher watches <c>simctl</c>/<c>devicectl</c>, not the app, so the process's
    /// first line is not necessarily the app's first line. Letting a launcher-owned line end the
    /// launch phase would hand a hung startup the (much larger) run budget — the exact swap the
    /// two budgets exist to prevent. Lines that match nothing here are treated as the app's,
    /// which conservatively blocks retries if ownership is uncertain.</para>
    /// </summary>
    public static bool IsLauncherOwnedLine(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return true;
        if (ShowsProductFailure(line)) return false;
        if (ShowsAppOutput(line)) return false;
        return ContainsAny(line!, LauncherStartedApp) || ContainsAny(line!, LauncherAbortPatterns)
            || IsRetryableInfrastructureLine(line!);
    }

    /// <summary>
    /// How many times a launch is re-attempted, in place, when the launcher aborted before the
    /// app started. This is the validators' budget: they hold the simulator and re-launch on it.
    ///
    /// <para>The CI orchestrator does not re-launch in place — it raises the abort as an
    /// infrastructure failure so a fresh simulator is prepared first, and that path is bounded
    /// by <c>--max-infra-retries</c> instead. The two numbers are deliberately separate: one
    /// counts launches against the same device, the other counts devices.</para>
    /// </summary>
    public const int MaxLauncherAbortAttempts = 3;

    /// <summary>
    /// True once <paramref name="abortCount"/> aborts have been observed and the budget is spent.
    /// The caller must then report a launch-specific diagnosis — never a test verdict.
    /// </summary>
    public static bool LauncherAbortBudgetExhausted(int abortCount) => abortCount >= MaxLauncherAbortAttempts;

    /// <summary>
    /// How long to settle before re-attempting a launch the launcher aborted: linear backoff
    /// (5s, 10s, …) keyed on how many aborts have been seen. Retry pacing only — no delay is
    /// inserted ahead of a first attempt.
    /// </summary>
    public static TimeSpan SettleDelayAfterAbort(int abortCount) =>
        TimeSpan.FromSeconds(5 * Math.Max(1, abortCount));

    /// <summary>
    /// Folds the transcripts of the launch attempts that were re-attempted into the final
    /// result's output, each under its own heading, so a re-launch never discards what the
    /// launcher printed the first time. Returns <paramref name="final"/> unchanged when there
    /// were no earlier attempts.
    /// </summary>
    public static TestRunResult WithPriorAttempts(TestRunResult final, IReadOnlyList<string> priorAttemptOutputs)
    {
        if (priorAttemptOutputs.Count == 0)
            return final;

        var sections = new System.Text.StringBuilder();
        for (int i = 0; i < priorAttemptOutputs.Count; i++)
        {
            sections.Append("=== launch attempt ").Append(i + 1).Append(" (launcher aborted before the app started) ===\n");
            sections.Append(priorAttemptOutputs[i].TrimEnd()).Append('\n');
        }
        sections.Append("=== launch attempt ").Append(priorAttemptOutputs.Count + 1).Append(" (final) ===\n");
        sections.Append(final.Output);
        return final with { Output = sections.ToString() };
    }

    static bool ContainsAny(string haystack, string[] needles)
    {
        foreach (var n in needles)
        {
            if (haystack.Contains(n, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
