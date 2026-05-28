# FamilyControls for .NET — Usage Guide

`SwiftBindings.Apple.FamilyControls` exposes Apple's [FamilyControls](https://developer.apple.com/documentation/familycontrols) framework to C# through .NET 10's native Swift interop — direct Swift calls, not Objective-C proxy wrappers. FamilyControls is the authorization layer for Screen Time apps (parental controls, focus/wellbeing apps): you request authorization, let the user pick which apps/categories/websites to manage, and hand the resulting selection to ManagedSettings / DeviceActivity to enforce restrictions.

The model trips people up, so read [The token model](#the-token-model) before anything else: FamilyControls deliberately gives you **opaque tokens**, not app names or bundle IDs. You can store and apply a selection, but you cannot inspect what's inside it. This guide documents exactly what the bindings emit and what you can and can't do with it.

## Contents

- [Requirements & install](#requirements--install)
- [Naming conventions](#naming-conventions)
- [The token model](#the-token-model)
- [Quick start: request authorization](#quick-start-request-authorization)
- [Authorization status](#authorization-status)
- [The activity selection](#the-activity-selection)
- [Persisting a selection](#persisting-a-selection)
- [Errors](#errors)
- [The picker (SwiftUI)](#the-picker-swiftui)
- [Memory & threading](#memory--threading)
- [Reference links](#reference-links)

## Requirements & install

- .NET 10.0+
- iOS 26.2+
- macOS host for development
- The **Family Controls** capability enabled on your app (request the entitlement from Apple; it is not granted automatically)

```
dotnet add package SwiftBindings.Apple.FamilyControls
```

```csharp
using FamilyControls;
```

FamilyControls only does real work on a physical device with the Family Controls entitlement and (for child accounts) a Screen Time / Family Sharing setup. In the simulator the types load and authorization can be queried, but enforcement is a no-op.

## Naming conventions

| Swift | C# | Rule |
|---|---|---|
| `func requestAuthorization(for:) async throws` | `RequestAuthorizationAsync(member, ct)` | `async` method gains `Async` suffix; first label dropped |
| `AuthorizationCenter.shared` | `AuthorizationCenter.Shared` (static property) | singletons are PascalCase static properties |
| `enum AuthorizationStatus { case approved }` | `AuthorizationStatus` class with `.Tag` + `CaseTag.Approved` static singletons | a Swift enum projects to a wrapper class with a `Tag` and static case singletons |
| `enum FamilyControlsError` | plain C# `enum` (`int`-backed) | payload-free error enum |
| `error.errorDescription` | `error.GetErrorDescription()` (extension method) | enum-attached members become extension methods |
| `selection.applicationTokens` | `selection.ApplicationTokens` | properties are PascalCase |

## The token model

A `FamilyActivitySelection` is the central value: it's the set of apps, categories, and websites the user chose to manage. Crucially, those choices are stored as **opaque tokens**, by design — Apple does not let your app learn which apps a child selected (that would defeat the privacy model). What the bindings emit reflects this exactly:

| Property | C# type | What you get |
|---|---|---|
| `ApplicationTokens` | `Swift.SwiftSet<IntPtr>` | opaque application tokens — count them, store them, pass them on; you **cannot** read an app name or bundle ID from them |
| `CategoryTokens` | `Swift.SwiftSet<IntPtr>` | opaque category tokens |
| `WebDomainTokens` | `Swift.SwiftSet<IntPtr>` | opaque web-domain tokens |
| `Applications` | `IReadOnlySet<Swift.ManagedSettings.Application>` | the selected applications as ManagedSettings values |
| `Categories` | `IReadOnlySet<Swift.ManagedSettings.ActivityCategory>` | the selected categories |
| `WebDomains` | `IReadOnlySet<Swift.ManagedSettings.WebDomain>` | the selected web domains |
| `IncludeEntireCategory` | `bool` | whether picking a category implies all its apps |

The tokens (`ApplicationTokens`, etc.) are settable (`get`/`set`); the materialized `Applications` / `Categories` / `WebDomains` sets are read-only. The practical workflow is: get a selection from the picker → store it (as JSON) → later, load it and hand its tokens to ManagedSettings to apply shields/restrictions. You never decode the tokens yourself.

## Quick start: request authorization

Authorization is the gate for everything else. Request it once, then check the status before applying any restriction.

```csharp
using FamilyControls;

var center = AuthorizationCenter.Shared;

try
{
    // FamilyControlsMember: Individual (this device's user) or Child (a child in Family Sharing)
    await center.RequestAuthorizationAsync(FamilyControlsMember.Individual);

    var status = center.AuthorizationStatus;        // AuthorizationStatus (wrapper)
    if (status.Tag == AuthorizationStatus.CaseTag.Approved)
    {
        // You may now read selections and apply Screen Time restrictions.
    }
}
catch (Swift.Runtime.SwiftException ex)
{
    // e.g. user declined, restricted, or the entitlement is missing
    Console.WriteLine(ex.Message);
}
```

`RequestAuthorizationAsync(FamilyControlsMember member, CancellationToken ct = default)` is `throws` in Swift, so failures arrive as a `Swift.Runtime.SwiftException` (see [Errors](#errors)).

> There is also a closure-based `RequestAuthorization(Action<…>)` overload and a `RevokeAuthorization(Action<…>)` overload, but the `Async` form above is the idiomatic one for C#.

`FamilyControlsMember` is a plain `enum` (`long`-backed): `Child = 0`, `Individual = 1`. Its `GetDescription()` extension method returns a human-readable label.

## Authorization status

`AuthorizationCenter.Shared.AuthorizationStatus` returns an `AuthorizationStatus` — a wrapper over the Swift enum. Discriminate with `.Tag` against the `CaseTag` enum, or compare against the static singletons:

```csharp
public enum CaseTag : uint { NotDetermined = 0, Denied = 1, Approved = 2 }
```

```csharp
var status = AuthorizationCenter.Shared.AuthorizationStatus;

switch (status.Tag)
{
    case AuthorizationStatus.CaseTag.Approved:     /* good to go */     break;
    case AuthorizationStatus.CaseTag.Denied:       /* user said no */   break;
    case AuthorizationStatus.CaseTag.NotDetermined:/* not asked yet */  break;
}

// Or compare against the cached singletons:
if (status == AuthorizationStatus.Approved) { /* … */ }
```

`AuthorizationStatus` static singletons: `NotDetermined`, `Denied`, `Approved`. Other members: `Description` (`string`), `RawValue` (`int`), `Tag` (`CaseTag`). It implements value equality (`==`, `!=`, `Equals`).

## The activity selection

`FamilyActivitySelection` holds the user's choices. Construct an empty one, or one that treats category picks as "the entire category":

```csharp
var selection = new FamilyActivitySelection();                          // empty
var withCategories = new FamilyActivitySelection(includeEntireCategory: true);
```

Read the token sets to drive enforcement or to show a count in your UI:

```csharp
int appCount = selection.ApplicationTokens.Count;
int categoryCount = selection.CategoryTokens.Count;
int domainCount = selection.WebDomainTokens.Count;
Console.WriteLine($"User chose {appCount} apps, {categoryCount} categories, {domainCount} sites.");
```

You cannot get an app name out of a token — that's the privacy contract. To *display* the selection to the user, use the picker UI (which renders names on the system's behalf) or the SwiftUI label/icon view bridges; to *enforce* it, pass the tokens to ManagedSettings.

`FamilyActivitySelection` implements value equality, so two selections with the same contents compare equal (`==` / `Equals`).

## Persisting a selection

Because you can't inspect tokens, the way to persist a selection across launches is to serialize the whole value. The binding exposes JSON round-tripping (Foundation `Codable` under the hood):

```csharp
// Save
byte[] json = selection.EncodeToJson();
File.WriteAllBytes(path, json);

// Restore
byte[] bytes = File.ReadAllBytes(path);
FamilyActivitySelection restored = FamilyActivitySelection.DecodeFromJson(bytes);
```

`EncodeToJson()` throws `InvalidOperationException` if the Swift encoder rejects the value; `DecodeFromJson(byte[])` throws `ArgumentNullException` for null input and `InvalidOperationException` if decoding fails.

> The synthesized Swift `Codable` `encode(to:)` / `init(from:)` members themselves are intentionally pruned from the binding (their `Encoder`/`Decoder` are unresolvable existential protocols). Use the `EncodeToJson` / `DecodeFromJson` helpers instead — they are the supported persistence path.

## Errors

`RequestAuthorizationAsync` surfaces failures as **`Swift.Runtime.SwiftException`**. The error codes are also available as a plain `enum`:

```csharp
public enum FamilyControlsError : int
{
    Restricted = 0,
    Unavailable = 1,
    InvalidAccountType = 2,
    InvalidArgument = 3,
    AuthorizationConflict = 4,
    AuthorizationCanceled = 5,
    NetworkError = 6,
    AuthenticationMethodUnavailable = 7,   // iOS 16+
}
```

Each value carries localized text via an extension method:

```csharp
string? text = FamilyControlsError.Restricted.GetErrorDescription();   // may be null
```

> `GetErrorDescription()` returns `string?` and may be `null` on some OS versions — null-check before display.

## The picker (SwiftUI)

Apple's `FamilyActivityPicker` — the system UI a user taps through to choose apps/categories — is a **SwiftUI `View`**. SwiftUI views are not bound as ordinary callable C# types; the generator emits a SwiftUI bridge instead (`FamilyControls.SwiftUIBridge`). What the bindings expose for embedding in a hosted SwiftUI surface are the session bridge types `FamilyActivityTitleViewSession` and `FamilyActivityIconViewSession` (each with a `Create(Action? onAppear, Action? onDisappear)` factory and `Dispose()`), used to render the title/icon for a selected activity.

> **Limitation:** there is no direct C# call that pops the picker and hands you back a `FamilyActivitySelection`. Presenting the picker requires the SwiftUI hosting path. From plain C# you can still construct, read, persist, compare, and apply a `FamilyActivitySelection` you obtained elsewhere — you just can't drive the picker UI imperatively.

## Memory & threading

Generated types implement `ISwiftObject` / `IDisposable`. For short-lived locals the finalizer cleans up, but `using var` is the recommended pattern for deterministic cleanup — `Dispose` is safe on every generated type and double-Dispose is a no-op.

```csharp
using var selection = new FamilyActivitySelection();
```

- **`AuthorizationStatus` singletons** (`Approved`, etc.) are cached and must not be disposed — disposing a cached singleton is a no-op by design, but don't wrap them in `using`.
- **Authorization is async and may prompt.** `RequestAuthorizationAsync` shows system UI on a device; await it off the UI thread is fine, but marshal back before touching your UI. Pass a `CancellationToken` to cancel.
- **`AuthorizationCenter.Shared`** is a process-wide singleton; fetch it where you need it rather than caching a field.

## Reference links

- [Apple — FamilyControls framework](https://developer.apple.com/documentation/familycontrols)
- [Apple — ManagedSettings](https://developer.apple.com/documentation/managedsettings) (apply the selection's tokens)
- [Apple — DeviceActivity](https://developer.apple.com/documentation/deviceactivity) (monitor usage)
