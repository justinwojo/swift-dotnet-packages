using System.Text.RegularExpressions;
using Nuke.Common.IO;
using SwiftBindings.Build.Models;

namespace SwiftBindings.Build.Helpers;

/// <summary>
/// Rewrites the auto-managed dependency blocks in a <c>SwiftBindings.*.csproj</c>
/// file. Direct port of the csproj-mutating logic in
/// <c>scripts/detect-dependencies.sh</c>.
///
/// <para>
/// There are two distinct auto-blocks, each with its own marker pair:
/// </para>
/// <list type="bullet">
///   <item>
///     <c>SwiftFrameworkDependency</c> — populated from
///     <c>.swiftinterface</c> import statements. Driven by
///     <see cref="RewriteFrameworkDeps"/>.
///   </item>
///   <item>
///     <c>ProjectReference</c> — populated from grep over freshly-generated
///     C# under <c>obj/.../swift-binding/</c>. Driven by
///     <see cref="RewriteProjectRefs"/>.
///   </item>
/// </list>
///
/// <para>
/// Both operations are idempotent — re-running on a previously-injected csproj
/// produces zero diff.
/// </para>
///
/// <para><b>String-based not XML-based.</b> We mutate the csproj as raw text
/// rather than via <c>System.Xml.Linq</c> because we need byte-identical
/// roundtrips with the existing bash injector — XML serializers normalize
/// whitespace, attribute order, and namespace prefixes in ways that would
/// produce a noisy migration diff.</para>
/// </summary>
public static class CsprojRewriter
{
    public const string SfdBeginMarker = "<!-- BEGIN auto-detected SwiftFrameworkDependency -->";
    public const string SfdEndMarker = "<!-- END auto-detected SwiftFrameworkDependency -->";
    public const string PrBeginMarker = "<!-- BEGIN auto-detected ProjectReference -->";
    public const string PrEndMarker = "<!-- END auto-detected ProjectReference -->";

    // ────────────────────────────────────────────────────────────────────────
    // SwiftFrameworkDependency rewrite
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Rewrite the SwiftFrameworkDependency auto-block in <paramref name="csprojPath"/>.
    /// Returns true iff the file changed on disk.
    /// </summary>
    /// <param name="csprojPath">Absolute path to the csproj.</param>
    /// <param name="depIncludes">
    /// Canonical sibling include paths to inject (e.g.
    /// <c>"../StripeCore/StripeCore.xcframework"</c>). New items are appended
    /// to the auto-block in ordinal sort order; pre-existing in-block items
    /// keep their original order across re-runs (no churn).
    /// </param>
    /// <param name="knownSiblings">
    /// Canonical sibling include set for the library (every other product's
    /// xcframework path under the same library root). Used to prune entries
    /// inside the auto-block that no longer correspond to a current product
    /// — otherwise a sibling removed from <c>library.json</c> would stay
    /// pinned in the managed block forever. Items <i>outside</i> the
    /// auto-block are still preserved verbatim regardless of this set.
    /// </param>
    public static bool RewriteFrameworkDeps(
        AbsolutePath csprojPath,
        IReadOnlyCollection<string> depIncludes,
        IReadOnlyCollection<string> knownSiblings)
    {
        var (original, bom, lineEnding) = ReadCsproj(csprojPath);
        var rewritten = ApplyFrameworkDeps(original, depIncludes, knownSiblings);
        if (rewritten == original)
            return false;
        WriteCsproj(csprojPath, rewritten, bom, lineEnding);
        return true;
    }

    /// <summary>Pure-function variant of <see cref="RewriteFrameworkDeps"/> for unit tests.</summary>
    /// <remarks>
    /// <para><b>Contract.</b></para>
    /// <list type="bullet">
    ///   <item><c>SwiftFrameworkDependency</c> items inside the
    ///         <c>BEGIN/END auto-detected SwiftFrameworkDependency</c> block
    ///         are managed: rewrites add, drop, and re-emit them based on
    ///         the union of <paramref name="depIncludes"/> and existing
    ///         in-block entries that are still in <paramref name="knownSiblings"/>.
    ///         Existing in-block ordering is preserved on rerun (new items
    ///         append sorted); entries that fall out of <paramref name="knownSiblings"/>
    ///         are dropped so a removed product cannot pin itself forever.</item>
    ///   <item><c>SwiftFrameworkDependency</c> items <i>outside</i> the
    ///         auto-block are user-authored and preserved verbatim on every
    ///         rerun — the in-place regex replacement only touches the
    ///         marked block, regardless of <paramref name="knownSiblings"/>.</item>
    ///   <item><c>&lt;PropertyGroup&gt;</c> content is never touched. Only
    ///         the marked <c>&lt;ItemGroup&gt;</c> block is edited.</item>
    /// </list>
    /// </remarks>
    public static string ApplyFrameworkDeps(
        string original,
        IReadOnlyCollection<string> depIncludes,
        IReadOnlyCollection<string> knownSiblings)
    {
        // ── Step 1: capture preserved attrs from existing SFD items ──
        // The bash matches `<SwiftFrameworkDependency ... />` self-closing
        // elements anywhere in the file (including inside the existing
        // auto-block) so that PackageId/PackageVersion attrs survive a
        // rerun. Same regex shape (Singleline / DOTALL).
        var preservedAttrs = new Dictionary<string, List<KeyValuePair<string, string>>>(StringComparer.Ordinal);
        var sfdElementRegex = new Regex(@"<SwiftFrameworkDependency\b([^>]*?)/>", RegexOptions.Singleline);
        var attrRegex = new Regex(@"(\w+)\s*=\s*""([^""]*)""");
        foreach (Match m in sfdElementRegex.Matches(original))
        {
            string? include = null;
            var extras = new List<KeyValuePair<string, string>>();
            foreach (Match a in attrRegex.Matches(m.Groups[1].Value))
            {
                var key = a.Groups[1].Value;
                var val = a.Groups[2].Value;
                if (key == "Include")
                    include = val;
                else
                    extras.Add(new KeyValuePair<string, string>(key, val));
            }
            if (include is not null && extras.Count > 0)
                preservedAttrs[include] = extras;
        }

        var content = original;
        var hasBegin = content.Contains(SfdBeginMarker);
        var hasEnd = content.Contains(SfdEndMarker);
        // Hard-fail on asymmetric markers — silently double-injecting on the
        // next run would corrupt the csproj. The bash version has the same
        // latent bug; we tighten it here since the C# version is more
        // amenable to a clean abort.
        if (hasBegin != hasEnd)
        {
            throw new InvalidOperationException(
                "Asymmetric SwiftFrameworkDependency auto-block markers detected " +
                $"({(hasBegin ? "BEGIN without END" : "END without BEGIN")}). " +
                "Refusing to rewrite to avoid silent double-injection. " +
                "Restore the matching marker (or remove both) and re-run.");
        }
        var hasMarkers = hasBegin && hasEnd;

        // Collect every SFD Include path already in the file — both inside
        // and outside the auto-block. Used below to dedupe heuristic hits
        // against user-authored entries: items outside the auto-block are
        // user-authored and preserved verbatim.
        var allExistingIncludes = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in sfdElementRegex.Matches(original))
        {
            foreach (Match a in attrRegex.Matches(m.Groups[1].Value))
            {
                if (a.Groups[1].Value == "Include")
                {
                    allExistingIncludes.Add(a.Groups[2].Value);
                    break;
                }
            }
        }

        if (hasMarkers)
        {
            // Subsequent run: keep the existing in-block ordering of entries,
            // and append new heuristic hits (those not already anywhere in
            // the file) in ordinal sort order. Surrounding whitespace is
            // left alone so unchanged content is a true no-op.
            //
            // Why union (not overwrite): the heuristic in
            // Build.Dependencies.cs walks the public .swiftinterface, but
            // SDK-generated wrappers sometimes transitively import a sibling
            // not listed there (e.g. StripePaymentSheet's wrapper imports
            // Stripe3DS2 through StripePayments). Dropping in-block entries
            // the heuristic doesn't reproduce would silently delete those
            // and break wrapper compile.
            //
            // Why preserve order: hand-curated csprojs (e.g. StripePayments
            // ships [StripeCore, Stripe3DS2], not the ordinal-sorted
            // [Stripe3DS2, StripeCore]) would otherwise churn on every rerun
            // even when the *content* is identical.

            // Suppress heuristic hits that already exist OUTSIDE the auto-
            // block as user-authored manual overrides. The in-place regex
            // replacement only touches the marked block, so the manual entry
            // stays put — emitting it in-block too would duplicate the
            // <SwiftFrameworkDependency> (and its PackageId/PackageVersion
            // metadata).
            var blockSpan = new Regex(
                Regex.Escape(SfdBeginMarker) + ".*?" + Regex.Escape(SfdEndMarker),
                RegexOptions.Singleline);
            var contentOutsideBlock = blockSpan.Replace(content, "");
            var outOfBlockIncludes = new HashSet<string>(StringComparer.Ordinal);
            foreach (Match m in sfdElementRegex.Matches(contentOutsideBlock))
            {
                foreach (Match a in attrRegex.Matches(m.Groups[1].Value))
                {
                    if (a.Groups[1].Value == "Include")
                    {
                        outOfBlockIncludes.Add(a.Groups[2].Value);
                        break;
                    }
                }
            }
            IReadOnlyCollection<string> filteredDeps = outOfBlockIncludes.Count > 0
                ? depIncludes.Where(d => !outOfBlockIncludes.Contains(d)).ToList()
                : depIncludes;

            var orderedIncludes = OrderInBlockIncludes(content, filteredDeps, knownSiblings);
            var depsBlock = BuildSfdBlock(orderedIncludes, preservedAttrs);

            var inPlacePattern = new Regex(
                Regex.Escape(SfdBeginMarker) + ".*?" + Regex.Escape(SfdEndMarker),
                RegexOptions.Singleline);
            if (depsBlock.Length > 0)
            {
                content = inPlacePattern.Replace(content, depsBlock.Replace("$", "$$"), 1);
            }
            else
            {
                // No deps — strip markers AND surrounding whitespace.
                var stripPattern = new Regex(
                    @"\n?\s*" + Regex.Escape(SfdBeginMarker) + ".*?" + Regex.Escape(SfdEndMarker) + @"\s*\n?",
                    RegexOptions.Singleline);
                content = stripPattern.Replace(content, "\n", 1);
            }
        }
        else
        {
            // First run (no markers): user-authored SFD items in unmarked
            // ItemGroups stay where they are. Only NEW heuristic hits —
            // Includes the user hasn't already declared anywhere — get
            // emitted into a fresh auto-block. This avoids both:
            //   1. Silent loss of user-authored items (the regression seen
            //      on the Stripe umbrella csproj, where the umbrella's
            //      hand-authored unmarked ItemGroup was being stripped), and
            //   2. Duplicate <SwiftFrameworkDependency> items between the
            //      existing user block and the new auto-block.
            // If every heuristic hit is already user-authored, no auto-block
            // is emitted at all.
            var newItems = depIncludes
                .Where(d => !allExistingIncludes.Contains(d))
                .OrderBy(s => s, StringComparer.Ordinal)
                .ToList();
            if (newItems.Count > 0)
            {
                var depsBlock = BuildSfdBlock(newItems, preservedAttrs);
                // str.replace() in Python without count replaces ALL — but
                // </Project> is unique in a csproj, so a single-shot replace
                // is equivalent.
                content = ReplaceFirst(content, "</Project>", depsBlock + "\n\n</Project>");
            }
        }

        return content;
    }

    /// <summary>
    /// Compute the order in which the SFD auto-block should be re-emitted
    /// on a subsequent run: union of <paramref name="depIncludes"/> and any
    /// in-block items still present in <paramref name="knownSiblings"/>, with
    /// existing items kept in their original order and new heuristic hits
    /// appended in ordinal sort order. Pure function — operates only on the
    /// slice between <see cref="SfdBeginMarker"/> and <see cref="SfdEndMarker"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Union (not overwrite) semantics is intentional.</b> The heuristic
    /// in <c>Build.Dependencies.cs</c> walks the public <c>.swiftinterface</c>
    /// of each product, but the SDK-generated Swift wrapper sometimes
    /// transitively imports a sibling not listed there (e.g.
    /// <c>StripePaymentSheet</c>'s wrapper imports <c>Stripe3DS2</c> through
    /// <c>StripePayments</c>). Dropping in-block entries that the heuristic
    /// doesn't reproduce would silently delete those transitively-required
    /// SFDs and break wrapper compile. The union keeps them.
    /// </para>
    /// <para>
    /// <b>Pruning against <paramref name="knownSiblings"/>.</b> The union
    /// is bounded by the canonical sibling set for the library: an in-block
    /// entry whose include path no longer corresponds to a current product
    /// (e.g. the product was removed from <c>library.json</c>) is dropped.
    /// This prevents stale managed entries from pinning forever once a
    /// product is removed or renamed.
    /// </para>
    /// <para>
    /// Attribute parsing uses the order-agnostic
    /// <c>&lt;SwiftFrameworkDependency ... /&gt;</c> matcher rather than
    /// requiring <c>Include</c> to be the first attribute — robust against
    /// hand-edited orderings such as <c>PackageId="..." Include="..."</c>.
    /// </para>
    /// </remarks>
    private static List<string> OrderInBlockIncludes(
        string content,
        IReadOnlyCollection<string> depIncludes,
        IReadOnlyCollection<string> knownSiblings)
    {
        var blockMatch = new Regex(
            Regex.Escape(SfdBeginMarker) + "(.*?)" + Regex.Escape(SfdEndMarker),
            RegexOptions.Singleline).Match(content);

        // Order-agnostic: scan the block slice for any
        // <SwiftFrameworkDependency .../> element and pull the Include attr
        // regardless of attribute order.
        var sfdElementRegex = new Regex(@"<SwiftFrameworkDependency\b([^>]*?)/>", RegexOptions.Singleline);
        var attrRegex = new Regex(@"(\w+)\s*=\s*""([^""]*)""");

        var existingOrder = new List<string>();
        if (blockMatch.Success)
        {
            foreach (Match m in sfdElementRegex.Matches(blockMatch.Groups[1].Value))
            {
                foreach (Match a in attrRegex.Matches(m.Groups[1].Value))
                {
                    if (a.Groups[1].Value == "Include")
                    {
                        existingOrder.Add(a.Groups[2].Value);
                        break;
                    }
                }
            }
        }

        var siblingSet = knownSiblings as HashSet<string>
            ?? new HashSet<string>(knownSiblings, StringComparer.Ordinal);

        var emitted = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();
        foreach (var inc in existingOrder)
        {
            // Prune entries no longer in the canonical sibling set so a
            // removed/renamed product cannot stay pinned in the managed block.
            if (!siblingSet.Contains(inc))
                continue;
            if (emitted.Add(inc))
                result.Add(inc);
        }
        foreach (var inc in depIncludes.OrderBy(s => s, StringComparer.Ordinal))
        {
            if (emitted.Add(inc))
                result.Add(inc);
        }
        return result;
    }

    /// <summary>
    /// Emit the SFD auto-block. Caller is responsible for the order of
    /// <paramref name="orderedIncludes"/>: first-run uses ordinal sort
    /// (matching Python's <c>sorted()</c> default); subsequent runs preserve
    /// the existing in-block order via <see cref="OrderInBlockIncludes"/>.
    /// </summary>
    private static string BuildSfdBlock(
        IReadOnlyList<string> orderedIncludes,
        Dictionary<string, List<KeyValuePair<string, string>>> preservedAttrs)
    {
        if (orderedIncludes.Count == 0)
            return "";

        var lines = new List<string>
        {
            SfdBeginMarker,
            "  <ItemGroup>",
        };
        foreach (var inc in orderedIncludes)
        {
            if (preservedAttrs.TryGetValue(inc, out var extras))
            {
                var attrStr = string.Concat(extras.Select(kv => $" {kv.Key}=\"{kv.Value}\""));
                lines.Add($"    <SwiftFrameworkDependency Include=\"{inc}\"{attrStr} />");
            }
            else
            {
                lines.Add($"    <SwiftFrameworkDependency Include=\"{inc}\" />");
            }
        }
        lines.Add("  </ItemGroup>");
        lines.Add("  " + SfdEndMarker);
        return string.Join("\n", lines);
    }

    // ────────────────────────────────────────────────────────────────────────
    // ProjectReference rewrite
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Rewrite the ProjectReference auto-block in <paramref name="csprojPath"/>.
    /// Returns true iff the file changed on disk.
    /// </summary>
    /// <param name="csprojPath">Absolute csproj path.</param>
    /// <param name="hits">Detected sibling refs (one per cross-module reference found in generated C#).</param>
    /// <param name="allSiblings">All sibling products in the library (used for basename matching during migration).</param>
    public static bool RewriteProjectRefs(
        AbsolutePath csprojPath,
        IReadOnlyList<DependencyHit> hits,
        IReadOnlyList<DependencyHit> allSiblings)
    {
        var op = PrepareProjectRefs(csprojPath, hits, allSiblings, out _);
        if (op is null)
            return false;
        op.Commit();
        return true;
    }

    /// <summary>
    /// Compute the rewritten csproj content for a ProjectReference injection
    /// without writing to disk. Returns <c>null</c> if no change is needed.
    /// Used by <c>InjectProjectRefsForLibrary</c> for two-phase batched commit:
    /// every product is prepared first, then committed in a second loop.
    /// </summary>
    /// <param name="carriedForwardModules">
    /// Modules that end up in the rewritten auto-block even though
    /// <paramref name="hits"/> did not contain them — i.e. references carried
    /// forward from the previous auto-block (or migrated in from a
    /// hand-authored ItemGroup) rather than re-detected by the scan. Surfaced
    /// so the caller can log them: a permanently-carried reference that the
    /// scan never reproduces is the signal that the reference is stale, and
    /// silently retaining it without saying so would bury that.
    /// </param>
    public static CsprojWriteOp? PrepareProjectRefs(
        AbsolutePath csprojPath,
        IReadOnlyList<DependencyHit> hits,
        IReadOnlyList<DependencyHit> allSiblings,
        out IReadOnlyList<string> carriedForwardModules)
    {
        var (original, bom, lineEnding) = ReadCsproj(csprojPath);
        var rewritten = ApplyProjectRefs(original, csprojPath, hits, allSiblings, out carriedForwardModules);
        if (rewritten == original)
            return null;
        return new CsprojWriteOp(csprojPath, rewritten, bom, lineEnding);
    }

    /// <summary>Pure-function variant of <see cref="RewriteProjectRefs"/> for unit tests.</summary>
    public static string ApplyProjectRefs(
        string original,
        AbsolutePath csprojPath,
        IReadOnlyList<DependencyHit> hits,
        IReadOnlyList<DependencyHit> allSiblings)
        => ApplyProjectRefs(original, csprojPath, hits, allSiblings, out _);

    /// <summary>
    /// Pure-function variant of <see cref="RewriteProjectRefs"/> that also
    /// reports which modules were carried forward rather than re-detected.
    /// </summary>
    /// <remarks>
    /// <para><b>A zero-hit scan must never delete a reference.</b> The detection
    /// pass behind <paramref name="hits"/> is a literal <c>\bModule\.</c> token
    /// grep over freshly-generated wrapper C#. Any reference that does not
    /// surface as that exact text — a sibling reached only through the Swift
    /// wrapper, a type used solely in a generic constraint, generated output
    /// that is missing or stale — reads as zero hits. Under the old
    /// strip-then-decide sequence that scan result silently deleted the
    /// reference, which is a hard downstream failure (SWIFTBIND081: the managed
    /// assembly the binding needs is simply not referenced). Retaining a
    /// reference that is no longer needed costs one redundant restore edge.
    /// The trade is therefore deliberately asymmetric: this method takes the
    /// UNION of the fresh hits, the references the previous run put inside the
    /// auto-block, and any hand-authored sibling reference that
    /// <see cref="MigrateSiblingProjectRefs"/> removed from an unmarked
    /// ItemGroup.</para>
    /// <para><b>Bounded by the canonical sibling set.</b> The union cannot grow
    /// without limit: a carried-forward include whose basename no longer
    /// matches any entry in <paramref name="allSiblings"/> (the product was
    /// removed from <c>library.json</c>, renamed, or marked internal) is
    /// pruned. Same semantics the SwiftFrameworkDependency side already uses in
    /// <see cref="OrderInBlockIncludes"/>.</para>
    /// <para><b>Never double-declares.</b> Sibling references that are still
    /// present outside the auto-block after migration (e.g. inside a
    /// conditioned <c>&lt;ItemGroup Condition="..."&gt;</c>, which the
    /// line-based migration deliberately does not touch) suppress their own
    /// emission inside the block.</para>
    /// </remarks>
    public static string ApplyProjectRefs(
        string original,
        AbsolutePath csprojPath,
        IReadOnlyList<DependencyHit> hits,
        IReadOnlyList<DependencyHit> allSiblings,
        out IReadOnlyList<string> carriedForwardModules)
    {
        var hasBegin = original.Contains(PrBeginMarker);
        var hasEnd = original.Contains(PrEndMarker);
        if (hasBegin != hasEnd)
        {
            throw new InvalidOperationException(
                "Asymmetric ProjectReference auto-block markers detected " +
                $"({(hasBegin ? "BEGIN without END" : "END without BEGIN")}). " +
                "Refusing to rewrite to avoid silent double-injection. " +
                "Restore the matching marker (or remove both) and re-run.");
        }
        var hadAutoBlock = hasBegin && hasEnd;

        // Capture what the PREVIOUS run put inside the auto-block before the
        // block is stripped. Read first, decide later: the strip below is
        // unconditional, so anything not captured here is unrecoverable.
        var previousBlockIncludes = hadAutoBlock
            ? ExtractAutoBlockProjectRefs(original)
            : (IReadOnlyList<string>)Array.Empty<string>();

        // Always strip the auto-block; surrounding whitespace becomes
        // a single "\n\n" gap, which is what the re-insertion below expects.
        var content = StripPrAutoBlock(original);

        // Canonical sibling lookup, keyed by csproj basename — the same key
        // the migration and out-of-block scans use.
        var siblingByBasename = new Dictionary<string, DependencyHit>(StringComparer.Ordinal);
        foreach (var sib in allSiblings)
        {
            var basename = Path.GetFileName((string)sib.Csproj);
            // First declaration wins; a duplicate basename inside one library
            // would already be ambiguous for the migration path too.
            if (!siblingByBasename.ContainsKey(basename))
                siblingByBasename.Add(basename, sib);
        }
        var siblingBasenames = new HashSet<string>(siblingByBasename.Keys, StringComparer.Ordinal);

        // Hand-authored sibling refs living OUTSIDE the auto-block, sampled
        // before migration so we can tell "migration moved it into the block"
        // (carry it forward) from "migration left it alone" (suppress ours).
        var manualBefore = ExtractOutsideSiblingRefs(content, siblingBasenames);

        if (!hadAutoBlock)
        {
            // First run: hand-authored sibling refs in unmarked ItemGroups are
            // migrated into the managed block. They are unioned back in below,
            // so the move is never a deletion.
            content = MigrateSiblingProjectRefs(content, siblingBasenames);
        }

        // Whatever sibling refs survive out-of-block are user-authored and
        // stay exactly where they are (subsequent runs never migrate; the
        // line-based migration also skips attributed/conditioned ItemGroups).
        var manualAfter = ExtractOutsideSiblingRefs(content, siblingBasenames);

        var union = new List<DependencyHit>();
        var seenModules = new HashSet<string>(StringComparer.Ordinal);
        var carried = new List<string>();

        void AddCandidate(DependencyHit hit, bool isCarryForward)
        {
            if (string.IsNullOrEmpty(hit.Module))
                return;
            if (hit.Csproj == csprojPath)
                return; // never self-reference
            if (!seenModules.Add(hit.Module))
                return;
            union.Add(hit);
            if (isCarryForward)
                carried.Add(hit.Module);
        }

        // 1. Freshly-detected hits.
        foreach (var hit in hits)
            AddCandidate(hit, isCarryForward: false);

        // 2. References the previous run managed. Pruned against the canonical
        //    sibling set so a removed/renamed product cannot pin itself forever.
        foreach (var include in previousBlockIncludes)
        {
            if (siblingByBasename.TryGetValue(Path.GetFileName(include), out var sib))
                AddCandidate(sib, isCarryForward: true);
        }

        // 3. Hand-authored refs the migration above just removed.
        foreach (var basename in manualBefore)
        {
            if (manualAfter.Contains(basename))
                continue; // still declared out-of-block — handled by the suppression below
            if (siblingByBasename.TryGetValue(basename, out var sib))
                AddCandidate(sib, isCarryForward: true);
        }

        // Suppress emission for siblings still declared outside the block so
        // the reference is never declared twice.
        var preservedModules = new HashSet<string>(
            allSiblings
                .Where(s => manualAfter.Contains(Path.GetFileName((string)s.Csproj)))
                .Select(s => s.Module),
            StringComparer.Ordinal);

        var filteredHits = preservedModules.Count > 0
            ? union.Where(h => !preservedModules.Contains(h.Module)).ToList()
            : union;
        carriedForwardModules = preservedModules.Count > 0
            ? carried.Where(m => !preservedModules.Contains(m)).ToList()
            : carried;

        var newBlock = BuildPrBlock(csprojPath, filteredHits);
        if (newBlock.Length > 0)
        {
            // re.sub(r"\s*</Project>", "\n\n" + new_block + "\n\n</Project>", ..., count=1)
            // The leading \s* eats whatever whitespace was already there so
            // the surrounding spacing is normalized to "\n\n" on every run.
            var pattern = new Regex(@"\s*</Project>");
            content = pattern.Replace(content, ("\n\n" + newBlock + "\n\n</Project>").Replace("$", "$$"), 1);
        }

        return content;
    }

    /// <summary>
    /// Strip any previous auto-detected ProjectReference block and normalize
    /// the surrounding whitespace to a single paragraph break (<c>\n\n</c>).
    /// Always safe — called on every run for idempotence.
    /// </summary>
    private static string StripPrAutoBlock(string content)
    {
        var pattern = new Regex(
            @"\s*" + Regex.Escape(PrBeginMarker) + ".*?" + Regex.Escape(PrEndMarker) + @"\s*",
            RegexOptions.Singleline);
        return pattern.Replace(content, "\n\n");
    }

    /// <summary>
    /// Match a <c>&lt;ProjectReference ... /&gt;</c> element (self-closing or
    /// not) and capture its attribute span. Order-agnostic on purpose:
    /// hand-edited csprojs write <c>Condition</c> or <c>PrivateAssets</c>
    /// before <c>Include</c>, and a ref we fail to see out-of-block would be
    /// emitted a second time inside the auto-block.
    /// </summary>
    private static readonly Regex ProjectRefElementRegex =
        new(@"<ProjectReference\b([^>]*?)/?>", RegexOptions.Singleline);

    private static readonly Regex XmlAttrRegex = new(@"(\w+)\s*=\s*""([^""]*)""");

    /// <summary>
    /// Pull the <c>Include</c> attribute value of every
    /// <c>&lt;ProjectReference&gt;</c> element in <paramref name="span"/>,
    /// in document order.
    /// </summary>
    private static List<string> ExtractProjectRefIncludes(string span)
    {
        var includes = new List<string>();
        foreach (Match m in ProjectRefElementRegex.Matches(span))
        {
            foreach (Match a in XmlAttrRegex.Matches(m.Groups[1].Value))
            {
                if (a.Groups[1].Value == "Include")
                {
                    includes.Add(a.Groups[2].Value);
                    break;
                }
            }
        }
        return includes;
    }

    /// <summary>
    /// Return the <c>Include</c> paths of every <c>&lt;ProjectReference&gt;</c>
    /// inside the auto-detected block of <paramref name="content"/>, in
    /// document order. Must be called on the ORIGINAL content, before
    /// <see cref="StripPrAutoBlock"/> removes the block.
    /// </summary>
    private static List<string> ExtractAutoBlockProjectRefs(string content)
    {
        var blockMatch = new Regex(
            Regex.Escape(PrBeginMarker) + "(.*?)" + Regex.Escape(PrEndMarker),
            RegexOptions.Singleline).Match(content);
        return blockMatch.Success
            ? ExtractProjectRefIncludes(blockMatch.Groups[1].Value)
            : new List<string>();
    }

    /// <summary>
    /// Return the basenames of every <c>&lt;ProjectReference&gt;</c> in
    /// <paramref name="content"/> that points at one of the
    /// <paramref name="siblingBasenames"/>. Caller is expected to pass content
    /// AFTER <see cref="StripPrAutoBlock"/> has removed the auto-managed block,
    /// so only hand-authored out-of-block refs remain.
    /// </summary>
    private static HashSet<string> ExtractOutsideSiblingRefs(string content, HashSet<string> siblingBasenames)
    {
        var refs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var include in ExtractProjectRefIncludes(content))
        {
            var bn = Path.GetFileName(include);
            if (siblingBasenames.Contains(bn))
                refs.Add(bn);
        }
        return refs;
    }

    /// <summary>
    /// Direct line-by-line port of <c>migrate_sibling_refs</c> at
    /// <c>scripts/detect-dependencies.sh:563–616</c>.
    /// Walks each <c>&lt;ItemGroup&gt;</c>; drops sibling
    /// <c>&lt;ProjectReference&gt;</c> items; if the group is empty after
    /// filtering, drops the whole group AND any preceding blank line.
    /// </summary>
    private static string MigrateSiblingProjectRefs(string content, HashSet<string> siblingBasenames)
    {
        var lines = content.Split('\n');
        var output = new List<string>(lines.Length);
        var prRegex = new Regex(@"<ProjectReference\s+Include=""([^""]+)""");

        var i = 0;
        while (i < lines.Length)
        {
            var line = lines[i];
            var stripped = line.Trim();

            // Bash: `if stripped == "<ItemGroup>"`. Strict equality — attributed
            // groups (`<ItemGroup Condition="...">`) are NOT processed.
            if (stripped == "<ItemGroup>")
            {
                var groupLines = new List<string> { line };
                i++;
                while (i < lines.Length && lines[i].Trim() != "</ItemGroup>")
                {
                    groupLines.Add(lines[i]);
                    i++;
                }
                if (i < lines.Length)
                {
                    groupLines.Add(lines[i]); // </ItemGroup>
                    i++;
                }

                var filtered = new List<string> { groupLines[0] };
                for (var j = 1; j < groupLines.Count - 1; j++)
                {
                    var body = groupLines[j];
                    var m = prRegex.Match(body);
                    if (m.Success && siblingBasenames.Contains(Path.GetFileName(m.Groups[1].Value)))
                        continue;
                    filtered.Add(body);
                }
                filtered.Add(groupLines[^1]);

                var hasBody = false;
                for (var j = 1; j < filtered.Count - 1; j++)
                {
                    if (filtered[j].Trim().Length > 0)
                    {
                        hasBody = true;
                        break;
                    }
                }

                if (!hasBody)
                {
                    while (output.Count > 0 && output[^1].Trim().Length == 0)
                        output.RemoveAt(output.Count - 1);
                    continue;
                }

                output.AddRange(filtered);
                continue;
            }

            output.Add(line);
            i++;
        }

        return string.Join("\n", output);
    }

    /// <summary>
    /// Emit the ProjectReference auto-block for <paramref name="hits"/>.
    /// An empty list yields an empty string — i.e. no block at all.
    /// <b>Callers must pass the full union</b> (fresh hits + carried-forward
    /// refs), never a raw scan result: an empty return re-inserts nothing, so
    /// handing this method a zero-hit scan is exactly how a needed reference
    /// gets dropped. See <see cref="ApplyProjectRefs(string, AbsolutePath, IReadOnlyList{DependencyHit}, IReadOnlyList{DependencyHit}, out IReadOnlyList{string})"/>.
    /// </summary>
    private static string BuildPrBlock(AbsolutePath csprojPath, IReadOnlyList<DependencyHit> hits)
    {
        if (hits.Count == 0)
            return "";

        var srcDir = Path.GetDirectoryName((string)csprojPath)!;
        var lines = new List<string>
        {
            PrBeginMarker,
            "  <ItemGroup>",
        };
        // Bash: sorted(hits, key=lambda s: s["module"])
        foreach (var h in hits.OrderBy(h => h.Module, StringComparer.Ordinal))
        {
            var rel = Path.GetRelativePath(srcDir, (string)h.Csproj);
            lines.Add($"    <ProjectReference Include=\"{rel}\" />");
        }
        lines.Add("  </ItemGroup>");
        lines.Add("  " + PrEndMarker);
        return string.Join("\n", lines);
    }

    // ────────────────────────────────────────────────────────────────────────
    // Helpers
    // ────────────────────────────────────────────────────────────────────────

    private static string ReplaceFirst(string content, string find, string replace)
    {
        var idx = content.IndexOf(find, StringComparison.Ordinal);
        if (idx < 0)
            return content;
        return string.Concat(content.AsSpan(0, idx), replace, content.AsSpan(idx + find.Length));
    }

    // ────────────────────────────────────────────────────────────────────────
    // Encoding-preserving I/O
    // ────────────────────────────────────────────────────────────────────────

    private static readonly byte[] Utf8Bom = { 0xEF, 0xBB, 0xBF };

    /// <summary>
    /// Read a csproj while preserving BOM presence and the original line
    /// ending convention. The returned <c>Content</c> is normalized to LF
    /// for downstream string processing.
    /// </summary>
    internal static (string Content, byte[]? Bom, string LineEnding) ReadCsproj(AbsolutePath path)
    {
        var bytes = File.ReadAllBytes(path);
        byte[]? bom = null;
        var startIdx = 0;
        if (bytes.Length >= 3 && bytes[0] == Utf8Bom[0] && bytes[1] == Utf8Bom[1] && bytes[2] == Utf8Bom[2])
        {
            bom = Utf8Bom;
            startIdx = 3;
        }
        var raw = System.Text.Encoding.UTF8.GetString(bytes, startIdx, bytes.Length - startIdx);

        // Detect line ending: if any \r\n exists at all, treat as CRLF.
        // (csprojs are uniform within a single file in practice; mixed-ending
        // input is corrupt regardless of which sequence we pick.)
        var lineEnding = raw.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

        // Normalize to LF for the pure-function rewrite paths.
        var content = lineEnding == "\r\n" ? raw.Replace("\r\n", "\n") : raw;
        return (content, bom, lineEnding);
    }

    /// <summary>
    /// Write a csproj with the originally-detected BOM presence and line
    /// ending convention. Uses an atomic temp-file + rename so that a partial
    /// write cannot leave a half-rewritten csproj on disk.
    /// </summary>
    internal static void WriteCsproj(AbsolutePath path, string content, byte[]? bom, string lineEnding)
    {
        if (lineEnding == "\r\n")
            content = content.Replace("\n", "\r\n");

        var contentBytes = System.Text.Encoding.UTF8.GetBytes(content);
        var tempPath = (string)path + ".tmp";

        try
        {
            using (var fs = File.Create(tempPath))
            {
                if (bom is not null)
                    fs.Write(bom, 0, bom.Length);
                fs.Write(contentBytes, 0, contentBytes.Length);
            }
            // Move with overwrite is the closest the .NET file APIs come to
            // an atomic publish on POSIX (rename(2) is atomic on the same fs).
            File.Move(tempPath, path, overwrite: true);
        }
        catch
        {
            // Best-effort: clean up the temp file so a failed write doesn't
            // leave litter alongside the csproj.
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            throw;
        }
    }
}

/// <summary>
/// A prepared csproj rewrite that has not yet been written to disk. Returned
/// by <see cref="CsprojRewriter.PrepareProjectRefs"/> so callers can compute
/// every csproj's new content first and only commit if every product passes,
/// approximating an all-or-nothing batch rewrite.
/// </summary>
public sealed class CsprojWriteOp
{
    public AbsolutePath Path { get; }
    private readonly string _content;
    private readonly byte[]? _bom;
    private readonly string _lineEnding;

    internal CsprojWriteOp(AbsolutePath path, string content, byte[]? bom, string lineEnding)
    {
        Path = path;
        _content = content;
        _bom = bom;
        _lineEnding = lineEnding;
    }

    /// <summary>
    /// Atomically write the rewritten content to <see cref="Path"/> via
    /// temp-file + rename.
    /// </summary>
    public void Commit() => CsprojRewriter.WriteCsproj(Path, _content, _bom, _lineEnding);
}
