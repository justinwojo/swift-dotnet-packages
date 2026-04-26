using Nuke.Common;
using Nuke.Common.IO;
using Nuke.Common.Tooling;
using Serilog;
using SwiftBindings.Build.Helpers;
using SwiftBindings.Build.Models;

partial class Build
{
    /// <summary>
    /// Build the xcframework(s) for a single library. Reads <c>library.json</c>,
    /// dispatches on <see cref="BuildMode"/>, and (for source/binary) bundles
    /// all selected products into a SINGLE <c>spm-to-xcframework</c> invocation
    /// — preserving the bash wrapper's behaviour at <c>build-xcframework.sh:209–244</c>.
    /// </summary>
    Target BuildXcframework => _ => _
        .Description("Build the xcframework(s) for a single library (--library Foo [--products P1,P2 | --all-products])")
        .Requires(() => Library)
        .Executes(() => BuildXcframeworkForLibrary(Library!, ParseProductSubset(Products), AllProducts));

    /// <summary>
    /// Normalize a Nuke <c>string[]</c> parameter to support both Nuke's
    /// space-separated form (<c>--products A B C</c>) and the bash-compatible
    /// comma-separated form (<c>--products A,B,C</c>) that the existing
    /// <c>build-xcframework.sh</c> wrapper accepts.
    /// </summary>
    static string[] ParseProductSubset(string[] raw)
    {
        if (raw.Length == 0)
            return raw;
        return raw
            .SelectMany(p => p.Split(',', StringSplitOptions.RemoveEmptyEntries))
            .Select(p => p.Trim())
            .Where(p => p.Length > 0)
            .ToArray();
    }

    /// <summary>
    /// Build xcframeworks for every library under <c>libraries/</c>. Parallelism
    /// caps at 4 to keep the simultaneously-running spm-to-xcframework processes
    /// under control on developer machines and the GHA runner.
    /// </summary>
    Target BuildAllXcframeworks => _ => _
        .Description("Build the xcframework(s) for every library in libraries/, in parallel (degreeOfParallelism=4)")
        .Executes(async () =>
        {
            var libs = DiscoverLibraries().ToList();
            Log.Information("BuildAllXcframeworks: {Count} libraries", libs.Count);

            await Parallel.ForEachAsync(
                libs,
                new ParallelOptions { MaxDegreeOfParallelism = 4 },
                async (lib, ct) =>
                {
                    await Task.Run(() => BuildXcframeworkForLibrary(lib, System.Array.Empty<string>(), allProducts: true), ct);
                });
        });

    // ── Core dispatch ───────────────────────────────────────────────────────

    void BuildXcframeworkForLibrary(string library, string[] requestedProducts, bool allProducts)
    {
        var libraryDir = LibraryDir(library);
        if (!Directory.Exists(libraryDir))
            throw new InvalidOperationException($"Library directory not found: {libraryDir}");

        var configPath = LibraryConfigPath(library);
        var config = LibraryConfigLoader.Load(configPath);
        var selected = config.ResolveProducts(requestedProducts, allProducts);

        Log.Information(
            "BuildXcframework {Library}: mode={Mode}, products=[{Products}]",
            library,
            config.Mode,
            string.Join(", ", selected.Select(p => p.Framework)));

        // config.Mode has been null-checked by LibraryConfigLoader.Load.
        switch (config.Mode!.Value)
        {
            case BuildMode.Source:
                BuildSource(libraryDir, config, selected);
                break;
            case BuildMode.Binary:
                BuildBinary(libraryDir, config, selected);
                break;
            case BuildMode.Zip:
                BuildZip(libraryDir, config, selected);
                break;
            case BuildMode.Manual:
                VerifyManual(libraryDir, selected);
                break;
        }

        Log.Information("=== Build complete: {Library} ===", library);
    }

    // ── Source mode ─────────────────────────────────────────────────────────

    void BuildSource(AbsolutePath libraryDir, LibraryConfig config, IReadOnlyList<Product> selected)
    {
        var tool = SpmToXcframeworkInstaller.EnsureInstalled(ToolsCacheDir);

        var workspace = libraryDir / ".build-workspace";
        var outputDir = workspace / "xcframeworks";
        workspace.CreateOrCleanDirectory();
        outputDir.CreateDirectory();

        // Walk selected products and emit --product/--target flags. The
        // useTarget field opts into spm-to-xcframework's SPM-target escape
        // hatch (see Stripe's 11 target-based modules).
        var args = new List<string>
        {
            config.Repository!,
            "--version", config.Version!,
            "--output", outputDir,
            "--min-ios", config.MinIOS,
        };
        foreach (var product in selected)
        {
            args.Add(product.UseTarget ? "--target" : "--product");
            args.Add(product.Framework);
        }
        if (!string.IsNullOrEmpty(config.Revision))
        {
            args.Add("--revision");
            args.Add(config.Revision);
        }

        Log.Information("=== Building source-mode xcframeworks via spm-to-xcframework ===");
        RunSpmToXcframework(tool, args);

        InstallProducts(libraryDir, outputDir, selected);

        workspace.DeleteDirectory();
    }

    // ── Binary mode ─────────────────────────────────────────────────────────

    void BuildBinary(AbsolutePath libraryDir, LibraryConfig config, IReadOnlyList<Product> selected)
    {
        var tool = SpmToXcframeworkInstaller.EnsureInstalled(ToolsCacheDir);

        // Tag SHA verification — must run in the wrapper because the upstream
        // tool's --binary planner does NOT call its own verify_revision. See
        // build-xcframework.sh:267–277 for the rationale.
        if (!string.IsNullOrEmpty(config.Revision))
        {
            Log.Information("=== Verifying tag '{Version}' resolves to {Revision} ===",
                config.Version, config.Revision);
            VerifyTagResolvesTo(config.Repository!, config.Version!, config.Revision);
            Log.Information("Revision verified.");
        }

        var workspace = libraryDir / ".build-workspace";
        var outputDir = workspace / "xcframeworks";
        workspace.CreateOrCleanDirectory();
        outputDir.CreateDirectory();

        // Refuse to silently ignore the legacy artifactPath disambiguation
        // field. The new tool's binary planner dedupes by product name (first
        // match wins) and exposes no per-product path override. Same loud
        // failure as build-xcframework.sh:294.
        var args = new List<string>
        {
            config.Repository!,
            "--version", config.Version!,
            "--binary",
            "--output", outputDir,
            "--min-ios", config.MinIOS,
        };
        foreach (var product in selected)
        {
            if (!string.IsNullOrEmpty(product.ArtifactPath))
                throw new InvalidOperationException(
                    $"Product '{product.Framework}' sets 'artifactPath', which is no longer supported. " +
                    "The pinned spm-to-xcframework binary planner dedupes by product name and has no " +
                    "per-product path override. Remove the field or file an upstream feature request.");
            // useTarget is source-mode-only — binary artifacts are always products.
            args.Add("--product");
            args.Add(product.Framework);
        }

        Log.Information("=== Building binary-mode xcframeworks via spm-to-xcframework ===");
        RunSpmToXcframework(tool, args);

        InstallProducts(libraryDir, outputDir, selected);

        workspace.DeleteDirectory();
    }

    // ── Zip mode ────────────────────────────────────────────────────────────

    /// <summary>
    /// Download a single release zip and extract per-product xcframeworks. Used for
    /// vendors who publish a bundle of xcframeworks on their GitHub release page —
    /// today only Stripe (<c>Stripe.xcframework.zip</c> contains all 14 product +
    /// internal xcframeworks at the archive root).
    /// </summary>
    void BuildZip(AbsolutePath libraryDir, LibraryConfig config, IReadOnlyList<Product> selected)
    {
        var url = config.ZipUrl!.Replace("{version}", config.Version!);

        var workspace = libraryDir / ".build-workspace";
        var outputDir = workspace / "xcframeworks";
        workspace.CreateOrCleanDirectory();
        outputDir.CreateDirectory();

        var zipPath = workspace / "release.zip";

        Log.Information("=== Downloading release zip: {Url} ===", url);
        DownloadFile(url, zipPath);

        Log.Information("=== Extracting {Zip} → {Out} ===", zipPath, outputDir);
        System.IO.Compression.ZipFile.ExtractToDirectory(zipPath, outputDir);

        // The Stripe zip lays xcframeworks out at the archive root. Confirm each
        // selected product's xcframework is present where InstallProducts will
        // look for it (outputDir/<framework>.xcframework).
        var missing = selected
            .Select(p => p.Framework)
            .Where(f => !Directory.Exists(outputDir / $"{f}.xcframework"))
            .ToList();
        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                $"Release zip from {url} did not contain expected xcframework(s): " +
                string.Join(", ", missing) + ". " +
                "Top-level entries in the extracted archive: " +
                string.Join(", ", Directory.EnumerateDirectories(outputDir).Select(Path.GetFileName)));
        }

        InstallProducts(libraryDir, outputDir, selected);

        workspace.DeleteDirectory();
    }

    /// <summary>
    /// Download a URL to a file path, streaming. Uses HttpClient with a 10-minute
    /// timeout — Stripe's release zip is ~75 MiB and finishes well under that on
    /// any reasonable connection.
    /// </summary>
    static void DownloadFile(string url, AbsolutePath destination)
    {
        using var http = new System.Net.Http.HttpClient
        {
            Timeout = TimeSpan.FromMinutes(10),
        };
        // GitHub release downloads accept any User-Agent, but reject anonymous
        // requests with no UA at all. Set one explicitly.
        http.DefaultRequestHeaders.UserAgent.ParseAdd("swift-dotnet-packages-build/1.0");

        using var response = http.GetAsync(url, System.Net.Http.HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
        response.EnsureSuccessStatusCode();

        using var src = response.Content.ReadAsStream();
        using var dst = File.Create(destination);
        src.CopyTo(dst);
    }

    // ── Manual mode ─────────────────────────────────────────────────────────

    void VerifyManual(AbsolutePath libraryDir, IReadOnlyList<Product> selected)
    {
        var missing = new List<AbsolutePath>();
        foreach (var product in selected)
        {
            var path = product.XcframeworkPath(libraryDir);
            if (Directory.Exists(path))
                Log.Information("Manual xcframework present: {Path}", path);
            else
                missing.Add(path);
        }

        if (missing.Count > 0)
        {
            // Same diagnostic shape as build-xcframework.sh:336–344.
            var sb = new System.Text.StringBuilder();
            sb.AppendLine();
            sb.AppendLine("Error: manual-mode library requires these xcframeworks to be provisioned:");
            foreach (var p in missing)
                sb.AppendLine($"  - {p}");
            sb.AppendLine();
            sb.AppendLine("Manual-mode xcframeworks are proprietary artifacts and are not committed");
            sb.AppendLine("to the repo. Download them from the vendor portal and place them at the");
            sb.AppendLine("paths above before running the build.");
            throw new InvalidOperationException(sb.ToString());
        }
    }

    // ── Install helper ──────────────────────────────────────────────────────

    /// <summary>
    /// Move each built xcframework from the workspace output directory into
    /// its final location under the library root, honoring <c>subdirectory</c>.
    /// Direct port of <c>install_products</c> in <c>build-xcframework.sh:177</c>.
    /// </summary>
    static void InstallProducts(
        AbsolutePath libraryDir,
        AbsolutePath outputDir,
        IReadOnlyList<Product> selected)
    {
        foreach (var product in selected)
        {
            var src = outputDir / $"{product.Framework}.xcframework";
            var dst = product.XcframeworkPath(libraryDir);

            if (!Directory.Exists(src))
                throw new InvalidOperationException(
                    $"Expected {src} after build, not found. The tool did not produce this product.");

            var targetDir = dst.Parent;
            targetDir.CreateDirectory();
            if (Directory.Exists(dst))
                dst.DeleteDirectory();
            Directory.Move(src, dst);
            Log.Information("Installed {Path}", dst);
        }
    }

    // ── Tool invocation ─────────────────────────────────────────────────────

    /// <summary>
    /// Run <c>spm-to-xcframework</c> with the given args. Stdout and stderr
    /// are streamed live to the Nuke log so vendor-side build output is
    /// visible during long source-mode builds.
    /// </summary>
    /// <remarks>
    /// Uses <see cref="System.Diagnostics.ProcessStartInfo.ArgumentList"/>
    /// directly so each argument is passed as a separate argv entry — no
    /// shell quoting, no string-roundtripping. This avoids subtle escaping
    /// bugs for paths containing spaces or quotes (the alternative,
    /// <c>ProcessTasks.StartProcess</c>, requires a pre-quoted command line
    /// string, which is brittle by construction).
    /// </remarks>
    static void RunSpmToXcframework(AbsolutePath tool, IEnumerable<string> args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = tool,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        Log.Information("> {Tool} {Args}", Path.GetFileName((string)tool), string.Join(' ', psi.ArgumentList));

        using var process = System.Diagnostics.Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start {tool}");

        // Drain both pipes asynchronously to avoid deadlocking on a full
        // stderr buffer (a 4KiB stderr write will block the child until we
        // read it). Forward each line straight to the Nuke logger.
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) Log.Information("{Line}", e.Data); };
        process.ErrorDataReceived  += (_, e) => { if (e.Data is not null) Log.Information("{Line}", e.Data); };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        // 1-hour cap matches the prior ProcessTasks timeout. Source-mode
        // builds for Stripe (~14 products) finish well under this.
        if (!process.WaitForExit((int)TimeSpan.FromHours(1).TotalMilliseconds))
        {
            try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
            throw new InvalidOperationException($"spm-to-xcframework timed out after 1 hour: {tool}");
        }

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"spm-to-xcframework exited with code {process.ExitCode}");
    }

    /// <summary>
    /// Mirror of the bash <c>git ls-remote</c> revision check at
    /// <c>build-xcframework.sh:268–276</c>. Tries the bare tag, the dereferenced
    /// tag, the <c>v</c>-prefixed tag, and its dereference. Uses the LAST line
    /// of output (the <c>^{}</c> dereferenced ref if present).
    ///
    /// <para>
    /// Matches the bash semantics exactly: git's exit code is ignored
    /// (<c>2&gt;/dev/null</c> in bash), and an empty output triggers the
    /// "tag not found" error rather than a "git failed" error. This matters
    /// because <c>git ls-remote</c> returns non-zero when no refs match, and
    /// we want the user to see the same diagnostic in either case.
    /// </para>
    /// </summary>
    static void VerifyTagResolvesTo(string repo, string version, string expectedSha)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("git")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("ls-remote");
        psi.ArgumentList.Add(repo);
        psi.ArgumentList.Add($"refs/tags/{version}");
        psi.ArgumentList.Add($"refs/tags/{version}^{{}}");
        psi.ArgumentList.Add($"refs/tags/v{version}");
        psi.ArgumentList.Add($"refs/tags/v{version}^{{}}");

        using var process = System.Diagnostics.Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start git");
        // Drain BOTH pipes — bash uses 2>/dev/null, but we redirected stderr
        // so we have to actively read it or git will block on a full stderr
        // pipe. Read stderr async; read stdout sync (it's the data we need).
        // Discard stderr to match the bash `2>/dev/null` semantics.
        process.ErrorDataReceived += (_, _) => { /* drain & discard */ };
        process.BeginErrorReadLine();
        var stdout = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        // Exit code intentionally ignored — bash uses `2>/dev/null` and only
        // checks the captured output.

        var lines = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .ToList();
        if (lines.Count == 0)
            throw new InvalidOperationException($"Tag '{version}' (or 'v{version}') not found in {repo}");

        // tail -1 | awk '{print $1}': last line, first whitespace-separated field.
        // The dereferenced tag (^{}) sorts after its base ref, so the last line
        // carries the actual commit SHA for annotated tags.
        var lastLine = lines[^1];
        var sha = lastLine.Split('\t', ' ', StringSplitOptions.RemoveEmptyEntries)[0];

        if (!string.Equals(sha, expectedSha, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Tag '{version}' resolves to {sha}, expected {expectedSha}");
    }

    // ── Library discovery ───────────────────────────────────────────────────

    IEnumerable<string> DiscoverLibraries()
    {
        if (!Directory.Exists(LibrariesDir))
            return Enumerable.Empty<string>();

        return Directory.EnumerateDirectories(LibrariesDir)
            .Where(d => File.Exists(Path.Combine(d, "library.json")))
            .Select(Path.GetFileName)
            .OfType<string>()
            .OrderBy(n => n, StringComparer.Ordinal);
    }
}
