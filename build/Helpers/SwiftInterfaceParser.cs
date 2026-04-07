using System.Text.RegularExpressions;
using Nuke.Common.IO;

namespace SwiftBindings.Build.Helpers;

/// <summary>
/// Walks an xcframework directory to find the device-slice <c>.swiftinterface</c>
/// and extracts <c>import</c> statements from it.
/// Direct port of the find + python regex block at
/// <c>scripts/detect-dependencies.sh:196–232</c>.
/// </summary>
public static class SwiftInterfaceParser
{
    private static readonly Regex ImportPattern = new(
        @"^(?:@_exported\s+)?import\s+(\w+)",
        RegexOptions.Compiled);

    /// <summary>
    /// Locate the preferred <c>.swiftinterface</c> in <paramref name="xcframeworkDir"/>.
    /// Mirrors the bash file selection at <c>detect-dependencies.sh:196–211</c>:
    /// prefers <c>ios-arm64*</c> path components, then any non-private fallback.
    /// </summary>
    public static AbsolutePath? FindSwiftInterface(AbsolutePath xcframeworkDir)
    {
        if (!Directory.Exists(xcframeworkDir))
            return null;

        // Prefer the device slice (ios-arm64*) so we don't accidentally pick
        // up a simulator-only set of imports.
        foreach (var path in EnumerateSwiftInterfaces(xcframeworkDir))
        {
            // Bash uses `find -path "*/ios-arm64*"`, which matches any path
            // component containing the substring after the slash. Use the
            // same Contains check.
            if (path.Contains("/ios-arm64", StringComparison.Ordinal))
                return (AbsolutePath)path;
        }

        // Fall back to any non-private slice.
        foreach (var path in EnumerateSwiftInterfaces(xcframeworkDir))
            return (AbsolutePath)path;

        return null;
    }

    private static IEnumerable<string> EnumerateSwiftInterfaces(string xcframeworkDir)
    {
        // Skip *.private.swiftinterface — these expose @_spi entry points and
        // should never feed dependency inference.
        return Directory
            .EnumerateFiles(xcframeworkDir, "*.swiftinterface", SearchOption.AllDirectories)
            .Where(p => !Path.GetFileName(p).EndsWith(".private.swiftinterface", StringComparison.Ordinal));
    }

    /// <summary>
    /// Parse top-of-file <c>import</c> statements from a .swiftinterface file.
    /// Stops at the first non-blank, non-comment, non-import line — preserving
    /// the bash semantics that "imports must be in the leading import block".
    /// </summary>
    public static SortedSet<string> ParseImports(AbsolutePath swiftInterfacePath)
    {
        var imports = new SortedSet<string>(StringComparer.Ordinal);

        var inImports = false;
        foreach (var rawLine in File.ReadLines(swiftInterfacePath))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal))
                continue;

            var m = ImportPattern.Match(line);
            if (m.Success)
            {
                inImports = true;
                imports.Add(m.Groups[1].Value);
            }
            else if (inImports)
            {
                // First non-import line after entering the import block — stop.
                break;
            }
        }

        return imports;
    }

    /// <summary>
    /// Convenience: locate the .swiftinterface and parse its imports in one call.
    /// Returns <c>null</c> if no .swiftinterface is found (typical for ObjC-only
    /// frameworks).
    /// </summary>
    public static SortedSet<string>? ExtractImports(AbsolutePath xcframeworkDir)
    {
        var path = FindSwiftInterface(xcframeworkDir);
        return path is null ? null : ParseImports(path);
    }
}
