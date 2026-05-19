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
}
