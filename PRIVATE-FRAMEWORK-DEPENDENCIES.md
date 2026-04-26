# Private framework dependencies — SDK gap blocking the Stripe 1.0 release

## TL;DR

The SwiftBindings SDK conflates two purposes of `<SwiftFrameworkDependency>`: a build-time framework search-path entry (and runtime native-reference), and a pack-time NuGet `<PackageReference>` declaration. There's no way to express the first without the second.

Multi-product Swift libraries that ship internal frameworks — frameworks that are required at build/link/runtime by sibling public products but should not be surfaced as standalone NuGet packages — cannot ship through the SDK today. Stripe is the immediate blocker (`Stripe3DS2`, `StripeCameraCore`), but any vendor whose SPM/zip distribution includes umbrella-style internal frameworks will hit the same wall.

The proposed fix is a new SDK item type, `<SwiftFrameworkPrivateDependency>`, that bundles the xcframework into the consuming `.nupkg` instead of emitting a NuGet dependency. Until that lands, Stripe stays paused.

## How we got here

`libraries/Stripe/library.json` declares 14 products from the upstream Stripe iOS release zip. Twelve are user-facing (`Stripe`, `StripeCore`, `StripePayments`, `StripePaymentSheet`, `StripePaymentsUI`, `StripeApplePay`, `StripeConnect`, `StripeIdentity`, `StripeIssuing`, `StripeCardScan`, `StripeFinancialConnections`, `StripeUICore`). Two are marked `"internal": true` (`Stripe3DS2`, `StripeCameraCore`) — their xcframeworks are produced by `BuildXcframework` but no csproj is generated and no NuGet is published.

Several public products link against `Stripe3DS2` (the umbrella `Stripe`, plus `StripeIssuing`, `StripePaymentSheet`, `StripePaymentsUI`, `StripePayments`); `StripeCardScan` links against `StripeCameraCore`. Their wrapper compilation, binding generation, and runtime resolution all need the internal xcframework on the framework search path.

`InjectFrameworkDeps` (in `build/Build.Dependencies.cs`) parses each public product's `.swiftinterface`, finds an `import Stripe3DS2`, and writes a `<SwiftFrameworkDependency Include="../Stripe3DS2/Stripe3DS2.xcframework" />` into the public product's csproj. That's the right thing for build-time correctness — without it, wrapper compile fails with "module not found".

At pack time, the SDK target `_ValidateSwiftDependencyMetadata` (Sdk.targets:1444) runs:

```xml
<Error Condition="'%(SwiftFrameworkDependency.PackageId)' == '' OR '%(SwiftFrameworkDependency.PackageVersion)' == ''"
       Code="SWIFTBIND040"
       Text="SwiftFrameworkDependency '%(SwiftFrameworkDependency.Identity)' is missing PackageId or PackageVersion metadata.
             The NuGet package cannot declare this dependency.
             Add PackageId and PackageVersion metadata for correct NuGet dependency propagation,
             or exclude the dependency before pack." />
```

The error fires because `Stripe3DS2.xcframework` has no NuGet to point at — it's intentionally not a published package. Pack aborts. Five Stripe csprojs fail this check (the umbrella + `StripeIssuing` + `StripePaymentSheet` + `StripePaymentsUI` + `StripePayments`).

The error message offers two outs:

1. **"Add PackageId and PackageVersion metadata"** — would require publishing `Stripe3DS2` and `StripeCameraCore` as standalone NuGets just to satisfy the dep declaration. Surface area users don't want, naming churn ("`SwiftBindings.Stripe.ThreeDS2`"?), and the "internal" notion is lost — they look like normal user-facing packages on nuget.org.
2. **"Exclude the dependency before pack"** — there is no SDK-supported exclusion mechanism. We'd have to write a custom MSBuild target in `Directory.Build.targets` that removes items from `@(SwiftFrameworkDependency)` before `_ValidateSwiftDependencyMetadata` runs. That bypasses the validation, but the SDK then doesn't bundle the xcframework into the `.nupkg` either (only source/wrapper/bridge xcframeworks are packed; SFD-referenced xcframeworks rely on the consuming-NuGet path). Consumers would get `DllNotFoundException` at runtime.

Neither out is a real solution.

## Why this is a structural problem, not a Stripe quirk

The current SDK design assumes a one-to-one mapping: one xcframework → one NuGet package. That works for libraries where every framework in the dep graph is independently publishable (`Nuke`, `Lottie`, single-product builds, even multi-product builds where every product is a real public API surface).

It breaks for any vendor whose binary distribution bundles internal infrastructure frameworks alongside the public API surface — which is common in iOS SDKs:

- **Stripe** — `Stripe3DS2`, `StripeCameraCore`
- **Firebase** — `FirebaseCoreInternal`, `GoogleUtilities` sub-frameworks
- **Facebook SDK** — `FBSDKCoreKit_Basics`, several utility frameworks
- **Many vendor SDKs** that ship with Apple-style umbrella + private-module patterns

Forcing every internal framework to be a published NuGet would double-or-more the package surface for these vendors, leak implementation detail to nuget.org, and force consumers to install packages they shouldn't even know exist. The structural mismatch is between what the *upstream library author* considers private vs. public, and what NuGet's package model requires.

## Proposal: `SwiftFrameworkPrivateDependency`

Add a new SDK item type that means **"build-time framework search path + bundle xcframework into THIS pkg's nupkg, no NuGet dep declared"**.

```xml
<ItemGroup>
  <!-- Public dep — emits a PackageReference, validated against PackageId/Version -->
  <SwiftFrameworkDependency Include="../StripeCore/StripeCore.xcframework"
                            PackageId="SwiftBindings.Stripe.Core"
                            PackageVersion="25.11.0" />

  <!-- Private dep — xcframework bundled into THIS pkg, no NuGet emitted -->
  <SwiftFrameworkPrivateDependency Include="../Stripe3DS2/Stripe3DS2.xcframework" />
</ItemGroup>
```

### Build-time semantics (identical to today's `SwiftFrameworkDependency`)

- Added to wrapper-compile framework search path (`-F` flags in `_SwiftWrapperCmd`).
- Added to binding-gen framework dependency list (`--framework-dependency` in `_SwiftGenCmd`).
- Added to bridge-compile framework search path (`_SwiftBridgeCmd`).
- Materialized as `<NativeReference>` in `_ResolveSwiftNativeReferences` so in-tree builds and test apps link against the source xcframework.

### Pack-time semantics (the new behavior)

- `_ValidateSwiftDependencyMetadata` ignores private deps (no `PackageId`/`PackageVersion` requirement).
- Sdk.props's auto-`PackageReference` injection (Sdk.props:145) is already gated on both metadata values being present — private deps simply produce no `PackageReference` (no Sdk.props change needed).
- `_ConfigureSwiftBindingPack` emits a new `TfmSpecificPackageFile` entry for each private dep, packing the xcframework into `runtimes/<rid>/native/<framework>.xcframework/`. That puts it alongside the package's own source/wrapper/bridge xcframeworks.
- A pre-pack guard verifies the xcframework exists on disk so we fail at pack, not at the consumer's runtime.

### Consumer-side resolution

When a consumer installs `SwiftBindings.Stripe.PaymentSheet`, the existing .NET iOS NativeReference resolution picks up every `*.xcframework` under `runtimes/<rid>/native/` from the pkg. Today that's the source + wrapper + bridge; with private deps it's also `Stripe3DS2.xcframework`. No consumer-side change required.

### Cost: xcframework duplication across siblings

If five sibling pkgs all privately depend on `Stripe3DS2`, all five `.nupkg`s ship a copy of `Stripe3DS2.xcframework` (~10 MB compressed each). Consumers installing more than one Stripe pkg get multiple copies on disk; dyld at runtime de-dupes by binary identity, so this is a disk-cost concern only, not a correctness concern, and only for users who install multiple sibling packages.

The duplication cost is real but bounded: it scales with `(# private xcframeworks) × (# sibling packages that need each)`, and only matters for pkgs that actually publish. For the vast majority of apps depending on `Stripe.PaymentSheet`, only one or two Stripe pkgs are installed. We accept it in exchange for a clean public surface.

If duplication becomes problematic for a specific vendor, the answer is to consolidate sibling packages, not to invent shared-internal-pkg machinery.

## Alternatives considered

### A. Publish internal frameworks as thin NuGets

Add a fake "package" for each internal framework: `SwiftBindings.Stripe.ThreeDS2`, etc. Each is just a `NativeReference`-bundled xcframework with no C# bindings. Existing SFD entries get `PackageId="SwiftBindings.Stripe.ThreeDS2"` and pack passes.

**Why rejected:**

- Leaks implementation detail. Nuget.org gets two more "Stripe" packages that no app should ever explicitly install.
- Naming pollution. What do you call them? `*.Internal.ThreeDS2`? `*.Private.ThreeDS2`? Whatever you pick, it's wrong, because an internal-to-Stripe framework being a public NuGet *is* the wrong abstraction.
- Doesn't generalize. Every multi-product vendor with internal frameworks (Firebase, Facebook, etc.) eats the same surface-area expansion.
- Versioning churn. Every Stripe bump now requires bumping 14 packages instead of 12.

### B. "Designated owner ships, siblings depend on it"

One sibling owns each internal framework: `Stripe.Payments` ships `Stripe3DS2.xcframework`; everyone else depends on `Stripe.Payments` via `<PackageReference>`. No new NuGet, no duplication.

**Why rejected:**

- Reintroduces the NuGet dep under a different name. Consumers of `Stripe.PaymentSheet` now drag in `Stripe.Payments` whether they need the actual `Stripe.Payments` API surface or not.
- Diamond-dependency brittleness. If `Stripe.PaymentSheet` and `Stripe.Issuing` both want `Stripe3DS2` and they pick different "owners," the resolution is non-deterministic.
- Picking the owner is editorial. Why does `Stripe.Payments` "own" `Stripe3DS2` rather than `Stripe.PaymentSheet`? The graph of which siblings link against which internal framework is upstream-defined; we'd be inventing ownership that doesn't exist in the source.

### C. "Just make a custom MSBuild target that strips internal SFDs before pack"

A `Directory.Build.targets` with `<ItemGroup BeforeTargets="_ValidateSwiftDependencyMetadata"><SwiftFrameworkDependency Remove="..." /></ItemGroup>` to bypass the validation.

**Why rejected:**

- The xcframework still doesn't get bundled into the `.nupkg`. The SDK only packs source/wrapper/bridge xcframeworks; the SFD-referenced xcframework is *not* in the pack pipeline. Strip the SFD and the consumer gets `DllNotFoundException` at first call into the missing module.
- We'd need to *also* add manual `TfmSpecificPackageFile` entries to bundle the xcframework, *and* re-add it as `NativeReference` for in-tree consumers, *and* keep the framework search path correct for wrapper compile. We'd be reimplementing half of what the SDK already does, in user-space, per affected csproj.
- It's the SDK's job. Doing this in user-space spreads the kludge across every multi-product vendor instead of fixing it once.

### D. SDK "transitive xcframework" mechanism

A general-purpose mechanism for packs to declare "I ship these extra xcframeworks alongside my own." More flexible than `SwiftFrameworkPrivateDependency`, but the flexibility doesn't buy anything for the use case at hand and adds surface area to the SDK contract.

**Why rejected:** YAGNI. The private-framework case is the concrete need; design for it directly.

## Implementation plan

### Phase 1 — SDK changes (`swift-dotnet-bindings`)

1. Add `<SwiftFrameworkPrivateDependency>` to the SDK contract (Sdk.props or Sdk.targets — wherever item group declarations live).
2. Wire build-time: append private deps to `_SwiftGenCmd`, `_SwiftWrapperCmd`, `_SwiftBridgeCmd` framework-dependency flags (mirroring today's SFD handling). Add NativeReference entries in `_ResolveSwiftNativeReferences`.
3. Wire pack-time: in `_ConfigureSwiftBindingPack`, emit `TfmSpecificPackageFile` entries that pack each private dep's xcframework into `runtimes/<rid>/native/<framework>.xcframework/`. Add a pre-pack `<Error>` guarding existence on disk.
4. Update `_ValidateSwiftDependencyMetadata` to skip private deps (no metadata required).
5. Update `GetNativeManifest` so consuming projects via `<ProjectReference>` see the private-dep xcframework in their NativeReference list (parity with how SFDs flow today).
6. Tests: a multi-product fixture with one product privately depending on a sibling-built xcframework that has no published NuGet. Pack should succeed; the produced nupkg's `runtimes/<rid>/native/` should contain both xcframeworks; a downstream consumer project should resolve the private xcframework as a NativeReference at compile time.
7. New diagnostic codes for clarity (suggested):
   - `SWIFTBIND042`: private dep xcframework missing on disk at pack time.
   - `SWIFTBIND043` (warning): private dep is also declared as a public `SwiftFrameworkDependency` in the same csproj — pick one.

### Phase 2 — Rewriter changes (`swift-dotnet-packages`)

1. `CsprojRewriter` already preserves attributes on existing SFD entries. Extend it to write `<SwiftFrameworkPrivateDependency>` (instead of `<SwiftFrameworkDependency>`) when the dependency points at a product whose `library.json` entry has `"internal": true`.
2. `Build.Dependencies.cs::InjectFrameworkDepsForLibrary` already iterates `config.Products` and knows which are internal (currently it skips them as iteration *targets* but happily emits SFDs *toward* them). The inject step needs to call out which output kind to use.
3. Migration: existing csprojs with hand-authored `<SwiftFrameworkDependency Include="../Stripe3DS2/..." />` get rewritten on first run after the SDK bump.

### Phase 3 — Stripe ship

Once the SDK lands and is published, bump `SwiftBindings.Sdk` in `global.json` (or wherever it's pinned), re-run `BuildLibrary --library Stripe --all-products`, validate, fire the release dispatch. README flips Stripe rows back from `_Coming soon_` to NuGet badges.

## Out of scope

- **Apple-framework packages.** They don't have private deps — they bind against Apple's system SDKs and the only "extra" xcframework involved is the wrapper. Unaffected.
- **Single-product third-party libraries** (`Nuke`, `Lottie`, etc.). Same — no internal dep graph. Unaffected.
- **Mappedin / BlinkID.** These are paused for unrelated reasons (manual-mode artifact provisioning, not internal-framework structure). Tracked separately.

## Status

- **2026-04-26** — issue identified during the Stripe 1.0 release dispatch (pack failed at `_ValidateSwiftDependencyMetadata`). Stripe rows flipped to "Coming soon" in `README.md`. Design doc opened (this file). SDK issue to be filed against `swift-dotnet-bindings`.
