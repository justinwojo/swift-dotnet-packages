using Nuke.Common;
using Nuke.Common.IO;
using Serilog;

partial class Build
{
    /// <summary>
    /// Build a simulator/device test app under <c>tests/&lt;Name&gt;.SimTests/</c>.
    /// Direct port of <c>scripts/build-testapp.sh</c>.
    ///
    /// <para>Three modes (mirrors <c>build-testapp.sh:50–87</c>):</para>
    /// <list type="bullet">
    ///   <item><b>Default</b> — <c>dotnet build -c Debug</c> for the simulator.</item>
    ///   <item><c>--device</c> — <c>dotnet build -c Debug -p:RuntimeIdentifier=ios-arm64</c> (Mono AOT).</item>
    ///   <item><c>--device --aot</c> — <c>dotnet publish -c Release -r ios-arm64 -p:PublishAot=true ...</c>
    ///         (NativeAOT). Requires the codesign env vars listed below.</item>
    /// </list>
    ///
    /// <para>
    /// For the NativeAOT path, <c>CODESIGN_IDENTITY</c>, <c>PROVISIONING_PROFILE</c>,
    /// and <c>TEAM_ID</c> must be set in the environment. The same diagnostic
    /// shape as <c>build-testapp.sh:65–73</c> is emitted on missing vars.
    /// </para>
    /// </summary>
    Target BuildTestApp => _ => _
        .Description("Build a test app (--library Foo [--device [--aot]] [--platform ios|macos|maccatalyst|tvos])")
        .Requires(() => Library)
        .Executes(() =>
        {
            var library = Library!;
            var (testDir, _, _) = ResolveTestNames(library);

            var platform = EffectivePlatform;
            var device = !string.IsNullOrEmpty(RuntimeIdentifier) && RuntimeIdentifier == "ios-arm64"
                || Device;
            var aot = Aot;

            if (platform != "ios" && device)
                throw new InvalidOperationException("--device is only supported for --platform ios");

            if (aot && !device)
                throw new InvalidOperationException("--aot requires --device");

            if (device && aot)
            {
                BuildTestAppNativeAot(library, testDir);
            }
            else if (device)
            {
                BuildTestAppDevice(library, testDir);
            }
            else
            {
                BuildTestAppSimulator(library, testDir, platform);
            }

            Log.Information("=== Build complete ===");
        });

    void BuildTestAppSimulator(string library, AbsolutePath testDir, string platform = "ios")
    {
        Log.Information("=== Building {Library} tests for {Platform} ===", library, platform);
        var args = new List<string> { "build", (string)testDir, "-c", "Debug" };
        // Pass -f only when necessary: non-default platform always needs it
        // (e.g. --platform macos), and multi-TFM projects need it to select
        // the right TFM. Single-TFM legacy projects (which may use versioned
        // TFMs like net10.0-ios26.2) must NOT receive -f net10.0-ios — that
        // would fail to match.
        if (platform != "ios" || IsMultiTfmTestProject(testDir))
            args.AddRange(new[] { "-f", ResolveTfm(platform) });
        // Only pass an explicit RID when the user overrode it. Leaving the
        // default build command untouched preserves the SDK's automatic
        // iossimulator-arm64 selection that Session 3 validated end-to-end.
        if (!string.IsNullOrEmpty(RuntimeIdentifier))
            args.Add($"-p:RuntimeIdentifier={RuntimeIdentifier}");
        var exit = RunDotnet(args.ToArray());
        if (exit != 0)
            throw new InvalidOperationException($"dotnet build ({platform}) failed with exit {exit}");
    }

    void BuildTestAppDevice(string library, AbsolutePath testDir)
    {
        Log.Information("=== Building {Library} tests for device ===", library);
        var rid = ResolveRid("ios-arm64");
        var args = new List<string>
        {
            "build", (string)testDir, "-c", "Debug", $"-p:RuntimeIdentifier={rid}",
        };
        if (IsMultiTfmTestProject(testDir))
            args.AddRange(new[] { "-f", ResolveTfm("ios") });
        var exit = RunDotnet(args.ToArray());
        if (exit != 0)
            throw new InvalidOperationException($"dotnet build (device) failed with exit {exit}");
    }

    void BuildTestAppNativeAot(string library, AbsolutePath testDir)
    {
        // Codesign properties (CodesignKey / CodesignProvision / TeamIdentifierPrefix)
        // come from tests/Directory.Build.props.local — a gitignored, per-developer
        // file imported by tests/Directory.Build.props. See the example block in
        // that committed template for the schema. The env vars below act as
        // command-line overrides: when set, they're forwarded as -p: properties;
        // when unset, the .local file (or per-csproj values) takes over and the
        // publish "just works" without any per-invocation environment plumbing.
        var codesignIdentity = Environment.GetEnvironmentVariable("CODESIGN_IDENTITY");
        var provisioningProfile = Environment.GetEnvironmentVariable("PROVISIONING_PROFILE");
        var teamId = Environment.GetEnvironmentVariable("TEAM_ID");

        Log.Information("=== Building {Library} tests for device (NativeAOT) ===", library);
        var rid = ResolveRid("ios-arm64");
        var args = new List<string>
        {
            "publish", (string)testDir,
            "-c", "Release",
            "-r", rid,
            "-p:PublishAot=true",
            "-p:PublishAotUsingRuntimePack=true",
        };
        if (IsMultiTfmTestProject(testDir))
            args.AddRange(new[] { "-f", ResolveTfm("ios") });
        if (!string.IsNullOrEmpty(codesignIdentity))
            args.Add($"-p:CodesignKey={codesignIdentity}");
        if (!string.IsNullOrEmpty(provisioningProfile))
            args.Add($"-p:CodesignProvision={provisioningProfile}");
        if (!string.IsNullOrEmpty(teamId))
            args.Add($"-p:TeamIdentifierPrefix={teamId}");

        var exit = RunDotnet(args.ToArray());
        if (exit != 0)
            throw new InvalidOperationException($"dotnet publish (NativeAOT) failed with exit {exit}");
    }
}
