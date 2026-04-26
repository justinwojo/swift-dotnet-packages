# SwiftBindings Packages

Native Swift interop bindings for popular Swift libraries on .NET. Each package uses the [SwiftBindings SDK](https://www.nuget.org/packages/SwiftBindings.Sdk), built from the [swift-dotnet-bindings](https://github.com/justinwojo/swift-dotnet-bindings) project, to auto-generate C# bindings from Swift xcframeworks, with every library validated end-to-end on iOS Simulator and physical devices.

These are not traditional Objective-C proxy wrappers — they use .NET 10's native Swift interop for direct, high-performance calls into Swift APIs from C#.

## Apple frameworks

Bindings against the system-installed Apple SDKs. They do **not** download or build Swift sources; they wrap the system framework and ship a `.xcframework` of generated `@_cdecl` thunks. Multi-TFM: `net10.0-ios26.2`, `net10.0-macos26.2`, `net10.0-maccatalyst26.2`, `net10.0-tvos26.2` (TFMs available depend on the framework). See [Apple framework support](#apple-framework-support) below for build-host requirements.

| Package | Description | Version |
|---|---|---|
| [`SwiftBindings.CryptoKit`](apple-frameworks/CryptoKit/README.md) | Hashing, symmetric/authenticated encryption, key agreement, signatures | [![NuGet](https://img.shields.io/nuget/v/SwiftBindings.CryptoKit.svg)](https://www.nuget.org/packages/SwiftBindings.CryptoKit) |
| [`SwiftBindings.FamilyControls`](apple-frameworks/FamilyControls/README.md) | Screen Time authorization for parental-control apps | [![NuGet](https://img.shields.io/nuget/v/SwiftBindings.FamilyControls.svg)](https://www.nuget.org/packages/SwiftBindings.FamilyControls) |
| [`SwiftBindings.LiveCommunicationKit`](apple-frameworks/LiveCommunicationKit/README.md) | VoIP and Live Communication conversations | [![NuGet](https://img.shields.io/nuget/v/SwiftBindings.LiveCommunicationKit.svg)](https://www.nuget.org/packages/SwiftBindings.LiveCommunicationKit) |
| [`SwiftBindings.MusicKit`](apple-frameworks/MusicKit/README.md) | Apple Music catalog, library access, and playback | [![NuGet](https://img.shields.io/nuget/v/SwiftBindings.MusicKit.svg)](https://www.nuget.org/packages/SwiftBindings.MusicKit) |
| [`SwiftBindings.ProximityReader`](apple-frameworks/ProximityReader/README.md) | Tap to Pay on iPhone, contactless payment-card and NFC reads | [![NuGet](https://img.shields.io/nuget/v/SwiftBindings.ProximityReader.svg)](https://www.nuget.org/packages/SwiftBindings.ProximityReader) |
| [`SwiftBindings.RoomPlan`](apple-frameworks/RoomPlan/README.md) | LiDAR-based 3D room capture and floorplan reconstruction | [![NuGet](https://img.shields.io/nuget/v/SwiftBindings.RoomPlan.svg)](https://www.nuget.org/packages/SwiftBindings.RoomPlan) |
| [`SwiftBindings.StoreKit2`](apple-frameworks/StoreKit2/README.md) | Swift-first In-App Purchase API | [![NuGet](https://img.shields.io/nuget/v/SwiftBindings.StoreKit2.svg)](https://www.nuget.org/packages/SwiftBindings.StoreKit2) |
| [`SwiftBindings.TipKit`](apple-frameworks/TipKit/README.md) | In-app tips for surfacing app features (see README for `@_alwaysEmitIntoClient` DSL caveat) | [![NuGet](https://img.shields.io/nuget/v/SwiftBindings.TipKit.svg)](https://www.nuget.org/packages/SwiftBindings.TipKit) |
| [`SwiftBindings.Translation`](apple-frameworks/Translation/README.md) | On-device language translation | [![NuGet](https://img.shields.io/nuget/v/SwiftBindings.Translation.svg)](https://www.nuget.org/packages/SwiftBindings.Translation) |
| [`SwiftBindings.WeatherKit`](apple-frameworks/WeatherKit/README.md) | Current conditions, forecasts, alerts, historical weather | [![NuGet](https://img.shields.io/nuget/v/SwiftBindings.WeatherKit.svg)](https://www.nuget.org/packages/SwiftBindings.WeatherKit) |
| [`SwiftBindings.WorkoutKit`](apple-frameworks/WorkoutKit/README.md) | Custom workouts and Apple Watch scheduling (HealthKit writes deferred — see README) | [![NuGet](https://img.shields.io/nuget/v/SwiftBindings.WorkoutKit.svg)](https://www.nuget.org/packages/SwiftBindings.WorkoutKit) |

> [`SwiftBindings.ActivityKit`](apple-frameworks/ActivityKit/README.md) is **not published**. Live Activities require user-defined `Codable & Hashable` Swift types that the binding layer cannot synthesize from C#. See its README for the full reasoning.

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
| [`SwiftBindings.Stripe`](libraries/Stripe/Stripe/README.md) | Umbrella module re-exporting the common payment APIs | _Coming soon_ |
| [`SwiftBindings.Stripe.Core`](libraries/Stripe/StripeCore/README.md) | `STPAPIClient`, networking, shared types | _Coming soon_ |
| [`SwiftBindings.Stripe.Payments`](libraries/Stripe/StripePayments/README.md) | `PaymentIntent`, `SetupIntent`, `PaymentMethod` flows | _Coming soon_ |
| [`SwiftBindings.Stripe.PaymentsUI`](libraries/Stripe/StripePaymentsUI/README.md) | UI controls (`STPCardFormView`, `STPPaymentCardTextField`) | _Coming soon_ |
| [`SwiftBindings.Stripe.PaymentSheet`](libraries/Stripe/StripePaymentSheet/README.md) | Drop-in payment UI with 3DS and Apple Pay | _Coming soon_ |
| [`SwiftBindings.Stripe.ApplePay`](libraries/Stripe/StripeApplePay/README.md) | Lightweight Apple Pay integration | _Coming soon_ |
| [`SwiftBindings.Stripe.Connect`](libraries/Stripe/StripeConnect/README.md) | Stripe Connect embedded components | _Coming soon_ |
| [`SwiftBindings.Stripe.Identity`](libraries/Stripe/StripeIdentity/README.md) | Stripe Identity verification sheet | _Coming soon_ |
| [`SwiftBindings.Stripe.Issuing`](libraries/Stripe/StripeIssuing/README.md) | Push-provisioning to Apple Wallet | _Coming soon_ |
| [`SwiftBindings.Stripe.CardScan`](libraries/Stripe/StripeCardScan/README.md) | On-device card scanning | _Coming soon_ |
| [`SwiftBindings.Stripe.FinancialConnections`](libraries/Stripe/StripeFinancialConnections/README.md) | Bank-account linking sheet | _Coming soon_ |
| [`SwiftBindings.Stripe.UICore`](libraries/Stripe/StripeUICore/README.md) | Shared UI primitives consumed by sibling Stripe packages (do not use directly) | _Coming soon_ |

> Stripe publish is paused on a SwiftBindings SDK gap: the upstream `Stripe.xcframework.zip` includes two internal frameworks (`Stripe3DS2`, `StripeCameraCore`) that several public sub-packages link against but that we don't want to surface as standalone NuGets. The SDK has no way today to declare a build/runtime framework dependency without also emitting a NuGet `<PackageReference>`. See [`PRIVATE-FRAMEWORK-DEPENDENCIES.md`](PRIVATE-FRAMEWORK-DEPENDENCIES.md) for the full write-up and proposed `SwiftFrameworkPrivateDependency` design.

## Apple framework support

Apple-framework packages bind the system SDK directly. There is no SPM checkout and no upstream source pull — `dotnet build` runs the SwiftBindings SDK against the framework already on disk in the active Xcode toolchain.

**Build host requirements:**

- macOS host with Xcode 26.3 or later (for the iOS 26.2 / macOS 26.2 / Mac Catalyst 26.2 / tvOS 26.2 SDKs).
- .NET SDK 10.0+ (pinned at `10.0.103` in `global.json`).
- The `SwiftBindings.Apple` supplement package (currently `26.2.0`) is pulled in transitively — do not pin it directly.

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
