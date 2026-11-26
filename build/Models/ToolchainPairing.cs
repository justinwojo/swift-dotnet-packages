using System.Text.Json;

namespace SwiftBindings.Build.Models;

/// <summary>
/// What a simulator, tvOS or Mac Catalyst test app actually executes on. The pairing decides
/// the possible engines (.NET 10 apps run Mono; .NET 11 apps run CoreCLR) and the build
/// configuration picks between the two .NET 11 ones: Debug runs the CoreCLR interpreter,
/// Release runs ReadyToRun code.
/// </summary>
public enum AppEngine
{
    /// <summary>Mono (.NET 10 simulator, tvOS and Mac Catalyst apps; JIT or interpreter).</summary>
    Mono,

    /// <summary>CoreCLR with the interpreter (.NET 11, Debug).</summary>
    CoreClrInterpreter,

    /// <summary>CoreCLR running ReadyToRun code (.NET 11, Release).</summary>
    CoreClrReadyToRun,

    /// <summary>CoreCLR with the JIT (every macOS app; no Apple workload choice).</summary>
    CoreClrJit,

    /// <summary>NativeAOT (device publish with PublishAot).</summary>
    NativeAot,

    /// <summary>Mono full-AOT (.NET 10 device build without PublishAot).</summary>
    MonoAot,
}

/// <summary>
/// One supported .NET + Xcode pairing, read from <c>toolchains/&lt;pairing&gt;.json</c>: the
/// single record of the toolchain, the target framework and copy label every csproj derives
/// (through <c>Directory.Build.props</c>), the simulator runtimes and the engines its apps run on.
/// </summary>
public sealed record ToolchainPairing(
    string Name,
    string SdkVersion,
    string WorkloadVersion,
    string DotnetRoot,
    string DeveloperDirectory,
    string XcodeVersion,
    string TargetFramework,
    string TfmPlatformVersion,
    string PlatformLabel,
    IReadOnlyList<AppEngine> Engines,
    IReadOnlyList<string> SimRuntimes,
    IReadOnlyList<string> TvSimRuntimes)
{
    /// <summary>Every supported pairing, oldest first (the merge orders copies the same way).</summary>
    public static readonly IReadOnlyList<string> All = new[] { "net10-xcode26", "net10-xcode27", "net11-xcode27" };

    /// <summary>The pairing a build uses when none is selected (matches Directory.Build.props).</summary>
    public const string Default = "net10-xcode26";

    /// <summary>The environment variable (and MSBuild property) that selects the pairing.</summary>
    public const string EnvironmentVariable = "SwiftBindingsPairing";

    /// <summary>True for the .NET 10 pairings, whose simulator and Catalyst apps run Mono.</summary>
    public bool AppsRunOnMono => TargetFramework == "net10.0";

    /// <summary>The TFM the pairing builds a platform with (<c>net10.0-ios</c>,
    /// <c>net10.0-ios27.2</c>, <c>net11.0-ios27.2</c>), as Directory.Build.props spells it.</summary>
    public string Tfm(string platform) => $"{TargetFramework}-{platform}{TfmPlatformVersion}";

    /// <summary>The <c>lib/</c> folder the pairing's package copy lands in (<c>net10.0-ios26.0</c>,
    /// <c>net10.0-ios27.2</c>, <c>net11.0-ios27.2</c>). It differs from <see cref="Tfm"/> only on
    /// net10-xcode26, whose versionless build TFM packs under the workload's 26.0 platform version.</summary>
    public string CopyTfm(string platform) => $"{TargetFramework}-{platform}{PlatformLabel}";

    /// <summary>Load and validate <c>toolchains/&lt;name&gt;.json</c>.</summary>
    public static ToolchainPairing Load(string toolchainsDir, string name)
    {
        if (!All.Contains(name, StringComparer.Ordinal))
            throw new InvalidOperationException(
                $"'{name}' is not a supported pairing; use one of {string.Join(", ", All)}.");
        var path = Path.Combine(toolchainsDir, name + ".json");
        if (!File.Exists(path))
            throw new InvalidOperationException($"Pairing record {path} is missing.");
        return Parse(File.ReadAllText(path), name, path);
    }

    internal static ToolchainPairing Parse(string json, string name, string source)
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        string Str(string key) =>
            r.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s
                ? s
                : throw new InvalidOperationException($"{source}: '{key}' is missing or empty.");
        IReadOnlyList<string> List(string key) =>
            r.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Array
                ? v.EnumerateArray().Select(e => e.GetString() ?? "").Where(s => s.Length > 0).ToList()
                : throw new InvalidOperationException($"{source}: '{key}' is missing or not an array.");

        if (Str("pairing") != name)
            throw new InvalidOperationException($"{source} names pairing '{Str("pairing")}', not '{name}'.");

        var tfm = Str("targetFramework");
        if (tfm is not ("net10.0" or "net11.0"))
            throw new InvalidOperationException($"{source}: targetFramework '{tfm}' is not net10.0 or net11.0.");

        var engines = List("engines").Select(ParseEngine).ToList();
        var expected = tfm == "net10.0"
            ? new[] { AppEngine.Mono }
            : new[] { AppEngine.CoreClrInterpreter, AppEngine.CoreClrReadyToRun };
        if (!engines.SequenceEqual(expected))
            throw new InvalidOperationException(
                $"{source}: engines [{string.Join(", ", engines.Select(EngineName))}] do not match {tfm}'s " +
                $"[{string.Join(", ", expected.Select(EngineName))}].");

        var sims = List("simRuntimes");
        if (sims.Count == 0)
            throw new InvalidOperationException($"{source}: simRuntimes is empty.");

        var root = Str("dotnetRoot");
        if (root.StartsWith("~/", StringComparison.Ordinal))
            root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), root[2..]);

        // Empty means a versionless build TFM (net10-xcode26), so it is the one field allowed to be "".
        var tfmVersion = r.TryGetProperty("tfmPlatformVersion", out var tv) && tv.ValueKind == JsonValueKind.String
            ? tv.GetString()!
            : throw new InvalidOperationException($"{source}: 'tfmPlatformVersion' is missing.");
        var label = Str("platformLabel");
        if (tfmVersion.Length > 0 && tfmVersion != label)
            throw new InvalidOperationException(
                $"{source}: tfmPlatformVersion '{tfmVersion}' must be empty or equal platformLabel '{label}'.");

        return new ToolchainPairing(
            name, Str("sdkVersion"), Str("workloadVersion"), root, Str("developerDirectory"),
            Str("xcodeVersion"), tfm, tfmVersion, label, engines, sims, List("tvSimRuntimes"));
    }

    public static AppEngine ParseEngine(string raw) => raw switch
    {
        "mono" => AppEngine.Mono,
        "coreclr-interpreter" => AppEngine.CoreClrInterpreter,
        "coreclr-r2r" => AppEngine.CoreClrReadyToRun,
        "coreclr-jit" => AppEngine.CoreClrJit,
        "nativeaot" => AppEngine.NativeAot,
        "mono-aot" => AppEngine.MonoAot,
        _ => throw new InvalidOperationException($"unknown engine '{raw}'"),
    };

    /// <summary>The engine's JSON/label spelling (the inverse of <see cref="ParseEngine"/>).</summary>
    public static string EngineName(AppEngine e) => e switch
    {
        AppEngine.Mono => "mono",
        AppEngine.CoreClrInterpreter => "coreclr-interpreter",
        AppEngine.CoreClrReadyToRun => "coreclr-r2r",
        AppEngine.CoreClrJit => "coreclr-jit",
        AppEngine.NativeAot => "nativeaot",
        AppEngine.MonoAot => "mono-aot",
        _ => e.ToString(),
    };
}
