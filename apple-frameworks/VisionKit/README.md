# SwiftBindings.Apple.VisionKit

Native .NET bindings for Apple's [VisionKit](https://developer.apple.com/documentation/visionkit) framework — Live Text image analysis, subject lifting, and the `DataScannerViewController` camera scanner for barcodes and on-screen text. These are not Objective-C proxy wrappers; they use .NET 10's native Swift interop for direct calls into Swift APIs from C#.

> 📖 **[Full usage guide →](https://github.com/justinwojo/swift-dotnet-packages/wiki/VisionKit)** — Swift→C# naming, the analyzer/analysis lifecycle, the data-scanner delegate flow, and interaction types.

## Installation

```
dotnet add package SwiftBindings.Apple.VisionKit
```

## Requirements

- .NET 10.0+
- **Target framework: `net10.0-ios26.2` (or `-maccatalyst26.2` / `-macos26.2`) or higher.** The package is compiled against the 26.2 SDK supplement, so your app's `<TargetFramework>` platform version must be **≥ 26.2** to restore it — an explicit lower TPV (e.g. `net10.0-ios18.0`) or a bare `net10.0-ios` whose installed workload defaults below 26.2 fails restore with `NU1202`. This is the compile-SDK pin, **not** your app's deployment minimum.
- **Deployment minimum is separate** — set your app's real minimum OS with `<SupportedOSPlatformVersion>` (independent of the TFM above). VisionKit's members carry generated `[SupportedOSPlatform]` attributes (`ImageAnalyzer` needs iOS 16 / macOS 13 / Mac Catalyst 17, `DataScannerViewController` iOS 16), and the C# compiler flags a call that isn't valid for your target.
- macOS host for development

## Usage

### Analyze an image for text and machine-readable codes (iOS / Mac Catalyst)

```csharp
using VisionKit;

using var analyzer = new ImageAnalyzer();
using var configuration = new ImageAnalyzer.Configuration(
    ImageAnalyzer.AnalysisTypes.Text | ImageAnalyzer.AnalysisTypes.MachineReadableCode);
configuration.Locales = new[] { "en-US" };

using var analysis = await analyzer.AnalyzeAsync(image, configuration);
if (analysis.HasResults(ImageAnalyzer.AnalysisTypes.Text))
    Console.WriteLine(analysis.Transcript);
```

The two-argument form above is the `UIImage` overload, so it compiles on iOS and Mac Catalyst only. `AnalyzeAsync` also has overloads for `NSImage` (macOS), `CGImage`, `CIImage`, and an `NSUrl` pointing at an image file — each of those takes a `CGImagePropertyOrientation` before the configuration:

```csharp
using var analysis = await analyzer.AnalyzeAsync(cgImage, CGImagePropertyOrientation.Up, configuration);
```

Each maps to a Swift `async throws` method, so a framework-side failure surfaces as a faulted `Task`.

### Attach Live Text to an image view (iOS / Mac Catalyst)

```csharp
using VisionKit;

var interaction = new ImageAnalysisInteraction
{
    Analysis = analysis,
    PreferredInteractionTypes = ImageAnalysisInteraction.InteractionTypes.Automatic,
};

// `subjects` is an async accessor in Swift; the binding surfaces it as a Get…Async method.
var subjects = await interaction.GetSubjectsAsync();
```

On macOS the equivalent type is `ImageAnalysisOverlayView`.

### Configure the data scanner (iOS)

The `DataScannerViewController` initializer takes a Swift `Set<RecognizedDataType>`, and building
that set from a populated `HashSet` marshals each element through Swift's `Set.insert`. The
bindings runtime (0.19.3+) routes that call through a plain-C shim rather than a direct
`CallConvSwift` P/Invoke, because Mono's JIT mis-handles the insert's `(Bool, @out)` tuple return
for struct elements. This package's test suite constructs the controller with a populated set on
the iOS Simulator, and the runtime's own test suite exercises the same insert path on a physical
device under NativeAOT.

```csharp
using Vision;
using VisionKit;

using var barcodes = DataScannerViewController.RecognizedDataType.Barcode(
    new[] { VNBarcodeSymbology.Ean13, VNBarcodeSymbology.Code128 });
using var text = DataScannerViewController.RecognizedDataType.Text(new[] { "en-US" });

if (DataScannerViewController.IsSupported && DataScannerViewController.IsAvailable)
{
    using var scanner = new DataScannerViewController(
        recognizedDataTypes: new HashSet<DataScannerViewController.RecognizedDataType> { barcodes, text },
        qualityLevel: DataScannerViewController.QualityLevelKind.Balanced,
        recognizesMultipleItems: true,
        isHighFrameRateTrackingEnabled: true,
        isPinchToZoomEnabled: true,
        isGuidanceEnabled: true,
        isHighlightingEnabled: true);
    scanner.StartScanning();
}
```

`RecognizedDataType.GetBarcode()` / `GetText()` are the unparameterized cases. Scanned results arrive as `RecognizedItem.Text` / `RecognizedItem.Barcode` values through `IDataScannerViewControllerDelegate`. Present and retain the controller (it is a `UIViewController`) before calling `StartScanning`, and add an `NSCameraUsageDescription` string to your app's `Info.plist` — the scanner uses the camera, and iOS terminates an app that requests camera access without it. `DataScannerViewController` requires a real camera, so `IsSupported` is false on the simulator.

## Platform surface

The Swift interface differs per platform, so the generated surface does too:

| TFM | What binds |
|---|---|
| `net10.0-ios26.2` | Full surface — `ImageAnalyzer`, `ImageAnalysis`, `ImageAnalysisInteraction`, `DataScannerViewController` + `RecognizedItem` |
| `net10.0-maccatalyst26.2` | `ImageAnalyzer`, `ImageAnalysis`, `ImageAnalysisInteraction` (no `DataScannerViewController` — Apple does not vend it on Mac Catalyst) |
| `net10.0-macos26.2` | `ImageAnalyzer`, `ImageAnalysis`, `ImageAnalysisOverlayView` (AppKit equivalent of the interaction) |

`VNDocumentCameraViewController` is an Objective-C class, not part of the Swift module this package binds; use the existing .NET `VisionKit` platform bindings for it.

## Documentation

- [**Usage guide** (wiki)](https://github.com/justinwojo/swift-dotnet-packages/wiki/VisionKit) — full C# walkthrough of the binding surface
- [Apple VisionKit framework](https://developer.apple.com/documentation/visionkit)
- [Enabling Live Text interactions with images](https://developer.apple.com/documentation/visionkit/enabling-live-text-interactions-with-images)

## How It Works

These bindings are auto-generated by [Swift Dotnet Bindings](https://github.com/justinwojo/swift-dotnet-bindings) using .NET 10's native Swift interop via the [SwiftBindings SDK](https://www.nuget.org/packages/SwiftBindings.Sdk).

## License

The bindings are MIT licensed. VisionKit is part of the Apple SDK; refer to Apple's licensing for the underlying framework.
