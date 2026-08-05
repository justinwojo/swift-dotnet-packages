# Facebook (Meta) iOS SDK for .NET — Usage Guide

The `SwiftBindings.Facebook.*` packages expose Meta's [Facebook SDK for iOS](https://developers.facebook.com/docs/ios/) (upstream **v18.1.0**) to C# — SDK initialization, App Events, Facebook Login, Graph API requests, and Sharing. The Facebook SDK is a *mixed* codebase: some modules are pure Objective-C, some are pure Swift, and `FBSDKCoreKit` is both. That mix is the single most important thing to understand before you translate a snippet from Meta's docs, because **it decides what a type is called in C#**. This guide teaches that translation and walks the flows most apps need.

## Contents

- [Package map](#package-map)
- [Requirements & install](#requirements--install)
- [The two binding shapes (and how to name things)](#the-two-binding-shapes-and-how-to-name-things)
- [Setup: Info.plist + app delegate wiring](#setup-infoplist--app-delegate-wiring)
- [App Events](#app-events)
- [Facebook Login](#facebook-login)
- [Access token and profile](#access-token-and-profile)
- [Graph API requests](#graph-api-requests)
- [Sharing](#sharing)
- [FBSDKCoreKit_Basics utilities](#fbsdkcorekit_basics-utilities)
- [FBAEMKit (Aggregated Event Measurement)](#fbaemkit-aggregated-event-measurement)
- [Object lifetime](#object-lifetime)
- [Known limitations](#known-limitations)
- [Reference links](#reference-links)

## Package map

Five packages, mirroring the five upstream component SDKs. Install what you use — dependencies pull the rest in.

| Package | Upstream module | Install it when… |
|---|---|---|
| `SwiftBindings.Facebook.Core` | FBSDKCoreKit | Always. SDK init, App Events, `AccessToken`, `Settings`, `Profile`, `FBSDKGraphRequest`. |
| `SwiftBindings.Facebook.Login` | FBSDKLoginKit | You need Facebook Login (`LoginManager`, `FBLoginButton`). Pulls in Core. |
| `SwiftBindings.Facebook.Share` | FBSDKShareKit | You need share dialogs (`ShareDialog`, `ShareLinkContent`). Pulls in Core. |
| `SwiftBindings.Facebook.AEM` | FBAEMKit | Rarely direct — Aggregated Event Measurement. Pulled in by Core. |
| `SwiftBindings.Facebook.CoreBasics` | FBSDKCoreKit_Basics | Rarely direct — the pure-ObjC kernel (Base64, JSON/type coercion, crash handler). Pulled in by everything. |

Dependency direction is `Login`/`Share` → `Core` → `AEM` → `CoreBasics`. A typical app installs `Login` and/or `Share` and gets Core transitively:

```
dotnet add package SwiftBindings.Facebook.Login
dotnet add package SwiftBindings.Facebook.Share
```

All five ship at the same version as the upstream SDK (`18.1.0`) and must be kept in lockstep — mixing versions across the five packages is not supported.

## Requirements & install

- .NET 10.0+
- **Target framework: `net10.0-ios`.** iOS only — there is no macOS, Mac Catalyst, or tvOS binding, because the upstream xcframeworks the packages are built from target iOS (and Mac Catalyst, which is not bound here).
- `<SupportedOSPlatformVersion>15.0</SupportedOSPlatformVersion>` or higher (the upstream minimum).
- macOS host for development.
- A Facebook app ID and client token from the [Meta App Dashboard](https://developers.facebook.com/apps).

```csharp
using FBSDKCoreKit;   // ApplicationDelegate, Settings, Profile, AccessToken, FBSDKAppEvents, FBSDKGraphRequest
using FBSDKLoginKit;  // LoginManager, LoginConfiguration, FBLoginButton
using FBSDKShareKit;  // ShareDialog, ShareLinkContent, SharePhoto, Hashtag
```

Namespaces are the **native module names**, not `Swift.<Module>` — `FBSDKCoreKit`, `FBSDKLoginKit`, `FBSDKShareKit`, `FBAEMKit`, `FBSDKCoreKit_Basics`. You also need `using Foundation;` (for `NSDictionary`, `NSUrl`, `NSString`) and, for anything that takes a Swift geometry value, `using Swift;` (`Swift.CGRect`).

## The two binding shapes (and how to name things)

The generator produced **two different kinds of C#** inside these packages, and which one a type came from determines its name:

| Origin | Binding shape | C# name | Examples |
|---|---|---|---|
| Objective-C class/protocol that Swift imports under a shorter name | ObjC (bgen) binding | **takes the Swift-import name** | `AccessToken`, `AuthenticationToken`, `Location`, `UserAgeRange`, `BridgeAPIResponse` |
| Objective-C class/protocol with no such rename | ObjC (bgen) binding | **keeps the full `FBSDK…` ObjC name** | `FBSDKAppEvents`, `FBSDKGraphRequest`, `FBSDKGraphRequestConnection`, `FBSDKUtility`, `FBSDKAppLinkUtility`, `FBSDKSettings`-family protocols |
| Swift class/struct/enum | Swift interop binding | **keeps the short Swift name** | `ApplicationDelegate`, `Settings`, `Profile`, `Permission`, `LoginManager`, `LoginConfiguration`, `FBLoginButton`, `ShareDialog`, `ShareLinkContent`, `Hashtag`, `AEMReporter` |

This is the rule that costs the most time if you don't know it, and **it changed in SDK 0.19.0**. Meta's Swift docs write `AccessToken.current` and `AppEvents.shared`. `AccessToken` is an ObjC class (`FBSDKAccessToken`) that Swift imports as `AccessToken`, and the binding now follows that import — so it is `AccessToken.CurrentAccessToken` in C#, where earlier packages spelled it `FBSDKAccessToken.CurrentAccessToken`. `FBSDKAppEvents` carries no such Swift rename, so it keeps its ObjC name and stays `FBSDKAppEvents.Shared`. And `Settings.shared` really is a Swift class, so it has always been `Settings.Shared`.

Only the *managed* name moves — the raw ObjC name is preserved in the binding's `[BaseType(…, Name = "FBSDKAccessToken")]`, so native registration, superclass resolution and anything that reaches these objects through the ObjC runtime are unaffected. The FBSDKCoreKit renames a consumer actually touches are:

| Old C# name | New C# name |
|---|---|
| `FBSDKAccessToken` | `AccessToken` |
| `FBSDKAuthenticationToken` | `AuthenticationToken` |
| `FBSDKLocation` | `Location` |
| `FBSDKUserAgeRange` | `UserAgeRange` |
| `FBSDKBridgeAPIResponse` | `BridgeAPIResponse` |
| `FBSDKPaymentProductRequestor` | `PaymentProductRequestor` |
| `FBSDKAdvertisingTrackingStatus` (enum) | `AdvertisingTrackingStatus` |
| `FBSDKAppLinkNavigationType` (enum) | `AppLinkNavigationType` |
| `FBSDKFeature` (enum) | `SDKFeature` |
| `FBSDKWebDialogView` | `FBWebDialogView` |
| `FBSDKShareBridgeOptions` (enum, FBSDKShareKit) | `ShareBridgeOptions` |

Four more — `FBSDKAppEventsState`, `FBSDKContainerViewController`, `FBSDKDialogConfiguration`, `FBSDKLogger` — moved to `_`-prefixed names (`_AppEventsState`, …) because Swift imports them as internal; they are not API (see [Known limitations](#known-limitations)). `FBSDKCoreKit_Basics`, `FBSDKLoginKit` and `FBAEMKit` have **zero** type renames: CoreBasics' ObjC names carry no Swift rename, and LoginKit/AEMKit emit no ObjC lane at all (their `@objc` classes are Swift declarations the Swift lane already binds). Two `FBSDKWebDialogViewDelegate` methods also renamed — `WebDialogViewDidCancel` → `DidCancel`, `WebDialogViewDidFinishLoad` → `DidFinishLoad` — but that protocol is internal surface.

Per-module: `FBSDKCoreKit_Basics` is **entirely** ObjC-shaped; `FBSDKLoginKit` and `FBAEMKit` are **entirely** Swift-shaped; `FBSDKCoreKit` is **mixed**; `FBSDKShareKit` is Swift-shaped in its callable surface.

Other conventions worth internalizing:

| Swift / ObjC | C# | Rule |
|---|---|---|
| `AccessToken.current` | `AccessToken.CurrentAccessToken` | ObjC selector name wins for the *member* (`currentAccessToken`), even where the *type* takes its Swift-import name |
| `AppEvents.shared.logEvent(_:)` | `FBSDKAppEvents.Shared.LogEvent(…)` | selectors PascalCase; the label-free first argument disappears |
| `LoginManager.logIn(permissions:from:handler:)` | `LoginManager.LogIn(permissions, viewController, handler)` | Swift argument labels are dropped; order preserved |
| `init?(permissions:tracking:)` (failable) | `LoginConfiguration.TryCreate(…, out var config)` | failable Swift initializers become `TryCreate` + `out` |
| `enum LoginResult { case success(…), cancelled, failed(Error) }` | class with `.Tag` (`LoginResult.CaseTag`) + `TryGetSuccess(out …)` | Swift enums *with payloads* project to a class + `CaseTag` + `TryGet…` |
| `enum LoginTracking { case enabled, limited }` | `enum LoginTracking : ulong` | payload-free Swift enums become ordinary C# enums |
| `enum ShareDialog.Mode` | `ShareDialog.ModeKind` | nested Swift type names that clash with a member get a `Kind` suffix |
| `FBSDKProductAvailabilityInStock` (ObjC enum case) | `FBSDKProductAvailability.InStock` | ObjC enum cases drop the prefix **only when it is exactly the native enum name** |
| `FBSDKErrorInvalidArgument` (ObjC enum case) | `FBSDKCoreError.FBSDKErrorInvalidArgument` | …and keep the full case name when it isn't — here the cases are `FBSDKError…` but the enum is `FBSDKCoreError` |
| `[String: Any]` parameters (ObjC surface) | `Foundation.NSDictionary` | ObjC bindings take `NSDictionary`; build with `NSDictionary.FromObjectsAndKeys` |
| `Set<String>` (Swift surface) | `IReadOnlySet<string>` | Swift collections project to the matching .NET read-only interface |
| `completion: (Result?, Error?) -> Void` | `Action<T?, Swift.Foundation.AnyError?>` | Swift closures become `Action<…>`; Swift `Error` is `Swift.Foundation.AnyError` |
| ObjC completion `(id, NSError *)` | `Action<…, NSObject, NSError>` | ObjC blocks keep `NSError` |

Three FBSDKCoreKit enums land on the second of those two rows, so their members read redundantly: `FBSDKCoreError` (cases `FBSDKError…`), `FBSDKGraphRequestFlags` (cases `FBSDKGraphRequestFlag…` — singular, against a plural type name) and the internal `AdvertisingTrackingStatus` (cases `FBSDKAdvertisingTracking…`). Every other enum in the package strips cleanly. Let IntelliSense complete these rather than guessing the short form; this is longstanding `bgen` behavior driven by Meta's own header naming, not something 0.19.0 changed.

## Setup: Info.plist + app delegate wiring

This is where .NET consumers need the most translation help. There is no Xcode project to edit — you edit your app's `Info.plist` and your `UIApplicationDelegate` subclass directly.

### 1. Info.plist

Add these to your app's `Info.plist` (the same keys Meta documents in [Getting Started](https://developers.facebook.com/docs/ios/getting-started); substitute your app ID, client token, and display name):

```xml
<key>CFBundleURLTypes</key>
<array>
  <dict>
    <key>CFBundleURLSchemes</key>
    <array>
      <string>fb1234567890</string>   <!-- literally "fb" + your app ID -->
    </array>
  </dict>
</array>
<key>FacebookAppID</key>
<string>1234567890</string>
<key>FacebookClientToken</key>
<string>your-client-token</string>
<key>FacebookDisplayName</key>
<string>Your App Name</string>

<!-- Needed for native Login / Share app-switching dialogs -->
<key>LSApplicationQueriesSchemes</key>
<array>
  <string>fbapi</string>
  <string>fb-messenger-share-api</string>
</array>
```

If you would rather not hardcode the IDs in the plist, `Settings.Shared` exposes the same values as writable properties — set them **before** initializing the SDK:

```csharp
Settings.Shared.AppID = "1234567890";
Settings.Shared.ClientToken = "your-client-token";
Settings.Shared.DisplayName = "Your App Name";
```

`FacebookAutoLogAppEventsEnabled` and `FacebookAdvertiserIDCollectionEnabled` are honoured from the plist as usual; the C# equivalents are `Settings.Shared.IsAutoLogAppEventsEnabled` and `Settings.Shared.IsAdvertiserIDCollectionEnabled`.

### 2. App delegate

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using FBSDKCoreKit;
using Foundation;
using UIKit;

[Register("AppDelegate")]
public sealed class AppDelegate : UIApplicationDelegate
{
    public override UIWindow? Window { get; set; }

    public override bool FinishedLaunching(UIApplication application, NSDictionary launchOptions)
    {
        // Swift: ApplicationDelegate.shared.application(app, didFinishLaunchingWithOptions: launchOptions)
        ApplicationDelegate.Shared.Application(application, AsOptions(launchOptions));

        // … your own window setup …
        return true;
    }

    // Swift: application(_:open:options:) — required for login / share redirects back into the app
    public override bool OpenUrl(UIApplication app, NSUrl url, UIApplicationOpenUrlOptions options)
        => ApplicationDelegate.Shared.Application(app, url, options.SourceApplication, options.Annotation);

    // Swift: application(_:continue:restorationHandler:) — App Links / deferred deep links
    public override bool ContinueUserActivity(
        UIApplication application,
        NSUserActivity userActivity,
        UIApplicationRestorationHandler completionHandler)
        => ApplicationDelegate.Shared.Application(application, userActivity);

    // The Swift dictionaries project as IReadOnlyDictionary<NSString, object>;
    // .NET-for-iOS hands you an NSDictionary, so cross the two here once.
    private static Dictionary<NSString, object>? AsOptions(NSDictionary? d)
        => d?.Keys.ToDictionary(k => (NSString)k, k => (object)d[k]);
}
```

Three things to note:

- **The `launchOptions` and `openURL:options:` overloads are bound.** Both take the Swift dictionary projected as `IReadOnlyDictionary<NSString, object>` (`IDictionary<…>` for the open-URL one) — *not* as `NSDictionary`. The two are unrelated .NET types, so the small `AsOptions` bridge above is what crosses them; there is no implicit conversion.
- **`launchOptions` is optional.** `Application(application)` also compiles — the parameter defaults to `null` — and the SDK does not require launch options for normal initialization. Pass them if you have them.
- **Prefer the `sourceApplication`/`annotation` `OpenUrl` shape** as shown above. `UIApplicationOpenUrlOptions` is a strongly-typed wrapper rather than a plain dictionary, so forwarding its two properties is less work than projecting it. It is the same native method the Swift `options:` variant funnels into. If you already hold a raw options `NSDictionary`, `ApplicationDelegate.Shared.Application(app, url, AsOptions(dict)!)` is the equivalent call.

If your app uses a `UIWindowSceneDelegate`, forward `scene(_:openURLContexts:)` to the same `ApplicationDelegate.Shared.Application(UIApplication.SharedApplication, url, sourceApplication: null, annotation: null)` call, as Meta's docs describe for `SceneDelegate.swift`.

## App Events

`AppEvents` in Meta's docs is the ObjC class `FBSDKAppEvents` — a singleton reached through `.Shared`.

```csharp
using FBSDKCoreKit;
using Foundation;

// Session tracking. Auto-logged unless you set FacebookAutoLogAppEventsEnabled=false,
// in which case call it from applicationDidBecomeActive.
FBSDKAppEvents.Shared.ActivateApp();

// Simple event, and one with a value to sum. The standard event names are bound
// constants — NSString converts to string implicitly, so they drop straight in.
FBSDKAppEvents.Shared.LogEvent(FBSDKCoreKitConstants.FBSDKAppEventNameAddedToCart);
FBSDKAppEvents.Shared.LogEvent(FBSDKCoreKitConstants.FBSDKAppEventNameAddedToCart, 29.99);

// Event with parameters — ObjC surface, so parameters are an NSDictionary
var parameters = NSDictionary.FromObjectsAndKeys(
    objects: new NSObject[] { new NSString("product"), new NSNumber(2) },
    keys:    new NSObject[]
    {
        FBSDKCoreKitConstants.FBSDKAppEventParameterNameContentType,
        FBSDKCoreKitConstants.FBSDKAppEventParameterNameNumItems,
    });

FBSDKAppEvents.Shared.LogEvent(FBSDKCoreKitConstants.FBSDKAppEventNameAddedToCart, 29.99, parameters);

// Purchases
FBSDKAppEvents.Shared.LogPurchase(29.99, "USD", parameters);

// Flush control
FBSDKAppEvents.Shared.FlushBehavior = FBSDKAppEventsFlushBehavior.ExplicitOnly;
FBSDKAppEvents.Shared.Flush();

// User identity for advanced matching
FBSDKAppEvents.Shared.UserID = "internal-user-42";
FBSDKAppEvents.Shared.SetUserEmail("a@example.com", "Ada", "Lovelace",
    phone: null, dateOfBirth: null, gender: null, city: null, state: null, zip: null, country: null);
FBSDKAppEvents.Shared.ClearUserData();
```

> **Where the event-name constants live.** Swift's `AppEvents.Name.addedToCart` / `AppEvents.ParameterName.contentType` are `extern NSString` constants on the ObjC side. They are bound as static properties on **`FBSDKCoreKitConstants`**, a generated `[Static]` container in the `FBSDKCoreKit` namespace holding all 171 of the module's extern constants — every `FBSDKAppEventName…`, `FBSDKAppEventParameterName…`, `FBSDKAppEventParameterValue…`, plus the `FBSDKLoggingBehavior…` and error-domain/key strings. The property names keep the full `FBSDK…` C name; unlike some other modules the generator does **not** peel a common prefix here, because one constant (`DefaultKeychainServicePrefix`) does not carry it and the de-prefixing rule is all-or-nothing per module.
>
> The type is `NSString`, which converts to `string` implicitly, so the constants pass straight into `LogEvent(string, …)`. Raw strings from Meta's [standard-events reference](https://developers.facebook.com/docs/app-events/reference) still work identically — the strings *are* the wire format — but the constants give you compile-time checking.

## Facebook Login

> **Read this before you copy a snippet from Meta's docs.** The overload everyone reaches for first — `LoginManager.logIn(permissions:from:handler:)`, i.e. `LogIn(IEnumerable<string>, UIViewController, Action<LoginManagerLoginResult, AnyError>)` — is emitted as an **`SB0001` non-callable stub** (no `@_cdecl` wrapper or native thunk could be generated for it). It is present in the API surface and compiles with a warning, but do not call it. The supported path is **always** `LoginConfiguration` + `LogIn(viewController, configuration, completion)`, shown below. That path covers everything the permissions overload did, including classic (non-Limited) login.

```csharp
using FBSDKCoreKit;
using FBSDKLoginKit;
using UIKit;

var manager = new LoginManager();

// Swift's failable init?(permissions:tracking:) → TryCreate + out
if (!LoginConfiguration.TryCreate(
        new[] { "public_profile", "email" },
        LoginTracking.Enabled,          // or LoginTracking.Limited for Limited Login
        out var configuration))
{
    return;   // invalid permission set
}

manager.LogIn(presentingViewController, configuration, result =>
{
    using (result)
    {
        switch (result.Tag)
        {
            case LoginResult.CaseTag.Success:
                result.TryGetSuccess(out var granted, out var declined, out var token);
                Console.WriteLine($"{granted.Count} granted, {declined.Count} declined");
                Console.WriteLine($"userID={token?.UserID}");
                break;

            case LoginResult.CaseTag.Cancelled:
                Console.WriteLine("cancelled");
                break;

            case LoginResult.CaseTag.Failed:
                result.TryGetFailed(out var failure);
                Console.WriteLine($"failed: {failure}");
                break;
        }
    }
});

manager.LogOut();
```

The same call has a generated `Task` form:

```csharp
using var result = await manager.LogInAsync(presentingViewController, configuration);
```

`granted`/`declined` are `IReadOnlySet<FBSDKCoreKit.Permission>` — the Swift `Permission` enum, which exposes the well-known permissions as statics (`Permission.PublicProfile`, `Permission.Email`, `Permission.UserFriends`, …) plus `Permission.Custom("some_scope")` for anything else.

`TryCreate` has a large family of overloads mirroring Swift's initializers — add `nonce`, `messengerPageId`, `authType`, `AppSwitch`, or a `CodeVerifier` as needed, or drop `permissions` entirely (`TryCreate(LoginTracking.Limited, out var config)`).

Other members you are likely to want: `LoginTracking.Enabled` / `.Limited`, `DefaultAudience.Friends` / `.OnlyMe` / `.Everyone` (`new LoginManager(DefaultAudience.OnlyMe)` or `manager.DefaultAudience`), and `manager.ReauthorizeDataAccess(viewController, handler)`.

### The login button

```csharp
using Swift;      // Swift.CGRect

var button = new FBLoginButton(new CGRect(0, 0, 240, 44))
{
    Permissions = new[] { "public_profile", "email" },
    LoginTracking = LoginTracking.Enabled,
};
View.AddSubview(button);   // FBLoginButton derives from UIButton
```

## Access token and profile

```csharp
using FBSDKCoreKit;

// Swift docs say AccessToken.current — in C# the type name matches, the member does not.
AccessToken? token = AccessToken.CurrentAccessToken;
if (AccessToken.CurrentAccessTokenIsActive)
{
    // Also: AppID, ExpirationDate, RefreshDate, DataAccessExpirationDate,
    //       Expired, DeclinedPermissions, ExpiredPermissions
    Console.WriteLine($"{token!.UserID}: {token.TokenString}");
    Console.WriteLine(token.HasGranted("email"));
    foreach (var p in token.Permissions)      // NSSet<NSString>
        Console.WriteLine(p);
}

AccessToken.RefreshCurrentAccessTokenWithCompletion(
    (connection, result, error) => { /* … */ });
```

`Profile` is a Swift class and always had its short name; `AccessToken` is Objective-C (`FBSDKAccessToken`) and reaches the same name through the Swift-import rename described in [The two binding shapes](#the-two-binding-shapes-and-how-to-name-things). Both read the same in C# now:

```csharp
Profile.EnableUpdatesOnAccessTokenChange(true);
Console.WriteLine(Profile.Current?.Name);

Profile.LoadCurrentProfile((profile, error) =>
{
    Console.WriteLine(profile?.FirstName);
    Console.WriteLine(profile?.Location?.Name);   // Location — an ObjC type (FBSDKLocation)
    Console.WriteLine(profile?.AgeRange?.Min);    // UserAgeRange — an ObjC type (FBSDKUserAgeRange)
});
```

`Profile.Location` / `.Hometown` / `.AgeRange` returning `Location` / `UserAgeRange` is the mixed-module bridge working: a Swift class handing back Objective-C values. Those two are `FBSDKLocation` / `FBSDKUserAgeRange` natively — the raw names survive on `[BaseType(…, Name = "…")]` for the registrar, so nothing about the runtime changes.

## Graph API requests

`GraphRequest` is Objective-C, so it is `FBSDKGraphRequest` and takes `NSDictionary` parameters.

```csharp
using FBSDKCoreKit;
using Foundation;

var parameters = NSDictionary.FromObjectsAndKeys(
    objects: new NSObject[] { new NSString("id,name,email") },
    keys:    new NSObject[] { new NSString("fields") });

new FBSDKGraphRequest("me", parameters).StartWithCompletion((connection, result, error) =>
{
    if (error is not null) { Console.WriteLine(error.LocalizedDescription); return; }
    if (result is NSDictionary dict)
        Console.WriteLine(dict[(NSString)"name"]);
});
```

The request uses `AccessToken.CurrentAccessToken` unless you pass a token string. Other constructors take an explicit HTTP method (`new FBSDKGraphRequest("me/feed", parameters, "POST")`) or a token string + Graph API version. For batching, build an `FBSDKGraphRequestConnection`, `AddRequest(request, completion)` each call, then `Start()`.

## Sharing

```csharp
using FBSDKShareKit;
using Foundation;
using UIKit;

var content = new ShareLinkContent
{
    ContentURL = new NSUrl("https://developers.facebook.com"),
    Quote      = "Check this out",
    Hashtag    = new Hashtag("#dotnet"),
};

// Keep a field-level reference: the dialog and its content must outlive Show().
_dialog = new ShareDialog(presentingViewController, content, @delegate: null)
{
    Mode = ShareDialog.ModeKind.Automatic,
};

if (_dialog.CanShow)
    _dialog.Show();
```

Photos and video use the same dialog with a different content object:

```csharp
var photo = new SharePhoto(someUIImage, isUserGenerated: true) { Caption = "hello" };
var photoContent = new SharePhotoContent { Photos = new[] { photo } };
_dialog = new ShareDialog(presentingViewController, photoContent, null);
_dialog.Show();

var video = new ShareVideo(videoUrl, previewPhoto: photo);
var videoContent = new ShareVideoContent { Video = video };
```

To receive the result, implement `ISharingDelegate` — three members: `Sharer(ISharing, IDictionary<string, object> results)`, `Sharer(ISharing, Swift.Foundation.AnyError error)`, and `SharerDidCancel(ISharing)` — and pass it as the dialog's third constructor argument (or assign `dialog.Delegate`). A C#-authored delegate **does** get called back: the binding builds a `SharingDelegateProxy` for any conformer that is not already a Swift object.

`MessageDialog` (Messenger) mirrors `ShareDialog` minus the view controller: `new MessageDialog(content, delegate)`.

Two sharp edges here — see [Known limitations](#known-limitations):

- **The `ShareDialog.ShareContent` getter throws.** Set it, or pass content through the constructor; never read it back.
- **Errors raised by this package use the `FBSDKShareErrorDomain` domain**, bound as `FBSDKShareKitConstants.ShareErrorDomain` (an `NSString`, so it compares against `NSError.Domain` directly). Note the name: FBSDKShareKit's constants *do* get the module tag peeled, because that one constant is the module's whole extern-constant surface — unlike FBSDKCoreKit, where the tag stays.
- **The constructor and the `Dialog(...)` / `Show(...)` statics all carry an `SB0008` warning.** It is about the `ISharingContent` parameter, not the delegate: `ISharingContent` is an `@objc` protocol existential, so a *C#-authored* content type cannot be marshalled to Swift (it throws `NotSupportedException` at the call). The Swift-vended content types — `ShareLinkContent`, `SharePhotoContent`, `ShareVideoContent`, `ShareMediaContent`, `ShareCameraEffectContent` — are `ISwiftObject` and round-trip normally, so **the warning is benign for every supported usage** and you can suppress `SB0008` at the call site. The same applies to `MessageDialog`.

## FBSDKCoreKit_Basics utilities

You rarely reference this package directly, but its surface is fully bound and is the one place in the Facebook graph that is *purely* ObjC-shaped. Everything is static:

```csharp
using FBSDKCoreKit_Basics;
using Foundation;

string? b64  = FBSDKBase64.EncodeString("hello .NET");
string? back = FBSDKBase64.DecodeAsString(b64);
NSData? raw  = FBSDKBase64.DecodeAsData(b64);

string enc  = FBSDKBasicUtility.URLEncode("a b&c=d");
string? sha = FBSDKBasicUtility.SHA256Hash(new NSString("abc"));
NSData? gz  = FBSDKBasicUtility.Gzip(NSData.FromString("…"));

// Defensive coercion helpers (wrong-typed input yields the documented zero/nil, never a throw)
string? s = FBSDKTypeUtility.StringValueOrNil(new NSString("s"));
NSUrl?  u = FBSDKTypeUtility.CoercedToURLValue(new NSString("https://example.com"));

string version = FBSDKCrashHandler.GetFBSDKVersion();   // "18.1.0"
```

Also here: `FBSDKBasicUtility.URLDecode`, `.JSONStringForObject`/`.ObjectForJSONString`, `.QueryStringWithDictionary`/`.DictionaryWithQueryString`, `.AnonymousID`, and the rest of `FBSDKTypeUtility` (`IntegerValue`, `BoolValue`, `NumberValue`, `DictionaryValue`, `ArrayValue`, …).

If a name here collides with something in your own code, import it aliased — `using Basics = global::FBSDKCoreKit_Basics;` — as the binding test app does.

## FBAEMKit (Aggregated Event Measurement)

AEM is infrastructure the Core SDK drives for you; the useful direct surface is small.

```csharp
using FBAEMKit;

string? appId = AEMSettings.GetAppID();   // reads Info.plist's FacebookAppID

AEMReporter.SetConversionFilteringEnabled(true);
AEMReporter.SetCatalogMatchingEnabled(true);
AEMReporter.SetAdvertiserRuleMatchInServerEnabled(false);

// Enum raw values match upstream
var op = AEMAdvertiserRuleOperator.RegexMatch;
```

`AEMReporter.Configure(networker, appId, reporter)`, `AEMReporter.Enable()`, and `AEMReporter.HandleMethod(url)` are bound too, but Core drives them for you — `Configure` wants implementations of the Swift protocols `IAEMNetworking` / `ISKAdNetworkReporting`, so calling it by hand means supplying the whole networking stack.

`IAEMNetworking` additionally carries an `SB0010` marker: none of its requirements is reverse-dispatchable, so a C# type implementing it is **never called back from Swift**. Consuming a Swift-vended `IAEMNetworking` value is unaffected — the marker is only about authoring one. In practice this closes the door on hand-driving `Configure`, which was already the discouraged path.

Five `AEMReporter` members — `IsContentOptimized`, `RuleMatchRequestParameters`, `LoadRuleMatch`, `LoadMinAggregationRequestTimestamp`, and `AddConfigurations` — reach module-internal Swift types and are emitted as `[Obsolete(DiagnosticId = "SB0009")]` tombstones that throw `NotSupportedException` rather than making an unsound native call.

## Object lifetime

Swift-shaped types implement `ISwiftObject`. The recommended pattern is standard C# deterministic cleanup — `using var` — for short-lived values you fully own. `Dispose` is safe on every generated type and double-`Dispose` is a no-op.

Two things not to dispose: singletons reached through a static (`Settings.Shared`, `FBSDKAppEvents.Shared`, `ApplicationDelegate.Shared`, `Profile.Current`) are owned by the SDK; and any object that outlives the statement that created it — a `ShareDialog` and its content stay alive for the whole presentation, and a `FBLoginButton` for the lifetime of its view, so hold those in fields rather than wrapping them in `using`.

Swift enum values with payloads — `LoginResult` is the one you will actually handle — are `IDisposable` structs; dispose them when you own them, as in the login example above.

## Known limitations

- **`LoginManager.LogIn(permissions, viewController, handler)` is an `SB0001` non-callable stub** — no native thunk exists for it (its closure parameter shape cannot be marshalled by the C wrapper bridge). Use `LoginConfiguration` + `LogIn(viewController, configuration, completion)` (see [Facebook Login](#facebook-login)). This is the single most likely way to translate a Meta doc snippet into a crash.
- **Three App Links entry points are `SB0001` stubs too** — `AppLinkNavigation.ResolveAppLink(destination, resolver, handler)`, `AppLinkNavigation.Navigate(destination, resolver, handler)` and `AppLinkResolver.AppLinks(urls, handler)`. All three take an `any AppLinkResolving` existential alongside a closure, which is the shape the bridge cannot carry. Deferred deep links via `ApplicationDelegate.Shared.Application(application, userActivity)` are unaffected — only the manual resolver API is out of reach. (There is a fifth `SB0001` on the internal `_ErrorFactory`, which is not API.)
- **The `ShareContent` getter is unavailable on four types** — `ShareDialog`, `MessageDialog`, `FBSendButton` and `FBShareButton`, each `[Obsolete(DiagnosticId = "SB0006")]`, and reading one throws `NotSupportedException` (its Swift protocol reverse-dispatch proxy could not be generated). The **setter works**, and so does passing content through the constructor. Keep your own reference to the content object if you need to read it back.
- **The `ShareDialog` / `MessageDialog` constructors *and* their `Dialog(...)` / `Show(...)` statics carry an `SB0008` warning** — a *C#-authored* `ISharingContent` implementation cannot be marshalled to Swift (it throws `NotSupportedException`), because `ISharingContent` is an `@objc` protocol existential. The Swift-vended content types (`ShareLinkContent`, `SharePhotoContent`, `ShareVideoContent`, `ShareMediaContent`, `ShareCameraEffectContent`) round-trip fine, so the warning is benign for every supported usage. It does **not** apply to the delegate: a C#-authored `ISharingDelegate` is proxied and is called back normally.
- **`IAEMNetworking` carries an `SB0010` marker** — no requirement of the Swift `AEMNetworking` protocol is reverse-dispatchable, so a C# type implementing it is never invoked from Swift. Consuming a Swift-vended value through the interface is unaffected. Practical effect: `AEMReporter.Configure` cannot be hand-driven from C#, which was already the discouraged path.
- **`SB0009` tombstones throw `NotSupportedException`** — members whose signature reaches a type that is `internal` to its Swift module, so no `@_cdecl` wrapper can be generated. They are declared for source and conformance compatibility only. There are ~75 across the five packages, but essentially all are on Meta's own plumbing types (`MACARuleMatchingManager`, `LoginURLCompleter`, `GraphRequestPiggybackManager`, `_ShareUtility`, `DPoPKeyManager`, `DeviceRequestsHelper`, …) that no app calls. The ones on types this guide covers are the five `AEMReporter` members (see [FBAEMKit](#fbaemkit-aggregated-event-measurement)) and `LoginManager.GetRecentlyGrantedPermissions` / `GetRecentlyDeclinedPermissions`, which take the module-internal `FBPermission` rather than the public `Permission` — read `granted`/`declined` off the `LoginResult` instead.
- **`FBSendButton.ImpressionTrackingEventName` / `FBShareButton.ImpressionTrackingEventName` are not bound** — their type is `FBSDKCoreKit`'s `AppEvents.Name` (an `NS_TYPED_EXTENSIBLE_ENUM`), which does not resolve across the module boundary into FBSDKShareKit, so both properties are dropped. The buttons themselves work; only this analytics-labelling knob is missing. Known upstream.
- **Fourteen FBSDKCoreKit members typed by `FBSDKCoreKit_Basics` protocols fail closed** — eleven `configureWith…` / `init…` dependency-injection seams plus the `DataStore`, `FileManager` and `NotificationCenter` properties. Cross-assembly ObjC type resolution is not implemented, so rather than binding to the sibling assembly's real type they are omitted. Every one of them is a Facebook `INTERNAL - DO NOT USE` surface, so no public API is lost. This is the residual of the `MT4118` fix described below.
- **Deprecated upstream members carry `[Obsolete]`** — `Settings.AutoLogAppEventsEnabled` (use `IsAutoLogAppEventsEnabled`), `Settings.AdvertiserTrackingEnabled` (use `IsAdvertiserTrackingEnabled`), `ShareDialog.ModeKind.Web` / `.FeedBrowser` / `.FeedWeb`. Follow the message.
- **Internal `_`-prefixed types are bound but are not API** — `_BridgeAPI`, `_WebDialog`, `_ErrorFactory`, `_LoginCompletionParameters` and friends are marked "INTERNAL — DO NOT USE" upstream and can change without notice. Likewise, most `I…` interfaces exist so Swift-vended values can be handed *back* to you, not so you can implement them; `ISharingDelegate` is the notable exception.
- **Older SDK note:** under **SDK 0.18.0**, `FBSDKCoreKit`'s binding re-declared seven native names also owned by `FBSDKCoreKit_Basics` (`FBSDKCrashHandler`, `FBSDKCrashObserving`, `FBSDKDataPersisting`, `FBSDKFileDataExtracting`, `FBSDKFileManaging`, `FBSDKInfoDictionaryProviding`, `FBSDKNotificationDelivering`), breaking the registrar (`MT4118`) and therefore every device and App Store build. That duplication was fixed in **SDK 0.18.1** and remains fixed in 0.19.0, which these packages are built with — a plain `using FBSDKCoreKit_Basics;` alongside `using FBSDKCoreKit;` is no longer ambiguous. The only leftover is the fourteen internal DI seams noted above.

## Reference links

The binding is a faithful projection of Meta's SDK, not a re-design. **For full API semantics — permission names, event names, dialog behaviour, review requirements — consult Meta's own documentation and translate the names using the [table above](#the-two-binding-shapes-and-how-to-name-things).**

- [Facebook SDK for iOS — documentation home](https://developers.facebook.com/docs/ios/) — component SDKs, getting started, advanced topics
- [Getting Started with the iOS SDK](https://developers.facebook.com/docs/ios/getting-started) — the canonical `Info.plist` + app-delegate setup this guide translates
- [Facebook Login for iOS](https://developers.facebook.com/docs/facebook-login/ios) — permissions, Limited Login, access tokens, review
- [Sharing on iOS](https://developers.facebook.com/docs/sharing/ios) — content types, dialog modes, App Links
- [App Events for iOS](https://developers.facebook.com/docs/app-events/getting-started-app-events-ios) and the [standard events reference](https://developers.facebook.com/docs/app-events/reference) — the event- and parameter-name strings
- [Graph API reference](https://developers.facebook.com/docs/graph-api) — endpoints, fields, versions
- [Aggregated Event Measurement](https://developers.facebook.com/docs/app-events/guides/aggregated-event-measurement) — what FBAEMKit implements
- [facebook/facebook-ios-sdk](https://github.com/facebook/facebook-ios-sdk) — upstream source, changelog, and release notes (these packages track **v18.1.0**)
- [Meta App Dashboard](https://developers.facebook.com/apps) — where your app ID and client token live
