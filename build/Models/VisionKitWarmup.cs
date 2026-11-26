// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System.Text.RegularExpressions;

namespace SwiftBindings.Build.Models;

/// <summary>
/// Pure policy for the VisionKit simulator warm-up. The first image analysis after a simulator
/// boots waits for mediaanalysisd's first-run migration, which took from 0.04 s to 356 s on
/// freshly erased simulators under host load: no budget inside the managed app can bound it. The
/// harness therefore runs one throwaway pure-Swift analysis per simulator boot, before the managed
/// VisionKit app, and waits for it under a bound of its own.
/// </summary>
internal static class VisionKitWarmup
{
    internal const string Library = "VisionKit";
    internal const string BundleId = "com.swiftbindings.visionkit.warmup";
    internal const string ReadyPrefix = "[VK-WARMUP] READY ";

    /// <summary>
    /// The unified-log predicate the harness streams while the warm-up runs: the daemon's own
    /// migration lines and VisionKit's request lines. Kept as evidence and quoted in a failure.
    /// </summary>
    internal const string DaemonLogPredicate =
        "process == \"mediaanalysisd\" OR subsystem BEGINSWITH \"com.apple.VisionKit\"";

    // VisionKit's completion line for a request, and the daemon's end of its first-run migration.
    private static readonly string[] CompletionMarkers = ["Mad processing complete", "Migration finished"];
    private static readonly string[] MigrationStartMarkers = ["Resetting processing state", "First daemon run after boot"];

    private static readonly Regex ReadyPattern = new(
        @"^.*\[VK-WARMUP\] READY schema=1 supported=(?<supported>true|false)(?: outcome=(?<outcome>analyzed|refused))? ms=(?<ms>\d+)(?<rest>.*)$",
        RegexOptions.CultureInvariant | RegexOptions.Multiline);

    internal sealed record Receipt(bool Supported, string Outcome, long Milliseconds, string Detail);

    internal static bool AppliesTo(string library, string platform) =>
        string.Equals(library, Library, StringComparison.Ordinal) && platform == "ios";

    /// <summary>
    /// The warm-up's own verdict line. A refusal still counts as warm: VisionKit answered, so the
    /// daemon is past its migration, and whether that refusal is legitimate is the managed
    /// suite's call. A missing or malformed line is a failure.
    /// </summary>
    internal static Receipt RequireReady(string output, string simulatorUdid)
    {
        Match? last = null;
        foreach (Match m in ReadyPattern.Matches(output))
            last = m;
        if (last is null)
            throw new InvalidOperationException(
                $"VisionKit warm-up on simulator {simulatorUdid} printed no '{ReadyPrefix.Trim()}' line, " +
                "so the analysis daemon cannot be shown to be warm. The managed VisionKit suite was not run.");
        var supported = last.Groups["supported"].Value == "true";
        var outcome = last.Groups["outcome"].Success ? last.Groups["outcome"].Value : (supported ? "" : "unsupported");
        if (supported && outcome.Length == 0)
            throw new InvalidOperationException(
                $"VisionKit warm-up on simulator {simulatorUdid} printed a READY line with no outcome: {last.Value.Trim()}");
        return new Receipt(supported, outcome, long.Parse(last.Groups["ms"].Value), last.Groups["rest"].Value.Trim());
    }

    /// <summary>
    /// Identity of one simulator boot: the host PID and start time of the simulator's
    /// <c>launchd_sim</c>, read from <c>ps -axo pid=,lstart=,command=</c>. A reboot changes both, so
    /// a warm-up recorded for one boot never vouches for the next. Null when the simulator's
    /// <c>launchd_sim</c> is not in the listing; the caller then warms unconditionally.
    /// </summary>
    internal static string? BootKey(string simulatorUdid, string psOutput)
    {
        var devicePath = $"/Devices/{simulatorUdid}/";
        foreach (var raw in psOutput.Split('\n'))
        {
            var line = raw.Trim();
            if (!line.Contains("launchd_sim", StringComparison.Ordinal)
                || !line.Contains(devicePath, StringComparison.OrdinalIgnoreCase))
                continue;
            // "<pid> <Dow Mon dd hh:mm:ss yyyy> launchd_sim <path>"
            var commandAt = line.IndexOf("launchd_sim", StringComparison.Ordinal);
            var head = line[..commandAt].Trim();
            var firstSpace = head.IndexOf(' ');
            if (firstSpace <= 0 || !int.TryParse(head[..firstSpace], out var pid))
                continue;
            var started = Regex.Replace(head[(firstSpace + 1)..].Trim(), @"\s+", " ");
            if (started.Length == 0)
                continue;
            return $"{simulatorUdid.ToUpperInvariant()}|{pid}|{started}";
        }
        return null;
    }

    internal static bool IsCompletionLine(string line) =>
        CompletionMarkers.Any(m => line.Contains(m, StringComparison.Ordinal));

    internal static bool IsMigrationStartLine(string line) =>
        MigrationStartMarkers.Any(m => line.Contains(m, StringComparison.Ordinal));

    /// <summary>
    /// The failure a warm-up that did not finish reports. It names the bound, the simulator, what
    /// the daemon log showed, and why the managed suite is not run: its first analysis would queue
    /// behind the same migration and fail for a reason that is not the binding's.
    /// </summary>
    internal static string NotWarmMessage(
        string simulatorUdid, int runBudgetSeconds, string status, string? reason,
        IReadOnlyList<string> daemonLines)
    {
        var migrationSeen = daemonLines.Any(IsMigrationStartLine);
        var completionSeen = daemonLines.Any(IsCompletionLine);
        var tail = daemonLines.Count == 0
            ? "  (no mediaanalysisd or VisionKit lines were logged)"
            : string.Join("\n", daemonLines.TakeLast(8).Select(l => "  " + l));
        return
            $"VisionKit warm-up: the throwaway analysis on simulator {simulatorUdid} did not complete within its " +
            $"{runBudgetSeconds}s bound ({status}{(reason is null ? "" : ": " + reason)}). " +
            $"mediaanalysisd migration started={(migrationSeen ? "yes" : "not seen")}, " +
            $"completion line ('Mad processing complete'/'Migration finished') seen={(completionSeen ? "yes" : "no")}. " +
            "The managed VisionKit suite was not run: its first analysis would wait on the same daemon. " +
            "Re-run on a less loaded host or erase the simulator.\nLast daemon log lines:\n" + tail;
    }
}
