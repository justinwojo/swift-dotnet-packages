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
    /// Two-pass design (prepare, then commit):
    /// </para>
    /// <list type="number">
    ///   <item>For each selected non-internal product: freshness-check it, scan
    ///         its generated C# for sibling-module references and prepare the
    ///         csproj rewrite in memory. A product that fails its freshness
    ///         check is skipped on its own — its csproj is left untouched and
    ///         the rest of the library is still processed.</item>
    ///   <item>Commit every prepared rewrite, then fail if any product was
    ///         skipped so the gap is never mistaken for "nothing to do".</item>
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

        // Map every library.json product module → manifest entry. The
        // wrapper-compile path needs SwiftFrameworkDependency entries for
        // every framework the public .swiftinterface imports, INCLUDING
        // ObjC-only mixed/umbrella modules like Stripe3DS2 that ship without
        // a .swiftmodule. Those still need to be on the framework search
        // path so that `import X` in the generated Swift wrapper resolves;
        // filtering by .swiftmodule presence (the previous behavior here)
        // silently dropped them and broke wrapper compile for StripePayments.
        // The companion ProjectReference / NativeReference plumbing handles
        // the consuming side for ObjC-only deps; SFD here is strictly about
        // the wrapper-compile framework search path.
        var productModules = new HashSet<string>(
            config.Products.Select(p => p.Module ?? p.Framework),
            StringComparer.Ordinal);

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
                if (!productModules.Contains(imp))
                    continue; // System framework or unrelated module — not a sibling.

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

    void InjectProjectRefsForLibrary(string library, string[] requestedProducts, bool allProducts,
        string? configurationOverride = null)
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

        // Pass 1: freshness-check, scan and PREPARE rewrites in memory, one
        // product at a time. No disk writes yet. A product that fails its
        // freshness check is skipped individually (see
        // ProjectRefInjectionPlanner) rather than cancelling the whole
        // library's injection — the freshness question is per-product, and a
        // library-wide abort here is what let the ProjectReference block drift
        // out of sync with the always-per-product SwiftFrameworkDependency
        // block. Skips are reported and re-raised after the safe writes land.
        var targets = products
            .Select(entry => new ProjectRefTarget(
                entry.Product.Framework,
                entry.Csproj,
                entry.Product.XcframeworkPath(libraryDir) / "Info.plist",
                SwiftBindingDir(entry.Product, libraryDir, configurationOverride)))
            .ToList();

        var plan = ProjectRefInjectionPlanner.Plan(targets, allSiblings);

        foreach (var entry in plan)
        {
            if (entry.Skipped)
            {
                Log.Warning("  {Framework} → SKIPPED (csproj left untouched): {Reason}",
                    entry.Target.Framework, entry.SkipReason);
                continue;
            }

            Log.Information("  {Framework} → needs: [{Needs}]",
                entry.Target.Framework,
                entry.DetectedModules.Count > 0 ? string.Join(", ", entry.DetectedModules) : "(none)");

            // A reference the scan no longer reproduces is retained rather
            // than deleted (deleting one the build needs is a hard failure;
            // keeping a spare one costs a redundant restore edge). Say so —
            // a module that stays on this list run after run is the signal
            // that the reference has genuinely gone stale.
            if (entry.CarriedForwardModules.Count > 0)
            {
                Log.Information("  {Framework} → retained (not re-detected this run): [{Retained}]",
                    entry.Target.Framework, string.Join(", ", entry.CarriedForwardModules));
            }
        }

        // Pass 2: commit all prepared rewrites. Each Commit() is itself
        // atomic (temp-file + rename), so a single per-file failure cannot
        // produce a half-written csproj. A failure here can still leave
        // earlier products committed and later ones not — true cross-file
        // atomicity isn't possible without a transactional fs — but every
        // file on disk is either fully old or fully new, never torn.
        var pending = plan.Where(e => e.WriteOp is not null).Select(e => e.WriteOp!).ToList();
        foreach (var op in pending)
        {
            op.Commit();
            Log.Information("    updated {Csproj}", Path.GetRelativePath(libraryDir, op.Path));
        }

        Log.Information(pending.Count > 0
            ? "ProjectReference block(s) updated. Run 'dotnet build' (second pass)."
            : "All csprojs already up to date — no changes made.");

        // Fail closed on anything we could not process. The writes above have
        // already landed, so the fresh products are correct; this exists so a
        // skipped product can never pass unnoticed as "injection completed".
        var skipped = plan.Where(e => e.Skipped).ToList();
        if (skipped.Count > 0)
        {
            var detail = string.Join("\n\n", skipped.Select(e => e.SkipReason));
            throw new InvalidOperationException(
                $"ProjectReference injection skipped {skipped.Count} of {plan.Count} product(s) in {library} " +
                $"({string.Join(", ", skipped.Select(e => e.Target.Framework))}). " +
                "Their csprojs were left untouched; every other product was updated.\n\n" +
                detail);
        }
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
    /// Pass <paramref name="configurationOverride"/> when the caller built at a
    /// configuration other than the runtime Nuke field default (e.g. the
    /// release path runs at <c>Release</c> while the field stays <c>Debug</c>).
    /// </summary>
    AbsolutePath SwiftBindingDir(Product product, AbsolutePath libraryDir, string? configurationOverride = null)
    {
        var productDir = product.ProductDir(libraryDir);
        var configuration = configurationOverride ?? (string)Configuration;
        return productDir / "obj" / configuration / "net10.0-ios" / "swift-binding";
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
