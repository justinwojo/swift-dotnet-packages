using System.Text.RegularExpressions;
using Nuke.Common;
using Nuke.Common.IO;
using Serilog;
using SwiftBindings.Build.Helpers;
using SwiftBindings.Build.Models;

partial class Build
{
    /// <summary>
    /// Build a library end-to-end: xcframeworks, dependency injection passes,
    /// and the SDK-driven C# binding generation + compile.
    ///
    /// <para>
    /// Replaces the manual two-pass recipe documented in <c>CLAUDE.md</c>:
    /// </para>
    ///
    /// <code>
    /// Single product:
    ///   BuildXcframework → DotNetBuild
    /// Multi-product:
    ///   BuildXcframework
    ///     → InjectFrameworkDeps
    ///     → DotNetBuild  (pass 1, may fail on wrapper compile — tolerated)
    ///     → InjectProjectRefs
    ///     → DotNetBuild  (pass 2, must succeed)
    /// </code>
    ///
    /// <para>
    /// <c>--configuration</c> is honored by both <c>dotnet build</c> passes.
    /// The Session 4 release path requires this so that <c>dotnet pack
    /// --no-build -c Release</c> finds <c>bin/Release/.../*.dll</c>.
    /// </para>
    ///
    /// <para>
    /// <c>--clean-first</c> wipes <c>obj/&lt;Configuration&gt;/net10.0-ios/swift-binding/</c>
    /// for each selected product before pass 1.
    /// </para>
    /// </summary>
    Target BuildLibrary => _ => _
        .Description("Build a library: xcframeworks → inject → dotnet build (multi-pass for multi-product)")
        .Requires(() => Library)
        .Executes(() => BuildLibraryEndToEnd(Library!, ParseProductSubset(Products), AllProducts));

    /// <summary>
    /// Build every library under <c>libraries/</c> sequentially. Each library
    /// builds with <c>--all-products</c>, the same default the bash CI uses.
    /// </summary>
    Target BuildAllLibraries => _ => _
        .Description("Build every library in libraries/ end-to-end")
        .Executes(() =>
        {
            var libs = DiscoverLibraries().ToList();
            Log.Information("BuildAllLibraries: {Count} libraries", libs.Count);
            foreach (var lib in libs)
                BuildLibraryEndToEnd(lib, Array.Empty<string>(), allProducts: true);
        });

    void BuildLibraryEndToEnd(string library, string[] requestedProducts, bool allProducts)
    {
        var configPath = LibraryConfigPath(library);
        var config = LibraryConfigLoader.Load(configPath);
        var selected = config.ResolveProducts(requestedProducts, allProducts);

        // ── Step 0: cross-library xcframework prerequisites ──
        // The SDK generator's --framework-dependency flag is fed from
        // <SwiftFrameworkDependency Include="../X/X.xcframework">. When X is a
        // different top-level library, X's xcframework must exist on disk
        // before our pass-1 dotnet build runs (the generator opens its
        // .swiftinterface for cross-module type resolution). The single
        // cross-library edge in the repo today is BlinkIDUX → BlinkID; a CI
        // run that builds only BlinkIDUX previously failed because BlinkID's
        // xcframework was never materialized.
        BuildCrossLibraryXcframeworkPrerequisites(library);

        // ── Step 1: xcframeworks ──
        BuildXcframeworkForLibrary(library, requestedProducts, allProducts);

        // ── Step 2: --clean-first (optional) ──
        if (CleanFirst)
        {
            Log.Information("BuildLibrary {Library}: --clean-first → wiping swift-binding/ output", library);
            CleanSwiftBindingForLibrary(library, requestedProducts, allProducts);
        }

        // Multi-product = any selected product is non-internal AND there's
        // more than one non-internal product across the library. Single-
        // product libraries collapse to a one-shot dotnet build with no
        // injection.
        var nonInternalCount = config.Products.Count(p => !p.Internal);
        var multiProduct = nonInternalCount > 1;

        if (!multiProduct)
        {
            Log.Information("BuildLibrary {Library}: single-product, single-pass build", library);
            DotNetBuildSelected(library, selected);
            return;
        }

        // ── Step 3: Multi-product two-pass orchestration ──
        Log.Information("BuildLibrary {Library}: multi-product, two-pass build", library);

        // Pass 1: SwiftFrameworkDependency injection → first dotnet build.
        // The first build may fail on wrapper compile for products that
        // depend on yet-to-be-injected ProjectReferences — tolerated.
        InjectFrameworkDepsForLibrary(library, requestedProducts, allProducts);
        Log.Information("=== Pass 1: dotnet build (wrapper-compile failures tolerated) ===");
        DotNetBuildSelected(library, selected, tolerateFailure: true);

        // Pass 2: ProjectReference injection (freshness-checked) → final build.
        InjectProjectRefsForLibrary(library, requestedProducts, allProducts);
        Log.Information("=== Pass 2: dotnet build (must succeed) ===");
        DotNetBuildSelected(library, selected, tolerateFailure: false);
    }

    /// <summary>
    /// Run <c>dotnet build</c> for every selected non-internal product. Honors
    /// <see cref="Configuration"/>. <paramref name="tolerateFailure"/> turns
    /// non-zero exit codes into a warning instead of an exception (used for
    /// the first multi-product pass).
    /// </summary>
    void DotNetBuildSelected(string library, IReadOnlyList<Product> selected, bool tolerateFailure = false)
    {
        var libraryDir = LibraryDir(library);
        foreach (var product in selected)
        {
            if (product.Internal)
                continue;

            var csproj = product.CsprojPath(libraryDir);
            Log.Information("dotnet build -c {Config} {Csproj}", Configuration, Path.GetRelativePath(RootDirectory, csproj));

            var exit = RunDotnet(new[] { "build", (string)csproj, "-c", Configuration });
            if (exit != 0)
            {
                if (tolerateFailure)
                {
                    Log.Warning("Pass 1: {Csproj} build failed (expected for multi-pass)", csproj);
                }
                else
                {
                    throw new InvalidOperationException(
                        $"dotnet build failed (exit {exit}) for {csproj}");
                }
            }
        }
    }

    /// <summary>
    /// Invoke <c>dotnet</c> with the given args, streaming stdout + stderr to
    /// the Nuke log. Returns the process exit code (does NOT throw — callers
    /// decide whether non-zero is fatal).
    /// </summary>
    /// <remarks>
    /// Mirrors the <c>RunSpmToXcframework</c> pattern in
    /// <c>Build.Xcframework.cs</c> — uses <see cref="System.Diagnostics.ProcessStartInfo.ArgumentList"/>
    /// for hands-off argument escaping and async drains both pipes to avoid
    /// stderr-buffer deadlocks during long compiles.
    /// </remarks>
    static int RunDotnet(IEnumerable<string> args) => RunDotnet(args, onLine: null);

    /// <summary>
    /// Overload that routes child stdout/stderr through <paramref name="onLine"/>
    /// instead of the global <see cref="Log"/> sink. The parallel
    /// regression-validate harness uses this to tee each cell's output into a
    /// dedicated <c>/tmp/regression-cell-*.log</c> file so the main session log
    /// stays readable when N cells run concurrently.
    /// </summary>
    static int RunDotnet(IEnumerable<string> args, Action<string>? onLine)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "dotnet",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in args)
            psi.ArgumentList.Add(a);

        var argsLine = "> dotnet " + string.Join(' ', psi.ArgumentList);
        if (onLine is null) Log.Information("{Line}", argsLine);
        else onLine(argsLine);

        using var process = System.Diagnostics.Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start dotnet");

        if (onLine is null)
        {
            process.OutputDataReceived += (_, e) => { if (e.Data is not null) Log.Information("{Line}", e.Data); };
            process.ErrorDataReceived  += (_, e) => { if (e.Data is not null) Log.Information("{Line}", e.Data); };
        }
        else
        {
            // Per-cell sink: write under a lock so concurrent stdout/stderr
            // drains can't interleave half-lines in the tee file.
            process.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (onLine) onLine(e.Data); };
            process.ErrorDataReceived  += (_, e) => { if (e.Data is not null) lock (onLine) onLine(e.Data); };
        }
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        // 30-minute cap is generous for the slowest known csproj
        // (StripePaymentSheet ≈ 8 minutes on a clean cache).
        if (!process.WaitForExit((int)TimeSpan.FromMinutes(30).TotalMilliseconds))
        {
            try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
            throw new InvalidOperationException("dotnet build timed out after 30 minutes");
        }
        return process.ExitCode;
    }

    /// <summary>
    /// Matches a self-closing <c>&lt;SwiftFrameworkDependency Include="..." /&gt;</c>
    /// item. Same shape used by <c>CsprojRewriter</c>; the <c>Include</c> attribute
    /// is captured for resolution.
    /// </summary>
    static readonly Regex SfdIncludeRegex = new(
        @"<SwiftFrameworkDependency\b[^>]*?\bInclude\s*=\s*""([^""]+)""[^>]*?/>",
        RegexOptions.Singleline | RegexOptions.Compiled);

    /// <summary>
    /// Pre-build every cross-library xcframework <paramref name="library"/>
    /// depends on, in dependency-first order. Each prerequisite is built with
    /// <c>--all-products</c> so that any sibling xcframework the consumer
    /// might reference is materialized regardless of which specific product
    /// the SFD points at.
    /// </summary>
    void BuildCrossLibraryXcframeworkPrerequisites(string library)
    {
        var prereqs = ResolveCrossLibraryXcframeworkPrerequisites(library);
        if (prereqs.Count == 0)
            return;

        Log.Information("Cross-library xcframework prerequisites for {Library}: [{Prereqs}]",
            library, string.Join(", ", prereqs));

        foreach (var prereq in prereqs)
        {
            Log.Information("=== Pre-building cross-library prerequisite: {Prereq} ===", prereq);
            BuildXcframeworkForLibrary(prereq, Array.Empty<string>(), allProducts: true);
        }
    }

    /// <summary>
    /// Resolve the transitive cross-library xcframework prerequisites of
    /// <paramref name="library"/> by scanning every non-test
    /// <c>SwiftBindings.*.csproj</c> in the library tree for
    /// <c>&lt;SwiftFrameworkDependency Include="../X/..."&gt;</c> items where
    /// <c>X</c> resolves to a sibling library directory under
    /// <c>libraries/</c> (not the target itself, not intra-library product
    /// subdirectories).
    ///
    /// <para>
    /// Result is a deduped, post-order DFS list — dependencies appear before
    /// the libraries that depend on them. The target library itself is never
    /// included. Cycles aren't expected (the dep graph is depth-1 today:
    /// BlinkIDUX → BlinkID) but the visited-set guards make a cycle a no-op
    /// rather than an infinite loop.
    /// </para>
    ///
    /// <para>
    /// Intra-library SFDs (e.g. Stripe's <c>StripePayments → StripeCore</c>,
    /// where both csprojs sit under <c>libraries/Stripe/</c>) are filtered
    /// out because <c>BuildXcframeworkForLibrary(library, allProducts: true)</c>
    /// already builds every sibling product in the same library.
    /// </para>
    /// </summary>
    IReadOnlyList<string> ResolveCrossLibraryXcframeworkPrerequisites(string library)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var ordered = new List<string>();

        void Visit(string lib)
        {
            if (!visited.Add(lib))
                return;

            foreach (var dep in DirectCrossLibraryDeps(lib))
                Visit(dep);

            if (!string.Equals(lib, library, StringComparison.Ordinal))
                ordered.Add(lib);
        }

        Visit(library);
        return ordered;
    }

    /// <summary>
    /// Direct (non-transitive) cross-library SFD targets of
    /// <paramref name="lib"/>. Returns sibling library names under
    /// <c>libraries/</c>, deduped, in deterministic order.
    /// </summary>
    IEnumerable<string> DirectCrossLibraryDeps(string lib)
    {
        AbsolutePath libDir;
        try
        {
            libDir = LibraryDir(lib);
        }
        catch (InvalidOperationException)
        {
            // ResolveLibrary throws only for names absent from both
            // libraries/ and apple-frameworks/. Apple framework names DO
            // resolve here (under apple-frameworks/) and will fall through
            // to the scan loop below; their library.json check later filters
            // them out without warning.
            yield break;
        }

        var librariesPrefix = ((string)LibrariesDir) + Path.DirectorySeparatorChar;
        var seen = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var csproj in Directory.EnumerateFiles(libDir, "SwiftBindings.*.csproj", SearchOption.AllDirectories))
        {
            // Skip test csprojs — only the package csprojs feed the SDK
            // generator, so test-only SFDs (none exist today, but the
            // template could grow them) are not build prerequisites.
            var rel = Path.GetRelativePath(libDir, csproj);
            if (rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                   .Any(seg => string.Equals(seg, "tests", StringComparison.OrdinalIgnoreCase)))
                continue;

            var csprojDir = Path.GetDirectoryName(csproj)!;
            var content = File.ReadAllText(csproj);
            foreach (Match m in SfdIncludeRegex.Matches(content))
            {
                var include = m.Groups[1].Value;
                var resolved = Path.GetFullPath(Path.Combine(csprojDir, include));

                if (!resolved.StartsWith(librariesPrefix, StringComparison.Ordinal))
                    continue;

                var rest = resolved.Substring(librariesPrefix.Length);
                var firstSep = rest.IndexOfAny(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar });
                if (firstSep <= 0)
                    continue;

                var other = rest.Substring(0, firstSep);
                if (string.Equals(other, lib, StringComparison.Ordinal))
                    continue; // intra-library

                if (!File.Exists(LibrariesDir / other / "library.json"))
                {
                    Log.Warning(
                        "Cross-library SwiftFrameworkDependency in {Csproj} references {Other} which has no library.json — skipping",
                        Path.GetRelativePath(RootDirectory, csproj), other);
                    continue;
                }

                seen.Add(other);
            }
        }

        foreach (var s in seen)
            yield return s;
    }
}
