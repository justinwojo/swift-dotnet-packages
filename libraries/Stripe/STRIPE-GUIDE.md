# Stripe for .NET — usage guide

C# bindings for [Stripe's iOS SDK](https://github.com/stripe/stripe-ios) (pinned at **26.4.1**), generated with [SwiftBindings](https://github.com/justinwojo/swift-dotnet-bindings) on .NET 10's native Swift interop. Stripe ships as **14 NuGet packages** mirroring the 14 modules of the Swift SDK — you install the one(s) matching the flow you need and NuGet pulls the rest in transitively.

This guide covers picking a package, translating Stripe's Swift/ObjC documentation into the generated C# surface, and the handful of flows that are the mainstream path: **PaymentSheet** (drop-in checkout), **Core/Payments** (API client, intents, card validation), and the niche modules. It is a quick start, not a reference — for API semantics always read [Stripe's own docs](#native-documentation).

## Contents

- [Package map](#package-map)
- [Requirements and installation](#requirements-and-installation)
- [Swift → C# translation rules](#swift--c-translation-rules)
- [Quick start: PaymentSheet checkout](#quick-start-paymentsheet-checkout)
- [Configuring PaymentSheet](#configuring-paymentsheet)
- [Appearance](#appearance)
- [FlowController: custom checkout](#flowcontroller-custom-checkout)
- [StripeCore: API client and publishable key](#stripecore-api-client-and-publishable-key)
- [StripePayments: intents, payment methods, validation](#stripepayments-intents-payment-methods-validation)
- [StripePaymentsUI: card-entry controls](#stripepaymentsui-card-entry-controls)
- [Apple Pay](#apple-pay)
- [The other packages](#the-other-packages)
- [Known limitations](#known-limitations)
- [Memory and threading](#memory-and-threading)
- [Native documentation](#native-documentation)

## Package map

Every module of the Swift SDK is published as its own package. Start from the flow you want, install that package, and let NuGet resolve the rest — each package declares its siblings as real dependencies.

| Package | C# namespace | What it's for | Key entry types |
|---|---|---|---|
| `SwiftBindings.Stripe.PaymentSheet` | `StripePaymentSheet` | **Start here for checkout.** Drop-in payment UI: cards, wallets, 3DS, saved methods | `PaymentSheet`, `PaymentSheet.ConfigurationInfo`, `PaymentSheet.FlowController`, `PaymentSheetResult`, `AddressViewController` |
| `SwiftBindings.Stripe.Core` | `StripeCore` | Foundation for every other module: API client, publishable key, partner info | `STPAPIClient`, `StripeAPI`, `STPAppInfo` |
| `SwiftBindings.Stripe.Payments` | `StripePayments` | Payment/setup intents, payment methods, tokens, 3DS action handling, card validation | `STPPaymentIntentConfirmParams`, `STPPaymentMethodParams`, `STPPaymentHandler`, `STPCardValidator`, `STPAPIClientStripePaymentsExtensions` |
| `SwiftBindings.Stripe.PaymentsUI` | `StripePaymentsUI` | UIKit card-entry controls if you build your own checkout | `STPPaymentCardTextField`, `STPCardFormView`, `STPAUBECSDebitFormView`, `STPImageLibrary` |
| `SwiftBindings.Stripe.ApplePay` | `StripeApplePay` | Standalone Apple Pay without the full PaymentSheet/Payments stack | `STPApplePayContext` |
| `SwiftBindings.Stripe.Identity` | `StripeIdentity` | Stripe Identity document/selfie verification sheet | `IdentityVerificationSheet` |
| `SwiftBindings.Stripe.FinancialConnections` | `StripeFinancialConnections` | Bank-account linking sheet (ACH, balances, ownership) | `FinancialConnectionsSheet` |
| `SwiftBindings.Stripe.CardScan` | `StripeCardScan` | On-device camera PAN scanning | `CardScanSheet`, `CardImageVerificationSheet`, `ScannedCard` |
| `SwiftBindings.Stripe.Issuing` | `StripeIssuing` | Push-provisioning Stripe-issued cards into Apple Wallet, PIN retrieval | `STPPushProvisioningContext`, `STPPinManagementService` |
| `SwiftBindings.Stripe.Connect` | `StripeConnect` | Connect embedded components (onboarding, payouts) — **not usable from C# today**, see [limitations](#known-limitations) | `EmbeddedComponentManager` (no constructor) |
| `SwiftBindings.Stripe` | `Stripe` | Umbrella module. Exposes almost nothing of its own — it exists to mirror the Swift `import Stripe` layout and to pull the common modules in together | `ISTPApplePayContextDelegate` |
| `SwiftBindings.Stripe.UICore` | `StripeUICore` | Shared UI primitives consumed by the sheets. **Transitive** — nearly the whole module is `@_spi`/internal and emits no public C# API | — |
| `SwiftBindings.Stripe.ThreeDS2` | `Stripe3DS2` | ObjC-only 3-D Secure 2 challenge engine. **Transitive** — driven by `STPPaymentHandler`, not called directly | `STDSThreeDS2Service`, `STDSTransaction`, `STDSUICustomization` |
| `SwiftBindings.Stripe.CameraCore` | `StripeCameraCore` | Shared camera capture for CardScan/Identity. **Transitive** — no public C# API | — |

**About the "transitive" rows.** `UICore`, `ThreeDS2` and `CameraCore` *are* real, published NuGet packages — they are not embedded inside their consumers. They are separate packages so one copy of each xcframework is shared across siblings. You do not add them yourself — they are real NuGet `<dependency>` entries in the consuming package's nuspec, so restore pulls them in automatically. `SwiftBindings.Stripe.PaymentSheet 26.4.1` depends on `…ThreeDS2`, `…ApplePay`, `…Core`, `…Payments`, `…PaymentsUI` and `…UICore`, all at `26.4.1`; `SwiftBindings.Stripe.Identity 26.4.1` depends on `…CameraCore`, `…Core` and `…UICore`. Install them explicitly only if you intend to call their symbols directly — which, for `UICore` and `CameraCore`, you can't: their public surface is `@_spi`-annotated upstream and is suppressed from the bindings.

## Requirements and installation

- **.NET 10.0+**
- **Target framework `net10.0-ios`** — every Stripe package is iOS-only (no macOS/tvOS/Mac Catalyst slice)
- **iOS 15.0+** deployment target (`<SupportedOSPlatformVersion>15.0</SupportedOSPlatformVersion>`)
- **macOS host** for development

```bash
# drop-in checkout — pulls Core, Payments, PaymentsUI, UICore, ApplePay, ThreeDS2 with it
dotnet add package SwiftBindings.Stripe.PaymentSheet

# custom checkout — API client + intents only
dotnet add package SwiftBindings.Stripe.Payments

# individual features
dotnet add package SwiftBindings.Stripe.Identity
dotnet add package SwiftBindings.Stripe.FinancialConnections
dotnet add package SwiftBindings.Stripe.CardScan
dotnet add package SwiftBindings.Stripe.Issuing
```

The native frameworks are resolved by the package; there is no manual `NativeReference` or framework-loading step in a consuming app.

## Swift → C# translation rules

Namespaces are the Swift module names, so Stripe's Swift snippets translate almost symbol-for-symbol:

```csharp
using StripeCore;
using StripePayments;
using StripePaymentSheet;
using Swift;            // runtime types (SwiftResult, CGRect, …)
using Swift.Runtime;
```

| Swift | C# | Notes |
|---|---|---|
| `PaymentSheet.Configuration` | `PaymentSheet.ConfigurationInfo` | A nested type whose name would collide with the parent's `Configuration` **property** gets an `Info` suffix. Same for `Appearance.Font`→`FontInfo`, `Colors`→`ColorsInfo`, `Shadow`→`ShadowInfo`, `PrimaryButton`→`PrimaryButtonInfo` |
| `someValue.status` | `.StatusValue` | A property that collides with a nested enum of the same name gets a `Value` suffix (also `permissions`→`PermissionsValue`) |
| `enum Result { case completed(X), failed(Error) }` | class + `Result.CaseTag` enum + `TryGetCompleted(out X)` | Payload enums become classes; test `result.Tag == Result.CaseTag.Completed`, then read the payload via the matching `TryGetX` |
| `init?(…)` / `init(…) throws` | `static bool TryCreate(…, out T result)` | Failable/throwing initialisers |
| `func f(completion: @escaping (R) -> Void)` | `void F(…, Action<R> completion)` | Plus a generated `FAsync(…, CancellationToken)` convenience wrapper — a `TaskCompletionSource` over the callback |
| `func f(from vc: …) async -> R` | `Task<R> FFromAsync(…, CancellationToken)` | The *true* Swift-async binding (real cancellation, `@MainActor`-aware). It takes the **argument-label** name so it doesn't collide with the callback wrapper's `FAsync` — e.g. `present(from:)` → `PresentFromAsync` next to `PresentAsync`. Both are fine; prefer the label-derived one |
| Overloaded Swift members | Names derived from argument labels/types | Two Swift members that map to one C# name are disambiguated by appending their labels: `FlowController.create(paymentIntentClientSecret:configuration:completion:)` → `CreatePaymentIntentClientSecretConfigurationCompletion`. Only genuinely-ambiguous siblings get the long name; a member with no C# sibling keeps its short one |
| Swift `String?` | `string?` | Optionals map to C# nullables |
| `returnURL` next to `returnUrl` | `ReturnURL` and `ReturnUrl` | Names differing only by case both survive — check which one you mean. On `STPPaymentIntentConfirmParams`, `ReturnURL` is the live property and `ReturnUrl` its upstream-deprecated sibling |
| Swift default arguments | C# optional parameters, or expanded overloads | Varies per member — check IntelliSense |
| Extension on a type from another module | `static partial class <Type><Module>Extensions` | e.g. Swift's `STPAPIClient` payment methods live in `STPAPIClientStripePaymentsExtensions` and are C# extension methods on `StripeCore.STPAPIClient` |

**Object lifetime.** Generated types implement `ISwiftObject`/`ISwiftStruct` and `IDisposable`. Use `using var` for deterministic cleanup on short-lived values; keep long-lived UI objects (a `PaymentSheet` you are about to present, a `FlowController`) in a field so the GC doesn't collect them mid-presentation.

## Quick start: PaymentSheet checkout

Create a PaymentIntent on your server, hand the client secret to `PaymentSheet`, present it, and act on the result.

```csharp
using StripeCore;
using StripePaymentSheet;
using UIKit;

public sealed class CheckoutController : UIViewController
{
    PaymentSheet? _sheet;      // keep alive while presented

    public override void ViewDidLoad()
    {
        base.ViewDidLoad();
        StripeAPI.DefaultPublishableKey = "pk_test_…";   // once, at startup
    }

    public async Task CheckoutAsync(string paymentIntentClientSecret)
    {
        var config = new PaymentSheet.ConfigurationInfo
        {
            MerchantDisplayName = "Example, Inc.",
            ReturnURL = "myapp://stripe-redirect",
            AllowsDelayedPaymentMethods = true,
        };

        _sheet = new PaymentSheet(paymentIntentClientSecret, config);

        var result = await _sheet.PresentAsync(this);

        switch (result.Tag)
        {
            case PaymentSheetResult.CaseTag.Completed:
                Console.WriteLine("Payment complete");
                break;
            case PaymentSheetResult.CaseTag.Canceled:
                Console.WriteLine("Customer cancelled");
                break;
            case PaymentSheetResult.CaseTag.Failed:
                result.TryGetFailed(out var error);
                Console.WriteLine($"Payment failed: {error}");
                break;
        }
    }
}
```

Callback form, if you prefer it (identical semantics):

```csharp
_sheet.Present(this, result =>
{
    if (result.Tag == PaymentSheetResult.CaseTag.Completed)
        ShowReceipt();
});
```

`ReturnURL` must match a URL scheme your app declares in `Info.plist`; redirect-based payment methods (iDEAL, Klarna, …) return through it. Route the callback back into Stripe with `StripeAPI.HandleURLCallback(url)`.

A `SetupIntent` client secret works with the same constructor — pass it in place of the PaymentIntent secret.

## Configuring PaymentSheet

`PaymentSheet.ConfigurationInfo` is a plain settable object. The properties confirmed in the bindings:

```csharp
var config = new PaymentSheet.ConfigurationInfo
{
    MerchantDisplayName = "Example, Inc.",
    ReturnURL = "myapp://stripe-redirect",
    AllowsDelayedPaymentMethods = true,
    AllowsPaymentMethodsRequiringShippingAddress = false,
    PaymentMethodLayout = PaymentSheet.PaymentMethodLayout.Vertical,  // Horizontal | Vertical | Automatic
    Style = PaymentSheet.UserInterfaceStyle.Automatic,                // Automatic | AlwaysLight | AlwaysDark
    SavePaymentMethodOptInBehavior = PaymentSheet.SavePaymentMethodOptInBehavior.Automatic,
    PrimaryButtonLabel = "Pay now",
    PrimaryButtonColor = UIColor.SystemBlue,
    OpensCardScannerAutomatically = false,
    PreferredNetworks = new[] { StripePayments.STPCardBrand.Visa },
    PaymentMethodOrder = new[] { "card", "apple_pay" },
};

// Prefill billing details
var billing = config.DefaultBillingDetails;
billing.Name  = "Jane Doe";
billing.Email = "jane@example.com";
billing.Phone = "+15555550123";
billing.Address = new PaymentSheet.Address(
    city: "San Francisco", country: "US", line1: "123 Market St",
    postalCode: "94105", state: "CA");
config.DefaultBillingDetails = billing;          // write the struct back — see note below

// Which billing fields the sheet collects
var collect = config.BillingDetailsCollectionConfiguration;
collect.Name    = PaymentSheet.BillingDetailsCollectionConfiguration.CollectionMode.Always;
collect.Address = PaymentSheet.BillingDetailsCollectionConfiguration.AddressCollectionMode.Full;
config.BillingDetailsCollectionConfiguration = collect;

// Saved payment methods for a returning customer (ephemeral key from your server)
config.Customer = new PaymentSheet.CustomerConfiguration(
    id: "cus_…", ephemeralKeySecret: "ek_test_…");

// Apple Pay inside the sheet
config.ApplePay = new PaymentSheet.ApplePayConfiguration(
    merchantId: "merchant.com.example", merchantCountryCode: "US");

// Restrict accepted brands
config.CardBrandAcceptance = PaymentSheet.CardBrandAcceptance.Disallowed(
    new[] { PaymentSheet.CardBrandAcceptance.BrandCategory.Amex });

// Point the sheet at a non-default API client (e.g. Connect direct charges)
config.ApiClient = new STPAPIClient("pk_test_…") { StripeAccount = "acct_…" };
```

⚠️ **Assign struct-valued sub-objects back.** `DefaultBillingDetails`, `BillingDetailsCollectionConfiguration`, `Appearance` and friends are Swift *structs* projected as C# wrapper classes. Reading one, mutating it, and assuming the parent saw the change is not something the bindings guarantee — every such property has a setter, so read → mutate → assign back, as above. (Mutating the local wrapper does round-trip on the wrapper itself, which is why it's easy to miss.)

### Address collection

`AddressViewController` collects a shipping address with autocomplete:

```csharp
var addressConfig = new AddressViewController.ConfigurationInfo(
    new AddressViewController.ConfigurationInfo.DefaultAddressDetails(),
    new AddressViewController.ConfigurationInfo.AdditionalFieldsInfo());

var addressVc = new AddressViewController(addressConfig, myDelegate);   // IAddressViewControllerDelegate
PresentViewController(new UINavigationController(addressVc), true, null);
```

A parameterless `ConfigurationInfo()` also works if the defaults suit you; the richer overloads add `allowedCountries`, an `Appearance`, `buttonTitle` and `title`. Prefill with `DefaultAddressDetails(PaymentSheet.Address address, string? name, …)`.

The delegate hands you back an `AddressDetails`, whose nested address struct is `AddressInfo` — **not** `Address`:

```csharp
var address = new AddressViewController.AddressDetails.AddressInfo(
    city: "San Francisco", country: "US", line1: "123 Market St",
    line2: null, postalCode: "94105", state: "CA");
var details = new AddressViewController.AddressDetails(address, name: "Jane Doe");
```

## Appearance

`PaymentSheet.Appearance` mirrors the [Appearance API](https://docs.stripe.com/elements/appearance-api?platform=ios). Start from `Default` and mutate:

```csharp
var appearance = PaymentSheet.Appearance.Default;
appearance.CornerRadius = 12.0;
appearance.BorderWidth = 2.0;

var colors = appearance.Colors;
colors.Primary    = UIColor.SystemIndigo;
colors.Background = UIColor.SystemBackground;
colors.Text       = UIColor.Label;
appearance.Colors = colors;        // write back

var font = appearance.Font;
font.SizeScaleFactor = 1.1;
appearance.Font = font;

appearance.ApplyLiquidGlass();     // iOS 26 glass styling preset
config.Appearance = appearance;
```

The nested groups are `Appearance.FontInfo`, `Appearance.ColorsInfo`, `Appearance.ShadowInfo`, `Appearance.PrimaryButtonInfo` when you need to name the type; `var` (as above) avoids spelling them out. `ColorsInfo` also carries `ComponentBackground`, `ComponentBorder`, `ComponentDivider`, `ComponentText`, `ComponentPlaceholderText`, `TextSecondary`, `Icon` and `Danger`.

## FlowController: custom checkout

`PaymentSheet.FlowController` splits selection from confirmation — the customer picks a payment method in your own UI, and you confirm when they tap your button.

```csharp
PaymentSheet.FlowController? _flow;

async Task StartAsync(string paymentIntentClientSecret, PaymentSheet.ConfigurationInfo config)
{
    _flow = await PaymentSheet.FlowController
        .CreatePaymentIntentClientSecretConfigurationAsync(paymentIntentClientSecret, config);
    UpdatePaymentButton();
}

void UpdatePaymentButton()
{
    var option = _flow!.PaymentOption;         // null until one is selected
    _paymentMethodLabel.Text  = option?.Label ?? "Select";
    _paymentMethodImage.Image = option?.Image;
}

void OnSelectTapped() =>
    _flow!.PresentPaymentOptions(this, UpdatePaymentButton);

void OnPayTapped() =>
    _flow!.Confirm(this, result =>
    {
        if (result.Tag == PaymentSheetResult.CaseTag.Completed) ShowReceipt();
    });
```

The factory names carry their Swift argument labels, because Swift has three `create` overloads that would otherwise collide (see the translation table):

| What you have | Awaitable | Callback |
|---|---|---|
| PaymentIntent client secret | `CreatePaymentIntentClientSecretConfigurationAsync` | `CreatePaymentIntentClientSecretConfigurationCompletion` |
| SetupIntent client secret | `CreateSetupIntentClientSecretConfigurationAsync` | `CreateSetupIntentClientSecretConfigurationCompletion` |
| `IntentConfiguration` (deferred intent) | `CreateAsync` | `Create` — **not constructible from C#**, see [limitations](#known-limitations) |

The callback forms hand you a `SwiftResult<FlowController, …>`; the `…Async` forms are easier and are what the snippet above uses. `ConfirmFromAsync` and `PresentPaymentOptionsAsync` are the awaitable equivalents of the last two calls (`ConfirmAsync` is the `TaskCompletionSource` wrapper over `Confirm` and behaves the same). `Update(intentConfiguration, …)` / `UpdateAsync` exist but need the same unconstructible `IntentConfiguration`.

## StripeCore: API client and publishable key

```csharp
using StripeCore;

// Global default — every module picks this up
StripeAPI.DefaultPublishableKey = "pk_test_…";
StripeAPI.MaxRetries = 3;
StripeAPI.AdvancedFraudSignalsEnabled = true;

// Or a scoped client
var client = new STPAPIClient("pk_test_…")
{
    StripeAccount = "acct_…",                       // Connect direct charges
    AppInfo = new STPAppInfo("MyApp", null, "2.0", null),
};

// The process-wide shared client
var shared = STPAPIClient.Shared;
shared.PublishableKey = "pk_test_…";

Console.WriteLine(STPAPIClient.STPSDKVersion);   // e.g. "26.4.1"
Console.WriteLine(STPAPIClient.ApiVersion);      // pinned Stripe REST API version

// Redirect handling — call from your UIApplicationDelegate / scene URL handler
bool handled = StripeAPI.HandleURLCallback(url);

// Apple Pay capability
bool canApplePay = StripeAPI.GetDeviceSupportsApplePay();
var request = StripeAPI.PaymentRequest("merchant.com.example", "US", "USD");
```

## StripePayments: intents, payment methods, validation

`StripePayments` extends `STPAPIClient` with the REST surface. In C# these arrive as extension methods (`using StripePayments;` brings them into scope).

```csharp
using StripeCore;
using StripePayments;

var client = STPAPIClient.Shared;

// Build card params
var card = new STPPaymentMethodCardParams
{
    Number = "4242424242424242",
    ExpMonth = new Foundation.NSNumber(12),
    ExpYear  = new Foundation.NSNumber(2030),
    Cvc      = "314",
};
var billing = new STPPaymentMethodBillingDetails { Name = "Jane Doe" };
var pmParams = new STPPaymentMethodParams(
    card, billing, STPPaymentMethodAllowRedisplay.Always, metadata: null);

// Create a PaymentMethod
var paymentMethod = await client.CreatePaymentMethodAsync(pmParams);

// Retrieve / confirm intents (callback form)
client.RetrievePaymentIntent(clientSecret, (intent, error) => { /* … */ });

var confirm = new STPPaymentIntentConfirmParams(clientSecret)
{
    PaymentMethodParams = pmParams,
    ReturnURL = "myapp://stripe-redirect",
    ReceiptEmail = "jane@example.com",
};
client.ConfirmPaymentIntent(confirm, (intent, error) => { /* … */ });
```

Also available on the client: `CreateToken` / `CreateTokenAsync` (card, bank account, PKPayment, SSN last-4), `CreateSource` / `CreateSourceAsync`, `RetrieveSource`, `RetrieveSetupIntent`, `ConfirmSetupIntent`, `UpdatePaymentMethod`, `VerifyPaymentIntentWithMicrodeposits` / `VerifySetupIntentWithMicrodeposits` (amount pair *or* descriptor code), and `CreateRadarSession` / `CreateRadarSessionAsync`.

### 3D Secure and next actions

`STPPaymentHandler` runs authentication (including the 3DS2 challenge) against an authentication context you supply:

```csharp
var handler = STPPaymentHandler.SharedHandler;
handler.ApiClient = client;

handler.ConfirmPaymentIntent(confirm, authContext, (status, intent, error) =>
{
    if (status == STPPaymentHandlerActionStatus.Succeeded) ShowReceipt();
});
```

The `handleNextAction` / `confirmSetupIntent` families each have several Swift overloads, so they bind under their argument labels: `HandleNextActionPaymentIntentClientSecretAuthenticationContextReturnURLCompletion`, `HandleNextActionSetupIntentClientSecretAuthenticationContextReturnURLCompletion`, `ConfirmSetupIntentParamsAuthenticationContextCompletion` (plus `…Async` variants of the first two). The short names — `ConfirmPayment`, `ConfirmSetupIntentWithCompletion`, `HandleNextActionForPaymentWithReturnURLCompletion` — are Stripe's own upstream-deprecated spellings and carry a `[Obsolete]` saying so; use the label-derived ones above.

⚠️ `authContext` is an `ISTPAuthenticationContext`, an `@objc` protocol with no generated reverse-dispatch proxy. A **C#-authored** implementation is accepted by the compiler but will never be called back from Swift — the four non-deprecated `STPPaymentHandler` entry points are marked `SB0008` for exactly this reason. Until the proxy is generated, prefer PaymentSheet (which owns its own authentication context internally) for anything needing 3DS.

### Card validation

`STPCardValidator` is all-static and fully usable:

```csharp
var brand  = STPCardValidator.Brand("4242424242424242");                 // STPCardBrand.Visa
var state  = STPCardValidator.ValidationState("4242424242424242", true); // STPCardValidationState
var cvcLen = STPCardValidator.MaxCVCLength(brand);                       // nuint
var digits = STPCardValidator.SanitizedNumericString(userInput);
var expiry = STPCardValidator.ValidationState("2030", "12");             // note: (year, month)

// Co-branded card lookup is a network call, so it's callback-shaped
STPCardValidator.PossibleBrands("4242424242424242", result =>
{
    if (result.TryGetSuccess(out var brands)) { /* SwiftSet<STPCardBrand> */ }
});
```

## StripePaymentsUI: card-entry controls

UIKit views for hand-rolled checkout:

```csharp
using StripePaymentsUI;

var textField = new STPPaymentCardTextField(new Swift.CGRect(0, 0, 320, 44));
View.AddSubview(textField);

var form = new STPCardFormView(STPCardFormViewStyle.Standard);
form.Delegate = myFormDelegate;                    // ISTPCardFormViewDelegate — works
var pmParams = form.CardParams;                    // STPPaymentMethodParams? once valid

var au = new STPAUBECSDebitFormView("Example, Inc.");

var brandIcon = STPImageLibrary.CardBrandImage(StripePayments.STPCardBrand.Visa);
var cvcIcon   = STPImageLibrary.CvcImage(StripePayments.STPCardBrand.Amex);
```

⚠️ `ISTPPaymentCardTextFieldDelegate` implemented in C# receives **no** callbacks — every one of its twelve requirements is `@objc optional` upstream, and optional ObjC requirements get no reverse-dispatch slot, so the generated proxy's vtable is empty. Note there is **no compile-time warning** on this interface. Poll `STPPaymentCardTextField` state instead (`IsValid`, `CardParams`, `PaymentMethodParams`, `CardNumber`, …), or use `STPCardFormView`, whose single required delegate method does forward. See [limitations](#known-limitations).

## Apple Pay

Building a `PKPaymentRequest` and checking device capability works today:

```csharp
if (StripeAPI.GetDeviceSupportsApplePay())
{
    // Returns a PassKit PKPaymentRequest pre-filled with your merchant identity
    PKPaymentRequest request = StripeAPI.PaymentRequest(
        merchantIdentifier: "merchant.com.example", countryCode: "US", currencyCode: "USD");

    // …populate request.PaymentSummaryItems with PassKit APIs as usual…

    bool ok = StripeAPI.CanSubmitPaymentRequest(request);
}
```

⚠️ **The standalone `STPApplePayContext` flow is not usable from C# in this release.** `STPApplePayContext.TryCreate(request, delegate, out ctx)` requires an `I_stpinternal_STPApplePayContextDelegateBase`, an `@objc` protocol existential — passing a C# implementation throws `NotSupportedException` at the marshalling boundary ("only a value vended by the Swift library round-trips"). Once you have a Swift-vended context the instance methods (`PresentApplePay(…)`, the `PaymentStatus` enum) work, but there is no way to author the delegate from managed code.

**Use Apple Pay through PaymentSheet instead** — set `config.ApplePay = new PaymentSheet.ApplePayConfiguration("merchant.com.example", "US")` and the sheet drives the context internally. That path is fully supported. (`ApplePayConfiguration.Handlers` does bind as a type — `Handlers.Create()` builds an empty one, and the constructor's `customHandlers:` parameter accepts it — but all four hook properties are unreachable: `paymentRequestHandler` and `authorizationResultHandler` are closure-typed and not yet marshallable, and the two shipping-update handlers are `@_spi`-suppressed upstream. The default handling is what you get.)

## The other packages

**Identity** (`SwiftBindings.Stripe.Identity`) — presents Stripe's hosted verification flow. Construct with the client secret from your server (`new IdentityVerificationSheet(verificationSessionClientSecret)`, or the `(sessionId, ephemeralKeySecret, Configuration)` overload with a `Configuration(UIImage brandLogo)` for the native flow) and `await sheet.PresentAsync(vc)`; the result's `Tag` is `FlowCompleted`, `FlowCanceled` or `FlowFailed`. Semantics: [docs.stripe.com/identity](https://docs.stripe.com/identity).

**FinancialConnections** (`SwiftBindings.Stripe.FinancialConnections`) — bank-account linking. `new FinancialConnectionsSheet(clientSecret, returnURL)` then `PresentAsync(vc)` (`Result.CaseTag.Completed` carries a `FinancialConnectionsSession`) or `PresentForTokenAsync(vc)` when you need an ACH `BankAccountToken` as well. `OnEvent` takes an `Action<StripeCore.FinancialConnectionsEvent>` for progress telemetry. Semantics: [docs.stripe.com/financial-connections](https://docs.stripe.com/financial-connections).

**CardScan** (`SwiftBindings.Stripe.CardScan`) — `new CardScanSheet().Present(vc, result => …)` opens the camera; on `CardScanSheetResult.CaseTag.Completed`, `TryGetCompleted(out ScannedCard card)` gives `Pan`, `ExpiryMonth`, `ExpiryYear`, `Name`. `CardImageVerificationSheet(cardImageVerificationIntentId, secret)` is the server-driven CIV variant. Callback-only — no `Task` overloads are generated. Requires an `NSCameraUsageDescription` in `Info.plist`.

**Issuing** (`SwiftBindings.Stripe.Issuing`) — push provisioning into Apple Wallet. `STPPushProvisioningContext.RequestConfiguration(name, description, last4, brand, primaryAccountIdentifier)` builds the `PKAddPaymentPassRequestConfiguration`; `new STPPushProvisioningContext(keyProvider)` and `new STPPinManagementService(keyProvider)` (`RetrievePin`, `UpdatePin`) do the work. Note that `ISTPIssuingCardEphemeralKeyProvider` (and its sibling `ISTPCustomerEphemeralKeyProvider`) carries an interface-level `SB0010`: its single requirement takes a completion closure that can't be marshalled, so **no** requirement is reverse-dispatchable and a C#-authored key provider is never called back from Swift. The proxy's own forward-direction member is marked `SB0003` for the same reason. This flow is effectively read-only from managed code today. Semantics: [docs.stripe.com/issuing/cards/digital-wallets](https://docs.stripe.com/issuing/cards/digital-wallets?platform=iOS).

**Connect** (`SwiftBindings.Stripe.Connect`) — embedded components for platforms. **Not usable from C#**: `EmbeddedComponentManager` has no public constructor because its `fetchClientSecret` closure parameter is not yet bridgeable, and every component view hangs off the manager. The value types (`Appearance` and its nested `…Info` groups, `AccountCollectionOptions`) do construct, but there is nothing to attach them to. Semantics: [docs.stripe.com/connect/supported-embedded-components](https://docs.stripe.com/connect/supported-embedded-components).

**ThreeDS2** (`SwiftBindings.Stripe.ThreeDS2`) — the only ObjC-bound package here, so it follows bgen conventions (the `ApiDefinition.cs` interface `STDSThreeDS2Service` becomes a class of that name). `STDSUICustomization` is the interesting type if you want to theme the 3DS challenge screen; `STDSThreeDS2Service` / `STDSTransaction` are normally driven for you by `STPPaymentHandler`. Semantics: [docs.stripe.com/payments/3d-secure](https://docs.stripe.com/payments/3d-secure).

**Umbrella, UICore, CameraCore** — `SwiftBindings.Stripe` exposes only `ISTPApplePayContextDelegate` (the composed Apple Pay delegate protocol) beyond framework registration; `StripeUICore` and `StripeCameraCore` compile to nothing but `// Unsupported: … ModuleInternal (@_spi type)` comments. They are dependency plumbing — install a feature package, not these.

## Known limitations

Verified against the generated bindings for 26.4.1 / SDK 0.19.0. None of these are crashes at runtime — they are surfaces that cannot be reached or driven from C#.

| Flow | Status | Cause |
|---|---|---|
| **Deferred-intent PaymentSheet** (`PaymentSheet.IntentConfiguration`) | Unavailable | Every `IntentConfiguration.init` takes a closure the generator can't marshal (`confirmHandler`, or `confirmationTokenConfirmHandler`); the remaining one is `@_spi`. The `PaymentSheet(intentConfiguration, config)` and `FlowController.Create(intentConfiguration, …)` overloads exist but there is no way to construct the argument. **Use the client-secret constructors.** |
| **EmbeddedPaymentElement** | Unavailable | Same `IntentConfiguration` dependency — `EmbeddedPaymentElement.CreateAsync` takes one |
| **CustomerSheet** / `StripeCustomerAdapter` | Unavailable | The `CustomerSheet(configuration, ICustomerAdapter)` constructor is emitted, but no adapter can be built: `StripeCustomerAdapter.init` takes a `customerEphemeralKeyProvider` closure, and all nine `ICustomerAdapter` proxy members are `SB0003` ("async methods require Swift concurrency runtime"). The `(configuration, intentConfiguration, provider)` constructor is `SB0005`. Manage saved methods with `PaymentSheet.CustomerConfiguration` instead |
| **StripeConnect embedded components** | Unavailable | `EmbeddedComponentManager` has no constructible initialiser — one `init` is `@_spi`, the other takes a `fetchClientSecret` closure. Its `CreateAccountOnboardingController` / `CreatePaymentsViewController` / `CreatePayoutsViewController` factories and the whole `Appearance` tree are emitted, but there is no manager to call them on |
| **Standalone `STPApplePayContext`** | Unavailable from C# | `@objc` protocol existential delegate — `TryCreate` is `SB0008` and throws `NotSupportedException` for a managed conformer. Use PaymentSheet's `ApplePayConfiguration` |
| **`ISTPPaymentCardTextFieldDelegate`** | Callbacks never fire, **no warning** | All twelve requirements are `@objc optional`; optional ObjC requirements get no reverse-dispatch slot, so the generated proxy's vtable is empty. It is not marked `SB0010` even though it qualifies. `ISTPCardFormViewDelegate`, `ISTPAUBECSDebitFormViewDelegate` and `IAddressViewControllerDelegate` all have required members and *are* dispatched |
| **`ISTPAuthenticationContext`** (for `STPPaymentHandler`) | Managed conformer never invoked | `SB0008` — `@objc` existential with no generated conformance proxy at all |
| **`ISTPIssuingCardEphemeralKeyProvider`**, **`ISTPCustomerEphemeralKeyProvider`** | Managed conformer never invoked | `SB0010` on the interface: the only requirement takes a completion closure that cannot be marshalled, so nothing is reverse-dispatchable |
| **`@_spi` types** across `StripeUICore`, `StripeCameraCore`, `PaymentAnalytic`, half of `ApplePayConfiguration.Handlers`, various internals | Not emitted | Upstream `@_spi`/internal annotations are deliberately excluded from public bindings |

There are **no `SB0001` non-callable stubs** anywhere in the Stripe bindings — everything the generator emitted as a public member is callable. The full obsolete inventory across all 14 packages is 20 × `SB0003` (protocol-typed member not dispatchable), 5 × `SB0008` (managed conformer never invoked), 2 × `SB0010` (whole interface inert for reverse dispatch), and one each of `SB0002` (return type has no usable surface) and `SB0005` (closure shape not bridgeable). All are compile-time warnings pointing at the surfaces in the table above; everything else you will see marked `[Obsolete]` is Stripe's own upstream deprecation text.

Two diagnostics from the analyzer are worth knowing about even though nothing in the bindings carries them: `SB1003` fires when you write through a struct-valued property (`config.DefaultBillingDetails.Name = …`) and the write lands on a copy — the read → mutate → assign-back pattern above is what it is asking for. `SB0010` marks an interface no C# implementation of which will ever be called back.

Stripe's API surface shifts between minor releases — if you upgrade the package, re-check any member you depend on rather than assuming it survived.

## Memory and threading

- **UI must be driven on the main thread.** `Present`, `Confirm`, `PresentPaymentOptions` and every sheet constructor expect the UI thread; use `InvokeOnMainThread` if you're arriving from a background continuation. The true Swift-async variants (`PresentFromAsync`, `ConfirmFromAsync`, `PresentPaymentOptionsAsync`) are bound as `[SwiftMainActor]` and resume appropriately.
- **Completions arrive on the main thread** for all the presentation APIs, so you can touch UIKit directly inside them.
- **Keep presented objects alive.** Store the `PaymentSheet` / `FlowController` / `IdentityVerificationSheet` in a field for the duration of the presentation — a local that goes out of scope can be collected while Swift still holds the presentation.
- **`using var` for short-lived values.** Params objects, appearance structs and results are `IDisposable`; disposing is safe and double-dispose is a no-op. Do not dispose an object you have handed to a sheet that is still on screen.
- **`Task` overloads and cancellation.** The plain `…Async` overload wraps a callback in a `TaskCompletionSource` — the `CancellationToken` stops your `await`, not the native work. The label-derived sibling (`PresentFromAsync`, `ConfirmFromAsync`) is a true Swift async binding and cancels the underlying task.

## Native documentation

The bindings are a faithful but partial projection of the Swift SDK. For API semantics, parameter meanings, server-side setup and testing, use Stripe's own documentation and translate with the rules above:

- [Stripe iOS SDK docs](https://docs.stripe.com/sdks/ios) — the SDK landing page
- [stripe.dev/stripe-ios](https://stripe.dev/stripe-ios/) — generated API reference for every module ([StripePaymentSheet](https://stripe.dev/stripe-ios/stripepaymentsheet/documentation/stripepaymentsheet), [StripePayments](https://stripe.dev/stripe-ios/stripepayments/documentation/stripepayments))
- [Accept a payment (iOS)](https://docs.stripe.com/payments/accept-a-payment?platform=ios) — the PaymentSheet integration this guide mirrors
- [Mobile payments quickstart](https://docs.stripe.com/payments/quickstart?platform=ios)
- [Appearance API](https://docs.stripe.com/elements/appearance-api?platform=ios)
- [Save and reuse payment methods](https://docs.stripe.com/payments/save-and-reuse?platform=ios)
- [PaymentIntents](https://docs.stripe.com/payments/payment-intents) · [API keys](https://docs.stripe.com/keys) · [3D Secure](https://docs.stripe.com/payments/3d-secure)
- [Apple Pay](https://docs.stripe.com/apple-pay) · [Identity](https://docs.stripe.com/identity) · [Financial Connections](https://docs.stripe.com/financial-connections) · [Issuing digital wallets](https://docs.stripe.com/issuing/cards/digital-wallets?platform=iOS) · [Connect embedded components](https://docs.stripe.com/connect/supported-embedded-components)
- [github.com/stripe/stripe-ios](https://github.com/stripe/stripe-ios) — upstream source (pinned at 26.4.1)

Never embed a **secret** key in a mobile app — the SDK takes a publishable key only, and intents/ephemeral keys must be minted server-side. See [Stripe's security guide](https://docs.stripe.com/security/guide).
