# TipKit for .NET — Usage Guide

`SwiftBindings.Apple.TipKit` exposes Apple's [TipKit](https://developer.apple.com/documentation/tipkit) framework — in-app tips for surfacing features — to C# through .NET 10's native Swift interop. These are direct Swift calls, not Objective-C proxy wrappers. TipKit's model is non-obvious: a *tip* is a Swift type conforming to the `Tip` protocol, its *eligibility* is governed by display rules and donated *events*, and its current *status* (pending / available / invalidated) is what you check before showing UI. This guide maps that model to the generated C# surface and is explicit about which parts can be driven from pure C# versus which require a Swift companion.

## Contents

- [Requirements & install](#requirements--install)
- [Naming conventions](#naming-conventions)
- [The TipKit model in C#](#the-tipkit-model-in-c)
- [Quick start: configure and check status](#quick-start-configure-and-check-status)
- [Configuring the datastore](#configuring-the-datastore)
- [Tip status & invalidation](#tip-status--invalidation)
- [Display-frequency options](#display-frequency-options)
- [Donations & events](#donations--events)
- [Displaying tips (UIKit)](#displaying-tips-uikit)
- [Tip actions](#tip-actions)
- [Errors](#errors)
- [Known limitations](#known-limitations)
- [Memory & threading](#memory--threading)
- [Reference links](#reference-links)

## Requirements & install

- .NET 10.0+
- iOS 26.2+, macOS 26.2+, Mac Catalyst 26.2+, tvOS 26.2+
- macOS host for development

```
dotnet add package SwiftBindings.Apple.TipKit
```

```csharp
using TipKit;
```

## Naming conventions

| Swift | C# | Rule |
|---|---|---|
| `Tips.configure(_:)` | `Tips.Configure(IEnumerable<Tips.ConfigurationOption>)` | static methods are PascalCase; the trailing default-empty form gets a no-arg overload `Tips.Configure()` |
| `enum Status { case available }` | `Tips.Status` class with `.Tag` (a `CaseTag` enum) + static singletons | Swift enums-with-payload project to a class with a `CaseTag` and `TryGet*` accessors; payload-less cases are static properties (`Tips.Status.Available`) |
| `enum InvalidationReason: Int` (plain) | C# `enum : int` | plain Swift enums become ordinary C# enums |
| `.maxDisplayCount(3)` (a `Tips.Rule.Option`) | `new Tips.MaxDisplayCount((nint)3)` | option cases project to constructible types |
| `Tips.DonationTimeRange.minutes(5)` | `Tips.DonationTimeRange.Minutes(5)` | enum-with-associated-value factories become static methods |
| nested `Tips.ConfigurationOption.DisplayFrequency.daily` | `Tips.ConfigurationOption.DisplayFrequency.Daily` (static property) | nested config presets are static singletons |

> Most of the consumer-facing types are **nested under the static `Tips` class** (`Tips.Status`, `Tips.ConfigurationOption`, `Tips.MaxDisplayCount`, `Tips.DonationTimeRange`, `Tips.Action`, …). A few live at the `TipKit` namespace root: `TipKitError`, `TipGroup`, `AnyTip`, the `ITip` interface, and the UIKit view types (`TipUIView`, `TipUIPopoverViewController`, `TipUICollectionViewCell`, `TipUICollectionReusableView`).

## The TipKit model in C#

The single most important fact: **you cannot define a `Tip` type in pure C#.** In Swift you write `struct MyTip: Tip { ... }`; that protocol conformance (and the result-builder rules it carries) is compiler-synthesized and has no C#-constructible equivalent. The binding instead gives you:

- **`ITip`** — the projected `Tip` protocol. Its `Status`, `ShouldDisplay`, and `Invalidate(...)` members are protocol-extension defaults that throw `NotSupportedException` when called through the interface; they only work on a concrete conforming type (which lives in Swift).
- **`AnyTip`** — a type-erased wrapper around an existing Swift tip, exposing read-only metadata (`Id`, `Title`, `Message`, `Image`, `Options`). It has no public constructor; you obtain one from Swift via `AnyTip.FromTipKit_AnyTip(...)`.

So the supported pattern from C# is: **define your tips in a small Swift companion target, then drive configuration, display, donations, and presentation from C#.** Everything in the rest of this guide — datastore configuration, status enums, option/frequency types, donation time ranges, the UIKit presentation controller, actions, and errors — is fully usable from C#. The rule-builder DSL itself is the one piece that must originate in Swift.

## Quick start: configure and check status

```csharp
using TipKit;

// 1. Configure the TipKit datastore once, at app startup.
//    The no-arg overload uses defaults; pass options to customize (see below).
try
{
    Tips.Configure();
}
catch (Exception ex)
{
    // Configure throws TipKitError.TipsDatastoreAlreadyConfigured if called twice.
}

// 2. Inspect a tip's current status. (`tip` is an ITip provided by your
//    Swift companion target — see "The TipKit model in C#".)
ITip tip = GetMyTipFromSwift();
// Work with the type-erased metadata:
using var anyTip = AnyTip.FromTipKit_AnyTip(/* a Swift AnyTip */);
string title = anyTip.Title.ToString();

// 3. Compare status singletons / tags:
if (Tips.Status.Available.Tag == Tips.Status.CaseTag.Available)
{
    // a tip in the Available state is eligible to be shown
}

// 4. Testing helpers force every tip visible / hidden regardless of rules:
Tips.ShowAllTipsForTesting();
Tips.HideAllTipsForTesting();

// 5. Reset stored donation/event/display state (e.g. between test runs):
Tips.ResetDatastore();
```

## Configuring the datastore

`Tips.Configure` initializes TipKit's persistent store. Two overloads:

```csharp
Tips.Configure();                                            // defaults
Tips.Configure(IEnumerable<Tips.ConfigurationOption> options); // customized
```

Build the options from the static factories on `Tips.ConfigurationOption`, each of which wraps a nested preset type:

```csharp
var options = new[]
{
    Tips.ConfigurationOption.DisplayFrequencyMethod(
        Tips.ConfigurationOption.DisplayFrequency.Daily),
    Tips.ConfigurationOption.DatastoreLocationMethod(
        Tips.ConfigurationOption.DatastoreLocation.ApplicationDefault),
};
Tips.Configure(options);
```

`Tips.ConfigurationOption` factories:

| Factory | Argument | |
|---|---|---|
| `DisplayFrequencyMethod(DisplayFrequency)` | a frequency preset | how often tips may appear globally |
| `DatastoreLocationMethod(DatastoreLocation)` | a store location | where state is persisted |
| `CloudKitContainerMethod(CloudKitContainer?)` | a CloudKit container | sync tip state across devices |

`Tips.ConfigurationOption.DisplayFrequency` presets (static properties): `Immediate`, `Hourly`, `Daily`, `Weekly`, `Monthly`.

`Tips.ConfigurationOption.DatastoreLocation`: `ApplicationDefault` (static), plus `GroupContainer(string identifier)` and `Url(Foundation.NSUrl url)` factories.

`Tips.ConfigurationOption.CloudKitContainer`: `Automatic` (static) and `Named(string containerName)`.

## Tip status & invalidation

`Tips.Status` is a projected Swift enum. Discriminate with `.Tag`; the `Invalidated` case carries a reason.

```csharp
public enum Tips.Status.CaseTag : uint
{
    Invalidated = 0,
    Pending     = 1,
    Available   = 2,
}
```

```csharp
Tips.Status status = /* a status value */;

switch (status.Tag)
{
    case Tips.Status.CaseTag.Available:
        // eligible to show
        break;
    case Tips.Status.CaseTag.Pending:
        // rules not yet satisfied
        break;
    case Tips.Status.CaseTag.Invalidated:
        if (status.TryGetInvalidated(out Tips.InvalidationReason reason))
        {
            // reason tells you why it was dismissed
        }
        break;
}
```

Cached singletons for the payload-less cases:

```csharp
Tips.Status p = Tips.Status.Pending;
Tips.Status a = Tips.Status.Available;
// the payload case is a factory:
Tips.Status inv = Tips.Status.Invalidated(Tips.InvalidationReason.TipClosed);
```

`Tips.InvalidationReason` (plain `enum : int`):

| Case | Value |
|---|---|
| `ActionPerformed` | 0 |
| `DisplayCountExceeded` | 1 |
| `DisplayDurationExceeded` | 2 |
| `TipClosed` | 3 |

> **Invalidating a tip from C#** goes through the concrete tip's `Invalidate(reason:)`, which lives on your Swift conforming type. The `ITip.Invalidate(...)` interface default throws `NotSupportedException` — expose an invalidation entry point from your Swift companion if you need to invalidate programmatically.

## Display-frequency options

These per-tip options correspond to Swift's `Tips.Rule.Option`/`@Parameter`-adjacent option cases and are constructible directly. They configure how an individual tip is gated.

```csharp
var maxCount    = new Tips.MaxDisplayCount((nint)3);          // show at most 3 times
var maxDuration = new Tips.MaxDisplayDuration(30.0);          // seconds on screen
var ignoresFreq = new Tips.IgnoresDisplayFrequency(true);     // bypass global frequency
```

| Type | Constructor | |
|---|---|---|
| `Tips.MaxDisplayCount` | `new Tips.MaxDisplayCount(nint maxDisplayCount)` | cap total displays |
| `Tips.MaxDisplayDuration` | `new Tips.MaxDisplayDuration(double maxDisplayDuration)` | cap on-screen time (seconds) |
| `Tips.IgnoresDisplayFrequency` | `new Tips.IgnoresDisplayFrequency(bool)` | exempt from global `DisplayFrequency` |

All three conform to `ITipOption`. Like configuration options, they're consumed by tips declared in Swift; constructing them in C# lets you supply values to a Swift-side tip factory.

## Donations & events

TipKit's eligibility engine counts *event donations* — "the user did X N times". The supporting value types are bound, even though the rule-builder DSL that consumes them is Swift-only (see [Known limitations](#known-limitations)).

```csharp
// A time window for donation-count rules:
Tips.DonationTimeRange lastWeek  = Tips.DonationTimeRange.Week;     // preset
Tips.DonationTimeRange last5Min  = Tips.DonationTimeRange.Minutes(5);
Tips.DonationTimeRange last2Days = Tips.DonationTimeRange.Days(2);

// A cap on how much donation history to retain:
using var limit = new Tips.DonationLimit(maximumCount: 100);
int cap = limit.MaximumCount;   // 100
```

`Tips.DonationTimeRange` — presets (static): `Minute`, `Hour`, `Day`, `Week`; factories: `Minutes(nint)`, `Hours(nint)`, `Days(nint)`, `Weeks(nint)` (each also has an `int` convenience overload). It supports value equality and a `DecodeFromJson(byte[])` helper.

`Tips.DonationLimit` — `new Tips.DonationLimit(nint maximumCount, Tips.DonationTimeRange? maximumAge = null)`, exposing `.MaximumCount`.

`Tips.ParameterOption.Transient` (static singleton) marks an event/parameter as non-persisted.

> The `Tips.Event<TDonationInfo>`, `Tips.Event.Donation`, and `Tips.Parameter<TValue>` types are generic over Swift-defined info types and are primarily reachable from a Swift companion that declares the concrete event/parameter; the surrounding value types above bind cleanly in C#.

## Displaying tips (UIKit)

TipKit's SwiftUI views (`TipView`, `PopoverTipView`) are **not** usable from C# (see [Known limitations](#known-limitations)). The supported presentation path is UIKit, via `TipUIPopoverViewController` — a `UIViewController` you present from a source view/bar item.

```csharp
using TipKit;
using UIKit;

// `tip` is an ITip from your Swift companion target.
ITip tip = GetMyTipFromSwift();

var popover = new TipUIPopoverViewController(
    tip,
    sourceItem,                 // any UIPopoverPresentationControllerSourceItem (e.g. a UIView / UIBarButtonItem)
    action =>                   // Action<Tips.Action> — invoked when the user taps a tip action
    {
        // handle the tapped action (see "Tip actions")
        action.Handler();
    });

PresentViewController(popover, animated: true, completionHandler: null);
```

The other UIKit types are also bound for collection-view-based tip layouts:

| Type | Base | |
|---|---|---|
| `TipUIPopoverViewController` | `UIKit.UIViewController` | present a tip as a popover; ctor takes `(ITip tip, object sourceItem, Action<Tips.Action> actionHandler)` |
| `TipUIView` | `UIKit.UIView` | inline tip view; obtain via `TipUIView.FromTipKit_AnyTip(AnyTip)` |
| `TipUICollectionViewCell` | `UIKit.UICollectionViewCell` | `new TipUICollectionViewCell(CGRect frame)` |
| `TipUICollectionReusableView` | `UIKit.UICollectionReusableView` | reusable supplementary view |

## Tip actions

A tip can declare action buttons. When the user taps one, your presentation handler receives a `Tips.Action`.

```csharp
// Inside the actionHandler passed to TipUIPopoverViewController:
action =>
{
    string id = action.Id;        // the action's identifier
    int? index = action.Index;    // its position, if ordered
    action.Handler();             // invoke the action's handler
}
```

`Tips.Action` members: `Id` (`string`), `Index` (`int?`), `Handler` (`System.Action`). You can also construct one directly:

```csharp
var custom = new Tips.Action(
    id: "learn-more",
    handler: () => OpenHelp(),
    label: () => /* Swift.SwiftUI.Text */);
```

## Errors

Throwing TipKit calls (notably `Tips.Configure`) surface a `TipKitError`. It exposes payload-less static singletons:

| Singleton | When |
|---|---|
| `TipKitError.TipsDatastoreAlreadyConfigured` | `Configure` called more than once |
| `TipKitError.InvalidPredicateValueType` | a rule predicate used an unsupported value type |
| `TipKitError.MissingGroupContainerEntitlements` | `DatastoreLocation.GroupContainer(...)` without the App Group entitlement |

```csharp
try
{
    Tips.Configure();
}
catch (Exception ex)
{
    // Compare against the singletons, or just log — Configure throwing on a
    // second call is expected and usually safe to ignore.
}
```

`TipKitError` supports value equality (`==`, `Equals`), but unlike `Tips.Status` it has no `CaseTag` discriminator (no `.Tag`, no `TryGet*` accessors) — its cases are exposed only via three static singleton properties (`InvalidPredicateValueType`, `MissingGroupContainerEntitlements`, `TipsDatastoreAlreadyConfigured`).

`TipGroup` (root-level) bundles multiple tips with a presentation priority: `TipGroup.Priority` (`enum : int`) is `FirstAvailable = 0` or `Ordered = 1`.

## Known limitations

- **You cannot define a `Tip` in pure C#.** Conforming to the `Tip` protocol (and its synthesized rule/parameter machinery) requires Swift. From C# you get `ITip` (interface, with throwing defaults) and `AnyTip` (read-only, no public ctor). Author tips in a small Swift companion target and drive everything else from C#.
- **The `@Rule` / `Tips.Rule(...) { ... }` result-builder DSL is unreachable from C#.** Those entry points are `@_alwaysEmitIntoClient` in the Swift standard library — they're inlined into each Swift caller and export no stable ABI symbol, so there is no call target to bind. Donation-tracking, parameter rules, and event-count rules built through the DSL must be declared in Swift. (You can publish concrete `Tips.Rule` values as `public static let` from your Swift companion; ordinary stored-property symbols bind cleanly.) `Tips.Rule`, `Tips.RuleBuilder`, `Tips.ActionBuilder`, `Tips.OptionsBuilder`, and `Tips.GroupBuilder` are present as types but their builder methods are part of this DSL surface.
- **SwiftUI views are not bound.** `TipView` and `PopoverTipView` are detected only as SwiftUI bridge templates that require manual completion — they are *not* generated as usable C# types. Use the UIKit path (`TipUIPopoverViewController`, `TipUIView`) instead.
- **`ITip.Invalidate(reason:)`, `ITip.Status`, and `ITip.ShouldDisplay`** throw `NotSupportedException` when called through the interface — they are Swift protocol-extension defaults. Access status / invalidation through your concrete Swift tip type.

What *does* work from C#, fully: `Tips.Configure` / `ResetDatastore` / `ShowAllTipsForTesting` / `HideAllTipsForTesting`, the `Tips.Status` enum and its singletons, `Tips.InvalidationReason`, all the configuration presets, the display-frequency option types, `Tips.DonationTimeRange` / `Tips.DonationLimit` / `Tips.ParameterOption`, `Tips.Action`, the UIKit presentation types, `TipKitError`, and `TipGroup.Priority`.

## Memory & threading

Generated types implement `ISwiftObject` / `IDisposable`. For genuinely disposable types, `using var` is the recommended pattern for deterministic cleanup (the finalizer also cleans up short-lived locals). The one exception is the configuration option value-structs — `Tips.DonationLimit`, `Tips.IgnoresDisplayFrequency`, `Tips.MaxDisplayCount`, and `Tips.MaxDisplayDuration` — which implement `IDisposable` unconditionally with no cached-singleton guard and must **not** be disposed; construct them with a plain `var` and pass them directly to `Tips.Configure(...)`.

```csharp
var limit = new Tips.DonationLimit(100);          // option value-struct — do NOT dispose
using var anyTip = AnyTip.FromTipKit_AnyTip(/* … */);
```

- **Do not dispose static singletons.** `Tips.Status.Pending`, `Tips.DonationTimeRange.Week`, `Tips.ConfigurationOption.DisplayFrequency.Daily`, the `TipKitError` singletons, etc. are cached — treat them as shared values.
- **Configure on the main thread at launch.** `Tips.Configure` initializes the persistent datastore; call it once during app startup before presenting any UI. The UIKit presentation types are `UIViewController`/`UIView` subclasses and must be used on the main thread like any UIKit object.
- **`Sendable` value types.** Several TipKit value types are marked `[SwiftSendable]` and may be shared across threads without external synchronization.

## Reference links

- [Apple — TipKit](https://developer.apple.com/documentation/tipkit) — upstream documentation and full API semantics
- [Apple — Displaying tips in your app](https://developer.apple.com/documentation/tipkit/displaying-tips-in-your-app)
