using System.Text.Json;
using System.Text.Json.Serialization;
using Nuke.Common.IO;
using SwiftBindings.Build.Models;

namespace SwiftBindings.Build.Helpers;

/// <summary>
/// Loader and product-resolution helpers for <c>library.json</c>. Replaces the
/// JSON helpers in <c>scripts/lib.sh</c> and the product-selection logic at
/// <c>scripts/build-xcframework.sh:101–142</c>.
/// </summary>
public static class LibraryConfigLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>Load and validate a <c>library.json</c> file.</summary>
    public static LibraryConfig Load(AbsolutePath configPath)
    {
        if (!File.Exists(configPath))
            throw new FileNotFoundException($"library.json not found in {configPath.Parent}", configPath);

        LibraryConfig config;
        try
        {
            using var stream = File.OpenRead(configPath);
            config = JsonSerializer.Deserialize<LibraryConfig>(stream, JsonOptions)
                ?? throw new InvalidOperationException($"library.json deserialized to null: {configPath}");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Failed to parse {configPath}: {ex.Message}", ex);
        }

        if (config.Products.Count == 0)
            throw new InvalidOperationException($"products array is empty in {configPath}");

        // Mirrors lib.sh:81 — bash dies on missing mode rather than defaulting.
        // Mode is nullable on the model precisely so we can distinguish
        // "field omitted" from "field present and equal to source".
        if (config.Mode is null)
            throw new InvalidOperationException($"mode is required in {configPath}");

        switch (config.Mode.Value)
        {
            case BuildMode.Source:
            case BuildMode.Binary:
                if (string.IsNullOrEmpty(config.Repository))
                    throw new InvalidOperationException($"repository is required in {configPath} for {config.Mode} mode");
                if (string.IsNullOrEmpty(config.Version))
                    throw new InvalidOperationException($"version is required in {configPath} for {config.Mode} mode");
                break;
            case BuildMode.Zip:
                if (string.IsNullOrEmpty(config.ZipUrl))
                    throw new InvalidOperationException($"zipUrl is required in {configPath} for zip mode");
                if (string.IsNullOrEmpty(config.Version))
                    throw new InvalidOperationException($"version is required in {configPath} for zip mode");
                break;
            case BuildMode.Manual:
                break;
            default:
                throw new InvalidOperationException($"Unknown mode in {configPath}. Must be 'source', 'binary', 'zip', or 'manual'.");
        }

        return config;
    }

    /// <summary>
    /// Resolve which products to build given the CLI flags. Port of
    /// <c>scripts/build-xcframework.sh:101–142</c>:
    /// <list type="bullet">
    ///   <item>Specific list (<c>--products</c>) — order matches CLI input; unknown names error.</item>
    ///   <item>All products (<c>--all-products</c>).</item>
    ///   <item>No flags + single product — auto-select.</item>
    ///   <item>No flags + multiple products — error with helpful message.</item>
    /// </list>
    /// </summary>
    public static List<Product> ResolveProducts(this LibraryConfig config, string[] requested, bool allProducts)
    {
        if (requested.Length > 0)
        {
            // Reject duplicates up front. install_products would otherwise
            // try to move the same xcframework twice and fail with a less
            // obvious "expected src after build, not found" on the second pass.
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var resolved = new List<Product>(requested.Length);
            foreach (var name in requested)
            {
                if (!seen.Add(name))
                    throw new InvalidOperationException(
                        $"Duplicate product '{name}' in --products. Each product may only be specified once.");
                var match = config.Products.FirstOrDefault(p => p.Framework == name);
                if (match is null)
                {
                    var available = string.Join(", ", config.Products.Select(p => p.Framework));
                    throw new InvalidOperationException(
                        $"Product '{name}' not found in library.json. Available: {available}");
                }
                resolved.Add(match);
            }
            return resolved;
        }

        if (allProducts)
            return new List<Product>(config.Products);

        if (config.Products.Count == 1)
            return new List<Product>(config.Products);

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Error: library.json has {config.Products.Count} products. Specify which to build:");
        sb.AppendLine();
        sb.AppendLine("Available products:");
        foreach (var p in config.Products)
            sb.AppendLine($"  - {p.Framework}");
        sb.AppendLine();
        sb.Append("Use --products P1,P2 to select specific products, or --all-products for all.");
        throw new InvalidOperationException(sb.ToString());
    }

    /// <summary>
    /// Path to the directory holding the product's xcframework + csproj. Honors
    /// the optional <c>subdirectory</c> field for multi-product vendors.
    /// </summary>
    public static AbsolutePath ProductDir(this Product product, AbsolutePath libraryDir)
        => string.IsNullOrEmpty(product.Subdirectory) ? libraryDir : libraryDir / product.Subdirectory;

    /// <summary>
    /// Path to the product's <c>&lt;framework&gt;.xcframework</c> on disk, honoring
    /// <c>subdirectory</c>. Used for both manual-mode verification and post-build install.
    /// </summary>
    public static AbsolutePath XcframeworkPath(this Product product, AbsolutePath libraryDir)
        => product.ProductDir(libraryDir) / $"{product.Framework}.xcframework";

    /// <summary>
    /// Discover the single <c>SwiftBindings.*.csproj</c> for this product. Strict
    /// port of <c>discover_single_csproj</c> in <c>scripts/lib.sh:82</c>: fails on
    /// 0 or &gt;1 matches. Internal products do not have a csproj — callers should
    /// skip them before invoking this.
    /// </summary>
    public static AbsolutePath CsprojPath(this Product product, AbsolutePath libraryDir)
    {
        var dir = product.ProductDir(libraryDir);
        if (!Directory.Exists(dir))
            throw new InvalidOperationException($"discover_single_csproj: directory not found: {dir}");

        var matches = Directory.GetFiles(dir, "SwiftBindings.*.csproj", SearchOption.TopDirectoryOnly);
        if (matches.Length == 0)
            throw new InvalidOperationException($"No SwiftBindings.*.csproj found in {dir}");
        if (matches.Length > 1)
            throw new InvalidOperationException(
                $"Multiple SwiftBindings.*.csproj found in {dir}: {string.Join(' ', matches)}");

        return (AbsolutePath)matches[0];
    }
}
