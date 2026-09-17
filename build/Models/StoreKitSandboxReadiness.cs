// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System.Text.RegularExpressions;

namespace SwiftBindings.Build.Models;

/// <summary>
/// Pure fail-closed policy for command-line StoreKit Sandbox lanes. Declaring a product id is not
/// readiness proof; only a fresh same-device native-control receipt permits the managed launch.
/// </summary>
internal static class StoreKitSandboxReadiness
{
    internal const string ProductIdEnvironmentVariable = "STOREKIT_SANDBOX_PRODUCT_ID";
    internal const string ProductIdArgument = "--storekit-sandbox-product-id";
    internal const string RunTokenArgument = "--storekit-sandbox-run-token";
    internal const string ReceiptPrefix = "[SK-CONTROL] READY ";
    internal const string SuccessMarker = "[SK-CONTROL] TEST SUCCESS";

    private static readonly Regex ProductIdPattern =
        new("^[A-Za-z0-9][A-Za-z0-9._-]{2,254}$", RegexOptions.CultureInvariant);
    private static readonly Regex RunTokenPattern =
        new("^[a-f0-9]{32}$", RegexOptions.CultureInvariant);

    internal sealed record Configuration(string Platform, string BundleId, string ProductId);

    internal sealed record Receipt(
        string Platform,
        string BundleId,
        string ProductId,
        string RunToken,
        int ProductCount,
        int CurrentEntitlementsCount,
        int AllTransactionsCount);

    internal static Configuration RequireConfiguration(string? productId, string platform, string bundleId)
    {
        if (platform is not ("ios" or "tvos"))
            throw new InvalidOperationException(
                $"StoreKit Sandbox readiness is supported only for command-line iOS/tvOS runners; got '{platform}'.");
        if (string.IsNullOrWhiteSpace(bundleId))
            throw new InvalidOperationException("StoreKit Sandbox readiness requires the managed app bundle id.");
        if (string.IsNullOrWhiteSpace(productId))
            throw new InvalidOperationException(
                $"StoreKit Sandbox readiness preflight failed before app launch: {ProductIdEnvironmentVariable} " +
                "(or --storekit-sandbox-product-id) is missing. Configure an App Store Connect product for " +
                $"bundle '{bundleId}' and sign the target {platform} simulator into a Sandbox tester account. " +
                "A .storekit path cannot be activated through simctl, mlaunch, dotnet, or this command-line lane.");

        productId = productId.Trim();
        if (!ProductIdPattern.IsMatch(productId)
            || productId.Contains("nonexistent", StringComparison.OrdinalIgnoreCase)
            || productId.Contains("placeholder", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"StoreKit Sandbox readiness preflight rejected product id '{productId}'. Supply the exact " +
                "App Store Connect Sandbox product id; placeholder/nonexistent probes cannot establish backend readiness.");
        }

        return new Configuration(platform, bundleId, productId);
    }

    /// <summary>What a StoreKit2 runtime lane may assert on this host.</summary>
    internal enum LaneMode
    {
        /// <summary>
        /// A same-device native control proves Sandbox readiness before the managed launch, so the
        /// managed app receives the product id and runs its live Sandbox section.
        /// </summary>
        Live,

        /// <summary>
        /// Readiness cannot be attested on this lane. The managed app runs its offline suite only
        /// and must end with <c>TEST UNQUALIFIED</c>, which is never a pass.
        /// </summary>
        OfflineOnly,
    }

    internal sealed record Lane(LaneMode Mode, Configuration? Configuration, string? NotQualifiedReason);

    /// <summary>
    /// Resolve the StoreKit2 lane for <paramref name="platform"/> (<c>ios</c>, <c>tvos</c>,
    /// <c>macos</c>, <c>maccatalyst</c>).
    ///
    /// <para>A missing product id is a fact about the host, not a product result: the lane runs
    /// offline and records that live qualification did not happen. A malformed product id is an
    /// operator error and still throws, on every platform, before anything is built.</para>
    ///
    /// <para>Mac lanes are offline-only even when a product id is configured. The command-line
    /// macOS test app is an unbundled, unprovisioned executable and the Catalyst app is ad-hoc
    /// signed; neither can hold a Sandbox App Store receipt, and no Mac native readiness control
    /// exists. Handing them the product id would turn a missing capability back into a product
    /// FAIL, which is the ambiguity the native control exists to remove.</para>
    /// </summary>
    internal static Lane ResolveLane(string? productId, string platform, string bundleId)
    {
        switch (platform)
        {
            case "ios" or "tvos":
                if (string.IsNullOrWhiteSpace(productId))
                    return new Lane(LaneMode.OfflineOnly, null,
                        $"{ProductIdEnvironmentVariable} (or {ProductIdArgument}) is not configured, so the " +
                        $"{platform} same-device Sandbox readiness control cannot run");
                return new Lane(LaneMode.Live, RequireConfiguration(productId, platform, bundleId), null);

            case "macos" or "maccatalyst":
                if (string.IsNullOrWhiteSpace(productId))
                    return new Lane(LaneMode.OfflineOnly, null,
                        $"{ProductIdEnvironmentVariable} (or {ProductIdArgument}) is not configured, and no " +
                        $"command-line Sandbox readiness control exists for {platform}");
                // Validate exactly as the iOS lane would so a malformed id is rejected everywhere.
                _ = RequireConfiguration(productId, "ios", bundleId);
                return new Lane(LaneMode.OfflineOnly, null,
                    $"no command-line Sandbox readiness control exists for {platform} (the {platform} test app " +
                    "is not provisioned to hold a Sandbox App Store receipt), so the configured product id " +
                    "cannot be attested on this lane");

            default:
                throw new InvalidOperationException(
                    $"StoreKit Sandbox lane resolution does not know platform '{platform}'.");
        }
    }

    /// <summary>
    /// Reconcile the managed app's terminal marker with what the lane allowed it to assert.
    /// A pass is only possible on a live lane; an offline lane's best outcome is
    /// <see cref="TestRunStatus.Unqualified"/>. Offline product failures, crashes and timeouts are
    /// reported as themselves.
    /// </summary>
    internal static TestRunResult ApplyLaneVerdict(TestRunResult result, Lane lane) =>
        (lane.Mode, result.Status) switch
        {
            (LaneMode.OfflineOnly, TestRunStatus.Success) => result with
            {
                Status = TestRunStatus.Failed,
                Reason = "StoreKit2 app reported TEST SUCCESS although live Sandbox qualification was not " +
                    $"handed to it on this lane ({lane.NotQualifiedReason})",
            },
            (LaneMode.OfflineOnly, TestRunStatus.Unqualified) => result with
            {
                Reason = $"{result.Reason}; lane: {lane.NotQualifiedReason}",
            },
            (LaneMode.Live, TestRunStatus.Unqualified) => result with
            {
                Status = TestRunStatus.Failed,
                Reason = "StoreKit2 app skipped live Sandbox qualification although the harness proved " +
                    $"readiness and passed the product id: {result.Reason}",
            },
            _ => result,
        };

    internal static Receipt RequireFreshNativeReceipt(string output, Configuration expected, string runToken)
    {
        if (!RunTokenPattern.IsMatch(runToken))
            throw new InvalidOperationException("StoreKit Sandbox native-control run token is malformed.");

        var receiptLines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => line.StartsWith(ReceiptPrefix, StringComparison.Ordinal))
            .ToArray();
        if (receiptLines.Length != 1)
            throw new InvalidOperationException(
                $"StoreKit Sandbox native control emitted {receiptLines.Length} READY receipts; exactly one fresh receipt is required.");
        if (!output.Split('\n').Any(line => string.Equals(line.Trim(), SuccessMarker, StringComparison.Ordinal)))
            throw new InvalidOperationException(
                "StoreKit Sandbox native control did not emit TEST SUCCESS; backend readiness is not established.");

        var fields = ParseFields(receiptLines[0][ReceiptPrefix.Length..]);
        RequireField(fields, "schema", "1");
        RequireField(fields, "platform", expected.Platform);
        RequireField(fields, "bundle", expected.BundleId);
        RequireField(fields, "product", expected.ProductId);
        RequireField(fields, "token", runToken);
        RequireField(fields, "appTransaction", "sandbox");

        var products = RequireNonNegativeInt(fields, "products");
        if (products != 1)
            throw new InvalidOperationException(
                $"StoreKit Sandbox native receipt reported {products} products; exactly one configured product is required.");
        var current = RequireNonNegativeInt(fields, "currentEntitlements");
        var all = RequireNonNegativeInt(fields, "all");
        return new Receipt(expected.Platform, expected.BundleId, expected.ProductId, runToken,
            products, current, all);
    }

    private static Dictionary<string, string> ParseFields(string body)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var token in body.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = token.IndexOf('=');
            if (separator <= 0 || separator == token.Length - 1)
                throw new InvalidOperationException($"StoreKit Sandbox native receipt contains malformed token '{token}'.");
            if (!fields.TryAdd(token[..separator], token[(separator + 1)..]))
                throw new InvalidOperationException(
                    $"StoreKit Sandbox native receipt repeats field '{token[..separator]}'.");
        }
        return fields;
    }

    private static void RequireField(IReadOnlyDictionary<string, string> fields, string name, string expected)
    {
        if (!fields.TryGetValue(name, out var actual) || !string.Equals(actual, expected, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"StoreKit Sandbox native receipt field '{name}' was '{actual ?? "<missing>"}', expected '{expected}'.");
    }

    private static int RequireNonNegativeInt(IReadOnlyDictionary<string, string> fields, string name)
    {
        if (!fields.TryGetValue(name, out var text) || !int.TryParse(text, out var value) || value < 0)
            throw new InvalidOperationException(
                $"StoreKit Sandbox native receipt field '{name}' is missing or not a non-negative integer.");
        return value;
    }
}
