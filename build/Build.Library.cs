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
    static int RunDotnet(IEnumerable<string> args)
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

        Log.Information("> dotnet {Args}", string.Join(' ', psi.ArgumentList));

        using var process = System.Diagnostics.Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start dotnet");
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) Log.Information("{Line}", e.Data); };
        process.ErrorDataReceived  += (_, e) => { if (e.Data is not null) Log.Information("{Line}", e.Data); };
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
}
