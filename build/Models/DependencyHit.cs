using Nuke.Common.IO;

namespace SwiftBindings.Build.Models;

/// <summary>
/// One sibling product that has been identified as a dependency of the current
/// product, either via <c>.swiftinterface</c> import (SwiftFrameworkDependency
/// inject) or via grep over freshly-generated C# (ProjectReference inject).
///
/// Mirrors the bash <c>siblings</c> entries in
/// <c>scripts/detect-dependencies.sh</c> but typed.
/// </summary>
/// <param name="Framework">SPM product/target name and the resulting xcframework filename stem.</param>
/// <param name="Module">Swift module name (used to build <c>\bModule\.</c> grep patterns and the dependency block sort key).</param>
/// <param name="Csproj">Absolute path to the sibling's <c>SwiftBindings.*.csproj</c>.</param>
/// <param name="Subdirectory">Optional subdirectory under the library root (multi-product vendor layout). <see cref="string.Empty"/> for top-level.</param>
public sealed record DependencyHit(
    string Framework,
    string Module,
    AbsolutePath Csproj,
    string Subdirectory);
