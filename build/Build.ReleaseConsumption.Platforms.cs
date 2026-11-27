using System.Text.Json;
using Nuke.Common;
using Nuke.Common.IO;
using Serilog;
using SwiftBindings.Build.Models;

partial class Build
{
    // ═══════════════════════════════════════════════════════════════════════
    //  Release consumption across platforms, configurations and engines.
    //
    //  A merged package carries one copy per pairing (lib/net10.0-ios26.0/,
    //  lib/net10.0-ios27.2/, lib/net11.0-ios27.2/, and the same under native/),
    //  and NuGet picks one of them per consumer TFM. Consuming on the iOS
    //  simulator in Debug proves that selection for one platform and one engine
    //  only. A consume leg here is the consume lane run for one of the validate
    //  cells' host platforms (ios-sim, macos, maccatalyst, tvos, and on .NET 11
    //  the *-release ReadyToRun lanes), with the same configuration and the same
    //  engine proof from the bundle a validate cell uses, plus a copy check: the
    //  managed assembly and the native frameworks the app carries must be THIS
    //  pairing's copy, never another pairing's.
    // ═══════════════════════════════════════════════════════════════════════

    [Parameter("ReleaseConsume: platforms to consume on (comma list, default ios-sim). Same spellings as " +
               "RegressionValidate --platforms minus the device lanes: ios-sim, macos, maccatalyst, tvos, " +
               "ios-sim-release / maccatalyst-release / tvos-release (.NET 11), host (every one the test app declares)")]
    readonly string? ConsumePlatforms;

    /// <summary>One consume leg's outcome, as recorded in <c>consume-legs.json</c>.</summary>
    internal sealed record ConsumeLegRecord(
        string Library,
        string Pairing,
        string Platform,
        string Configuration,
        string Engine,
        bool EngineProven,
        string Result,
        string? Reason,
        string? ManagedCopy,
        IReadOnlyList<string> NativeCopies,
        double Seconds);

    /// <summary>What a passing leg established, for the leg record. <paramref name="Unqualified"/> is
    /// the reason when the suite passed offline but its live credential section did not run.</summary>
    internal sealed record ConsumeLegEvidence(string? ManagedCopy, IReadOnlyList<string> NativeCopies,
        string? Unqualified = null);

    /// <summary>The Apple platform a consume leg builds (<c>ios</c>, <c>macos</c>, <c>maccatalyst</c>, <c>tvos</c>).</summary>
    static string ConsumePlatformName(CellPlatform p) => BasePlatform(p) switch
    {
        CellPlatform.IosSim => "ios",
        CellPlatform.MacOs => "macos",
        CellPlatform.MacCatalyst => "maccatalyst",
        CellPlatform.TvOs => "tvos",
        _ => throw new InvalidOperationException(
            $"{PlatformLabel(p)} is not a consume platform: the consume lane runs host platforms only " +
            "(device consumption is not a lane yet)."),
    };

    /// <summary>
    /// The legs to run: the requested platforms the test app declares, in request order.
    /// <para>A platform named explicitly that the test app does not declare is a hard error —
    /// silently dropping it would report coverage that never ran. A platform that only came in
    /// through <c>host</c> (absent from <paramref name="named"/>) is kept only when declared. A device platform
    /// is refused either way, and an empty plan is refused: a consume run with no legs proves
    /// nothing.</para>
    /// </summary>
    internal static IReadOnlyList<CellPlatform> PlanConsumeLegs(
        IReadOnlyList<CellPlatform> requested,
        IReadOnlyCollection<string> declaredTfms,
        Func<CellPlatform, string> tfmOf,
        IReadOnlyCollection<CellPlatform> named,
        string context)
    {
        var device = requested.Where(IsDeviceCell).ToList();
        if (device.Count > 0)
            throw new InvalidOperationException(
                $"{context}: {string.Join(", ", device.Select(PlatformLabel))} is a device lane; the consume lane " +
                "runs host platforms only (ios-sim, macos, maccatalyst, tvos and their -release lanes).");

        var legs = new List<CellPlatform>();
        var undeclared = new List<string>();
        foreach (var p in requested)
        {
            if (declaredTfms.Contains(tfmOf(p), StringComparer.Ordinal))
            {
                if (!legs.Contains(p)) legs.Add(p);
            }
            else if (named.Contains(p))
                undeclared.Add($"{PlatformLabel(p)} ({tfmOf(p)})");
        }

        if (undeclared.Count > 0)
            throw new InvalidOperationException(
                $"{context}: the test app does not declare {string.Join(", ", undeclared)} " +
                $"(it declares {string.Join(", ", declaredTfms)}). Name only platforms it builds, or pass host.");
        if (legs.Count == 0)
            throw new InvalidOperationException(
                $"{context}: none of the requested platforms is one the test app declares " +
                $"({string.Join(", ", declaredTfms)}), so there is nothing to consume.");
        return legs;
    }

    /// <summary>The xcframework slice a consumer on <paramref name="platform"/> embeds from.</summary>
    internal static bool ConsumeSliceMatches(string platform, string slice) => platform switch
    {
        "ios" => IsIosSimulatorSlice(slice),
        "tvos" => slice.StartsWith("tvos-", StringComparison.OrdinalIgnoreCase)
                  && slice.Contains("simulator", StringComparison.OrdinalIgnoreCase),
        "maccatalyst" => slice.StartsWith("ios-", StringComparison.OrdinalIgnoreCase)
                         && slice.Contains("maccatalyst", StringComparison.OrdinalIgnoreCase),
        "macos" => slice.StartsWith("macos-", StringComparison.OrdinalIgnoreCase),
        _ => false,
    };

    /// <summary>
    /// The pairing copy a package-relative path belongs to: the folder right under <c>lib/</c>
    /// or <c>native/</c> (<c>native/net11.0-ios27.2/X.xcframework/…</c> → <c>net11.0-ios27.2</c>).
    /// Null for a path outside those layouts (a runtime package's <c>runtimes/&lt;rid&gt;/native/</c>).
    /// </summary>
    internal static string? CopyTfmOf(string packageRelativePath)
    {
        var parts = packageRelativePath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 && parts[0] is "lib" or "native" ? parts[1] : null;
    }

    /// <summary>
    /// Judge which pairing copy of a package's managed assembly the app deployed.
    /// <paramref name="copies"/> is every <c>lib/&lt;tfm&gt;/&lt;Id&gt;.dll</c> the package ships, by
    /// copy folder. Null failure when the deployed MVID is the expected copy's; otherwise the
    /// reason. Copies with the same MVID as the expected one (byte-identical compilations) are
    /// not a mismatch — the selection is moot for them — and are named in the note.
    /// </summary>
    internal static (string? Failure, string Note) JudgeManagedCopy(
        string packageId, Guid deployedMvid, IReadOnlyList<(string CopyTfm, Guid? Mvid)> copies, string expectedCopyTfm)
    {
        var expected = copies.Where(c => string.Equals(c.CopyTfm, expectedCopyTfm, StringComparison.OrdinalIgnoreCase)).ToList();
        var others = copies.Where(c => !string.Equals(c.CopyTfm, expectedCopyTfm, StringComparison.OrdinalIgnoreCase)).ToList();
        var shipped = string.Join(", ", copies.Select(c => c.CopyTfm).Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.Ordinal));

        if (expected.Count == 0)
            return ($"{packageId} ships no lib/{expectedCopyTfm}/ copy (it ships [{shipped}]), so this pairing's consumer " +
                    "cannot have selected its own copy.", "");

        if (expected.Any(c => c.Mvid == deployedMvid))
        {
            var twins = others.Where(c => c.Mvid == deployedMvid).Select(c => c.CopyTfm).ToList();
            return (null, twins.Count == 0
                ? $"lib/{expectedCopyTfm}/"
                : $"lib/{expectedCopyTfm}/ (identical to {string.Join(", ", twins)})");
        }

        var wrong = others.Where(c => c.Mvid == deployedMvid).Select(c => c.CopyTfm).ToList();
        return wrong.Count > 0
            ? ($"the deployed {packageId}.dll (mvid {deployedMvid}) is the lib/{string.Join(", lib/", wrong)}/ copy, not " +
               $"this pairing's lib/{expectedCopyTfm}/ — the consumer selected another pairing's build.", "")
            : ($"the deployed {packageId}.dll (mvid {deployedMvid}) matches none of the copies {packageId} ships " +
               $"([{shipped}]) — the app is shipping a different build of the binding than the package under release.", "");
    }

    /// <summary>
    /// The bundle a consume leg launches and inspects: the same RID and path resolution the
    /// validators use, under the lane's own output root. Null when the build produced none.
    /// </summary>
    AbsolutePath? ConsumeBundlePath(AbsolutePath outputRoot, string configuration, string platform, string appName) =>
        (platform == "ios"
            // The build honours --runtime-identifier, so the bundle lookup must too.
            ? new[] { ResolveAppPath(outputRoot, configuration, "ios", ResolveRid("iossimulator-arm64"), appName) }
            : EngineBundleCandidates(outputRoot, configuration, platform, appName))
        .FirstOrDefault(c => Directory.Exists(c));

    /// <summary>
    /// Fail when a consume run recorded no legs or any leg that did not pass. The one non-pass
    /// that is not a failure is DEFERRED, which only <c>--defer-credential-qualification</c>
    /// produces (as for a validate cell): every check the leg could run passed on its proven engine.
    /// </summary>
    internal static IReadOnlyList<string> ConsumeSummaryFailures(IReadOnlyList<ConsumeLegRecord> legs)
    {
        if (legs.Count == 0)
            return new[] { "no consume leg ran, so the run says nothing about the package." };
        var failures = new List<string>();
        foreach (var leg in legs)
        {
            if (leg.Result is not ("PASS" or "DEFERRED"))
                failures.Add($"{leg.Library} {leg.Pairing} {leg.Platform} ({leg.Engine}): {leg.Result} — {leg.Reason}");
            else if (!leg.EngineProven)
                failures.Add($"{leg.Library} {leg.Pairing} {leg.Platform}: {leg.Result} recorded without an engine proof for {leg.Engine}.");
        }
        return failures;
    }

    /// <summary>Run every requested consume leg, record each, and fail if any leg did.</summary>
    void RunReleaseConsumptionLegs(string library, string version, AbsolutePath feed)
    {
        var raw = string.IsNullOrWhiteSpace(ConsumePlatforms) ? "ios-sim" : ConsumePlatforms!;
        var requested = ParsePlatformsParam(raw);
        // Platforms named outright stay strict even next to `host`.
        var namedTokens = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(t => !t.Equals("host", StringComparison.OrdinalIgnoreCase)).ToList();
        var named = namedTokens.Count == 0 ? Array.Empty<CellPlatform>() : ParsePlatformsParam(string.Join(",", namedTokens));
        var (testDir, _, _) = ResolveTestNames(library);
        var legs = PlanConsumeLegs(requested, ParseTestProjectTfms(testDir), PlatformTfm, named,
            $"release-consume/{library}");
        Log.Information("release-consume/{Library}: pairing {Pairing}, legs {Legs}", library, ActivePairing.Name,
            string.Join(", ", legs.Select(p => $"{PlatformLabel(p)}={ToolchainPairing.EngineName(ExpectedEngine(p))}")));

        var records = new List<ConsumeLegRecord>();
        foreach (var leg in legs)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var engine = ToolchainPairing.EngineName(ExpectedEngine(leg));
            var proven = false;
            try
            {
                var evidence = RunReleaseConsumptionLeg(library, version, feed, Timeout, DeviceUdid, onLine: null,
                    platform: leg, onEngineProven: () => proven = true);
                // Offline checks passed but the live credential section did not run: DEFERRED only
                // when the operator deferred it, exactly as a validate cell records it.
                var (result, reason) = evidence.Unqualified is null
                    ? ("PASS", (string?)null)
                    : DeferCredentialQualification
                        ? ("DEFERRED", $"CREDENTIAL QUALIFICATION DEFERRED — {evidence.Unqualified}")
                        : ("ERROR", $"QUALIFICATION NOT RUN — {evidence.Unqualified}");
                records.Add(new ConsumeLegRecord(library, ActivePairing.Name, PlatformLabel(leg), CellConfiguration(leg),
                    engine, proven, result, reason, evidence.ManagedCopy, evidence.NativeCopies, sw.Elapsed.TotalSeconds));
            }
            catch (Exception ex)
            {
                // Any exception (a simulator boot timeout included) ends this leg, never the run:
                // the remaining legs still run and the record is still written.
                Log.Error("release-consume/{Library} {Leg}: {Message}", library, PlatformLabel(leg), ex.Message);
                records.Add(new ConsumeLegRecord(library, ActivePairing.Name, PlatformLabel(leg), CellConfiguration(leg),
                    engine, proven, proven ? "FAIL" : "ERROR", ex.Message, null, Array.Empty<string>(),
                    sw.Elapsed.TotalSeconds));
            }
            var r = records[^1];
            Log.Information("CONSUME-LEG {Library} {Pairing} {Platform} -c {Config} engine={Engine} proven={Proven} {Result} ({Sec:F0}s)",
                r.Library, r.Pairing, r.Platform, r.Configuration, r.Engine, r.EngineProven, r.Result, r.Seconds);
        }

        var summaryPath = ReleaseConsumptionRoot / library / $"consume-legs.{ActivePairing.Name}.json";
        summaryPath.Parent.CreateDirectory();
        File.WriteAllText(summaryPath, JsonSerializer.Serialize(new
        {
            library,
            version,
            pairing = ActivePairing.Name,
            feed = (string)feed,
            legs = records,
        }, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        Log.Information("release-consume/{Library}: leg record at {Path}", library, summaryPath);

        var failures = ConsumeSummaryFailures(records);
        if (failures.Count > 0)
            throw new InvalidOperationException(
                $"release-consume/{library}: {failures.Count} of {records.Count} leg(s) did not pass:\n  " +
                string.Join("\n  ", failures));
        Log.Information("release-consume/{Library}: {Passed} leg(s) passed, {Deferred} deferred, on {Pairing}", library,
            records.Count(r => r.Result == "PASS"), records.Count(r => r.Result == "DEFERRED"), ActivePairing.Name);
    }
}
