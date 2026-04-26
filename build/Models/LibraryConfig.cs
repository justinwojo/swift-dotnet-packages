using System.Text.Json.Serialization;

namespace SwiftBindings.Build.Models;

/// <summary>
/// Build mode for a library — selects how the xcframeworks are produced.
/// Mirrors the <c>mode</c> field in <c>library.json</c>.
/// </summary>
public enum BuildMode
{
    /// <summary>Clone the SPM repo and build from source via spm-to-xcframework.</summary>
    Source,

    /// <summary>Resolve the SPM tag and copy prebuilt xcframeworks via spm-to-xcframework --binary.</summary>
    Binary,

    /// <summary>
    /// Download a single release zip from <c>zipUrl</c> (with <c>{version}</c> substitution),
    /// extract it, and pick out each product's xcframework by name. Used for vendors who
    /// publish a bundle of xcframeworks on their GitHub release page (e.g. Stripe).
    /// </summary>
    Zip,

    /// <summary>Verify-only path. Xcframeworks must be provisioned out-of-band.</summary>
    Manual,
}

/// <summary>
/// Strongly-typed view of a <c>library.json</c> file. Field names match the JSON
/// keys produced by <c>scripts/lib.sh</c> and parsed by <c>scripts/build-xcframework.sh</c>.
/// </summary>
public sealed class LibraryConfig
{
    /// <summary>SPM repository URL. Required for source/binary modes; unused for manual.</summary>
    [JsonPropertyName("repository")]
    public string? Repository { get; init; }

    /// <summary>SPM tag (e.g. <c>12.8.0</c>). Required for source/binary modes.</summary>
    [JsonPropertyName("version")]
    public string? Version { get; init; }

    /// <summary>Optional explicit revision SHA for tag verification (binary mode pre-flight).</summary>
    [JsonPropertyName("revision")]
    public string? Revision { get; init; }

    /// <summary>
    /// How to produce the xcframeworks for this library. Nullable so we can
    /// distinguish "missing field" (bash dies with "mode is required in
    /// library.json", lib.sh:81) from "explicit Source" — without this the
    /// enum's default value would silently treat omission as Source.
    /// </summary>
    [JsonPropertyName("mode")]
    public BuildMode? Mode { get; init; }

    /// <summary>Minimum iOS deployment target. Defaults to 15.0 to match the bash wrapper.</summary>
    [JsonPropertyName("minIOS")]
    public string MinIOS { get; init; } = "15.0";

    /// <summary>
    /// Release-zip URL for <see cref="BuildMode.Zip"/>. Supports <c>{version}</c> substitution
    /// (e.g. <c>https://github.com/stripe/stripe-ios/releases/download/{version}/Stripe.xcframework.zip</c>).
    /// Required for Zip mode; ignored otherwise.
    /// </summary>
    [JsonPropertyName("zipUrl")]
    public string? ZipUrl { get; init; }

    /// <summary>Products produced by this library. Order is significant — used to resolve indices for selection flags.</summary>
    [JsonPropertyName("products")]
    public List<Product> Products { get; init; } = new();
}

/// <summary>
/// A single product (= one xcframework) emitted by a library. Mirrors an entry
/// in the <c>products</c> array of <c>library.json</c>.
/// </summary>
public sealed class Product
{
    /// <summary>
    /// Required. SPM product/target name and the resulting xcframework filename
    /// stem (e.g. <c>Nuke</c> → <c>Nuke.xcframework</c>).
    /// </summary>
    [JsonPropertyName("framework")]
    public string Framework { get; init; } = "";

    /// <summary>Optional Swift module name when it differs from <see cref="Framework"/>.</summary>
    [JsonPropertyName("module")]
    public string? Module { get; init; }

    /// <summary>
    /// Optional layout subdirectory under the library root. Multi-product vendors
    /// (e.g. Stripe) place each product in <c>libraries/Stripe/&lt;subdirectory&gt;/</c>.
    /// </summary>
    [JsonPropertyName("subdirectory")]
    public string? Subdirectory { get; init; }

    /// <summary>
    /// Opt into spm-to-xcframework's <c>--target</c> escape hatch for SPM
    /// <c>.target(...)</c> entries that are not exposed as <c>.library(...)</c>.
    /// Source-mode only — binary-mode artifacts are always products.
    /// </summary>
    [JsonPropertyName("useTarget")]
    public bool UseTarget { get; init; }

    /// <summary>
    /// Internal product. Built as an xcframework for runtime linking, but no
    /// C# bindings or csproj are generated. Stripe has 2 of these
    /// (Stripe3DS2, StripeCameraCore).
    /// </summary>
    [JsonPropertyName("internal")]
    public bool Internal { get; init; }

    /// <summary>
    /// Legacy disambiguation field. The new spm-to-xcframework binary planner
    /// dedupes by product name and exposes no per-product path override, so
    /// the wrapper rejects this field loudly to prevent shipping a wrong
    /// artifact. See <c>scripts/build-xcframework.sh:294</c>.
    /// </summary>
    [JsonPropertyName("artifactPath")]
    public string? ArtifactPath { get; init; }
}
