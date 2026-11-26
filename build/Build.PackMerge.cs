using System.Diagnostics;
using System.IO.Compression;
using Nuke.Common;
using Nuke.Common.IO;
using Serilog;
using SwiftBindings.Build.Helpers;
using SwiftBindings.Build.Models;

/// <summary>
/// One package per binding, carrying one copy per .NET + Xcode pairing. Each pairing packs its
/// own copy (a "contribution": the same package id and version, with that pairing's
/// <c>lib/</c>, <c>buildTransitive/</c> and <c>native/</c> folders under its labelled TFMs), and
/// the SDK's <c>SwiftBindingsMergePackages</c> target folds the contributions into the one
/// package that ships. The merge refuses copies whose public API or compiled-in values
/// disagree; a refusal is a finding, never something to suppress.
///
/// <para>Layout used by both targets and by CI:
/// <c>&lt;root&gt;/contributions/&lt;pairing&gt;/&lt;Id&gt;.&lt;Version&gt;.nupkg</c> in,
/// <c>&lt;root&gt;/merged/&lt;Id&gt;.&lt;Version&gt;.nupkg</c> out.</para>
/// </summary>
partial class Build
{
    [Parameter("MergePackages: directory holding one <pairing>/ folder of packed contributions per pairing " +
               "(default: <output>/contributions)")]
    readonly string? Contributions;

    AbsolutePath MergeRoot => string.IsNullOrEmpty(Output)
        ? RootDirectory / "artifacts" / "merged-packages"
        : (AbsolutePath)Path.GetFullPath(Output);

    AbsolutePath ContributionsRoot => string.IsNullOrEmpty(Contributions)
        ? MergeRoot / "contributions"
        : (AbsolutePath)Path.GetFullPath(Contributions);

    /// <summary>
    /// Local equivalent of the CI pack matrix + merge job: BuildAndPackRelease on every pairing
    /// (each in a child process under <c>scripts/use-toolchain.sh</c>), then MergePackages.
    /// </summary>
    Target PackAllPairings => _ => _
        .Description("Pack a library on every pairing and merge the copies into one package per id " +
                     "(--library X --version V [--output dir]; contributions in <output>/contributions/<pairing>, " +
                     "merged packages in <output>/merged)")
        .Requires(() => Library, () => Version)
        .Executes(() =>
        {
            var library = Library!;
            var version = Version!;
            foreach (var pairing in ToolchainPairing.All)
            {
                var outDir = ContributionsRoot / pairing;
                outDir.CreateOrCleanDirectory();
                var exit = RunOnPairing(pairing, new[]
                {
                    "BuildAndPackRelease", "--library", library, "--version", version,
                    "--output", (string)outDir, "--dry-run", "true",
                });
                if (exit != 0)
                    throw new InvalidOperationException(
                        $"PackAllPairings: BuildAndPackRelease for {library} failed on {pairing} (exit {exit}); nothing was merged.");
            }
            MergeLibraryPackages(library, version, ContributionsRoot, MergeRoot / "merged");
        });

    /// <summary>
    /// Merge one library's per-pairing contributions (the CI merge job runs this after the
    /// per-pairing pack jobs). Every pairing must contribute every package id: a package with a
    /// pairing missing would ship without that copy and silently serve a consumer an older one.
    /// </summary>
    Target MergePackages => _ => _
        .Description("Merge per-pairing contributions into one package per id " +
                     "(--library X --version V [--contributions dir] [--output dir])")
        .Requires(() => Library, () => Version)
        .Executes(() => MergeLibraryPackages(Library!, Version!, ContributionsRoot, MergeRoot / "merged"));

    void MergeLibraryPackages(string library, string version, AbsolutePath contributionsRoot, AbsolutePath outputDir)
    {
        outputDir.CreateDirectory();
        var (_, libDir) = ResolveLibrary(library);
        var csprojs = Directory.EnumerateFiles(libDir, "SwiftBindings.*.csproj", SearchOption.AllDirectories)
            .Where(p => !p.Contains("/tests", StringComparison.Ordinal) && !p.Contains("/obj/", StringComparison.Ordinal)
                        && !p.Contains("/bin/", StringComparison.Ordinal))
            .ToDictionary(p => Path.GetFileNameWithoutExtension(p), p => p, StringComparer.Ordinal);

        // The expected matrix comes from the source, never from the contributions being checked:
        // one package per package csproj (id = csproj name), each carrying one copy per pairing
        // of every platform its csproj declares. NuGet drops build metadata from file names.
        var fileVersion = NormalizePackageVersion(version);
        var pairings = ToolchainPairing.All.Select(p => ToolchainPairing.Load(RootDirectory / "toolchains", p)).ToList();
        var ids = csprojs.Keys.ToHashSet(StringComparer.Ordinal);
        foreach (var pairing in ToolchainPairing.All)
        {
            var packed = PackageIdsIn(contributionsRoot / pairing, fileVersion);
            if (!packed.SetEquals(ids))
                throw new InvalidOperationException(
                    $"MergePackages: {pairing} contributes [{string.Join(", ", packed.Order())}] but {library} packs " +
                    $"[{string.Join(", ", ids.Order())}]; every pairing must pack every package.");
        }

        EnsureMergeNativeInputs(library, csprojs.Values);

        var refused = new List<string>();
        foreach (var id in ids.Order(StringComparer.Ordinal))
        {
            var csproj = csprojs[id];
            var parts = ToolchainPairing.All
                .Select(p => contributionsRoot / p / $"{id}.{fileVersion}.nupkg")
                .ToList();
            var platforms = DeclaredPlatforms(csproj);
            var tfms = pairings.SelectMany(p => platforms.Select(p.CopyTfm)).ToList();
            var carried = parts.SelectMany(LibTfms).ToHashSet(StringComparer.Ordinal);
            if (!carried.SetEquals(tfms))
            {
                refused.Add($"{id}: contributions carry [{string.Join(";", carried.Order())}], the csproj and pairings " +
                            $"require [{string.Join(";", tfms)}]");
                continue;
            }
            Log.Information("=== Merging {Id} {Version}: {Count} copies [{Tfms}] ===", id, version, tfms.Count, string.Join(";", tfms));
            var exit = RunDotnet(new[]
            {
                "msbuild", csproj, "-restore", "-nodeReuse:false",
                "-t:SwiftBindingsMergePackages",
                $"-p:Version={version}",
                $"-p:SwiftBindingsMergeContributions=\"{string.Join(";", parts.Select(p => (string)p))}\"",
                $"-p:SwiftBindingsMergeTargetFrameworks=\"{string.Join(";", tfms)}\"",
                $"-p:SwiftBindingsMergeOutput={outputDir}",
            });
            // The SDK names the merged file from the nuspec version, which keeps build metadata;
            // give it NuGet's normalized file name, which upload and ReleaseConsume look for.
            var merged = outputDir / $"{id}.{fileVersion}.nupkg";
            var raw = outputDir / $"{id}.{version.Trim()}.nupkg";
            if (exit == 0 && !File.Exists(merged) && File.Exists(raw))
                File.Move(raw, merged);
            if (exit != 0 || !File.Exists(merged))
                refused.Add($"{id}: merge exit {exit}{(File.Exists(merged) ? "" : ", no merged package")}");
            else
                Log.Information("  → {Merged}", merged);
        }

        if (refused.Count > 0)
            throw new InvalidOperationException(
                $"MergePackages: {refused.Count} package(s) of {library} did not merge — a merge refusal is a " +
                "finding, not something to suppress:\n  " + string.Join("\n  ", refused));
        Log.Information("MergePackages {Library}: {Count} merged package(s) in {Dir}", library, ids.Count, outputDir);
    }

    /// <summary>
    /// The SDK merge resolves each project's references, which runs binding generation, so the
    /// library's xcframeworks (and those of any library it ProjectReferences) must exist. A clean
    /// checkout (the CI merge job) has none.
    /// </summary>
    void EnsureMergeNativeInputs(string library, IEnumerable<string> csprojs) =>
        EnsureNativeInputs(new[] { library }.Concat(CrossLibraryReferences(csprojs)));

    /// <summary>
    /// Make every listed third-party library's xcframeworks present and readable by the active
    /// pairing's Swift compiler, building them when they are not. The xcframeworks live once on
    /// disk for all pairings: one built by Xcode 27 (Swift 6.4) writes module interfaces that
    /// Xcode 26.3 cannot parse, so a pairing whose compiler is older than the one that built a
    /// source-mode library rebuilds it (an older interface reads fine in a newer compiler, so the
    /// oldest pairing's build serves them all). Vendor-built (zip/binary/manual) xcframeworks are
    /// only checked for presence: rebuilding cannot change what the vendor shipped.
    /// Apple frameworks bind the system framework and need nothing.
    /// </summary>
    void EnsureNativeInputs(IEnumerable<string> libraries)
    {
        Version? swift = null;
        var swiftProbed = false;
        foreach (var lib in libraries.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            var configPath = RootDirectory / "libraries" / lib / "library.json";
            if (!File.Exists(configPath))
                continue;
            var config = LibraryConfigLoader.Load(configPath);
            var xcframeworks = config.ResolveProducts(Array.Empty<string>(), allProducts: true)
                .Select(p => RootDirectory / "libraries" / lib / (p.Subdirectory ?? "") / $"{p.Framework}.xcframework")
                .ToList();
            var missing = xcframeworks.Count(p => !Directory.Exists(p));
            var newer = new List<string>();
            if (missing == 0 && config.Mode == BuildMode.Source)
            {
                if (!swiftProbed) { swift = ActiveSwiftVersion(); swiftProbed = true; }
                if (swift is not null)
                    newer = xcframeworks
                        .Select(p => (Path: p, Built: InterfaceCompilerVersion(p)))
                        .Where(x => x.Built is not null && x.Built > swift)
                        .Select(x => $"{Path.GetFileName(x.Path)} (Swift {x.Built})")
                        .ToList();
            }
            if (missing == 0 && newer.Count == 0)
                continue;
            Log.Information("Native inputs: building {Library}'s xcframeworks ({Why})", lib,
                missing > 0 ? $"{missing} missing" : $"built by a newer compiler than this pairing's Swift {swift}: {string.Join(", ", newer)}");
            BuildXcframeworkForLibrary(lib, Array.Empty<string>(), allProducts: true);
        }
    }

    /// <summary>Libraries (under <c>libraries/</c>) the given csprojs ProjectReference.</summary>
    IEnumerable<string> CrossLibraryReferences(IEnumerable<string> csprojs)
    {
        foreach (var csproj in csprojs)
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(
                         File.ReadAllText(csproj), @"<ProjectReference\s+Include=""(?<p>[^""]+)"""))
            {
                var target = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(csproj)!, m.Groups["p"].Value));
                var rel = Path.GetRelativePath(RootDirectory / "libraries", target);
                if (!rel.StartsWith("..", StringComparison.Ordinal))
                    yield return rel.Split(Path.DirectorySeparatorChar)[0];
            }
    }

    /// <summary>The active Xcode's Swift compiler version (major.minor), or null when unknown.</summary>
    static Version? ActiveSwiftVersion() =>
        ParseSwiftVersion(TryCapture("xcrun", new[] { "swift", "--version" }), "Apple Swift version ");

    /// <summary>The Swift version (major.minor) that wrote an xcframework's module interfaces.</summary>
    static Version? InterfaceCompilerVersion(AbsolutePath xcframework)
    {
        var iface = Directory.Exists(xcframework)
            ? Directory.EnumerateFiles(xcframework, "*.swiftinterface", SearchOption.AllDirectories)
                .FirstOrDefault(f => !f.EndsWith(".private.swiftinterface", StringComparison.Ordinal))
            : null;
        if (iface is null)
            return null;
        using var reader = new StreamReader(iface);
        for (var i = 0; i < 5 && reader.ReadLine() is { } line; i++)
            if (line.StartsWith("// swift-compiler-version:", StringComparison.Ordinal))
                return ParseSwiftVersion(line, "Apple Swift version ");
        return null;
    }

    internal static Version? ParseSwiftVersion(string? text, string marker)
    {
        var at = text?.IndexOf(marker, StringComparison.Ordinal) ?? -1;
        if (at < 0)
            return null;
        var m = System.Text.RegularExpressions.Regex.Match(text![(at + marker.Length)..], @"^(\d+)\.(\d+)");
        return m.Success ? new Version(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value)) : null;
    }

    /// <summary>The platforms a package csproj builds, read from its pairing tokens
    /// (<c>$(SwiftBindingsTfmIos)</c>…). A literal TFM is refused: it would carry no pairing label.</summary>
    static IReadOnlyList<string> DeclaredPlatforms(string csproj)
    {
        var m = System.Text.RegularExpressions.Regex.Match(File.ReadAllText(csproj),
            @"<TargetFrameworks?>(?<v>[^<]*)</TargetFrameworks?>");
        var raw = m.Success ? m.Groups["v"].Value : "$(SwiftBindingsTfmIos)";
        return raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => t switch
            {
                "$(SwiftBindingsTfmIos)" => "ios",
                "$(SwiftBindingsTfmMacos)" => "macos",
                "$(SwiftBindingsTfmMaccatalyst)" => "maccatalyst",
                "$(SwiftBindingsTfmTvos)" => "tvos",
                _ => throw new InvalidOperationException(
                    $"MergePackages: {csproj} declares '{t}'; package TFMs must come from the pairing tokens."),
            })
            .ToList();
    }

    static HashSet<string> PackageIdsIn(AbsolutePath dir, string version)
    {
        var suffix = $".{version}.nupkg";
        return Directory.Exists(dir)
            ? Directory.EnumerateFiles(dir, $"*{suffix}")
                .Select(Path.GetFileName)
                .Select(n => n![..^suffix.Length])
                .ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);
    }

    /// <summary>The <c>lib/&lt;tfm&gt;/</c> folders of a packed contribution: exactly the copies it carries.</summary>
    static IEnumerable<string> LibTfms(AbsolutePath nupkg)
    {
        if (!File.Exists(nupkg))
            throw new InvalidOperationException($"MergePackages: missing contribution {nupkg}");
        using var zip = ZipFile.OpenRead(nupkg);
        return zip.Entries
            .Select(e => e.FullName.Split('/'))
            .Where(parts => parts.Length > 2 && parts[0] == "lib")
            .Select(parts => parts[1])
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// A child Nuke process opens <c>.nuke/temp/build.log</c> exclusively, as this process
    /// already has. Before the first child starts, close this process's file sink and keep
    /// logging to the console only; the children write their own build.log.
    /// </summary>
    static void ReleaseBuildLogForChildren()
    {
        if (_buildLogReleased) return;
        _buildLogReleased = true;
        Log.CloseAndFlush();
        Log.Logger = new Serilog.LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.Console(outputTemplate: "{Timestamp:HH:mm:ss} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();
    }

    static bool _buildLogReleased;

    /// <summary>Run this build's own entry point on another pairing (a child process under
    /// <c>scripts/use-toolchain.sh</c>), streaming its output.</summary>
    int RunOnPairing(string pairing, IEnumerable<string> nukeArgs)
    {
        ReleaseBuildLogForChildren();
        var psi = new ProcessStartInfo("bash")
        {
            WorkingDirectory = RootDirectory,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add(RootDirectory / "scripts" / "use-toolchain.sh");
        psi.ArgumentList.Add(pairing);
        psi.ArgumentList.Add("--");
        psi.ArgumentList.Add("dotnet");
        psi.ArgumentList.Add(typeof(Build).Assembly.Location);
        foreach (var a in nukeArgs) psi.ArgumentList.Add(a);
        psi.Environment.Remove(ToolchainPairing.EnvironmentVariable);
        Log.Information("→ {Pairing}: {Args}", pairing, string.Join(' ', psi.ArgumentList));
        using var proc = Process.Start(psi)!;
        proc.WaitForExit();
        return proc.ExitCode;
    }
}
