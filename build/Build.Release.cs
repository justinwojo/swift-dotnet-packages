using System.Text.Json;
using System.Text.RegularExpressions;
using Nuke.Common;
using Nuke.Common.IO;
using Serilog;
using SwiftBindings.Build.Helpers;
using SwiftBindings.Build.Models;

partial class Build
{
    /// <summary>
    /// Strict semver tag regex from <c>release.yml:50</c>:
    /// <c>&lt;Library&gt;/&lt;major&gt;.&lt;minor&gt;.&lt;patch&gt;[-+suffix]</c>.
    /// Anchored, no whitespace. Mirrored exactly so existing tags keep parsing.
    /// </summary>
    static readonly Regex ReleaseTagRegex = new(
        @"^(?<library>[A-Za-z0-9._-]+)/(?<version>[0-9]+\.[0-9]+\.[0-9]+([-+].*)?)$",
        RegexOptions.Compiled);

    /// <summary>
    /// Output directory for <see cref="BuildAndPackRelease"/>'s manifest +
    /// .nupkg files. Distinct from <see cref="PackOutputDir"/> only because
    /// the release flow conventionally uses <c>${{ runner.temp }}/packages</c>.
    /// Falls back to <c>artifacts/release/</c> when not set.
    /// </summary>
    AbsolutePath ReleaseOutputDir => string.IsNullOrEmpty(Output)
        ? RootDirectory / "artifacts" / "release"
        : (AbsolutePath)Path.GetFullPath(Output);

    [Parameter("Mark this release as a dry run (build + pack, skip publish)")]
    readonly bool DryRun;

    [Parameter("Directory containing .nupkg files for PublishRelease (artifact-staged)")]
    readonly string? PackagesDir;

    /// <summary>
    /// Result of parsing a <c>Library/version</c> release tag. Mirrors
    /// the <c>steps.parse</c> outputs in <c>release.yml:40–63</c>.
    /// </summary>
    public sealed record ReleaseTag(string Library, string Version, string Tag);

    /// <summary>
    /// Strict-port of the <c>release.yml:50</c> tag parser. Same diagnostic
    /// shape so the failure mode is grep-stable. Used by
    /// <see cref="BuildAndPackRelease"/> when the workflow chooses to consolidate
    /// tag parsing into the Nuke layer (the workflow can also keep the parse
    /// in YAML and pass <c>--library</c> + <c>--version</c> directly).
    /// </summary>
    public static ReleaseTag ParseReleaseTag(string tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
            throw new InvalidOperationException(
                "Invalid tag format:  (expected <Library>/<semver>, e.g. Nuke/12.8.0)");

        var m = ReleaseTagRegex.Match(tag);
        if (!m.Success)
            throw new InvalidOperationException(
                $"Invalid tag format: {tag} (expected <Library>/<semver>, e.g. Nuke/12.8.0)");

        return new ReleaseTag(m.Groups["library"].Value, m.Groups["version"].Value, tag);
    }

    /// <summary>
    /// Build + pack one library for release. Replaces <c>release.yml:120–230</c>
    /// (everything from "Build library" through "Generate release manifest")
    /// with one Nuke target. Runs in the macOS <c>build</c> job; the <c>publish</c>
    /// job consumes the artifact directory this target produces.
    ///
    /// <para>Chain (no DependsOn — manual sequencing so we can fail fast on
    /// missing parameters before kicking off a 30-minute Stripe build):</para>
    /// <list type="number">
    ///   <item><c>BuildLibrary --configuration Release --all-products</c></item>
    ///   <item><c>Pack --configuration Release</c></item>
    ///   <item>Generate <c>release-manifest.json</c> (same shape as
    ///         <c>release.yml:201–229</c>).</item>
    /// </list>
    ///
    /// <para>
    /// <c>--configuration Release</c> is hard-coded — passing Debug here
    /// would silently produce broken NuGet packages because <c>dotnet pack
    /// --no-build</c> would resolve <c>bin/Debug/.../*.dll</c> instead of
    /// <c>bin/Release/.../*.dll</c>. The release pack step in the prior bash
    /// flow had the same constraint at <c>release.yml:124–125</c>; the
    /// design doc calls this out as the single most likely cutover regression.
    /// </para>
    /// </summary>
    Target BuildAndPackRelease => _ => _
        .Description("Build + pack a library for release (--library Foo --version 1.2.3 [--output dir] [--dry-run])")
        .Requires(() => Library, () => Version)
        .Executes(() =>
        {
            var library = Library!;
            var version = Version!;
            var outputDir = ReleaseOutputDir;
            outputDir.CreateDirectory();

            Log.Information("=== BuildAndPackRelease {Library} v{Version} (dry_run={DryRun}) ===",
                library, version, DryRun);

            // Dispatch on kind: third-party libraries follow the multi-product
            // library.json flow; Apple frameworks are single-csproj, no
            // library.json, no xcframework build (the SDK reads the system
            // framework target directly).
            var (kind, dir) = ResolveLibrary(library);
            if (kind == LibraryKind.AppleFramework)
            {
                BuildAndPackAppleFramework(dir, version, outputDir);
            }
            else
            {
                // Step 1: full build at Release.
                // We invoke the implementation directly rather than going
                // through the Nuke target graph because we need to override
                // the Configuration parameter in this scope. The runtime
                // Configuration field stays at Debug (its declared default)
                // unless the caller passes --configuration Release explicitly,
                // so we forward it via a local helper that mirrors what the
                // BuildLibrary target's executor does.
                BuildLibraryAtConfiguration(library, "Release");

                // Step 2: pack at Release into the release output dir.
                // Same Configuration override note: pack --no-build needs to
                // see Release-built artifacts.
                PackAtConfiguration(library, "Release", version, outputDir);
            }

            // Step 3: generate release-manifest.json — exact JSON shape
            // from release.yml:219–228, including the boolean dry_run and
            // ISO-8601 UTC timestamp.
            GenerateReleaseManifest(library, version, outputDir, DryRun);

            Log.Information("=== BuildAndPackRelease complete: {Output} ===", outputDir);
        });

    /// <summary>
    /// Apple-framework variant of the third-party build+pack pair. Single
    /// <c>dotnet build -c Release</c> (the SDK handles binding generation
    /// from <c>SwiftAppleFrameworkTarget</c> in one shot, all TFMs at once),
    /// then <c>dotnet pack --no-build</c> with the version override.
    /// </summary>
    static void BuildAndPackAppleFramework(AbsolutePath dir, string version, AbsolutePath outputDir)
    {
        var csproj = dir.GlobFiles("SwiftBindings.*.csproj").SingleOrDefault()
            ?? throw new InvalidOperationException(
                $"No SwiftBindings.*.csproj found in {dir}");

        Log.Information("BuildAndPackRelease (Apple framework): build {Csproj} -c Release", csproj);
        var buildExit = RunDotnet(new[] { "build", (string)csproj, "-c", "Release" });
        if (buildExit != 0)
            throw new InvalidOperationException($"dotnet build failed (exit {buildExit}) for {csproj}");

        Log.Information("BuildAndPackRelease (Apple framework): pack {Csproj} -p:Version={Version} → {Output}",
            csproj, version, outputDir);
        var packExit = RunDotnet(new[]
        {
            "pack", (string)csproj,
            "--no-build",
            "-c", "Release",
            $"/p:Version={version}",
            "-o", (string)outputDir,
        });
        if (packExit != 0)
            throw new InvalidOperationException($"dotnet pack failed (exit {packExit}) for {csproj}");
    }

    /// <summary>
    /// Push every <c>.nupkg</c> in <see cref="PackagesDir"/> to nuget.org.
    /// Replaces the bash loop at <c>release.yml:265–273</c>.
    ///
    /// <para>
    /// <b>No <c>DependsOn(BuildAndPackRelease)</c></b> — by design. The
    /// publish job runs on <c>ubuntu-latest</c> inside <c>environment:
    /// nuget-publish</c>, downloads the artifact produced by the macOS
    /// <c>build</c> job, and invokes this target standalone. Coupling the
    /// targets would force the publish job to also rebuild on every
    /// invocation and would defeat the environment-gate isolation that
    /// keeps <c>NUGET_API_KEY</c> off the macOS runner.
    /// </para>
    ///
    /// <para>
    /// Uses <see cref="System.Threading.Tasks.Parallel.ForEachAsync"/> with
    /// <c>MaxDegreeOfParallelism = 5</c> to replace the sequential bash
    /// <c>for pkg in *.nupkg</c> loop. <c>--skip-duplicate</c> matches the
    /// bash flag at <c>release.yml:272</c>, so re-running a release with the
    /// same version is a no-op rather than a hard failure.
    /// </para>
    ///
    /// <para>
    /// <c>OnlyWhenStatic</c> on <c>!DryRun</c> preserves the
    /// <c>if: needs.build.outputs.dry_run != 'true'</c> gate from
    /// <c>release.yml:242</c>. Local invocations should pass <c>--dry-run</c>
    /// to skip the actual push.
    /// </para>
    /// </summary>
    Target PublishRelease => _ => _
        .Description("Push .nupkg files in --packages-dir to nuget.org (publish job)")
        .Requires(() => PackagesDir)
        .OnlyWhenStatic(() => !DryRun)
        .Executes(async () =>
        {
            if (string.IsNullOrEmpty(NuGetApiKey))
                throw new InvalidOperationException(
                    "NuGetApiKey is required for PublishRelease. Pass --nuget-api-key (or set NUGET_API_KEY env var).");

            var dir = (AbsolutePath)Path.GetFullPath(PackagesDir!);
            if (!Directory.Exists(dir))
                throw new InvalidOperationException($"Packages directory not found: {dir}");

            // Sort to give deterministic push ordering — useful when reading
            // CI logs to verify all expected packages were pushed. The
            // bash loop iterated `*.nupkg` glob order which is also sorted
            // by name on macOS/Linux, so we match.
            var packages = dir.GlobFiles("*.nupkg")
                .OrderBy(p => p.Name, StringComparer.Ordinal)
                .ToList();

            if (packages.Count == 0)
                throw new InvalidOperationException($"No .nupkg files found in {dir}");

            Log.Information("=== PublishRelease: pushing {Count} package(s) (parallelism=5) ===",
                packages.Count);
            foreach (var p in packages)
                Log.Information("  - {Name}", p.Name);

            // Parallelism degree=5 (mirror of the design-doc spec) — small
            // enough not to overwhelm nuget.org but parallel enough to
            // close the gap on the sequential bash loop.
            await Parallel.ForEachAsync(
                packages,
                new ParallelOptions { MaxDegreeOfParallelism = 5 },
                async (pkg, ct) =>
                {
                    Log.Information("Pushing {Name}...", pkg.Name);
                    var exit = await Task.Run(() => RunDotnet(new[]
                    {
                        "nuget", "push", (string)pkg,
                        "--source", "https://api.nuget.org/v3/index.json",
                        "--api-key", NuGetApiKey!,
                        "--skip-duplicate",
                    }), ct);
                    if (exit != 0)
                        throw new InvalidOperationException(
                            $"dotnet nuget push failed (exit {exit}) for {pkg.Name}");
                });

            Log.Information("=== PublishRelease complete: {Count} package(s) pushed ===", packages.Count);
        });

    // ── Implementation helpers ──────────────────────────────────────────────

    /// <summary>
    /// Run <see cref="BuildLibraryEndToEnd"/> with an explicit configuration
    /// override. The Configuration parameter is read-only on the Build
    /// instance, so we re-implement the per-pass dotnet build invocations
    /// here with the explicit config rather than mutating shared state.
    /// </summary>
    void BuildLibraryAtConfiguration(string library, string configuration)
    {
        var libraryDir = LibraryDir(library);
        var config = LibraryConfigLoader.Load(LibraryConfigPath(library));
        // BuildAndPackRelease always builds the full product set — releases
        // are never partial.
        var selected = config.ResolveProducts(Array.Empty<string>(), allProducts: true);

        Log.Information("BuildAndPackRelease: BuildLibrary {Library} -c {Config} --all-products",
            library, configuration);

        // Step 1: xcframeworks.
        BuildXcframeworkForLibrary(library, Array.Empty<string>(), allProducts: true);

        var nonInternalCount = config.Products.Count(p => !p.Internal);
        var multiProduct = nonInternalCount > 1;

        if (!multiProduct)
        {
            DotNetBuildSelectedAtConfiguration(libraryDir, selected, configuration, tolerateFailure: false);
            return;
        }

        // Multi-product two-pass orchestration with the explicit config.
        InjectFrameworkDepsForLibrary(library, Array.Empty<string>(), allProducts: true);
        Log.Information("=== Pass 1: dotnet build -c {Config} (wrapper failures tolerated) ===", configuration);
        DotNetBuildSelectedAtConfiguration(libraryDir, selected, configuration, tolerateFailure: true);

        InjectProjectRefsForLibrary(library, Array.Empty<string>(), allProducts: true, configurationOverride: configuration);
        Log.Information("=== Pass 2: dotnet build -c {Config} (must succeed) ===", configuration);
        DotNetBuildSelectedAtConfiguration(libraryDir, selected, configuration, tolerateFailure: false);
    }

    /// <summary>
    /// Configuration-overriding sibling of <see cref="DotNetBuildSelected"/>.
    /// </summary>
    static void DotNetBuildSelectedAtConfiguration(
        AbsolutePath libraryDir,
        IReadOnlyList<SwiftBindings.Build.Models.Product> selected,
        string configuration,
        bool tolerateFailure)
    {
        foreach (var product in selected)
        {
            if (product.Internal)
                continue;

            var csproj = product.CsprojPath(libraryDir);
            Log.Information("dotnet build -c {Config} {Csproj}", configuration, csproj);

            var exit = RunDotnet(new[] { "build", (string)csproj, "-c", configuration });
            if (exit != 0)
            {
                if (tolerateFailure)
                    Log.Warning("Pass 1: {Csproj} build failed (expected for multi-pass)", csproj);
                else
                    throw new InvalidOperationException(
                        $"dotnet build failed (exit {exit}) for {csproj}");
            }
        }
    }

    /// <summary>
    /// Configuration-overriding pack helper. Mirrors the per-csproj loop
    /// inside <see cref="Pack"/>, but with the configuration baked in so
    /// the release path can build at Release without flipping the global
    /// <see cref="Configuration"/> field.
    /// </summary>
    void PackAtConfiguration(string library, string configuration, string version, AbsolutePath outputDir)
    {
        var libraryDir = LibraryDir(library);
        var config = LibraryConfigLoader.Load(LibraryConfigPath(library));

        Log.Information("BuildAndPackRelease: Pack {Library} -c {Config} -p:Version={Version} → {Output}",
            library, configuration, version, outputDir);

        foreach (var product in config.Products)
        {
            if (product.Internal)
                continue;

            var csproj = product.CsprojPath(libraryDir);
            Log.Information("=== Packing {Csproj} ===", Path.GetRelativePath(RootDirectory, csproj));

            var exit = RunDotnet(new[]
            {
                "pack", (string)csproj,
                "--no-build",
                "-c", configuration,
                $"/p:Version={version}",
                "-o", (string)outputDir,
            });
            if (exit != 0)
                throw new InvalidOperationException(
                    $"dotnet pack failed (exit {exit}) for {csproj}");
        }
    }

    /// <summary>
    /// Generate <c>release-manifest.json</c> with the same shape the bash
    /// inline-Python at <c>release.yml:201–230</c> produces. Output is written
    /// to <c>{outputDir}/release-manifest.json</c> next to the <c>.nupkg</c>
    /// files; the artifact-upload step picks up the whole directory.
    /// </summary>
    void GenerateReleaseManifest(string library, string version, AbsolutePath outputDir, bool dryRun)
    {
        var nupkgs = outputDir.GlobFiles("*.nupkg")
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .ToList();

        var packages = nupkgs.Select(p => new
        {
            id = ExtractPackageId(p.Name),
            file = p.Name,
        }).ToList();

        var gitSha = Environment.GetEnvironmentVariable("GITHUB_SHA");
        if (string.IsNullOrEmpty(gitSha))
        {
            // Local fallback — match what the bash flow gets from the
            // GHA env var (which is unset locally).
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo("git", "rev-parse HEAD")
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                };
                using var p = System.Diagnostics.Process.Start(psi);
                gitSha = p?.StandardOutput.ReadToEnd().Trim() ?? "";
                p?.WaitForExit();
            }
            catch { gitSha = ""; }
        }

        var manifest = new
        {
            library,
            version,
            git_sha = gitSha,
            dry_run = dryRun,
            packages,
            timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ"),
        };

        var json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions
        {
            WriteIndented = true,
        });

        var manifestPath = outputDir / "release-manifest.json";
        File.WriteAllText(manifestPath, json);
        Log.Information("Wrote {Path}", manifestPath);
        Log.Information("\n{Json}", json);
    }

    /// <summary>
    /// Mirror of the bash inline-Python at <c>release.yml:207–217</c> that
    /// extracts the package id from a <c>.nupkg</c> filename. The version
    /// stem starts at the first dot-separated segment whose leading char is
    /// numeric — everything before is the package id.
    /// </summary>
    static string ExtractPackageId(string nupkgName)
    {
        var stem = nupkgName.EndsWith(".nupkg", StringComparison.OrdinalIgnoreCase)
            ? nupkgName[..^".nupkg".Length]
            : nupkgName;
        var segments = stem.Split('.');
        for (var i = 0; i < segments.Length; i++)
        {
            if (segments[i].Length > 0 && char.IsDigit(segments[i][0]))
                return string.Join('.', segments[..i]);
        }
        // No version-looking segment found — return the whole stem (matches
        // the bash fallback at release.yml:216).
        return stem;
    }
}
