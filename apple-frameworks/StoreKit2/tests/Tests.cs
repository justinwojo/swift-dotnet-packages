// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using StoreKit2;
using Swift.Runtime;

namespace SwiftBindings.StoreKit2.Tests;

internal static class Tests
{
    public static int Run()
    {
        int passed = 0, failed = 0, skipped = 0;

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

        // Test 13: Product.SubscriptionPeriod.UnitType.CaseTag enum values
        try
        {
            if ((uint)Product.SubscriptionPeriod.UnitType.CaseTag.Day != 0)
                throw new InvalidOperationException($"Day tag expected 0, got {(uint)Product.SubscriptionPeriod.UnitType.CaseTag.Day}");
            if ((uint)Product.SubscriptionPeriod.UnitType.CaseTag.Week != 1)
                throw new InvalidOperationException($"Week tag expected 1, got {(uint)Product.SubscriptionPeriod.UnitType.CaseTag.Week}");
            if ((uint)Product.SubscriptionPeriod.UnitType.CaseTag.Month != 2)
                throw new InvalidOperationException($"Month tag expected 2, got {(uint)Product.SubscriptionPeriod.UnitType.CaseTag.Month}");
            if ((uint)Product.SubscriptionPeriod.UnitType.CaseTag.Year != 3)
                throw new InvalidOperationException($"Year tag expected 3, got {(uint)Product.SubscriptionPeriod.UnitType.CaseTag.Year}");
            Log($"Product.SubscriptionPeriod.UnitType.CaseTag values verified");
            Pass("Product.SubscriptionPeriod.UnitType.CaseTag");
        }
        catch (Exception ex)
        {
            Fail("Product.SubscriptionPeriod.UnitType.CaseTag", ex.Message);
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

        // Test 18: Transaction.ReasonType static singletons (Purchase, Renewal)
        try
        {
            var purchase = Transaction.ReasonType.Purchase;
            var renewal = Transaction.ReasonType.Renewal;
            if (purchase is null || renewal is null)
                throw new InvalidOperationException("a ReasonType singleton was null");
            Log($"Transaction.ReasonType: Purchase={purchase.RawValue}, Renewal={renewal.RawValue}");
            Pass("Transaction.ReasonType singletons");
        }
        catch (Exception ex)
        {
            Fail("Transaction.ReasonType singletons", ex.Message);
        }

        // Test 19: Transaction.OwnershipTypeType static singletons (Purchased, FamilyShared)
        try
        {
            var purchased = Transaction.OwnershipTypeType.Purchased;
            var familyShared = Transaction.OwnershipTypeType.FamilyShared;
            if (purchased is null || familyShared is null)
                throw new InvalidOperationException("an OwnershipTypeType singleton was null");
            Log($"Transaction.OwnershipTypeType: Purchased={purchased.RawValue}, FamilyShared={familyShared.RawValue}");
            Pass("Transaction.OwnershipTypeType singletons");
        }
        catch (Exception ex)
        {
            Fail("Transaction.OwnershipTypeType singletons", ex.Message);
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

        // Test 21: Transaction.OfferTypeTypeType static singletons
        try
        {
            var introductory = Transaction.OfferTypeTypeType.Introductory;
            var promotional = Transaction.OfferTypeTypeType.Promotional;
            var code = Transaction.OfferTypeTypeType.Code;
            if (introductory is null || promotional is null || code is null)
                throw new InvalidOperationException("an OfferTypeTypeType singleton was null");
            Log($"Transaction.OfferTypeTypeType: Introductory={introductory.RawValue}, Promotional={promotional.RawValue}, Code={code.RawValue}");
            Pass("Transaction.OfferTypeTypeType singletons");
        }
        catch (Exception ex)
        {
            Fail("Transaction.OfferTypeTypeType singletons", ex.Message);
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

        // Test 26: Storefront.GetCurrentAsync dispatch (expect framework response = pass)
        try
        {
            var task = Storefront.GetCurrentAsync();
            if (task is null)
                throw new InvalidOperationException("GetCurrentAsync returned null Task");
            Log($"Storefront.GetCurrentAsync returned Task status = {task.Status}");
            Pass("Storefront.GetCurrentAsync dispatch");
        }
        catch (Exception ex)
        {
            Fail("Storefront.GetCurrentAsync dispatch", ex.Message);
        }

        // Test 27: AppTransaction.GetSharedAsync dispatch (expect framework response = pass)
        try
        {
            var task = AppTransaction.GetSharedAsync();
            if (task is null)
                throw new InvalidOperationException("GetSharedAsync returned null Task");
            Log($"AppTransaction.GetSharedAsync returned Task status = {task.Status}");
            Pass("AppTransaction.GetSharedAsync dispatch");
        }
        catch (Exception ex)
        {
            Fail("AppTransaction.GetSharedAsync dispatch", ex.Message);
        }

        // Test 28: Transaction.RevocationReasonType singletons (raw-value type, not CaseTag)
        try
        {
            var devIssue = Transaction.RevocationReasonType.DeveloperIssue;
            var other = Transaction.RevocationReasonType.Other;
            if (devIssue is null || other is null)
                throw new InvalidOperationException("a RevocationReasonType singleton was null");
            Log($"RevocationReasonType: DeveloperIssue.RawValue={devIssue.RawValue}, Other.RawValue={other.RawValue}");
            Pass("Transaction.RevocationReasonType singletons");
        }
        catch (Exception ex)
        {
            Fail("Transaction.RevocationReasonType singletons", ex.Message);
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

        // Summary
        Log($"Results: {passed} passed, {failed} failed, {skipped} skipped");
        if (failed == 0)
            Log("TEST SUCCESS", prefixed: false);
        else
            Log($"TEST FAILED: {failed} failures", prefixed: false);
        return failed;
    }

    internal static void Log(string msg, bool prefixed = true) =>
        Console.WriteLine(prefixed ? $"[STOREKIT2-TEST] {msg}" : msg);
}
