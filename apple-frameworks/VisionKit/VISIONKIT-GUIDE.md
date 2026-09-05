# VisionKit for .NET — Usage Guide

`SwiftBindings.Apple.VisionKit` exposes Apple's [VisionKit](https://developer.apple.com/documentation/visionkit) framework to C# through .NET 10's native Swift interop — direct Swift calls, not Objective-C proxy wrappers. VisionKit is three features behind one module: **Live Text image analysis** (`ImageAnalyzer` → `ImageAnalysis`), **on-screen Live Text interaction and subject lifting** (`ImageAnalysisInteraction` on UIKit, `ImageAnalysisOverlayView` on AppKit), and the **live camera scanner** `DataScannerViewController` (iOS only).

The Swift module's contents differ per platform, so the generated C# does too — the [per-TFM surface](#per-tfm-surface) table is the authority on what exists where. This guide maps the Swift API to the exact C# names the generator emits, and is explicit about the members that are not reachable from C#.

## Contents

- [Requirements & install](#requirements--install)
- [Naming conventions](#naming-conventions)
- [Quick start: read the text in an image](#quick-start-read-the-text-in-an-image)
- [The analyzer](#the-analyzer)
- [The analysis result](#the-analysis-result)
- [Live Text on screen (iOS / Mac Catalyst)](#live-text-on-screen-ios--mac-catalyst)
- [Live Text on screen (macOS)](#live-text-on-screen-macos)
- [Subject lifting](#subject-lifting)
- [Scanning with the camera (iOS)](#scanning-with-the-camera-ios)
- [Per-TFM surface](#per-tfm-surface)
- [Known limitations](#known-limitations)
- [Memory & threading](#memory--threading)
- [Reference links](#reference-links)

## Requirements & install

- .NET 10.0+
- **Target framework: `net10.0-ios26.2` (or `-maccatalyst26.2` / `-macos26.2`) or higher.** The package is compiled against the 26.2 SDK supplement, so your app's `<TargetFramework>` platform version must be **≥ 26.2** to restore it — an explicit lower TPV (e.g. `net10.0-ios18.0`) or a bare `net10.0-ios` whose installed workload defaults below 26.2 fails restore with `NU1202`. This is the compile-SDK pin, **not** your app's deployment minimum.
- **Deployment minimum is separate** — set your app's real minimum OS with `<SupportedOSPlatformVersion>` (independent of the TFM above). Generated `[SupportedOSPlatform]` attributes gate the individual APIs, and the C# compiler flags a call that isn't valid for your target. The floors this module declares:

  | Type | iOS | macOS | Mac Catalyst |
  |---|---|---|---|
  | `ImageAnalyzer`, `ImageAnalysis` (+ `Configuration`, `AnalysisTypes`) | 16.0 | 13.0 | 17.0 |
  | `ImageAnalysisInteraction` (+ `InteractionTypes`, `Subject`) | 16.0 | — | 17.0 |
  | `ImageAnalysisOverlayView` (+ `InteractionTypes`, `Subject`) | — | 13.0 | — |
  | `DataScannerViewController`, `RecognizedItem` | 16.0 | — | — |

  A handful of members sit above their type's floor — the text-selection properties (`Text`, `SelectedText`, `SelectedAttributedText`, `SubjectUnavailable`) need iOS 17 / macOS 14, `TextContentType.Currency` needs iOS 17, and the whole macOS `MenuTag` group needs macOS 14. Each carries its own attribute.
- macOS host for development

```
dotnet add package SwiftBindings.Apple.VisionKit
```

```csharp
using VisionKit;
```

Two more `using`s show up in real code: `using ImageIO;` for `CGImagePropertyOrientation` (the orientation argument on most `AnalyzeAsync` overloads), and — on iOS — `using Vision;` for `VNBarcodeSymbology` and the `VNRecognizedTextObservation` / `VNBarcodeObservation` a scanned item carries.

## Naming conventions

The generator applies a few consistent transforms over the Swift names:

| Swift | C# | Rule |
|---|---|---|
| `func analyze(_:configuration:) async throws` | `AnalyzeAsync(UIImage, Configuration, CancellationToken)` | `async` methods gain an `Async` suffix and return `Task`/`Task<T>`; argument labels are dropped |
| `var subjects: Set<Subject> { get async }` | `GetSubjectsAsync(CancellationToken)` | an `async` *accessor* becomes a `Get…Async` **method**, not a property |
| `func capturePhoto() async throws -> UIImage` | `GetCapturePhotoAsync(CancellationToken)` | a no-argument `async` producer is also spelled `Get…Async` |
| `func subject(at:) async -> Subject?` | `SubjectMethodAsync(CGPoint, CancellationToken)` | a method whose name collides with a nested type (`Subject`) is suffixed `Method` |
| `func image(for:) async throws -> UIImage` | `ImageAsync(IReadOnlySet<Subject>, CancellationToken)` | otherwise the method name is just PascalCased |
| `func hasResults(for:)` | `HasResults(AnalysisTypes)` | a leading preposition label is dropped |
| `enum DataScannerViewController.QualityLevel` | `DataScannerViewController.QualityLevelKind` | a nested type colliding with a sibling property (`qualityLevel`) gains a `Kind` suffix |
| `struct RecognizedItem.Bounds` | `RecognizedItem.BoundsInfo` | same rule — the `bounds` property keeps `Bounds` |
| `struct AnalysisTypes : OptionSet` | `class AnalysisTypes` with `RawValue`, static options, `\|` `&` `^` `~`, `Contains` | option sets project to a class, not a `[Flags]` enum |
| `enum RecognizedItem { case text(Text) … }` | `class RecognizedItem` with `Tag`, `TryGetText`, `TryGetBarcode`, `TextValue(…)` | a payload-carrying Swift enum becomes a class with a `CaseTag` discriminator |
| `static func text(languages:textContentType:)` | `RecognizedDataType.Text(IEnumerable<string>, TextContentType?)` **and** `GetText()` | a static factory with all-defaulted arguments also gets a no-argument `Get…()` form |
| `case URL` | `TextContentType.Url` | enum cases are PascalCased |
| `throws` | `Swift.Runtime.SwiftException` | Swift errors surface as managed exceptions |
| `Foundation.UUID` | `System.Guid` | `RecognizedItem.Id` and friends |
| `Set<T>` | `IReadOnlySet<T>` (out) / `IReadOnlySet<T>` parameter (in) | any `HashSet<T>` satisfies the parameter |
| `AsyncStream<[RecognizedItem]>` | `IAsyncEnumerable<IReadOnlyList<RecognizedItem>>` | `await foreach` it |
| `@MainActor` | `[SwiftMainActor]` + a runtime main-thread assertion | see [Memory & threading](#memory--threading) |

Every `*Async` method also accepts a trailing `CancellationToken` (defaulted), so you can cancel an in-flight call.

## Quick start: read the text in an image

```csharp
using VisionKit;

using var analyzer = new ImageAnalyzer();
using var types = ImageAnalyzer.AnalysisTypes.Text | ImageAnalyzer.AnalysisTypes.MachineReadableCode;
using var configuration = new ImageAnalyzer.Configuration(types);
configuration.Locales = new[] { "en-US" };

using ImageAnalysis analysis = await analyzer.AnalyzeAsync(uiImage, configuration);

if (analysis.HasResults(ImageAnalyzer.AnalysisTypes.Text))
    Console.WriteLine(analysis.Transcript);
```

That is the whole Live Text read path: build an analyzer, describe what you want, await the analysis, read the transcript. Nothing here is main-thread-bound — `ImageAnalyzer` is `Sendable` and the analysis can run from any thread.

## The analyzer

### `ImageAnalyzer`

| Member | Signature | |
|---|---|---|
| ctor | `new ImageAnalyzer()` | no arguments |
| `IsSupported` | `static bool` | false where the device or region can't run Live Text |
| `SupportedTextRecognitionLanguages` | `static IReadOnlyList<string>` | BCP-47 language tags |
| `AnalyzeAsync` | five overloads (below) | `Task<ImageAnalysis>` |

The overload set is platform-dependent, because Swift's is:

| Overload | iOS | Mac Catalyst | macOS |
|---|:--:|:--:|:--:|
| `AnalyzeAsync(UIImage image, Configuration configuration, CancellationToken = default)` | ✅ | ✅ | — |
| `AnalyzeAsync(UIImage image, UIImageOrientation orientation, Configuration configuration, …)` | ✅ | ✅ | — |
| `AnalyzeAsync(NSImage image, CGImagePropertyOrientation orientation, Configuration configuration, …)` | — | — | ✅ |
| `AnalyzeAsync(CGImage image, CGImagePropertyOrientation orientation, Configuration configuration, …)` | ✅ | ✅ | ✅ |
| `AnalyzeAsync(CIImage image, CGImagePropertyOrientation orientation, Configuration configuration, …)` | ✅ | ✅ | ✅ |
| `AnalyzeAsync(NSUrl url, CGImagePropertyOrientation orientation, Configuration configuration, …)` | ✅ | ✅ | ✅ |

Note the orientation type: the `UIImage` overload takes `UIKit.UIImageOrientation`, every other one takes `ImageIO.CGImagePropertyOrientation`. The `NSUrl` overload maps Swift's `analyze(imageAt:orientation:configuration:)` and points at an image file.

```csharp
using ImageIO;

using var analysis = await analyzer.AnalyzeAsync(cgImage, CGImagePropertyOrientation.Up, configuration);
```

Swift's `CVPixelBuffer` overload is **not bound** — see [Known limitations](#known-limitations).

Each overload maps a Swift `async throws` method, so a framework-side failure surfaces as a faulted `Task` carrying a `Swift.Runtime.SwiftException`.

### `ImageAnalyzer.Configuration`

A Swift struct describing what to look for.

```csharp
public Configuration(ImageAnalyzer.AnalysisTypes types)
```

| Member | Type | |
|---|---|---|
| `AnalysisTypes` | `ImageAnalyzer.AnalysisTypes` (get/set) | what to analyze for |
| `Locales` | `IReadOnlyList<string>` (get/set) | preferred text-recognition locales; assign any `IEnumerable<string>` |

```csharp
using var config = new ImageAnalyzer.Configuration(ImageAnalyzer.AnalysisTypes.Text);
config.Locales = new[] { "en-US", "fr-FR" };
IReadOnlyList<string> current = config.Locales;
```

### `ImageAnalyzer.AnalysisTypes`

Swift's `OptionSet` projects to a class with static options and the bitwise operators, **not** a `[Flags]` enum:

| Option | |
|---|---|
| `Text` | Live Text recognition |
| `MachineReadableCode` | barcodes / QR codes |
| `VisualLookUp` | Visual Look Up subjects |

```csharp
using var types = ImageAnalyzer.AnalysisTypes.Text | ImageAnalyzer.AnalysisTypes.MachineReadableCode;
uint bits = types.RawValue;                                    // uint
bool wantsText = types.Contains(ImageAnalyzer.AnalysisTypes.Text);
using var custom = new ImageAnalyzer.AnalysisTypes(rawValue: 3); // ctor takes nuint
```

`&`, `^`, `~`, `==`/`!=` and `Equals` are all emitted. Note the asymmetry the generator produces: `RawValue` reads back as `uint`, while the raw-value constructor takes `nuint`.

## The analysis result

`ImageAnalysis` is a Swift class — a reference type, disposable, with two members:

| Member | Signature | |
|---|---|---|
| `Transcript` | `string` (get) | all recognized text, newline-separated |
| `HasResults` | `bool HasResults(ImageAnalyzer.AnalysisTypes analysisTypes)` | whether the analysis found anything of that kind |

An analysis is also the value you hand to an interaction or overlay view to light up Live Text on screen.

## Live Text on screen (iOS / Mac Catalyst)

`ImageAnalysisInteraction` is the UIKit half. It derives from `Foundation.NSObject` and is **`@MainActor`-isolated**: 34 of its members assert the main thread at runtime.

```csharp
var interaction = new ImageAnalysisInteraction();       // or: new ImageAnalysisInteraction(myDelegate)
interaction.Analysis = analysis;
interaction.PreferredInteractionTypes = ImageAnalysisInteraction.InteractionTypes.Automatic;
```

| Member | Type | |
|---|---|---|
| `Analysis` | `ImageAnalysis?` (get/set) | the analysis to present |
| `Delegate` | `IImageAnalysisInteractionDelegate?` (get/set) | see below |
| `View` | `UIView?` (get) | the view the interaction is attached to — read-only |
| `PreferredInteractionTypes` / `ActiveInteractionTypes` | `InteractionTypes` (get/set, get) | requested vs. currently live |
| `SelectableItemsHighlighted` | `bool` (get/set) | highlight state |
| `AllowLongPressForDataDetectorsInTextMode` | `bool` (get/set) | |
| `HasActiveTextSelection` | `bool` (get) | |
| `Text` / `SelectedText` / `SelectedAttributedText` | `string` / `string` / `AttributedString` (get) | iOS 17+ |
| `ContentsRect` | `CGRect` (get/set) | where the image lives inside the view |
| `LiveTextButtonVisible` | `bool` (get) | |
| `IsSupplementaryInterfaceHidden` | `bool` (get/set) | plus `SetSupplementaryInterfaceHidden(bool hidden, bool animated)` |
| `SupplementaryInterfaceContentInsets` | `UIEdgeInsets` (get/set) | |
| `SupplementaryInterfaceFont` | `UIFont?` (get/set) | |
| `HighlightedSubjects` | `IReadOnlySet<Subject>` (get/set) | see [Subject lifting](#subject-lifting) |
| `ResetTextSelection()` / `SetContentsRectNeedsUpdate()` | `void` | |
| `HasInteractiveItem(CGPoint)`, `HasText(CGPoint)`, `HasDataDetector(CGPoint)`, `HasSupplementaryInterface(CGPoint)`, `AnalysisHasText(CGPoint)` | `bool` | hit-testing |
| `WillMove(UIView?)` / `DidMove(UIView?)` | `void` | the `UIInteraction` lifecycle callbacks |

`InteractionTypes` is another option-set class: `Automatic`, `AutomaticTextOnly`, `TextSelection`, `DataDetectors`, `ImageSubject`, `VisualLookUp`.

> **Attaching it to a view needs an escape hatch.** Swift's `ImageAnalysisInteraction` conforms to `UIInteraction`, but that conformance is not in the generator's type database, so the emitted class does **not** implement .NET's `IUIInteraction` — `myImageView.AddInteraction(interaction)` does not compile. The underlying instance is a real `NSObject` that does conform natively, so Objective-C messaging (e.g. `imageView.PerformSelector(new ObjCRuntime.Selector("addInteraction:"), interaction, 0)`) is the available workaround; it is **not** exercised by this package's test app. Everything reachable without a view attachment — the analysis property, subjects, hit-testing — works directly.

### `IImageAnalysisInteractionDelegate`

Implement the interface and assign it (or pass it to the delegate-taking constructor). Members without a Swift protocol-extension default are abstract; the rest have a default body that **throws** `NotSupportedException`, so implement every callback you might actually receive rather than relying on the default:

```csharp
sealed class MyDelegate : IImageAnalysisInteractionDelegate
{
    public bool InteractionShouldBeginAtFor(ImageAnalysisInteraction interaction, CGPoint point,
        ImageAnalysisInteraction.InteractionTypes interactionType) => true;
    public CGRect ContentsRect(ImageAnalysisInteraction interaction) => new CGRect(0, 0, 1, 1);
    public UIView? ContentView(ImageAnalysisInteraction interaction) => null;
    public UIViewController? PresentingViewController(ImageAnalysisInteraction interaction) => null;
    public void InteractionLiveTextButtonDidChangeToVisible(ImageAnalysisInteraction interaction, bool visible) { }
    public void InteractionHighlightSelectedItemsDidChange(ImageAnalysisInteraction interaction, bool highlightSelectedItems) { }
    public void TextSelectionDidChange(ImageAnalysisInteraction interaction) { }   // iOS 17+
}
```

**Keep a strong reference to your implementation.** The `delegate` property is a Swift `weak var`, so nothing on the Swift side keeps your object alive; the binding's wrapper follows your implementation's lifetime rather than the other way round. Hold the implementation for as long as you want callbacks. If you drop it, the Swift slot clears and callbacks stop, and if VisionKit still calls through some other reference it holds, the call degrades quietly (a `void` callback is dropped) and the runtime raises `ProxyDegradation.ImplCollected` once so you can find out why your delegate went silent.

## Live Text on screen (macOS)

macOS gets `ImageAnalysisOverlayView` instead — an `AppKit.NSView` subclass, so it composes normally (`AddSubview`) with no escape hatch needed. It is also `@MainActor`-isolated (32 members).

```csharp
public ImageAnalysisOverlayView(CGRect frameRect)
public ImageAnalysisOverlayView(IImageAnalysisOverlayViewDelegate @delegate)
public static bool TryCreate(NSCoder coder, out ImageAnalysisOverlayView result)
```

The property set mirrors the interaction (`Analysis`, `Delegate`, `PreferredInteractionTypes`, `ActiveInteractionTypes`, `SelectableItemsHighlighted`, `HasActiveTextSelection`, `Text` / `SelectedText` / `SelectedAttributedText` (macOS 14+), `ContentsRect`, `LiveTextButtonVisible`, `IsSupplementaryInterfaceHidden`, `SupplementaryInterfaceFont`, `HighlightedSubjects`, the five `Has…(CGPoint)` probes, `SetSupplementaryInterfaceHidden`, `SetContentsRectNeedsUpdate`) with these AppKit-side differences:

- `TrackingImageView` (`NSImageView?`) instead of the UIKit `View`
- `ResetSelection()` instead of `ResetTextSelection()`
- `BeginSubjectAnalysisIfNecessary()` — no UIKit equivalent
- `ViewDidMoveToSuperview()` instead of `WillMove` / `DidMove`
- a nested `MenuTag` class exposing the `static int` tags `CopyImage`, `ShareImage`, `CopySubject`, `ShareSubject`, `LookupItem`, `RecommendedAppItems` (macOS 14+)
- **no** `SupplementaryInterfaceContentInsets` — see [Known limitations](#known-limitations)

`IImageAnalysisOverlayViewDelegate` carries the AppKit menu and key-event callbacks (`OverlayViewShouldHandleKeyDownEvent`, `OverlayViewShouldShowMenuForEventAtPoint`, `OverlayViewUpdatedMenuForForAt`, `OverlayViewNeedsUpdate`, `OverlayViewWillOpen`, `OverlayViewDidClose`, `OverlayViewMenuWillHighlight`) alongside the same content/selection callbacks as the UIKit protocol.

## Subject lifting

A `Subject` is a lifted foreground object. Both the interaction and the overlay view expose the same shape (each with its own nested `Subject` type):

| Member | Signature | |
|---|---|---|
| `GetSubjectsAsync` | `Task<IReadOnlySet<Subject>>` | Swift's `var subjects { get async }` |
| `HighlightedSubjects` | `IReadOnlySet<Subject>` (get/set) | which subjects are highlighted |
| `SubjectMethodAsync` | `Task<Subject?> SubjectMethodAsync(CGPoint point, …)` | the subject under a point |
| `ImageAsync` | `Task<UIImage>` / `Task<NSImage>` `ImageAsync(IReadOnlySet<Subject> subjects, …)` | composite cutout of the given subjects |
| `Subject.Bounds` | `CGRect` (get) | |
| `Subject.GetImageAsync` | `Task<UIImage>` / `Task<NSImage>` | that one subject's cutout |
| `SubjectUnavailable` | `enum : int { ImageUnavailable = 0 }` | thrown case (iOS 17 / macOS 14) |

```csharp
interaction.Analysis = analysis;                       // subjects only resolve with an analysis attached
IReadOnlySet<ImageAnalysisInteraction.Subject> subjects = await interaction.GetSubjectsAsync();

foreach (var subject in subjects)
{
    using var cutout = await subject.GetImageAsync();
    // …
}
```

`GetSubjectsAsync` is main-actor-isolated: `await` it from the UI thread so the continuation can resume on the main executor. The package's test app does exactly that and reports the subject count.

## Scanning with the camera (iOS)

`DataScannerViewController` is a `UIViewController` subclass that runs a live camera scan for text and machine-readable codes. **iOS only** — Apple does not vend it on Mac Catalyst or macOS. Every member is `@MainActor`-isolated (27 of them assert the main thread), including the constructor.

### `RecognizedDataType`

What to scan for. A Swift `Hashable` struct built through static factories:

```csharp
using Vision;
using VisionKit;

using var barcodes = DataScannerViewController.RecognizedDataType.Barcode(
    new[] { VNBarcodeSymbology.Ean13, VNBarcodeSymbology.Code128 });
using var anyBarcode = DataScannerViewController.RecognizedDataType.GetBarcode();     // all symbologies
using var localizedText = DataScannerViewController.RecognizedDataType.Text(new[] { "en-US" });
using var anyText = DataScannerViewController.RecognizedDataType.GetText();           // any language
```

| Factory | |
|---|---|
| `Barcode(IEnumerable<VNBarcodeSymbology> symbologies)` | restrict to the listed symbologies |
| `GetBarcode()` | Swift's `.barcode()` with its defaulted argument — any symbology |
| `Text(IEnumerable<string> languages)` | restrict to the listed BCP-47 languages |
| `Text(IEnumerable<string> languages, TextContentType? textContentType)` | also restrict to a content type |
| `GetText()` | Swift's `.text()` with both arguments defaulted |

`TextContentType` is a plain `enum : int`: `DateTimeDuration = 0`, `EmailAddress`, `FlightNumber`, `FullStreetAddress`, `ShipmentTrackingNumber`, `TelephoneNumber`, `Url`, `Currency = 7` (iOS 17+).

`VNBarcodeSymbology` comes from .NET's own `Vision` bindings. It is an `NS_STRING_ENUM` value type, and getting it across the ABI correctly is one of the two defects SDK 0.19.3 fixed — before that release the generated code referenced a `Handle` the value type does not have.

### Constructing the controller

```csharp
if (DataScannerViewController.IsSupported && DataScannerViewController.IsAvailable)
{
    using var scanner = new DataScannerViewController(
        recognizedDataTypes: new HashSet<DataScannerViewController.RecognizedDataType> { barcodes, localizedText },
        qualityLevel: DataScannerViewController.QualityLevelKind.Balanced,
        recognizesMultipleItems: true,
        isHighFrameRateTrackingEnabled: true,
        isPinchToZoomEnabled: true,
        isGuidanceEnabled: true,
        isHighlightingEnabled: true);

    scanner.Delegate = myDelegate;
    // present `scanner` (it is a UIViewController) before scanning
    scanner.StartScanning();
}
```

The full signature, with the defaults Swift applies:

```csharp
public DataScannerViewController(
    IReadOnlySet<DataScannerViewController.RecognizedDataType> recognizedDataTypes,
    DataScannerViewController.QualityLevelKind qualityLevel = QualityLevelKind.Balanced,
    bool recognizesMultipleItems = false,
    bool isHighFrameRateTrackingEnabled = true,
    bool isPinchToZoomEnabled = true,
    bool isGuidanceEnabled = true,
    bool isHighlightingEnabled = false)
```

`QualityLevelKind` is `enum : int { Balanced = 0, Fast = 1, Accurate = 2 }` — renamed from Swift's `QualityLevel` because the instance property already owns that name.

> **Building the `Set` used to corrupt memory.** Passing a *populated* `HashSet` marshals each struct element through Swift's `Set.insert`, whose `(Bool, @out)` tuple return Mono's JIT mishandles. Since SDK 0.19.3 the runtime routes that insert through a plain-C shim instead of a direct `CallConvSwift` P/Invoke. Both the empty and the populated form are exercised by this package's simulator test app, and the runtime's own suite covers the insert path on a physical device under NativeAOT. If you see this crash, you are on an older runtime.

| Member | Type | |
|---|---|---|
| `IsSupported` / `IsAvailable` | `static bool` | hardware capability / current availability (camera permission, Stage-Manager-style restrictions) |
| `SupportedTextRecognitionLanguages` | `static IReadOnlyList<string>` | |
| `RecognizedDataTypes` | `IReadOnlySet<RecognizedDataType>` (get/set) | |
| `QualityLevel` | `QualityLevelKind` (get/set) | |
| `RecognizesMultipleItems`, `IsHighFrameRateTrackingEnabled`, `IsPinchToZoomEnabled`, `IsGuidanceEnabled`, `IsHighlightingEnabled` | `bool` (get/set) | mirror the constructor arguments |
| `Delegate` | `IDataScannerViewControllerDelegate?` (get/set) | |
| `OverlayContainerView` | `UIView` (get) | add your own overlays here |
| `RegionOfInterest` | `CGRect?` (get/set) | restrict scanning to part of the frame |
| `IsScanning` | `bool` (get) | |
| `MinZoomFactor` / `MaxZoomFactor` / `ZoomFactor` | `double` (get, get, get/set) | writes are clamped to the capture device's range |
| `RecognizedItems` | `IAsyncEnumerable<IReadOnlyList<RecognizedItem>>` (get) | Swift's `AsyncStream` |
| `StartScanning()` | `void` — **throws** | `SwiftException` when scanning is unavailable |
| `StopScanning()` | `void` | safe no-op when not scanning |
| `GetCapturePhotoAsync(…)` | `Task<UIImage>` | still frame from the live scan |
| `LoadView()`, `ViewDidLoad()`, `ViewWillAppear(bool)`, `ViewDidDisappear(bool)`, `RemoveFromParent()` | `void` | the overridden `UIViewController` hooks |

`StartScanning` maps a Swift `throws` function; when the scanner can't run it raises `DataScannerViewController.ScanningUnavailable` (`enum : int { Unsupported = 0, CameraRestricted = 1 }`), which reaches you as a `SwiftException` whose message names the case:

```csharp
using Swift.Runtime;

try { scanner.StartScanning(); }
catch (SwiftException ex) { Console.WriteLine($"scanner unavailable: {ex.Message}"); }
```

### Reading results

Two ways in. The stream:

```csharp
await foreach (IReadOnlyList<RecognizedItem> items in scanner.RecognizedItems)
{
    foreach (var item in items)
        Handle(item);
}
```

Fetching `RecognizedItems` starts a native producer, so dispose it (`(items as IDisposable)?.Dispose()`) if you fetch it without enumerating. Or the delegate:

```csharp
sealed class ScannerDelegate : IDataScannerViewControllerDelegate
{
    public void DataScannerDidZoom(DataScannerViewController dataScanner) { }
    public void DataScannerDidTapOn(DataScannerViewController dataScanner, RecognizedItem item) => Handle(item);
    public void DataScannerDidAddAllItems(DataScannerViewController s, IEnumerable<RecognizedItem> added, IEnumerable<RecognizedItem> all) { }
    public void DataScannerDidUpdateAllItems(DataScannerViewController s, IEnumerable<RecognizedItem> updated, IEnumerable<RecognizedItem> all) { }
    public void DataScannerDidRemoveAllItems(DataScannerViewController s, IEnumerable<RecognizedItem> removed, IEnumerable<RecognizedItem> all) { }
    public void DataScannerBecameUnavailableWithError(DataScannerViewController s,
        DataScannerViewController.ScanningUnavailable error) { }
}

scanner.Delegate = new ScannerDelegate();   // keep a strong reference to it
```

Every member of this interface carries a default body that throws `NotSupportedException` (Swift declares them all with protocol-extension defaults, i.e. "optional" delegate methods). Implement all six rather than relying on the defaults — an unimplemented one turns a callback Swift does make into an exception crossing a native frame. As with the interaction delegate, **hold your own strong reference**: the property is `weak` on the Swift side, so your reference is the only one keeping the implementation alive.

### `RecognizedItem`

A payload-carrying Swift enum, so the C# shape is a class with a discriminator:

```csharp
static void Handle(RecognizedItem item)
{
    Console.WriteLine($"{item.Id} at {item.Bounds.TopLeft}");     // Id is a System.Guid

    switch (item.Tag)
    {
        case RecognizedItem.CaseTag.Text when item.TryGetText(out var text):
            Console.WriteLine(text.Transcript);
            break;
        case RecognizedItem.CaseTag.Barcode when item.TryGetBarcode(out var barcode):
            Console.WriteLine(barcode.PayloadStringValue ?? "(no payload)");
            break;
    }
}
```

| Type | Members |
|---|---|
| `RecognizedItem` | `Tag` (`CaseTag { Text = 0, Barcode = 1 }`), `TryGetText(out Text)`, `TryGetBarcode(out Barcode)`, `Id` (`Guid`), `Bounds` (`BoundsInfo`), static `TextValue(Text)` / `BarcodeValue(Barcode)` |
| `RecognizedItem.Text` | `Id` (`Guid`), `Bounds`, `Transcript` (`string`), `Observation` (`Vision.VNRecognizedTextObservation`) |
| `RecognizedItem.Barcode` | `Id` (`Guid`), `Bounds`, `PayloadStringValue` (`string?`), `Observation` (`Vision.VNBarcodeObservation`) |
| `RecognizedItem.BoundsInfo` | `TopLeft`, `TopRight`, `BottomRight`, `BottomLeft` (all `CGPoint`) — Swift's `RecognizedItem.Bounds` |

### Camera permission and the simulator

- Add an **`NSCameraUsageDescription`** string to your app's `Info.plist`. The scanner uses the camera, and iOS terminates an app that requests camera access without it.
- **Present and retain the controller** before calling `StartScanning` — it is a `UIViewController` and needs a live view.
- **The simulator has no camera**, so `IsSupported` is false there and `StartScanning` raises `ScanningUnavailable`. Everything else works on a simulator: constructing the controller, reading and writing its properties, assigning a delegate, fetching the item stream, and `StopScanning`. Callbacks require a real device with a real camera.

## Per-TFM surface

Every type the module declares on each platform is emitted — the binding report shows 19/19 types on iOS, 9/9 on Mac Catalyst, 10/10 on macOS, with zero skipped types.

| Type | `net10.0-ios26.2` | `net10.0-maccatalyst26.2` | `net10.0-macos26.2` |
|---|:--:|:--:|:--:|
| `ImageAnalyzer` (+ `Configuration`, `AnalysisTypes`) | ✅ | ✅ | ✅ |
| `ImageAnalysis` | ✅ | ✅ | ✅ |
| `ImageAnalysisInteraction` (+ `InteractionTypes`, `Subject`, `SubjectUnavailable`, `IImageAnalysisInteractionDelegate`) | ✅ | ✅ | — |
| `ImageAnalysisOverlayView` (+ `InteractionTypes`, `Subject`, `SubjectUnavailable`, `MenuTag`, `IImageAnalysisOverlayViewDelegate`) | — | — | ✅ |
| `DataScannerViewController` (+ `RecognizedDataType`, `QualityLevelKind`, `TextContentType`, `ScanningUnavailable`, `IDataScannerViewControllerDelegate`) | ✅ | — | — |
| `RecognizedItem` (+ `Text`, `Barcode`, `BoundsInfo`) | ✅ | — | — |

`VNDocumentCameraViewController` is an Objective-C class that is not part of the Swift module this package binds — it does not appear in VisionKit's `.swiftinterface` at all. Use .NET's existing `VisionKit` platform bindings for the document camera.

## Known limitations

A small number of members are declined by the generator and are **not callable** from C#:

- **`ImageAnalyzer.analyze(_ pixelBuffer: CVPixelBuffer, orientation:configuration:)`** — skipped on every TFM (*"method signature contains unsupported placeholder type"*): `CoreVideo.CVPixelBuffer` has no projection. Convert the buffer to a `CGImage`/`CIImage` and use one of the five bound overloads.
- **`ImageAnalysisInteraction.selectedRanges` / `ImageAnalysisOverlayView.selectedRanges`** — skipped (*"property type resolved to AnyType"*): Swift's `[Range<String.Index>]` has no C# projection. `SelectedText` / `SelectedAttributedText` give you the selected content itself; only the index ranges are unavailable.
- **`ImageAnalysisOverlayView.supplementaryInterfaceContentInsets`** (macOS only) — skipped (*"framework type `Foundation.NSEdgeInsets` has no .NET binding"*). The UIKit interaction's `SupplementaryInterfaceContentInsets` (a `UIEdgeInsets`) **is** bound; only the AppKit one is missing.
- **`ImageAnalysisInteraction` does not implement `IUIInteraction`.** Swift's `UIInteraction` conformance isn't in the generator's type database, so the interaction can't be handed to `UIView.AddInteraction` from C# — see the note in [Live Text on screen (iOS / Mac Catalyst)](#live-text-on-screen-ios--mac-catalyst). The same class of drop removes cosmetic conformances (`CustomStringConvertible`, `NSCoding`, the UIKit/AppKit responder protocols) from the generated types; those never had a usable C# projection anyway.
- **Delegate requirements with non-blittable shapes can't be called *through a protocol-typed value*.** Members whose parameters or returns are `CGPoint`, `CGRect`, `NSMenu`, `NSEvent`, `UIView?`, `RecognizedItem`, or `[RecognizedItem]` are marked `[Obsolete(… SB0003)]` on the generated proxy class. This only affects invoking them on a Swift-supplied delegate value; it does **not** affect the direction you actually use — Swift calling *into* your C# implementation, for which reverse-dispatch thunks are emitted for every requirement on all three delegate protocols.
- **Delegate defaults throw.** Interface members that map a Swift protocol-extension default have a C# default body that throws `NotSupportedException`. Implement every callback you might receive.

Everything else in the module is emitted: 122 of 124 members on iOS, 70 on Mac Catalyst, 80 on macOS (the counts include synthesized members such as the option-set operators).

**Validated on the iOS simulator** by `apple-frameworks/VisionKit/tests/Tests.cs`: type metadata for every bound type, the option-set bit algebra, `Configuration` construction and `Locales` round-trip, `ImageAnalyzer.IsSupported` / `SupportedTextRecognitionLanguages`, `AnalyzeAsync` on a drawn in-memory image (UIKit and CoreGraphics paths), `GetSubjectsAsync`, the `RecognizedDataType` factories, all three `DataScannerViewController` constructor forms (empty set, populated set, defaults), property round-trips including `RegionOfInterest`, delegate assignment, the `RecognizedItems` stream shape, `StopScanning`, and `StartScanning`'s catchable failure. Delegate *callbacks* and live scanning need a physical device.

## Memory & threading

Generated types implement `ISwiftObject` / `IDisposable`. For short-lived locals the finalizer cleans up, but `using var` is the recommended pattern for deterministic cleanup — `Dispose` is safe on every generated type and double-Dispose is a no-op.

```csharp
using var analyzer = new ImageAnalyzer();
using var config = new ImageAnalyzer.Configuration(ImageAnalyzer.AnalysisTypes.Text);
using var analysis = await analyzer.AnalyzeAsync(image, config);
```

- **`ImageAnalyzer` is `Sendable`** and carries no main-actor isolation — analyze from any thread.
- **`ImageAnalysisInteraction`, `ImageAnalysisOverlayView`, and `DataScannerViewController` are `@MainActor`-isolated.** Their generated members are marked `[SwiftMainActor]` and assert the main thread at runtime, constructors included. Await their async members *from* the UI thread so the platform synchronization context can resume each continuation back on main; blocking the main thread while awaiting stalls the very executor those continuations need.
- **Option-set statics allocate.** `ImageAnalyzer.AnalysisTypes.Text`, `InteractionTypes.Automatic`, etc. return disposable instances rather than cached singletons — `using var` them (or accept finalizer cleanup) rather than treating them as free constants.
- **Delegates are held weakly.** `IDataScannerViewControllerDelegate` / `IImageAnalysisInteractionDelegate` / `IImageAnalysisOverlayViewDelegate` are assigned into Swift `weak` properties, so your application owns the implementation and the binding's wrapper lives exactly as long as it does. Keep the implementation alive yourself. Dropping it clears the Swift slot; a callback that still arrives afterwards (VisionKit holding the delegate elsewhere for work in flight) is degraded rather than fatal — a `void` requirement is dropped, a value-returning one hands Swift an empty or zero value — and `ProxyDegradation.ImplCollected` fires once per conformance so the silence is diagnosable.
- **The recognized-item stream is a live producer.** `DataScannerViewController.RecognizedItems` starts native work when fetched; dispose it if you don't enumerate it, and don't enumerate the same fetch twice.
- **Cancellation.** Every `*Async` member takes a trailing `CancellationToken` that cancels the in-flight Swift task.

## Reference links

- [Apple — VisionKit](https://developer.apple.com/documentation/visionkit)
- [Apple — Enabling Live Text interactions with images](https://developer.apple.com/documentation/visionkit/enabling-live-text-interactions-with-images)
- [Apple — Scanning data with the camera](https://developer.apple.com/documentation/visionkit/scanning-data-with-the-camera)
- [Apple — `DataScannerViewController`](https://developer.apple.com/documentation/visionkit/datascannerviewcontroller)
- [Apple — `NSCameraUsageDescription`](https://developer.apple.com/documentation/bundleresources/information-property-list/nscamerausagedescription)
- [`SwiftBindings.Apple.VisionKit` package README](https://github.com/justinwojo/swift-dotnet-packages/blob/main/apple-frameworks/VisionKit/README.md)
- [Swift Dotnet Bindings](https://github.com/justinwojo/swift-dotnet-bindings) — the generator behind these bindings
