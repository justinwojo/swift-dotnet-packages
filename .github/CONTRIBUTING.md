# Contributing: Adding Packages

This monorepo ships two kinds of NuGet packages:

- **Third-party libraries** (`libraries/<Name>/`) — Swift libraries from SPM. Single-TFM (`net10.0-ios`). Built via `BuildLibrary` (xcframework + bindings + compile).
- **Apple system frameworks** (`apple-frameworks/<Name>/`) — Apple's first-party Swift frameworks (CryptoKit, WeatherKit, …). Multi-TFM (iOS / macOS / MacCatalyst / tvOS as supported). Built via `BuildAppleFramework` (single `dotnet build` — no xcframework step, no SPM checkout).

For deeper details on internals (build modes, two-pass dependency injection, the `spm-to-xcframework` pin, etc.), see `CLAUDE.md` at the repo root.

## Scaffolding

```bash
# Single-product third-party library
./scripts/new-library.sh Nuke \
  --repo https://github.com/kean/Nuke.git --version 12.8.0 \
  --mode source --scheme Nuke

# Multi-product vendor (vendor-prefixed NuGet packages: SwiftBindings.Stripe.Core, …)
./scripts/new-library.sh Stripe \
  --repo https://github.com/stripe/stripe-ios.git --version 25.6.2 \
  --mode source --vendor Stripe \
  --products StripeCore,StripePayments,StripePaymentSheet \
  [--internal Stripe3DS2,StripeCameraCore]

# Discover available SPM products before scaffolding
./scripts/new-library.sh --discover https://github.com/stripe/stripe-ios.git

# Test app
./scripts/new-test.sh Nuke
./scripts/new-test.sh Stripe --all-products
./scripts/new-test.sh BlinkIDUX --with BlinkID         # cross-repo dep
```

`--vendor` only affects csproj filename and `PackageId` (e.g. `StripeCore` → `SwiftBindings.Stripe.Core`). Swift module names, framework filenames, and anything that feeds binding generation stay untouched. Every listed product must start with the vendor prefix or scaffolding fails.

Apple frameworks have no scaffolding script. Copy a sibling under `apple-frameworks/<Name>/` and edit the `<TargetFrameworks>` list, `PackageId`, and `<SwiftAppleFrameworkTarget>`. Drop TFMs the framework doesn't support (e.g. `RoomPlan` is iOS-only).

## Directory Layout

```
libraries/Nuke/                                    # single-package
├── library.json
├── SwiftBindings.Nuke.csproj
└── README.md

libraries/Stripe/                                  # multi-package vendor
├── library.json
├── StripeCore/SwiftBindings.Stripe.Core.csproj
├── StripePayments/SwiftBindings.Stripe.Payments.csproj
└── ...

libraries/BlinkID/                                 # dependent pair
├── library.json
├── BlinkID/SwiftBindings.BlinkID.csproj
└── BlinkIDUX/SwiftBindings.BlinkIDUX.csproj      # ProjectReference → BlinkID
```

`ProjectReference` between sibling csprojs becomes a `PackageReference` when packed — consumers of `SwiftBindings.BlinkIDUX` get `SwiftBindings.BlinkID` transitively.

## Build & Test

```bash
# Third-party library: end-to-end (xcframework + dependency injection + dotnet build)
dotnet nuke BuildLibrary --library Stripe --all-products

# Apple framework
dotnet nuke BuildAppleFramework --library CryptoKit

# Test app + validate
dotnet nuke BuildTestApp --library Nuke
dotnet nuke BootSim
dotnet nuke ValidateSim --library Nuke --timeout 30

# Other validators (Apple frameworks): ValidateMac, ValidateMacCatalyst (tvOS is build-only)
dotnet nuke BuildTestApp --library CryptoKit --platform macos
dotnet nuke ValidateMac --library CryptoKit --timeout 30

# Physical device — NativeAOT requires CODESIGN_IDENTITY/PROVISIONING_PROFILE/TEAM_ID env vars
dotnet nuke BuildTestApp --library Nuke --device --aot
dotnet nuke ValidateDevice --library Nuke --aot --timeout 30

# Pre-release matrix across every TFM each test csproj declares
dotnet nuke RegressionValidate --version 0.9.0
```

Test apps watch stdout for `TEST SUCCESS` and detect crashes automatically. Apple framework test apps are multi-TFM: `Tests.cs` holds shared methods, `Program.UIKit.cs` is the iOS/MacCatalyst/tvOS entrypoint, `Program.MacConsole.cs` is the macOS console entrypoint. To add a test, edit `Tests.cs` (or the third-party library's single `Program.cs`) — call your method from `RunLibraryTests()`, log via `TestLogger`, record via `TestResults.Pass()` / `.Fail()`.

## Gotchas

- **ObjC-only frameworks** (no Swift module) must NOT be listed as `<SwiftFrameworkDependency>` — the generator silently produces no output. Use `NativeReference` only.
- **Multi-product two-pass build**: `BuildLibrary` chains xcframework build → `InjectFrameworkDeps` → pass-1 `dotnet build` (wrapper compile may fail; expected) → `InjectProjectRefs` (reads the freshly generated C# to detect cross-module references) → pass-2 `dotnet build`. If pass 2 still fails with missing types, the grep missed a reference that lives only in `*.Wrapper.swift` — hand-author the `ProjectReference` outside the auto-block.
- **Zip-mode version bumps** (currently only Stripe): wipe `bin/` + `obj/` under the affected library before re-running `BuildTestApp`. MSBuild's incremental copy of `<NativeReference>` slices into `.app/Frameworks/` can miss the change, leaving stale frameworks that fail at dyld load.
- **Don't pin `SwiftBindings.Runtime`** in csproj — it's resolved transitively via the SDK.

## NuGet Naming

| Pattern | Example |
|---|---|
| Single library | `SwiftBindings.Nuke` |
| Vendor group | `SwiftBindings.Stripe.Core`, `SwiftBindings.Stripe.Payments` |
| Dependent pair | `SwiftBindings.BlinkID`, `SwiftBindings.BlinkIDUX` |
| Apple framework | `SwiftBindings.Apple.CryptoKit` |
