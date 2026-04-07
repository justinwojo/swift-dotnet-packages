# SwiftBindings Packages

> **Work in progress.** Published NuGet packages and sample apps are coming soon — the infrastructure and initial libraries are functional but expect rough edges.

Native Swift interop bindings for popular Swift libraries on .NET. Each package uses the [SwiftBindings SDK](https://www.nuget.org/packages/SwiftBindings.Sdk), built from the [swift-dotnet-bindings](https://github.com/justinwojo/swift-dotnet-bindings) project, to auto-generate C# bindings from Swift xcframeworks, with every library validated end-to-end on iOS Simulator and physical devices.

These are not traditional Objective-C proxy wrappers — they use .NET 10's native Swift interop for direct, high-performance calls into Swift APIs from C#.

## Libraries

| Package | Description | Version | Mode |
|---------|-------------|---------|------|
| `SwiftBindings.Nuke` | High-performance image loading and caching | 12.8.0 | Source |
| `SwiftBindings.Lottie` | Lottie animation rendering | 4.6.0 | Source |
| `SwiftBindings.BlinkID` | Identity document scanning (Microblink) | 7.6.2 | Binary |
| `SwiftBindings.BlinkIDUX` | BlinkID scanning UI components | 7.6.2 | Source |
| `SwiftBindings.Stripe.*` | Stripe payments SDK (12 packages) | 25.6.2 | Source |

Stripe packages: Stripe, Core, Payments, PaymentSheet, PaymentsUI, UICore, ApplePay, Identity, Issuing, CardScan, FinancialConnections, Connect.

## Sample App

The [`samples/SwiftBindingsSamples`](samples/SwiftBindingsSamples) directory contains a .NET for iOS app demonstrating real-world usage of the bindings:

- **Nuke** — concurrent image loading grid with cache management and pipeline configuration
- **Lottie** — animation playback with play/pause/stop, speed control, loop mode toggle, and frame scrubbing

## How It Works

Each library follows the same workflow:

1. **Build xcframeworks** — fetch the Swift library via SPM and build device + simulator xcframeworks
2. **Generate bindings** — `dotnet build` invokes the SwiftBindings SDK, which reads the xcframework and emits C# bindings + a Swift interop wrapper
3. **Validate** — a .NET for iOS test app exercises the bindings on iOS Simulator and physical devices, verifying that Swift APIs are callable end-to-end from C#

## Quick Start

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

## Adding Libraries

See [CONTRIBUTING.md](CONTRIBUTING.md) for the full guide on scaffolding new libraries, configuring multi-product vendors, and writing simulator tests.

## License

MIT
