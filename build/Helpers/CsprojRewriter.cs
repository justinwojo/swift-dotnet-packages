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
    /// <c>"../StripeCore/StripeCore.xcframework"</c>). The block re-emits these
    /// in ordinal-sorted order to match the bash output.
    /// </param>
    /// <param name="knownSiblings">
    /// Full set of canonical sibling include paths for the library (used to
    /// distinguish sibling vs non-sibling SFD items during first-run migration).
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

        // ── Step 2: build the new auto-block ──
        var depsBlock = BuildSfdBlock(depIncludes, preservedAttrs);

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

        if (hasMarkers)
        {
            // Subsequent run: replace the in-block content. Surrounding
            // whitespace is preserved on purpose so re-runs are no-ops.
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
            // First run: migrate hand-authored sibling SFD entries OUT of
            // their original ItemGroups (so they don't double-up with the
            // new auto-block).
            content = MigrateSfdSiblings(content, knownSiblings);

            if (depsBlock.Length > 0)
            {
                // str.replace() in Python without count replaces ALL — but
                // </Project> is unique in a csproj, so a single-shot replace
                // is equivalent.
                content = ReplaceFirst(content, "</Project>", depsBlock + "\n\n</Project>");
            }
        }

        return content;
    }

    private static string BuildSfdBlock(
        IReadOnlyCollection<string> depIncludes,
        Dictionary<string, List<KeyValuePair<string, string>>> preservedAttrs)
    {
        if (depIncludes.Count == 0)
            return "";

        var lines = new List<string>
        {
            SfdBeginMarker,
            "  <ItemGroup>",
        };
        // Ordinal sort matches Python's sorted() default.
        foreach (var inc in depIncludes.OrderBy(s => s, StringComparer.Ordinal))
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

    /// <summary>
    /// Direct line-by-line port of the SFD migration state machine at
    /// <c>scripts/detect-dependencies.sh:909–984</c>. Walks the csproj line by
    /// line and:
    /// <list type="bullet">
    ///   <item>Identifies <c>&lt;ItemGroup&gt;</c> blocks containing any
    ///         <c>SwiftFrameworkDependency</c> entry.</item>
    ///   <item>Drops sibling SFD lines from those blocks.</item>
    ///   <item>If the resulting block is empty, drops the whole
    ///         <c>&lt;ItemGroup&gt;</c> AND any preceding comment + blank line.</item>
    ///   <item>Keeps non-sibling entries (e.g. <c>Stripe3DS2.xcframework</c>
    ///         which is internal but not in the canonical sibling set).</item>
    /// </list>
    /// </summary>
    private static string MigrateSfdSiblings(string content, IReadOnlyCollection<string> knownSiblings)
    {
        var manualPattern = new Regex(@"<SwiftFrameworkDependency\s+Include=""([^""]+)""");
        if (!manualPattern.IsMatch(content))
            return content;

        var sibSet = knownSiblings as HashSet<string>
            ?? new HashSet<string>(knownSiblings, StringComparer.Ordinal);

        var lines = content.Split('\n');
        var output = new List<string>(lines.Length);

        var inSfdItemGroup = false;
        var itemGroupStart = -1;
        var itemGroupEmptyAfterRemoval = true;

        var sfdLineRegex = new Regex(@"<SwiftFrameworkDependency\s+Include=""([^""]+)""");

        var i = 0;
        while (i < lines.Length)
        {
            var line = lines[i];
            var stripped = line.Trim();

            // Bash: `if '<ItemGroup>' in stripped and i + 1 < len(lines):`
            // Python `in` is substring; the literal `<ItemGroup>` substring
            // implicitly excludes attributed groups like `<ItemGroup Condition="...">`
            // because the closing `>` would shift to after the attribute list.
            if (!inSfdItemGroup && stripped.Contains("<ItemGroup>", StringComparison.Ordinal) && i + 1 < lines.Length)
            {
                // Look ahead to detect whether this group contains an SFD
                // entry. We stop at </ItemGroup> so we don't bleed into the
                // next group.
                var hasSfd = false;
                for (var p = i + 1; p < lines.Length; p++)
                {
                    if (lines[p].Contains("SwiftFrameworkDependency", StringComparison.Ordinal))
                    {
                        hasSfd = true;
                        break;
                    }
                    if (lines[p].Contains("</ItemGroup>", StringComparison.Ordinal))
                        break;
                }

                if (hasSfd)
                {
                    inSfdItemGroup = true;
                    itemGroupStart = output.Count;
                    itemGroupEmptyAfterRemoval = true;
                    output.Add(line);
                    i++;
                    continue;
                }
            }

            if (inSfdItemGroup)
            {
                if (stripped.Contains("</ItemGroup>", StringComparison.Ordinal))
                {
                    inSfdItemGroup = false;
                    if (itemGroupEmptyAfterRemoval)
                    {
                        // Roll back the entire group plus any preceding
                        // SFD-related comment AND any preceding blank line.
                        output.RemoveRange(itemGroupStart, output.Count - itemGroupStart);
                        while (output.Count > 0 && output[^1].Trim().StartsWith("<!--", StringComparison.Ordinal))
                            output.RemoveAt(output.Count - 1);
                        while (output.Count > 0 && output[^1].Trim().Length == 0)
                            output.RemoveAt(output.Count - 1);
                    }
                    else
                    {
                        output.Add(line);
                    }
                    i++;
                    continue;
                }

                var sfdMatch = sfdLineRegex.Match(stripped);
                if (sfdMatch.Success)
                {
                    var includePath = sfdMatch.Groups[1].Value;
                    if (sibSet.Contains(includePath))
                    {
                        // Sibling — drop. The new auto-block will re-emit it.
                        i++;
                        continue;
                    }
                    // Non-sibling: keep it (not part of cross-module deps).
                    itemGroupEmptyAfterRemoval = false;
                    output.Add(line);
                    i++;
                    continue;
                }

                if (stripped.StartsWith("<!--", StringComparison.Ordinal))
                {
                    // Drop SFD-related comments; keep unrelated ones.
                    if (stripped.Contains("SwiftFrameworkDependency", StringComparison.Ordinal)
                        || stripped.Contains("ObjC-only", StringComparison.Ordinal))
                    {
                        i++;
                        continue;
                    }
                    output.Add(line);
                    i++;
                    continue;
                }

                if (stripped.Length > 0)
                    itemGroupEmptyAfterRemoval = false;
                output.Add(line);
                i++;
                continue;
            }

            output.Add(line);
            i++;
        }

        return string.Join("\n", output);
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
        var op = PrepareProjectRefs(csprojPath, hits, allSiblings);
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
    public static CsprojWriteOp? PrepareProjectRefs(
        AbsolutePath csprojPath,
        IReadOnlyList<DependencyHit> hits,
        IReadOnlyList<DependencyHit> allSiblings)
    {
        var (original, bom, lineEnding) = ReadCsproj(csprojPath);
        var rewritten = ApplyProjectRefs(original, csprojPath, hits, allSiblings);
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

        // Always strip the auto-block first; surrounding whitespace becomes
        // a single "\n\n" gap, which is what the re-insertion below expects.
        var content = StripPrAutoBlock(original);

        var siblingBasenames = new HashSet<string>(
            allSiblings.Select(s => Path.GetFileName((string)s.Csproj)),
            StringComparer.Ordinal);

        var filteredHits = new List<DependencyHit>(hits);

        if (!hadAutoBlock)
        {
            // First run: union hand-authored sibling refs INTO the detection
            // hits BEFORE stripping them, so refs grep missed are carried
            // forward rather than silently deleted.
            var manualBasenames = ExtractOutsideSiblingRefs(content, siblingBasenames);
            var existingModules = new HashSet<string>(
                filteredHits.Select(h => h.Module),
                StringComparer.Ordinal);
            foreach (var sib in allSiblings)
            {
                if (manualBasenames.Contains(Path.GetFileName((string)sib.Csproj))
                    && !existingModules.Contains(sib.Module))
                {
                    filteredHits.Add(sib);
                    existingModules.Add(sib.Module);
                }
            }

            content = MigrateSiblingProjectRefs(content, siblingBasenames);
        }
        else
        {
            // Subsequent runs: preserve out-of-block hand-authored refs as
            // an escape hatch. De-dupe by suppressing detection hits that
            // duplicate a preserved ref.
            var preservedBasenames = ExtractOutsideSiblingRefs(content, siblingBasenames);
            var preservedModules = new HashSet<string>(
                allSiblings
                    .Where(s => preservedBasenames.Contains(Path.GetFileName((string)s.Csproj)))
                    .Select(s => s.Module),
                StringComparer.Ordinal);
            if (preservedModules.Count > 0)
            {
                filteredHits = filteredHits
                    .Where(h => !preservedModules.Contains(h.Module))
                    .ToList();
            }
        }

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
    /// Return the basenames of every <c>&lt;ProjectReference&gt;</c> in
    /// <paramref name="content"/> that points at one of the
    /// <paramref name="siblingBasenames"/>. Caller is expected to pass content
    /// AFTER <see cref="StripPrAutoBlock"/> has removed the auto-managed block,
    /// so only hand-authored out-of-block refs remain.
    /// </summary>
    private static HashSet<string> ExtractOutsideSiblingRefs(string content, HashSet<string> siblingBasenames)
    {
        var refs = new HashSet<string>(StringComparer.Ordinal);
        var pattern = new Regex(@"<ProjectReference\s+Include=""([^""]+)""");
        foreach (Match m in pattern.Matches(content))
        {
            var include = m.Groups[1].Value;
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
