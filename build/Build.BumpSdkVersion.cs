using System.Text.RegularExpressions;
using Nuke.Common;
using Serilog;

partial class Build
{
    // --- SwiftBindings.Apple[.*] dependency-pin matching ---
    //
    // These three patterns are the SINGLE source of truth for finding and
    // rewriting Apple package pins, shared by BumpAppleVersionInternal
    // (rewrite) and RegressionValidate's DiscoverCrossFrameworkRefs (read). Using
    // one definition for both is deliberate: if the bump and the coherence guard
    // matched pins differently, the guard could report "clean" on exactly the pin
    // the bump missed — a false-negative in the safety net.
    //
    // They match BOTH Apple package shapes, because two independent version
    // trains run through the same tag syntax and each pin belongs to exactly one
    // of them:
    //
    //   * the CORE supplement, Include="SwiftBindings.Apple" — a package this
    //     repo only CONSUMES (it is produced by the swift-bindings SDK repo's
    //     apple-v* lane). Its pin rides --apple-version.
    //   * a SIBLING per-framework binding, Include="SwiftBindings.Apple.X" — a
    //     package this repo PRODUCES, so its pin must match the identity the
    //     same run stamps into the sibling's own <Version> element. Those ride
    //     --apple-bindings-version.
    //
    // The two trains de-sync routinely (a wave can justify new per-framework
    // bindings without a new core supplement), so classification is not cosmetic:
    // stamping a sibling pin with the core version silently downgrades it to an
    // already-published package with different content. AppleRefFrameworkName is
    // the one place that classification happens, and every consumer — bump,
    // coherence guard, packing discovery — routes through it so they cannot
    // disagree about which train a pin belongs to.
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
    /// <c>Include="SwiftBindings.Apple"</c> or <c>Include="SwiftBindings.Apple.X"</c>
    /// attribute, any attribute order.
    /// Either quote style (<c>"</c> or <c>'</c>) — XML/MSBuild allows both, and a
    /// single-quoted pin the tag pattern missed would be invisible to the bump,
    /// the coherence guard, AND packing discovery, reopening the silent-skew hole
    /// the single source of truth exists to close. The Include quote is captured
    /// (<c>q</c>) and backreferenced as its closer (<c>\k&lt;q&gt;</c>), matching
    /// the Include/Version sub-patterns exactly — so a malformed mixed-quote tag
    /// (<c>Include="…'</c>) is rejected here too rather than matching the tag but
    /// parsing an empty name downstream in the coherence guard.
    ///
    /// <para>The framework suffix is optional so the CORE supplement ref is
    /// matched too. It rides a different train (see the block comment above), and
    /// a pattern that could not see it would leave the core pin invisible to both
    /// the bump and the guard — the same silent-skew hole, just on the other
    /// train.</para></summary>
    static readonly Regex AppleRefTagPattern = new(
        @"<PackageReference\b[^>]*?\bInclude\s*=\s*(?<q>[""'])SwiftBindings\.Apple(?:\.[^""']+)?\k<q>[^>]*?>",
        RegexOptions.Compiled);

    /// <summary>The <c>Include="SwiftBindings.Apple[.X]"</c> attribute, capturing
    /// X when present (absent ⇒ the core supplement ref).
    /// <c>q</c> backreferences the opening quote so the close must match (no
    /// <c>Include="…'</c> mixed-quote false match); either style accepted.</summary>
    static readonly Regex AppleRefIncludePattern = new(
        @"\bInclude\s*=\s*(?<q>[""'])SwiftBindings\.Apple(?:\.(?<name>[^""']+))?\k<q>",
        RegexOptions.Compiled);

    /// <summary>
    /// Which train an Apple <c>PackageReference</c> tag belongs to: the sibling
    /// per-framework name (the part after <c>SwiftBindings.Apple.</c>) for a pin
    /// this repo produces, or <c>null</c> for the core supplement ref this repo
    /// only consumes.
    ///
    /// <para>Single classification point for the bump, the coherence guard and
    /// packing discovery, for the same reason the patterns above are shared: a
    /// consumer that classified differently could stamp one train's version onto
    /// the other train's pin, or report "clean" on a pin the bump moved to the
    /// wrong value.</para>
    /// </summary>
    internal static string? AppleRefFrameworkName(string tag)
    {
        var m = AppleRefIncludePattern.Match(tag);
        return m.Success && m.Groups["name"].Success ? m.Groups["name"].Value : null;
    }

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
    static readonly Regex AppleRefVersionPattern = new(
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
    /// <c>&lt;Version&gt;NN.N.N&lt;/Version&gt;</c> element — the identity the
    /// per-framework package is PRODUCED at — to
    /// <c>--apple-bindings-version</c>, defaulting to <c>--apple-version</c>
    /// when that is omitted. Sibling <c>SwiftBindings.Apple.X</c> pins move with
    /// it; a core <c>SwiftBindings.Apple</c> supplement pin (a reference, not an
    /// identity) moves with <c>--apple-version</c> instead. Neither train is
    /// covered by <see cref="BumpSdkVersion"/>, which only rewrites the
    /// <c>Sdk="SwiftBindings.Sdk/X.Y.Z"</c> attribute.
    ///
    /// Idempotent: files already at the target version are left untouched.
    /// Only csprojs that import <c>SwiftBindings.Sdk</c> are touched — test
    /// csprojs use <c>Microsoft.NET.Sdk</c> and don't carry the train
    /// <c>&lt;Version&gt;</c> element.
    /// </summary>
    Target BumpAppleVersion => _ => _
        .Description("Pin every apple-framework csproj's <Version> + sibling pins to <--apple-bindings-version> (default: <--apple-version>), and any core supplement pin to <--apple-version>")
        .Executes(() =>
        {
            // Deliberately not `.Requires(() => AppleVersion)`: the produced train
            // can now move on its own, so `--apple-bindings-version` alone is a
            // legitimate invocation. Requiring a core version there would force
            // the caller to name a train the bump isn't touching.
            var bindingsVersion = ResolveAppleBindingsVersion(AppleBindingsVersion, AppleVersion)
                ?? throw new InvalidOperationException(
                    "BumpAppleVersion: pass --apple-bindings-version (the per-framework train) " +
                    "and/or --apple-version (the core supplement train).");
            BumpAppleVersionInternal(bindingsVersion, AppleVersion);
        });

    /// <summary>
    /// Walk every <c>*.csproj</c> under <paramref name="root"/> (default
    /// <c>apple-frameworks/</c>) and replace the per-framework package's own
    /// version (digit-and-dot content only — e.g. <c>26.2.1</c> →
    /// <c>26.2.3</c>) with <paramref name="appleBindingsVersion"/>.
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
    /// Also rewrites Apple <c>PackageReference</c> pins, each on ITS OWN train:
    /// <list type="bullet">
    /// <item><description>a sibling pin
    /// <c>&lt;PackageReference Include="SwiftBindings.Apple.X" Version="Y" /&gt;</c>
    /// (e.g. RealityKit→RealityFoundation, MatterSupport→Matter) →
    /// <paramref name="appleBindingsVersion"/>. These edges MUST move in lockstep
    /// with the produced train: a consumer regenerated by the current generator
    /// can emit calls against a sibling proxy's newer ABI (e.g. the
    /// <c>ownsContainer</c> ctor argument on owned-existential returns), and a
    /// stale pin pulls an older published sibling whose API lacks it — a hard
    /// compile break in generated code. Pinning it to the CORE version instead
    /// would be the same break with a friendlier-looking number.</description></item>
    /// <item><description>a core supplement pin
    /// <c>&lt;PackageReference Include="SwiftBindings.Apple" Version="Y" /&gt;</c>
    /// → <paramref name="appleCoreVersion"/>, because that package is consumed,
    /// not produced here. <c>null</c> leaves core pins untouched — the caller
    /// named no core train, so there is nothing to stamp and inventing one would
    /// silently repoint a reference.</description></item>
    /// </list>
    /// The standalone <c>&lt;Version&gt;</c> element is XML element content; the
    /// dependency pin is an attribute, so it needs its own pattern.
    ///
    /// Unlike <see cref="BumpSdkVersionInternal"/>, this does NOT wipe
    /// sibling obj/ directories: the package version doesn't feed the
    /// generator's input fingerprint, so stale generated source is fine.
    /// When called from RegressionValidate, BumpSdkVersionInternal has
    /// already wiped obj/ in the same pre-flight, so doing it again here
    /// would be redundant.
    ///
    /// <paramref name="root"/> exists so the self-tests can drive this over a
    /// scratch tree; production callers omit it.
    /// </summary>
    int BumpAppleVersionInternal(string appleBindingsVersion, string? appleCoreVersion, string? root = null)
    {
        var scanRoot = root ?? AppleFrameworksDir;
        Log.Information("--- bump Apple csproj versions → bindings {Bindings}, core supplement {Core} ---",
            appleBindingsVersion, appleCoreVersion ?? "(unchanged)");

        var pattern = new Regex(@"<Version>[\d.]+</Version>", RegexOptions.Compiled);
        var replacement = $"<Version>{appleBindingsVersion}</Version>";

        var updated = 0;
        var alreadyPinned = 0;
        var inspected = 0;
        var siblingPinsBumped = 0;
        var corePinsBumped = 0;
        var written = 0;

        if (!Directory.Exists(scanRoot))
        {
            Log.Information("--- no {Root} directory, skipping ---", scanRoot);
            return 0;
        }

        foreach (var csproj in Directory.EnumerateFiles(scanRoot, "*.csproj", SearchOption.AllDirectories))
        {
            var content = File.ReadAllText(csproj);
            if (!content.Contains("SwiftBindings.Sdk/", StringComparison.Ordinal))
                continue;

            inspected++;

            // 1. Own <Version> element — the package's produced identity, so it rides the
            //    bindings train. This pattern is deliberately broad — it rewrites
            //    EVERY numeric <Version>N.N.N</Version> element in the file, not a property
            //    scoped to one PropertyGroup. In an apple-framework csproj the only such
            //    element is the package's own train version; Apple pins use the Version
            //    attribute (step 2) and a child-element pin would be caught by the coherence
            //    guard, never reaching disk in an unhandled form. That guard matters more
            //    now that two trains exist: a child-element pin would take the BINDINGS
            //    version from this step even when it is a core supplement ref, so letting
            //    one through silently would cross the trains. The match/throw logic gates
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

            // 2. Apple dependency pins → the target version OF THEIR OWN TRAIN.
            //    Rewrite the Version attribute within each Apple PackageReference tag,
            //    leaving the rest of the tag (Include name, Condition, ordering) intact.
            var rewritten = AppleRefTagPattern.Replace(afterVersion, tag =>
            {
                var name = AppleRefFrameworkName(tag.Value);
                var target = name is null ? appleCoreVersion : appleBindingsVersion;
                if (target is null) return tag.Value;   // core pin, no core train named
                var bumped = AppleRefVersionPattern.Replace(tag.Value,
                    v => v.Groups["pre"].Value + target + v.Groups["post"].Value);
                if (bumped != tag.Value)
                {
                    if (name is null) corePinsBumped++;
                    else siblingPinsBumped++;
                }
                return bumped;
            });

            if (rewritten != content)
            {
                File.WriteAllText(csproj, rewritten);
                written++;
                if (afterVersion != content) updated++;
                Log.Information("  bumped: {Path}", csproj);
            }
        }

        Log.Information(
            "--- Apple bump complete: {Written} file(s) written ({Updated} own <Version> updated, {SiblingPins} sibling pin(s), {CorePins} core supplement pin(s) bumped); {Already} own <Version> already at target ({Inspected} apple-framework csproj(s) total) ---",
            written, updated, siblingPinsBumped, corePinsBumped, alreadyPinned, inspected);
        return written;
    }
}
