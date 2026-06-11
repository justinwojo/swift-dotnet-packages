# ActivityKit (Live Activities) for .NET — Usage Guide

`SwiftBindings.Apple.ActivityKit` lets you drive [Live Activities](https://developer.apple.com/documentation/activitykit) — the lock-screen cards and Dynamic Island content iOS shows for ongoing events — from a .NET app. You **start, update, and end** an activity entirely from C# and render it with a tiny SwiftUI widget, exactly the way every Live Activity is rendered, .NET or not. The request/update/end chain is verified end-to-end on both the iOS Simulator (Mono JIT) and a physical device (NativeAOT).

This guide covers the one design constraint that makes it possible, the complete setup (package, capability, widget, C#), and the full API surface.

## Contents

- [Requirements & install](#requirements--install)
- [Quick start](#quick-start)
- [How it works (and why a fixed attributes type)](#how-it-works-and-why-a-fixed-attributes-type)
- [Step 1 — Add the package](#step-1--add-the-package)
- [Step 2 — Declare the capability](#step-2--declare-the-capability)
- [Step 3 — Add the SwiftUI widget extension](#step-3--add-the-swiftui-widget-extension)
- [Step 4 — Drive it from C#](#step-4--drive-it-from-c)
- [API reference](#api-reference)
- [Push-driven updates](#push-driven-updates)
- [Lifetime & threading](#lifetime--threading)
- [What ships vs. what doesn't](#what-ships-vs-what-doesnt)
- [Troubleshooting](#troubleshooting)
- [Reference links](#reference-links)

## Requirements & install

- .NET 10.0+
- Target framework: any `net10.0-ios` TFM — set your minimum with `<SupportedOSPlatformVersion>`; the package itself is built against the iOS 26.2 supplement. Live Activities are an iOS/iPadOS surface only — there is no macOS, Mac Catalyst, or tvOS leg
- **iOS 16.2+** at runtime for `Request` / `Update` / `End` (the attributes type itself is 16.1+)
- macOS host for development
- The host app must be **foreground-active** when it calls `Request` — ActivityKit throws otherwise (an Apple rule, not a binding limitation)
- A **WidgetKit extension** embedded in your app to render the activity (~30 lines of SwiftUI; template below)
- The **`NSSupportsLiveActivities`** Info.plist key on the host app
- The **Dynamic Island** specifically needs an iPhone 14 Pro or newer; every Live-Activity-capable device shows the lock-screen / banner presentation regardless

```
dotnet add package SwiftBindings.Apple.ActivityKit
```

```csharp
using Swift.ActivityKit;
```

> This package also generates a binding for the **system ActivityKit framework types** (`ActivityAuthorizationInfo`, `ActivityState`, `ActivityStyle`, `ActivityAuthorizationError`, push-token metadata, …) under the `ActivityKit` namespace. The high-level lifecycle API you'll use day to day is `Swift.ActivityKit.LiveActivity`, which ships in the transitively-referenced `SwiftBindings.Apple` supplement — no extra package reference needed.

## Quick start

The content crosses as a **JSON string** — that's the contract, since it round-trips through `Codable` into the widget's separate process — but you never hand-write it. Model each payload as whatever C# types suit your app and serialize them. Your payload shape and your widget's UI are entirely yours; the binding never looks inside the JSON.

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;
using Swift.ActivityKit;

// Your payloads — shape them however you like; the binding only needs them as JSON.
record DeliveryAttributes(string OrderId);
record DeliveryState(string Status, string? Eta = null);

// camelCase keys match the property names on the Swift struct your widget decodes into;
// null fields are omitted so an ended activity sends just {"status":"Delivered"}.
var jsonOptions = new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
};

// Always check first — a request on a disabled app throws.
if (!LiveActivity.AreActivitiesEnabled)
    return;

// Start. `name` selects which widget UI renders; the two payloads are your serialized objects.
var activity = LiveActivity.Request(
    name: "delivery",
    attributesJson:   JsonSerializer.Serialize(new DeliveryAttributes("A-42"), jsonOptions),
    contentStateJson: JsonSerializer.Serialize(new DeliveryState("Preparing", "15 min"), jsonOptions));

// Update the changing state as often as you like (fire-and-forget; applies in order).
activity.Update(JsonSerializer.Serialize(new DeliveryState("Out for delivery", "5 min"), jsonOptions));

// Finish it. `immediate: true` removes it at once; the default lets the system keep it briefly.
activity.End(JsonSerializer.Serialize(new DeliveryState("Delivered"), jsonOptions), immediate: true);
```

That's the whole loop. `Request` returns a handle; `Update`/`End` are methods on it. **This C# won't render anything on its own** — you must still do the two standard setup steps below (the `NSSupportsLiveActivities` Info.plist key and the SwiftUI widget), exactly as a pure-Swift Live Activity requires.

> **Publishing to a physical device?** Device builds use NativeAOT, where reflection-based `JsonSerializer.Serialize` reports `IL2026`/`IL3050` trim/AOT warnings (it still runs — simple payloads serialize fine — but the publish is noisy). For a warning-free publish, generate the serializer at compile time with a [source-generated context](https://learn.microsoft.com/dotnet/standard/serialization/system-text-json/source-generation): declare the context once — alongside your payload records or in any shared source file — and pass the generated per-type `JsonTypeInfo` in place of `jsonOptions`.
>
> ```csharp
> [JsonSourceGenerationOptions(
>     PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
>     DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
> [JsonSerializable(typeof(DeliveryAttributes))]
> [JsonSerializable(typeof(DeliveryState))]
> partial class LiveActivityJson : JsonSerializerContext;
>
> // ...then serialize against the generated context:
> activity.Update(JsonSerializer.Serialize(
>     new DeliveryState("Out for delivery", "5 min"), LiveActivityJson.Default.DeliveryState));
> ```

## How it works (and why a fixed attributes type)

That setup — JSON payloads plus a hand-copied Swift struct in the widget — looks the way it does for one reason. ActivityKit's entry point is `Activity<Attributes>`, where `Attributes` conforms to `ActivityAttributes`, which refines `Codable & Hashable`. Those conformances are **synthesized by the Swift compiler from the type's stored properties at compile time** — there is no runtime entry point that manufactures a working witness table for a type the Swift compiler never saw. A C# type therefore cannot serve as `Attributes`, and `Activity<YourCSharpType>` can never be materialized. That is the permanent limitation behind the old "ActivityKit isn't supported from C#" guidance.

This binding sidesteps it by shipping **one** concrete attributes type, `DotNetLiveActivityAttributes`, fully defined in Swift *inside* the native `SBApple` framework. Because it is concrete at the binding's build time, the compiler synthesizes its `Codable`/`Hashable` witnesses then, and the `Activity<DotNetLiveActivityAttributes>` generic is resolved entirely within `SBApple` — **no generic and no protocol-witness table ever crosses the C ABI.** Your per-activity data rides inside that fixed type as **JSON**, and the widget decodes it to draw the UI.

```
Your .NET app
    Swift.ActivityKit.LiveActivity.Request / Update / End
        │
        │   per-activity data as JSON, over the @_cdecl C ABI
        ▼
SBApple.framework            (ships inside SwiftBindings.Apple)
    DotNetLiveActivityAttributes — one fixed, Swift-defined type
    Activity<…>.request / update / end
    (concrete: no generics, no protocol-witness tables cross the C boundary)
        │
        │   ActivityKit pairs the activity to the widget by the attributes
        │   type's *unqualified name* + a Codable round-trip
        ▼
Your SwiftUI widget extension   (a ~30-line *.appex)
    Declares its OWN byte-for-byte copy of DotNetLiveActivityAttributes
    ActivityConfiguration(for:) { lock-screen card + Dynamic Island }
```

Cross-process pairing between your running activity and the widget is by the attributes type's **unqualified name** plus a `Codable` round-trip — *not* module identity — so your widget extension declares its own byte-for-byte copy of the type (Apple's standard "attributes type in two targets" pattern) and never links this package.

## Step 1 — Add the package

```bash
dotnet add package SwiftBindings.Apple.ActivityKit
```

This brings in the `Swift.ActivityKit.LiveActivity` API and, transitively, the `SwiftBindings.Apple` supplement that carries the native `SBApple` framework the API calls into. No other native reference is needed.

## Step 2 — Declare the capability

Add to your app's **Info.plist**:

```xml
<key>NSSupportsLiveActivities</key>
<true/>
```

(For background/remote updates via push you would also enable push capabilities — see [Push-driven updates](#push-driven-updates). Local start/update/end from your own code needs only this key.)

## Step 3 — Add the SwiftUI widget extension

Live Activity UI is always SwiftUI, authored in a WidgetKit extension — this is true for Swift apps too; it is the one piece that cannot be C#. Add a **Widget Extension** target to your app (in Xcode: *File ▸ New ▸ Target ▸ Widget Extension*, check *Include Live Activity*), then put these **two** files in it.

**`DotNetLiveActivityAttributes.swift`** — a byte-for-byte copy of the binding's attributes type. ActivityKit pairs a running activity to your widget by this type's **unqualified name** plus a `Codable` round-trip, so your widget declaring its own identical copy is all the pairing needs.

```swift
import Foundation
import ActivityKit

@available(iOS 16.1, *)
public struct DotNetLiveActivityAttributes: ActivityAttributes {
    public struct ContentState: Codable, Hashable {
        public var json: String
        public init(json: String) { self.json = json }
    }
    /// Identifies the activity "kind"; switch on it to pick a UI.
    public var name: String
    /// Static (non-updating) attributes, as a JSON blob.
    public var json: String
    public init(name: String, json: String) {
        self.name = name
        self.json = json
    }
}
```

**`DotNetLiveActivityWidget.swift`** — the UI. Decode `context.attributes.json` (static) and `context.state.json` (updating) into whatever shape your app sends, and switch on `context.attributes.name` if you render more than one kind of activity. This example shows the raw JSON fields for brevity — decode them into a real model before you ship (see the note right after the code):

```swift
import WidgetKit
import SwiftUI
import ActivityKit

@main
struct DotNetWidgetBundle: WidgetBundle {
    var body: some Widget { DotNetLiveActivityWidget() }
}

@available(iOS 16.2, *)
struct DotNetLiveActivityWidget: Widget {
    var body: some WidgetConfiguration {
        ActivityConfiguration(for: DotNetLiveActivityAttributes.self) { context in
            // Lock screen / banner presentation.
            HStack {
                VStack(alignment: .leading, spacing: 4) {
                    Text(context.attributes.name).font(.headline)
                    Text(context.state.json).font(.caption).foregroundStyle(.secondary)
                }
                Spacer()
                Image(systemName: "bolt.fill").foregroundStyle(.yellow)
            }
            .padding()
            .activityBackgroundTint(Color.blue.opacity(0.25))
        } dynamicIsland: { context in
            DynamicIsland {
                DynamicIslandExpandedRegion(.leading) {
                    Text(context.attributes.name).font(.caption).bold()
                }
                DynamicIslandExpandedRegion(.bottom) {
                    Text(context.state.json).font(.caption2)
                }
            } compactLeading: {
                Image(systemName: "bolt.fill").foregroundStyle(.yellow)
            } compactTrailing: {
                Text(context.attributes.name).font(.caption2)
            } minimal: {
                Image(systemName: "bolt.fill").foregroundStyle(.yellow)
            }
        }
    }
}
```

Decode the JSON into a real model instead of showing the raw string in anything you ship — for example `try JSONDecoder().decode(MyState.self, from: Data(context.state.json.utf8))`. The binding does not care about the JSON's shape; you own both ends of it.

## Step 4 — Drive it from C#

The C# is the three-call loop from [Quick start](#quick-start) — `Request`, then `Update` as the state changes, then `End`. One rule is specific to this binding: every JSON argument (`attributesJson`, `contentStateJson`, and the `Update` / `End` payloads) must be a JSON **object** — `{ … }` — or null/empty, which the facade normalizes to `{}`. A malformed payload would start an activity whose widget silently renders nothing, so the facade validates eagerly and throws `ArgumentException` before any ActivityKit call.

## API reference

`Swift.ActivityKit.LiveActivity`

| Member | Description |
|---|---|
| `static bool AreActivitiesEnabled` | The per-app Settings → Live Activities toggle combined with the `NSSupportsLiveActivities` capability. Check before `Request`. |
| `static LiveActivity Request(string name, string attributesJson = "{}", string contentStateJson = "{}", bool usePushToken = false)` | Starts an activity. Throws `LiveActivityException` if the system refuses (disabled, payload over the ~4 KB budget, app not foreground, unsupported target). Returns a live handle. |
| `bool IsActive` | False once the activity has ended (via `End`). |
| `bool Update(string contentStateJson)` | Replaces the updating content state. Returns false if already ended — never throws on a dead handle. Consecutive updates apply in call order. |
| `bool End(string? finalContentStateJson = null, bool immediate = false)` | Ends the activity. Idempotent — a second call is a safe no-op returning false. The end is ordered after pending updates, and the call blocks (bounded) until applied, so the activity is actually gone when it returns. |
| `bool ObservePushToken(Action<string> onToken)` | For server-driven updates: invokes `onToken` with each APNs push token as lowercase hex. Requires `usePushToken: true` and the push capability; otherwise a harmless no-op. |

`Swift.ActivityKit.LiveActivityException` — thrown only by `Request`; its `Message` is the system-reported reason (e.g. activities disabled, attributes over the ~4 KB budget, app not foreground-active).

## Push-driven updates

To update an activity from your server instead of from the device:

1. Start it with `usePushToken: true` and enable the push-notifications capability on the host app.
2. Observe token refreshes:

```csharp
var activity = LiveActivity.Request("delivery", usePushToken: true);
activity.ObservePushToken(hex =>
{
    // hex is the APNs push token as a lowercase hex string.
    // Send it to your server; it pushes ContentState updates to APNs.
    // NOTE: this callback runs on a background thread — marshal to the
    // main thread before touching UI. Only one observer per activity.
});
```

Your server then pushes `content-state` payloads to APNs against that token. The binding's role ends at delivering the token; the APNs push itself is standard server-side ActivityKit.

## Lifetime & threading

- **A Live Activity outlives the `LiveActivity` object** — the system holds it. Letting the object be garbage-collected does **not** end the activity (correct ActivityKit behavior: an order-tracking card should outlive the view model that started it). End it explicitly with `End`. There is no finalizer.
- `ObservePushToken`'s callback runs on a background thread (the Swift concurrency pool). Marshal to the main thread before touching UI. An exception it throws cannot propagate across the native boundary — it is caught and written to standard error.
- `Update`/`End` are safe to call on a handle that has already ended (they return false); concurrent `End` calls are serialized so only one native end is dispatched.

## What ships vs. what doesn't

**Ships and works:**

- `LiveActivity.Request` / `Update` / `End` — the full lifecycle, returning a handle.
- `LiveActivity.AreActivitiesEnabled`, `IsActive`, and `LiveActivityException` for the failure reason.
- `LiveActivity.ObservePushToken` — APNs push tokens for server-driven updates.
- The generated system-ActivityKit type surface (`ActivityAuthorizationInfo`, `ActivityState`, `ActivityStyle`, `ActivityAuthorizationError` + extensions, `ActivityUIDismissalPolicy`, `AlertConfiguration`, `PushType`).
- Registry hardening: idempotent `End`, and `Update`-after-`End` is a safe no-op rather than a use-after-free.

**Not available:** genuinely distinct, strongly-typed `ActivityAttributes` structs authored per app in C#. You model per-activity data as JSON inside the one fixed type instead. If you need separate compiler-checked attributes types, declare them in a Swift companion target and call into a narrow `@_cdecl` shim — the same technique this binding uses internally.

## Troubleshooting

| Symptom | Cause / fix |
|---|---|
| `LiveActivityException: unsupportedTarget` on `Request` | `NSSupportsLiveActivities` isn't in the **built** app bundle's Info.plist. Confirm the key is present *and* that an incremental build didn't skip the manifest — a clean rebuild forces it. |
| `LiveActivityException: visibility` | The app wasn't foreground-active when you called `Request`. Call it from an active state, not from the background or during launch. |
| `AreActivitiesEnabled` is false | The user turned Live Activities off for your app in Settings (or the entitlement is absent at runtime). A missing `NSSupportsLiveActivities` key usually surfaces instead as `LiveActivityException: unsupportedTarget` on `Request`. |
| Activity starts but nothing renders | No widget extension embedded, or its `DotNetLiveActivityAttributes` doesn't match (same property names, same `Codable` shape). The activity is still tracked; only the UI is missing. |
| Nothing in the Dynamic Island, but the lock screen works | Expected on devices without Dynamic Island hardware (anything before iPhone 14 Pro). The lock-screen presentation is the cross-device surface. |
| Simulator shows nothing in the Dynamic Island | The iOS Simulator does not composite third-party Live Activities into the Dynamic Island. Use the lock screen, or a physical device, to see it render. The start/update/end calls themselves work on the simulator. |

## Reference links

- [Apple: ActivityKit](https://developer.apple.com/documentation/activitykit)
- [Apple: Displaying live data with Live Activities](https://developer.apple.com/documentation/activitykit/displaying-live-data-with-live-activities)
- [swift-dotnet-bindings wiki: Known Limitations](https://github.com/justinwojo/swift-dotnet-bindings/wiki/Known-Limitations) — where the fixed-attributes-type design fits in the broader binding limitations
