namespace SwiftBindings.Build.Models;

/// <summary>
/// Token contract shared by the Mac Catalyst launcher and the TipKit smoke app. LaunchServices
/// detaches app stdout from the <c>open</c> process, so each launch receives a fresh token and
/// writes to fresh files; the runner accepts a TipKit pass only when both managed lifecycle entry
/// and the terminal result carry that exact token.
/// </summary>
public static class CatalystLaunchContract
{
    public const string RunTokenArgument = "--swift-bindings-catalyst-run-token";
    public const string ManagedEntryPrefix = "[TIPKIT-CATALYST] MANAGED ENTRY run_token=";
    public const string ResultPrefix = "[TIPKIT-CATALYST] RESULT run_token=";

    public static string ManagedEntryMarker(string runToken) => ManagedEntryPrefix + runToken;

    public static string ResultMarker(string runToken, bool success) =>
        $"{ResultPrefix}{runToken} status={(success ? "success" : "failure")}";

    public static bool IsValidRunToken(string? runToken) =>
        Guid.TryParseExact(runToken, "N", out _);

    public static bool IsCorrelatedTipKitSuccess(TestRunResult result, string runToken) =>
        IsValidRunToken(runToken) &&
        result.Status == TestRunStatus.Success &&
        result.Output.Contains(ManagedEntryMarker(runToken), StringComparison.Ordinal) &&
        result.Output.Contains(ResultMarker(runToken, success: true), StringComparison.Ordinal);
}
