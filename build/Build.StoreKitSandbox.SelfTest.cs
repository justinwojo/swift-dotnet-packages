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

            AssertStoreKitLanePolicy(bundle, product);

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

    /// <summary>
    /// Lane policy: which lanes may run the live Sandbox section, and that no lane can turn an
    /// unattested capability into a pass (or into a product FAIL) on its way to the matrix.
    /// </summary>
    void AssertStoreKitLanePolicy(string bundle, string product)
    {
        // Resolution. A missing product id is offline-only on every platform. A configured id is
        // live on "ios" and "tvos": the simulators prove readiness with the same-device native
        // control first, while the physical-device lanes (also "ios") receive the id without a
        // device-side control, so a configured device run asserts live Sandbox unattested. Mac
        // lanes stay offline-only because their test apps cannot hold a Sandbox receipt.
        foreach (var platform in new[] { "ios", "tvos", "macos", "maccatalyst" })
        {
            foreach (var missingId in new[] { null, "", "   " })
            {
                var offline = StoreKitSandboxReadiness.ResolveLane(missingId, platform, bundle);
                Assert.True(offline.Mode == StoreKitSandboxReadiness.LaneMode.OfflineOnly
                    && offline.Configuration is null
                    && offline.NotQualifiedReason!.Contains(StoreKitSandboxReadiness.ProductIdEnvironmentVariable, StringComparison.Ordinal),
                    $"{platform}: a missing product id must resolve offline-only and name the missing variable");
                Assert.True(StoreKitManagedLaunchArguments(offline).Length == 0,
                    $"{platform}: an offline-only lane must not hand the app a product id");
            }

            // A malformed id is an operator error on every platform, including the Mac lanes that
            // never use it.
            ExpectStoreKitInvalidOperation(() =>
                StoreKitSandboxReadiness.ResolveLane("com.example.nonexistent", platform, bundle));
            ExpectStoreKitInvalidOperation(() =>
                StoreKitSandboxReadiness.ResolveLane("placeholder.product", platform, bundle));
        }

        foreach (var platform in new[] { "ios", "tvos" })
        {
            var live = StoreKitSandboxReadiness.ResolveLane(product, platform, bundle);
            Assert.True(live.Mode == StoreKitSandboxReadiness.LaneMode.Live
                && live.Configuration == new StoreKitSandboxReadiness.Configuration(platform, bundle, product)
                && live.NotQualifiedReason is null,
                $"{platform}: a configured product id must resolve to a live lane for that platform");
            Assert.True(StoreKitManagedLaunchArguments(live)
                    .SequenceEqual(new[] { StoreKitSandboxReadiness.ProductIdArgument, product }),
                $"{platform}: a live lane must hand the app exactly the product-id argument");
        }

        foreach (var platform in new[] { "macos", "maccatalyst" })
        {
            var mac = StoreKitSandboxReadiness.ResolveLane(product, platform, bundle);
            Assert.True(mac.Mode == StoreKitSandboxReadiness.LaneMode.OfflineOnly
                && mac.Configuration is null
                && mac.NotQualifiedReason!.Contains("no command-line Sandbox readiness control exists", StringComparison.Ordinal),
                $"{platform}: a configured product id must still be offline-only (no Mac readiness control)");
            Assert.True(StoreKitManagedLaunchArguments(mac).Length == 0,
                $"{platform}: the Mac lane must never receive the product id");
        }

        ExpectStoreKitInvalidOperation(() => StoreKitSandboxReadiness.ResolveLane(product, "watchos", bundle));

        Assert.True(StoreKitLanePlatform(CellPlatform.IosSim) == "ios"
            && StoreKitLanePlatform(CellPlatform.IosDevice) == "ios"
            && StoreKitLanePlatform(CellPlatform.IosDeviceMonoAot) == "ios"
            && StoreKitLanePlatform(CellPlatform.TvOs) == "tvos"
            && StoreKitLanePlatform(CellPlatform.MacOs) == "macos"
            && StoreKitLanePlatform(CellPlatform.MacCatalyst) == "maccatalyst",
            "every matrix platform must map to its StoreKit lane platform");

        // Verdict reconciliation.
        var offlineLane = StoreKitSandboxReadiness.ResolveLane(null, "macos", bundle);
        var liveLane = StoreKitSandboxReadiness.ResolveLane(product, "ios", bundle);
        static TestRunResult Run(TestRunStatus status, string? reason = null) =>
            new(status, reason, "output", Array.Empty<string>());

        var launderedPass = StoreKitSandboxReadiness.ApplyLaneVerdict(Run(TestRunStatus.Success, "TEST SUCCESS"), offlineLane);
        Assert.True(launderedPass.Status == TestRunStatus.Failed,
            "a TEST SUCCESS from an offline-only lane must never survive as a pass");

        var unqualified = StoreKitSandboxReadiness.ApplyLaneVerdict(
            Run(TestRunStatus.Unqualified, "TEST UNQUALIFIED: offline suite passed"), offlineLane);
        Assert.True(unqualified.Status == TestRunStatus.Unqualified
            && unqualified.Reason!.Contains("offline suite passed", StringComparison.Ordinal)
            && unqualified.Reason.Contains(offlineLane.NotQualifiedReason!, StringComparison.Ordinal),
            "an offline-only UNQUALIFIED run must keep its status and carry the lane's reason");

        var skippedLive = StoreKitSandboxReadiness.ApplyLaneVerdict(Run(TestRunStatus.Unqualified, "x"), liveLane);
        Assert.True(skippedLive.Status == TestRunStatus.Failed,
            "an app that skips live qualification on a live lane is a contract failure");

        foreach (var status in new[] { TestRunStatus.Failed, TestRunStatus.Crashed, TestRunStatus.Timeout, TestRunStatus.Exited })
        {
            Assert.True(StoreKitSandboxReadiness.ApplyLaneVerdict(Run(status, "r"), offlineLane).Status == status,
                $"an offline-lane {status} must be reported as itself");
            Assert.True(StoreKitSandboxReadiness.ApplyLaneVerdict(Run(status, "r"), liveLane).Status == status,
                $"a live-lane {status} must be reported as itself");
        }
        Assert.True(StoreKitSandboxReadiness.ApplyLaneVerdict(Run(TestRunStatus.Success), liveLane).Status == TestRunStatus.Success,
            "a live-lane pass stays a pass");
        Assert.True(ApplyStoreKitLaneVerdict(Run(TestRunStatus.Unqualified, "other library"), null).Status == TestRunStatus.Unqualified,
            "a non-StoreKit lane leaves the app's own verdict alone");

        // Matrix and standalone reporting: UNQUALIFIED is ERROR, counts as a failure, never a pass.
        var cell = new Cell(StoreKit2Library, CellPlatform.MacOs);
        var outcome = ToOutcome(cell, unqualified, TimeSpan.FromSeconds(1));
        Assert.True(outcome.Result == CellResult.Error
            && outcome.Reason!.Contains("QUALIFICATION NOT RUN", StringComparison.Ordinal)
            && outcome.Reason.Contains(offlineLane.NotQualifiedReason!, StringComparison.Ordinal),
            $"UNQUALIFIED must map to ERROR with the lane reason; got {outcome.Result}: {outcome.Reason}");
        Assert.True(IsFailure(outcome, allowUnavailable: false) && IsFailure(outcome, allowUnavailable: true),
            "an UNQUALIFIED cell must fail the run with or without --allow-unavailable");
        Assert.True(ToOutcome(cell, launderedPass, TimeSpan.Zero).Result == CellResult.Fail,
            "a laundered offline TEST SUCCESS must reach the matrix as FAIL");

        // Deferring credential qualification turns only UNQUALIFIED into DEFERRED, keeps the lane's
        // reason, and never touches a real failure or a laundered pass.
        var deferred = ToOutcome(cell, unqualified, TimeSpan.Zero, deferCredentialQualification: true);
        Assert.True(deferred.Result == CellResult.Deferred
            && deferred.Reason!.Contains("CREDENTIAL QUALIFICATION DEFERRED", StringComparison.Ordinal)
            && deferred.Reason.Contains(offlineLane.NotQualifiedReason!, StringComparison.Ordinal),
            $"a deferred UNQUALIFIED must map to DEFERRED with the lane reason; got {deferred.Result}: {deferred.Reason}");
        Assert.True(!IsFailure(deferred, allowUnavailable: false),
            "a DEFERRED cell is the operator's explicit opt-out and must not fail the run");
        Assert.True(ToOutcome(cell, launderedPass, TimeSpan.Zero, deferCredentialQualification: true).Result == CellResult.Fail,
            "deferral must not rescue a laundered offline TEST SUCCESS");
        foreach (var status in new[] { TestRunStatus.Failed, TestRunStatus.Crashed, TestRunStatus.Timeout })
            Assert.True(IsFailure(ToOutcome(cell, Run(status, "r"), TimeSpan.Zero, deferCredentialQualification: true),
                    allowUnavailable: false),
                $"deferral must leave a {status} failing the run");
        ExpectStoreKitInvalidOperation(() => ReportTerminalStatus(unqualified, StoreKit2Library));

        // The managed app and the watcher must agree on the marker, and the app must gate the
        // live section on the harness-provided product id rather than on the platform.
        var appSource = File.ReadAllText(RootDirectory / "apple-frameworks" / "StoreKit2" / "tests" / "Tests.cs");
        Assert.True(appSource.Contains($"$\"{StdoutWatcher.UnqualifiedPrefix}: ", StringComparison.Ordinal),
            "the StoreKit2 test app must emit the watcher's TEST UNQUALIFIED marker");
        Assert.True(appSource.Contains("if (sandboxProductId is not null)", StringComparison.Ordinal)
                && !appSource.Contains("#if IOS || TVOS", StringComparison.Ordinal),
            "the StoreKit2 live section must be gated on the product id, identically on every platform");
    }

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
