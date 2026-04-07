using System.Text.RegularExpressions;
using Nuke.Common.IO;

namespace SwiftBindings.Build.Helpers;

/// <summary>
/// Scans freshly-generated C# under <c>obj/.../swift-binding/</c> for sibling
/// module references. Direct port of the comment-stripping + grep logic at
/// <c>scripts/detect-dependencies.sh:485–527</c>.
///
/// <para>
/// We strip C# comments BEFORE the grep so that XML doc comments such as
/// <c>/// communicate with Stripe.</c> don't trigger false positives on
/// <c>\bStripe\.</c>. Conservative implementation: handles <c>// line</c> and
/// <c>/* block */</c> comments. String literals containing <c>"//"</c> or
/// <c>"/*"</c> are rare in generated bindings (which are mostly method
/// signatures + XML doc comments), so we don't bother with a full C# lexer.
/// </para>
/// </summary>
public static class GeneratedCsScanner
{
    private static readonly Regex BlockCommentRegex = new(
        @"/\*.*?\*/",
        RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex LineCommentRegex = new(
        @"//[^\n]*",
        RegexOptions.Compiled);

    /// <summary>Strip <c>// line</c> and <c>/* block */</c> comments from C# source text.</summary>
    public static string StripComments(string text)
    {
        // Order matches detect-dependencies.sh:494–497: block comments first,
        // then line comments.
        text = BlockCommentRegex.Replace(text, "");
        text = LineCommentRegex.Replace(text, "");
        return text;
    }

    /// <summary>
    /// Read every <c>*.cs</c> file in <paramref name="csFiles"/>, strip comments,
    /// and concatenate with newline separators. OS errors on individual files
    /// are silently skipped (matches the bash <c>except OSError: pass</c>).
    /// </summary>
    public static string Collect(IEnumerable<AbsolutePath> csFiles)
    {
        var parts = new List<string>();
        foreach (var p in csFiles)
        {
            try
            {
                parts.Add(StripComments(File.ReadAllText(p)));
            }
            catch (IOException) { /* match Python's OSError pass */ }
            catch (UnauthorizedAccessException) { /* match Python's OSError pass */ }
        }
        return string.Join("\n", parts);
    }

    /// <summary>
    /// True if <paramref name="content"/> mentions <c>\bModule\.</c> anywhere.
    /// The bound C# emits module-level namespaces (e.g.
    /// <c>StripeCore.STPAPIClient</c>), so a bare module-dot match is the
    /// right signal.
    /// </summary>
    public static bool ContainsModuleReference(string content, string module)
    {
        if (string.IsNullOrEmpty(module))
            return false;
        var pattern = new Regex(@"\b" + Regex.Escape(module) + @"\.");
        return pattern.IsMatch(content);
    }
}
