# ProximityReader for .NET — Usage Guide

`SwiftBindings.Apple.ProximityReader` exposes Apple's [ProximityReader](https://developer.apple.com/documentation/proximityreader) framework — **Tap to Pay on iPhone**, contactless payment-card reads, VAS (Wallet pass / loyalty) reads, and mobile-document (e.g. mobile driver's license) reads — to C# through .NET 10's native Swift interop. These are direct Swift calls, not Objective-C proxy wrappers.

This is an orientation guide: it covers the few entry points you build a flow around and is honest about what requires Apple entitlements and a live device session. Every type, method, and enum case below is verbatim from the generated C#.

## Contents

- [Requirements & install](#requirements--install)
- [Naming conventions](#naming-conventions)
- [Quick start](#quick-start)
- [The reader lifecycle](#the-reader-lifecycle)
- [Reading a payment card](#reading-a-payment-card)
- [Read requests](#read-requests)
- [Read results](#read-results)
- [Mobile documents](#mobile-documents)
- [Errors](#errors)
- [Memory & threading](#memory--threading)
- [Reference links](#reference-links)

## Requirements & install

- .NET 10.0+
- iOS 26.2+, Mac Catalyst 26.2+
- macOS host for development
- iPhone XS or later (Tap to Pay is device-only — not available in the simulator)
- **Tap to Pay on iPhone entitlement** (Apple-issued), merchant onboarding, and account linking before any live read will succeed

```
dotnet add package SwiftBindings.Apple.ProximityReader
```

```csharp
using ProximityReader;
```

> ProximityReader is permission- and session-heavy. You can construct request objects and check `PaymentCardReader.IsSupported` freely, but `Prepare`/`Read` calls require the entitlement, a merchant account, and a physical device. The co-located test app only validates type metadata and plain enum round-trips for exactly this reason.

## Naming conventions

| Swift | C# | Rule |
|---|---|---|
| `func prepare(...) async throws` | `PrepareAsync(...)` | `async` methods gain an `Async` suffix and return `Task`/`Task<T>` |
| trailing `completion:` / event closures | `Action<…>? handler` overloads | a closure parameter becomes an optional `Action<>`; a parameterless overload is also emitted |
| `static var isSupported` | `PaymentCardReader.IsSupported` | static Swift members become static C# properties |
| nested `enum Event` / `struct Options` | `PaymentCardReader.Event`, `PaymentCardReader.OptionsType` | nested types that collide with a member get a `Type` suffix |
| `enum CardEffectiveState` | `PaymentCardReadResult.CardEffectiveStateType` (returned as nullable) | optional enum results project to `…Type?` |

Every async method also accepts a trailing `CancellationToken` (defaulted).

## Quick start

Constructing requests and checking support works anywhere. The actual read requires a live, entitled session.

```csharp
using ProximityReader;
using Foundation;

// 0. Capability check (safe on any device)
if (!PaymentCardReader.IsSupported)
    return;

// 1. Build the reader and a transaction request for $1.99 USD
using var reader = new PaymentCardReader();
using var request = new PaymentCardTransactionRequest(
    NSDecimalNumber.FromString("1.99"),
    currencyCode: "USD",
    type: PaymentCardTransactionRequest.TransactionType.Purchase);

// 2. Prepare a session with your reader token, then read.
//    (Requires the Tap to Pay entitlement + a linked merchant account.)
var token = new PaymentCardReader.Token("<reader-token-from-your-backend>");
using PaymentCardReaderSession session = await reader.PrepareAsync(token);
using PaymentCardReadResult result = await session.ReadPaymentCardAsync(request);

if (result.Outcome == PaymentCardReadResult.ReadOutcome.Success)
{
    string? encrypted = result.PaymentCardData;   // signed payment data for your PSP
}
```

## The reader lifecycle

`PaymentCardReader` is the device-level object; `PaymentCardReaderSession` is the prepared, read-capable session.

```csharp
public partial class PaymentCardReader
{
    public static bool IsSupported { get; }
    public string Id { get; }
    public IAsyncEnumerable<PaymentCardReader.Event> Events { get; }
    public PaymentCardReader.OptionsType Options { get; }

    public PaymentCardReader();
    public PaymentCardReader(PaymentCardReader.OptionsType options);

    public virtual Task<string> GetReaderIdentifierAsync(CancellationToken ct = default);

    public virtual Task<bool> IsAccountLinkedAsync(PaymentCardReader.Token token, CancellationToken ct = default);
    public virtual Task        LinkAccountAsync(PaymentCardReader.Token token, CancellationToken ct = default);
    public virtual Task        RelinkAccountAsync(PaymentCardReader.Token token, CancellationToken ct = default);

    public virtual Task<PaymentCardReaderSession>               PrepareAsync(PaymentCardReader.Token token, CancellationToken ct = default);
    public virtual Task<PaymentCardReaderSession>               PrepareAsync(PaymentCardReader.Token token, Action<PaymentCardReader.UpdateEvent>? updateHandler, CancellationToken ct = default);
    public virtual Task<StoreAndForwardPaymentCardReaderSession> PrepareStoreAndForwardAsync(CancellationToken ct = default);

    public virtual PaymentCardReaderStore FetchPaymentCardReaderStore();
}
```

Reader tokens come from your backend (issued via Apple's onboarding). Wrap one with `new PaymentCardReader.Token(string rawValue)`. Watch live reader state through the `Events` async sequence.

## Reading a payment card

`PaymentCardReaderSession` carries the actual read operations:

```csharp
public partial class PaymentCardReaderSession
{
    public string Id { get; }
    public DateTimeOffset? CurrentOSVersionDeprecationDate { get; }

    public virtual Task<bool> CancelReadAsync(CancellationToken ct = default);

    // Payment / verification reads (each has a plain and an event-handler overload)
    public virtual Task<PaymentCardReadResult> ReadPaymentCardAsync(PaymentCardTransactionRequest request, CancellationToken ct = default);
    public virtual Task<PaymentCardReadResult> ReadPaymentCardAsync(PaymentCardTransactionRequest request, Action<PaymentCardReaderSession.Event>? eventHandler = null, CancellationToken ct = default);
    public virtual Task<PaymentCardReadResult> ReadPaymentCardAsync(PaymentCardVerificationRequest request, CancellationToken ct = default);
    public virtual Task<PaymentCardReadResult> ReadPaymentCardAsync(PaymentCardVerificationRequest request, Action<PaymentCardReaderSession.Event>? eventHandler = null, CancellationToken ct = default);

    // VAS (Wallet pass / loyalty)
    public virtual Task<VASReadResult> ReadVASAsync(VASRequest request, CancellationToken ct = default);
    public virtual Task<VASReadResult> ReadVASAsync(VASRequest request, Action<PaymentCardReaderSession.Event>? eventHandler = null, CancellationToken ct = default);

    // Combined payment + VAS in one tap
    public virtual Task<(PaymentCardReadResult?, VASReadResult?)> ReadPaymentCardAsync(
        PaymentCardTransactionRequest request, VASRequest vasRequest, bool stopOnVASResult, CancellationToken ct = default);

    // PIN capture
    public virtual Task<PaymentCardReadResult> CapturePINAsync(PaymentCardReaderSession.PINToken token, string cardReaderTransactionID, CancellationToken ct = default);
}
```

The event-handler overloads stream `PaymentCardReaderSession.Event` values (a plain `enum`) so you can drive UI ("hold card near reader", "remove card", etc.).

## Read requests

All three request types are constructible up front, with no session:

```csharp
// Purchase / refund
using var purchase = new PaymentCardTransactionRequest(
    NSDecimalNumber.FromString("9.99"), currencyCode: "USD",
    type: PaymentCardTransactionRequest.TransactionType.Purchase);   // or .Refund

// Card verification (no charge) — e.g. look-up, save-card, open-tab
using var verify = new PaymentCardVerificationRequest(
    currencyCode: "USD",
    reason: PaymentCardVerificationRequest.Reason.SaveCard);   // LookUp / SaveCard / OpenTab / Other

// VAS (Apple Wallet pass / loyalty)
using var merchant = new VASRequest.Merchant("merchant.com.yourapp", localizedName: "My Store");
using var vas = new VASRequest(new[] { merchant }, localizedVASType: "loyalty");
```

`PaymentCardTransactionRequest.PaymentCycle` (`Weekly` / `Monthly` / `Yearly`) is available for installment-style descriptions via `TransactionAmountDescription`.

## Read results

```csharp
public partial class PaymentCardReadResult
{
    public string Id { get; }
    public PaymentCardReadResult.ReadOutcome Outcome { get; }      // Success / CardDeclined / Failure
    public string? PaymentCardData { get; }                        // signed payment data for your PSP
    public string? GeneralCardData { get; }
    public string? ApplicationTypeIdentifier { get; }
    public bool PinBypassed { get; }
    public bool IsPINFallback { get; }
    public PaymentCardReadResult.CardEffectiveStateType? CardEffectiveState { get; }   // Active/Inactive/Invalid/Unknown
    public PaymentCardReadResult.CardExpirationStateType? CardExpirationState { get; } // NotExpired/Expired/Invalid/Unknown
}
```

`VASReadResult` carries `ReadEntry` items (each with a `StatusType`). The store-and-forward flow (`StoreAndForwardPaymentCardReaderSession`, `StoreAndForwardBatch`, `StoreAndForwardStatus`, `StoreAndForwardBatchDeletionToken`, and the `PaymentCardReaderStore` batch APIs — `FetchStoredPaymentCardReadResultCountAsync`, `FetchStoredPaymentCardReadResultBatchAsync`, `ResolveBatchAsync`, `ResetBatchStateAsync`) lets you queue reads offline and resolve them later.

## Mobile documents

ProximityReader also reads mobile identity documents (mobile driver's license, national ID, photo ID). The entry point is `MobileDocumentReader`:

```csharp
using var docReader = new MobileDocumentReader();
MobileDocumentReader.ConfigurationType config = await docReader.GetConfigurationAsync();
using MobileDocumentReaderSession docSession = await docReader.PrepareAsync(/* token */ null);
```

The data-request types (`MobileDriversLicenseDataRequest`, `MobileNationalIDCardDataRequest`, `MobilePhotoIDDataRequest`, and their `…RawDataRequest` / `…DisplayRequest` variants) and their nested `Element` / `Response` types model exactly which document fields you request and receive. `MobileDocumentAnyOfDataRequest` lets you accept any of several document types. This is a large, structured surface — consult Apple's docs for the field-by-field semantics.

## Errors

Throwing reader calls surface Swift errors as `Swift.Runtime.SwiftException<PaymentCardReaderError>`. Discriminate via the error's `Tag` (`PaymentCardReaderError.CaseTag`):

```csharp
using Swift.Runtime;

try
{
    using var session = await reader.PrepareAsync(token);
}
catch (SwiftException<PaymentCardReaderError> ex)
{
    // ex.Error.Tag — e.g. InvalidReaderToken, PrepareFailed, DeviceBanned,
    // NotAllowed, Unsupported, OsVersionNotSupported, ModelNotSupported,
    // PasscodeDisabled, NetworkError, AccountNotLinked, AccountLinkingFailed,
    // MerchantBlocked, TokenExpired, PrepareExpired, ReaderBusy, …
    Console.WriteLine($"reader error: {ex.Message}");
}
```

`PaymentCardReaderError` also exposes static singletons for common cases (e.g. `PaymentCardReaderError.PrepareExpired`).

Mobile-document errors use a plain enum, `MobileDocumentReaderError` (`Unknown=0`, `NotAllowed`, `NotSupported`, `Cancelled`, `SessionExpired`, `NetworkUnavailable`, `ServiceUnavailable`, `SystemBusy`, `InvalidToken`, `InvalidRequest`, `InvalidResponse`).

> **Known gap:** `MobileDocumentReaderError`'s `GetErrorDescription()` extension is emitted on the C# side but the matching Swift `@_cdecl` wrapper is missing, so calling it throws `EntryPointNotFoundException`. Read the enum case directly instead. Tracked as a generator bug.

## Memory & threading

Generated types implement `ISwiftObject` / `IDisposable`. Most request/result types wrap a Swift struct; reader and session types are class wrappers. `using var` is the recommended pattern for deterministic cleanup — `Dispose` is safe on every generated type and double-Dispose is a no-op.

```csharp
using var reader = new PaymentCardReader();
using var request = new PaymentCardTransactionRequest(amount, "USD",
    PaymentCardTransactionRequest.TransactionType.Purchase);
```

- **Async sequences are cold.** Enumerating `PaymentCardReader.Events` starts the underlying Swift `AsyncSequence`; run it on a background task keyed to the session lifetime.
- **Continuations** resume on a thread-pool thread — marshal back to your UI thread before touching UI.

## Reference links

- [Apple — ProximityReader](https://developer.apple.com/documentation/proximityreader) — upstream documentation
- [Apple — Tap to Pay on iPhone](https://developer.apple.com/tap-to-pay-on-iphone/) — entitlement request, merchant attestation, onboarding
