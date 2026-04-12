using Nuke.Common;
using Nuke.Common.IO;
using Serilog;

partial class Build
{
    /// <summary>
    /// Build an Apple framework csproj via <c>dotnet build</c>. Apple framework
    /// csprojs are self-contained: the <c>SwiftBindings.Sdk</c> handles binding
    /// generation from the system framework target, emits the C#, and compiles
    /// everything in a single <c>dotnet build</c>. No xcframework build, no
    /// product iteration, no dependency injection.
    /// </summary>
    Target BuildAppleFramework => _ => _
        .Description("dotnet build for apple-frameworks/<Name>/SwiftBindings.<Name>.csproj (--library X)")
        .Requires(() => Library)
        .Executes(() =>
        {
            var (kind, dir) = ResolveLibrary(Library!);
            if (kind != LibraryKind.AppleFramework)
                throw new InvalidOperationException(
                    $"{Library} is not an Apple framework (found in libraries/). Use BuildLibrary instead.");

            var csproj = dir.GlobFiles("SwiftBindings.*.csproj").SingleOrDefault()
                ?? throw new InvalidOperationException(
                    $"No SwiftBindings.*.csproj found in {dir}");

            Log.Information("Building Apple framework: {Csproj}", csproj);
            var exit = RunDotnet(new[] { "build", (string)csproj, "-c", (string)Configuration });
            if (exit != 0)
                throw new InvalidOperationException($"dotnet build failed: exit {exit}");
        });

    /// <summary>
    /// Smoke pack an Apple framework csproj with version <c>0.0.0-ci</c>.
    /// Validates that the csproj packs cleanly without publishing anything.
    /// </summary>
    Target PackValidateAppleFramework => _ => _
        .Description("Smoke pack an Apple framework csproj with version 0.0.0-ci (--library X)")
        .Requires(() => Library)
        .Executes(() =>
        {
            var (kind, dir) = ResolveLibrary(Library!);
            if (kind != LibraryKind.AppleFramework)
                throw new InvalidOperationException(
                    $"{Library} is not an Apple framework (found in libraries/). Use PackValidate instead.");

            var csproj = dir.GlobFiles("SwiftBindings.*.csproj").SingleOrDefault()
                ?? throw new InvalidOperationException(
                    $"No SwiftBindings.*.csproj found in {dir}");

            var outputDir = PackOutputDir;
            outputDir.CreateDirectory();

            Log.Information("PackValidate Apple framework: {Csproj} → {Output}", csproj, outputDir);
            var exit = RunDotnet(new[]
            {
                "pack", (string)csproj, "-c", "Release",
                "-p:Version=0.0.0-ci", "-o", (string)outputDir,
            });
            if (exit != 0)
                throw new InvalidOperationException($"dotnet pack failed: exit {exit}");
        });
}
