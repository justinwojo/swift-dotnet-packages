using Nuke.Common;
using Nuke.Common.IO;
using Serilog;
using SwiftBindings.Build.Helpers;
using SwiftBindings.Build.Models;

partial class Build
{
    /// <summary>
    /// Inject the SwiftFrameworkDependency auto-block from <c>.swiftinterface</c>
    /// imports. Replaces <c>scripts/detect-dependencies.sh --inject</c>.
    ///
    /// <para>
    /// Walks every selected non-internal product, parses imports out of its
    /// device-slice <c>.swiftinterface</c>, cross-references them against the
    /// library's product list, and rewrites the csproj's auto-block via
    /// <see cref="CsprojRewriter.RewriteFrameworkDeps"/>.
    /// </para>
    ///
    /// <para>
    /// Prerequisite: xcframeworks must already be built (the
    /// <see cref="BuildXcframework"/> dependency edge enforces this when invoked
    /// via <see cref="BuildLibrary"/>).
    /// </para>
    /// </summary>
    Target InjectFrameworkDeps => _ => _
        .Description("Inject SwiftFrameworkDependency auto-block from .swiftinterface imports")
        .Requires(() => Library)
        .Executes(() => InjectFrameworkDepsForLibrary(Library!, ParseProductSubset(Products), AllProducts));

    /// <summary>
    /// Inject the ProjectReference auto-block from grep over freshly-generated
    /// C# under <c>obj/&lt;Configuration&gt;/net10.0-ios/swift-binding/</c>. Replaces
    /// <c>scripts/detect-dependencies.sh --inject-project-refs</c>.
    ///
    /// <para>
    /// Two-pass design (atomic):
    /// </para>
    /// <list type="number">
    ///   <item>Freshness-check ALL selected non-internal products before
    ///         touching any csproj. If any check fails, abort with no writes.</item>
    ///   <item>Scan generated C# for sibling-module references and rewrite
    ///         each csproj's ProjectReference block via
    ///         <see cref="CsprojRewriter.RewriteProjectRefs"/>.</item>
    /// </list>
    ///
    /// <para>
    /// The sibling table covers ALL non-internal products in
    /// <c>library.json</c>, not just the ones selected for this run — a product
    /// may reference a sibling that wasn't passed via <c>--products</c>.
    /// </para>
    /// </summary>
    Target InjectProjectRefs => _ => _
        .Description("Inject ProjectReference auto-block from generated C# scan")
        .Requires(() => Library)
        .Executes(() => InjectProjectRefsForLibrary(Library!, ParseProductSubset(Products), AllProducts));

    /// <summary>
    /// Wipe each selected non-internal product's
    /// <c>obj/&lt;Configuration&gt;/net10.0-ios/swift-binding/</c> directory.
    /// Replaces the <c>--clean-first</c> flag of <c>detect-dependencies.sh</c>.
    /// Honors <see cref="Configuration"/> so a Release-targeted clean wipes
    /// the Release output rather than Debug's.
    /// </summary>
    Target CleanSwiftBindingOutput => _ => _
        .Description("Wipe obj/<Configuration>/net10.0-ios/swift-binding/ for each selected product (replaces --clean-first)")
        .Requires(() => Library)
        .Executes(() => CleanSwiftBindingForLibrary(Library!, ParseProductSubset(Products), AllProducts));

    // ── Implementations (separated so BuildLibrary can call them directly) ─

    void InjectFrameworkDepsForLibrary(string library, string[] requestedProducts, bool allProducts)
    {
        var libraryDir = LibraryDir(library);
        var config = LibraryConfigLoader.Load(LibraryConfigPath(library));
        var selected = config.ResolveProducts(requestedProducts, allProducts);

        // Detect Swift modules across ALL products. A sibling is only a valid
        // SFD candidate if its xcframework actually has a .swiftmodule
        // (ObjC-only frameworks must NOT be listed as SwiftFrameworkDependency
        // — that causes the SDK generator to silently produce no output).
        var swiftModules = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in config.Products)
        {
            var xcfwDir = p.XcframeworkPath(libraryDir);
            if (!Directory.Exists(xcfwDir))
                continue;
            var hasSwiftModule = Directory
                .EnumerateDirectories(xcfwDir, "*.swiftmodule", SearchOption.AllDirectories)
                .Any();
            if (hasSwiftModule)
                swiftModules.Add(p.Module ?? p.Framework);
        }

        // Canonical sibling include set — same path scheme the bash uses to
        // build SIBLINGS in `--inject` mode. Includes internal products too,
        // since e.g. Stripe3DS2 IS a valid SFD candidate (it has a .swiftmodule).
        var allSiblingIncludes = new HashSet<string>(
            config.Products.Select(BuildSiblingInclude),
            StringComparer.Ordinal);

        Log.Information("InjectFrameworkDeps {Library}: scanning {Count} product(s)",
            library, selected.Count(p => !p.Internal));

        foreach (var product in selected)
        {
            if (product.Internal)
                continue;

            var xcfwDir = product.XcframeworkPath(libraryDir);
            if (!Directory.Exists(xcfwDir))
            {
                Log.Warning("  {Framework}: xcframework not found at {Path}", product.Framework, xcfwDir);
                continue;
            }

            var imports = SwiftInterfaceParser.ExtractImports(xcfwDir);
            if (imports is null)
            {
                Log.Information("  {Framework}: no .swiftinterface found (ObjC-only?)", product.Framework);
                // Still rewrite the csproj — a previously-injected block may
                // need to be cleared. (Bash matches: products with no
                // .swiftinterface emit DEPS:NONE which results in an empty
                // dep list and an in-place rewrite.)
                var emptyCsproj = product.CsprojPath(libraryDir);
                CsprojRewriter.RewriteFrameworkDeps(emptyCsproj, Array.Empty<string>(), allSiblingIncludes);
                continue;
            }

            var module = product.Module ?? product.Framework;
            var hits = new List<string>();
            foreach (var imp in imports)
            {
                if (imp == module)
                    continue;
                if (!swiftModules.Contains(imp))
                    continue; // ObjC-only sibling — silently skip per bash semantics

                var sibling = config.Products.FirstOrDefault(p => (p.Module ?? p.Framework) == imp);
                if (sibling is null)
                    continue;

                hits.Add(BuildSiblingInclude(sibling));
            }

            // Distinct: an import statement that resolves to the same sibling
            // twice (e.g. via @_exported) shouldn't double up.
            var distinct = hits.Distinct(StringComparer.Ordinal).ToList();

            var csproj = product.CsprojPath(libraryDir);
            var changed = CsprojRewriter.RewriteFrameworkDeps(csproj, distinct, allSiblingIncludes);
            Log.Information("  {Framework} → {Count} sibling dep(s) [{Status}]",
                product.Framework, distinct.Count, changed ? "updated" : "no changes");
        }
    }

    void InjectProjectRefsForLibrary(string library, string[] requestedProducts, bool allProducts)
    {
        var libraryDir = LibraryDir(library);
        var config = LibraryConfigLoader.Load(LibraryConfigPath(library));
        var selected = config.ResolveProducts(requestedProducts, allProducts);

        // Per-product entries: non-internal selected.
        var products = new List<(Product Product, AbsolutePath Csproj)>();
        foreach (var p in selected)
        {
            if (p.Internal)
                continue;
            products.Add((p, p.CsprojPath(libraryDir)));
        }

        if (products.Count == 0)
        {
            Log.Information("No non-internal products to process.");
            return;
        }

        // Sibling table: ALL non-internal products in library.json, regardless
        // of --products. Cross-module refs may point at siblings that weren't
        // selected for this run.
        var allSiblings = new List<DependencyHit>();
        foreach (var p in config.Products)
        {
            if (p.Internal)
                continue;
            allSiblings.Add(new DependencyHit(
                Framework: p.Framework,
                Module: p.Module ?? p.Framework,
                Csproj: p.CsprojPath(libraryDir),
                Subdirectory: p.Subdirectory ?? ""));
        }

        Log.Information("Analyzing ProjectReference needs for {Count} product(s)...", products.Count);

        // Pass 1: freshness check ALL products before any writes.
        // If any product is stale, abort without touching the csprojs — the
        // rewrite is all-or-nothing (matches detect-dependencies.sh:737–741).
        var freshnessCache = new Dictionary<AbsolutePath, IReadOnlyList<AbsolutePath>>();
        foreach (var (product, csproj) in products)
        {
            var swiftBindingDir = SwiftBindingDir(product, libraryDir);
            var xcfwPlist = product.XcframeworkPath(libraryDir) / "Info.plist";
            freshnessCache[csproj] = Freshness.CheckFresh(product.Framework, csproj, xcfwPlist, swiftBindingDir);
        }

        // Pass 2: scan + PREPARE rewrites in memory. No disk writes yet.
        // If any product's prepare step throws (e.g. asymmetric markers),
        // the whole batch aborts before any csproj is touched.
        var pending = new List<CsprojWriteOp>();
        foreach (var (product, csproj) in products)
        {
            var csFiles = freshnessCache[csproj];
            var content = GeneratedCsScanner.Collect(csFiles);

            var hits = new List<DependencyHit>();
            foreach (var sib in allSiblings)
            {
                if (sib.Csproj == csproj)
                    continue; // skip self
                if (string.IsNullOrEmpty(sib.Module))
                    continue;
                if (GeneratedCsScanner.ContainsModuleReference(content, sib.Module))
                    hits.Add(sib);
            }

            if (hits.Count > 0)
            {
                var needs = string.Join(", ", hits.Select(h => h.Module).OrderBy(m => m, StringComparer.Ordinal));
                Log.Information("  {Framework} → needs: [{Needs}]", product.Framework, needs);
            }
            else
            {
                Log.Information("  {Framework} → needs: (none)", product.Framework);
            }

            var op = CsprojRewriter.PrepareProjectRefs(csproj, hits, allSiblings);
            if (op is not null)
                pending.Add(op);
        }

        // Pass 3: commit all prepared rewrites. Each Commit() is itself
        // atomic (temp-file + rename), so a single per-file failure cannot
        // produce a half-written csproj. A failure here can still leave
        // earlier products committed and later ones not — true cross-file
        // atomicity isn't possible without a transactional fs — but every
        // file on disk is either fully old or fully new, never torn.
        foreach (var op in pending)
        {
            op.Commit();
            Log.Information("    updated {Csproj}", Path.GetRelativePath(libraryDir, op.Path));
        }

        Log.Information(pending.Count > 0
            ? "ProjectReference block(s) updated. Run 'dotnet build' (second pass)."
            : "All csprojs already up to date — no changes made.");
    }

    void CleanSwiftBindingForLibrary(string library, string[] requestedProducts, bool allProducts)
    {
        var libraryDir = LibraryDir(library);
        var config = LibraryConfigLoader.Load(LibraryConfigPath(library));
        var selected = config.ResolveProducts(requestedProducts, allProducts);

        var cleaned = 0;
        foreach (var product in selected)
        {
            if (product.Internal)
                continue;
            var swiftBindingDir = SwiftBindingDir(product, libraryDir);
            if (Directory.Exists(swiftBindingDir))
            {
                swiftBindingDir.DeleteDirectory();
                Log.Information("Cleaned {Path}", swiftBindingDir);
                cleaned++;
            }
        }

        if (cleaned == 0)
            Log.Information("Nothing to clean — no swift-binding/ directories found.");
    }

    // ── Path helpers ────────────────────────────────────────────────────────

    /// <summary>
    /// Resolve the SDK's swift-binding output directory for one product.
    /// Configuration-aware: <c>obj/&lt;Configuration&gt;/net10.0-ios/swift-binding</c>.
    /// </summary>
    AbsolutePath SwiftBindingDir(Product product, AbsolutePath libraryDir)
    {
        var productDir = product.ProductDir(libraryDir);
        return productDir / "obj" / Configuration / "net10.0-ios" / "swift-binding";
    }

    /// <summary>
    /// Build the canonical sibling SwiftFrameworkDependency Include path for a
    /// product, the same way the bash <c>--inject</c> driver does at
    /// <c>scripts/detect-dependencies.sh:790–794</c>.
    /// </summary>
    private static string BuildSiblingInclude(Product product)
        => string.IsNullOrEmpty(product.Subdirectory)
            ? $"../{product.Framework}/{product.Framework}.xcframework"
            : $"../{product.Subdirectory}/{product.Framework}.xcframework";
}
