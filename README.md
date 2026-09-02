# SwiftBindings Packages

Native Swift interop bindings for popular Swift libraries on .NET. Each package uses the [SwiftBindings SDK](https://www.nuget.org/packages/SwiftBindings.Sdk), to auto-generate C# bindings from Swift xcframeworks, with every library validated end-to-end on iOS Simulator and physical devices.

## Apple frameworks

Bindings against the system-installed Apple SDKs. Multi-TFM: `net10.0-ios26.2`, `net10.0-macos26.2`, `net10.0-maccatalyst26.2`, `net10.0-tvos26.2` (TFMs available depend on the framework). See [Apple framework support](#apple-framework-support) below for build-host requirements.

| Package | Description | Version | Guide |
|---|---|---|---|
| [`SwiftBindings.Apple.ActivityKit`](apple-frameworks/ActivityKit/README.md) | Live Activities and the Dynamic Island — start/update/end from C# (lock-screen card + JSON payload; SwiftUI widget renders it) | [![NuGet](https://img.shields.io/nuget/v/SwiftBindings.Apple.ActivityKit.svg)](https://www.nuget.org/packages/SwiftBindings.Apple.ActivityKit) | [wiki](https://github.com/justinwojo/swift-dotnet-packages/wiki/ActivityKit) |
| [`SwiftBindings.Apple.CryptoKit`](apple-frameworks/CryptoKit/README.md) | Hashing, symmetric/authenticated encryption, key agreement, signatures | [![NuGet](https://img.shields.io/nuget/v/SwiftBindings.Apple.CryptoKit.svg)](https://www.nuget.org/packages/SwiftBindings.Apple.CryptoKit) | [wiki](https://github.com/justinwojo/swift-dotnet-packages/wiki/CryptoKit) |
| [`SwiftBindings.Apple.FamilyControls`](apple-frameworks/FamilyControls/README.md) | Screen Time authorization for parental-control apps | [![NuGet](https://img.shields.io/nuget/v/SwiftBindings.Apple.FamilyControls.svg)](https://www.nuget.org/packages/SwiftBindings.Apple.FamilyControls) | [wiki](https://github.com/justinwojo/swift-dotnet-packages/wiki/FamilyControls) |
| [`SwiftBindings.Apple.LiveCommunicationKit`](apple-frameworks/LiveCommunicationKit/README.md) | VoIP and Live Communication conversations | [![NuGet](https://img.shields.io/nuget/v/SwiftBindings.Apple.LiveCommunicationKit.svg)](https://www.nuget.org/packages/SwiftBindings.Apple.LiveCommunicationKit) | [wiki](https://github.com/justinwojo/swift-dotnet-packages/wiki/LiveCommunicationKit) |
| [`SwiftBindings.Apple.Matter`](apple-frameworks/Matter/README.md) | Apple's Matter smart-home protocol client surface (`MTRSetupPayload`, `MTRDeviceController`) | [![NuGet](https://img.shields.io/nuget/v/SwiftBindings.Apple.Matter.svg)](https://www.nuget.org/packages/SwiftBindings.Apple.Matter) | [wiki](https://github.com/justinwojo/swift-dotnet-packages/wiki/Matter) |
| [`SwiftBindings.Apple.MatterSupport`](apple-frameworks/MatterSupport/README.md) | `MatterAddDeviceRequest` flow layered on top of Matter | [![NuGet](https://img.shields.io/nuget/v/SwiftBindings.Apple.MatterSupport.svg)](https://www.nuget.org/packages/SwiftBindings.Apple.MatterSupport) | [wiki](https://github.com/justinwojo/swift-dotnet-packages/wiki/MatterSupport) |
| [`SwiftBindings.Apple.MusicKit`](apple-frameworks/MusicKit/README.md) | Apple Music catalog, library access, and playback | [![NuGet](https://img.shields.io/nuget/v/SwiftBindings.Apple.MusicKit.svg)](https://www.nuget.org/packages/SwiftBindings.Apple.MusicKit) | [wiki](https://github.com/justinwojo/swift-dotnet-packages/wiki/MusicKit) |
| [`SwiftBindings.Apple.ProximityReader`](apple-frameworks/ProximityReader/README.md) | Tap to Pay on iPhone, contactless payment-card and NFC reads | [![NuGet](https://img.shields.io/nuget/v/SwiftBindings.Apple.ProximityReader.svg)](https://www.nuget.org/packages/SwiftBindings.Apple.ProximityReader) | [wiki](https://github.com/justinwojo/swift-dotnet-packages/wiki/ProximityReader) |
| [`SwiftBindings.Apple.RealityFoundation`](apple-frameworks/RealityFoundation/README.md) | Entity / component / scene foundation re-exported by RealityKit (`Entity`, `ModelEntity`, transforms, materials) | [![NuGet](https://img.shields.io/nuget/v/SwiftBindings.Apple.RealityFoundation.svg)](https://www.nuget.org/packages/SwiftBindings.Apple.RealityFoundation) | [wiki](https://github.com/justinwojo/swift-dotnet-packages/wiki/RealityFoundation) |
| [`SwiftBindings.Apple.RealityKit`](apple-frameworks/RealityKit/README.md) | High-level 3D rendering, simulation, and AR composition (`ARView`, gesture recognizers, render options) | [![NuGet](https://img.shields.io/nuget/v/SwiftBindings.Apple.RealityKit.svg)](https://www.nuget.org/packages/SwiftBindings.Apple.RealityKit) | [wiki](https://github.com/justinwojo/swift-dotnet-packages/wiki/RealityKit) |
| [`SwiftBindings.Apple.RoomPlan`](apple-frameworks/RoomPlan/README.md) | LiDAR-based 3D room capture and floorplan reconstruction | [![NuGet](https://img.shields.io/nuget/v/SwiftBindings.Apple.RoomPlan.svg)](https://www.nuget.org/packages/SwiftBindings.Apple.RoomPlan) | [wiki](https://github.com/justinwojo/swift-dotnet-packages/wiki/RoomPlan) |
| [`SwiftBindings.Apple.StoreKit2`](apple-frameworks/StoreKit2/README.md) | Swift-first In-App Purchase API | [![NuGet](https://img.shields.io/nuget/v/SwiftBindings.Apple.StoreKit2.svg)](https://www.nuget.org/packages/SwiftBindings.Apple.StoreKit2) | [wiki](https://github.com/justinwojo/swift-dotnet-packages/wiki/StoreKit2) |
| [`SwiftBindings.Apple.TipKit`](apple-frameworks/TipKit/README.md) | In-app tips for surfacing app features (see README for `@_alwaysEmitIntoClient` DSL caveat) | [![NuGet](https://img.shields.io/nuget/v/SwiftBindings.Apple.TipKit.svg)](https://www.nuget.org/packages/SwiftBindings.Apple.TipKit) | [wiki](https://github.com/justinwojo/swift-dotnet-packages/wiki/TipKit) |
| [`SwiftBindings.Apple.Translation`](apple-frameworks/Translation/README.md) | On-device language translation | [![NuGet](https://img.shields.io/nuget/v/SwiftBindings.Apple.Translation.svg)](https://www.nuget.org/packages/SwiftBindings.Apple.Translation) | [wiki](https://github.com/justinwojo/swift-dotnet-packages/wiki/Translation) |
| [`SwiftBindings.Apple.VisionKit`](apple-frameworks/VisionKit/README.md) | Live Text image analysis, subject lifting, and the barcode/text data scanner | [![NuGet](https://img.shields.io/nuget/v/SwiftBindings.Apple.VisionKit.svg)](https://www.nuget.org/packages/SwiftBindings.Apple.VisionKit) | [wiki](https://github.com/justinwojo/swift-dotnet-packages/wiki/VisionKit) |
| [`SwiftBindings.Apple.WeatherKit`](apple-frameworks/WeatherKit/README.md) | Current conditions, forecasts, alerts, historical weather | [![NuGet](https://img.shields.io/nuget/v/SwiftBindings.Apple.WeatherKit.svg)](https://www.nuget.org/packages/SwiftBindings.Apple.WeatherKit) | [wiki](https://github.com/justinwojo/swift-dotnet-packages/wiki/WeatherKit) |
| [`SwiftBindings.Apple.WorkoutKit`](apple-frameworks/WorkoutKit/README.md) | Custom workouts and Apple Watch scheduling (HealthKit writes deferred — see README) | [![NuGet](https://img.shields.io/nuget/v/SwiftBindings.Apple.WorkoutKit.svg)](https://www.nuget.org/packages/SwiftBindings.Apple.WorkoutKit) | [wiki](https://github.com/justinwojo/swift-dotnet-packages/wiki/WorkoutKit) |

> [`SwiftBindings.Apple.AppIntents`](apple-frameworks/AppIntents/README.md) is **not published**. AppIntents' value is OS-level Siri / Shortcuts / Spotlight integration, which requires authoring App Intent Swift structs and build-time metadata extraction from the consumer's Swift source — neither reachable from C#. The `AppEntity` value types that do bind are orphaned without an authored intent to consume them. See its README for the full reasoning.

## Third-party libraries

Bindings are generated against each vendor's official native frameworks. Most libraries (BlinkID, BlinkIDUX, Mappedin, MapLibre, Stripe, Facebook) use the prebuilt xcframeworks the vendor publishes with each release; Nuke, Lottie, and Kingfisher are compiled from upstream Swift Package Manager sources via [`spm-to-xcframework`](https://github.com/justinwojo/spm-to-xcframework).

> 📖 **Every third-party library has a full usage guide on the [wiki](https://github.com/justinwojo/swift-dotnet-packages/wiki)** — Swift→C# naming rules, setup, worked examples, and a per-API list of what is and isn't callable, verified against the generated bindings.

| Package | Description | Version | Guide |
|---|---|---|---|
| [`SwiftBindings.Nuke`](libraries/Nuke/README.md) | High-performance image loading and caching (multi-TFM: iOS, macOS, tvOS) | [![NuGet](https://img.shields.io/nuget/v/SwiftBindings.Nuke.svg)](https://www.nuget.org/packages/SwiftBindings.Nuke) | [wiki](https://github.com/justinwojo/swift-dotnet-packages/wiki/Nuke) |
| [`SwiftBindings.Lottie`](libraries/Lottie/README.md) | Lottie animation rendering | [![NuGet](https://img.shields.io/nuget/v/SwiftBindings.Lottie.svg)](https://www.nuget.org/packages/SwiftBindings.Lottie) | [wiki](https://github.com/justinwojo/swift-dotnet-packages/wiki/Lottie) |
| [`SwiftBindings.Kingfisher`](libraries/Kingfisher/README.md) | Image downloading and caching with the `KF` fluent builder | [![NuGet](https://img.shields.io/nuget/v/SwiftBindings.Kingfisher.svg)](https://www.nuget.org/packages/SwiftBindings.Kingfisher) | [wiki](https://github.com/justinwojo/swift-dotnet-packages/wiki/Kingfisher) |
| [`SwiftBindings.BlinkID`](libraries/BlinkID/README.md) | Microblink ID document scanning | [![NuGet](https://img.shields.io/nuget/v/SwiftBindings.BlinkID.svg)](https://www.nuget.org/packages/SwiftBindings.BlinkID) | [wiki](https://github.com/justinwojo/swift-dotnet-packages/wiki/BlinkID) |
| [`SwiftBindings.BlinkIDUX`](libraries/BlinkIDUX/README.md) | Drop-in scanning UX components for BlinkID | [![NuGet](https://img.shields.io/nuget/v/SwiftBindings.BlinkIDUX.svg)](https://www.nuget.org/packages/SwiftBindings.BlinkIDUX) | [wiki](https://github.com/justinwojo/swift-dotnet-packages/wiki/BlinkIDUX) |
| [`SwiftBindings.Mappedin`](libraries/Mappedin/README.md) | Indoor mapping and wayfinding | [![NuGet](https://img.shields.io/nuget/v/SwiftBindings.Mappedin.svg)](https://www.nuget.org/packages/SwiftBindings.Mappedin) | [wiki](https://github.com/justinwojo/swift-dotnet-packages/wiki/Mappedin) |
| [`SwiftBindings.MapLibre`](libraries/MapLibre/README.md) | Open-source vector maps (MapLibre Native iOS) | [![NuGet](https://img.shields.io/nuget/v/SwiftBindings.MapLibre.svg)](https://www.nuget.org/packages/SwiftBindings.MapLibre) | [wiki](https://github.com/justinwojo/swift-dotnet-packages/wiki/MapLibre) |

## Facebook

Bindings against the prebuilt xcframeworks shipped with each [facebook-ios-sdk](https://github.com/facebook/facebook-ios-sdk) release. Install the sub-package(s) you need — `Facebook.Core` is the common dependency the others build on.

> 📖 **[Full usage wiki guide →](https://github.com/justinwojo/swift-dotnet-packages/wiki/Facebook)** — one guide covers all Facebook packages: SDK initialization, App Events, Graph API, Login, Sharing, and Aggregated Event Measurement.

| Package | Description | Version |
|---|---|---|
| [`SwiftBindings.Facebook.Core`](libraries/Facebook/FBSDKCoreKit/README.md) | Facebook SDK core — initialization, App Events, Graph API | [![NuGet](https://img.shields.io/nuget/v/SwiftBindings.Facebook.Core.svg)](https://www.nuget.org/packages/SwiftBindings.Facebook.Core) |
| [`SwiftBindings.Facebook.Login`](libraries/Facebook/FBSDKLoginKit/README.md) | Facebook Login | [![NuGet](https://img.shields.io/nuget/v/SwiftBindings.Facebook.Login.svg)](https://www.nuget.org/packages/SwiftBindings.Facebook.Login) |
| [`SwiftBindings.Facebook.Share`](libraries/Facebook/FBSDKShareKit/README.md) | Sharing dialogs and share content | [![NuGet](https://img.shields.io/nuget/v/SwiftBindings.Facebook.Share.svg)](https://www.nuget.org/packages/SwiftBindings.Facebook.Share) |
| [`SwiftBindings.Facebook.AEM`](libraries/Facebook/FBAEMKit/README.md) | Aggregated Event Measurement | [![NuGet](https://img.shields.io/nuget/v/SwiftBindings.Facebook.AEM.svg)](https://www.nuget.org/packages/SwiftBindings.Facebook.AEM) |

**Transitive support package** — [`SwiftBindings.Facebook.CoreBasics`](libraries/Facebook/FBSDKCoreKit_Basics/README.md) (FBSDKCoreKit_Basics infrastructure) is pulled in automatically by the Facebook packages above; **do not install directly**.

## Stripe

Bindings against the prebuilt `Stripe.xcframework.zip` published with each [stripe-ios](https://github.com/stripe/stripe-ios) release. Most apps depend on a specific sub-package (`Stripe.PaymentSheet`, `Stripe.ApplePay`, etc.) rather than the umbrella.

> 📖 **[Full usage wiki guide →](https://github.com/justinwojo/swift-dotnet-packages/wiki/Stripe)** — one guide covers all Stripe sub-packages: PaymentSheet, card entry UI, Apple Pay, card scanning, Identity, Financial Connections, Issuing, and Connect.

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

**Transitive support packages** — pulled in automatically by the products above; **do not install directly**: [`SwiftBindings.Stripe.UICore`](libraries/Stripe/StripeUICore/README.md) (shared UI primitives), [`SwiftBindings.Stripe.ThreeDS2`](libraries/Stripe/Stripe3DS2/README.md) (3-D Secure 2), [`SwiftBindings.Stripe.CameraCore`](libraries/Stripe/StripeCameraCore/README.md) (camera-capture infrastructure), and [`SwiftBindings.Stripe.FinancialConnectionsLite`](libraries/Stripe/StripeFinancialConnectionsLite/README.md) (lightweight bank-payment flow that PaymentSheet links against from Stripe 26.7.0). These are SPI/native carriers with no standalone entry-point API; NuGet resolves them for you.

## Apple framework support

Apple-framework packages bind the system SDK directly. There is no SPM checkout and no upstream source pull — `dotnet build` runs the SwiftBindings SDK against the framework already on disk in the active Xcode toolchain.

**Build host requirements:**

- macOS host with Xcode 26.3 or later (for the iOS 26.2 / macOS 26.2 / Mac Catalyst 26.2 / tvOS 26.2 SDKs).
- .NET SDK 10.0+ (pinned at `10.0.103` in `global.json`).
- The `SwiftBindings.Apple` supplement package (currently `26.2.8`) is pulled in transitively — do not pin it directly.

**Consumer requirements** (apps that install these NuGets):

- Application targets must be on `net10.0-ios26.2` (or the matching macOS / Mac Catalyst / tvOS TFM) or later — the packages are packed at `lib/net10.0-ios26.2/`, so an app whose TFM platform version is below 26.2 (an explicit lower TPV, or a bare `net10.0-ios` whose installed workload defaults below 26.2) fails NuGet restore with `NU1202`. This TFM is the compile-SDK pin, **not** your app's deployment floor — set that independently with `<SupportedOSPlatformVersion>`.

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
