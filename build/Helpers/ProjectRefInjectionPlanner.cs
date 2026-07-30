using Nuke.Common.IO;
using SwiftBindings.Build.Models;

namespace SwiftBindings.Build.Helpers;

/// <summary>
/// One product's inputs for a ProjectReference injection pass.
/// </summary>
/// <param name="Framework">Framework name, used in diagnostics.</param>
/// <param name="Csproj">Absolute path to the product's <c>SwiftBindings.*.csproj</c>.</param>
/// <param name="XcframeworkPlist">Absolute path to <c>&lt;framework&gt;.xcframework/Info.plist</c>.</param>
/// <param name="SwiftBindingDir">Absolute path to the SDK's <c>swift-binding/</c> output directory.</param>
public sealed record ProjectRefTarget(
    string Framework,
    AbsolutePath Csproj,
    AbsolutePath XcframeworkPlist,
    AbsolutePath SwiftBindingDir);

/// <summary>
/// The planned outcome for one product. Either a (possibly null) prepared
/// write, or a skip with the reason the product could not be scanned.
/// </summary>
/// <param name="Target">The product this entry describes.</param>
/// <param name="DetectedModules">Sibling modules the generated-C# scan found, sorted ordinal.</param>
/// <param name="CarriedForwardModules">
/// Modules retained in the rewritten block that the scan did NOT reproduce.
/// </param>
/// <param name="WriteOp">Prepared rewrite, or <c>null</c> when the csproj is already correct.</param>
/// <param name="SkipReason">Non-null iff the product was skipped (freshness failure).</param>
public sealed record ProjectRefPlanEntry(
    ProjectRefTarget Target,
    IReadOnlyList<string> DetectedModules,
    IReadOnlyList<string> CarriedForwardModules,
    CsprojWriteOp? WriteOp,
    string? SkipReason)
{
    public bool Skipped => SkipReason is not null;
}

/// <summary>
/// Plans the ProjectReference auto-block rewrite for a whole library, one
/// product at a time.
///
/// <para><b>Why per-product and not all-or-nothing.</b> The freshness check
/// answers a strictly per-product question: "is the generated C# I am about to
/// scan current for THIS product?". A stale or never-built product tells us
/// nothing about its siblings, so failing that check must disqualify that one
/// product's scan — not cancel the writes for every other product in the
/// library. The previous design ran the freshness check for every product up
/// front and let the first failure throw before any csproj was written, which
/// meant one un-built product silently left the entire library's
/// ProjectReference blocks un-updated. Because the SwiftFrameworkDependency
/// injector (which has no freshness dependency at all — it reads the shipped
/// <c>.swiftinterface</c>) always writes per-product, the two marker blocks
/// then drifted apart with nothing to flag it. Skipping is per-product here so
/// the two injectors agree.</para>
///
/// <para><b>Skipping is not silent.</b> Skips are returned as data, not
/// swallowed: the caller logs each one and is expected to fail the target after
/// committing the writes it could safely make. Doing the safe work first and
/// failing afterwards is strictly better than failing before it — the fresh
/// products end up correct either way, and the operator still gets a non-zero
/// exit naming exactly what was not processed.</para>
///
/// <para><b>What is still all-or-nothing.</b> A malformed csproj (asymmetric
/// auto-block markers) throws out of <see cref="Plan"/> and aborts the batch
/// before anything is committed. That is a different failure class — the file
/// needs human repair, and refusing to touch anything keeps a corrupt marker
/// pair from being double-injected.</para>
/// </summary>
public static class ProjectRefInjectionPlanner
{
    /// <summary>
    /// Freshness-check, scan and prepare a rewrite for each target.
    /// Performs no disk writes — every returned <see cref="CsprojWriteOp"/> is
    /// still uncommitted.
    /// </summary>
    /// <param name="targets">Products to process, in order.</param>
    /// <param name="allSiblings">
    /// Every non-internal product in the library (not just the selected ones):
    /// a product may reference a sibling that was not selected for this run.
    /// </param>
    /// <param name="freshnessCheck">
    /// Override for the freshness gate. Defaults to
    /// <see cref="Freshness.CheckFresh"/>. Injected so the per-product skip
    /// behavior is testable without fabricating stamp mtimes.
    /// </param>
    public static IReadOnlyList<ProjectRefPlanEntry> Plan(
        IReadOnlyList<ProjectRefTarget> targets,
        IReadOnlyList<DependencyHit> allSiblings,
        Func<ProjectRefTarget, IReadOnlyList<AbsolutePath>>? freshnessCheck = null)
    {
        freshnessCheck ??= t => Freshness.CheckFresh(t.Framework, t.Csproj, t.XcframeworkPlist, t.SwiftBindingDir);

        var entries = new List<ProjectRefPlanEntry>(targets.Count);
        foreach (var target in targets)
        {
            IReadOnlyList<AbsolutePath> csFiles;
            try
            {
                csFiles = freshnessCheck(target);
            }
            catch (InvalidOperationException ex)
            {
                // Per-product skip. The csproj is left byte-identical, so the
                // ProjectReference block this product already has survives —
                // scanning stale output could only ever produce a WORSE block.
                entries.Add(new ProjectRefPlanEntry(
                    target,
                    Array.Empty<string>(),
                    Array.Empty<string>(),
                    WriteOp: null,
                    SkipReason: ex.Message));
                continue;
            }

            var content = GeneratedCsScanner.Collect(csFiles);

            var hits = new List<DependencyHit>();
            foreach (var sib in allSiblings)
            {
                if (sib.Csproj == target.Csproj)
                    continue; // skip self
                if (string.IsNullOrEmpty(sib.Module))
                    continue;
                if (GeneratedCsScanner.ContainsModuleReference(content, sib.Module))
                    hits.Add(sib);
            }

            var op = CsprojRewriter.PrepareProjectRefs(target.Csproj, hits, allSiblings, out var carried);

            entries.Add(new ProjectRefPlanEntry(
                target,
                hits.Select(h => h.Module).OrderBy(m => m, StringComparer.Ordinal).ToList(),
                carried,
                op,
                SkipReason: null));
        }

        return entries;
    }
}
