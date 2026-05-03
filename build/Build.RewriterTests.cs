using Nuke.Common;
using Serilog;
using SwiftBindings.Build.Helpers;

partial class Build
{
    /// <summary>
    /// In-process unit tests for <see cref="CsprojRewriter.ApplyFrameworkDeps"/>.
    /// Pure-function tests only — no disk I/O, no external test framework.
    /// Each case asserts one observable behavior of the SFD rewrite contract.
    ///
    /// <para>
    /// The fixture-based <see cref="ValidateRewriter"/> target compares the C#
    /// port's output against a recorded baseline; this target asserts the
    /// behavioral contract directly (preservation of out-of-block items,
    /// in-block ordering, PropertyGroup invariance, etc.) so a regression in
    /// any one rule fails fast with a targeted message rather than a
    /// noisy whole-file diff.
    /// </para>
    /// </summary>
    Target TestCsprojRewriter => _ => _
        .Description("Run in-process behavioral tests for CsprojRewriter.ApplyFrameworkDeps")
        .Executes(() =>
        {
            var failures = new List<string>();

            void Assert(string name, bool condition, string detail)
            {
                if (!condition)
                    failures.Add($"  ✗ {name}: {detail}");
                else
                    Log.Information("  ✓ {Name}", name);
            }

            // ── Case 1: PropertyGroup is never edited ──
            {
                const string input = """
                    <Project Sdk="SwiftBindings.Sdk/0.9.0">

                      <PropertyGroup>
                        <TargetFramework>net10.0-ios</TargetFramework>
                        <SwiftWrapperRequired>true</SwiftWrapperRequired>
                      </PropertyGroup>

                    </Project>
                    """;
                var output = CsprojRewriter.ApplyFrameworkDeps(
                    input,
                    new[] { "../A/A.xcframework" },
                    Array.Empty<string>());
                Assert(
                    "PropertyGroup preserved verbatim",
                    output.Contains("<SwiftWrapperRequired>true</SwiftWrapperRequired>"),
                    "<SwiftWrapperRequired> was modified or removed");
                Assert(
                    "PropertyGroup TargetFramework preserved",
                    output.Contains("<TargetFramework>net10.0-ios</TargetFramework>"),
                    "<TargetFramework> was modified or removed");
            }

            // ── Case 2: First-run injection appends auto-block before </Project> ──
            {
                const string input = """
                    <Project Sdk="SwiftBindings.Sdk/0.9.0">
                      <PropertyGroup>
                        <TargetFramework>net10.0-ios</TargetFramework>
                      </PropertyGroup>
                    </Project>
                    """;
                var output = CsprojRewriter.ApplyFrameworkDeps(
                    input,
                    new[] { "../B/B.xcframework", "../A/A.xcframework" },
                    Array.Empty<string>());
                Assert(
                    "First run emits BEGIN marker",
                    output.Contains(CsprojRewriter.SfdBeginMarker),
                    "BEGIN marker missing");
                Assert(
                    "First run emits END marker",
                    output.Contains(CsprojRewriter.SfdEndMarker),
                    "END marker missing");
                Assert(
                    "First run sorts ordinal (A before B)",
                    output.IndexOf("A.xcframework") < output.IndexOf("B.xcframework"),
                    "first-run output not in ordinal order");
            }

            // ── Case 3: Out-of-block SFD items are preserved verbatim on first run ──
            // The Stripe umbrella csproj at HEAD has SFD items in an UNMARKED
            // <ItemGroup>; the rewriter must not migrate or strip them.
            {
                const string input = """
                    <Project Sdk="SwiftBindings.Sdk/0.9.0">
                      <PropertyGroup>
                        <TargetFramework>net10.0-ios</TargetFramework>
                      </PropertyGroup>

                      <ItemGroup>
                        <SwiftFrameworkDependency Include="../StripeCore/StripeCore.xcframework" PackageId="SwiftBindings.Stripe.Core" PackageVersion="1.0.0" />
                      </ItemGroup>

                    </Project>
                    """;
                // Heuristic re-detected the same item — must not duplicate, must not move.
                var output = CsprojRewriter.ApplyFrameworkDeps(
                    input,
                    new[] { "../StripeCore/StripeCore.xcframework" },
                    Array.Empty<string>());
                Assert(
                    "Out-of-block sibling preserved verbatim (no auto-block emitted)",
                    !output.Contains(CsprojRewriter.SfdBeginMarker),
                    "auto-block was emitted even though every hit is already user-authored");
                Assert(
                    "Out-of-block sibling not duplicated",
                    System.Text.RegularExpressions.Regex.Matches(
                        output, @"Include=""\.\./StripeCore/StripeCore\.xcframework""").Count == 1,
                    "user-authored entry was duplicated");
                Assert(
                    "PackageId/PackageVersion attrs preserved",
                    output.Contains("PackageId=\"SwiftBindings.Stripe.Core\"")
                        && output.Contains("PackageVersion=\"1.0.0\""),
                    "preserved attrs lost");
            }

            // ── Case 4: First-run injection adds NEW heuristic hits and keeps user items ──
            {
                const string input = """
                    <Project Sdk="SwiftBindings.Sdk/0.9.0">
                      <PropertyGroup>
                        <TargetFramework>net10.0-ios</TargetFramework>
                      </PropertyGroup>

                      <ItemGroup>
                        <SwiftFrameworkDependency Include="../StripeCore/StripeCore.xcframework" />
                      </ItemGroup>

                    </Project>
                    """;
                var output = CsprojRewriter.ApplyFrameworkDeps(
                    input,
                    new[]
                    {
                        "../StripeCore/StripeCore.xcframework", // already present
                        "../Stripe3DS2/Stripe3DS2.xcframework", // new
                    },
                    Array.Empty<string>());
                Assert(
                    "User-authored ItemGroup retained",
                    System.Text.RegularExpressions.Regex.Matches(
                        output, @"Include=""\.\./StripeCore/StripeCore\.xcframework""").Count == 1,
                    "user-authored StripeCore lost or duplicated");
                Assert(
                    "New heuristic hit added to auto-block",
                    output.Contains(CsprojRewriter.SfdBeginMarker)
                        && output.Contains("Stripe3DS2.xcframework"),
                    "new sibling missing from auto-block");
                Assert(
                    "Auto-block contains only the new entry",
                    System.Text.RegularExpressions.Regex.Matches(
                        output, @"Include=""\.\./Stripe3DS2/Stripe3DS2\.xcframework""").Count == 1,
                    "Stripe3DS2 emitted more than once");
            }

            // ── Case 5: In-block ordering is preserved on rerun ──
            // StripePayments at HEAD ships [StripeCore, Stripe3DS2], not the
            // ordinal-sorted [Stripe3DS2, StripeCore]. A second rewrite with
            // the same heuristic results must not churn the order.
            {
                const string input = $$"""
                    <Project Sdk="SwiftBindings.Sdk/0.9.0">
                      <PropertyGroup>
                        <TargetFramework>net10.0-ios</TargetFramework>
                      </PropertyGroup>

                      {{CsprojRewriter.SfdBeginMarker}}
                      <ItemGroup>
                        <SwiftFrameworkDependency Include="../StripeCore/StripeCore.xcframework" />
                        <SwiftFrameworkDependency Include="../Stripe3DS2/Stripe3DS2.xcframework" />
                      </ItemGroup>
                      {{CsprojRewriter.SfdEndMarker}}

                    </Project>
                    """;
                var siblings = new[]
                {
                    "../StripeCore/StripeCore.xcframework",
                    "../Stripe3DS2/Stripe3DS2.xcframework",
                };
                var output = CsprojRewriter.ApplyFrameworkDeps(input, siblings, siblings);
                Assert(
                    "Rerun is a no-op for unchanged content",
                    output == input,
                    "in-block order churned (or unrelated content changed)");
            }

            // ── Case 6: In-block ObjC-only sibling preserved when heuristic misses it ──
            // StripePayments' .swiftinterface does not import Stripe3DS2 (ObjC-only,
            // no .swiftmodule), but the SDK-generated wrapper does. The rewriter must
            // keep an in-block entry the heuristic doesn't reproduce.
            {
                const string input = $$"""
                    <Project Sdk="SwiftBindings.Sdk/0.9.0">
                      <PropertyGroup>
                        <TargetFramework>net10.0-ios</TargetFramework>
                      </PropertyGroup>

                      {{CsprojRewriter.SfdBeginMarker}}
                      <ItemGroup>
                        <SwiftFrameworkDependency Include="../StripeCore/StripeCore.xcframework" />
                        <SwiftFrameworkDependency Include="../Stripe3DS2/Stripe3DS2.xcframework" />
                      </ItemGroup>
                      {{CsprojRewriter.SfdEndMarker}}

                    </Project>
                    """;
                // Heuristic only emits StripeCore (it walked the .swiftinterface);
                // Stripe3DS2 must survive the union because it's still a
                // canonical sibling listed in library.json.
                var output = CsprojRewriter.ApplyFrameworkDeps(
                    input,
                    new[] { "../StripeCore/StripeCore.xcframework" },
                    new[]
                    {
                        "../StripeCore/StripeCore.xcframework",
                        "../Stripe3DS2/Stripe3DS2.xcframework",
                    });
                Assert(
                    "In-block ObjC-only sibling survives heuristic miss",
                    output.Contains("Stripe3DS2.xcframework"),
                    "Stripe3DS2 was dropped — wrapper compile would break");
                Assert(
                    "In-block order still preserved (StripeCore before Stripe3DS2)",
                    output.IndexOf("StripeCore.xcframework")
                        < output.IndexOf("Stripe3DS2.xcframework"),
                    "in-block order churned");
            }

            // ── Case 7: New heuristic hit appends to existing block in sorted order ──
            {
                const string input = $$"""
                    <Project Sdk="SwiftBindings.Sdk/0.9.0">
                      <PropertyGroup>
                        <TargetFramework>net10.0-ios</TargetFramework>
                      </PropertyGroup>

                      {{CsprojRewriter.SfdBeginMarker}}
                      <ItemGroup>
                        <SwiftFrameworkDependency Include="../StripeCore/StripeCore.xcframework" />
                      </ItemGroup>
                      {{CsprojRewriter.SfdEndMarker}}

                    </Project>
                    """;
                var siblings = new[]
                {
                    "../StripeCore/StripeCore.xcframework",
                    "../StripeApplePay/StripeApplePay.xcframework",
                };
                var output = CsprojRewriter.ApplyFrameworkDeps(input, siblings, siblings);
                Assert(
                    "Existing in-block item kept first",
                    output.IndexOf("StripeCore.xcframework")
                        < output.IndexOf("StripeApplePay.xcframework"),
                    "existing item lost its leading position");
                Assert(
                    "New heuristic hit appended inside auto-block",
                    output.Contains("StripeApplePay.xcframework")
                        && output.IndexOf("StripeApplePay.xcframework")
                            < output.IndexOf(CsprojRewriter.SfdEndMarker),
                    "new sibling not added inside the marked block");
            }

            // ── Case 8: Asymmetric markers throw rather than corrupt the file ──
            {
                const string input = $$"""
                    <Project Sdk="SwiftBindings.Sdk/0.9.0">
                      <PropertyGroup>
                        <TargetFramework>net10.0-ios</TargetFramework>
                      </PropertyGroup>

                      {{CsprojRewriter.SfdBeginMarker}}
                      <!-- missing END marker -->

                    </Project>
                    """;
                var threw = false;
                try
                {
                    CsprojRewriter.ApplyFrameworkDeps(
                        input,
                        new[] { "../A/A.xcframework" },
                        Array.Empty<string>());
                }
                catch (InvalidOperationException)
                {
                    threw = true;
                }
                Assert(
                    "Asymmetric markers throw InvalidOperationException",
                    threw,
                    "rewrite proceeded against an unbalanced marker pair");
            }

            // ── Case 9: Empty depIncludes on a marked block strips the block ──
            {
                const string input = $$"""
                    <Project Sdk="SwiftBindings.Sdk/0.9.0">
                      <PropertyGroup>
                        <TargetFramework>net10.0-ios</TargetFramework>
                      </PropertyGroup>

                      {{CsprojRewriter.SfdBeginMarker}}
                      <ItemGroup>
                        <SwiftFrameworkDependency Include="../A/A.xcframework" />
                      </ItemGroup>
                      {{CsprojRewriter.SfdEndMarker}}

                    </Project>
                    """;
                // Empty depIncludes — but A is in-block, so union semantics keep it.
                // The block-stripping path is reached only when the union is empty.
                var stripInput = $$"""
                    <Project Sdk="SwiftBindings.Sdk/0.9.0">
                      <PropertyGroup>
                        <TargetFramework>net10.0-ios</TargetFramework>
                      </PropertyGroup>

                      {{CsprojRewriter.SfdBeginMarker}}
                      <ItemGroup>
                      </ItemGroup>
                      {{CsprojRewriter.SfdEndMarker}}

                    </Project>
                    """;
                var stripped = CsprojRewriter.ApplyFrameworkDeps(
                    stripInput,
                    Array.Empty<string>(),
                    Array.Empty<string>());
                Assert(
                    "Empty marked block is removed when no deps remain",
                    !stripped.Contains(CsprojRewriter.SfdBeginMarker)
                        && !stripped.Contains(CsprojRewriter.SfdEndMarker),
                    "empty block left behind");

                // And the union case: in-block A is preserved when it's
                // still a canonical sibling.
                var preserved = CsprojRewriter.ApplyFrameworkDeps(
                    input,
                    Array.Empty<string>(),
                    new[] { "../A/A.xcframework" });
                Assert(
                    "In-block entry preserved when heuristic returns empty",
                    preserved.Contains("A.xcframework"),
                    "in-block entry dropped on empty heuristic result");
            }

            // ── Case 10: Stale in-block entry pruned when removed from knownSiblings ──
            // Counterpart to Case 6: if a product is removed from library.json,
            // its in-block entry should be dropped on the next rewrite rather
            // than stay pinned forever.
            {
                const string input = $$"""
                    <Project Sdk="SwiftBindings.Sdk/0.9.0">
                      <PropertyGroup>
                        <TargetFramework>net10.0-ios</TargetFramework>
                      </PropertyGroup>

                      {{CsprojRewriter.SfdBeginMarker}}
                      <ItemGroup>
                        <SwiftFrameworkDependency Include="../StripeCore/StripeCore.xcframework" />
                        <SwiftFrameworkDependency Include="../RemovedSibling/RemovedSibling.xcframework" />
                      </ItemGroup>
                      {{CsprojRewriter.SfdEndMarker}}

                    </Project>
                    """;
                // RemovedSibling is no longer in library.json — knownSiblings
                // contains only StripeCore.
                var output = CsprojRewriter.ApplyFrameworkDeps(
                    input,
                    new[] { "../StripeCore/StripeCore.xcframework" },
                    new[] { "../StripeCore/StripeCore.xcframework" });
                Assert(
                    "Stale in-block entry pruned (no longer in knownSiblings)",
                    !output.Contains("RemovedSibling.xcframework"),
                    "stale entry survived — would pin a removed product forever");
                Assert(
                    "Surviving in-block entry retained",
                    output.Contains("StripeCore.xcframework"),
                    "valid sibling lost during stale-entry prune");
            }

            // ── Case 11: Attribute-order variation — Include not first ──
            // Hand-edited csprojs sometimes write PackageId before Include.
            // The in-block parser must still recognize the entry so a rerun
            // is a true no-op.
            {
                const string input = $$"""
                    <Project Sdk="SwiftBindings.Sdk/0.9.0">
                      <PropertyGroup>
                        <TargetFramework>net10.0-ios</TargetFramework>
                      </PropertyGroup>

                      {{CsprojRewriter.SfdBeginMarker}}
                      <ItemGroup>
                        <SwiftFrameworkDependency PackageId="SwiftBindings.Foo" PackageVersion="1.0.0" Include="../Foo/Foo.xcframework" />
                      </ItemGroup>
                      {{CsprojRewriter.SfdEndMarker}}

                    </Project>
                    """;
                var siblings = new[] { "../Foo/Foo.xcframework" };
                var output = CsprojRewriter.ApplyFrameworkDeps(input, siblings, siblings);
                Assert(
                    "Foo entry retained when Include is not the first attribute",
                    output.Contains("../Foo/Foo.xcframework"),
                    "in-block parser missed entry with non-leading Include attr");
                Assert(
                    "PackageId/PackageVersion preserved across rerun",
                    output.Contains("PackageId=\"SwiftBindings.Foo\"")
                        && output.Contains("PackageVersion=\"1.0.0\""),
                    "preserved attrs lost across rerun");
                Assert(
                    "Foo entry not duplicated",
                    System.Text.RegularExpressions.Regex.Matches(
                        output, @"Include=""\.\./Foo/Foo\.xcframework""").Count == 1,
                    "Foo emitted more than once across rerun");
            }

            // ── Case 12: Heuristic hit duplicating an out-of-block manual SFD is suppressed ──
            // On rerun (marker-present), if the heuristic re-detects an SFD already
            // declared in an unmarked ItemGroup, the in-place replace leaves the
            // manual entry untouched — emitting it inside the auto-block too
            // would publish the same <SwiftFrameworkDependency> (and its
            // PackageId/PackageVersion attrs) twice.
            {
                const string input = $$"""
                    <Project Sdk="SwiftBindings.Sdk/0.9.0">
                      <PropertyGroup>
                        <TargetFramework>net10.0-ios</TargetFramework>
                      </PropertyGroup>

                      <ItemGroup>
                        <SwiftFrameworkDependency Include="../StripeCore/StripeCore.xcframework" PackageId="SwiftBindings.Stripe.Core" PackageVersion="25.6.2" />
                      </ItemGroup>

                      {{CsprojRewriter.SfdBeginMarker}}
                      <ItemGroup>
                        <SwiftFrameworkDependency Include="../Stripe3DS2/Stripe3DS2.xcframework" />
                      </ItemGroup>
                      {{CsprojRewriter.SfdEndMarker}}

                    </Project>
                    """;
                var siblings = new[]
                {
                    "../StripeCore/StripeCore.xcframework",  // also declared out-of-block
                    "../Stripe3DS2/Stripe3DS2.xcframework",  // in-block already
                };
                var output = CsprojRewriter.ApplyFrameworkDeps(input, siblings, siblings);
                Assert(
                    "Out-of-block manual SFD not duplicated into auto-block on rerun",
                    System.Text.RegularExpressions.Regex.Matches(
                        output, @"Include=""\.\./StripeCore/StripeCore\.xcframework""").Count == 1,
                    "manual out-of-block StripeCore was duplicated inside the auto-block");
                Assert(
                    "Manual out-of-block PackageId/PackageVersion attrs preserved",
                    output.Contains("PackageId=\"SwiftBindings.Stripe.Core\"")
                        && output.Contains("PackageVersion=\"25.6.2\""),
                    "manual entry attrs lost");
                Assert(
                    "In-block sibling retained on rerun (not dropped)",
                    System.Text.RegularExpressions.Regex.Matches(
                        output, @"Include=""\.\./Stripe3DS2/Stripe3DS2\.xcframework""").Count == 1,
                    "in-block Stripe3DS2 lost or duplicated");
            }

            if (failures.Count > 0)
            {
                Log.Error("CsprojRewriter test failures:");
                foreach (var f in failures)
                    Log.Error("{Failure}", f);
                throw new Exception($"{failures.Count} CsprojRewriter test case(s) failed");
            }

            Log.Information("All CsprojRewriter test cases passed.");
        });
}
