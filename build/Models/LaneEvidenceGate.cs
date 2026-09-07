using System.Text.RegularExpressions;

namespace SwiftBindings.Build.Models;

/// <summary>
/// The rule that decides whether a lane run actually ran the thing its lane
/// claims — the runtime it names, and the packages it says it consumed.
///
/// <para><b>Why this exists.</b> Two device lanes share one bundle id, one
/// device slice and one install path: the NativeAOT cell
/// (<c>dotnet publish -c Release -r ios-arm64 -p:PublishAot=true</c>) and the
/// Mono full-AOT cell (<c>dotnet build -c Debug -r ios-arm64</c>). The only
/// thing separating them is an MSBuild shape. And the release-consumption lane
/// differs from an ordinary test run only by a rewritten reference: if the
/// rewrite silently fails, the app still builds, installs, launches and goes
/// green — while proving nothing about the <c>.nupkg</c>. Both failures are a
/// false green about the exact property the lane exists to establish, which is
/// worse than not having the lane.</para>
///
/// <para><b>The evidence.</b> A generated module initializer (see
/// <c>build/Build.LaneEvidence.cs</c>) prints, from inside the running app,
/// what the process resolved: the runtime that is executing it and the
/// identity of every <c>SwiftBindings.*</c> assembly it loaded. Those lines,
/// not the build log, are what this gate judges.</para>
/// </summary>
public static class LaneEvidenceGate
{
    /// <summary>Prefix every generated evidence line carries.</summary>
    public const string LinePrefix = "[lane-evidence] ";

    static readonly Regex RuntimePattern =
        new(@"\[lane-evidence\] runtime: (?<body>[^\r\n]+)", RegexOptions.CultureInvariant);

    static readonly Regex AssemblyPattern =
        new(@"\[lane-evidence\] assembly: (?<name>\S+) version=(?<version>\S+) informational=(?<informational>\S+)",
            RegexOptions.CultureInvariant);

    /// <summary>What the app's own output says about the claim under test.</summary>
    public enum Verdict
    {
        /// <summary>No evidence line in the output — no verdict available.</summary>
        NoEvidence,

        /// <summary>The evidence agrees with the lane.</summary>
        Match,

        /// <summary>The evidence contradicts the lane.</summary>
        Mismatch,
    }

    /// <summary>One <c>assembly:</c> evidence line.</summary>
    public sealed record AssemblyEvidence(string Name, string Version, string InformationalVersion);

    /// <summary>The runtime evidence line's body, or null when the app printed none.</summary>
    public static string? ExtractRuntimeBanner(string? output)
    {
        if (string.IsNullOrEmpty(output)) return null;
        var m = RuntimePattern.Match(output!);
        return m.Success ? m.Groups["body"].Value : null;
    }

    /// <summary>
    /// Judge a run's output against the runtime its lane launched.
    /// <paramref name="expectMono"/> is true for the Mono full-AOT device cell
    /// and for every simulator lane (Mono JIT); false for NativeAOT.
    ///
    /// <para>The probe reports <c>MonoRuntime=</c> from the presence of MonoVM's
    /// own corlib types, which is the one signal that separates Mono full-AOT
    /// from NativeAOT on a device — both report dynamic code as unsupported, so
    /// a <c>RuntimeFeature</c> heuristic cannot tell them apart and would call
    /// every Mono full-AOT run NativeAOT.</para>
    /// </summary>
    public static Verdict JudgeRuntime(string? output, bool expectMono)
    {
        var body = ExtractRuntimeBanner(output);
        if (body is null) return Verdict.NoEvidence;

        var reportsMono = body.Contains("MonoRuntime=True", StringComparison.Ordinal);
        var reportsNotMono = body.Contains("MonoRuntime=False", StringComparison.Ordinal);
        if (!reportsMono && !reportsNotMono) return Verdict.NoEvidence;

        return reportsMono == expectMono ? Verdict.Match : Verdict.Mismatch;
    }

    /// <summary>Failure text for a runtime mismatch, naming the lane and what ran.</summary>
    public static string RuntimeMismatchMessage(string laneLabel, string banner, bool expectMono) =>
        $"{laneLabel}: the app reported a runtime that contradicts the lane. " +
        $"Expected {(expectMono ? "Mono" : "NativeAOT")}, app reported: {banner}. " +
        "Refusing to report this run under a runtime it did not execute on.";

    /// <summary>Every <c>assembly:</c> evidence line in the output, in order.</summary>
    public static IReadOnlyList<AssemblyEvidence> ExtractAssemblies(string? output)
    {
        var result = new List<AssemblyEvidence>();
        if (string.IsNullOrEmpty(output)) return result;
        foreach (Match m in AssemblyPattern.Matches(output!))
        {
            result.Add(new AssemblyEvidence(
                m.Groups["name"].Value,
                m.Groups["version"].Value,
                m.Groups["informational"].Value));
        }
        return result;
    }

    /// <summary>
    /// Judge the in-process identity of one consumed package against the
    /// version the lane staged. Returns null when the evidence agrees, and a
    /// human-readable failure otherwise.
    ///
    /// <para>Absence of the assembly line is a FAILURE here, unlike the runtime
    /// verdict: the consumption lane exists to establish that the packaged
    /// assembly was loaded, so "the app never mentioned it" is exactly the
    /// outcome that must not pass. A run that failed for an unrelated reason is
    /// reported by the validator before this gate is consulted.</para>
    ///
    /// <para>Comparison is on the assembly's informational version's leading
    /// version component: NuGet package version <c>26.2.14</c> surfaces as
    /// informational version <c>26.2.14</c> or <c>26.2.14+&lt;sha&gt;</c>, and
    /// the file version is padded to four parts (<c>26.2.14.0</c>).</para>
    /// </summary>
    public static string? JudgeAssemblyIdentity(string? output, string assemblyName, string expectedVersion)
    {
        var seen = ExtractAssemblies(output);
        var match = seen.FirstOrDefault(a => string.Equals(a.Name, assemblyName, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            return $"the app never reported loading {assemblyName}. " +
                   $"Assemblies it did report: {(seen.Count == 0 ? "(none)" : string.Join(", ", seen.Select(a => a.Name)))}. " +
                   "The consumption lane cannot claim the packaged assembly was loaded.";
        }

        if (!VersionsAgree(match.InformationalVersion, expectedVersion)
            && !VersionsAgree(match.Version, expectedVersion))
        {
            return $"{assemblyName} loaded at version {match.Version} " +
                   $"(informational {match.InformationalVersion}) but the staged package is {expectedVersion} — " +
                   "the app is running an assembly that did not come from the package under test.";
        }

        return null;
    }

    /// <summary>
    /// True when an assembly version string names the same release as a NuGet
    /// package version. Tolerates SemVer build metadata (<c>+sha</c>), a
    /// prerelease suffix already present in both, and the four-part padding the
    /// assembly file version carries.
    /// </summary>
    internal static bool VersionsAgree(string assemblyVersion, string packageVersion)
    {
        static string Core(string v)
        {
            var s = v.Split('+')[0].Trim();
            // Trim a trailing ".0" revision so 26.2.14.0 matches 26.2.14, but
            // never a meaningful revision: 18.1.1.1 stays 18.1.1.1.
            if (s.EndsWith(".0", StringComparison.Ordinal) && s.Count(c => c == '.') == 3)
                s = s[..^2];
            return s;
        }

        return string.Equals(Core(assemblyVersion), Core(packageVersion), StringComparison.OrdinalIgnoreCase);
    }
}
