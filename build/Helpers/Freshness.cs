using Nuke.Common.IO;

namespace SwiftBindings.Build.Helpers;

/// <summary>
/// Freshness check for the SDK's swift-binding output. Mirrors the bash
/// freshness logic at <c>scripts/detect-dependencies.sh:443–483</c>:
///
/// <list type="number">
///   <item>The <c>swift-binding.stamp</c> file must exist.</item>
///   <item>At least one <c>*.cs</c> file must exist alongside the stamp
///         (guards against generator crashes that left a stamp behind).</item>
///   <item><c>mtime(stamp) &gt; mtime(csproj)</c> — the csproj has not been
///         rewritten since the last generation pass.</item>
///   <item><c>mtime(stamp) &gt; mtime(&lt;framework&gt;.xcframework/Info.plist)</c>
///         — the xcframework has not been rebuilt since the last pass.</item>
/// </list>
///
/// <para>
/// Diagnostic strings are intentionally byte-identical to the bash version's
/// templates so developers who grep'd them in shell history still find them.
/// </para>
/// </summary>
public static class Freshness
{
    /// <summary>
    /// Verify the swift-binding output for one product is fresh and return the
    /// <c>*.cs</c> files inside it (sorted by ordinal name to match Python's
    /// <c>sorted(sb_dir.glob("*.cs"))</c>).
    /// </summary>
    /// <param name="framework">Framework name for use in error messages.</param>
    /// <param name="csprojPath">Absolute csproj path. Used both for the mtime check and to derive a "cd into ..." hint.</param>
    /// <param name="xcframeworkPlist">Absolute path to <c>&lt;framework&gt;.xcframework/Info.plist</c>.</param>
    /// <param name="swiftBindingDir">Absolute path to <c>obj/&lt;Configuration&gt;/net10.0-ios/swift-binding/</c>.</param>
    public static IReadOnlyList<AbsolutePath> CheckFresh(
        string framework,
        AbsolutePath csprojPath,
        AbsolutePath xcframeworkPlist,
        AbsolutePath swiftBindingDir)
    {
        var stamp = swiftBindingDir / "swift-binding.stamp";

        if (!File.Exists(stamp))
        {
            throw new InvalidOperationException(
                $"Missing freshness marker for {framework}:\n" +
                $"  expected: {stamp}\n" +
                $"  cause: the first-pass 'dotnet build' hasn't been run (or was cleaned)\n" +
                $"  fix: cd into {Path.GetDirectoryName((string)csprojPath)} && dotnet build, then re-run");
        }

        // Match Python's `sorted(sb_dir.glob("*.cs"))` — ordinal name sort.
        var csFiles = Directory
            .EnumerateFiles(swiftBindingDir, "*.cs", SearchOption.TopDirectoryOnly)
            .OrderBy(p => p, StringComparer.Ordinal)
            .Select(p => (AbsolutePath)p)
            .ToList();

        if (csFiles.Count == 0)
        {
            throw new InvalidOperationException(
                $"swift-binding.stamp present but no .cs files for {framework}:\n" +
                $"  dir: {swiftBindingDir}\n" +
                $"  this usually means the generator failed mid-run. Re-run 'dotnet build' " +
                $"and investigate any SWIFTBIND warnings.");
        }

        var stampMt = GetMtime(stamp);
        var csprojMt = GetMtime(csprojPath);
        if (csprojMt is not null && csprojMt > stampMt)
        {
            throw new InvalidOperationException(
                $"Stale generated C# for {framework}:\n" +
                $"  csproj modified at {FormatMtime(csprojMt.Value)} is newer than stamp at {FormatMtime(stampMt!.Value)}\n" +
                $"  (the csproj was rewritten since the last generation pass)\n" +
                $"  fix: re-run 'dotnet build' to regenerate bindings, then re-run this script.");
        }

        var plistMt = GetMtime(xcframeworkPlist);
        if (plistMt is not null && plistMt > stampMt)
        {
            throw new InvalidOperationException(
                $"Stale generated C# for {framework}:\n" +
                $"  xcframework Info.plist at {FormatMtime(plistMt.Value)} is newer than stamp at {FormatMtime(stampMt!.Value)}\n" +
                $"  (the xcframework was rebuilt since the last generation pass)\n" +
                $"  fix: re-run 'dotnet build' to regenerate bindings, then re-run this script.");
        }

        return csFiles;
    }

    private static double? GetMtime(AbsolutePath path)
    {
        try
        {
            // Match Python's os.path.getmtime — Unix epoch seconds (double).
            return new DateTimeOffset(File.GetLastWriteTimeUtc(path)).ToUnixTimeMilliseconds() / 1000.0;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Render a Unix-epoch mtime in the bash diagnostic the same way Python does.
    /// Python's f-string of a float trims trailing zeros via repr; we use
    /// "G" which approximates that for round-trippable display.
    /// </summary>
    private static string FormatMtime(double mt)
    {
        // The exact precision doesn't matter for grep-ability — only the
        // surrounding template wording does. We use invariant culture so the
        // output doesn't shift on machines with non-en locales.
        return mt.ToString("G", System.Globalization.CultureInfo.InvariantCulture);
    }
}
