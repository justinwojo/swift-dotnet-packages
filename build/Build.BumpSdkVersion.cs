using System.Text.RegularExpressions;
using Nuke.Common;
using Serilog;

partial class Build
{
    // --- Cross-framework SwiftBindings.Apple.* dependency-pin matching ---
    //
    // These three patterns are the SINGLE source of truth for finding and
    // rewriting cross-framework dependency pins, shared by BumpAppleVersionInternal
    // (rewrite) and RegressionValidate's DiscoverCrossFrameworkRefs (read). Using
    // one definition for both is deliberate: if the bump and the coherence guard
    // matched pins differently, the guard could report "clean" on exactly the pin
    // the bump missed — a false-negative in the safety net.
    //
    // The tag pattern is tolerant of real MSBuild attribute variation: any
    // attribute order (Version before or after Include), whitespace around '=',
    // and intervening attributes (e.g. Condition). It matches the whole
    // <PackageReference ...> opening tag (self-closing or not) bounded by the
    // next '>' (attribute values never contain '>'). The Include/Version sub-
    // patterns then locate those attributes within the matched tag regardless of
    // position. Only the Version-ATTRIBUTE form is matched; a child-element
    // <Version> pin carries no attribute, so the version sub-pattern misses it.
    // That is NOT silently tolerated: AssertCrossFrameworkCoherence rejects any
    // Apple PackageReference tag it can't read a version from, because the broad
    // <Version> regex in BumpAppleVersionInternal WOULD still rewrite a child-
    // element pin's value while discovery and packing never see it — exactly the
    // bump-vs-guard false negative the single source of truth exists to prevent.
    // Fail loud rather than let the standalone bump silently skew the pin.

    /// <summary>A whole <c>&lt;PackageReference …&gt;</c> opening tag carrying an
    /// <c>Include="SwiftBindings.Apple.X"</c> attribute, any attribute order.
    /// Either quote style (<c>"</c> or <c>'</c>) — XML/MSBuild allows both, and a
    /// single-quoted pin the tag pattern missed would be invisible to the bump,
    /// the coherence guard, AND packing discovery, reopening the silent-skew hole
    /// the single source of truth exists to close. The Include quote is captured
    /// (<c>q</c>) and backreferenced as its closer (<c>\k&lt;q&gt;</c>), matching
    /// the Include/Version sub-patterns exactly — so a malformed mixed-quote tag
    /// (<c>Include="…'</c>) is rejected here too rather than matching the tag but
    /// parsing an empty name downstream in the coherence guard.</summary>
    static readonly Regex AppleCrossRefTagPattern = new(
        @"<PackageReference\b[^>]*?\bInclude\s*=\s*(?<q>[""'])SwiftBindings\.Apple\.[^""']+\k<q>[^>]*?>",
        RegexOptions.Compiled);

    /// <summary>The <c>Include="SwiftBindings.Apple.X"</c> attribute, capturing X.
    /// <c>q</c> backreferences the opening quote so the close must match (no
    /// <c>Include="…'</c> mixed-quote false match); either style accepted.</summary>
    static readonly Regex AppleCrossRefIncludePattern = new(
        @"\bInclude\s*=\s*(?<q>[""'])SwiftBindings\.Apple\.(?<name>[^""']+)\k<q>",
        RegexOptions.Compiled);

    /// <summary>The <c>Version="N.N.N"</c> attribute. Three NAMED groups —
    /// <c>pre</c> (<c>Version="</c>, including the opening quote), <c>ver</c> (the
    /// numeric version), and <c>post</c> (the closing quote). All named (no unnamed
    /// groups) and referenced by name on purpose: .NET numbers unnamed groups
    /// before named ones, so a mixed pattern would make <c>Groups[3]</c> the
    /// version digits, not the closing quote — a rewrite of
    /// <c>pre + target + Groups[3]</c> would then duplicate the old version and
    /// drop the quote. Naming every group removes that footgun:
    /// <c>Groups["pre"]</c>/<c>Groups["post"]</c> can never be confused with the
    /// captured digits. The quote char is itself a group (<c>q</c>) and
    /// <c>post</c> backreferences it, so either quote style is accepted and the
    /// original style is preserved on rewrite (<c>pre</c> carries the opener,
    /// <c>post</c> the matching closer).</summary>
    static readonly Regex AppleCrossRefVersionPattern = new(
        @"(?<pre>\bVersion\s*=\s*(?<q>[""']))(?<ver>[\d.]+)(?<post>\k<q>)",
        RegexOptions.Compiled);

    /// <summary>
    /// Sweep every package csproj under <c>libraries/</c> and
    /// <c>apple-frameworks/</c> and pin its
    /// <c>&lt;Project Sdk="SwiftBindings.Sdk/X.Y.Z"&gt;</c> attribute to the
    /// requested <c>--version</c>. Idempotent: files already at the target
    /// version are left untouched (no mtime change). Test csprojs use
    /// <c>Microsoft.NET.Sdk</c> so they are skipped automatically.
    ///
    /// Without this step, downstream csprojs can drift from the SDK version
    /// being validated — NuGet then silently resolves the OLD SDK from cache
    /// or nuget.org, and the regression run validates the wrong version.
    /// </summary>
    Target BumpSdkVersion => _ => _
        .Description("Pin every package csproj to SwiftBindings.Sdk/<--version>")
        .Requires(() => Version)
        .Executes(() => BumpSdkVersionInternal(Version!));

    /// <summary>
    /// Walk every <c>*.csproj</c> under <c>libraries/</c> and
    /// <c>apple-frameworks/</c>, replacing the SDK version in the
    /// <c>Sdk="SwiftBindings.Sdk/X.Y.Z"</c> attribute with
    /// <paramref name="targetVersion"/>. Returns the number of files
    /// rewritten. Throws if a csproj references <c>SwiftBindings.Sdk</c>
    /// in a form the attribute regex doesn't match — that almost always
    /// means malformed XML rather than a deliberate variant, and silently
    /// skipping it would defeat the point of the sweep.
    ///
    /// Also wipes each inspected csproj's sibling <c>obj/</c> directory.
    /// The generator writes <c>.cs</c>, <c>Wrapper.swift</c>, the wrapper
    /// xcframework, and <c>binding-report.json</c> into
    /// <c>obj/Debug/.../swift-binding/</c>; <c>dotnet build</c> only
    /// regenerates them when its own input fingerprint changes. Bumping
    /// SDK version alone does not invalidate that fingerprint, and a
    /// no-op bump (csproj already pinned) doesn't even touch the csproj
    /// mtime — so stale generated source from a prior generator commit
    /// survives the next validation run and produces phantom regressions.
    /// Wiping obj/ on every inspected csproj forces regeneration with
    /// the SDK actually being validated.
    /// </summary>
    int BumpSdkVersionInternal(string targetVersion)
    {
        Log.Information("--- bump SDK csproj refs → {Version} ---", targetVersion);

        var pattern = new Regex(@"Sdk=""SwiftBindings\.Sdk/[^""]*""", RegexOptions.Compiled);
        var replacement = $@"Sdk=""SwiftBindings.Sdk/{targetVersion}""";

        var roots = new[] { LibrariesDir, AppleFrameworksDir };
        var updated = 0;
        var alreadyPinned = 0;
        var inspected = 0;
        var objWiped = 0;

        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) continue;
            foreach (var csproj in Directory.EnumerateFiles(root, "*.csproj", SearchOption.AllDirectories))
            {
                var content = File.ReadAllText(csproj);
                if (!content.Contains("SwiftBindings.Sdk/", StringComparison.Ordinal))
                    continue;

                inspected++;
                // Use a delegate so '$' in targetVersion (unlikely but possible)
                // isn't reinterpreted as a regex backreference.
                var rewritten = pattern.Replace(content, _ => replacement);
                if (rewritten == content)
                {
                    if (content.Contains(replacement, StringComparison.Ordinal))
                    {
                        alreadyPinned++;
                    }
                    else
                    {
                        throw new InvalidOperationException(
                            $"BumpSdkVersion: {csproj} references SwiftBindings.Sdk " +
                            "but the <Project Sdk=...> regex didn't match. " +
                            "Check the file's SDK attribute formatting.");
                    }
                }
                else
                {
                    File.WriteAllText(csproj, rewritten);
                    updated++;
                    Log.Information("  bumped: {Path}", csproj);
                }

                var objDir = Path.Combine(Path.GetDirectoryName(csproj)!, "obj");
                if (Directory.Exists(objDir))
                {
                    Directory.Delete(objDir, recursive: true);
                    objWiped++;
                }
            }
        }

        Log.Information(
            "--- bump complete: {Updated} updated, {Already} already pinned, {Wiped} obj/ wiped ({Inspected} package csproj(s) total) ---",
            updated, alreadyPinned, objWiped, inspected);
        return updated;
    }

    /// <summary>
    /// Sweep every csproj under <c>apple-frameworks/</c> and pin its
    /// <c>&lt;Version&gt;NN.N.N&lt;/Version&gt;</c> element to the requested
    /// <c>--apple-version</c>. The Apple supplement train (e.g. 26.2.3) is
    /// versioned independently of the SDK lane and isn't covered by
    /// <see cref="BumpSdkVersion"/>, which only rewrites the
    /// <c>Sdk="SwiftBindings.Sdk/X.Y.Z"</c> attribute.
    ///
    /// Idempotent: files already at the target version are left untouched.
    /// Only csprojs that import <c>SwiftBindings.Sdk</c> are touched — test
    /// csprojs use <c>Microsoft.NET.Sdk</c> and don't carry the train
    /// <c>&lt;Version&gt;</c> element.
    /// </summary>
    Target BumpAppleVersion => _ => _
        .Description("Pin every apple-framework csproj's <Version> to <--apple-version>")
        .Requires(() => AppleVersion)
        .Executes(() => BumpAppleVersionInternal(AppleVersion!));

    /// <summary>
    /// Walk every <c>*.csproj</c> under <c>apple-frameworks/</c> and replace
    /// the apple-train package version (digit-and-dot content only — e.g.
    /// <c>26.2.1</c> → <c>26.2.3</c>) with <paramref name="targetVersion"/>.
    /// Returns the number of files rewritten. Throws if a csproj imports
    /// the SDK but has no recognizable <c>&lt;Version&gt;</c> element — the
    /// apple-framework convention requires one, so a missing match almost
    /// always means a malformed csproj.
    ///
    /// The digit-and-dot guard on the content keeps this from matching
    /// non-numeric <c>&lt;Version&gt;</c> elements (e.g. third-party
    /// libraries using prerelease tags), but is paired with the
    /// apple-frameworks/ root constraint as defense in depth — third-party
    /// libraries are versioned per upstream release and must not be touched
    /// by an SDK/Apple release sweep.
    ///
    /// Also rewrites cross-framework dependency pins of the shape
    /// <c>&lt;PackageReference Include="SwiftBindings.Apple.X" Version="Y" /&gt;</c>
    /// (e.g. RealityKit→RealityFoundation, MatterSupport→Matter) to the same
    /// target version. These edges MUST move in lockstep with the Apple train:
    /// a consumer regenerated by the current generator can emit calls against a
    /// sibling proxy's newer ABI (e.g. the <c>ownsContainer</c> ctor argument on
    /// owned-existential returns), and a stale pin pulls an older published
    /// sibling whose API lacks it — a hard compile break in generated code. The
    /// standalone <c>&lt;Version&gt;</c> element is XML element content; the
    /// dependency pin is an attribute, so it needs its own pattern.
    ///
    /// Unlike <see cref="BumpSdkVersionInternal"/>, this does NOT wipe
    /// sibling obj/ directories: the package version doesn't feed the
    /// generator's input fingerprint, so stale generated source is fine.
    /// When called from RegressionValidate, BumpSdkVersionInternal has
    /// already wiped obj/ in the same pre-flight, so doing it again here
    /// would be redundant.
    /// </summary>
    int BumpAppleVersionInternal(string targetVersion)
    {
        Log.Information("--- bump Apple csproj versions → {Version} ---", targetVersion);

        var pattern = new Regex(@"<Version>[\d.]+</Version>", RegexOptions.Compiled);
        var replacement = $"<Version>{targetVersion}</Version>";

        var updated = 0;
        var alreadyPinned = 0;
        var inspected = 0;
        var crossRefsBumped = 0;
        var written = 0;

        if (!Directory.Exists(AppleFrameworksDir))
        {
            Log.Information("--- no apple-frameworks/ directory, skipping ---");
            return 0;
        }

        foreach (var csproj in Directory.EnumerateFiles(AppleFrameworksDir, "*.csproj", SearchOption.AllDirectories))
        {
            var content = File.ReadAllText(csproj);
            if (!content.Contains("SwiftBindings.Sdk/", StringComparison.Ordinal))
                continue;

            inspected++;

            // 1. Own <Version> element. This pattern is deliberately broad — it rewrites
            //    EVERY numeric <Version>N.N.N</Version> element in the file, not a property
            //    scoped to one PropertyGroup. In an apple-framework csproj the only such
            //    element is the package's own train version; cross-ref pins use the Version
            //    attribute (step 2) and a child-element pin would be caught by the coherence
            //    guard, never reaching disk in an unhandled form. The match/throw logic gates
            //    on this alone so the "missing <Version>" invariant is unchanged by step 2.
            var afterVersion = pattern.Replace(content, _ => replacement);
            if (afterVersion == content)
            {
                if (content.Contains(replacement, StringComparison.Ordinal))
                {
                    alreadyPinned++;
                }
                else
                {
                    throw new InvalidOperationException(
                        $"BumpAppleVersion: {csproj} has no <Version>NN.N.N</Version> element matching the apple-framework convention. " +
                        "Check the file's <Version> formatting.");
                }
            }

            // 2. Cross-framework SwiftBindings.Apple.* dependency pins → target version.
            //    Rewrite the Version attribute within each Apple PackageReference tag,
            //    leaving the rest of the tag (Include name, Condition, ordering) intact.
            var rewritten = AppleCrossRefTagPattern.Replace(afterVersion, tag =>
                AppleCrossRefVersionPattern.Replace(tag.Value,
                    v => v.Groups["pre"].Value + targetVersion + v.Groups["post"].Value));
            if (rewritten != afterVersion)
                crossRefsBumped++;

            if (rewritten != content)
            {
                File.WriteAllText(csproj, rewritten);
                written++;
                if (afterVersion != content) updated++;
                Log.Information("  bumped: {Path}", csproj);
            }
        }

        Log.Information(
            "--- Apple bump complete: {Written} file(s) written ({Updated} own <Version> updated, {CrossRefs} cross-framework pin(s) bumped); {Already} own <Version> already at target ({Inspected} apple-framework csproj(s) total) ---",
            written, updated, crossRefsBumped, alreadyPinned, inspected);
        return written;
    }
}
