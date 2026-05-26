# StoreKit 2 for .NET — Usage Guide

`SwiftBindings.Apple.StoreKit2` exposes Apple's [StoreKit 2](https://developer.apple.com/documentation/storekit) In-App Purchase API to C# through .NET 10's native Swift interop. The API mirrors Swift's StoreKit closely; this guide maps the Swift workflow to the generated C# surface and walks the full purchase lifecycle end to end.

> **Looking for `purchase()`?** Swift's `product.purchase()` is exposed as **`product.PurchaseAsync()`**. Every `async` Swift method gets an `Async` suffix in C# — see [Naming conventions](#naming-conventions).

## Contents

- [Requirements & install](#requirements--install)
- [Naming conventions](#naming-conventions)
- [Quick start: a complete purchase](#quick-start-a-complete-purchase)
- [1. Check capability](#1-check-capability)
- [2. Load products](#2-load-products)
- [3. Initiate a purchase](#3-initiate-a-purchase)
- [4. Read the purchase result](#4-read-the-purchase-result)
- [5. Verify the transaction](#5-verify-the-transaction)
- [6. Finish the transaction](#6-finish-the-transaction)
- [Listening for transaction updates](#listening-for-transaction-updates)
- [Current entitlements & history](#current-entitlements--history)
- [Subscriptions](#subscriptions)
- [Restoring purchases](#restoring-purchases)
- [Storefront](#storefront)
- [App Store helpers & UI](#app-store-helpers--ui)
- [Purchase options](#purchase-options)
- [Error handling](#error-handling)
- [Memory & threading notes](#memory--threading-notes)
- [API reference](#api-reference)

## Requirements & install

- .NET 10.0+
- iOS 26.2+, macOS 26.2+, Mac Catalyst 26.2+, tvOS 26.2+
- macOS host for development
- Products configured in App Store Connect for live flows, or a `.storekit` configuration file for local testing in the simulator

```
dotnet add package SwiftBindings.Apple.StoreKit2
```

```csharp
using StoreKit2;
```

> The namespace is `StoreKit2` (not `StoreKit`) to avoid colliding with Microsoft's `StoreKit` types.

## Naming conventions

The generator applies a few consistent transforms over the Swift names. Knowing them makes the whole API predictable:

| Swift | C# | Rule |
|---|---|---|
| `func purchase(...) async` | `PurchaseAsync(...)` | `async` methods gain an `Async` suffix and return `Task`/`Task<T>` |
| `Product.products(for:)` | `Product.ProductsAsync(IEnumerable<string>)` | first label dropped; `async` suffix added |
| `enum PurchaseResult { case success(...) }` | `PurchaseResult` class with `.Tag` + `TryGetSuccess(out …)` | Swift enums-with-payload project to a class with a `CaseTag` and `TryGet*` accessors |
| `transaction.finish()` | `transaction.FinishAsync()` | — |
| trailing default args (`options: [] `) | a parameterless overload is also emitted | you can call `PurchaseAsync()` with no options |

Every async method also accepts a trailing `CancellationToken` (defaulted), so you can cancel an in-flight call.

## Quick start: a complete purchase

```csharp
using StoreKit2;

// 1. Load the product from App Store Connect / your .storekit file
var products = await Product.ProductsAsync(new[] { "com.yourapp.premium" });
var product = products[0];

// 2. Buy it. PurchaseAsync() == Swift's product.purchase().
var result = await product.PurchaseAsync();

// 3. Inspect the outcome
switch (result.Tag)
{
    case Product.PurchaseResult.CaseTag.Success:
        if (result.TryGetSuccess(out var verification) &&
            verification.TryGetVerified(out Transaction tx))
        {
            // 4. Grant entitlement, then finish so StoreKit stops re-delivering it
            GrantEntitlement(tx.ProductID);
            await tx.FinishAsync();
        }
        break;

    case Product.PurchaseResult.CaseTag.UserCancelled:
        // user dismissed the sheet
        break;

    case Product.PurchaseResult.CaseTag.Pending:
        // e.g. Ask to Buy — resolution will arrive via Transaction.Updates
        break;
}
```

The rest of this guide breaks each step down and covers entitlements, subscriptions, and restore.

## 1. Check capability

```csharp
bool canBuy = AppStore.CanMakePayments;
Guid? deviceId = AppStore.DeviceVerificationID;
```

`CanMakePayments` is `false` when payments are disabled (e.g. parental restrictions). Gate your purchase UI on it.

## 2. Load products

`Product.products(for:)` → static **`Product.ProductsAsync`**, returning your products in no guaranteed order. Identifiers that don't resolve are simply omitted from the result.

```csharp
string[] ids = { "com.yourapp.premium", "com.yourapp.coins_100" };
IReadOnlyList<Product> products = await Product.ProductsAsync(ids);

foreach (var p in products)
{
    Console.WriteLine($"{p.Id}: {p.DisplayName} — {p.DisplayPrice}");
}
```

Useful `Product` members:

| Member | Type | Notes |
|---|---|---|
| `Id` | `string` | product identifier |
| `Type` | `Product.ProductType` | `Consumable` / `NonConsumable` / `NonRenewable` / `AutoRenewable` |
| `DisplayName` | `string` | localized name |
| `Description` | `string` | localized description |
| `Price` | `NSDecimalNumber` | numeric price |
| `DisplayPrice` | `string` | localized, currency-formatted price string — show this in UI |
| `IsFamilyShareable` | `bool` | |
| `Subscription` | `Product.SubscriptionInfo?` | non-null for auto-renewable subscriptions |
| `JsonRepresentation` | `byte[]` | raw signed JSON |

```csharp
if (product.Type == Product.ProductType.AutoRenewable)
{
    var info = product.Subscription!;       // Product.SubscriptionInfo
    Console.WriteLine(info.SubscriptionGroupID);
}
```

## 3. Initiate a purchase

`product.purchase(options:)` → **`product.PurchaseAsync(...)`**, returning `Task<Product.PurchaseResult>`. Three overloads exist:

```csharp
// No options (Swift's default `options: []`)
Task<Product.PurchaseResult> PurchaseAsync(CancellationToken ct = default);

// With purchase options
Task<Product.PurchaseResult> PurchaseAsync(IReadOnlySet<Product.PurchaseOption> options, CancellationToken ct = default);

// iOS 18.2+ / Mac Catalyst 18.2+ / tvOS 18.2+ / visionOS 2.2+ (not macOS or watchOS):
// confirm the purchase in a specific view controller (requires `using UIKit;`)
Task<Product.PurchaseResult> PurchaseAsync(UIKit.UIViewController vc, IReadOnlySet<Product.PurchaseOption> options, CancellationToken ct = default);
```

```csharp
// Simplest case
var result = await product.PurchaseAsync();

// With options — see "Purchase options" below
var options = new HashSet<Product.PurchaseOption>
{
    Product.PurchaseOption.AppAccountToken(userGuid),
    Product.PurchaseOption.Quantity(3),                 // consumables
};
var result2 = await product.PurchaseAsync(options);
```

`PurchaseAsync` presents the system purchase sheet and completes when the user finishes or dismisses it. It can throw — see [Error handling](#error-handling).

## 4. Read the purchase result

`Product.PurchaseResult` is a projected Swift enum. Discriminate with `.Tag`; only `Success` carries a payload, exposed via `TryGetSuccess`.

```csharp
public enum CaseTag : uint { Success, UserCancelled, Pending }
```

```csharp
switch (result.Tag)
{
    case Product.PurchaseResult.CaseTag.Success:
        result.TryGetSuccess(out VerificationResult<Transaction> verification);
        // continue to step 5
        break;
    case Product.PurchaseResult.CaseTag.UserCancelled:
        break;
    case Product.PurchaseResult.CaseTag.Pending:
        // deferred (Ask to Buy, SCA, etc.); watch Transaction.Updates
        break;
}
```

## 5. Verify the transaction

A successful purchase yields a `VerificationResult<Transaction>` — StoreKit's signed-payload wrapper. **Always check verification before granting content.**

```csharp
public bool TryGetVerified(out T value);
public bool TryGetUnverified(out T value, out VerificationResult<T>.VerificationError error);
```

```csharp
if (verification.TryGetVerified(out Transaction tx))
{
    // Signature is valid — trust this transaction
}
else if (verification.TryGetUnverified(out Transaction untrusted, out var error))
{
    // Signature check failed — do NOT grant entitlement
    Console.WriteLine(error.ErrorDescription);
}
```

The same `VerificationResult<T>` type is used for `AppTransaction` and `SubscriptionInfo.RenewalInfo`. Extension helpers expose the raw signed data when you need server-side verification: `GetJwsRepresentation()`, `GetPayloadData()`, `GetSignedDate()`, `GetSignature()`, etc.

Key `Transaction` members:

| Member | Type | |
|---|---|---|
| `Id` | `ulong` | transaction id |
| `OriginalID` | `ulong` | original (first) transaction id |
| `ProductID` | `string` | |
| `ProductType` | `Product.ProductType` | |
| `PurchaseDate` | `DateTimeOffset` | |
| `ExpirationDate` | `DateTimeOffset?` | subscriptions |
| `RevocationDate` | `DateTimeOffset?` | non-null if refunded/revoked |
| `RevocationReason` | `Transaction.RevocationReasonType?` | |
| `OwnershipType` | `Transaction.OwnershipTypeType` | `Purchased` / `FamilyShared` |
| `AppAccountToken` | `Guid?` | the token you passed at purchase |
| `Environment` | `AppStore.Environment` | `Production` / `Sandbox` / `Xcode` |
| `PurchasedQuantity` | `int` | |
| `IsUpgraded` | `bool` | superseded by an upgrade |

## 6. Finish the transaction

After delivering content, call **`FinishAsync`**. Until you do, StoreKit considers the transaction unfinished and keeps re-delivering it through `Transaction.Updates`.

```csharp
await tx.FinishAsync();
```

## Listening for transaction updates

Set this up **once at app launch**, before any purchase, so you catch deferred purchases, renewals, refunds, and purchases made on other devices. `Transaction.Updates` is an async sequence (`IAsyncEnumerable<VerificationResult<Transaction>>`).

```csharp
_ = Task.Run(async () =>
{
    await foreach (VerificationResult<Transaction> update in Transaction.Updates)
    {
        if (update.TryGetVerified(out Transaction tx))
        {
            GrantEntitlement(tx.ProductID);
            await tx.FinishAsync();
        }
    }
});
```

## Current entitlements & history

`Transaction` exposes several static async sequences and lookups:

| Member | Returns | Use |
|---|---|---|
| `Transaction.CurrentEntitlements` | `Transaction.Transactions` (async seq) | everything the user currently owns — drive your unlock state from this |
| `Transaction.All` | async seq | full transaction history |
| `Transaction.Unfinished` | async seq | transactions awaiting `FinishAsync` |
| `Transaction.Updates` | async seq | live updates (see above) |
| `Transaction.LatestAsync(productID)` | `Task<VerificationResult<Transaction>?>` | latest transaction for one product |
| `Transaction.CurrentEntitlementAsync(productID)` | `Task<VerificationResult<Transaction>?>` | current entitlement for one product (see deprecation note) |

> **Deprecation:** `Transaction.CurrentEntitlementAsync(productID)` is marked obsolete from iOS 18.4 / macOS 15.4 / tvOS 18.4. Use `Transaction.CurrentEntitlementsMethod(productID)` (returns a `Transaction.Transactions` async sequence) instead, or just enumerate the group-wide `Transaction.CurrentEntitlements`. `LatestAsync` is **not** deprecated.

```csharp
// Rebuild entitlement state at launch
await foreach (var result in Transaction.CurrentEntitlements)
{
    if (result.TryGetVerified(out Transaction tx) && tx.RevocationDate is null)
    {
        GrantEntitlement(tx.ProductID);
    }
}
```

## Subscriptions

Reach subscription metadata through `Product.Subscription` and the static helpers on `Product.SubscriptionInfo`.

```csharp
Product.SubscriptionInfo info = product.Subscription!;
string groupId = info.SubscriptionGroupID;
Product.SubscriptionPeriod period = info.SubscriptionPeriod;

// Intro-offer eligibility
bool eligible = await Product.SubscriptionInfo.IsEligibleForIntroOfferMethodAsync(groupId);

// Status for the whole subscription group
IReadOnlyList<Product.SubscriptionInfo.Status> statuses =
    await Product.SubscriptionInfo.StatusMethodAsync(groupId);

foreach (var status in statuses)
{
    Product.SubscriptionInfo.RenewalState state = status.State; // Subscribed, Expired, …
    if (status.Transaction.TryGetVerified(out Transaction tx) &&
        status.RenewalInfo.TryGetVerified(out Product.SubscriptionInfo.RenewalInfo renewal))
    {
        Console.WriteLine($"{tx.ProductID}: {state}, auto-renew={renewal.WillAutoRenew}");
    }
}
```

`RenewalState` is **not** a C# `enum` — it's a value type exposing five static singletons. Compare with `==` or `.Equals`, not a `switch` on enum members:

```csharp
if (status.State == Product.SubscriptionInfo.RenewalState.Subscribed) { /* active */ }
```

The five values are `Subscribed`, `Expired`, `InBillingRetryPeriod`, `InGracePeriod`, `Revoked`.

`Product.SubscriptionInfo.RenewalInfo` carries `WillAutoRenew`, `AutoRenewPreference`, `ExpirationReason`, `RenewalDate`, `RenewalPrice`, `IsInBillingRetry`, `GracePeriodExpirationDate`, and offer details.

You can also watch status changes via `Product.SubscriptionInfo.Status.Updates` (async sequence).

## Restoring purchases

For a manual "Restore Purchases" button, force a sync with the App Store:

```csharp
await AppStore.SyncAsync();
// then re-read Transaction.CurrentEntitlements
```

Most apps don't need this — `Transaction.CurrentEntitlements` and `Transaction.Updates` already reflect restored purchases. Reserve `SyncAsync` for an explicit user-initiated restore.

## App receipt (`AppTransaction`)

`AppTransaction` is the signed app-level receipt — the parallel to `Transaction` for the app purchase itself (original app version, purchase date, environment, device verification). Use it for first-launch / original-purchase checks and server-side app validation.

```csharp
// Cached signed app transaction (refreshes from the App Store if needed)
VerificationResult<AppTransaction> result = await AppTransaction.GetSharedAsync();

if (result.TryGetVerified(out AppTransaction appTx))
{
    Console.WriteLine(appTx.BundleID);
    Console.WriteLine(appTx.OriginalAppVersion);
    Console.WriteLine(appTx.OriginalPurchaseDate);
    Console.WriteLine(appTx.Environment);   // AppStore.Environment
}

// Force a refresh (may prompt for App Store authentication)
var refreshed = await AppTransaction.RefreshAsync();
```

Like `VerificationResult<Transaction>`, you can pull the raw signed payload for server validation via the extension helpers (`GetJwsRepresentation()`, `GetPayloadData()`, `GetSignedDate()`, …).

## Storefront

```csharp
Storefront? sf = await Storefront.GetCurrentAsync();
Console.WriteLine(sf?.CountryCode);   // e.g. "USA"

await foreach (Storefront s in Storefront.Updates)
{
    // react to App Store country changes
}
```

## App Store helpers & UI

`AppStore` (static) bundles store-level operations. The UI helpers take a `UIWindowScene` or `UIViewController`.

| Member | |
|---|---|
| `AppStore.CanMakePayments` | capability check |
| `AppStore.DeviceVerificationID` | device verification id |
| `AppStore.SyncAsync()` | restore / force sync |
| `AppStore.ShowManageSubscriptionsAsync(scene)` | manage-subscriptions sheet |
| `AppStore.ShowManageSubscriptionsAsync(scene, groupID)` | scoped to a group |
| `AppStore.PresentOfferCodeRedeemSheetAsync(scene)` | redeem offer codes |
| `AppStore.PresentMerchandisingAsync(kind, controller)` | merchandising sheet |
| `AppStore.RequestReview(scene)` | request an App Store review |
| `AppStore.GetAgeRatingCodeAsync()` | age rating code |

```csharp
await AppStore.ShowManageSubscriptionsAsync(windowScene);
```

> Several of these UI helpers are platform-restricted by the generated `[SupportedOSPlatform]` attributes (e.g. some are unavailable on macOS/tvOS/watchOS). The compiler will flag a call that isn't valid for your target — check the IntelliSense availability annotations.

## Purchase options

Build a `Set<Product.PurchaseOption>` (any `IReadOnlySet<>`, e.g. `HashSet`) and pass it to `PurchaseAsync`. All options are static factory methods on `Product.PurchaseOption`:

| Factory | Purpose |
|---|---|
| `AppAccountToken(Guid)` | associate the purchase with your user account |
| `Quantity(int)` | quantity for consumables |
| `SimulatesAskToBuyInSandbox(bool)` | force the Ask-to-Buy flow in sandbox |
| `OnStorefrontChange(Func<Storefront, bool>)` | decide whether to continue if the storefront changes mid-purchase |
| `Custom(string key, string/double/bool/byte[] value)` | custom key/value data |
| `PromotionalOffer(...)` | apply a promotional/subscription offer (several overloads) |
| `WinBackOffer(SubscriptionOffer)` | apply a win-back offer |
| `IntroductoryOfferEligibility(string compactJWS)` | server-signed intro-offer eligibility |

```csharp
var options = new HashSet<Product.PurchaseOption>
{
    Product.PurchaseOption.AppAccountToken(currentUserId),
    Product.PurchaseOption.SimulatesAskToBuyInSandbox(true),
};
var result = await product.PurchaseAsync(options);
```

## Error handling

`PurchaseAsync` (and other throwing Swift calls) surface Swift errors as **`Swift.Runtime.SwiftException<TError>`**, whose `.Error` property is the typed Swift error. Catch the specific type you care about, or the base `SwiftException`.

```csharp
using Swift.Runtime;

try
{
    var result = await product.PurchaseAsync();
    // …
}
catch (SwiftException<StoreKitError> ex)
{
    // ex.Error is a StoreKitError. Discriminate via ex.Error.Tag, e.g.
    // NetworkError, SystemError, NotEntitled, NotAvailableInStorefront,
    // UserCancelled, Unsupported, Unknown. Only SystemError carries a payload
    // (ex.Error.TryGetSystemError(out var underlying)).
    Console.WriteLine($"StoreKit error: {ex.Message}");
}
catch (SwiftException<Product.PurchaseError> ex)
{
    // ex.Error is a Product.PurchaseError (InvalidQuantity, ProductUnavailable, …)
    Console.WriteLine(ex.Error.GetErrorDescription());
}
catch (SwiftException ex)
{
    // untyped fallback
    Console.WriteLine(ex.Message);
}
```

`Product.PurchaseError` values: `InvalidQuantity`, `ProductUnavailable`, `PurchaseNotAllowed`, `IneligibleForOffer`, `InvalidOfferIdentifier`, `InvalidOfferPrice`, `InvalidOfferSignature`, `MissingOfferParameters`. The `GetErrorDescription()` / `GetFailureReason()` / `GetRecoverySuggestion()` extension methods return localized text.

> **`userCancelled` is not an exception.** A user dismissing the sheet returns `PurchaseResult.CaseTag.UserCancelled`, not a thrown error.

## Memory & threading notes

- **Disposal.** Most StoreKit types wrap a Swift struct and implement `IDisposable`. For short-lived locals (a `Product` you read and discard) the finalizer cleans up, but disposing deterministically is better — `using` or an explicit `Dispose()` — especially in loops over the async sequences.
- **Threading.** Swift declares `purchase(options:)` as `@MainActor`. The binding handles the hop to the main actor for you; you simply `await` it. Continuations resume on a thread-pool thread, so marshal back to your UI thread before touching UI.
- **Async sequences are cold.** Enumerating `Transaction.Updates` / `CurrentEntitlements` / `Storefront.Updates` starts the underlying Swift `AsyncSequence`. `Updates` is effectively infinite — run it on a background task and key its lifetime to your app, not a screen.
- **Verification is mandatory.** Treat `TryGetUnverified` results as untrusted; never unlock content from an unverified payload.

## API reference

Consumer-facing types (namespace `StoreKit2`):

| Type | Role |
|---|---|
| `Product` | a purchasable product; `ProductsAsync`, `PurchaseAsync`, metadata |
| `Product.ProductType` | `Consumable` / `NonConsumable` / `NonRenewable` / `AutoRenewable` |
| `Product.PurchaseOption` | options passed to `PurchaseAsync` |
| `Product.PurchaseResult` | `Success` / `UserCancelled` / `Pending` |
| `Product.PurchaseError` | thrown purchase errors |
| `Product.SubscriptionInfo` | subscription metadata, status, eligibility |
| `Product.SubscriptionInfo.RenewalState` | `Subscribed` / `Expired` / `InBillingRetryPeriod` / `InGracePeriod` / `Revoked` |
| `Product.SubscriptionInfo.RenewalInfo` | auto-renew preferences, renewal price/date |
| `Product.SubscriptionInfo.Status` | current state + signed transaction & renewal info |
| `Product.SubscriptionPeriod` | unit + value; `Weekly` / `Monthly` / `Yearly` presets |
| `Product.SubscriptionOffer` | intro / promotional / win-back offers |
| `Transaction` | a completed transaction; entitlements, history, `FinishAsync` |
| `Transaction.Transactions` | async sequence of `VerificationResult<Transaction>` |
| `Transaction.RefundRequestStatus` / `RefundRequestError` | refund request flow |
| `VerificationResult<T>` | signed-payload wrapper; `TryGetVerified` / `TryGetUnverified` |
| `AppTransaction` | app-level receipt (`GetSharedAsync`, `RefreshAsync`) |
| `Storefront` | current App Store storefront |
| `AppStore` | capability checks, sync, and store UI helpers |
| `AppStore.Environment` | `Production` / `Sandbox` / `Xcode` |
| `StoreKitError` | general StoreKit errors |
| `Message` | App Store messages (price-increase consent, etc.) |
| `PurchaseIntent` | promoted in-app purchase intents |
| `ExternalPurchase`, `ExternalPurchaseLink`, `ExternalPurchaseCustomLink` | external-purchase entitlement APIs (EU) |
| `AdvancedCommerceProduct`, `PaymentMethodBinding` | Advanced Commerce APIs |

### Reference links

- [Apple — StoreKit](https://developer.apple.com/documentation/storekit)
- [Apple — In-App Purchase](https://developer.apple.com/in-app-purchase/)
- [Apple — Testing with a StoreKit configuration file](https://developer.apple.com/documentation/xcode/setting-up-storekit-testing-in-xcode)
