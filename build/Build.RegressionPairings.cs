using System.Diagnostics;
using System.Text.Json;
using Nuke.Common;
using Nuke.Common.IO;
using Serilog;
using SwiftBindings.Build.Models;

/// <summary>
/// RegressionValidate across the supported .NET + Xcode pairings. A single process builds for
/// exactly one pairing (its SDK, workload, Xcode, simulators and target frameworks are fixed
/// for the life of the process), so <c>--pairing all</c> — or any pairing other than the one
/// the shell selected — re-runs this build once per pairing under
/// <c>scripts/use-toolchain.sh</c> and folds the children's verdicts into one.
/// </summary>
partial class Build
{
    [Parameter("RegressionValidate: the .NET + Xcode pairing to validate — net10-xcode26, net10-xcode27, " +
               "net11-xcode27, or all (each pairing in turn under scripts/use-toolchain.sh, one aggregate " +
               "verdict). Default: the pairing the shell selected (SwiftBindingsPairing, else net10-xcode26).")]
    readonly string? Pairing;

    /// <summary>
    /// The pairings this invocation must run in child processes, or an empty list when the
    /// active pairing runs in this process.
    /// </summary>
    IReadOnlyList<string> PairingsToDelegate()
    {
        var requested = Pairing?.Trim();
        if (string.IsNullOrEmpty(requested))
            return Array.Empty<string>();
        if (requested.Equals("all", StringComparison.OrdinalIgnoreCase))
            return ToolchainPairing.All;
        if (!ToolchainPairing.All.Contains(requested, StringComparer.Ordinal))
            throw new InvalidOperationException(
                $"--pairing {requested} is not a supported pairing; use one of {string.Join(", ", ToolchainPairing.All)} or all.");
        // In process only when the shell already selected exactly this pairing; an unselected
        // shell (whose active pairing is merely the default) still gets the toolchain selected.
        return Environment.GetEnvironmentVariable(ToolchainPairing.EnvironmentVariable) == requested
            ? Array.Empty<string>()
            : new[] { requested };
    }

    /// <summary>
    /// Fail unless this process really runs on the active pairing's toolchain: the .NET SDK
    /// that resolves here and the Xcode that <c>xcodebuild</c> selects must be the pairing's.
    /// Without this, a shell that never ran <c>use-toolchain.sh</c> builds net10-xcode27's
    /// TFMs with the wrong Xcode and the verdict names a pairing it never ran.
    /// </summary>
    void AssertPairingEnvironment()
    {
        var p = ActivePairing;
        var problems = new List<string>();
        var sdk = TryCapture("dotnet", new[] { "--version" })?.Trim();
        if (sdk != p.SdkVersion)
            problems.Add($".NET SDK is {sdk ?? "(none)"}, the pairing needs {p.SdkVersion}");
        var xcode = TryCapture("xcodebuild", new[] { "-version" })?.Split('\n').FirstOrDefault()?.Trim();
        if (xcode != $"Xcode {p.XcodeVersion}")
            problems.Add($"xcodebuild reports {xcode ?? "(none)"}, the pairing needs Xcode {p.XcodeVersion}");
        if (problems.Count > 0)
            throw new InvalidOperationException(
                $"Pre-flight: this shell is not on pairing {p.Name}: {string.Join("; ", problems)}. " +
                $"Select it with `source scripts/use-toolchain.sh {p.Name}`, or pass --pairing {p.Name} " +
                "to have RegressionValidate select it.");
        Log.Information("  Pairing: {Name} (.NET SDK {Sdk}, {Xcode}, {Tfm}, engines {Engines}){Cache}",
            p.Name, sdk, xcode, p.Tfm("<platform>"), string.Join("+", p.Engines.Select(ToolchainPairing.EngineName)),
            Environment.GetEnvironmentVariable("NUGET_PACKAGES") is { Length: > 0 } np ? $", NUGET_PACKAGES={np}" : "");
    }

    sealed record PairingRun(
        string Pairing,
        int ExitCode,
        string Verdict,
        bool Complete,
        string? RunArtifact,
        IReadOnlyList<string> IncompleteReasons,
        IReadOnlyList<string> EnginesRequired,
        IReadOnlyList<string> EnginesPassed,
        JsonElement? Counts,
        string Log);

    /// <summary>
    /// Run RegressionValidate once per pairing in a child process and write one aggregate
    /// verdict. PASS needs every pairing to pass; <c>complete</c> (coverage) needs every pairing
    /// complete and every engine each pairing requires proven by at least one PASS cell.
    /// </summary>
    void RunPairingsInChildren(IReadOnlyList<string> pairings, string version)
    {
        var runId = ComposeRunId(DateTime.UtcNow, TryGitShortSha());
        var aggregateDir = RootDirectory / "artifacts" / "regression-validate" / version / "all" / runId;
        Directory.CreateDirectory(aggregateDir);
        Log.Information("=== regression-validate across {Count} pairing(s): {Pairings} ===",
            pairings.Count, string.Join(", ", pairings));
        Log.Information("Aggregate directory: {Dir}", aggregateDir);

        ReleaseBuildLogForChildren();
        var forwarded = ForwardedArguments();
        var runs = new List<PairingRun>();
        foreach (var pairing in pairings)
        {
            var logPath = aggregateDir / $"{pairing}.log";
            var args = new List<string> { (string)(RootDirectory / "scripts" / "use-toolchain.sh"), pairing, "--",
                "dotnet", typeof(Build).Assembly.Location };
            args.AddRange(forwarded);
            args.AddRange(new[] { "--pairing", pairing });
            Log.Information("→ {Pairing}: bash {Args}", pairing, string.Join(' ', RedactSecrets(args)));

            var psi = new ProcessStartInfo("bash")
            {
                WorkingDirectory = RootDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            // The child selects its own pairing; never let this process's selection leak in.
            psi.Environment.Remove(ToolchainPairing.EnvironmentVariable);
            psi.Environment[RegressionRunIdEnvironmentVariable] = runId;

            int exit;
            using (var writer = new StreamWriter(logPath, append: false) { AutoFlush = true })
            using (var proc = Process.Start(psi)!)
            {
                void Pump(string? line)
                {
                    if (line is null) return;
                    lock (writer) writer.WriteLine(line);
                    Console.WriteLine($"[{pairing}] {line}");
                }
                proc.OutputDataReceived += (_, e) => Pump(e.Data);
                proc.ErrorDataReceived += (_, e) => Pump(e.Data);
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();
                proc.WaitForExit();
                exit = proc.ExitCode;
            }

            runs.Add(ReadPairingRun(pairing, version, runId, exit, logPath));
            var r = runs[^1];
            Log.Information("← {Pairing}: {Verdict} (exit {Exit}, complete={Complete}) {Artifact}",
                pairing, r.Verdict, exit, r.Complete, r.RunArtifact ?? "(no run artifact — pre-flight abort)");
        }

        // One requested pairing (--pairing net10-xcode27 from an unselected shell) is that
        // pairing's own run: its artifact is the verdict, and the parent only relays the outcome.
        if (pairings.Count == 1)
        {
            var only = runs[0];
            if (only.Verdict != "PASS")
            {
                RegressionExitCodeOverride = only.Verdict == "FAIL" ? RegressionExitCellsFailed : RegressionExitAborted;
                throw new InvalidOperationException(
                    $"regression-validate {only.Pairing}: {only.Verdict}\n  verdict artifact: {only.RunArtifact ?? (string)only.Log}");
            }
            return;
        }
        WritePairingsVerdict(version, runId, aggregateDir, pairings, runs);
    }

    /// <summary>The environment variable through which a <c>--pairing all</c> parent names the
    /// run id its child writes its evidence under.</summary>
    internal const string RegressionRunIdEnvironmentVariable = "SWIFTBINDINGS_REGRESSION_RUN_ID";

    /// <summary>The child's command line: RegressionValidate alone (never a sibling target the
    /// parent was also asked to run) and this invocation's parameters minus the pairing
    /// selection, so a child repeats exactly the same run on its own pairing.</summary>
    static List<string> ForwardedArguments()
    {
        var raw = Environment.GetCommandLineArgs().Skip(1).ToList();
        var result = new List<string> { nameof(RegressionValidate) };
        // Nuke reads leading positional tokens as target names; parameters start at the first "-".
        var firstParameter = raw.FindIndex(a => a.StartsWith('-'));
        for (var i = firstParameter < 0 ? raw.Count : firstParameter; i < raw.Count; i++)
        {
            var a = raw[i];
            var name = OptionName(a);
            if (name == "pairing")
            {
                if (!a.Contains('=')) i++;
                continue;
            }
            // Nuke's named target option outranks the positional one; the child runs one target.
            if (name == "target")
            {
                if (!a.Contains('='))
                    while (i + 1 < raw.Count && !raw[i + 1].StartsWith('-')) i++;
                continue;
            }
            result.Add(a);
        }
        return result;
    }

    /// <summary>A parameter's name as Nuke matches it: hyphens dropped, case folded
    /// (<c>--nuget-api-k-ey</c> and <c>-NugetApiKey</c> both name <c>nugetapikey</c>); empty for a value.</summary>
    internal static string OptionName(string arg) =>
        arg.StartsWith('-') ? arg.Split('=', 2)[0].Replace("-", "").ToLowerInvariant() : "";

    /// <summary>The arguments as they may be logged: the value of any secret parameter masked.</summary>
    static IEnumerable<string> RedactSecrets(IReadOnlyList<string> args)
    {
        static bool Secret(string a) => a.StartsWith('-')
            && OptionName(a) is var n && (n.Contains("key") || n.Contains("token") || n.Contains("secret") || n.Contains("password"));
        for (var i = 0; i < args.Count; i++)
        {
            var eq = args[i].IndexOf('=');
            if (eq > 0 && Secret(args[i][..eq])) { yield return args[i][..(eq + 1)] + "***"; continue; }
            yield return args[i];
            if (Secret(args[i]) && i + 1 < args.Count && !args[i + 1].StartsWith('-')) { yield return "***"; i++; }
        }
    }

    PairingRun ReadPairingRun(string pairing, string version, string runId, int exit, AbsolutePath log)
    {
        // Exactly the run this parent assigned to the child (SWIFTBINDINGS_REGRESSION_RUN_ID).
        var artifact = RootDirectory / "artifacts" / "regression-validate" / version / pairing / runId / "regression-validate.json";
        if (!File.Exists(artifact))
            return new PairingRun(pairing, exit, "ABORTED", false, null,
                new[] { $"{pairing} wrote no run artifact (exit {exit}); see {log}" },
                Array.Empty<string>(), Array.Empty<string>(), null, log);

        JsonDocument doc;
        try { doc = JsonDocument.Parse(File.ReadAllText(artifact)); }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return new PairingRun(pairing, exit, "ABORTED", false, artifact,
                new[] { $"{pairing}'s run artifact {artifact} is unreadable: {ex.Message}" },
                Array.Empty<string>(), Array.Empty<string>(), null, log);
        }
        using var _ = doc;
        var root = doc.RootElement;
        string[] Strings(JsonElement e, string key) =>
            e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Array
                ? v.EnumerateArray().Select(x => x.GetString() ?? "").ToArray()
                : Array.Empty<string>();
        var verdict = root.TryGetProperty("verdict", out var vv) ? vv.GetString() ?? "UNKNOWN" : "UNKNOWN";
        var complete = root.TryGetProperty("complete", out var cv) && cv.ValueKind == JsonValueKind.True;
        var pairingBlock = root.TryGetProperty("pairing", out var pb) ? pb : default;
        var recordedName = pairingBlock.ValueKind == JsonValueKind.Object && pairingBlock.TryGetProperty("name", out var nm)
            ? nm.GetString() : null;
        var reasons = Strings(root, "incompleteReasons").ToList();
        if (recordedName != pairing)
        {
            // A child that ran some other pairing must not be counted as this one.
            reasons.Add($"artifact {artifact} records pairing '{recordedName}', not {pairing}");
            complete = false;
            verdict = "ABORTED";
        }
        // The child's own exit code is the authority on failure even if its artifact says PASS.
        if (exit != 0 && verdict == "PASS")
            verdict = "FAIL";
        return new PairingRun(pairing, exit, verdict, complete, artifact, reasons,
            pairingBlock.ValueKind == JsonValueKind.Object ? Strings(pairingBlock, "enginesRequired") : Array.Empty<string>(),
            pairingBlock.ValueKind == JsonValueKind.Object ? Strings(pairingBlock, "enginesPassed") : Array.Empty<string>(),
            root.TryGetProperty("counts", out var counts) ? counts.Clone() : null,
            log);
    }

    void WritePairingsVerdict(string version, string runId, AbsolutePath aggregateDir,
        IReadOnlyList<string> pairings, IReadOnlyList<PairingRun> runs)
    {
        var (verdict, complete, reasons) = JudgePairings(pairings, runs);
        var payload = new
        {
            schemaVersion = 3,
            kind = "all-pairings",
            version,
            runId,
            timestamp = DateTime.UtcNow.ToString("o"),
            verdict,
            complete,
            incompleteReasons = reasons.ToArray(),
            pairingsRequired = ToolchainPairing.All.ToArray(),
            pairingsRun = runs.Select(r => r.Pairing).ToArray(),
            exitCode = verdict switch { "PASS" => 0, "FAIL" => RegressionExitCellsFailed, _ => RegressionExitAborted },
            pairings = runs.Select(r => new
            {
                pairing = r.Pairing,
                verdict = r.Verdict,
                complete = r.Complete,
                exitCode = r.ExitCode,
                enginesRequired = r.EnginesRequired,
                enginesPassed = r.EnginesPassed,
                counts = r.Counts,
                incompleteReasons = r.IncompleteReasons,
                runArtifact = r.RunArtifact,
                log = (string)r.Log,
            }).ToArray(),
        };
        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
        var artifact = aggregateDir / "regression-validate.json";
        File.WriteAllText(artifact, json);
        if (complete)
            File.WriteAllText(RootDirectory / "artifacts" / $"regression-validate-{version}.json", json);

        Console.WriteLine();
        Console.WriteLine($"=== regression-validate, all pairings: {verdict} (complete={complete.ToString().ToLowerInvariant()}) ===");
        foreach (var r in runs)
            Console.WriteLine($"  {r.Pairing,-14} {r.Verdict,-8} complete={r.Complete.ToString().ToLowerInvariant(),-5} " +
                              $"engines passed: {(r.EnginesPassed.Count == 0 ? "none" : string.Join(", ", r.EnginesPassed))}  {r.RunArtifact}");
        foreach (var reason in reasons)
            Console.WriteLine($"  incomplete: {reason}");
        Console.WriteLine($"  artifact: {artifact}");

        if (verdict != "PASS")
        {
            RegressionExitCodeOverride = verdict == "FAIL" ? RegressionExitCellsFailed : RegressionExitAborted;
            throw new InvalidOperationException(
                $"regression-validate: {string.Join(", ", runs.Where(r => r.Verdict != "PASS").Select(r => $"{r.Pairing}={r.Verdict}"))}" +
                $"\n  verdict artifact: {artifact}");
        }
        if (!complete)
            Log.Warning("regression-validate: every pairing is GREEN but the run is INCOMPLETE — {Reasons}",
                string.Join("; ", reasons));
    }

    /// <summary>The aggregate verdict over the pairings' runs (pure; covered by the self-test).</summary>
    internal static (string Verdict, bool Complete, List<string> Reasons) JudgePairingsForTest(
        IReadOnlyList<string> pairings,
        IReadOnlyList<(string Pairing, string Verdict, bool Complete, string[] Required, string[] Passed)> runs) =>
        JudgePairings(pairings, runs.Select(r => new PairingRun(r.Pairing, r.Verdict == "PASS" ? 0 : 2, r.Verdict,
            r.Complete, null, Array.Empty<string>(), r.Required, r.Passed, null, (AbsolutePath)"/dev/null")).ToList());

    static (string Verdict, bool Complete, List<string> Reasons) JudgePairings(
        IReadOnlyList<string> pairings, IReadOnlyList<PairingRun> runs)
    {
        var reasons = new List<string>();
        var unprovenEngines = false;
        foreach (var missing in ToolchainPairing.All.Where(p => !pairings.Contains(p)))
            reasons.Add($"pairing {missing} was not run");
        foreach (var p in pairings.Where(p => runs.All(r => r.Pairing != p)))
            reasons.Add($"pairing {p} produced no result");
        foreach (var r in runs)
        {
            if (!r.Complete)
                reasons.Add($"{r.Pairing} is incomplete" +
                            (r.IncompleteReasons.Count == 0 ? "" : $": {string.Join("; ", r.IncompleteReasons)}"));
            if (r.EnginesRequired.Count == 0)
                reasons.Add($"{r.Pairing} recorded no required engines");
            // A required engine no PASS cell proved (every cell on it failed, or was only deferred)
            // is an outcome, not a coverage gap: it fails the run, so a complete red run still
            // replaces the canonical verdict.
            var unproven = r.EnginesRequired.Where(e => !r.EnginesPassed.Contains(e)).ToList();
            if (r.EnginesRequired.Count > 0 && unproven.Count > 0)
            {
                unprovenEngines = true;
                Log.Warning("{Pairing}: no passing cell proved {Engines}", r.Pairing, string.Join(", ", unproven));
            }
        }

        var verdict = runs.Any(r => r.Verdict == "ABORTED") || runs.Count < pairings.Count ? "ABORTED"
            : runs.Any(r => r.Verdict != "PASS") || unprovenEngines ? "FAIL"
            : "PASS";
        // Complete is coverage, not outcome (the per-pairing artifacts use the same split): a run
        // that covered every pairing and engine claims the canonical slot whatever its verdict,
        // so the canonical file is never a stale green after a later red run.
        return (verdict, reasons.Count == 0, reasons);
    }
}
