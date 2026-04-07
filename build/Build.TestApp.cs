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
        .Description("Build a tests/<Name>.SimTests app (--library Foo [--device [--aot]])")
        .Requires(() => Library)
        .Executes(() =>
        {
            var library = Library!;
            var testDir = TestDir(library);
            if (!Directory.Exists(testDir))
                throw new InvalidOperationException($"Test directory not found: {testDir}");

            var device = !string.IsNullOrEmpty(RuntimeIdentifier) && RuntimeIdentifier == "ios-arm64"
                || Device;
            var aot = Aot;

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
                BuildTestAppSimulator(library, testDir);
            }

            Log.Information("=== Build complete ===");
        });

    void BuildTestAppSimulator(string library, AbsolutePath testDir)
    {
        Log.Information("=== Building {Library} tests for simulator ===", library);
        var exit = RunDotnet(new[] { "build", (string)testDir, "-c", "Debug" });
        if (exit != 0)
            throw new InvalidOperationException($"dotnet build (sim) failed with exit {exit}");
    }

    void BuildTestAppDevice(string library, AbsolutePath testDir)
    {
        Log.Information("=== Building {Library} tests for device ===", library);
        var exit = RunDotnet(new[]
        {
            "build", (string)testDir, "-c", "Debug", "-p:RuntimeIdentifier=ios-arm64",
        });
        if (exit != 0)
            throw new InvalidOperationException($"dotnet build (device) failed with exit {exit}");
    }

    void BuildTestAppNativeAot(string library, AbsolutePath testDir)
    {
        // Codesign env vars are required ONLY for the NativeAOT publish path —
        // the bash gates this at build-testapp.sh:65–73. Same diagnostic
        // template (variable names + example block) so the developer-facing
        // error message is grep-stable.
        var codesignIdentity = Environment.GetEnvironmentVariable("CODESIGN_IDENTITY");
        var provisioningProfile = Environment.GetEnvironmentVariable("PROVISIONING_PROFILE");
        var teamId = Environment.GetEnvironmentVariable("TEAM_ID");

        var missing = new List<string>();
        if (string.IsNullOrEmpty(codesignIdentity)) missing.Add("CODESIGN_IDENTITY");
        if (string.IsNullOrEmpty(provisioningProfile)) missing.Add("PROVISIONING_PROFILE");
        if (string.IsNullOrEmpty(teamId)) missing.Add("TEAM_ID");

        if (missing.Count > 0)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("Error: --aot --device requires the following environment variables:");
            foreach (var v in missing)
                sb.AppendLine($"  {v}");
            sb.AppendLine();
            sb.AppendLine("Example:");
            sb.AppendLine("  export CODESIGN_IDENTITY=\"Apple Development: Name (TEAMID)\"");
            sb.AppendLine("  export PROVISIONING_PROFILE=\"Wildcard Dev\"");
            sb.AppendLine("  export TEAM_ID=\"TL2K6QUQEH\"");
            throw new InvalidOperationException(sb.ToString());
        }

        Log.Information("=== Building {Library} tests for device (NativeAOT) ===", library);
        var exit = RunDotnet(new[]
        {
            "publish", (string)testDir,
            "-c", "Release",
            "-r", "ios-arm64",
            "-p:PublishAot=true",
            "-p:PublishAotUsingRuntimePack=true",
            $"-p:CodesignKey={codesignIdentity}",
            $"-p:CodesignProvision={provisioningProfile}",
            $"-p:TeamIdentifierPrefix={teamId}",
        });
        if (exit != 0)
            throw new InvalidOperationException($"dotnet publish (NativeAOT) failed with exit {exit}");
    }
}
