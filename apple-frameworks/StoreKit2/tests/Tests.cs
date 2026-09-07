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

        int CompleteRun()
        {
            Log($"Results: {passed} passed, {failed} failed, {skipped} skipped");
            Log(failed == 0 ? "TEST SUCCESS" : $"TEST FAILED: {failed} failures", prefixed: false);
            return failed;
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

        // Test 27: AppTransaction.shared — REAL bounded await with a semantic assertion.
        // `AppTransaction.shared` is `async throws -> VerificationResult<AppTransaction>`. Documented
        // outcomes:
        //   (a) a VerificationResult that projects an AppTransaction — its bundleID must be non-empty
        //       and match this test app, and its verification arm must be one of verified/unverified;
        //   (b) a typed StoreKitError — the framework's own error category for an app with no App
        //       Store receipt. The typed payload must carry a declared CaseTag.
        // Anything else (including an untyped Swift error, or a timeout) fails: the point of this
        // test is to detect an async bridge that never completes or completes with a phantom value.
        try
        {
            // 120s: AppTransaction.shared can go out to the App Store for the app receipt, so the
            // bound is wide enough for a real fetch while still failing an async bridge that never
            // completes at all.
            using var shared = await WithCancellationTimeout(
                token => AppTransaction.GetSharedAsync(token),
                TimeSpan.FromSeconds(120),
                TimeSpan.FromSeconds(30),
                "AppTransaction.shared").ConfigureAwait(true);

            if (shared is null)
                throw new InvalidOperationException("AppTransaction.shared returned a null VerificationResult");

            AppTransaction? projected;
            string arm;
            if (shared.TryGetVerified(out var verified))
            {
                projected = verified;
                arm = "verified";
            }
            else if (shared.TryGetUnverified(out var unverified, out var verificationError))
            {
                projected = unverified;
                arm = $"unverified({verificationError?.ToString() ?? "(null error)"})";
            }
            else
            {
                throw new InvalidOperationException(
                    "VerificationResult<AppTransaction> projected neither the verified nor the unverified arm");
            }

            if (projected is null)
                throw new InvalidOperationException($"VerificationResult arm '{arm}' projected a null AppTransaction");
            var bundleId = projected.BundleID;
            // The identity to compare against comes from the running host, not from a string the
            // test happens to know: a projection that reads a wrong-but-plausible bundle ID (another
            // app's, a stale one, a mis-sized string field) has to fail here, and "non-empty" cannot
            // tell those apart from the right answer.
            var expectedBundleId = Foundation.NSBundle.MainBundle.BundleIdentifier;
            if (string.IsNullOrWhiteSpace(expectedBundleId))
                expectedBundleId = ExpectedApplicationId;
            Log($"AppTransaction.shared: arm={arm}, bundleID='{bundleId}', host bundle='{expectedBundleId}', environment={projected.Environment}");
            if (string.IsNullOrWhiteSpace(bundleId))
                throw new InvalidOperationException("AppTransaction.bundleID was empty");
            if (!string.Equals(bundleId, expectedBundleId, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"AppTransaction.bundleID was '{bundleId}' but the running app is '{expectedBundleId}' — " +
                    "the projected AppTransaction does not describe this app");
            Pass($"AppTransaction.shared projects this app's AppTransaction ({arm}, bundleID matches host)");
        }
        catch (ManagedUnwindTimeoutException ex)
        {
            Fail("AppTransaction.shared", ex.Message);
            return CompleteRun();
        }
        catch (SwiftException<StoreKitError> typed)
        {
            var err = typed.Error;
            if (err is null)
            {
                Fail("AppTransaction.shared error category",
                     $"SwiftException<StoreKitError> carried no typed payload (message: {typed.Message})");
            }
            else
            {
                var tag = err.Tag;
                if (!Enum.IsDefined(typeof(StoreKitError.CaseTag), tag))
                {
                    Fail("AppTransaction.shared error category",
                         $"typed StoreKitError carried an undeclared CaseTag ({(uint)tag})");
                }
                else
                {
                    Log($"AppTransaction.shared reported typed StoreKitError.{tag} — {typed.Message}");
                    Pass($"AppTransaction.shared reports typed StoreKitError.{tag}");
                }
            }
        }
        catch (Exception ex)
        {
            Fail("AppTransaction.shared", $"{ex.GetType().Name}: {ex.Message}");
        }

        // Test 27b: Transaction.currentEntitlements / Transaction.all are FINITE async sequences that
        // terminate on their own for an app with no purchases. This exercises the generated
        // IAsyncEnumerable adapter end-to-end — MakeAsyncIterator() + NextAsync() until nil — and
        // asserts the sequence actually completes (bounded) AND yields zero elements. A non-empty
        // result here would mean the marshaller materialized transactions StoreKit never returned,
        // so we fail closed on it; a sequence that never terminates fails on the time bound.
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
                            vr?.Dispose();
                            if (count > 64) break; // guard: never spin forever on a mis-marshalled sequence
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        observedCancellation = true;
                    }
                });
                // 120s, not 30s: these sequences are backed by the App Store daemon and their first
                // element can wait on a receipt fetch. The bound still has to exist — a sequence that
                // never terminates is the defect this test is here to catch — but it has to be wide
                // enough that a slow-but-finite fetch is not reported as a hang.
                var budget = TimeSpan.FromSeconds(120);
                var started = DateTime.UtcNow;
                if (!await CompletesWithin(drain, budget).ConfigureAwait(true))
                {
                    // Request native cancellation and bound managed enumeration unwind.
                    // Stop the suite if the managed loop remains pending.
                    drainCts.Cancel();
                    var unwound = await CompletesWithin(drain, TimeSpan.FromSeconds(30)).ConfigureAwait(true);
                    if (!unwound)
                        throw new ManagedUnwindTimeoutException(
                            $"{seqName} exceeded its deadline and its managed enumeration did not unwind " +
                            "within 30s after cancellation; stopping later tests");
                    throw new TimeoutException(
                        $"{seqName} did not terminate within {budget.TotalSeconds:0}s (finite sequence expected); "
                        + $"{count} element(s) had been yielded when the bound expired; "
                        + $"managed enumeration unwound (OperationCanceledException observed: {observedCancellation}); "
                        + "native completion/cleanup is not established");
                }

                if (observedCancellation)
                    throw new InvalidOperationException($"{seqName} canceled before the deadline instead of completing naturally");
                Log($"{seqName} terminated after {count} element(s) in {(DateTime.UtcNow - started).TotalSeconds:0.0}s");
                if (count != 0)
                    throw new InvalidOperationException(
                        $"{seqName} yielded {count} element(s) for an app with no purchases — " +
                        "the marshaller materialized transactions StoreKit never returned");
                Pass($"{seqName} enumerates to completion (empty)");
            }
            catch (ManagedUnwindTimeoutException ex)
            {
                Fail($"{seqName} managed unwind", ex.Message);
                return CompleteRun();
            }
            catch (Exception ex)
            {
                Fail($"{seqName} enumerates to completion (empty)", $"{ex.GetType().Name}: {ex.Message}");
            }
        }

        // Test 27c: Transaction.updates is an UNBOUNDED listener — it never terminates on its own.
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

        // Test 31: Product.ProductsAsync — a REAL awaited StoreKit query. Unknown product IDs
        // with no .storekit configuration (unavailable outside Xcode — the App Store returns
        // no products; documented not-our-bug) resolve to an EMPTY list. We assert the full
        // semantic outcome: a non-null IReadOnlyList<Product> that is empty (Count == 0). This
        // proves the async Swift->C# bridge marshalled the real [Product] payload back — an
        // empty array, not a phantom/garbage list — rather than merely firing dispatch. A
        // non-empty result here would mean the marshaller materialized products that StoreKit
        // never returned, so we fail closed on it.
        try
        {
            var products = await WithTimeout(
                Product.ProductsAsync(new[] { "com.swiftbindings.storekit2.tests.nonexistent" }),
                TimeSpan.FromSeconds(30),
                "Product.products(for:)").ConfigureAwait(true);
            if (products is null)
                throw new InvalidOperationException("ProductsAsync returned a null list");
            Log($"Product.ProductsAsync returned {products.Count} product(s) for unknown IDs (0 expected without a .storekit config)");
            if (products.Count != 0)
                throw new InvalidOperationException(
                    $"Unknown product IDs must resolve to an empty list; got {products.Count} product(s) — " +
                    "the marshaller materialized entries StoreKit never returned.");
            Pass("Product.ProductsAsync awaited (empty-list result)");
        }
        catch (Exception ex)
        {
            Fail("Product.ProductsAsync awaited (empty-list result)", ex.Message);
        }

        return CompleteRun();
    }

    internal static void Log(string msg, bool prefixed = true) =>
        Console.WriteLine(prefixed ? $"[STOREKIT2-TEST] {msg}" : msg);
}
