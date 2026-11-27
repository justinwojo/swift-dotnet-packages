using System.Text.Json;
using Nuke.Common;
using Nuke.Common.IO;
using Serilog;

partial class Build
{
    /// <summary>
    /// In-process behavioral tests for the regression-matrix evidence contract
    /// (F1: self-describing, un-clobberable artifacts; F2: verdict + exit-code
    /// integrity). No simulator, no device, no packages — synthetic
    /// <see cref="CellOutcome"/>s driven straight through the reporting tail.
    ///
    /// <para>
    /// This exists because the behavior under test only manifests on a full
    /// matrix run (tens of minutes, a booted sim fleet and a provisioned iPhone),
    /// which makes "run it twice and check nothing was clobbered" impractical as
    /// a routine check. Same shape as <c>TestCsprojRewriter</c>: pure assertions,
    /// no external test framework. Everything is written under the sentinel
    /// version <c>0.0.0-selftest</c> and deleted afterwards.
    /// </para>
    /// </summary>
    Target TestRegressionArtifacts => _ => _
        .Description("Run in-process tests for the regression-validate artifact + verdict contract")
        .Executes(async () =>
        {
            const string version = "0.0.0-selftest";
            var failures = new List<string>();

            void Assert(string name, bool condition, string detail)
            {
                if (!condition) failures.Add($"  ✗ {name}: {detail}");
                else Log.Information("  ✓ {Name}", name);
            }

            var artifactDir = RootDirectory / "artifacts";
            var canonical = artifactDir / CanonicalArtifactName(version);
            var runsDir = RegressionRunsDir(version);

            // Start from a clean slate so a previous aborted self-test can't
            // make a partial-index assertion pass or fail spuriously.
            CleanSelfTestArtifacts(version);

            static CellOutcome Pass(string lib, CellPlatform p, double sec) =>
                new(new Cell(lib, p), CellResult.Pass, null, TimeSpan.FromSeconds(sec));
            static CellOutcome Fail(string lib, CellPlatform p, string reason) =>
                new(new Cell(lib, p), CellResult.Fail, reason, TimeSpan.FromSeconds(1));

            JsonDocument Read(AbsolutePath p) => JsonDocument.Parse(File.ReadAllText(p));

            try
            {
                // ── Case 1: complete run claims the canonical slot ──
                {
                    var runId = ComposeRunId(new DateTime(2026, 7, 26, 10, 0, 0, DateTimeKind.Utc), "aaaaaaa");
                    var runDir = runsDir / runId;
                    var outcomes = new[]
                    {
                        Pass("Kingfisher", CellPlatform.IosSim, 30),
                        Pass("Kingfisher", CellPlatform.IosDevice, 90),
                        Pass("Kingfisher", CellPlatform.MacOs, 20),
                        Pass("Kingfisher", CellPlatform.MacCatalyst, 20),
                        Pass("Kingfisher", CellPlatform.TvOs, 20),
                    };
                    var result = WriteRegressionArtifacts(
                        version, runId, runDir, AllPlatforms, filter: null,
                        packages: new[] { "Kingfisher" }, cellsPlanned: outcomes.Length,
                        unreached: Array.Empty<Cell>(), outcomes: outcomes, abortReason: null,
                        totalDuration: TimeSpan.FromMinutes(3),
                        inputs: new { sdkVersion = version }, apiSurface: new { filesCollected = 0 });

                    Assert("complete run is marked complete", result.Complete,
                        $"reasons: {string.Join("; ", result.IncompleteReasons)}");
                    Assert("complete run writes the canonical artifact",
                        result.CanonicalArtifact is not null && File.Exists(canonical),
                        "canonical artifact missing");
                    Assert("complete run writes a run-scoped artifact",
                        File.Exists(runDir / "regression-validate.json"),
                        $"no artifact under {runDir}");

                    using var doc = Read(canonical);
                    var root = doc.RootElement;
                    Assert("verdict PASS on an all-pass run",
                        root.GetProperty("verdict").GetString() == "PASS",
                        root.GetProperty("verdict").GetString() ?? "<null>");
                    Assert("exitCode 0 on an all-pass run",
                        root.GetProperty("exitCode").GetInt32() == 0,
                        root.GetProperty("exitCode").GetInt32().ToString());
                    Assert("artifact carries cellsPlanned",
                        root.GetProperty("cellsPlanned").GetInt32() == 5, "cellsPlanned != 5");
                    Assert("artifact carries an inputs block",
                        root.TryGetProperty("inputs", out var _inputs), "no inputs block");
                    Assert("artifact keeps the legacy cells[] shape (skill + scheduler read it)",
                        root.GetProperty("cells").EnumerateArray().Any(c =>
                            c.GetProperty("library").GetString() == "Kingfisher"
                            && c.GetProperty("platform").GetString() == "ios-sim"
                            && c.GetProperty("result").GetString() == "PASS"
                            && c.TryGetProperty("durationSeconds", out var _dur)),
                        "cells[] entry missing or reshaped");
                }

                var canonicalStamp = File.ReadAllText(canonical);  // reassigned after Case 5

                // ── Case 2: a filtered (partial) run must NOT clobber the canonical slot ──
                {
                    var runId = ComposeRunId(new DateTime(2026, 7, 26, 11, 0, 0, DateTimeKind.Utc), "bbbbbbb");
                    var runDir = runsDir / runId;
                    var outcomes = new[] { Pass("Kingfisher", CellPlatform.IosSim, 31) };
                    var result = WriteRegressionArtifacts(
                        version, runId, runDir, new[] { CellPlatform.IosSim }, filter: "Kingfisher",
                        packages: new[] { "Kingfisher" }, cellsPlanned: 1,
                        unreached: Array.Empty<Cell>(), outcomes: outcomes, abortReason: null,
                        totalDuration: TimeSpan.FromSeconds(31),
                        inputs: new { sdkVersion = version }, apiSurface: new { filesCollected = 0 });

                    Assert("filtered run is NOT complete", !result.Complete, "marked complete");
                    Assert("filtered run explains why it is incomplete",
                        result.IncompleteReasons.Any(r => r.Contains("--filter"))
                        && result.IncompleteReasons.Any(r => r.Contains("platform subset")),
                        string.Join("; ", result.IncompleteReasons));
                    Assert("filtered run writes .partial-1.json",
                        result.PartialArtifact == artifactDir / $"regression-validate-{version}-{ActivePairing.Name}.partial-1.json"
                        && File.Exists(result.PartialArtifact),
                        $"partial artifact = {result.PartialArtifact}");
                    Assert("filtered run leaves the canonical artifact byte-identical",
                        File.ReadAllText(canonical) == canonicalStamp,
                        "canonical artifact was overwritten by a partial run");

                    using var doc = Read(result.PartialArtifact!);
                    Assert("partial artifact records complete:false",
                        !doc.RootElement.GetProperty("complete").GetBoolean(), "complete was true");
                    Assert("partial artifact records the filter it ran under",
                        doc.RootElement.GetProperty("filter").GetString() == "Kingfisher", "filter not recorded");
                }

                // ── Case 3: a second partial run gets its own index (no clobber) ──
                {
                    var runId = ComposeRunId(new DateTime(2026, 7, 26, 12, 0, 0, DateTimeKind.Utc), "ccccccc");
                    var result = WriteRegressionArtifacts(
                        version, runId, runsDir / runId, new[] { CellPlatform.MacOs }, filter: "Lottie",
                        packages: new[] { "Lottie" }, cellsPlanned: 1,
                        unreached: Array.Empty<Cell>(),
                        outcomes: new[] { Pass("Lottie", CellPlatform.MacOs, 12) },
                        abortReason: null, totalDuration: TimeSpan.FromSeconds(12),
                        inputs: new { sdkVersion = version }, apiSurface: new { filesCollected = 0 });

                    Assert("second partial run writes .partial-2.json",
                        result.PartialArtifact == artifactDir / $"regression-validate-{version}-{ActivePairing.Name}.partial-2.json",
                        $"partial artifact = {result.PartialArtifact}");
                    Assert("first partial artifact survives the second partial run",
                        File.Exists(artifactDir / $"regression-validate-{version}-{ActivePairing.Name}.partial-1.json"),
                        "partial-1 was clobbered");
                }

                // ── Case 4: two runs never share an evidence directory ──
                {
                    var runDirs = Directory.EnumerateDirectories(runsDir).ToList();
                    Assert("each run has its own evidence directory",
                        runDirs.Count == 3, $"expected 3 run dirs, found {runDirs.Count}");
                    Assert("every run directory holds its own verdict artifact",
                        runDirs.All(d => File.Exists(Path.Combine(d, "regression-validate.json"))),
                        "a run directory has no artifact");
                }

                // ── Case 5: failing cells are named and typed in the artifact ──
                {
                    var runId = ComposeRunId(new DateTime(2026, 7, 26, 13, 0, 0, DateTimeKind.Utc), "ddddddd");
                    var outcomes = new[]
                    {
                        Pass("Kingfisher", CellPlatform.IosSim, 30),
                        Fail("Stripe", CellPlatform.IosSim, "CS0246: StripeCore not found"),
                        new CellOutcome(new Cell("Nuke", CellPlatform.TvOs), CellResult.BuildOnly, "no tvos validator yet", TimeSpan.FromSeconds(9)),
                    };
                    var result = WriteRegressionArtifacts(
                        version, runId, runsDir / runId, AllPlatforms, filter: null,
                        packages: new[] { "Kingfisher", "Stripe", "Nuke" }, cellsPlanned: 3,
                        unreached: Array.Empty<Cell>(), outcomes: outcomes, abortReason: null,
                        totalDuration: TimeSpan.FromMinutes(1),
                        inputs: new { sdkVersion = version }, apiSurface: new { filesCollected = 0 });

                    using var doc = Read(result.RunArtifact);
                    var root = doc.RootElement;
                    Assert("verdict FAIL when a cell fails",
                        root.GetProperty("verdict").GetString() == "FAIL",
                        root.GetProperty("verdict").GetString() ?? "<null>");
                    Assert("exitCode 2 (cell failures) is recorded in the artifact",
                        root.GetProperty("exitCode").GetInt32() == RegressionExitCellsFailed,
                        root.GetProperty("exitCode").GetInt32().ToString());
                    Assert("failedCells names the failing cell",
                        root.GetProperty("failedCells").EnumerateArray().Any(c =>
                            c.GetProperty("library").GetString() == "Stripe"
                            && c.GetProperty("platform").GetString() == "ios-sim"
                            && c.GetProperty("reason").GetString()!.Contains("CS0246")),
                        "failing cell absent from failedCells");
                    Assert("BUILD-ONLY does not count as a failure",
                        root.GetProperty("counts").GetProperty("fail").GetInt32() == 1,
                        $"fail count = {root.GetProperty("counts").GetProperty("fail").GetInt32()}");
                    // A COMPLETE run owns the canonical slot whatever its verdict.
                    // The slot means "latest full-matrix record for this version",
                    // not "latest green one" — leaving a stale PASS there after a
                    // red full run is the same false-confidence inversion this
                    // whole change exists to remove. The superseded green run is
                    // not lost: its own run directory still holds it.
                    Assert("a complete-but-failing run replaces the canonical record",
                        File.ReadAllText(canonical) != canonicalStamp
                        && Read(canonical).RootElement.GetProperty("verdict").GetString() == "FAIL",
                        "canonical artifact still reports the superseded PASS");
                    Assert("the superseded green run survives in its own run directory",
                        Directory.EnumerateDirectories(runsDir)
                            .Select(d => Path.Combine(d, "regression-validate.json"))
                            .Where(File.Exists)
                            .Any(f => JsonDocument.Parse(File.ReadAllText(f)).RootElement
                                .GetProperty("verdict").GetString() == "PASS"),
                        "no PASS run artifact left on disk");
                }

                canonicalStamp = File.ReadAllText(canonical);

                // ── Case 6: harness abort is recorded, not swallowed ──
                {
                    var runId = ComposeRunId(new DateTime(2026, 7, 26, 14, 0, 0, DateTimeKind.Utc), "eeeeeee");
                    var unreached = new[] { new Cell("Stripe", CellPlatform.IosDevice) };
                    var result = WriteRegressionArtifacts(
                        version, runId, runsDir / runId, AllPlatforms, filter: null,
                        packages: new[] { "Kingfisher", "Stripe" }, cellsPlanned: 2,
                        unreached: unreached,
                        outcomes: new[]
                        {
                            Pass("Kingfisher", CellPlatform.IosSim, 30),
                            new CellOutcome(unreached[0], CellResult.Error, "no outcome recorded", TimeSpan.Zero),
                        },
                        abortReason: "Sim fleet exhausted",
                        totalDuration: TimeSpan.FromSeconds(45),
                        inputs: new { sdkVersion = version }, apiSurface: new { filesCollected = 0 });

                    using var doc = Read(result.RunArtifact);
                    var root = doc.RootElement;
                    Assert("verdict ABORTED on a harness abort",
                        root.GetProperty("verdict").GetString() == "ABORTED",
                        root.GetProperty("verdict").GetString() ?? "<null>");
                    Assert("exitCode 3 (abort) is recorded in the artifact",
                        root.GetProperty("exitCode").GetInt32() == RegressionExitAborted,
                        root.GetProperty("exitCode").GetInt32().ToString());
                    Assert("abort reason survives into the artifact",
                        root.GetProperty("abortReason").GetString() == "Sim fleet exhausted", "abortReason lost");
                    Assert("unreached cells make the run incomplete",
                        !result.Complete && result.IncompleteReasons.Any(r => r.Contains("no outcome")),
                        string.Join("; ", result.IncompleteReasons));
                    Assert("aborted run does not claim the canonical slot",
                        File.ReadAllText(canonical) == canonicalStamp,
                        "canonical artifact was overwritten by an aborted run");
                }

                // ── Case 7: the rendered matrix is a per-cell verdict table ──
                {
                    var report = RenderMatrix(
                        new[] { "Kingfisher", "Stripe" },
                        new[] { CellPlatform.IosSim, CellPlatform.MacOs },
                        new[]
                        {
                            Pass("Kingfisher", CellPlatform.IosSim, 30),
                            Fail("Stripe", CellPlatform.IosSim, "CS0246: StripeCore not found"),
                        },
                        TimeSpan.FromMinutes(2));

                    Assert("matrix names every package", report.Contains("Kingfisher") && report.Contains("Stripe"),
                        "a package row is missing");
                    Assert("matrix reports a verdict line", report.Contains("Verdict: 1 FAIL, 1 PASS"),
                        "verdict line missing or reshaped");
                    Assert("matrix names the failing cell and its reason",
                        report.Contains("Stripe ios-sim: CS0246: StripeCore not found"),
                        "failure detail missing");
                    Assert("matrix marks unrun cells rather than implying a pass",
                        report.Contains("—"), "no marker for cells with no outcome");
                }

                // ── Case 7b: a leg the host could not provide reds the run ──
                // A declared cell that never ran is not coverage. Left as a
                // non-failure it produced the worst possible artifact: verdict
                // PASS, exit 0, and a `complete=false` field nobody reads —
                // a release gate would take that as a full green matrix.
                {
                    var unavailable = new CellOutcome(
                        new Cell("Kingfisher", CellPlatform.TvOs),
                        CellResult.Unavailable,
                        "no tvOS simulator is available on this host",
                        TimeSpan.Zero);

                    Assert("an unavailable leg is a failure by default",
                        IsFailure(unavailable, allowUnavailable: false), "unavailable was treated as a pass");
                    Assert("--allow-unavailable is the only way to accept it",
                        !IsFailure(unavailable, allowUnavailable: true), "opt-out did not take effect");

                    // Skip and BuildOnly describe the matrix's own declared
                    // shape for a cell, not coverage the host failed to supply,
                    // so widening the failure set must not have swept them in.
                    foreach (var benign in new[] { CellResult.Skip, CellResult.BuildOnly, CellResult.Pass })
                    {
                        var o = new CellOutcome(new Cell("Kingfisher", CellPlatform.TvOs), benign, null, TimeSpan.Zero);
                        Assert($"{benign} stays a non-failure in both modes",
                            !IsFailure(o, allowUnavailable: false) && !IsFailure(o, allowUnavailable: true),
                            $"{benign} was counted as a failure");
                    }

                    var runId = ComposeRunId(new DateTime(2026, 9, 6, 11, 0, 0, DateTimeKind.Utc), "ccccccc");
                    var result = WriteRegressionArtifacts(
                        version: version, runId: runId, runDir: runsDir / runId,
                        platforms: AllPlatforms, filter: null,
                        packages: new[] { "Kingfisher" },
                        cellsPlanned: 2,
                        unreached: Array.Empty<Cell>(),
                        outcomes: new[] { Pass("Kingfisher", CellPlatform.IosSim, 30), unavailable },
                        abortReason: null,
                        totalDuration: TimeSpan.FromSeconds(30),
                        inputs: new { sdkVersion = version }, apiSurface: new { filesCollected = 0 });

                    using var doc = Read(result.RunArtifact);
                    var root = doc.RootElement;
                    Assert("verdict FAIL when a declared leg did not run",
                        root.GetProperty("verdict").GetString() == "FAIL",
                        root.GetProperty("verdict").GetString() ?? "<null>");
                    Assert("exitCode 2 rather than a silent 0",
                        root.GetProperty("exitCode").GetInt32() == RegressionExitCellsFailed,
                        root.GetProperty("exitCode").GetInt32().ToString());
                    Assert("the artifact records which way unavailable legs were counted",
                        root.TryGetProperty("allowUnavailable", out var allow) && !allow.GetBoolean(),
                        "allowUnavailable missing from the artifact");
                    Assert("the unavailable leg is still named with its reason",
                        root.GetProperty("unavailableCells")[0].GetProperty("platform").GetString() == "tvos",
                        "unavailableCells lost the leg");
                    Assert("an unavailable leg still leaves the run incomplete",
                        !result.Complete, "complete=true despite missing coverage");

                    var report = RenderMatrix(
                        new[] { "Kingfisher" },
                        new[] { CellPlatform.IosSim, CellPlatform.TvOs },
                        new[] { Pass("Kingfisher", CellPlatform.IosSim, 30), unavailable },
                        TimeSpan.FromSeconds(30));
                    Assert("the matrix says the gap was counted as a failure",
                        report.Contains("counted as failures") && report.Contains("--allow-unavailable"),
                        "matrix heading does not state how the gap was counted");
                    Assert("the matrix lists the unavailable leg under Failures",
                        report.Contains("UNAVAIL Kingfisher tvos"),
                        "unavailable leg absent from the failure list");
                }

                // ── Case 7c: a deferred credential cell passes the run but stays named ──
                // Deferral is the operator's explicit opt-out for live sections that need an
                // owner-held credential. The run may go green, but the artifact and the matrix must
                // still say which cells were not qualified and why.
                {
                    var deferred = new CellOutcome(
                        new Cell("StoreKit2", CellPlatform.IosSim),
                        CellResult.Deferred,
                        "CREDENTIAL QUALIFICATION DEFERRED — every check this cell could run passed",
                        TimeSpan.Zero);

                    var runId = ComposeRunId(new DateTime(2026, 9, 19, 11, 0, 0, DateTimeKind.Utc), "ddddddd");
                    var result = WriteRegressionArtifacts(
                        version: version, runId: runId, runDir: runsDir / runId,
                        platforms: AllPlatforms, filter: null,
                        packages: new[] { "StoreKit2" },
                        cellsPlanned: 1,
                        unreached: Array.Empty<Cell>(),
                        outcomes: new[] { deferred },
                        abortReason: null,
                        totalDuration: TimeSpan.FromSeconds(30),
                        inputs: new { sdkVersion = version }, apiSurface: new { filesCollected = 0 });

                    using var doc = Read(result.RunArtifact);
                    var root = doc.RootElement;
                    Assert("a deferred cell does not fail the run",
                        root.GetProperty("verdict").GetString() == "PASS",
                        root.GetProperty("verdict").GetString() ?? "<null>");
                    Assert("the artifact records the deferral opt-out",
                        root.TryGetProperty("deferCredentialQualification", out var deferFlag) && deferFlag.ValueKind is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False,
                        "deferCredentialQualification missing from the artifact");
                    Assert("the deferred cell is named with its reason",
                        root.GetProperty("counts").GetProperty("deferred").GetInt32() == 1
                        && root.GetProperty("deferredCells")[0].GetProperty("library").GetString() == "StoreKit2"
                        && root.GetProperty("deferredCells")[0].GetProperty("reason").GetString()!
                            .Contains("DEFERRED", StringComparison.Ordinal),
                        "deferredCells lost the cell or its reason");

                    var report = RenderMatrix(
                        new[] { "StoreKit2" }, new[] { CellPlatform.IosSim }, new[] { deferred },
                        TimeSpan.FromSeconds(30));
                    Assert("the matrix renders the cell as DEFERRED and lists it",
                        report.Contains("1 DEFERRED") && report.Contains("StoreKit2 ios-sim: CREDENTIAL QUALIFICATION DEFERRED"),
                        "matrix does not surface the deferred cell");
                }

                // ── Case 8: run ids are unique per run and carry the tree state ──
                {
                    var startA = new DateTime(2026, 7, 26, 10, 0, 0, DateTimeKind.Utc);
                    var a = ComposeRunId(startA, "aaaaaaa");
                    var b = ComposeRunId(new DateTime(2026, 7, 26, 10, 0, 1, DateTimeKind.Utc), "aaaaaaa");
                    Assert("run id changes with start time", a != b, $"{a} == {b}");
                    Assert("run id carries the sha", a.EndsWith("-aaaaaaa"), a);
                    Assert("run id degrades cleanly with no git", ComposeRunId(DateTime.UtcNow, null).EndsWith("-nogit"),
                        ComposeRunId(DateTime.UtcNow, null));
                    Assert("run ids sort chronologically as strings",
                        string.CompareOrdinal(a, b) < 0, $"{a} !< {b}");

                    // Two runs launched inside the same second on the same commit
                    // must not share a directory — they would interleave cell logs
                    // and one would overwrite the other's verdict.
                    var sameSecondA = ComposeRunId(new DateTime(2026, 7, 26, 10, 0, 0, 100, DateTimeKind.Utc), "aaaaaaa");
                    var sameSecondB = ComposeRunId(new DateTime(2026, 7, 26, 10, 0, 0, 900, DateTimeKind.Utc), "aaaaaaa");
                    Assert("same-second runs differ by the millisecond component",
                        sameSecondA != sameSecondB, $"{sameSecondA} == {sameSecondB}");
                    Assert("same-instant runs in different processes still differ",
                        ComposeRunId(startA, "aaaaaaa", processId: 111)
                            != ComposeRunId(startA, "aaaaaaa", processId: 222),
                        "pid does not disambiguate concurrent runs");
                    Assert("millisecond component preserves chronological sorting",
                        string.CompareOrdinal(sameSecondA, sameSecondB) < 0,
                        $"{sameSecondA} !< {sameSecondB}");
                }

                // ── Case 8b: every abort path is reason-coded, not Nuke's blanket 255 ──
                // Pre-flight throws (missing SDK nupkg, incoherent cross-framework
                // deps) escape before the matrix ever runs; before this they exited
                // 255, indistinguishable from an ordinary build failure.
                {
                    RegressionExitCodeOverride = null;
                    var rethrew = false;
                    try
                    {
                        await WithAbortExitCode(() =>
                            throw new InvalidOperationException("simulated pre-flight failure"));
                    }
                    catch (InvalidOperationException)
                    {
                        rethrew = true;
                    }
                    Assert("an abort with no reason code becomes exit 3",
                        RegressionExitCodeOverride == RegressionExitAborted,
                        $"override = {RegressionExitCodeOverride?.ToString() ?? "null"}");
                    Assert("the original failure still propagates", rethrew, "exception was swallowed");

                    RegressionExitCodeOverride = RegressionExitCellsFailed;
                    try
                    {
                        await WithAbortExitCode(() =>
                            throw new InvalidOperationException("cells failed"));
                    }
                    catch (InvalidOperationException) { }
                    Assert("a deliberate cell-failure code is not overwritten by the abort guard",
                        RegressionExitCodeOverride == RegressionExitCellsFailed,
                        $"override = {RegressionExitCodeOverride?.ToString() ?? "null"}");

                    RegressionExitCodeOverride = null;
                    await WithAbortExitCode(() => Task.CompletedTask);
                    Assert("a clean body leaves no reason code behind",
                        RegressionExitCodeOverride is null,
                        $"override = {RegressionExitCodeOverride?.ToString() ?? "null"}");
                }

                // ── Case 9: exit-code mapping never masks success or invents failure ──
                {
                    Assert("a green run stays 0 even if a reason code lingers",
                        ResolveExitCode(0, RegressionExitCellsFailed) == 0, "green run was reddened");
                    Assert("cell failures surface as exit 2",
                        ResolveExitCode(255, RegressionExitCellsFailed) == RegressionExitCellsFailed,
                        "reason code not applied");
                    Assert("harness abort surfaces as exit 3",
                        ResolveExitCode(255, RegressionExitAborted) == RegressionExitAborted,
                        "reason code not applied");
                    Assert("a failure with no reason code keeps Nuke's code",
                        ResolveExitCode(255, null) == 255, "255 was rewritten");
                }

                // ── Case 10: the inputs block pins what the run actually validated ──
                {
                    var inputs = BuildInputsBlock("0.18.0", "26.2.8", "26.2.10", new[] { "Kingfisher", "Matter" });
                    using var doc = JsonDocument.Parse(JsonSerializer.Serialize(inputs));
                    var root = doc.RootElement;
                    Assert("inputs record all three version trains",
                        root.GetProperty("sdkVersion").GetString() == "0.18.0"
                        && root.GetProperty("appleVersion").GetString() == "26.2.8"
                        && root.GetProperty("appleBindingsVersion").GetString() == "26.2.10",
                        "version trains missing");
                    Assert("inputs record the Xcode in use",
                        root.GetProperty("xcode").ValueKind == JsonValueKind.String
                        && root.GetProperty("xcode").GetString()!.Contains("Xcode"),
                        $"xcode = {root.GetProperty("xcode")}");
                    Assert("inputs pin each third-party library's own version",
                        root.GetProperty("libraries").GetProperty("Kingfisher").GetProperty("version").GetString()
                            is { Length: > 0 },
                        "Kingfisher version not pinned");
                    // The produced train, not the core supplement's: 26.2.10 is the
                    // version this run stamped into Matter's own <Version> and packed.
                    Assert("apple-framework packages ride the per-framework bindings train",
                        root.GetProperty("libraries").GetProperty("Matter").GetProperty("kind").GetString()
                            == "apple-framework"
                        && root.GetProperty("libraries").GetProperty("Matter").GetProperty("version").GetString()
                            == "26.2.10",
                        "apple-framework input not attributed");
                }

                // ── Case 11 (F3 scaffolding): api-surface snapshots collect, never gate ──
                {
                    var snapshotDir = runsDir / "api-surface-selftest";
                    var surface = CollectApiSurfaceSnapshots(new[] { "Kingfisher", "Nuke", "Mappedin" }, snapshotDir);
                    using var doc = JsonDocument.Parse(JsonSerializer.Serialize(surface));
                    var root = doc.RootElement;
                    Assert("api-surface collection is reported, not gated",
                        !root.GetProperty("gateEnabled").GetBoolean(), "F3 gate silently enabled");
                    Assert("api-surface collection tolerates a missing baseline",
                        !root.GetProperty("baselinePresent").GetBoolean()
                        || root.GetProperty("drift").GetArrayLength() >= 0,
                        "baseline handling broke");
                    // Only assert file collection when the repo actually has emitted
                    // snapshots — a clean checkout legitimately has none.
                    var emitted = root.GetProperty("filesCollected").GetInt32();
                    Assert("collected snapshots land under the run directory",
                        emitted == 0 || Directory.Exists(snapshotDir),
                        $"{emitted} file(s) reported but {snapshotDir} missing");
                    Log.Information("    (api-surface snapshots collected: {Count})", emitted);
                }

                // ── Case 12: pre-flight evicts the cached extracts of the packages under test ──
                // A regression run re-packs the same version with different content, so a
                // stale extract keyed on id+version would shadow it and the matrix would
                // report a verdict for the previous build. Runs against a throwaway cache
                // via NUGET_PACKAGES — never the developer's real ~/.nuget/packages.
                {
                    var fakeCache = runsDir / "nuget-cache-selftest";
                    var priorEnv = Environment.GetEnvironmentVariable("NUGET_PACKAGES");
                    try
                    {
                        var sdkEntry = fakeCache / SdkPackageId.ToLowerInvariant() / version;
                        var appleEntry = fakeCache / ApplePackageId.ToLowerInvariant() / "26.2.8";
                        var otherVersion = fakeCache / SdkPackageId.ToLowerInvariant() / "0.17.0";
                        var unrelated = fakeCache / "newtonsoft.json" / "13.0.3";
                        foreach (var d in new[] { sdkEntry, appleEntry, otherVersion, unrelated })
                        {
                            Directory.CreateDirectory(d);
                            File.WriteAllText(d / "marker.txt", "stale");
                        }
                        Environment.SetEnvironmentVariable("NUGET_PACKAGES", fakeCache);

                        var evicted = EvictGlobalCacheEntries(
                            (SdkPackageId, version),
                            (RuntimePackageId, version),   // never created — absence must be tolerated
                            (ApplePackageId, "26.2.8"));

                        Assert("the SDK extract under test is evicted",
                            !Directory.Exists(sdkEntry), $"{sdkEntry} survived");
                        Assert("the Apple extract under test is evicted",
                            !Directory.Exists(appleEntry), $"{appleEntry} survived");
                        Assert("a missing cache entry is tolerated, not an error",
                            evicted.Count == 2, $"evicted {evicted.Count}, expected 2");
                        Assert("other versions of the same package are left alone",
                            Directory.Exists(otherVersion), $"{otherVersion} was collateral damage");
                        Assert("unrelated packages are left alone",
                            Directory.Exists(unrelated), $"{unrelated} was collateral damage");
                    }
                    finally
                    {
                        Environment.SetEnvironmentVariable("NUGET_PACKAGES", priorEnv);
                    }
                }
            }
            finally
            {
                CleanSelfTestArtifacts(version);
            }

            // ── Pairings and engines ──
            {
                // Facts as the probe bundles of each workload actually carry them (2026-11-26):
                // .NET 10 Catalyst links Mono statically (no dylib), .NET 11 device turns
                // libcoreclr into a framework, Debug leaves the app assembly out of the R2R image.
                static SwiftBindings.Build.Models.AppEngine? C(bool? monoCoreLib, bool monoLib, bool clrLib, bool? appR2R) =>
                    SwiftBindings.Build.Models.EngineEvidence.Classify(
                        new SwiftBindings.Build.Models.EngineEvidence.Facts(monoCoreLib, monoLib, clrLib, appR2R)).Engine;
                const SwiftBindings.Build.Models.AppEngine Mono = SwiftBindings.Build.Models.AppEngine.Mono,
                    Interp = SwiftBindings.Build.Models.AppEngine.CoreClrInterpreter,
                    R2R = SwiftBindings.Build.Models.AppEngine.CoreClrReadyToRun;
                Assert("engine: Mono CoreLib + libmonosgen (net10 sim) is Mono", C(true, true, false, false) == Mono, "not Mono");
                Assert("engine: Mono CoreLib, statically linked (net10 Catalyst) is Mono", C(true, false, false, false) == Mono, "not Mono");
                Assert("engine: CoreCLR + IL-only app (net11 Debug) is the interpreter", C(false, false, true, false) == Interp, "not interpreter");
                Assert("engine: CoreCLR + ReadyToRun app (net11 Release) is ReadyToRun", C(false, false, true, true) == R2R, "not R2R");
                Assert("engine: a CoreCLR CoreLib without libcoreclr proves nothing", C(false, false, false, true) is null, "classified");
                Assert("engine: a CoreCLR CoreLib next to libmonosgen proves nothing", C(false, true, true, false) is null, "classified");
                Assert("engine: Mono CoreLib next to libcoreclr proves nothing", C(true, false, true, false) is null, "classified");
                Assert("engine: CoreCLR without the app assembly proves nothing", C(false, false, true, null) is null, "classified");
                Assert("engine: a bundle with no CoreLib proves nothing", C(null, false, false, null) is null, "classified");
                Assert("engine: an IL binding next to a ReadyToRun app proves neither CoreCLR engine",
                    SwiftBindings.Build.Models.EngineEvidence.Classify(
                        new SwiftBindings.Build.Models.EngineEvidence.Facts(false, false, true, true, MixedReadyToRun: true)).Engine is null,
                    "classified");

                var all = SwiftBindings.Build.Models.ToolchainPairing.All;
                string[] n10 = { "mono" }, n11 = { "coreclr-interpreter", "coreclr-r2r" };
                var green = JudgePairingsForTest(all, new[]
                {
                    ("net10-xcode26", "PASS", true, n10, n10),
                    ("net10-xcode27", "PASS", true, n10, n10),
                    ("net11-xcode27", "PASS", true, n11, n11),
                });
                Assert("pairings: all three green and complete is a complete PASS",
                    green.Verdict == "PASS" && green.Complete, $"{green.Verdict} complete={green.Complete} {string.Join("; ", green.Reasons)}");
                var oneMissing = JudgePairingsForTest(new[] { "net10-xcode26", "net11-xcode27" }, new[]
                {
                    ("net10-xcode26", "PASS", true, n10, n10),
                    ("net11-xcode27", "PASS", true, n11, n11),
                });
                Assert("pairings: a pairing left out makes the run incomplete",
                    oneMissing.Verdict == "PASS" && !oneMissing.Complete
                    && oneMissing.Reasons.Any(r => r.Contains("net10-xcode27")), string.Join("; ", oneMissing.Reasons));
                var noR2r = JudgePairingsForTest(all, new[]
                {
                    ("net10-xcode26", "PASS", true, n10, n10),
                    ("net10-xcode27", "PASS", true, n10, n10),
                    ("net11-xcode27", "PASS", true, n11, new[] { "coreclr-interpreter" }),
                });
                Assert("pairings: an engine no passing cell proved fails the run and still claims full coverage",
                    noR2r.Verdict == "FAIL" && noR2r.Complete, $"{noR2r.Verdict} complete={noR2r.Complete}");
                var red = JudgePairingsForTest(all, new[]
                {
                    ("net10-xcode26", "PASS", true, n10, n10),
                    ("net10-xcode27", "FAIL", true, n10, n10),
                    ("net11-xcode27", "PASS", true, n11, n11),
                });
                Assert("pairings: one red pairing fails the run; full coverage still counts as complete",
                    red.Verdict == "FAIL" && red.Complete, $"{red.Verdict} complete={red.Complete}");
                Assert("pairings: secret parameter values are masked in logged child command lines",
                    string.Join(' ', RedactSecrets(new[] { "RegressionValidate", "--nuget-api-key", "abc", "--version", "1.0.0", "--github-token=xyz" }))
                        == "RegressionValidate --nuget-api-key *** --version 1.0.0 --github-token=***",
                    string.Join(' ', RedactSecrets(new[] { "RegressionValidate", "--nuget-api-key", "abc", "--version", "1.0.0", "--github-token=xyz" })));
                Assert("native inputs: an interface written by Swift 6.4 is newer than an Xcode 26.3 compiler",
                    ParseSwiftVersion("// swift-compiler-version: Apple Swift version 6.4.2 (swiftlang-6.4.2.1.9 clang-2100.4.1.1)", "Apple Swift version ")
                        > ParseSwiftVersion("Apple Swift version 6.2.4 (swiftlang-6.2.4.1.4 clang-1700.6.4.2)\nTarget: arm64-apple-macosx26.0", "Apple Swift version ")
                    && ParseSwiftVersion("no version here", "Apple Swift version ") is null,
                    "comparison wrong");
                Assert("pairings: a secret parameter spelled with extra hyphens (as Nuke matches it) is masked",
                    string.Join(' ', RedactSecrets(new[] { "--nuget-api-k-ey", "abc", "--NugetApiKey=xyz" })) == "--nuget-api-k-ey *** --NugetApiKey=***",
                    string.Join(' ', RedactSecrets(new[] { "--nuget-api-k-ey", "abc", "--NugetApiKey=xyz" })));
                Assert("pairings: target and pairing are recognized however Nuke spells them",
                    OptionName("--tar-get") == "target" && OptionName("-Target=X") == "target" && OptionName("--pair-ing") == "pairing"
                    && OptionName("value") == "", "OptionName");
                Assert("pairings: a single-dash secret parameter is masked too",
                    string.Join(' ', RedactSecrets(new[] { "-nuget-api-key", "abc" })) == "-nuget-api-key ***",
                    string.Join(' ', RedactSecrets(new[] { "-nuget-api-key", "abc" })));
                var aborted = JudgePairingsForTest(all, new[]
                {
                    ("net10-xcode26", "PASS", true, n10, n10),
                    ("net10-xcode27", "ABORTED", false, n10, Array.Empty<string>()),
                    ("net11-xcode27", "FAIL", true, n11, n11),
                });
                Assert("pairings: an aborted pairing aborts the run", aborted.Verdict == "ABORTED", aborted.Verdict);

                var dir = RootDirectory / "toolchains";
                foreach (var name in all)
                {
                    var tp = SwiftBindings.Build.Models.ToolchainPairing.Load(dir, name);
                    Assert($"pairing record {name} loads", tp.Name == name, tp.Name);
                }
                Assert("pairing build TFMs and package copy labels match Directory.Build.props",
                    SwiftBindings.Build.Models.ToolchainPairing.Load(dir, "net10-xcode26").Tfm("ios") == "net10.0-ios"
                    && SwiftBindings.Build.Models.ToolchainPairing.Load(dir, "net10-xcode26").CopyTfm("ios") == "net10.0-ios26.0"
                    && SwiftBindings.Build.Models.ToolchainPairing.Load(dir, "net11-xcode27").CopyTfm("tvos") == "net11.0-tvos27.2"
                    && SwiftBindings.Build.Models.ToolchainPairing.Load(dir, "net10-xcode27").Tfm("tvos") == "net10.0-tvos27.2"
                    && SwiftBindings.Build.Models.ToolchainPairing.Load(dir, "net11-xcode27").Tfm("maccatalyst") == "net11.0-maccatalyst27.2",
                    "labels differ");
                Assert("an unknown pairing is refused",
                    Throws(() => SwiftBindings.Build.Models.ToolchainPairing.Load(dir, "net12-xcode28")), "loaded");
                Assert("a pairing whose engines contradict its framework is refused",
                    Throws(() => SwiftBindings.Build.Models.ToolchainPairing.Parse(
                        File.ReadAllText(dir / "net11-xcode27.json").Replace("\"coreclr-interpreter\", \"coreclr-r2r\"", "\"mono\""),
                        "net11-xcode27", "test")), "accepted");

                // The merge restore runs in this process, outside use-toolchain.sh: it must still use
                // the private per-pairing cache, never the global ~/.nuget/packages.
                var cacheRoot = Path.Combine(Path.GetTempPath(), $"sb-merge-env-{Guid.NewGuid():N}");
                try
                {
                    Func<string, string?> Env(params (string Key, string Value)[] vars) =>
                        k => vars.FirstOrDefault(v => v.Key == k).Value;
                    var unpaired = MergeRestoreEnvironment(Env(("SWIFTBINDINGS_NUGET_PACKAGES_ROOT", cacheRoot)));
                    Assert("merge restore: the private root maps onto the default pairing's cache",
                        unpaired.TryGetValue("NUGET_PACKAGES", out var c1) && c1 == Path.Combine(cacheRoot, "net10-xcode26")
                        && Directory.Exists(c1), string.Join(";", unpaired));
                    var paired = MergeRestoreEnvironment(Env(("SWIFTBINDINGS_NUGET_PACKAGES_ROOT", cacheRoot),
                        ("SwiftBindingsPairing", "net11-xcode27")));
                    Assert("merge restore: a selected pairing gets its own cache",
                        paired.TryGetValue("NUGET_PACKAGES", out var c2) && c2 == Path.Combine(cacheRoot, "net11-xcode27"),
                        string.Join(";", paired));
                    Assert("merge restore: an explicit NUGET_PACKAGES is left alone",
                        MergeRestoreEnvironment(Env(("SWIFTBINDINGS_NUGET_PACKAGES_ROOT", cacheRoot),
                            ("NUGET_PACKAGES", "/x"))).Count == 0, "overridden");
                    Assert("merge restore: no private root, no override",
                        MergeRestoreEnvironment(Env()).Count == 0, "overridden");
                }
                finally
                {
                    if (Directory.Exists(cacheRoot)) Directory.Delete(cacheRoot, recursive: true);
                }

                static bool Throws(Action a) { try { a(); return false; } catch (InvalidOperationException) { return true; } }
            }

            if (failures.Count > 0)
                throw new InvalidOperationException(
                    $"TestRegressionArtifacts: {failures.Count} assertion(s) failed:\n" + string.Join("\n", failures));

            Log.Information("TestRegressionArtifacts: all assertions passed");
        });

    void CleanSelfTestArtifacts(string version)
    {
        var artifactDir = RootDirectory / "artifacts";
        if (Directory.Exists(artifactDir))
            foreach (var f in Directory.EnumerateFiles(artifactDir, $"regression-validate-{version}*.json"))
                File.Delete(f);
        var runsDir = RegressionRunsDir(version);
        if (Directory.Exists(runsDir)) Directory.Delete(runsDir, recursive: true);
    }
}
