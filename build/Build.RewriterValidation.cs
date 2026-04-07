using Nuke.Common;
using Nuke.Common.IO;
using Serilog;
using SwiftBindings.Build.Helpers;
using SwiftBindings.Build.Models;

partial class Build
{
    /// <summary>
    /// Test-only target. Reads a fixture csproj + JSON spec from
    /// <c>--rewriter-fixture &lt;dir&gt;</c> and emits the rewritten csproj on
    /// stdout. Used by <c>scripts/_test/run_rewriter_diff.sh</c> to compare
    /// the C# port against the bash Python verbatim copy.
    ///
    /// <para>
    /// Fixture layout:
    /// </para>
    /// <code>
    /// fixture-dir/
    ///   input.csproj          # raw csproj content
    ///   spec.json             # see below
    /// </code>
    /// <para>
    /// spec.json fields:
    /// </para>
    /// <code>
    /// {
    ///   "mode": "sfd" | "pr",
    ///   "siblings": [...],         # for sfd: ["../A/A.xcframework",...]
    ///                              # for pr:  [{"framework", "module", "csproj", "subdirectory"},...]
    ///   "hits": [...]              # same shape as siblings
    /// }
    /// </code>
    /// </summary>
    [Parameter("Fixture directory for ValidateRewriter target")]
    readonly string? RewriterFixture;

    Target ValidateRewriter => _ => _
        .Description("Run CsprojRewriter against a fixture and write rewritten content to stdout")
        .Requires(() => RewriterFixture)
        .Executes(() =>
        {
            var fixtureDir = (AbsolutePath)Path.GetFullPath(RewriterFixture!);
            var specPath = fixtureDir / "spec.json";
            var spec = System.Text.Json.JsonDocument.Parse(File.ReadAllText(specPath)).RootElement;
            var mode = spec.GetProperty("mode").GetString();
            // Optional input_csproj_path lets a fixture place input under a nested
            // logical directory so PR-mode relative path calculations look realistic
            // (e.g. "StripePaymentSheet/SwiftBindings.Stripe.PaymentSheet.csproj").
            var inputRelative = spec.TryGetProperty("input_csproj_path", out var ip)
                ? ip.GetString() ?? "input.csproj"
                : "input.csproj";
            var inputCsproj = fixtureDir / inputRelative;
            var content = File.ReadAllText(inputCsproj);

            string output;
            if (mode == "sfd")
            {
                var siblings = spec.GetProperty("siblings")
                    .EnumerateArray()
                    .Select(e => e.GetString()!)
                    .ToList();
                var hits = spec.GetProperty("hits")
                    .EnumerateArray()
                    .Select(e => e.GetString()!)
                    .ToList();
                output = CsprojRewriter.ApplyFrameworkDeps(content, hits, siblings);
            }
            else if (mode == "pr")
            {
                var siblings = spec.GetProperty("siblings")
                    .EnumerateArray()
                    .Select(e => new DependencyHit(
                        Framework: e.GetProperty("framework").GetString()!,
                        Module: e.GetProperty("module").GetString()!,
                        Csproj: (AbsolutePath)e.GetProperty("csproj").GetString()!,
                        Subdirectory: e.TryGetProperty("subdirectory", out var s) ? (s.GetString() ?? "") : ""))
                    .ToList();
                var hits = spec.GetProperty("hits")
                    .EnumerateArray()
                    .Select(e => new DependencyHit(
                        Framework: e.GetProperty("framework").GetString()!,
                        Module: e.GetProperty("module").GetString()!,
                        Csproj: (AbsolutePath)e.GetProperty("csproj").GetString()!,
                        Subdirectory: e.TryGetProperty("subdirectory", out var s) ? (s.GetString() ?? "") : ""))
                    .ToList();
                output = CsprojRewriter.ApplyProjectRefs(content, inputCsproj, hits, siblings);
            }
            else
            {
                throw new InvalidOperationException($"Unknown mode '{mode}' in spec.json");
            }

            // Write to a sentinel file to avoid Nuke logging interleaving with stdout.
            File.WriteAllText(fixtureDir / "actual.txt", output);
            Log.Information("ValidateRewriter: wrote {Path}", fixtureDir / "actual.txt");
        });
}
