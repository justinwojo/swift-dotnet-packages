using Nuke.Common;
using Nuke.Common.IO;
using Serilog;

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

    [Parameter("Build the test app for a physical device (ios-arm64) instead of the simulator")]
    readonly bool Device;

    [Parameter("Use NativeAOT publish for the test app (requires --device + codesign env vars)")]
    readonly bool Aot;

    [Parameter("Reuse already-booted simulator (CI)")]
    readonly bool ReuseSim;

    [Parameter("Package version override")]
    readonly string? Version;

    [Parameter, Secret]
    readonly string? NuGetApiKey;

    // ── Computed paths (§3) ─────────────────────────────────────────────────

    AbsolutePath LibrariesDir => RootDirectory / "libraries";
    AbsolutePath TestsDir => RootDirectory / "tests";
    AbsolutePath ScriptsDir => RootDirectory / "scripts";
    AbsolutePath ToolsCacheDir => RootDirectory / ".tools";
    AbsolutePath LocalPackagesDir => RootDirectory / "local-packages";

    AbsolutePath LibraryDir(string lib) => LibrariesDir / lib;
    AbsolutePath LibraryConfigPath(string lib) => LibraryDir(lib) / "library.json";
    AbsolutePath TestDir(string lib) => TestsDir / $"{lib}.SimTests";

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
