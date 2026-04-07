using Nuke.Common;
using Nuke.Common.IO;
using Serilog;
using SwiftBindings.Build.Helpers;
using SwiftBindings.Build.Models;

partial class Build
{
    /// <summary>
    /// Output directory for <see cref="Pack"/> and <see cref="PackValidate"/>.
    /// Falls back to a sensible default under <c>artifacts/packages</c> when the
    /// caller doesn't pass <c>--output</c>.
    /// </summary>
    [Parameter("Output directory for Pack/PackValidate (default: artifacts/packages)")]
    readonly string? Output;

    AbsolutePath PackOutputDir => string.IsNullOrEmpty(Output)
        ? RootDirectory / "artifacts" / "packages"
        : (AbsolutePath)Path.GetFullPath(Output);

    /// <summary>
    /// Pack every non-internal product in a library to a NuGet <c>.nupkg</c>
    /// in <see cref="PackOutputDir"/>. Direct port of the per-csproj
    /// <c>dotnet pack --no-build -c Release /p:Version=...</c> loop in
    /// <c>release.yml:162–193</c>.
    ///
    /// <para>
    /// Iterates products in <c>library.json</c> order — that's the same order
    /// the bash loop preserved (dependencies first), and the order in which
    /// the publish job pushes the resulting <c>.nupkg</c> files. Skips internal
    /// products since they have no csproj.
    /// </para>
    ///
    /// <para>
    /// Honors <see cref="Configuration"/> and <see cref="Version"/>:
    /// </para>
    /// <list type="bullet">
    ///   <item><c>--version</c> overrides <c>/p:Version</c> when present;
    ///         when missing, the csproj's own <c>Version</c> property wins.</item>
    ///   <item><c>--configuration Release</c> is what the release path needs;
    ///         pack runs with <c>--no-build</c> so the prior
    ///         <see cref="BuildLibrary"/> step MUST have been invoked with the
    ///         same configuration. The release flow chains
    ///         <c>BuildLibrary --configuration Release</c> → <c>Pack</c>
    ///         in <see cref="BuildAndPackRelease"/> for this exact reason.</item>
    /// </list>
    /// </summary>
    Target Pack => _ => _
        .Description("Pack non-internal products to .nupkg (--library Foo --version 1.2.3 [--output dir])")
        .Requires(() => Library)
        .Executes(() =>
        {
            var library = Library!;
            var libraryDir = LibraryDir(library);
            var config = LibraryConfigLoader.Load(LibraryConfigPath(library));
            // Pack ALL non-internal products in library.json order — the
            // bash loop is unconditional. --products / --all-products are
            // intentionally NOT honored here so that release packs are
            // never partial.
            var packed = new List<AbsolutePath>();
            var outputDir = PackOutputDir;
            outputDir.CreateDirectory();

            Log.Information("Pack {Library}: configuration={Config}, version={Version}, output={Output}",
                library, Configuration, Version ?? "(from csproj)", outputDir);

            foreach (var product in config.Products)
            {
                if (product.Internal)
                    continue;

                var csproj = product.CsprojPath(libraryDir);
                Log.Information("=== Packing {Csproj} ===", Path.GetRelativePath(RootDirectory, csproj));

                var args = new List<string>
                {
                    "pack", (string)csproj,
                    "--no-build",
                    "-c", Configuration,
                    "-o", (string)outputDir,
                };
                if (!string.IsNullOrEmpty(Version))
                    args.Add($"/p:Version={Version}");

                var exit = RunDotnet(args);
                if (exit != 0)
                    throw new InvalidOperationException(
                        $"dotnet pack failed (exit {exit}) for {csproj}");

                // Discover the produced .nupkg by csproj basename. This works
                // because every csproj in the repo sets <PackageId> to its own
                // file basename (e.g. SwiftBindings.Stripe.Core.csproj has
                // <PackageId>SwiftBindings.Stripe.Core</PackageId>). dotnet pack
                // names the output file `{PackageId}.{Version}.nupkg`, so the
                // basename suffices as long as that invariant holds. If a
                // future csproj overrides <PackageId> independently of its
                // filename, the GlobFiles match below will return zero entries
                // and the InvalidOperationException will fire — easier to
                // diagnose than a silent mismatch.
                var packageId = Path.GetFileNameWithoutExtension((string)csproj);
                var nupkg = outputDir.GlobFiles($"{packageId}.*.nupkg")
                    .OrderByDescending(f => File.GetLastWriteTimeUtc(f))
                    .FirstOrDefault();
                if (nupkg is null)
                    throw new InvalidOperationException(
                        $"dotnet pack succeeded but no {packageId}.*.nupkg found in {outputDir}");
                packed.Add(nupkg);
                Log.Information("  → {Nupkg}", Path.GetFileName((string)nupkg));
            }

            Log.Information("Pack complete: {Count} package(s) in {Output}", packed.Count, outputDir);
        });

    /// <summary>
    /// CI-only sanity-pack — every non-internal product is packed with the
    /// throwaway version <c>0.0.0-ci</c> into a temp dir, just to confirm
    /// <c>dotnet pack --no-build</c> succeeds. Replaces the <c>Validate
    /// packaging</c> step at <c>ci.yml:147–162</c> of the post-Session-3 file.
    /// Output goes to <c>artifacts/ci-packages/</c> by default; CI typically
    /// passes <c>--output ${{ runner.temp }}/ci-packages</c>.
    /// </summary>
    Target PackValidate => _ => _
        .Description("Pack with version 0.0.0-ci to validate packaging (CI smoke check)")
        .Requires(() => Library)
        .Executes(() =>
        {
            var library = Library!;
            var libraryDir = LibraryDir(library);
            var config = LibraryConfigLoader.Load(LibraryConfigPath(library));
            var outputDir = string.IsNullOrEmpty(Output)
                ? RootDirectory / "artifacts" / "ci-packages"
                : (AbsolutePath)Path.GetFullPath(Output);
            outputDir.CreateDirectory();

            Log.Information("PackValidate {Library}: config={Config}, output={Output}",
                library, Configuration, outputDir);

            var anyPacked = false;
            foreach (var product in config.Products)
            {
                if (product.Internal)
                    continue;

                var csproj = product.CsprojPath(libraryDir);
                Log.Information("=== Validating pack: {Csproj} ===", Path.GetRelativePath(RootDirectory, csproj));

                // -c Configuration must match the prior `dotnet build` pass —
                // dotnet pack --no-build resolves bin/$(Configuration)/.../*.dll,
                // and CI's BuildLibrary step uses the global default (Debug). If
                // anyone runs `./build.sh PackValidate --library X --configuration Release`
                // without first re-running BuildLibrary at Release, pack will
                // fail loudly rather than silently picking up Debug artifacts.
                var exit = RunDotnet(new[]
                {
                    "pack", (string)csproj,
                    "--no-build",
                    "-c", Configuration,
                    "/p:Version=0.0.0-ci",
                    "-o", (string)outputDir,
                });
                if (exit != 0)
                    throw new InvalidOperationException(
                        $"dotnet pack (validate) failed (exit {exit}) for {csproj}");
                anyPacked = true;
            }

            if (!anyPacked)
                Log.Warning("PackValidate: no non-internal products to pack — nothing validated.");
            else
                Log.Information("PackValidate complete.");
        });
}
