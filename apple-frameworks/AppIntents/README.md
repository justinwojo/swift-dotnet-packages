# SwiftBindings.Apple.AppIntents

Native .NET bindings for Apple's [AppIntents](https://developer.apple.com/documentation/appintents) framework — the API behind exposing app actions to Siri, Shortcuts, and Spotlight.

## Status: not shipping for 1.0

AppIntents is **not being published to nuget.org for the 1.0 release**. The blocker (below) is structural: the framework's entire value is OS-level Siri / Shortcuts / Spotlight integration, and that integration is unreachable from C# for two independent reasons. First-class App Intent authorship would require a C# source generator that emits a Swift companion target, which is not in scope for 1.0.

If you're searching nuget.org for `SwiftBindings.Apple.AppIntents`: it is intentionally absent. The README is kept here so that the decision (and the path forward) is discoverable in the repository.

## Why it is shelved

AppIntents has two surfaces, and the binding only reaches the one that is worthless on its own:

1. **Data-modeling surface** — `AppEntity`, `EntityQuery`, `EntityProperty`, `DisplayRepresentation`. These bind today: a conformer compiles through the bindings, `EntityProperty` binds as a closed-generic class, queries resolve, and property values round-trip.
2. **The product surface** — authoring an `AppIntent` (`func perform() async throws`), declaring `AppShortcuts`, and the macro-expanded metadata. This is the entire reason the framework exists: it is how Siri, Shortcuts, and Spotlight get an action to run.

Surface 1 has value **only** when consumed by surface 2, and surface 2 is structurally impossible from C#:

1. **`AppIntent` / `AppEntity` are Swift-struct-only protocols whose conformances are compiler- and macro-synthesized.** Conforming a type requires the `@AppIntent` / `@AppEntity` macros plus compiler-synthesized `Codable` / `Hashable` / `_IntentValue` witnesses. `@objc` cannot be applied to Swift structs, so there is no bridging path to conform a C#-authored type, and there is no runtime entry point that produces a working witness table for a type the Swift compiler never saw.
2. **OS integration is driven by build-time metadata extraction from Swift source.** `appintentsmetadataprocessor` scans the consumer's **Swift source code** at build time and embeds static metadata into the app binary. Siri, Shortcuts, and Spotlight read that static metadata at the OS level *without ever running your app*. A C# consumer emits no Swift source for the processor to scan, so **no metadata is embedded and the OS sees nothing** — no matter how perfectly the `AppEntity` types bind.

Concretely, you can construct an `AppEntity` from C# and round-trip its properties, but there is no authored intent to consume it and no metadata for the OS to act on. The binding hands you data types you can instantiate and nothing the system will ever do anything with.

### What works (if you build the binding locally)

- Constructing `AppEntity` conformers and round-tripping their stored properties.
- Resolving `EntityQuery` / `DefaultQuery` static accessors and inspecting the AppIntents type surface.
- Consuming AppIntents indirectly: a Swift companion target can declare your `@AppIntent` / `@AppEntity` types and expose a C-callable shim (`@_cdecl`), and a binding can still be used for the surrounding type references.

### Path to shipping

Tracked as a permanent limitation absent one of:

- **A C# source generator that emits a Swift companion target** declaring the `@AppIntent` / `@AppEntity` types, running them through the AppIntents macros and `appintentsmetadataprocessor`, and exposing `@_cdecl` entry points that dispatch `perform()` back into C#. This is the only tractable path but has no precedent in the generator today (the existing analyzers all emit C#, not Swift). Same shape as the ActivityKit path forward.
- **Swift / OS changes** allowing intent metadata and conformances to be supplied for opaque external types at runtime. No indication Apple intends this.

Apps that must expose App Intents from a .NET codebase today should declare the `@AppIntent` types in a Swift companion target, run the standard AppIntents build-time metadata extraction over that target, and call into a narrow C-callable surface from C# for any shared logic.

## Documentation

- [Apple AppIntents framework](https://developer.apple.com/documentation/appintents)
