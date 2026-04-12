using Nuke.Common;
using Nuke.Common.IO;
using Serilog;

enum LibraryKind { ThirdParty, AppleFramework }

partial class Build : NukeBuild
{
    /// <summary>
    /// Entry point. Default target builds the Nuke build project itself
    /// (a smoke test of the harness, mirroring the template).
    /// </summary>
    public static int Main() => Execute<Build>(x => x.Compile);

    // ── Parameters (§3) ─────────────────────────────────────────────────────

    [Parameter("Library name (matches libraries/<name>/)")]
    readonly string? Library;

    [Parameter("Comma-separated product subset (e.g. StripeCore,StripePayments)")]
    readonly string[] Products = System.Array.Empty<string>();

    [Parameter("Build all products in library.json")]
    readonly bool AllProducts;

    [Parameter("Build configuration")]
    readonly Configuration Configuration = Configuration.Debug;

    [Parameter("iOS RID — overrides default of iossimulator-arm64")]
    readonly string? RuntimeIdentifier;

    [Parameter("Sim test timeout in seconds")]
    readonly int Timeout = 30;

    [Parameter("Simulator UDID (default: booted)")]
    readonly string? DeviceUdid;

    [Parameter("Wipe swift-binding/ before pass 1")]
    readonly bool CleanFirst;

    [Parameter("Target platform for test app build/validate: ios (default), macos, maccatalyst, tvos")]
    readonly string? Platform;

    [Parameter("Build the test app for a physical device (ios-arm64) instead of the simulator")]
    readonly bool Device;

    [Parameter("Use NativeAOT publish for the test app (requires --device + codesign env vars)")]
    readonly bool Aot;

    [Parameter("Reuse already-booted simulator (CI)")]
    readonly bool ReuseSim;

    [Parameter("Outer wall-clock budget for RunCiSimTest in seconds (default: 1140)")]
    readonly int StepTimeout = 1140;

    [Parameter("Max retries for test-timeout (RunCiSimTest)")]
    readonly int MaxTestRetries = 1;

    [Parameter("Max retries for infrastructure failures (RunCiSimTest)")]
    readonly int MaxInfraRetries = 1;

    [Parameter("Diagnostics dir for sim diagnostics (default: /tmp/sim-diagnostics)")]
    readonly string DiagDir = "/tmp/sim-diagnostics";

    [Parameter("Package version override")]
    readonly string? Version;

    [Parameter, Secret]
    readonly string? NuGetApiKey;

    // ── Computed paths (§3) ─────────────────────────────────────────────────

    AbsolutePath LibrariesDir => RootDirectory / "libraries";
    AbsolutePath AppleFrameworksDir => RootDirectory / "apple-frameworks";
    AbsolutePath TestsDir => RootDirectory / "tests";
    AbsolutePath ScriptsDir => RootDirectory / "scripts";
    AbsolutePath ToolsCacheDir => RootDirectory / ".tools";
    AbsolutePath LocalPackagesDir => RootDirectory / "local-packages";

    /// <summary>
    /// Resolve a library name to its kind and root directory by checking
    /// <c>libraries/</c> then <c>apple-frameworks/</c>. Throws if the name
    /// exists in neither (or both — collision assertion).
    /// </summary>
    (LibraryKind Kind, AbsolutePath Dir) ResolveLibrary(string lib)
    {
        var inLibraries = Directory.Exists(LibrariesDir / lib);
        var inAppleFrameworks = Directory.Exists(AppleFrameworksDir / lib);

        if (inLibraries && inAppleFrameworks)
            throw new InvalidOperationException(
                $"Name collision: '{lib}' exists in both libraries/ and apple-frameworks/");

        if (inLibraries)
            return (LibraryKind.ThirdParty, LibrariesDir / lib);
        if (inAppleFrameworks)
            return (LibraryKind.AppleFramework, AppleFrameworksDir / lib);

        throw new InvalidOperationException(
            $"Library '{lib}' not found in libraries/ or apple-frameworks/");
    }

    AbsolutePath LibraryDir(string lib) => ResolveLibrary(lib).Dir;
    AbsolutePath LibraryConfigPath(string lib) => LibrariesDir / lib / "library.json";
    AbsolutePath TestDir(string lib) => LibraryDir(lib) / "tests";

    /// <summary>
    /// Resolve the effective iOS runtime identifier for the currently-running
    /// target. Returns the user's <c>--runtime-identifier</c> override if set,
    /// otherwise the given default. Callers pass <c>"iossimulator-arm64"</c>
    /// for simulator paths and <c>"ios-arm64"</c> for device paths. This is
    /// the single place the RID parameter is honored so the build-time RID
    /// and the post-build bin path always agree.
    /// </summary>
    string ResolveRid(string defaultRid) =>
        !string.IsNullOrEmpty(RuntimeIdentifier) ? RuntimeIdentifier : defaultRid;

    /// <summary>
    /// Resolve the effective TFM string for the given platform. Returns
    /// <c>net10.0-{platform}</c> (e.g. <c>net10.0-ios</c>, <c>net10.0-macos</c>).
    /// </summary>
    static string ResolveTfm(string platform) => $"net10.0-{platform}";

    /// <summary>
    /// Return the effective platform name from the <c>--platform</c> parameter,
    /// defaulting to <c>"ios"</c>.
    /// </summary>
    string EffectivePlatform => string.IsNullOrEmpty(Platform) ? "ios" : Platform;

    /// <summary>
    /// Resolve the <c>.app</c> bundle path under <c>bin/{config}/</c>,
    /// probing both the unversioned TFM (<c>net10.0-ios</c>) and any
    /// versioned variant (<c>net10.0-ios26.2</c>) that may exist. This
    /// handles both multi-TFM test projects (which output to the
    /// unversioned TFM folder) and legacy single-TFM projects (which
    /// may use a versioned TFM like <c>net10.0-ios26.2</c>).
    /// </summary>
    static AbsolutePath ResolveAppPath(
        AbsolutePath testDir, string config, string platform, string rid, string appName)
    {
        var binConfig = testDir / "bin" / config;
        var unversioned = binConfig / ResolveTfm(platform) / rid / $"{appName}.app";
        if (Directory.Exists(unversioned))
            return unversioned;

        // Probe for a versioned TFM directory (e.g. net10.0-ios26.2)
        var prefix = ResolveTfm(platform);
        if (Directory.Exists(binConfig))
        {
            foreach (var dir in Directory.EnumerateDirectories(binConfig))
            {
                var name = Path.GetFileName(dir);
                if (name != null && name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && name != prefix)
                {
                    var candidate = (AbsolutePath)dir / rid / $"{appName}.app";
                    if (Directory.Exists(candidate))
                        return candidate;
                }
            }
        }

        // Fall back to the unversioned path (caller will report "not found")
        return unversioned;
    }

    /// <summary>
    /// Check whether a test directory contains a multi-TFM project
    /// (<c>&lt;TargetFrameworks&gt;</c> plural). Single-TFM projects must
    /// NOT receive a <c>-f</c> flag if their TFM is versioned (e.g.
    /// <c>net10.0-ios26.2</c>) and the caller would pass the unversioned
    /// form (<c>net10.0-ios</c>).
    /// </summary>
    static bool IsMultiTfmTestProject(AbsolutePath testDir)
    {
        foreach (var csproj in Directory.EnumerateFiles(testDir, "*.csproj"))
        {
            var content = File.ReadAllText(csproj);
            if (content.Contains("<TargetFrameworks>", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    // ── Stub targets ────────────────────────────────────────────────────────

    Target Clean => _ => _
        .Before(Restore)
        .Executes(() =>
        {
            // Clean the Nuke build project itself.
            var buildDir = RootDirectory / "build";
            (buildDir / "bin").CreateOrCleanDirectory();
            (buildDir / "obj").CreateOrCleanDirectory();
        });

    Target Restore => _ => _
        .Executes(() =>
        {
            Log.Information("Restore: nothing to do (Nuke build harness restores itself)");
        });

    Target Compile => _ => _
        .DependsOn(Restore)
        .Executes(() =>
        {
            Log.Information("Compile: Nuke build harness already compiled to run this target");
        });
}
