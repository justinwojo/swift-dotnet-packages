# SwiftBindings.Apple.ActivityKit

Native .NET bindings for Apple's [ActivityKit](https://developer.apple.com/documentation/activitykit) framework — the API behind Live Activities and the Dynamic Island on iOS.

You can **start, update, and end Live Activities from C#.** The request/update/end chain is verified end-to-end on both the iOS Simulator (Mono JIT) and a physical device (NativeAOT).

## Quick start

```bash
dotnet add package SwiftBindings.Apple.ActivityKit
```

The content crosses as a **JSON string**, but you never hand-write it — model each payload as a C# type and serialize it (camelCase keys match the Swift struct your widget decodes into). Your payload shape and your widget's UI are entirely yours:

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;
using Swift.ActivityKit;

record DeliveryAttributes(string OrderId);
record DeliveryState(string Status, string? Eta = null);

var jsonOptions = new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
};

if (LiveActivity.AreActivitiesEnabled)
{
    var activity = LiveActivity.Request(
        name: "delivery",
        attributesJson:   JsonSerializer.Serialize(new DeliveryAttributes("A-42"), jsonOptions),
        contentStateJson: JsonSerializer.Serialize(new DeliveryState("Preparing", "15 min"), jsonOptions));

    activity.Update(JsonSerializer.Serialize(new DeliveryState("Out for delivery", "5 min"), jsonOptions));
    activity.End(JsonSerializer.Serialize(new DeliveryState("Delivered"), jsonOptions), immediate: true);
}
```

> Publishing to a physical device (NativeAOT)? Reflection-based serialization warns there; the [wiki guide](https://github.com/justinwojo/swift-dotnet-packages/wiki/ActivityKit) shows the source-generated variant for a warning-free publish.

Two more things are required, both standard for *any* Live Activity (Swift apps need them too):

1. Add `<key>NSSupportsLiveActivities</key><true/>` to your app's **Info.plist**.
2. Embed a small **SwiftUI widget extension** (~30 lines) to render the activity.

The complete walkthrough — the widget template, the shared attributes type, and the full C# API — is in the wiki:

➡️ **[Live Activities from .NET](https://github.com/justinwojo/swift-dotnet-packages/wiki/ActivityKit)**

## How it works

ActivityKit's entry point is `Activity<Attributes>`, where `Attributes` conforms to `ActivityAttributes: Codable & Hashable`. Those conformances are synthesized by the Swift compiler from the type's stored properties **at compile time** — so a C# type the Swift compiler never saw cannot serve as `Attributes`, and `Activity<YourCSharpType>` can never be materialized from C#.

This binding sidesteps that by shipping **one fixed, Swift-defined attributes type** (`DotNetLiveActivityAttributes`) inside the native `SBApple` framework. Because it is concrete at the binding's build time, the compiler synthesizes its witnesses then, and the `Activity<…>` generic is resolved entirely within `SBApple` — no generic and no protocol-witness table ever crosses the C ABI. Your per-activity data rides inside that fixed type as **JSON**; the widget decodes it to draw the UI.

Cross-process pairing between your running activity and the widget is by the attributes type's **unqualified name** plus a `Codable` round-trip, so your widget extension declares its own byte-for-byte copy of the type (Apple's standard "attributes in two targets" pattern) and never links this package.

## What ships vs. what doesn't

**Ships and works:**

- `LiveActivity.Request` / `Update` / `End` — the full lifecycle, returning a handle.
- `LiveActivity.AreActivitiesEnabled`, `IsActive`, and `LiveActivityException` for the failure reason.
- `LiveActivity.ObservePushToken` — APNs push tokens for server-driven updates (lowercase hex), when started with `usePushToken: true`.
- Registry hardening: idempotent `End`, and `Update`-after-`End` is a safe no-op rather than a use-after-free.

**Not available:** genuinely distinct, strongly-typed `ActivityAttributes` structs authored per app in C#. You model per-activity data as JSON inside the one fixed type instead. If you need separate compiler-checked attributes types, declare them in a Swift companion target and call into a narrow `@_cdecl` shim — the same technique this binding uses internally.

## Documentation

- [Live Activities from .NET (wiki)](https://github.com/justinwojo/swift-dotnet-packages/wiki/ActivityKit) — full setup walkthrough
- [Apple ActivityKit framework](https://developer.apple.com/documentation/activitykit)
- [Known Limitations (wiki)](https://github.com/justinwojo/swift-dotnet-bindings/wiki/Known-Limitations)
