# SwiftBindings Packages

Native Swift interop bindings for popular Swift libraries on .NET. Each package uses the [SwiftBindings SDK](https://www.nuget.org/packages/SwiftBindings.Sdk) to auto-generate C# bindings from Swift xcframeworks, with every library validated end-to-end on an iOS Simulator.

These are not traditional Objective-C proxy wrappers — they use .NET 10's native Swift interop for direct, high-performance calls into Swift APIs from C#.

## Libraries

| Package | Description | Version | Mode |
|---------|-------------|---------|------|
| `SwiftBindings.Nuke` | High-performance image loading and caching | 12.8.0 | Source |
| `SwiftBindings.Lottie` | Lottie animation rendering | 4.6.0 | Source |
| `SwiftBindings.BlinkID` | Identity document scanning (Microblink) | 7.6.2 | Binary |
| `SwiftBindings.BlinkIDUX` | BlinkID scanning UI components | 7.6.2 | Source |
| `SwiftBindings.Stripe.*` | Stripe payments SDK (11 products) | 25.6.2 | Source |

Stripe products: Core, Payments, PaymentSheet, PaymentsUI, ApplePay, Identity, Issuing, CardScan, FinancialConnections, Connect.

## How It Works

Each library follows the same workflow:

1. **Build xcframeworks** — fetch the Swift library via SPM and build device + simulator xcframeworks
2. **Generate bindings** — `dotnet build` invokes the SwiftBindings SDK, which reads the xcframework and emits C# bindings + a Swift interop wrapper
3. **Validate on simulator** — a .NET for iOS test app exercises the bindings on an iOS Simulator, verifying that Swift APIs are callable end-to-end from C#

## Quick Start

```bash
# Build a library's xcframework
cd libraries/Nuke && ./build-xcframework.sh

# Generate bindings and compile (SDK handles generation automatically)
dotnet build

# Run simulator tests
cd ../../tests/Nuke.SimTests
./build-testapp.sh
xcrun simctl boot <device-udid>
./validate-sim.sh 15
```

## Adding Libraries

See [CONTRIBUTING.md](CONTRIBUTING.md) for the full guide on scaffolding new libraries, configuring multi-product vendors, and writing simulator tests.

## License

MIT
