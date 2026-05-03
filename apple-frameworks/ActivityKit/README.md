# SwiftBindings.Apple.ActivityKit

Native .NET bindings for Apple's [ActivityKit](https://developer.apple.com/documentation/activitykit) framework — the API behind Live Activities and Dynamic Island on iOS.

## Status: not shipping for 1.0

ActivityKit is **not being published to nuget.org for the 1.0 release**. The blocker (below) is structural — first-class C# activity authorship would require a C# source generator that emits a Swift companion target, which is not in scope for 1.0.

If you're searching nuget.org for `SwiftBindings.Apple.ActivityKit` (or the legacy `SwiftBindings.ActivityKit`): it is intentionally absent. The README is kept here so that the decision (and the path forward) is discoverable in the repository.

## Why it is shelved

`Activity<Attributes>` is the core entry point of ActivityKit, and `Attributes` must be a concrete user type conforming to `ActivityAttributes`. `ActivityAttributes` in turn refines `Codable` and `Hashable`, and its associated `ContentState` must also be `Codable & Hashable`. Activity payloads are then shipped across the XPC boundary inside `ActivityContent<ContentState>`.

The binding layer exposes the ActivityKit type metadata (`Activity`, `ActivityContent`, `ActivityAuthorizationInfo`, `ActivityState`, `ActivityActivationState`, push-token APIs, etc.), but **user types cannot supply the required conformances from C#**. Two unrelated constraints combine to block this:

1. **`Codable` / `Hashable` synthesis is compiler-driven.** Both protocols are synthesized in Swift from the user type's stored properties at compile time. There is no runtime entry point we can hand-roll from the C# side that produces a functioning witness table for a type the Swift compiler never saw.
2. **`ActivityContent<ContentState>` is projected as an indeterminate PWT shape.** Even if we could supply the conformances, the generic parameter's witness-table layout depends on which protocols the *concrete* `ContentState` actually conforms to, information that only exists at the Swift call site.

Concretely, you can compile and link against an ActivityKit binding, but any attempt to materialize an `Activity<YourAttributes>` from C# will fail because `YourAttributes` has no Swift-visible definition, no conformance records, and no metadata descriptor.

### What works (if you build the binding locally)

- Reading `ActivityAuthorizationInfo.areActivitiesEnabled` and push-token capability metadata.
- Inspecting the ActivityKit type surface (e.g. in mixed projects that import `ActivityKit` symbols but never construct activities themselves).
- Consuming ActivityKit indirectly: a Swift companion target can declare your `ActivityAttributes` type, expose a C-callable shim (`@_cdecl`) that accepts an opaque handle plus a JSON or struct payload, and a future ActivityKit binding can still be used for the surrounding type references.

### Path to shipping

Tracked as a permanent limitation absent one of:

- **A C# source generator that emits a Swift companion target** declaring the attributes struct, its `ContentState`, conformance decls, and wrapper `@_cdecl` entry points for `request/update/end`. This is the only tractable path but has no precedent in the generator today (the existing analyzers all emit C#, not Swift).
- **Swift ABI changes** allowing runtime synthesis of `Codable` / `Hashable` witnesses for opaque external types. No indication Apple intends this.

Apps that must ship Live Activities from a .NET codebase today should declare the `ActivityAttributes` in a Swift companion target, expose a narrow C-callable API (`activity_request`, `activity_update(handle, payloadPtr)`, `activity_end(handle)`), and call into that surface via P/Invoke.

## Documentation

- [Apple ActivityKit framework](https://developer.apple.com/documentation/activitykit)
