# BlinkIDUX for .NET — Usage Guide

`SwiftBindings.BlinkIDUX` exposes [BlinkIDUX](https://github.com/BlinkID/blinkid-ios) — Microblink's prebuilt, SwiftUI-based scanning experience layered on top of the BlinkID core SDK — to C# through .NET 10's native Swift interop. These are direct Swift calls, not Objective-C proxy wrappers.

BlinkIDUX is a *UI* package. Its centrepiece upstream is a SwiftUI view (`BlinkIDUXView`), and SwiftUI views cannot be constructed from C#. The binding solves this with a **generated SwiftUI bridge**: `BlinkIDUXViewSession` builds the whole stack (SDK → analyzer → view model → hosting controller) inside Swift and hands you back a plain `UIKit.UIViewController` you can present. That is the path most apps want, and it is where this guide starts — the bridge hands back both the outcome code and, if you ask for it, the full `BlinkIDScanningResult`. A second, headless path (`BlinkIDAnalyzer` + your own camera and UI) is available when you need to own the camera session and render your own scanning screen.

## Contents

- [Requirements & install](#requirements--install)
- [Swift → C# translation rules](#swift--c-translation-rules)
- [Quick start: launch the prebuilt scan UX](#quick-start-launch-the-prebuilt-scan-ux)
- [Scan outcomes from the UX session](#scan-outcomes-from-the-ux-session)
- [Theming the prebuilt UX](#theming-the-prebuilt-ux)
- [Headless path: analyzer + your own camera](#headless-path-analyzer--your-own-camera)
- [UX events](#ux-events)
- [Enums with payloads](#enums-with-payloads)
- [Known limitations](#known-limitations)
- [Memory & threading](#memory--threading)
- [Reference links](#reference-links)

## Requirements & install

- .NET 10.0+, macOS host for development
- Target framework `net10.0-ios` (iOS only — BlinkIDUX ships no macOS/tvOS slice)
- `<SupportedOSPlatformVersion>16.0</SupportedOSPlatformVersion>` or higher — the BlinkIDUX xcframework's own minimum is **iOS 16.0** (BlinkID core is 15.0, but the UX layer's SwiftUI requires 16)
- A **BlinkID licence key** from the [Microblink developer dashboard](https://developer.microblink.com) — BlinkID is a commercial SDK and nothing runs without a key
- `NSCameraUsageDescription` in your app's `Info.plist`

```
dotnet add package SwiftBindings.BlinkIDUX
```

That is the only package you add. `SwiftBindings.BlinkIDUX` declares a NuGet dependency on **`SwiftBindings.BlinkID`**, so the core SDK — types like `BlinkIDSdkInfo`, `BlinkIDScanningResult`, `CameraFrame` — restores transitively and both native xcframeworks land in your app. See the core-SDK page: **[BlinkID](https://github.com/justinwojo/swift-dotnet-packages/wiki/BlinkID)**.

> **Package version ≠ upstream version.** The NuGet version is independent of the pinned Microblink release: `SwiftBindings.BlinkIDUX 7.8.5` wraps upstream BlinkIDUX **7.8.0** (the extra patches are rebuilds against newer binding SDKs). Both sibling packages are released in lockstep at the same version.

```csharp
using BlinkID;
using BlinkIDUX;
using Swift;
using Swift.Runtime;
```

## Swift → C# translation rules

| Swift | C# | Rule |
|---|---|---|
| `module BlinkIDUX` | `namespace BlinkIDUX` | module-level namespace, no `Swift.` prefix. `using Swift;` / `using Swift.Runtime;` are still needed for runtime types (`SwiftColor`, `ISwiftObject`, `SwiftObjectHelper<T>`) |
| `enum UIEvent` | `BlinkIDUX.UIEvent` | **collides with `UIKit.UIEvent`.** In any file that also does `using UIKit;`, alias it: `using BUXUIEvent = BlinkIDUX.UIEvent;` |
| `func result() async -> ScanningResult<…>` | `GetResultAsync(CancellationToken)` | `async` Swift functions become `Task`-returning `…Async` methods with a trailing `CancellationToken`; a name that collides with a property gets a `Get` prefix |
| `init(sdk:eventStream:) async throws` | `static Task<BlinkIDAnalyzer> CreateAsync(…)` | async/throwing initializers become static `CreateAsync` factories |
| `enum DocumentSide { case passport(PassportOrientation), front, … }` | class with `.Tag` (`CaseTag` enum), static properties for payload-less cases, static methods for payload cases, `TryGet…(out …)` extractors | payload enums project to a class, not a C# `enum` |
| `enum PassportOrientation: Int` | plain C# `enum : int` | payload-less, raw-value-backed Swift enums become ordinary C# enums |
| `Camera.Position` | `Camera.CameraPosition` | nested types are de-collided by prefixing the outer type name |
| `struct ScanningUXSettings` | `class ScanningUXSettings : ISwiftStruct, IDisposable` | Swift structs project as classes over a native buffer — **dispose them** |
| `var isConnected: Bool { get }` | `bool IsConnected { get; }` | properties are PascalCase |
| `AsyncStream<[UIEvent]>` | `IAsyncEnumerable<IReadOnlyList<UIEvent>>` | Swift async sequences project as `IAsyncEnumerable<T>`; use `await foreach` |

Two rules worth internalising before reading the vendor docs:

1. **Swift default arguments become an overload set, longest-first.** `BlinkIDSessionSettings()` has a true parameterless form, and `BlinkIDSdkSettings` bottoms out at a one-argument `new BlinkIDSdkSettings(licenseKey)` that takes Swift's defaults for everything else (`licensee: nil`, `helloLogEnabled: false`, `downloadResources: true`, `resourceDownloadUrl: "https://models.cdn.microblink.com/resources"`). Only *trailing* defaults collapse, so a middle argument you want to change still means naming the ones after it.
2. **SwiftUI values are marshalled by value and are constructible.** `SwiftUI.Color` and `SwiftUI.Font` expose static factories — `SwiftUI.Color.Create(r, g, b, opacity)` and `SwiftUI.Font.System(size, weight, design)` — so the `BlinkIDTheme` *instance* properties are genuinely read/write. The static `Swift.SwiftColor` / `Swift.SwiftFont` theme bridge is still the more ergonomic path (hex colours, semantic font presets) — see [Theming](#theming-the-prebuilt-ux).

## Quick start: launch the prebuilt scan UX

`BlinkIDUXViewSession.CreateAsync` does everything upstream's sample does in Swift — creates the core SDK (`BlinkIDSdkInfo`, Swift's `BlinkIDSdk`), a `BlinkIDEventStream`, a `BlinkIDAnalyzer`, a `ScanningUXSettings`, a `BlinkIDUXModel`, and hosts `BlinkIDUXView` in a `UIHostingController` — then hands you the controller.

```csharp
using BlinkIDUX;
using UIKit;

public partial class ScanViewController : UIViewController
{
    private BlinkIDUXViewSession? _session;

    private async Task StartScanAsync()
    {
        _session = await BlinkIDUXViewSession.CreateAsync(
            licenseKey: "<your-microblink-licence-key>",
            showIntroductionAlert: true,       // default: true
            showHelpButton: true,              // default: true
            allowHapticFeedback: true,         // default: true
            preferFrontCamera: false,          // default: false (rear camera)
            onResult: code => InvokeOnMainThread(() => OnScanFinished(code)));

        var scanner = _session.ViewController;   // UIKit.UIViewController?
        if (scanner is not null)
            PresentViewController(scanner, animated: true, completionHandler: null);
    }
}
```

`CreateAsync` is genuinely asynchronous — SDK creation downloads/validates the licence and resource models — and it **throws `InvalidOperationException` carrying the Swift error text** if the licence is invalid, expired, or the bundle id doesn't match. Wrap it.

> **Bridge defaults now match the library's own.** Every `bool` on the generated bridge carries the default BlinkIDUX declares in Swift — `preferFrontCamera` is `false` (rear camera), matching `ScanningUXSettings`' `preferredCameraPosition = .Back`. Older binding SDKs defaulted these to `true` regardless of the declared value, so any code written against a pre-0.19.0 package that relied on the old behaviour should be re-read; passing the flags explicitly, as above, is immune either way.

Lifetime: `ViewController` is a **non-owning** managed peer. Presenting or embedding it transfers ownership to the parent, so the controller stays valid after you dispose the session; an *un-embedded* controller is only valid while the session lives. Dispose the session when the flow ends:

```csharp
_session?.Dispose();
_session = null;
```

## Scan outcomes from the UX session

The `onResult` callback fires once, with an `int` code, when the hosted flow finishes:

| Code | Meaning |
|---|---|
| `0` | Completed — a document was scanned successfully |
| `1` | Interrupted — an alert condition ended the session (timeout, disallowed document class) |
| `2` | Cancelled — the user backed out |
| `3` | Ended — the session ended without a result |
| `-1` | Unknown case (upstream added a case this binding predates) |

These mirror `ScanningResult<T, U>.CaseTag` (`Completed = 0`, `Interrupted = 1`, `Cancelled = 2`, `Ended = 3`).

**The UX session can also hand you the extracted data.** Alongside `onResult`, `CreateAsync` takes an optional `onResultPayload` callback that receives the outcome code *and* the value the result case carried — for `Completed` that is the full `BlinkID.BlinkIDScanningResult`:

```csharp
_session = await BlinkIDUXViewSession.CreateAsync(
    licenseKey: "<your-microblink-licence-key>",
    onResultPayload: (code, scan) => InvokeOnMainThread(() =>
    {
        if (code != 0 || scan is null) { OnScanFinished(code); return; }

        using (scan)                              // you own it — see below
        {
            var name   = scan.FullName?.Value;
            var docNo  = scan.DocumentNumber?.Value;
            var dob    = scan.DateOfBirth?.Date;
        }
    }));
```

> **Ownership.** The payload is a managed wrapper over memory Swift handed across, and registering `onResultPayload` transfers ownership to you — dispose it inside the callback (`using` around the body is the usual shape); nothing else releases it. If you supply only `onResult`, the bridge disposes the value itself and you have nothing to release. `onResult` still fires first when both are supplied, so a code-only path stays valid. Outcomes other than `Completed` pass `null`.

The headless analyzer path below remains the right choice when you want to drive the camera and UI yourself; it is no longer required merely to reach the extracted fields.

The callbacks arrive on Swift's main actor; hop to the UI thread with `InvokeOnMainThread` before touching UIKit, as in the snippets above.

## Theming the prebuilt UX

`BlinkIDTheme` is a singleton (`BlinkIDTheme.Shared`) whose colour/font properties are typed `SwiftUI.Color` / `SwiftUI.Font`. Those are real read/write properties — the values are marshalled by value and can be built with `SwiftUI.Color.Create(r, g, b, opacity)` / `SwiftUI.Font.System(size, weight, design)`. The binding also emits a **static theme bridge** taking `Swift.SwiftColor` and `Swift.SwiftFont`, which adds hex colours and the semantic font presets; that is the more convenient of the two and what the rest of this section uses. Set these **before** calling `BlinkIDUXViewSession.CreateAsync`; changes apply on the next render.

```csharp
using BlinkIDUX;
using Swift;

var brand = SwiftColor.FromHex(0x1E88E5);

// Alerts
BlinkIDTheme.SetAlertTitleColor(brand);
BlinkIDTheme.SetAlertTitleFont(SwiftFont.System(20, SwiftFontWeight.Semibold));
BlinkIDTheme.SetAlertDescriptionColor(new SwiftColor(0.3, 0.3, 0.3));
BlinkIDTheme.SetAlertDescriptionFont(SwiftFont.Body);
BlinkIDTheme.SetAlertButtonColor(brand);
BlinkIDTheme.SetAlertButtonFont(SwiftFont.Headline);
BlinkIDTheme.SetAlertBackgroundColor(SwiftColor.White);

// Onboarding sheet
BlinkIDTheme.SetOnboardingSheetTitleColor(brand);
BlinkIDTheme.SetOnboardingSheetTitleFont(SwiftFont.Title2);
BlinkIDTheme.SetOnboardingSheetDescriptionColor(SwiftColor.Black);
BlinkIDTheme.SetOnboardingSheetDescriptionFont(SwiftFont.Body);
BlinkIDTheme.SetOnboardingSheetButtonColor(brand);
BlinkIDTheme.SetOnboardingSheetButtonFont(SwiftFont.Headline);
BlinkIDTheme.SetOnboardingSheetPageIndicatorColor(brand);
BlinkIDTheme.SetOnboardingSheetBackgroundColor(SwiftColor.White);

// Reticle, help button, toast
BlinkIDTheme.SetReticleTooltipFont(SwiftFont.Callout);
BlinkIDTheme.SetHelpButtonForegroundColor(SwiftColor.White);
BlinkIDTheme.SetHelpButtonBackgroundColor(brand);
BlinkIDTheme.SetHelpButtonTooltipForegroundColor(SwiftColor.White);
BlinkIDTheme.SetHelpButtonTooltipBackgroundColor(SwiftColor.Black);
BlinkIDTheme.SetToastBackgroundColor(SwiftColor.FromHex(0x000000, 0.75));
```

That is the complete set — 21 setters, 14 colours and 7 fonts, and the same 21 slots are readable and writable as instance properties on `BlinkIDTheme.Shared` if you prefer to work in `SwiftUI.Color` / `SwiftUI.Font` directly (dispose the values you construct). `SwiftColor` offers `FromHex(uint)`, `FromHex(uint, double alpha)`, `White`/`Black`/`Clear`/`Red`/`Green`/`Blue`, and a `(r, g, b, a)` constructor with 0–1 components. `SwiftFont` offers `System(size, weight, design)`, `Custom(name, size)`, and the semantic presets (`LargeTitle`, `Title`, `Title2`, `Title3`, `Headline`, `Body`, `Callout`, `Subheadline`, `Footnote`, `Caption`, `Caption2`).

`MicroblinkColor` is a *separate* type and is not a colour value — it's a string-raw-value enum naming the SDK's asset-catalog colour slots (`Primary`, `Secondary`, `Background`, `TooltipBackground`, `HelpBackground`, `NeedHelpTooltipBackground`, `ToastBackgroundColor`). `.RawValue` gives the asset name; `MicroblinkColor.FromRawValue(string)` round-trips it. Overriding those assets is an asset-catalog exercise on the Swift side, not something you set from C#.

## Headless path: analyzer + your own camera

When you want to own the camera session and render your own scanning UI, drive `BlinkIDAnalyzer` directly. This is the C# equivalent of upstream's "core API" sample. (If all you needed was the extracted data, `onResultPayload` on the UX session above already gives you that.)

```csharp
using BlinkID;
using BlinkIDUX;

// 1. Create the core SDK (the one-argument overload takes Swift's defaults)
using var sdkSettings = new BlinkIDSdkSettings("<your-microblink-licence-key>");

// The C# type is BlinkIDSdkInfo — Swift's `BlinkIDSdk` class is renamed to avoid
// colliding with the `BlinkIDSDK` namespace; only the type moves, not the methods.
using var sdk = await BlinkIDSdkInfo.CreateBlinkIDSdkAsync(sdkSettings);

// 2. Analyzer: session settings + an event stream you keep a handle on
using var sessionSettings = new BlinkIDSessionSettings();
using var events = new BlinkIDEventStream();
using var analyzer = await BlinkIDAnalyzer.CreateAsync(sdk, sessionSettings, events);

// 3. Feed frames from your own AVCaptureVideoDataOutput delegate
//    (CMSampleBuffer → MBSampleBufferWrapper → CameraFrame)
using var wrapper = new MBSampleBufferWrapper(sampleBuffer);
using var frame = new CameraFrame(wrapper);
await analyzer.AnalyzeAsync(frame);

// 4. Await the terminal outcome
using var outcome = await analyzer.GetResultAsync();

switch (outcome.Tag)
{
    case ScanningResult<BlinkIDScanningResult, BlinkIDScanningAlertType>.CaseTag.Completed:
        if (outcome.TryGetCompleted(out var scan))
        {
            using (scan)
            {
                Console.WriteLine(scan.FullName?.Value);          // StringResult? → .Value
                Console.WriteLine(scan.DocumentNumber?.Value);
                Console.WriteLine(scan.DateOfBirth?.Date);        // DateResult<…>? → DateTimeOffset?
            }
        }
        break;

    case ScanningResult<BlinkIDScanningResult, BlinkIDScanningAlertType>.CaseTag.Interrupted:
        if (outcome.TryGetInterrupted(out var alert))
        {
            using (alert) { Console.WriteLine($"{alert.Title}: {alert.Description}"); }
        }
        break;

    case ScanningResult<BlinkIDScanningResult, BlinkIDScanningAlertType>.CaseTag.Cancelled:
    case ScanningResult<BlinkIDScanningResult, BlinkIDScanningAlertType>.CaseTag.Ended:
        break;
}
```

`outcome.ScanResult` is a shorthand for the completed payload (`T?`, null on the other cases) if you'd rather not switch on `Tag`.

Session control mirrors the Swift API: `PauseAsync()`, `ResumeAsync()`, `RestartAsync()`, `CancelAsync()`, `EndAsync()`, plus the read-only `SessionNumber` and `StepTimeoutDuration` (set the timeout via `BlinkIDSessionSettings`, not on the analyzer). `CreateAsync` also takes an optional `IBlinkIDClassFilter` — implement that interface in C# and the generated `BlinkIDClassFilterProxy` reverse-dispatches `ClassAllowed(BlinkIDSDK.DocumentClassInfo)` back into your code to reject document classes you don't accept.

The field surface on `BlinkIDScanningResult` is broad — around 50 `StringResult?` properties (names, address, document numbers, issuing authority, sex, nationality, …) plus `DateResult<…>?` dates, `DocumentClassInfo`, `DataMatchResult`, and image results. `StringResult` exposes `.Value` (`string?`) and `.Location`; `DateResult<…>` exposes `.Day`/`.Month`/`.Year`, `.Date` (`DateTimeOffset?`), `.OriginalString`, and `.SuccessfullyParsed`. Consult the [BlinkID DocC reference](https://microblink.github.io/blinkid-swift-package/documentation/blinkid/) for field semantics — the C# names are the Swift names PascalCased.

## UX events

`BlinkIDEventStream` is the feedback channel the prebuilt UI renders as tooltips ("move closer", "too dark", "flip the document"). Consume it with `await foreach`:

```csharp
using BUXUIEvent = BlinkIDUX.UIEvent;   // avoids the UIKit.UIEvent collision

_ = Task.Run(async () =>
{
    await foreach (IReadOnlyList<BUXUIEvent> batch in events.Stream)
    {
        foreach (var e in batch)
        {
            switch (e.Tag)
            {
                case BUXUIEvent.CaseTag.TooFar:   ShowHint("Move closer"); break;
                case BUXUIEvent.CaseTag.Glare:    ShowHint("Avoid glare"); break;
                case BUXUIEvent.CaseTag.TooDark:  ShowHint("More light");  break;
                case BUXUIEvent.CaseTag.RequestDocumentSide:
                    if (e.TryGetRequestDocumentSide(out var side))
                    {
                        using (side) { ShowHint($"Show the {side.Tag} side"); }
                    }
                    break;
            }
        }
    }
});
```

All 16 cases: `RequestDocumentSide(DocumentSide)`, `WrongSidePassport(PassportOrientation)`, `WrongSide`, `Blur`, `Glare`, `Occlusion`, `Tilt`, `TooClose`, `TooFar`, `TooCloseToEdge`, `NotFullyVisible`, `TooDark`, `TooBright`, `FacePhotoNotFullyVisible`, `WrongSidePassportWithBarcode`, `UnsupportedDocument`. You can also push events yourself with `events.SendAsync(new[] { BUXUIEvent.Glare })`.

Read `BlinkIDAnalyzer.Events` instead of holding your own `BlinkIDEventStream` and you'll hit a compile error — that property is emitted as an `[Obsolete(…, error: true)]` `SB0006` stub because its protocol-existential return can't be projected. Always keep the `BlinkIDEventStream` you passed to `CreateAsync`.

## Enums with payloads

The payload-carrying Swift enums all follow the same shape. `DocumentSide` is representative:

```csharp
DocumentSide front = DocumentSide.Front;                       // payload-less → static property
DocumentSide pp    = DocumentSide.Passport(PassportOrientation.Left90);  // payload → static method

if (pp.Tag == DocumentSide.CaseTag.Passport && pp.TryGetPassport(out var orientation))
    Console.WriteLine(orientation);   // Left90

front.TryGetPassport(out _);          // false — extractors are safe on the wrong case
```

Cases (tag order matches Swift, payload cases first):

- **`DocumentSide`** — `Passport(PassportOrientation)`, `Front`, `Back`, `Barcode`, `PassportBarcode`
- **`ReticleState`** — `Error(string)`, `Passport(string)`, `InactiveWithMessage(string)`, `Front`, `Back`, `Barcode`, `Detecting`, `Flip`, `Inactive`
- **`CameraStatus`** — `Unknown`, `Unauthorized`, `Failed`, `Running`, `Interrupted`, `Stopped` (all payload-less)
- **`BlinkIDScanningAlertType`** — `Timeout`, `DisallowedClass`; exposes `Id`, `Title`, `Description`, `ButtonTitle`, `RawValue` (`int`), and `ToString()` → `Description`
- **`PassportOrientation`** — a plain C# `enum`: `None = 0`, `Left90 = 1`, `Right90 = 2`
- **`Camera.CameraPosition`** — a plain C# `enum` with **Swift's ordering**: `Back = 0`, `Front = 1` (not the intuitive front-first order)

`NetworkMonitor` is a small standalone utility for the "no internet" state: `new NetworkMonitor()`, then `IsConnected` / `IsOffline`. The bridge also ships `NoInternetViewSession.Create(retryAction, onAppear, onDisappear)` — a hostable SwiftUI "no internet" screen with `SetFrame` / `SetPadding` / `SetBackground` / `SetForegroundColor` / `SetCornerRadius` / `SetOpacity` / `SetFontSize` modifiers and `PresentAsSheet` / `PushOnNavigationStack` / `Dismiss` presentation helpers.

## Known limitations

- **`BlinkIDAnalyzer.Events` is a hard-error stub (`SB0006`).** Keep your own `BlinkIDEventStream`.
- **`CameraPreview` and `CameraView` are not bridged.** The generator emits them as commented-out bridge templates — `CameraPreview` takes an `any PreviewSource` existential and `CameraView` is generic over an unconstrained `Camera`. Neither has a resolvable C ABI. If you need a live preview outside the packaged UX, use `AVCaptureVideoPreviewLayer` from C# and feed frames to the analyzer yourself.
- **Protocol-typed calls are narrower than concrete ones (`SB0003`).** Twelve members on the generated protocol proxies (`ICameraModel.Status`, `.Orientation`, `.Error`, `.SampleBuffer`, the async `StartAsync`/`StopAsync`/`FocusAndExposeAsync`, `IPreviewSource.Connect(IPreviewTarget)`, `IReticleStateProtocol.ReticleStateAppearance`, …) can't be dispatched through a witness table. Call them on the concrete type. `IScanningResultProtocol` is empty for the same reason (`SB0004`) — its single member couldn't be projected.
- **The generic `ScanningViewModel<…>` constructor is an `SB0001` stub** — its `analyzer` parameter is an existential (`any CameraFrameAnalyzer<CameraFrame, …>`) the wrapper cannot specialize, so no `@_cdecl` wrapper exists for the initializer. `BlinkIDUXModel` (the concrete subclass) is fine; use it, plus the `BlinkIDUXModelBaseTrampolines` extension methods (`StartScanning`, `PauseScanning`, `ResumeScanning`, `RestartScanning`, `PresentAlert`, `DismissAlert`, `StopEventHandling`, `LicenseErrorAlertDismised`) for the inherited surface.
- **`CaptureMode` is not emitted at all.** Upstream declares it as an opaque type with zero size, so the generator skips it and `BlinkIDUX.Types.CaptureMode.cs` contains an empty namespace. There is nothing to call.
- **Resource bundle.** BlinkIDUX is an SPM package with resources, so its `resource_bundle_accessor` looks up `BlinkIDUX_BlinkIDUX.bundle` in `Bundle.main` and *fatally traps* if it's missing — historically the first thing to bite anyone touching `BlinkIDTheme.Shared`. **SDK 0.18.1 and later handle this for you** (verified again on 0.19.0): the real bundle (every shipped localisation plus `Assets.car`) is extracted from the xcframework and added as a `BundleResource`, so it ships in your `.app` automatically. If you ever see that trap on an older SDK, the workaround is an empty `BlinkIDUX_BlinkIDUX.bundle` directory at the root of your app bundle.
- **`BlinkIDUXModel` is `@MainActor`-isolated.** So are most of its members and `BlinkIDUXViewSession`'s hosting controller. Call them on the platform main thread.

## Memory & threading

Every bound type implements `IDisposable`; double-dispose is a no-op. `using var` is the recommended pattern throughout.

The two shapes behave differently and the generated XML docs say which is which:

- **Swift classes** (`BlinkIDAnalyzer`, `BlinkIDEventStream`, `BlinkIDTheme`, `NetworkMonitor`, `BlinkIDUXModel`) bridge ARC automatically. `Dispose()` is deterministic cleanup; the finalizer handles release if you skip it.
- **Swift structs** (`ScanningUXSettings`, `BlinkIDResultState`, `ScanningResult<T,U>`, `MicroblinkColor`, `DocumentSide`, `UIEvent`, `ReticleState`) wrap a native buffer and **must be disposed explicitly** — failure to dispose leaks native memory. Dispose the payload you pull out of a `TryGet…` too, as the snippets above do.

`BlinkIDUXViewSession` and `NoInternetViewSession` own native Swift objects behind an `IntPtr` and have finalizers, but dispose them explicitly when the flow ends. Do not use an un-embedded `ViewController` after disposing its session.

`CreateAsync`, `AnalyzeAsync`, `GetResultAsync`, and friends are real Swift-concurrency calls surfaced as `Task`; continuations do **not** guarantee the UI thread, so marshal with `InvokeOnMainThread` before touching UIKit.

## Reference links

For full API semantics, consult Microblink's own documentation — the C# surface is a mechanical projection of these Swift APIs:

- **[BlinkIDUX DocC API reference](https://microblink.github.io/blinkid-ios/documentation/blinkidux/)** — the authoritative reference for `BlinkIDUXView`, `BlinkIDUXModel`, `ScanningUXSettings`, `BlinkIDAnalyzer`, `BlinkIDTheme`
- **[BlinkID DocC API reference](https://microblink.github.io/blinkid-swift-package/documentation/blinkid/)** — the core SDK: `BlinkIDSdk`, `BlinkIDScanningResult` and its ~80 result fields
- **[Microblink BlinkID product docs](https://docs.microblink.com/blinkid)** — concepts, supported documents, licensing
- **[UI & UX best practices](https://docs.microblink.com/blinkid/ui-ux-best-practices)** — Microblink's guidance on how far to customise the scanning screen before conversion suffers
- **[blinkid-ios on GitHub](https://github.com/microblink/blinkid-ios)** — upstream source, sample apps, release notes
- **[Microblink developer dashboard](https://developer.microblink.com)** — licence keys

Related pages in this wiki:

- **[BlinkID](https://github.com/justinwojo/swift-dotnet-packages/wiki/BlinkID)** — the core scanning SDK this package depends on
