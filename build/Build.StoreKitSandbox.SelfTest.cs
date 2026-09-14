// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Nuke.Common;
using SwiftBindings.Build.Helpers;
using SwiftBindings.Build.Models;

partial class Build
{
    Target TestStoreKitSandbox => _ => _
        .Description("Self-test the fail-closed StoreKit Sandbox readiness contract")
        .Executes(() =>
        {
            const string bundle = "com.swiftbindings.storekit2.tests";
            const string product = "com.swiftbindings.storekit2.tests.nonconsumable";
            const string token = "0123456789abcdef0123456789abcdef";

            var missing = ExpectStoreKitInvalidOperation(() =>
                StoreKitSandboxReadiness.RequireConfiguration(null, "ios", bundle));
            Assert.True(missing.Message.Contains("failed before app launch", StringComparison.Ordinal));
            Assert.True(missing.Message.Contains(".storekit", StringComparison.Ordinal));
            Assert.True(missing.Message.Contains("simctl", StringComparison.Ordinal));

            ExpectStoreKitInvalidOperation(() =>
                StoreKitSandboxReadiness.RequireConfiguration("com.example.nonexistent", "tvos", bundle));
            ExpectStoreKitInvalidOperation(() =>
                StoreKitSandboxReadiness.RequireConfiguration(product, "macos", bundle));

            var config = StoreKitSandboxReadiness.RequireConfiguration(product, "ios", bundle);
            var valid = Receipt(token, product, "ios", bundle, "sandbox", 1, 0, 0)
                + "\n[SK-CONTROL] TEST SUCCESS\n";
            var parsed = StoreKitSandboxReadiness.RequireFreshNativeReceipt(valid, config, token);
            Assert.True(parsed.ProductCount == 1 && parsed.CurrentEntitlementsCount == 0 && parsed.AllTransactionsCount == 0);

            var stale = Receipt("fedcba9876543210fedcba9876543210", product, "ios", bundle,
                "sandbox", 1, 0, 0) + "\n[SK-CONTROL] TEST SUCCESS\n";
            ExpectStoreKitInvalidOperation(() =>
                StoreKitSandboxReadiness.RequireFreshNativeReceipt(stale, config, token));

            var production = Receipt(token, product, "ios", bundle, "production", 1, 0, 0)
                + "\n[SK-CONTROL] TEST SUCCESS\n";
            ExpectStoreKitInvalidOperation(() =>
                StoreKitSandboxReadiness.RequireFreshNativeReceipt(production, config, token));

            var wrongPlatform = Receipt(token, product, "tvos", bundle, "sandbox", 1, 0, 0)
                + "\n[SK-CONTROL] TEST SUCCESS\n";
            ExpectStoreKitInvalidOperation(() =>
                StoreKitSandboxReadiness.RequireFreshNativeReceipt(wrongPlatform, config, token));

            var wrongBundle = Receipt(token, product, "ios", "com.example.other-app", "sandbox", 1, 0, 0)
                + "\n[SK-CONTROL] TEST SUCCESS\n";
            ExpectStoreKitInvalidOperation(() =>
                StoreKitSandboxReadiness.RequireFreshNativeReceipt(wrongBundle, config, token));

            var missingProduct = Receipt(token, product, "ios", bundle, "sandbox", 0, 0, 0)
                + "\n[SK-CONTROL] TEST SUCCESS\n";
            ExpectStoreKitInvalidOperation(() =>
                StoreKitSandboxReadiness.RequireFreshNativeReceipt(missingProduct, config, token));

            var ambiguousProduct = Receipt(token, product, "ios", bundle, "sandbox", 2, 0, 0)
                + "\n[SK-CONTROL] TEST SUCCESS\n";
            ExpectStoreKitInvalidOperation(() =>
                StoreKitSandboxReadiness.RequireFreshNativeReceipt(ambiguousProduct, config, token));

            var nonNumericCount = Receipt(token, product, "ios", bundle, "sandbox", 1, 0, 0)
                .Replace("all=0", "all=Infinity", StringComparison.Ordinal)
                + "\n[SK-CONTROL] TEST SUCCESS\n";
            ExpectStoreKitInvalidOperation(() =>
                StoreKitSandboxReadiness.RequireFreshNativeReceipt(nonNumericCount, config, token));

            var readyLine = Receipt(token, product, "ios", bundle, "sandbox", 1, 0, 0);
            var duplicateReady = readyLine + "\n" + readyLine + "\n[SK-CONTROL] TEST SUCCESS\n";
            ExpectStoreKitInvalidOperation(() =>
                StoreKitSandboxReadiness.RequireFreshNativeReceipt(duplicateReady, config, token));

            const string malformedToken = "zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz";
            var malformed = Receipt(malformedToken, product, "ios", bundle, "sandbox", 1, 0, 0)
                + "\n[SK-CONTROL] TEST SUCCESS\n";
            ExpectStoreKitInvalidOperation(() =>
                StoreKitSandboxReadiness.RequireFreshNativeReceipt(malformed, config, malformedToken));

            ExpectStoreKitInvalidOperation(() =>
                StoreKitSandboxReadiness.RequireFreshNativeReceipt(
                    Receipt(token, product, "ios", bundle, "sandbox", 1, 0, 0), config, token));

            var deviceLaunch = new DevicectlClient("00000000-0000-0000-0000-000000000000")
                .BuildLaunchPsi(bundle,
                    [StoreKitSandboxReadiness.ProductIdArgument, product]);
            var deviceArguments = deviceLaunch.ArgumentList.ToArray();
            var bundleIndex = Array.IndexOf(deviceArguments, bundle);
            Assert.True(bundleIndex >= 0
                && deviceArguments.Skip(bundleIndex + 1).SequenceEqual(
                    new[] { StoreKitSandboxReadiness.ProductIdArgument, product }),
                "devicectl must forward StoreKit product arguments positionally after the bundle id");
        });

    private static InvalidOperationException ExpectStoreKitInvalidOperation(Action action)
    {
        try
        {
            action();
        }
        catch (InvalidOperationException ex)
        {
            return ex;
        }

        throw new InvalidOperationException("Expected StoreKit Sandbox readiness validation to fail closed.");
    }

    private static string Receipt(string token, string product, string platform, string bundle,
        string environment, int products, int current, int all) =>
        $"{StoreKitSandboxReadiness.ReceiptPrefix}schema=1 platform={platform} bundle={bundle} " +
        $"product={product} token={token} appTransaction={environment} products={products} " +
        $"currentEntitlements={current} all={all}";
}
