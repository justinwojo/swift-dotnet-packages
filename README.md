# SwiftBindings Packages

Native Swift interop bindings for popular Swift libraries on .NET. Each package uses the [SwiftBindings SDK](https://www.nuget.org/packages/SwiftBindings.Sdk), built from the [swift-dotnet-bindings](https://github.com/justinwojo/swift-dotnet-bindings) project, to auto-generate C# bindings from Swift xcframeworks, with every library validated end-to-end on iOS Simulator and physical devices.

These are not traditional Objective-C proxy wrappers — they use .NET 10's native Swift interop for direct, high-performance calls into Swift APIs from C#.

## Apple frameworks

Bindings against the system-installed Apple SDKs. They do **not** download or build Swift sources; they wrap the system framework and ship a `.xcframework` of generated `@_cdecl` thunks. Multi-TFM: `net10.0-ios26.2`, `net10.0-macos26.2`, `net10.0-maccatalyst26.2`, `net10.0-tvos26.2` (TFMs available depend on the framework). See [Apple framework support](#apple-framework-support) below for build-host requirements.

| Package | Description | Version |
|---|---|---|
| [`SwiftBindings.Apple.CryptoKit`](apple-frameworks/CryptoKit/README.md) | Hashing, symmetric/authenticated encryption, key agreement, signatures | [![NuGet](https://img.shields.io/nuget/v/SwiftBindings.Apple.CryptoKit.svg)](https://www.nuget.org/packages/SwiftBindings.Apple.CryptoKit) |
| [`SwiftBindings.Apple.FamilyControls`](apple-frameworks/FamilyControls/README.md) | Screen Time authorization for parental-control apps | [![NuGet](https://img.shields.io/nuget/v/SwiftBindings.Apple.FamilyControls.svg)](https://www.nuget.org/packages/SwiftBindings.Apple.FamilyControls) |
| [`SwiftBindings.Apple.LiveCommunicationKit`](apple-frameworks/LiveCommunicationKit/README.md) | VoIP and Live Communication conversations | [![NuGet](https://img.shields.io/nuget/v/SwiftBindings.Apple.LiveCommunicationKit.svg)](https://www.nuget.org/packages/SwiftBindings.Apple.LiveCommunicationKit) |
| [`SwiftBindings.Apple.MusicKit`](apple-frameworks/MusicKit/README.md) | Apple Music catalog, library access, and playback | [![NuGet](https://img.shields.io/nuget/v/SwiftBindings.Apple.MusicKit.svg)](https://www.nuget.org/packages/SwiftBindings.Apple.MusicKit) |
| [`SwiftBindings.Apple.ProximityReader`](apple-frameworks/ProximityReader/README.md) | Tap to Pay on iPhone, contactless payment-card and NFC reads | [![NuGet](https://img.shields.io/nuget/v/SwiftBindings.Apple.ProximityReader.svg)](https://www.nuget.org/packages/SwiftBindings.Apple.ProximityReader) |
| [`SwiftBindings.Apple.RealityFoundation`](apple-frameworks/RealityFoundation/README.md) | Entity / component / scene foundation re-exported by RealityKit (`Entity`, `ModelEntity`, transforms, materials) | [![NuGet](https://img.shields.io/nuget/v/SwiftBindings.Apple.RealityFoundation.svg)](https://www.nuget.org/packages/SwiftBindings.Apple.RealityFoundation) |
| [`SwiftBindings.Apple.RealityKit`](apple-frameworks/RealityKit/README.md) | High-level 3D rendering, simulation, and AR composition (`ARView`, gesture recognizers, render options) | [![NuGet](https://img.shields.io/nuget/v/SwiftBindings.Apple.RealityKit.svg)](https://www.nuget.org/packages/SwiftBindings.Apple.RealityKit) |
| [`SwiftBindings.Apple.RoomPlan`](apple-frameworks/RoomPlan/README.md) | LiDAR-based 3D room capture and floorplan reconstruction | [![NuGet](https://img.shields.io/nuget/v/SwiftBindings.Apple.RoomPlan.svg)](https://www.nuget.org/packages/SwiftBindings.Apple.RoomPlan) |
| [`SwiftBindings.Apple.StoreKit2`](apple-frameworks/StoreKit2/README.md) | Swift-first In-App Purchase API | [![NuGet](https://img.shields.io/nuget/v/SwiftBindings.Apple.StoreKit2.svg)](https://www.nuget.org/packages/SwiftBindings.Apple.StoreKit2) |
| [`SwiftBindings.Apple.TipKit`](apple-frameworks/TipKit/README.md) | In-app tips for surfacing app features (see README for `@_alwaysEmitIntoClient` DSL caveat) | [![NuGet](https://img.shields.io/nuget/v/SwiftBindings.Apple.TipKit.svg)](https://www.nuget.org/packages/SwiftBindings.Apple.TipKit) |
| [`SwiftBindings.Apple.Translation`](apple-frameworks/Translation/README.md) | On-device language translation | [![NuGet](https://img.shields.io/nuget/v/SwiftBindings.Apple.Translation.svg)](https://www.nuget.org/packages/SwiftBindings.Apple.Translation) |
| [`SwiftBindings.Apple.WeatherKit`](apple-frameworks/WeatherKit/README.md) | Current conditions, forecasts, alerts, historical weather | [![NuGet](https://img.shields.io/nuget/v/SwiftBindings.Apple.WeatherKit.svg)](https://www.nuget.org/packages/SwiftBindings.Apple.WeatherKit) |
| [`SwiftBindings.Apple.WorkoutKit`](apple-frameworks/WorkoutKit/README.md) | Custom workouts and Apple Watch scheduling (HealthKit writes deferred — see README) | [![NuGet](https://img.shields.io/nuget/v/SwiftBindings.Apple.WorkoutKit.svg)](https://www.nuget.org/packages/SwiftBindings.Apple.WorkoutKit) |

> [`SwiftBindings.Apple.ActivityKit`](apple-frameworks/ActivityKit/README.md) is **not published**. Live Activities require user-defined `Codable & Hashable` Swift types that the binding layer cannot synthesize from C#. See its README for the full reasoning.

## Third-party libraries

Built from upstream Swift Package Manager sources via [`spm-to-xcframework`](https://github.com/justinwojo/swift-dotnet-bindings).

| Package | Description | Version |
|---|---|---|
| [`SwiftBindings.Nuke`](libraries/Nuke/README.md) | High-performance image loading and caching | [![NuGet](https://img.shields.io/nuget/v/SwiftBindings.Nuke.svg)](https://www.nuget.org/packages/SwiftBindings.Nuke) |
| [`SwiftBindings.Lottie`](libraries/Lottie/README.md) | Lottie animation rendering | [![NuGet](https://img.shields.io/nuget/v/SwiftBindings.Lottie.svg)](https://www.nuget.org/packages/SwiftBindings.Lottie) |
| [`SwiftBindings.BlinkID`](libraries/BlinkID/README.md) | Microblink ID document scanning | _Coming soon_ |
| [`SwiftBindings.BlinkIDUX`](libraries/BlinkIDUX/README.md) | Drop-in scanning UX components for BlinkID | _Coming soon_ |
| [`SwiftBindings.Mappedin`](libraries/Mappedin/README.md) | Indoor mapping and wayfinding | _Coming soon_ |

## Stripe

Bindings against the prebuilt `Stripe.xcframework.zip` published with each [stripe-ios](https://github.com/stripe/stripe-ios) release. Most apps depend on a specific sub-package (`Stripe.PaymentSheet`, `Stripe.ApplePay`, etc.) rather than the umbrella.

| Package | Description | Version |
|---|---|---|
| [`SwiftBindings.Stripe`](libraries/Stripe/Stripe/README.md) | Umbrella module re-exporting the common payment APIs | [![NuGet](https://img.shields.io/nuget/v/SwiftBindings.Stripe.svg)](https://www.nuget.org/packages/SwiftBindings.Stripe) |
| [`SwiftBindings.Stripe.Core`](libraries/Stripe/StripeCore/README.md) | `STPAPIClient`, networking, shared types | [![NuGet](https://img.shields.io/nuget/v/SwiftBindings.Stripe.Core.svg)](https://www.nuget.org/packages/SwiftBindings.Stripe.Core) |
| [`SwiftBindings.Stripe.Payments`](libraries/Stripe/StripePayments/README.md) | `PaymentIntent`, `SetupIntent`, `PaymentMethod` flows | [![NuGet](https://img.shields.io/nuget/v/SwiftBindings.Stripe.Payments.svg)](https://www.nuget.org/packages/SwiftBindings.Stripe.Payments) |
| [`SwiftBindings.Stripe.PaymentsUI`](libraries/Stripe/StripePaymentsUI/README.md) | UI controls (`STPCardFormView`, `STPPaymentCardTextField`) | [![NuGet](https://img.shields.io/nuget/v/SwiftBindings.Stripe.PaymentsUI.svg)](https://www.nuget.org/packages/SwiftBindings.Stripe.PaymentsUI) |
| [`SwiftBindings.Stripe.PaymentSheet`](libraries/Stripe/StripePaymentSheet/README.md) | Drop-in payment UI with 3DS and Apple Pay | [![NuGet](https://img.shields.io/nuget/v/SwiftBindings.Stripe.PaymentSheet.svg)](https://www.nuget.org/packages/SwiftBindings.Stripe.PaymentSheet) |
| [`SwiftBindings.Stripe.ApplePay`](libraries/Stripe/StripeApplePay/README.md) | Lightweight Apple Pay integration | [![NuGet](https://img.shields.io/nuget/v/SwiftBindings.Stripe.ApplePay.svg)](https://www.nuget.org/packages/SwiftBindings.Stripe.ApplePay) |
| [`SwiftBindings.Stripe.Connect`](libraries/Stripe/StripeConnect/README.md) | Stripe Connect embedded components | [![NuGet](https://img.shields.io/nuget/v/SwiftBindings.Stripe.Connect.svg)](https://www.nuget.org/packages/SwiftBindings.Stripe.Connect) |
| [`SwiftBindings.Stripe.Identity`](libraries/Stripe/StripeIdentity/README.md) | Stripe Identity verification sheet | [![NuGet](https://img.shields.io/nuget/v/SwiftBindings.Stripe.Identity.svg)](https://www.nuget.org/packages/SwiftBindings.Stripe.Identity) |
| [`SwiftBindings.Stripe.Issuing`](libraries/Stripe/StripeIssuing/README.md) | Push-provisioning to Apple Wallet | [![NuGet](https://img.shields.io/nuget/v/SwiftBindings.Stripe.Issuing.svg)](https://www.nuget.org/packages/SwiftBindings.Stripe.Issuing) |
| [`SwiftBindings.Stripe.CardScan`](libraries/Stripe/StripeCardScan/README.md) | On-device card scanning | [![NuGet](https://img.shields.io/nuget/v/SwiftBindings.Stripe.CardScan.svg)](https://www.nuget.org/packages/SwiftBindings.Stripe.CardScan) |
| [`SwiftBindings.Stripe.FinancialConnections`](libraries/Stripe/StripeFinancialConnections/README.md) | Bank-account linking sheet | [![NuGet](https://img.shields.io/nuget/v/SwiftBindings.Stripe.FinancialConnections.svg)](https://www.nuget.org/packages/SwiftBindings.Stripe.FinancialConnections) |
| [`SwiftBindings.Stripe.UICore`](libraries/Stripe/StripeUICore/README.md) | Shared UI primitives consumed by sibling Stripe packages (do not use directly) | [![NuGet](https://img.shields.io/nuget/v/SwiftBindings.Stripe.UICore.svg)](https://www.nuget.org/packages/SwiftBindings.Stripe.UICore) |
| [`SwiftBindings.Stripe.ThreeDS2`](libraries/Stripe/Stripe3DS2/README.md) | 3-D Secure 2 transitive dependency for sibling Stripe packages (do not use directly) | [![NuGet](https://img.shields.io/nuget/v/SwiftBindings.Stripe.ThreeDS2.svg)](https://www.nuget.org/packages/SwiftBindings.Stripe.ThreeDS2) |
| [`SwiftBindings.Stripe.CameraCore`](libraries/Stripe/StripeCameraCore/README.md) | Camera-scanning transitive dependency for sibling Stripe packages (do not use directly) | [![NuGet](https://img.shields.io/nuget/v/SwiftBindings.Stripe.CameraCore.svg)](https://www.nuget.org/packages/SwiftBindings.Stripe.CameraCore) |

## Apple framework support

Apple-framework packages bind the system SDK directly. There is no SPM checkout and no upstream source pull — `dotnet build` runs the SwiftBindings SDK against the framework already on disk in the active Xcode toolchain.

**Build host requirements:**

- macOS host with Xcode 26.3 or later (for the iOS 26.2 / macOS 26.2 / Mac Catalyst 26.2 / tvOS 26.2 SDKs).
- .NET SDK 10.0+ (pinned at `10.0.103` in `global.json`).
- The `SwiftBindings.Apple` supplement package (currently `26.2.1`) is pulled in transitively — do not pin it directly.

**Consumer requirements** (apps that install these NuGets):

- Application targets must be on `net10.0-ios26.2` (or the matching macOS / Mac Catalyst / tvOS TFM) or later. Older TFMs are not supported because the bound APIs are only available against the iOS 26.2 SDK surface.

## Sample app

The [`samples/SwiftBindingsSamples`](samples/SwiftBindingsSamples) directory contains a .NET for iOS app demonstrating real-world usage:

- **Nuke** — concurrent image loading grid with cache management and pipeline configuration
- **Lottie** — animation playback with play/pause/stop, speed control, loop mode toggle, and frame scrubbing

More samples will be added over time.

## How it works

Each library follows the same workflow:

1. **Build xcframeworks** — fetch the Swift library via SPM (or download the prebuilt zip / use the system SDK) and produce device + simulator xcframeworks
2. **Generate bindings** — `dotnet build` invokes the SwiftBindings SDK, which reads the xcframework and emits C# bindings + a Swift interop wrapper
3. **Validate** — a .NET for iOS test app exercises the bindings on iOS Simulator and physical devices, verifying that Swift APIs are callable end-to-end from C#

## Quick start

```bash
# One-time: install the pinned Nuke CLI from .config/dotnet-tools.json
dotnet tool restore

# Build a library end-to-end (xcframework + dotnet build)
dotnet nuke BuildLibrary --library Nuke

# Run simulator tests
dotnet nuke BootSim
dotnet nuke BuildTestApp --library Nuke
dotnet nuke ValidateSim --library Nuke --timeout 15
```

All build/test/release orchestration runs through the Nuke harness — see `build/Build.*.cs` for the target definitions.

## Adding libraries

See [CONTRIBUTING.md](CONTRIBUTING.md) for the full guide on scaffolding new libraries, configuring multi-product vendors, and writing simulator tests.

## License

MIT — see [LICENSE](LICENSE). Per-vendor licensing for the upstream Swift libraries is documented in each package's README; vendor binaries are pulled at build time from upstream sources and are not redistributed in our `.nupkg`s.
