// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using StoreKit2;
using Swift.Runtime;

namespace SwiftBindings.StoreKit2.Tests;

internal static class Tests
{
    /// <summary>
    /// The bundle identifier this test app is built with (the csproj's <c>ApplicationId</c>). Used
    /// only as the comparison identity when the host bundle reports none; the running app's own
    /// <c>NSBundle.mainBundle</c> is preferred so the check follows the binary rather than a string.
    /// </summary>
    private const string ExpectedApplicationId = "com.swiftbindings.storekit2.tests";
    private const string SandboxProductIdArgument = "--storekit-sandbox-product-id";

    /// <summary>
    /// Awaits <paramref name="task"/> with a hard upper bound; false means the bound elapsed first
    /// and the caller must fail. Real await (never Wait()/GetAwaiter().GetResult()) so a main-thread
    /// affine continuation can run instead of deadlocking against a blocked runner.
    /// </summary>
    internal static async Task<bool> CompletesWithin(Task task, TimeSpan budget)
    {
        var timeout = Task.Delay(budget);
        var winner = await Task.WhenAny(task, timeout).ConfigureAwait(true);
        if (!ReferenceEquals(winner, task))
            return false;
        await task.ConfigureAwait(true);
        return true;
    }

    /// <summary>
    /// Bounded await that yields the result; throws <see cref="TimeoutException"/> on the bound so a
    /// hung async bridge fails the test instead of being reported as a pass.
    /// </summary>
    internal static async Task<T> WithTimeout<T>(Task<T> task, TimeSpan budget, string what)
    {
        var timeout = Task.Delay(budget);
        var winner = await Task.WhenAny(task, timeout).ConfigureAwait(true);
        if (!ReferenceEquals(winner, task))
            throw new TimeoutException($"{what} did not complete within {budget.TotalSeconds:0.#}s");
        return await task.ConfigureAwait(true);
    }

    internal sealed class ManagedUnwindTimeoutException(string message) : TimeoutException(message);

    /// <summary>
    /// Reads the Sandbox product id the harness passes only after it has attested Sandbox readiness
    /// for this run. Absent means the live qualification section does not run; present but
    /// unusable is a harness contract violation and throws.
    /// </summary>
    private static string? ReadSandboxProductId()
    {
        var arguments = Foundation.NSProcessInfo.ProcessInfo.Arguments;
        var index = Array.IndexOf(arguments, SandboxProductIdArgument);
        if (index < 0)
            return null;

        var value = index + 1 < arguments.Length ? arguments[index + 1]?.Trim() : null;
        if (!string.IsNullOrWhiteSpace(value)
            && !value.StartsWith("-", StringComparison.Ordinal)
            && !value.Contains("placeholder", StringComparison.OrdinalIgnoreCase)
            && !value.Contains("nonexistent", StringComparison.OrdinalIgnoreCase))
            return value;

        throw new InvalidOperationException(
            $"StoreKit Sandbox readiness argument {SandboxProductIdArgument} was passed without a usable value. " +
            "The harness passes an exact App Store Connect product only after the same-device pure-Swift " +
            "control has emitted a fresh Sandbox receipt; a .storekit path is not supported here.");
    }

    // A completed managed task is not evidence that StoreKit or the native Swift task exited.
    internal static async Task<T> WithCancellationTimeout<T>(
        Func<CancellationToken, Task<T>> start, TimeSpan budget, TimeSpan unwindBudget, string what)
        where T : IDisposable
    {
        using var cts = new CancellationTokenSource();
        var task = start(cts.Token);
        if (ReferenceEquals(await Task.WhenAny(task, Task.Delay(budget)).ConfigureAwait(true), task))
            return await task.ConfigureAwait(true);

        // Observe faults and dispose a result that races the deadline, even if managed unwind
        // exceeds its second bound. This observer owns any successful result after the timeout.
        async Task<string> ObserveLateResult()
        {
            try
            {
                using var lateResult = await task.ConfigureAwait(false);
                return "completed successfully; late result disposed";
            }
            catch (OperationCanceledException) { return "cancellation observed"; }
            catch (Exception ex) { return $"fault observed: {ex.GetType().Name}: {ex.Message}"; }
        }

        var observation = ObserveLateResult();
        cts.Cancel();
        if (!await CompletesWithin(observation, unwindBudget).ConfigureAwait(true))
            throw new ManagedUnwindTimeoutException(
                $"{what} exceeded {budget.TotalSeconds:0.#}s and its managed task did not unwind " +
                $"within {unwindBudget.TotalSeconds:0.#}s after cancellation; stopping later tests");
        throw new TimeoutException(
            $"{what} exceeded {budget.TotalSeconds:0.#}s; managed task {await observation.ConfigureAwait(true)}. " +
            "Native completion/cleanup is not established by managed unwind.");
    }

    public static async Task<int> RunAsync()
    {
        int passed = 0, failed = 0, skipped = 0;
        string? sandboxProductId = null;
        var notRun = new List<string>();

        // Terminal marker precedence: any failure is TEST FAILED; otherwise a run whose live
        // Sandbox qualification did not happen is TEST UNQUALIFIED, never TEST SUCCESS. A run can
        // only succeed after the live section has actually executed.
        int CompleteRun()
        {
            Log($"Results: {passed} passed, {failed} failed, {skipped} skipped, {notRun.Count} live qualification checks not run");
            if (failed > 0)
                Log($"TEST FAILED: {failed} failures", prefixed: false);
            else if (sandboxProductId is null)
                Log($"TEST UNQUALIFIED: offline suite passed ({passed} passed, {skipped} skipped); " +
                    $"StoreKit Sandbox live qualification not run ({notRun.Count} checks: {string.Join(", ", notRun)}) " +
                    $"because {SandboxProductIdArgument} was not provided", prefixed: false);
            else
                Log("TEST SUCCESS", prefixed: false);
            return failed;
        }

        void NotRun(string name)
        {
            notRun.Add(name);
            Log($"NOT RUN: {name} — live Sandbox qualification was not attested for this run");
        }

        void Pass(string name)
        {
            passed++;
            Log($"PASS: {name}");
        }

        void Fail(string name, string error)
        {
            failed++;
            Log($"FAIL: {name} — {error}");
        }

        void Skip(string name, string reason)
        {
            skipped++;
            Log($"SKIP: {name} — {reason}");
        }

        void MetadataTest<T>(string name) where T : ISwiftObject
        {
            try
            {
                var md = SwiftObjectHelper<T>.GetTypeMetadata();
                if (md.Handle == IntPtr.Zero)
                    throw new InvalidOperationException("null handle");
                Log($"{name} metadata size = {md.Size}");
                Pass($"{name} metadata");
            }
            catch (Exception ex) { Fail($"{name} metadata", ex.Message); }
        }

        // Every platform reads the same argument. The harness passes it only on a lane whose
        // Sandbox readiness it attested (today: iOS/tvOS simulators via the native control); the
        // macOS and Mac Catalyst lanes never receive it.
        try
        {
            sandboxProductId = ReadSandboxProductId();
            if (sandboxProductId is null)
                Log($"{SandboxProductIdArgument} not provided: running the offline suite only; " +
                    "the live Sandbox qualification checks will be reported as not run");
            else
                Pass("StoreKit Sandbox managed readiness argument");
        }
        catch (Exception ex)
        {
            Fail("StoreKit Sandbox managed readiness argument", ex.Message);
            return CompleteRun();
        }

        await TimeoutTests.RunAsync(Pass, Fail);

        // Test 1: AppStore.CanMakePayments
        try
        {
            bool canMake = AppStore.CanMakePayments;
            Log($"AppStore.CanMakePayments = {canMake}");
            Pass("AppStore.CanMakePayments");
        }
        catch (Exception ex)
        {
            Fail("AppStore.CanMakePayments", ex.Message);
        }

        // Test 2: AppStore.DeviceVerificationID
        try
        {
            Guid? id = AppStore.DeviceVerificationID;
            Log($"AppStore.DeviceVerificationID = {id?.ToString() ?? "(nil)"}");
            Pass("AppStore.DeviceVerificationID");
        }
        catch (Exception ex)
        {
            Fail("AppStore.DeviceVerificationID", ex.Message);
        }

        // Test 3: Transaction.All async sequence creation (no enumeration)
        // Note: Transactions is ISwiftObject — do NOT Dispose (crashes on device).
        try
        {
            var allTransactions = Transaction.All;
            Log($"Transaction.All type = {allTransactions.GetType().Name}");
            Pass("Transaction.All");
        }
        catch (Exception ex)
        {
            Fail("Transaction.All", ex.Message);
        }

        // Test 4: Transaction.CurrentEntitlements async sequence
        try
        {
            var entitlements = Transaction.CurrentEntitlements;
            Log($"Transaction.CurrentEntitlements type = {entitlements.GetType().Name}");
            Pass("Transaction.CurrentEntitlements");
        }
        catch (Exception ex)
        {
            Fail("Transaction.CurrentEntitlements", ex.Message);
        }

        // Test 5: VerificationResult enum tag values — assert actual uint values
        try
        {
            if ((uint)VerificationResult<Transaction>.CaseTag.Unverified != 0)
                throw new InvalidOperationException($"Unverified expected 0, got {(uint)VerificationResult<Transaction>.CaseTag.Unverified}");
            if ((uint)VerificationResult<Transaction>.CaseTag.Verified != 1)
                throw new InvalidOperationException($"Verified expected 1, got {(uint)VerificationResult<Transaction>.CaseTag.Verified}");
            Pass("VerificationResult.CaseTag");
        }
        catch (Exception ex)
        {
            Fail("VerificationResult.CaseTag", ex.Message);
        }

        // Test 6: Transaction.Unfinished async sequence creation (no enumeration)
        // Note: Transactions is ISwiftObject — do NOT Dispose.
        try
        {
            var seq = Transaction.Unfinished;
            Log($"Transaction.Unfinished type = {seq.GetType().Name}");
            Pass("Transaction.Unfinished");
        }
        catch (Exception ex)
        {
            Fail("Transaction.Unfinished", ex.Message);
        }

        // Test 7: Transaction.Updates async sequence creation (no enumeration)
        try
        {
            var seq = Transaction.Updates;
            Log($"Transaction.Updates type = {seq.GetType().Name}");
            Pass("Transaction.Updates");
        }
        catch (Exception ex)
        {
            Fail("Transaction.Updates", ex.Message);
        }

#if !TVOS
        // Test 8: PurchaseIntent.Intents async sequence creation (no enumeration)
        // Note: PurchaseIntents is ISwiftObject — do NOT Dispose.
        try
        {
            var seq = PurchaseIntent.Intents;
            Log($"PurchaseIntent.Intents type = {seq.GetType().Name}");
            Pass("PurchaseIntent.Intents");
        }
        catch (Exception ex)
        {
            Fail("PurchaseIntent.Intents", ex.Message);
        }
#endif

#if IOS || MACCATALYST
        // Test 9: Message.Messages async sequence creation (no enumeration)
        // Note: MessagesType is ISwiftObject — do NOT Dispose.
        try
        {
            var seq = Message.Messages;
            Log($"Message.Messages type = {seq.GetType().Name}");
            Pass("Message.Messages");
        }
        catch (Exception ex)
        {
            Fail("Message.Messages", ex.Message);
        }
#endif

        // Test 10: Storefront.Updates async sequence creation (no enumeration)
        // Note: Storefronts is ISwiftObject — do NOT Dispose.
        try
        {
            var seq = Storefront.Updates;
            Log($"Storefront.Updates type = {seq.GetType().Name}");
            Pass("Storefront.Updates");
        }
        catch (Exception ex)
        {
            Fail("Storefront.Updates", ex.Message);
        }

        // Test 11: StoreKitError.CaseTag enum values
        try
        {
            if ((uint)StoreKitError.CaseTag.Unknown != 2)
                throw new InvalidOperationException($"Unknown tag expected 2, got {(uint)StoreKitError.CaseTag.Unknown}");
            if ((uint)StoreKitError.CaseTag.UserCancelled != 3)
                throw new InvalidOperationException($"UserCancelled tag expected 3, got {(uint)StoreKitError.CaseTag.UserCancelled}");
            if ((uint)StoreKitError.CaseTag.NotAvailableInStorefront != 4)
                throw new InvalidOperationException($"NotAvailableInStorefront tag expected 4, got {(uint)StoreKitError.CaseTag.NotAvailableInStorefront}");
            if ((uint)StoreKitError.CaseTag.NotEntitled != 5)
                throw new InvalidOperationException($"NotEntitled tag expected 5, got {(uint)StoreKitError.CaseTag.NotEntitled}");
            if ((uint)StoreKitError.CaseTag.Unsupported != 6)
                throw new InvalidOperationException($"Unsupported tag expected 6, got {(uint)StoreKitError.CaseTag.Unsupported}");
            Log($"StoreKitError CaseTag values verified");
            Pass("StoreKitError.CaseTag");
        }
        catch (Exception ex)
        {
            Fail("StoreKitError.CaseTag", ex.Message);
        }

        // Test 12: StoreKitError singleton accessors (Unknown, UserCancelled, NotAvailableInStorefront)
        try
        {
            var unknown = StoreKitError.Unknown;
            var userCancelled = StoreKitError.UserCancelled;
            var notAvailable = StoreKitError.NotAvailableInStorefront;
            var notEntitled = StoreKitError.NotEntitled;
            var unsupported = StoreKitError.Unsupported;
            if (unknown is null || userCancelled is null || notAvailable is null || notEntitled is null || unsupported is null)
                throw new InvalidOperationException("a StoreKitError singleton was null");
            if (unknown.Tag != StoreKitError.CaseTag.Unknown)
                throw new InvalidOperationException($"Unknown.Tag = {unknown.Tag}, expected Unknown");
            if (userCancelled.Tag != StoreKitError.CaseTag.UserCancelled)
                throw new InvalidOperationException($"UserCancelled.Tag = {userCancelled.Tag}, expected UserCancelled");
            Log($"StoreKitError singletons: Unknown.Tag={unknown.Tag}, UserCancelled.Tag={userCancelled.Tag}");
            Pass("StoreKitError singletons");
        }
        catch (Exception ex)
        {
            Fail("StoreKitError singletons", ex.Message);
        }

        // Test 13: Product.SubscriptionPeriod.UnitKind.CaseTag enum values
        try
        {
            if ((uint)Product.SubscriptionPeriod.UnitKind.CaseTag.Day != 0)
                throw new InvalidOperationException($"Day tag expected 0, got {(uint)Product.SubscriptionPeriod.UnitKind.CaseTag.Day}");
            if ((uint)Product.SubscriptionPeriod.UnitKind.CaseTag.Week != 1)
                throw new InvalidOperationException($"Week tag expected 1, got {(uint)Product.SubscriptionPeriod.UnitKind.CaseTag.Week}");
            if ((uint)Product.SubscriptionPeriod.UnitKind.CaseTag.Month != 2)
                throw new InvalidOperationException($"Month tag expected 2, got {(uint)Product.SubscriptionPeriod.UnitKind.CaseTag.Month}");
            if ((uint)Product.SubscriptionPeriod.UnitKind.CaseTag.Year != 3)
                throw new InvalidOperationException($"Year tag expected 3, got {(uint)Product.SubscriptionPeriod.UnitKind.CaseTag.Year}");
            Log($"Product.SubscriptionPeriod.UnitKind.CaseTag values verified");
            Pass("Product.SubscriptionPeriod.UnitKind.CaseTag");
        }
        catch (Exception ex)
        {
            Fail("Product.SubscriptionPeriod.UnitKind.CaseTag", ex.Message);
        }

        // Test 14: Product.PurchaseResult.CaseTag enum values
        try
        {
            if ((uint)Product.PurchaseResult.CaseTag.Success != 0)
                throw new InvalidOperationException($"Success tag expected 0, got {(uint)Product.PurchaseResult.CaseTag.Success}");
            if ((uint)Product.PurchaseResult.CaseTag.UserCancelled != 1)
                throw new InvalidOperationException($"UserCancelled tag expected 1, got {(uint)Product.PurchaseResult.CaseTag.UserCancelled}");
            if ((uint)Product.PurchaseResult.CaseTag.Pending != 2)
                throw new InvalidOperationException($"Pending tag expected 2, got {(uint)Product.PurchaseResult.CaseTag.Pending}");
            Log($"Product.PurchaseResult.CaseTag values verified");
            Pass("Product.PurchaseResult.CaseTag");
        }
        catch (Exception ex)
        {
            Fail("Product.PurchaseResult.CaseTag", ex.Message);
        }

        // Test 15: ExternalPurchase.NoticeResult.CaseTag enum values
        try
        {
            if ((uint)ExternalPurchase.NoticeResult.CaseTag.ContinuedWithExternalPurchaseToken != 0)
                throw new InvalidOperationException($"ContinuedWithExternalPurchaseToken tag expected 0, got {(uint)ExternalPurchase.NoticeResult.CaseTag.ContinuedWithExternalPurchaseToken}");
            if ((uint)ExternalPurchase.NoticeResult.CaseTag.Cancelled != 1)
                throw new InvalidOperationException($"Cancelled tag expected 1, got {(uint)ExternalPurchase.NoticeResult.CaseTag.Cancelled}");
            Log($"ExternalPurchase.NoticeResult.CaseTag values verified");
            Pass("ExternalPurchase.NoticeResult.CaseTag");
        }
        catch (Exception ex)
        {
            Fail("ExternalPurchase.NoticeResult.CaseTag", ex.Message);
        }

        // Test 16: AppStore.Environment static singletons (Production, Sandbox, Xcode)
        try
        {
            var production = AppStore.Environment.Production;
            var sandbox = AppStore.Environment.Sandbox;
            var xcode = AppStore.Environment.Xcode;
            if (production is null || sandbox is null || xcode is null)
                throw new InvalidOperationException("an Environment singleton was null");
            Log($"AppStore.Environment singletons: production={production.RawValue}, sandbox={sandbox.RawValue}, xcode={xcode.RawValue}");
            Pass("AppStore.Environment singletons");
        }
        catch (Exception ex)
        {
            Fail("AppStore.Environment singletons", ex.Message);
        }

        // Test 17: AppStore.Platform static singletons (IOS, MacOS, TvOS, VisionOS)
        try
        {
            var ios = AppStore.Platform.IOS;
            var macos = AppStore.Platform.MacOS;
            var tvos = AppStore.Platform.TvOS;
            var visionos = AppStore.Platform.VisionOS;
            if (ios is null || macos is null || tvos is null || visionos is null)
                throw new InvalidOperationException("a Platform singleton was null");
            Log($"AppStore.Platform singletons: ios={ios.RawValue}, macos={macos.RawValue}");
            Pass("AppStore.Platform singletons");
        }
        catch (Exception ex)
        {
            Fail("AppStore.Platform singletons", ex.Message);
        }

        // Test 18: Transaction.ReasonInfo static singletons (Purchase, Renewal)
        try
        {
            var purchase = Transaction.ReasonInfo.Purchase;
            var renewal = Transaction.ReasonInfo.Renewal;
            if (purchase is null || renewal is null)
                throw new InvalidOperationException("a ReasonInfo singleton was null");
            Log($"Transaction.ReasonInfo: Purchase={purchase.RawValue}, Renewal={renewal.RawValue}");
            Pass("Transaction.ReasonInfo singletons");
        }
        catch (Exception ex)
        {
            Fail("Transaction.ReasonInfo singletons", ex.Message);
        }

        // Test 19: Transaction.OwnershipTypeInfo static singletons (Purchased, FamilyShared)
        try
        {
            var purchased = Transaction.OwnershipTypeInfo.Purchased;
            var familyShared = Transaction.OwnershipTypeInfo.FamilyShared;
            if (purchased is null || familyShared is null)
                throw new InvalidOperationException("an OwnershipTypeInfo singleton was null");
            Log($"Transaction.OwnershipTypeInfo: Purchased={purchased.RawValue}, FamilyShared={familyShared.RawValue}");
            Pass("Transaction.OwnershipTypeInfo singletons");
        }
        catch (Exception ex)
        {
            Fail("Transaction.OwnershipTypeInfo singletons", ex.Message);
        }

        // Test 20: Product.ProductType static singletons (Consumable, NonConsumable, NonRenewable, AutoRenewable)
        try
        {
            var consumable = Product.ProductType.Consumable;
            var nonConsumable = Product.ProductType.NonConsumable;
            var nonRenewable = Product.ProductType.NonRenewable;
            var autoRenewable = Product.ProductType.AutoRenewable;
            if (consumable is null || nonConsumable is null || nonRenewable is null || autoRenewable is null)
                throw new InvalidOperationException("a ProductType singleton was null");
            Log($"Product.ProductType: Consumable={consumable.RawValue}, AutoRenewable={autoRenewable.RawValue}");
            Pass("Product.ProductType singletons");
        }
        catch (Exception ex)
        {
            Fail("Product.ProductType singletons", ex.Message);
        }

        // Test 21: Transaction.OfferTypeInfo static singletons
        try
        {
            var introductory = Transaction.OfferTypeInfo.Introductory;
            var promotional = Transaction.OfferTypeInfo.Promotional;
            var code = Transaction.OfferTypeInfo.Code;
            if (introductory is null || promotional is null || code is null)
                throw new InvalidOperationException("an OfferTypeInfo singleton was null");
            Log($"Transaction.OfferTypeInfo: Introductory={introductory.RawValue}, Promotional={promotional.RawValue}, Code={code.RawValue}");
            Pass("Transaction.OfferTypeInfo singletons");
        }
        catch (Exception ex)
        {
            Fail("Transaction.OfferTypeInfo singletons", ex.Message);
        }

        // Test 22: Metadata loads for key types
        MetadataTest<AppTransaction>("AppTransaction");
        MetadataTest<Product>("Product");
        MetadataTest<Transaction>("Transaction");
        MetadataTest<Storefront>("Storefront");
#if IOS || MACCATALYST
        MetadataTest<Message>("Message");
#endif
#if !TVOS
        MetadataTest<PurchaseIntent>("PurchaseIntent");
#endif
        MetadataTest<Product.SubscriptionInfo>("Product.SubscriptionInfo");

        // Test 23: TransactionRefundRequestError extension methods (GetErrorDescription, GetFailureReason)
        try
        {
            var desc = Transaction.RefundRequestError.DuplicateRequest.GetErrorDescription();
            var reason = Transaction.RefundRequestError.DuplicateRequest.GetFailureReason();
            var suggestion = Transaction.RefundRequestError.DuplicateRequest.GetRecoverySuggestion();
            Log($"RefundRequestError.DuplicateRequest: description={(desc ?? "(nil)")}, reason={(reason ?? "(nil)")}");
            Pass("TransactionRefundRequestError extension methods");
        }
        catch (Exception ex)
        {
            Fail("TransactionRefundRequestError extension methods", ex.Message);
        }

        // Test 24: ProductPurchaseError extension methods (GetErrorDescription, GetFailureReason, GetRecoverySuggestion)
        try
        {
            var desc = Product.PurchaseError.InvalidQuantity.GetErrorDescription();
            var reason = Product.PurchaseError.InvalidQuantity.GetFailureReason();
            var suggestion = Product.PurchaseError.InvalidQuantity.GetRecoverySuggestion();
            Log($"Product.PurchaseError.InvalidQuantity: description={(desc ?? "(nil)")}, reason={(reason ?? "(nil)")}");
            Pass("ProductPurchaseError extension methods");
        }
        catch (Exception ex)
        {
            Fail("ProductPurchaseError extension methods", ex.Message);
        }

#if IOS || MACCATALYST
        // Test 25: PaymentMethodBindingError extension method (GetErrorDescription)
        try
        {
            var desc = PaymentMethodBinding.PaymentMethodBindingError.NotEligible.GetErrorDescription();
            Log($"PaymentMethodBindingError.NotEligible: description={(desc ?? "(nil)")}");
            Pass("PaymentMethodBindingError.GetErrorDescription");
        }
        catch (Exception ex)
        {
            Fail("PaymentMethodBindingError.GetErrorDescription", ex.Message);
        }
#endif

        // Test 26: Storefront.current — REAL bounded await with a semantic assertion.
        // The previous version constructed the Task and never awaited it, so it passed even when the
        // async bridge never completed. `Storefront.current` is `async -> Storefront?` (non-throwing),
        // so exactly two outcomes are documented:
        //   (a) a Storefront — its countryCode must be an ISO-3166 alpha-3 code (three uppercase
        //       letters, e.g. "USA") and its id must be non-empty;
        //   (b) nil — a NAMED environment condition (no App Store storefront configured for this
        //       simulator), recorded as a skip, never as a feature pass.
        // Timing out is a failure.
        try
        {
            var storefront = await WithTimeout(
                Storefront.GetCurrentAsync(),
                TimeSpan.FromSeconds(30),
                "Storefront.current").ConfigureAwait(true);

            if (storefront is null)
            {
                Skip("Storefront.current",
                     "environment: Storefront.current == nil (no App Store storefront configured for this simulator)");
            }
            else
            {
                var country = storefront.CountryCode;
                var id = storefront.Id;
                Log($"Storefront.current: countryCode='{country}', id='{id}'");
                if (country is null || country.Length != 3 || !country.All(c => c >= 'A' && c <= 'Z'))
                    throw new InvalidOperationException(
                        $"countryCode must be an ISO-3166 alpha-3 uppercase code; got '{country ?? "(null)"}'");
                if (string.IsNullOrWhiteSpace(id))
                    throw new InvalidOperationException("Storefront.id was empty");
                Pass("Storefront.current returns a well-formed storefront");
            }
        }
        catch (Exception ex)
        {
            Fail("Storefront.current", $"{ex.GetType().Name}: {ex.Message}");
        }

        // Tests 27 and 27b are live Sandbox qualification. They run only when the harness has
        // proved Sandbox readiness for this exact device, bundle and product and handed the product
        // id over. Without it they are recorded as not run and the run ends TEST UNQUALIFIED,
        // which the harness never reads as a pass.
        if (sandboxProductId is not null)
        {
            // Test 27: the first of the four command-line Sandbox qualification operations. The native
            // control has already proved this exact simulator/bundle/product can reach Sandbox, so the
            // managed binding must now return the verified payload for this app from that same backend.
            try
            {
                using var shared = await WithCancellationTimeout(
                    token => AppTransaction.GetSharedAsync(token),
                    TimeSpan.FromSeconds(60),
                    TimeSpan.FromSeconds(10),
                    "AppTransaction.shared").ConfigureAwait(true);

                if (shared is null)
                    throw new InvalidOperationException("AppTransaction.shared returned a null VerificationResult");
                if (!shared.TryGetVerified(out var projected) || projected is null)
                    throw new InvalidOperationException(
                        "AppTransaction.shared did not return a verified AppTransaction after native Sandbox readiness passed");
                using var projectedLease = projected;
                var bundleId = projected.BundleID;
                var expectedBundleId = Foundation.NSBundle.MainBundle.BundleIdentifier;
                if (string.IsNullOrWhiteSpace(expectedBundleId))
                    expectedBundleId = ExpectedApplicationId;
                var environment = projected.Environment.RawValue;
                Log($"AppTransaction.shared: verified, bundleID='{bundleId}', host bundle='{expectedBundleId}', environment='{environment}'");
                if (string.IsNullOrWhiteSpace(bundleId))
                    throw new InvalidOperationException("AppTransaction.bundleID was empty");
                if (!string.Equals(bundleId, expectedBundleId, StringComparison.Ordinal))
                    throw new InvalidOperationException(
                        $"AppTransaction.bundleID was '{bundleId}' but the running app is '{expectedBundleId}' — " +
                        "the projected AppTransaction does not describe this app");
                if (!string.Equals(environment, "Sandbox", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(
                        $"AppTransaction.shared used environment '{environment}', expected Sandbox");
                Pass("AppTransaction.shared verified this app on Sandbox");
            }
            catch (ManagedUnwindTimeoutException ex)
            {
                Fail("AppTransaction.shared", ex.Message);
                return CompleteRun();
            }
            catch (Exception ex)
            {
                Fail("AppTransaction.shared", $"{ex.GetType().Name}: {ex.Message}");
            }

            // Test 27b: both finite transaction snapshots must terminate, and every element must be a
            // verified Sandbox transaction. Empty snapshots are semantically valid for a fresh tester.
            foreach (var (seqName, seq) in new (string, Transaction.Transactions)[]
                     {
                         ("Transaction.currentEntitlements", Transaction.CurrentEntitlements),
                         ("Transaction.all", Transaction.All),
                     })
            {
                try
                {
                    int count = 0;
                    bool observedCancellation = false;
                    // The deadline below has to stop the ENUMERATION, not just this test's wait on it.
                    // The token flows through WithCancellation into the generated
                    // GetAsyncEnumerator(CancellationToken) → NextAsync(CancellationToken), which routes
                    // to SBW_CancelTask and immediately cancels the managed task. Awaiting the
                    // drain proves managed unwind only; native completion/cleanup remains unproven.
                    using var drainCts = new CancellationTokenSource();
                    var drain = Task.Run(async () =>
                    {
                        try
                        {
                            await foreach (var vr in seq.WithCancellation(drainCts.Token).ConfigureAwait(false))
                            {
                                count++;
                                using (vr)
                                {
                                    if (vr is null || !vr.TryGetVerified(out var transaction) || transaction is null)
                                        throw new InvalidOperationException(
                                            $"{seqName} yielded an unverified or null transaction");
                                    using var transactionLease = transaction;
                                    var environment = transaction.Environment.RawValue;
                                    if (!string.Equals(environment, "Sandbox", StringComparison.OrdinalIgnoreCase))
                                        throw new InvalidOperationException(
                                            $"{seqName} yielded environment '{environment}', expected Sandbox");
                                }
                                if (count > 64)
                                    throw new InvalidOperationException(
                                        $"{seqName} exceeded the 64-element safety bound");
                            }
                        }
                        catch (OperationCanceledException)
                        {
                            observedCancellation = true;
                        }
                    });
                    var budget = TimeSpan.FromSeconds(60);
                    var started = DateTime.UtcNow;
                    if (!await CompletesWithin(drain, budget).ConfigureAwait(true))
                    {
                        // Request native cancellation and bound managed enumeration unwind.
                        // Stop the suite if the managed loop remains pending.
                        drainCts.Cancel();
                        var unwound = await CompletesWithin(drain, TimeSpan.FromSeconds(10)).ConfigureAwait(true);
                        if (!unwound)
                            throw new ManagedUnwindTimeoutException(
                                $"{seqName} exceeded its deadline and its managed enumeration did not unwind " +
                                "within 10s after cancellation; stopping later tests");
                        throw new TimeoutException(
                            $"{seqName} did not terminate within {budget.TotalSeconds:0}s (finite sequence expected); "
                            + $"{count} element(s) had been yielded when the bound expired; "
                            + $"managed enumeration unwound (OperationCanceledException observed: {observedCancellation}); "
                            + "native completion/cleanup is not established");
                    }

                    if (observedCancellation)
                        throw new InvalidOperationException($"{seqName} canceled before the deadline instead of completing naturally");
                    Log($"{seqName} terminated after {count} element(s) in {(DateTime.UtcNow - started).TotalSeconds:0.0}s");
                    Pass($"{seqName} terminates with verified Sandbox transactions (count={count})");
                }
                catch (ManagedUnwindTimeoutException ex)
                {
                    Fail($"{seqName} managed unwind", ex.Message);
                    return CompleteRun();
                }
                catch (Exception ex)
                {
                    Fail($"{seqName} Sandbox snapshot", $"{ex.GetType().Name}: {ex.Message}");
                }
            }
        }
        else
        {
            NotRun("AppTransaction.shared verified this app on Sandbox");
            NotRun("Transaction.currentEntitlements terminates with verified Sandbox transactions");
            NotRun("Transaction.all terminates with verified Sandbox transactions");
        }

        // Test 27c: cancellation starts already requested, removing network timing as a variable.
        // The generated managed task and async enumerator must both reach a terminal state and the
        // enumerator must dispose inside independent bounds. TimeoutTests above supplies the
        // controlled late-result ownership oracle; these probes exercise the real generated APIs.
        try
        {
            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            VerificationResult<AppTransaction>? racedResult = null;
            try
            {
                racedResult = await WithTimeout(
                    AppTransaction.GetSharedAsync(canceled.Token),
                    TimeSpan.FromSeconds(5),
                    "pre-canceled AppTransaction.shared").ConfigureAwait(true);
                throw new InvalidOperationException(
                    "pre-canceled AppTransaction.shared completed successfully instead of observing cancellation");
            }
            catch (OperationCanceledException)
            {
                Pass("AppTransaction.shared pre-canceled task unwinds");
            }
            finally
            {
                racedResult?.Dispose();
            }

            var enumerator = Transaction.All.GetAsyncEnumerator(canceled.Token);
            try
            {
                try
                {
                    var moved = await WithTimeout(
                        enumerator.MoveNextAsync().AsTask(),
                        TimeSpan.FromSeconds(5),
                        "pre-canceled Transaction.all iterator").ConfigureAwait(true);
                    if (moved)
                        enumerator.Current?.Dispose();
                    throw new InvalidOperationException(
                        $"pre-canceled Transaction.all returned MoveNext={moved} instead of observing cancellation");
                }
                catch (OperationCanceledException)
                {
                    Pass("Transaction.all pre-canceled iterator unwinds");
                }
            }
            finally
            {
                if (!await CompletesWithin(enumerator.DisposeAsync().AsTask(), TimeSpan.FromSeconds(5)).ConfigureAwait(true))
                    throw new ManagedUnwindTimeoutException(
                        "pre-canceled Transaction.all iterator disposal did not complete within 5s");
            }
            Pass("Transaction.all canceled iterator disposes within bound");
        }
        catch (ManagedUnwindTimeoutException ex)
        {
            Fail("StoreKit generated cancellation cleanup", ex.Message);
            return CompleteRun();
        }
        catch (Exception ex)
        {
            Fail("StoreKit generated cancellation cleanup", $"{ex.GetType().Name}: {ex.Message}");
        }

        // Test 27d: Transaction.updates is an UNBOUNDED listener — it never terminates on its own.
        // Cancellation must unwind the managed await foreach promptly. Generated NextAsync routes
        // the request to SBW_CancelTask then immediately cancels its managed task; this check
        // does not establish that the native Swift task completed or released its resources.
        try
        {
            using var cts = new CancellationTokenSource();
            bool observedCancellation = false;
            var listen = Task.Run(async () =>
            {
                try
                {
                    await foreach (var vr in Transaction.Updates.WithCancellation(cts.Token).ConfigureAwait(false))
                        vr?.Dispose();
                }
                catch (OperationCanceledException)
                {
                    observedCancellation = true;
                }
            });

            cts.CancelAfter(TimeSpan.FromSeconds(2));
            if (!await CompletesWithin(listen, TimeSpan.FromSeconds(30)).ConfigureAwait(true))
                throw new ManagedUnwindTimeoutException(
                    "Transaction.updates managed enumeration did not unwind within the 30s observation window; stopping later tests");

            if (!observedCancellation)
                throw new InvalidOperationException(
                    "cancelling Transaction.updates ended the loop without surfacing OperationCanceledException");
            Log("Transaction.updates managed enumeration unwound on cancellation (OperationCanceledException observed); native completion/cleanup unproven");
            Pass("Transaction.updates honours cancellation");
        }
        catch (ManagedUnwindTimeoutException ex)
        {
            Fail("Transaction.updates managed unwind", ex.Message);
            return CompleteRun();
        }
        catch (Exception ex)
        {
            Fail("Transaction.updates honours cancellation", $"{ex.GetType().Name}: {ex.Message}");
        }

        // Test 28: Transaction.RevocationReasonInfo singletons (raw-value type, not CaseTag)
        try
        {
            var devIssue = Transaction.RevocationReasonInfo.DeveloperIssue;
            var other = Transaction.RevocationReasonInfo.Other;
            if (devIssue is null || other is null)
                throw new InvalidOperationException("a RevocationReasonInfo singleton was null");
            Log($"RevocationReasonInfo: DeveloperIssue.RawValue={devIssue.RawValue}, Other.RawValue={other.RawValue}");
            Pass("Transaction.RevocationReasonInfo singletons");
        }
        catch (Exception ex)
        {
            Fail("Transaction.RevocationReasonInfo singletons", ex.Message);
        }

        // Test 29: Product.SubscriptionOffer.OfferType singletons (raw-value type, not CaseTag)
        try
        {
            var intro = Product.SubscriptionOffer.OfferType.Introductory;
            var promo = Product.SubscriptionOffer.OfferType.Promotional;
            var winBack = Product.SubscriptionOffer.OfferType.WinBack;
            if (intro is null || promo is null || winBack is null)
                throw new InvalidOperationException("a SubscriptionOffer.OfferType singleton was null");
            Pass("Product.SubscriptionOffer.OfferType singletons");
        }
        catch (Exception ex)
        {
            Fail("Product.SubscriptionOffer.OfferType singletons", ex.Message);
        }

        // Test 30: VerificationResult<T>.TryGetVerified / TryGetUnverified are reachable as concrete methods
        // with the expected out-parameter shapes. Constructing a real VerificationResult<Transaction> requires
        // a sandbox StoreKit configuration (live IAP transactions), so we verify the API surface by reflection
        // — the methods must exist with `out` parameters and return bool.
        try
        {
            var t = typeof(VerificationResult<Transaction>);

            var verified = t.GetMethod("TryGetVerified");
            if (verified is null)
                throw new InvalidOperationException("TryGetVerified method not found");
            if (verified.ReturnType != typeof(bool))
                throw new InvalidOperationException($"TryGetVerified should return bool, got {verified.ReturnType.Name}");
            var verifiedParams = verified.GetParameters();
            if (verifiedParams.Length != 1 || !verifiedParams[0].IsOut)
                throw new InvalidOperationException($"TryGetVerified should have 1 out param, got {verifiedParams.Length} (out={(verifiedParams.Length > 0 ? verifiedParams[0].IsOut.ToString() : "n/a")})");
            // out parameter ElementType is the unwrapped payload (TSignedType = Transaction here).
            if (verifiedParams[0].ParameterType.GetElementType() != typeof(Transaction))
                throw new InvalidOperationException($"TryGetVerified out param should be Transaction, got {verifiedParams[0].ParameterType.GetElementType()?.Name}");

            var unverified = t.GetMethod("TryGetUnverified");
            if (unverified is null)
                throw new InvalidOperationException("TryGetUnverified method not found");
            if (unverified.ReturnType != typeof(bool))
                throw new InvalidOperationException($"TryGetUnverified should return bool, got {unverified.ReturnType.Name}");
            var unverifiedParams = unverified.GetParameters();
            // Unverified case carries (T, VerificationError) — two out parameters.
            if (unverifiedParams.Length != 2 || !unverifiedParams[0].IsOut || !unverifiedParams[1].IsOut)
                throw new InvalidOperationException($"TryGetUnverified should have 2 out params, got {unverifiedParams.Length}");
            if (unverifiedParams[0].ParameterType.GetElementType() != typeof(Transaction))
                throw new InvalidOperationException($"TryGetUnverified[0] should be Transaction, got {unverifiedParams[0].ParameterType.GetElementType()?.Name}");

            Log($"VerificationResult<Transaction>: TryGetVerified(out Transaction), TryGetUnverified(out Transaction, out VerificationError)");
            Pass("VerificationResult<T>.TryGetVerified/TryGetUnverified shape");
        }
        catch (Exception ex)
        {
            Fail("VerificationResult<T>.TryGetVerified/TryGetUnverified shape", ex.Message);
        }

        // Test 31: with live qualification, the fourth qualification operation requests the exact
        // configured App Store Connect product and requires exactly that one Sandbox-backed product.
        // Without it, the offline suite keeps the unknown-id/empty-list assertion, which exercises
        // the async list bridge without depending on a Sandbox account.
        try
        {
            var requestedProductId = sandboxProductId
                ?? "com.swiftbindings.storekit2.tests.nonexistent";
            var products = await WithTimeout(
                Product.ProductsAsync(new[] { requestedProductId }),
                TimeSpan.FromSeconds(60),
                "Product.products(for:)").ConfigureAwait(true);
            if (products is null)
                throw new InvalidOperationException("ProductsAsync returned a null list");
            try
            {
                if (sandboxProductId is not null)
                {
                    if (products.Count != 1)
                        throw new InvalidOperationException(
                            $"exact Sandbox product '{sandboxProductId}' returned {products.Count} products; expected exactly one");
                    if (!string.Equals(products[0].Id, sandboxProductId, StringComparison.Ordinal))
                        throw new InvalidOperationException(
                            $"Product.products(for:) returned id '{products[0].Id}', expected '{sandboxProductId}'");
                    Pass($"Product.products(for:) returns exact Sandbox product '{sandboxProductId}'");
                }
                else
                {
                    if (products.Count != 0)
                        throw new InvalidOperationException(
                            $"Unknown product IDs must resolve to an empty list; got {products.Count} product(s)");
                    Pass("Product.ProductsAsync awaited (unknown id resolves to an empty list)");
                    NotRun("Product.products(for:) returns the exact configured Sandbox product");
                }
            }
            finally
            {
                foreach (var product in products)
                    product.Dispose();
            }
        }
        catch (Exception ex)
        {
            Fail("Product.ProductsAsync semantic result", ex.Message);
        }

        return CompleteRun();
    }

    internal static void Log(string msg, bool prefixed = true) =>
        Console.WriteLine(prefixed ? $"[STOREKIT2-TEST] {msg}" : msg);
}
