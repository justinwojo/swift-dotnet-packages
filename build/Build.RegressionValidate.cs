using System.Diagnostics;
using System.Text.Json;
using Nuke.Common;
using Nuke.Common.IO;
using Serilog;
using SwiftBindings.Build.Helpers;
using SwiftBindings.Build.Models;

partial class Build
{
    [Parameter("RegressionValidate: subset of platforms to run (comma list, default: all). Values: ios-sim, ios-device, macos, maccatalyst, tvos")]
    readonly string? Platforms;

    /// <summary>
    /// Pre-release regression: build + validate every in-scope package across
    /// every TFM its test csproj declares, sequentially. iOS sim runs MonoJIT,
    /// iOS device runs NativeAOT publish. macOS validates via direct binary
    /// run. MacCatalyst and tvOS build only — no validator yet, recorded as
    /// <c>BUILD-ONLY</c>.
    /// </summary>
    Target RegressionValidate => _ => _
        .Description("Build + validate every package across every declared TFM (--version X.Y.Z [--filter Foo] [--platforms ios-sim,macos])")
        .Requires(() => Version)
        .Executes(() =>
        {
            var version = Version!;
            var requestedPlatforms = ParsePlatformsParam(Platforms);
            var packages = ResolvePackages(Filter);

            Log.Information("=== regression-validate {Version} ===", version);
            Log.Information("Packages: {Count} ({Names})", packages.Count, string.Join(", ", packages));
            Log.Information("Platforms: {Platforms}", string.Join(", ", requestedPlatforms.Select(PlatformLabel)));

            var cells = EnumerateCells(packages, requestedPlatforms);
            Log.Information("Cells to run: {Count}", cells.Count);

            Preflight(version, cells);

            // Cache resolved device UDID once so each device cell doesn't re-detect.
            string? deviceUdid = cells.Any(c => c.Platform == CellPlatform.IosDevice)
                ? (DeviceUdid ?? DevicectlClient.AutoDetectDevice())
                : null;

            var outcomes = new List<CellOutcome>();
            var pipelineStart = DateTime.UtcNow;

            // Group by library so each package bundles its TFMs into one
            // Debug build (and one NativeAOT publish) instead of one build
            // per cell. For multi-TFM apple-frameworks this collapses up to
            // four redundant `dotnet build` invocations into one.
            var packageGroups = cells
                .GroupBy(c => c.Library, StringComparer.Ordinal)
                .OrderBy(g => g.Key, StringComparer.Ordinal)
                .ToList();

            for (var gi = 0; gi < packageGroups.Count; gi++)
            {
                var group = packageGroups[gi];
                var packageCells = group.ToList();
                Log.Information("");
                Log.Information("─── [{Idx}/{Total}] {Library} ({Cells} cell(s)) ───",
                    gi + 1, packageGroups.Count, group.Key, packageCells.Count);

                RunPackageGroup(group.Key, packageCells, deviceUdid, outcomes);
            }

            var totalDuration = DateTime.UtcNow - pipelineStart;
            PrintMatrix(packages, requestedPlatforms, outcomes, totalDuration);
            WriteJsonArtifact(version, requestedPlatforms, outcomes, totalDuration);

            var failures = outcomes.Count(o => o.Result is CellResult.Fail or CellResult.Timeout or CellResult.Crashed or CellResult.Error);
            if (failures > 0)
                throw new InvalidOperationException($"regression-validate: {failures} cell(s) failed");
        });

    [Parameter("RegressionValidate: single package filter (parity with --library)")]
    readonly string? Filter;

    enum CellPlatform { IosSim, IosDevice, MacOs, MacCatalyst, TvOs }

    enum CellResult { Pass, Fail, BuildOnly, Skip, Timeout, Crashed, Error }

    sealed record Cell(string Library, CellPlatform Platform);

    sealed record CellOutcome(
        Cell Cell,
        CellResult Result,
        string? Reason,
        TimeSpan Duration);

    static readonly CellPlatform[] AllPlatforms =
    {
        CellPlatform.IosSim,
        CellPlatform.IosDevice,
        CellPlatform.MacOs,
        CellPlatform.MacCatalyst,
        CellPlatform.TvOs,
    };

    static string PlatformLabel(CellPlatform p) => p switch
    {
        CellPlatform.IosSim => "ios-sim",
        CellPlatform.IosDevice => "ios-device",
        CellPlatform.MacOs => "macos",
        CellPlatform.MacCatalyst => "maccatalyst",
        CellPlatform.TvOs => "tvos",
        _ => p.ToString(),
    };

    static string PlatformTfm(CellPlatform p) => p switch
    {
        CellPlatform.IosSim or CellPlatform.IosDevice => "net10.0-ios",
        CellPlatform.MacOs => "net10.0-macos",
        CellPlatform.MacCatalyst => "net10.0-maccatalyst",
        CellPlatform.TvOs => "net10.0-tvos",
        _ => throw new ArgumentOutOfRangeException(nameof(p)),
    };

    static IReadOnlyList<CellPlatform> ParsePlatformsParam(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return AllPlatforms;
        var result = new List<CellPlatform>();
        foreach (var token in raw.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var t = token.Trim().ToLowerInvariant();
            CellPlatform p = t switch
            {
                "ios-sim" or "ios" or "iossim" => CellPlatform.IosSim,
                "ios-device" or "device" or "iosdevice" => CellPlatform.IosDevice,
                "macos" or "mac" => CellPlatform.MacOs,
                "maccatalyst" or "catalyst" => CellPlatform.MacCatalyst,
                "tvos" => CellPlatform.TvOs,
                _ => throw new InvalidOperationException(
                    $"Unknown platform '{token}'. Valid: ios-sim, ios-device, macos, maccatalyst, tvos"),
            };
            if (!result.Contains(p)) result.Add(p);
        }
        return result;
    }

    IReadOnlyList<string> ResolvePackages(string? filter)
    {
        if (!string.IsNullOrWhiteSpace(filter))
            return new[] { filter };

        var packages = new List<string>();
        if (Directory.Exists(LibrariesDir))
            foreach (var d in Directory.EnumerateDirectories(LibrariesDir))
                if (Directory.Exists(Path.Combine(d, "tests")))
                    packages.Add(Path.GetFileName(d)!);
        if (Directory.Exists(AppleFrameworksDir))
            foreach (var d in Directory.EnumerateDirectories(AppleFrameworksDir))
                if (Directory.Exists(Path.Combine(d, "tests")))
                    packages.Add(Path.GetFileName(d)!);

        packages.Sort(StringComparer.Ordinal);
        return packages;
    }

    IReadOnlyList<Cell> EnumerateCells(
        IReadOnlyList<string> packages,
        IReadOnlyList<CellPlatform> platforms)
    {
        var cells = new List<Cell>();
        foreach (var lib in packages)
        {
            var testDir = TestDir(lib);
            if (!Directory.Exists(testDir))
            {
                Log.Warning("Skipping {Library}: no tests/ directory", lib);
                continue;
            }
            var declared = ParseTestProjectTfms(testDir);
            foreach (var platform in platforms)
            {
                var tfm = PlatformTfm(platform);
                if (declared.Contains(tfm))
                    cells.Add(new Cell(lib, platform));
            }
        }
        return cells;
    }

    void Preflight(string version, IReadOnlyList<Cell> cells)
    {
        Log.Information("--- pre-flight ---");

        // 1. SDK nupkg present
        var nupkg = LocalPackagesDir / $"SwiftBindings.Sdk.{version}.nupkg";
        if (!File.Exists(nupkg))
            throw new InvalidOperationException(
                $"Pre-flight: missing SDK nupkg at {nupkg}\n" +
                $"Drop SwiftBindings.Sdk.{version}.nupkg into local-packages/ before invoking.");
        Log.Information("  SDK nupkg: {Path}", nupkg);

        var needsSim = cells.Any(c => c.Platform == CellPlatform.IosSim);
        var needsDevice = cells.Any(c => c.Platform == CellPlatform.IosDevice);

        // 2. Booted simulator if any sim cells
        if (needsSim)
        {
            var booted = ListBootedSims();
            if (booted.Count == 0)
                throw new InvalidOperationException(
                    "Pre-flight: no booted iOS simulator. Run `dotnet nuke BootSim` first.");
            Log.Information("  Booted simulator(s): {Count}", booted.Count);
        }

        // 3. Connected device if any device cells
        if (needsDevice)
        {
            var udid = DeviceUdid ?? DevicectlClient.AutoDetectDevice();
            if (string.IsNullOrEmpty(udid))
                throw new InvalidOperationException(
                    "Pre-flight: no connected iPhone. Connect a device or pass --device-udid X.");
            Log.Information("  Device UDID: {Udid}", udid);

            // 4. Codesign config (env vars OR Directory.Build.tests.props.local)
            var hasEnv = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CODESIGN_IDENTITY"))
                && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PROVISIONING_PROFILE"))
                && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TEAM_ID"));
            var hasLocal = File.Exists(RootDirectory / "Directory.Build.tests.props.local");
            if (!hasEnv && !hasLocal)
                throw new InvalidOperationException(
                    "Pre-flight: NativeAOT device builds need codesign config.\n" +
                    "  Either set CODESIGN_IDENTITY + PROVISIONING_PROFILE + TEAM_ID,\n" +
                    "  or create Directory.Build.tests.props.local at the repo root.");
            Log.Information("  Codesign: {Source}", hasEnv ? "env vars" : "Directory.Build.tests.props.local");
        }

        Log.Information("--- pre-flight OK ---");
    }

    static IReadOnlyList<string> ListBootedSims()
    {
        var psi = new ProcessStartInfo("xcrun")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("simctl");
        psi.ArgumentList.Add("list");
        psi.ArgumentList.Add("devices");
        psi.ArgumentList.Add("booted");
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        var booted = new List<string>();
        foreach (var line in stdout.Split('\n'))
        {
            var m = System.Text.RegularExpressions.Regex.Match(line, @"\(([0-9A-F-]{36})\) \(Booted\)");
            if (m.Success) booted.Add(m.Groups[1].Value);
        }
        return booted;
    }

    /// <summary>
    /// Run all cells for a single library. Each cell does its own
    /// <c>dotnet build</c> (per-TFM) so build failures attribute precisely
    /// to the cell that broke. NativeAOT publish runs once per package since
    /// there's only ever one ios-device cell.
    /// </summary>
    void RunPackageGroup(
        string library,
        IReadOnlyList<Cell> packageCells,
        string? deviceUdid,
        List<CellOutcome> outcomes)
    {
        foreach (var cell in packageCells)
        {
            var cellSw = Stopwatch.StartNew();
            CellOutcome outcome;
            try
            {
                outcome = RunCell(cell, deviceUdid, cellSw);
            }
            catch (Exception ex)
            {
                outcome = new CellOutcome(cell, CellResult.Error, ex.Message, cellSw.Elapsed);
            }
            outcomes.Add(outcome);
            Log.Information("  [{Library} {Platform}] → {Result} ({Duration:F1}s){Reason}",
                library, PlatformLabel(cell.Platform), outcome.Result, outcome.Duration.TotalSeconds,
                outcome.Reason is null ? "" : $" — {outcome.Reason}");
        }
    }

    /// <summary>
    /// Build + validate a single cell. Each platform dispatches to its
    /// matching build helper and validator (or build-only for catalyst/tvos).
    /// </summary>
    CellOutcome RunCell(Cell cell, string? deviceUdid, Stopwatch sw)
    {
        var (testDir, _, _) = ResolveTestNames(cell.Library);
        switch (cell.Platform)
        {
            case CellPlatform.IosSim:
            {
                BuildTestAppSimulator(cell.Library, testDir, "ios");
                var result = ValidateSimFor(cell.Library, Timeout, DeviceUdid, "iossimulator-arm64");
                return ToOutcome(cell, result, sw.Elapsed);
            }
            case CellPlatform.IosDevice:
            {
                BuildTestAppNativeAot(cell.Library, testDir);
                var result = ValidateDeviceFor(cell.Library, Timeout, deviceUdid, aot: true, "ios-arm64");
                return ToOutcome(cell, result, sw.Elapsed);
            }
            case CellPlatform.MacOs:
            {
                BuildTestAppSimulator(cell.Library, testDir, "macos");
                var result = ValidateMacFor(cell.Library, Timeout, "osx-arm64");
                return ToOutcome(cell, result, sw.Elapsed);
            }
            case CellPlatform.MacCatalyst:
            {
                BuildTestAppSimulator(cell.Library, testDir, "maccatalyst");
                var result = ValidateMacCatalystFor(cell.Library, Timeout, "maccatalyst-arm64");
                return ToOutcome(cell, result, sw.Elapsed);
            }
            case CellPlatform.TvOs:
            {
                BuildTestAppSimulator(cell.Library, testDir, "tvos");
                return new CellOutcome(cell, CellResult.BuildOnly, "no tvos validator yet", sw.Elapsed);
            }
            default:
                return new CellOutcome(cell, CellResult.Error, $"unhandled platform {cell.Platform}", sw.Elapsed);
        }
    }

    static CellOutcome ToOutcome(Cell cell, TestRunResult result, TimeSpan duration) =>
        result.Status switch
        {
            TestRunStatus.Success => new CellOutcome(cell, CellResult.Pass, null, duration),
            TestRunStatus.Failed => new CellOutcome(cell, CellResult.Fail, result.Reason, duration),
            TestRunStatus.Crashed => new CellOutcome(cell, CellResult.Crashed, result.Reason, duration),
            TestRunStatus.Timeout => new CellOutcome(cell, CellResult.Timeout, result.Reason, duration),
            TestRunStatus.Exited => new CellOutcome(cell, CellResult.Fail, result.Reason ?? "app exited without TEST SUCCESS", duration),
            _ => new CellOutcome(cell, CellResult.Error, $"unexpected status {result.Status}", duration),
        };

    static string CellLabel(CellResult r) => r switch
    {
        CellResult.Pass => "PASS",
        CellResult.Fail => "FAIL",
        CellResult.BuildOnly => "BUILD-ONLY",
        CellResult.Skip => "SKIP",
        CellResult.Timeout => "TIMEOUT",
        CellResult.Crashed => "CRASH",
        CellResult.Error => "ERROR",
        _ => r.ToString(),
    };

    void PrintMatrix(
        IReadOnlyList<string> packages,
        IReadOnlyList<CellPlatform> platforms,
        IReadOnlyList<CellOutcome> outcomes,
        TimeSpan totalDuration)
    {
        // Index outcomes by (library, platform) for fast lookup.
        var byCell = outcomes.ToDictionary(o => (o.Cell.Library, o.Cell.Platform));

        var libWidth = Math.Max(20, packages.Max(p => p.Length) + 2);
        var colWidths = platforms.ToDictionary(p => p, p => Math.Max(PlatformLabel(p).Length, "BUILD-ONLY".Length) + 2);

        var sb = new System.Text.StringBuilder();
        sb.AppendLine();
        sb.AppendLine($"=== regression-validate ({totalDuration.TotalMinutes:F1} min) ===");
        sb.AppendLine();

        // Header
        sb.Append(new string(' ', libWidth));
        foreach (var p in platforms)
            sb.Append(PlatformLabel(p).PadRight(colWidths[p]));
        sb.AppendLine();

        sb.Append(new string('─', libWidth));
        foreach (var p in platforms)
            sb.Append(new string('─', colWidths[p]));
        sb.AppendLine();

        foreach (var lib in packages)
        {
            sb.Append(lib.PadRight(libWidth));
            foreach (var p in platforms)
            {
                var label = byCell.TryGetValue((lib, p), out var o) ? CellLabel(o.Result) : "—";
                sb.Append(label.PadRight(colWidths[p]));
            }
            sb.AppendLine();
        }
        sb.AppendLine();

        // Verdict
        var counts = outcomes.GroupBy(o => o.Result).ToDictionary(g => g.Key, g => g.Count());
        var pass = counts.GetValueOrDefault(CellResult.Pass);
        var fail = counts.GetValueOrDefault(CellResult.Fail)
                 + counts.GetValueOrDefault(CellResult.Timeout)
                 + counts.GetValueOrDefault(CellResult.Crashed)
                 + counts.GetValueOrDefault(CellResult.Error);
        var buildOnly = counts.GetValueOrDefault(CellResult.BuildOnly);
        var skip = counts.GetValueOrDefault(CellResult.Skip);
        sb.AppendLine($"Verdict: {fail} FAIL, {pass} PASS, {buildOnly} BUILD-ONLY, {skip} SKIP");

        if (fail > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Failures:");
            foreach (var o in outcomes.Where(o => o.Result is CellResult.Fail or CellResult.Timeout or CellResult.Crashed or CellResult.Error))
                sb.AppendLine($"  {o.Cell.Library} {PlatformLabel(o.Cell.Platform)}: {o.Reason}");
        }

        Console.WriteLine(sb.ToString());
    }

    void WriteJsonArtifact(
        string version,
        IReadOnlyList<CellPlatform> platforms,
        IReadOnlyList<CellOutcome> outcomes,
        TimeSpan totalDuration)
    {
        var artifactDir = RootDirectory / "artifacts";
        Directory.CreateDirectory(artifactDir);
        var path = artifactDir / $"regression-validate-{version}.json";

        var payload = new
        {
            version,
            timestamp = DateTime.UtcNow.ToString("o"),
            totalDurationSeconds = totalDuration.TotalSeconds,
            platforms = platforms.Select(PlatformLabel).ToArray(),
            cells = outcomes.Select(o => new
            {
                library = o.Cell.Library,
                platform = PlatformLabel(o.Cell.Platform),
                result = CellLabel(o.Result),
                reason = o.Reason,
                durationSeconds = o.Duration.TotalSeconds,
            }).ToArray(),
        };
        File.WriteAllText(path, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
        Log.Information("Wrote artifact: {Path}", path);
    }
}
