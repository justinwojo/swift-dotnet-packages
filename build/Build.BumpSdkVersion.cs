using System.Text.RegularExpressions;
using Nuke.Common;
using Serilog;

partial class Build
{
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
                        $"BumpAppleVersion: {csproj} has no <Version>NN.N.N</Version> element matching the apple-framework convention. " +
                        "Check the file's <Version> formatting.");
                }
            }
            else
            {
                File.WriteAllText(csproj, rewritten);
                updated++;
                Log.Information("  bumped: {Path}", csproj);
            }
        }

        Log.Information(
            "--- Apple bump complete: {Updated} updated, {Already} already pinned ({Inspected} apple-framework csproj(s) total) ---",
            updated, alreadyPinned, inspected);
        return updated;
    }
}
