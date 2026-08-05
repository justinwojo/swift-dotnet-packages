# Kingfisher for .NET — Usage Guide

`SwiftBindings.Kingfisher` exposes [Kingfisher](https://github.com/onevcat/Kingfisher) — the pure-Swift image downloading and caching library — to C# through .NET 10's native Swift interop. These are direct Swift calls, not Objective-C proxy wrappers.

Kingfisher's headline Swift ergonomic is the `imageView.kf.setImage(with:)` extension. That specific spelling does **not** survive the binding (it is a generic `KingfisherWrapper<Base>` extension whose closure parameters aren't bridgeable), but the library's own `KF` fluent builder does, and `KF.Url(...).Set(imageView)` is the supported equivalent. This guide maps the working surface and is explicit about where it stops.

## Contents

- [Requirements & install](#requirements--install)
- [Swift → C# translation rules](#swift--c-translation-rules)
- [Quick start: load a remote image into a UIImageView](#quick-start-load-a-remote-image-into-a-uiimageview)
- [The `KF` builder](#the-kf-builder)
- [Awaiting a retrieval with `KingfisherManager`](#awaiting-a-retrieval-with-kingfishermanager)
- [Options (`KingfisherOptionsInfoItem`)](#options-kingfisheroptionsinfoitem)
- [Image cache](#image-cache)
- [Downloader](#downloader)
- [Processors](#processors)
- [Prefetching](#prefetching)
- [Animated GIFs](#animated-gifs)
- [Errors](#errors)
- [Known limitations](#known-limitations)
- [Memory & threading](#memory--threading)
- [Reference links](#reference-links)

## Requirements & install

- .NET 10.0+
- **Target framework: `net10.0-ios`.** The package ships a single iOS target framework.
- **iOS 15.0+.** Upstream Kingfisher supports iOS 13, but the binding's `SupportedOSPlatformVersion` is clamped up to the .NET iOS platform floor, so 15.0 is the effective minimum for consumers.
- macOS host for development

```
dotnet add package SwiftBindings.Kingfisher
```

```csharp
using Foundation;
using UIKit;
using Kingfisher;        // module-level namespace — all Kingfisher types live here
using Swift;             // CGSize, CGPoint, SwiftResult
using Swift.Runtime;     // SwiftException<T>
```

## Swift → C# translation rules

| Swift | C# | Rule |
|---|---|---|
| `import Kingfisher` | `using Kingfisher;` | the module name **is** the namespace — not `Swift.Kingfisher` |
| `KingfisherManager.shared`, `ImageCache.default` | `KingfisherManager.Shared`, `ImageCache.Default` | statics are PascalCase; `default` de-keywords to `Default` |
| `func retrieveImage(with:options:) async throws -> RetrieveImageResult` | `Task<RetrieveImageResult> RetrieveImageAsync(IResource, …, CancellationToken)` | Swift `async` methods get an `…Async` C# suffix and a trailing `CancellationToken` |
| `func downloadImage(with:completionHandler:)` | `DownloadImage(NSUrl, …, Action<SwiftResult<T, KingfisherError>>?)` | completion handlers become `Action<…>`; Swift's `Result<T, E>` becomes `Swift.SwiftResult<T, E>` |
| `enum StorageExpiration { case seconds(TimeInterval); case never }` | class with `CaseTag Tag`, static factories `StorageExpiration.Seconds(3600)`, static singletons `StorageExpiration.Never`, and `TryGetSeconds(out double)` | payload enums become classes; payload-less cases are static properties |
| `enum CacheType: Int { case none, memory, disk }` | plain C# `enum CacheType : int` | payload-less integer enums stay ordinary enums |
| `protocol ImageProcessor` | `interface IImageProcessor` | protocols get an `I` prefix |
| `var identifier: String { get }` | `string Identifier { get; }` | properties are PascalCase |
| `[KingfisherOptionsInfoItem]` | `IReadOnlyList<KingfisherOptionsInfoItem>` (a C# array works) | Swift arrays project to `IReadOnlyList<T>` / `IEnumerable<T>` |
| `CGSize`, `CGPoint` | `Swift.CGSize`, `Swift.CGPoint` | Swift-side geometry types come from the `Swift` namespace, **not** `CoreGraphics` |
| `URL` | `Foundation.NSUrl` | plus auto-generated `string`-taking convenience overloads on the downloader (`DownloadImage(string url, …)`) |
| `throws` | throws `SwiftException<KingfisherError>` | typed Swift errors are reconstructed into a generic exception carrying `.Error` |

Everything the generator emitted is enumerated in `obj/**/swift-binding/Kingfisher.api-surface.md` inside the package build — that file is the authoritative member list.

## Quick start: load a remote image into a UIImageView

```csharp
using Foundation;
using UIKit;
using Kingfisher;

var imageView = new UIImageView(new CoreGraphics.CGRect(0, 0, 200, 200));

using var builder = KF.Url(new NSUrl("https://example.com/image.png"));
DownloadTask? task = builder
    .Placeholder(UIImage.FromBundle("placeholder"))
    .Fade(0.25)
    .Set(imageView);

// later, if the cell was recycled:
task?.Cancel();
```

`KF.Url(NSUrl? url, string? cacheKey = null)` returns a `KF.Builder`. Every builder method returns a `KF.Builder` again, so chaining reads exactly like the Swift original. `Set(UIImageView)` is the terminal call; it kicks off the load and returns a `DownloadTask?` you can cancel.

Two other terminal calls exist for buttons:

```csharp
builder.Set(button, UIControlState.Normal);            // button image
builder.SetBackground(button, UIControlState.Normal);  // button background image
```

Sources other than a URL:

```csharp
KF.Resource(resource);            // any IResource
KF.Source(source);                // a Kingfisher.Source
KF.Data(bytes, cacheKey: "key");  // raw bytes
```

## The `KF` builder

The bound builder methods, all returning `KF.Builder`:

| Group | Methods |
|---|---|
| Placeholder & transition | `Placeholder(UIImage?)`, `Transition(ImageTransition)`, `Fade(double)`, `ForceTransition(bool)`, `KeepCurrentImageWhileLoading(bool)` |
| Caching | `TargetCache(ImageCache)`, `OriginalCache(ImageCache)`, `CacheMemoryOnly(bool)`, `CacheOriginalImage(bool)`, `OnlyFromCache(bool)`, `WaitForCache(bool)`, `ForceRefresh(bool)`, `FromMemoryCacheOrRefresh(bool)`, `LoadDiskFileSynchronously(bool)` |
| Downloading | `Downloader(ImageDownloader)`, `DownloadPriority(float)`, `RequestModifier(IAsyncImageDownloadRequestModifier)`, `RedirectHandler(IImageDownloadRedirectHandler)`, `Retry(IRetryStrategy?)` |
| Processing | `SetProcessor(IImageProcessor)`, `SetProcessors(IEnumerable<IImageProcessor>)`, `AppendProcessor(IImageProcessor)`, `Blur(double)`, `BlackWhite()`, `Cropping(CGSize, CGPoint)`, `Downsampling(CGSize)`, `ScaleFactor(double)`, `BackgroundDecode(bool)`, `ImageModifier(IImageModifier?)`, `Serialize(ICacheSerializer)` |
| Animated images | `OnlyLoadFirstFrame(bool)` |
| Misc | `CopyForMutation()`, `Options` (the parsed `KingfisherParsedOptionsInfo`) |

```csharp
using var builder = KF.Url(new NSUrl(url));
builder
    .Downsampling(new Swift.CGSize(200, 200))
    .SetProcessor(new RoundCornerImageProcessor(12.0))
    .CacheOriginalImage(true)
    .Fade(0.2)
    .Set(imageView);
```

> **The builder has no completion callback.** Kingfisher's `onSuccess` / `onFailure` / `onProgress` are `Delegate<…>` properties whose generic arguments can't satisfy the binding's constraints, so they are not emitted. If you need the result or the error, use `KingfisherManager.RetrieveImageAsync` (below) and assign `imageView.Image` yourself.

## Awaiting a retrieval with `KingfisherManager`

This is the path with full result and error reporting.

```csharp
using Foundation;
using Kingfisher;
using Swift.Runtime;

using var resource = new KF.ImageResource(new NSUrl("https://example.com/image.png"));

try
{
    using var result = await KingfisherManager.Shared.RetrieveImageAsync(resource);

    imageView.Image = result.Image;                   // UIImage
    Console.WriteLine(result.CacheType);              // None | Memory | Disk
    Console.WriteLine(result.CacheType.GetCached());  // extension on the enum
}
catch (SwiftException<KingfisherError> ex)
{
    KingfisherError? error = ex.Error;
    Console.WriteLine($"{ex.Message} ({error?.Tag})");
}
```

`KF.ImageResource(NSUrl downloadURL, string? cacheKey = null)` is the built-in `IResource`; it exposes `CacheKey` and `DownloadURL`.

With options and a progress block:

```csharp
var options = new[]
{
    KingfisherOptionsInfoItem.Processor(new DownsamplingImageProcessor(new Swift.CGSize(300, 300))),
    KingfisherOptionsInfoItem.CacheOriginalImage,
};

using var result = await KingfisherManager.Shared.RetrieveImageAsync(
    resource,
    options: options,
    progressBlock: (received, total) => Console.WriteLine($"{received}/{total}"));
```

A `Source`-taking overload exists too — build one with `Source.Network(IResource)` or `Source.Provider(IImageDataProvider)`. Note that `Source.Provider` only round-trips providers that originated in Swift; see [Known limitations](#known-limitations).

There is also a callback-style `RetrieveImage(...)` that returns the `DownloadTask?` synchronously and hands you a `SwiftResult<RetrieveImageResult, KingfisherError>`:

```csharp
DownloadTask? task = KingfisherManager.Shared.RetrieveImage(
    resource,
    options: null,
    progressBlock: null,
    downloadTaskUpdated: null,
    completionHandler: r =>
    {
        if (r.IsSuccess) { using var value = r.Success; /* value.Image */ }
        else             { using var err = r.Failure;   /* err.Tag */ }
    });
```

`SwiftResult<T, E>` offers `IsSuccess` / `IsFailure`, `Success` / `Failure`, `TryGetSuccess(out T)` / `TryGetFailure(out E)`, and `Match(onSuccess, onFailure)`.

## Options (`KingfisherOptionsInfoItem`)

Kingfisher's options array binds cleanly. Payload cases are static factory methods, payload-less cases are static properties:

```csharp
var options = new[]
{
    // payload cases
    KingfisherOptionsInfoItem.TargetCache(myCache),
    KingfisherOptionsInfoItem.Downloader(myDownloader),
    KingfisherOptionsInfoItem.Processor(new BlurImageProcessor(6.0)),
    KingfisherOptionsInfoItem.Transition(ImageTransition.Fade(0.25)),
    KingfisherOptionsInfoItem.ScaleFactor(UIScreen.MainScreen.Scale),
    KingfisherOptionsInfoItem.DownloadPriority(0.8f),
    KingfisherOptionsInfoItem.CallbackQueue(CallbackQueue.MainAsync),
    KingfisherOptionsInfoItem.MemoryCacheExpiration(StorageExpiration.Seconds(300)),
    KingfisherOptionsInfoItem.DiskCacheExpiration(StorageExpiration.Days(7)),
    KingfisherOptionsInfoItem.DiskCacheAccessExtendingExpiration(ExpirationExtending.CacheTime),
    KingfisherOptionsInfoItem.OnFailureImage(UIImage.FromBundle("broken")),
    KingfisherOptionsInfoItem.RetryStrategy(new DelayRetryStrategy(3)),
    KingfisherOptionsInfoItem.ProgressiveJPEG(new ImageProgressive(true, true, 0.0)),

    // payload-less cases
    KingfisherOptionsInfoItem.ForceRefresh,
    KingfisherOptionsInfoItem.CacheMemoryOnly,
    KingfisherOptionsInfoItem.OnlyFromCache,
    KingfisherOptionsInfoItem.BackgroundDecode,
    KingfisherOptionsInfoItem.CacheOriginalImage,
    KingfisherOptionsInfoItem.KeepCurrentImageWhileLoading,
    KingfisherOptionsInfoItem.OnlyLoadFirstFrame,
    KingfisherOptionsInfoItem.WaitForCache,
};
```

A few APIs take the pre-parsed form instead of the array; construct it directly:

```csharp
using var parsed = new KingfisherParsedOptionsInfo(options);
```

Enum-ish helper types follow the same tag pattern — `StorageExpiration` (`Seconds`, `Days`, `Date`, `Never`, `Expired`), `ExpirationExtending` (`None`, `CacheTime`, `ExpirationTime(StorageExpiration)`), `CallbackQueue` (`MainAsync`, `MainCurrentOrAsync`, `Untouch`), `ImageTransition` (`Fade`, `FlipFromLeft/Right/Top/Bottom`), `ImageFormat` (`Unknown`, `Png`, `Jpeg`, `Gif`), and `Radius` (`Point`, `WidthFraction`, `HeightFraction`). Read the case back with `.Tag` and pull the payload with the matching `TryGet…`:

```csharp
using var expiration = StorageExpiration.Seconds(3600);
if (expiration.Tag == StorageExpiration.CaseTag.Seconds && expiration.TryGetSeconds(out double secs))
    Console.WriteLine(secs);
```

## Image cache

```csharp
var cache = ImageCache.Default;                 // or: new ImageCache("MyCache")

// queries
bool cached      = cache.IsCached("key");
CacheType where  = cache.ImageCachedType("key");            // None | Memory | Disk
CacheType where2 = await cache.ImageCachedTypeAsync("key");
UIImage? inMem   = cache.RetrieveImageInMemoryCache("key");
string path      = cache.CachePath("key");
string hash      = cache.Hash("key");
int memoryCost   = cache.MemoryStorageCacheCost;
nuint diskBytes  = await cache.GetDiskStorageSizeAsync();

// retrieval (memory then disk)
using var hit = await cache.RetrieveImageAsync("key");
if (hit.Image is UIImage found)
    Console.WriteLine($"hit from {hit.CacheType}");

// storing
await cache.StoreAsync(image, original: null, key: "key");

// eviction
cache.ClearMemoryCache();
cache.CleanExpiredMemoryCache();
cache.BackgroundCleanExpiredDiskCache();
await cache.ClearDiskCacheAsync();
await cache.ClearCacheAsync();
await cache.CleanExpiredCacheAsync();
cache.RemoveImage("key", identifier: "", forcedExtension: null);
```

`ImageCacheResult` (returned by `RetrieveImageAsync`) is a tag union with `Disk(UIImage)` / `Memory(UIImage)` / `None`, plus the convenience properties `Image` (`UIImage?`) and `CacheType` used above.

There is also a callback form for disk-size calculation:

```csharp
cache.CalculateDiskStorageSize(r =>
{
    if (r.IsSuccess) Console.WriteLine($"{r.Success} bytes");
});
```

## Downloader

```csharp
var downloader = ImageDownloader.Default;       // or: new ImageDownloader("MyDownloader")

downloader.DownloadTimeout = 30.0;
downloader.RequestsUsePipelining = true;
NSUrlSessionConfiguration config = downloader.SessionConfiguration;

using var parsed = new KingfisherParsedOptionsInfo(null);
using var loaded = await downloader.DownloadImageAsync(new NSUrl(url), parsed);

imageView.Image = loaded.Image;         // UIImage
byte[] raw = loaded.OriginalData;       // bytes as downloaded
NSUrl? from = loaded.Url;

downloader.Cancel(new NSUrl(url));
downloader.CancelAll();
```

> **Overload-resolution gotcha.** `DownloadImageAsync` has three overloads — `(NSUrl, KingfisherParsedOptionsInfo, CancellationToken)`, `(NSUrl, IReadOnlyList<…>?, Action<long,long>?, CancellationToken)` and `(NSUrl, IReadOnlyList<…>?, CancellationToken)`. Because the last two differ only by an optional parameter, calling `DownloadImageAsync(url)` or `DownloadImageAsync(url, options)` is **ambiguous** and won't compile. Use the `KingfisherParsedOptionsInfo` overload as above, or name the progress block to disambiguate: `DownloadImageAsync(url, options, progressBlock: null)`.

## Processors

Every stock processor is bound, implements `IImageProcessor`, and exposes `Identifier`, `Process(ImageProcessItem, KingfisherParsedOptionsInfo)` and `Append(IImageProcessor)`.

```csharp
using var resize   = new ResizingImageProcessor(new Swift.CGSize(200, 200), ContentMode.AspectFit);
using var down     = new DownsamplingImageProcessor(new Swift.CGSize(200, 200));
using var crop     = new CroppingImageProcessor(new Swift.CGSize(100, 100), new Swift.CGPoint(0.5, 0.5));
using var blur     = new BlurImageProcessor(5.0);
using var bw       = new BlackWhiteProcessor();
using var tint     = new TintImageProcessor(UIColor.Red);
using var overlay  = new OverlayImageProcessor(UIColor.Black, 0.4);
using var colors   = new ColorControlsProcessor(0.0, 1.1, 1.0, 0.0); // brightness, contrast, saturation, inputEV
using var border   = new BorderImageProcessor(new Border(UIColor.White, 2.0));
using var deflt    = DefaultImageProcessor.Default;

// rounded corners — by point radius, or by a fraction of the image's dimensions
using var radius   = Radius.WidthFraction(0.5);
using var rounded  = new RoundCornerImageProcessor(radius);
using var rounded2 = new RoundCornerImageProcessor(10.0);

// chain them (Swift's `>>` operator)
IImageProcessor chained = down.Append(rounded);
```

Run one directly:

```csharp
using var item = ImageProcessItem.Image(sourceImage);
using var opts = new KingfisherParsedOptionsInfo(null);
UIImage? processed = blur.Process(item, opts);
```

Modifiers and serializers follow the same shape: `RenderingModeImageModifier(UIImageRenderingMode)`, `AlignmentRectInsetsImageModifier(UIEdgeInsets)`, `FlipsForRightToLeftLayoutDirectionImageModifier`, `DefaultCacheSerializer`, `FormatIndicatedCacheSerializer.JpegMethod(quality)`.

## Prefetching

```csharp
var urls = new[]
{
    new NSUrl("https://example.com/1.png"),
    new NSUrl("https://example.com/2.png"),
};

using var prefetcher = new ImagePrefetcher(urls, options: null);
prefetcher.MaxConcurrentDownloads = 3;
prefetcher.Start();
// …
prefetcher.Stop();
```

Constructors also accept `IEnumerable<IResource>` and `IEnumerable<Source>`. The per-batch completion/progress closures on the Swift initializers are not usable from C# (see limitations) — pass `null` and drive completion from your own bookkeeping if you need it.

## Animated GIFs

`AnimatedImageView` is a `UIImageView` subclass and binds fully:

```csharp
var gifView = new AnimatedImageView((UIImage?)null)
{
    AutoPlayAnimatedImage = true,
    FramePreloadCount = 20,
    NeedsPrescaling = true,
    BackgroundDecode = true,
    PurgeFramesOnBackground = true,
};

gifView.RepeatCount = AnimatedImageView.RepeatCountKind.Infinite;   // or .Once / .Finite(n)
gifView.StartAnimating();
bool running = gifView.IsAnimating;
gifView.StopAnimating();
gifView.PurgeFrames();
```

Load into it with the same `KF` builder — `AnimatedImageView` is a `UIImageView`, so `builder.Set(gifView)` works.

## Errors

Async calls that fail throw `Swift.Runtime.SwiftException<KingfisherError>`; `ex.Error` is the reconstructed Swift error. Callback-style calls hand you the error in the `Failure` branch of a `SwiftResult`.

```csharp
public enum KingfisherError.CaseTag : uint
{
    RequestError      = 0,
    ResponseError     = 1,
    CacheError        = 2,
    ProcessorError    = 3,
    ImageSettingError = 4,
}
```

Each top-level case nests its own reason type — `RequestErrorReason`, `ResponseErrorReason`, `CacheErrorReason`, `ProcessorErrorReason`, `ImageSettingErrorReason` — each again a class with its own `CaseTag` (e.g. `KingfisherError.RequestErrorReason.CaseTag.InvalidURL`, `.TaskCancelled`, `.EmptyRequest`). `KingfisherError.IsInvalidResponseStatusCodeMethod(nint)` mirrors the Swift helper.

```csharp
catch (SwiftException<KingfisherError> ex)
{
    if (ex.Error is { } err && err.Tag == KingfisherError.CaseTag.ResponseError)
        Console.WriteLine("server rejected the request");
}
```

## Known limitations

Confirmed against the generated bindings for SDK 0.19.0, and the test app passes end-to-end on SDK 0.19.0 on both iOS Simulator and physical device (pre-release regression matrix, 2026-08-05). The per-test breakdown — 248 pass / 0 fail / 1 skip — was last tallied on SDK 0.18.1, so treat the exact counts as indicative.

- **`imageView.kf.setImage(with:)` is not available.** The `KingfisherWrapper<Base>.SetImage(...)` overloads are emitted but marked `[Obsolete(... "SB0005")]` — their closure parameter shape isn't bridgeable from C#. Use `KF.Url(...).Set(imageView)` or `KingfisherManager.RetrieveImageAsync` + assign `imageView.Image`.
- **Builder result callbacks are missing.** `KF.Builder.onSuccess` / `onFailure` / `onProgress` are `Delegate<…>` generic properties the generator could not project. Use `RetrieveImageAsync` when you need the outcome.
- **Most `KingfisherWrapper` static image helpers are stubs.** `KingfisherWrapper<T>.Image(byte[], ImageCreatingOptions)` and both `AnimatedImage(...)` overloads carry `[Obsolete("SB0001")]` — the declaring type is generic in a way the wrapper can't specialize. `DownsampledImage(byte[], CGSize, double)` is **not** flagged and does run. The representation/state members — `jpegRepresentation`, `pngRepresentation`, `gifRepresentation`, `data`, `copyKingfisherState` — are not emitted at all (constrained-extension members on the generic wrapper). Decode raw bytes with `UIImage.LoadFromData` and re-encode with `UIImage.AsJPEG()` / `AsPNG()` instead.
- **`ImageDownloader.DownloadImage(url, KingfisherParsedOptionsInfo, completionHandler)`** (the callback overload taking parsed options), **`DownloadLivePhotoResource(...)`**, and the two-argument **`KingfisherManager.RetrieveImage(resource, completionHandler)`** carry `[Obsolete("SB0001")]`. The `async` variants, the `IReadOnlyList` callback overloads, and the five-argument `RetrieveImage(resource, options, progressBlock, downloadTaskUpdated, completionHandler)` shown above are fine.
- **`new ImageCache(name, cacheDirectoryURL, diskCachePathClosure)`** is `[Obsolete(… "SB0009")]` and throws `NotSupportedException` — the optional it carries is wider than the P/Invoke signature can pass. Use `new ImageCache(string name)`. (This is the one skipped test in the suite.) `DiskStorage.Backend<T>.Store(value, key, expiration, writeOptions, forcedExtension)` is flagged the same way — go through `ImageCache.Store…` rather than driving the disk backend directly.
- **`KF.Builder.Placeholder(IPlaceholder?)`** is `[Obsolete(… "SB0009")]`; the `Placeholder(UIImage?)` overload works. Likewise `KF.DataProvider(IImageDataProvider?)` and `Source.Provider(IImageDataProvider)` are flagged `SB0008` — a C#-authored data provider will never be called back from Swift; only providers that originated in Swift round-trip. (`Source.TryGetProvider(out …)` carries `SB0006` for the same reason.)
- **`ICacheSerializer.Data(...)` / `Image(...)` called through the interface** are `[Obsolete(… "SB0003")]` — those returns aren't dispatchable through a protocol witness. Call them on the concrete type (`DefaultCacheSerializer`, `FormatIndicatedCacheSerializer`) instead. The same pattern applies to several other protocol-typed members that return Swift optionals.
- **Custom Swift-protocol conformances written in C#** (a bespoke `IImageProcessor`, `IImageDownloaderDelegate`, …) are generally *declarable*, but where the member is flagged `SB0008` the reverse-dispatch proxy could not be generated and Swift will never invoke your implementation. Prefer composing the built-in types.
- **`IRetryStrategy` is `[Obsolete("SB0010")]` — the whole interface.** Its single requirement, `Retry(RetryContext, Action<RetryDecision>)`, is not reverse-dispatchable, so a C#-authored retry strategy compiles but is never called back. Use Kingfisher's own `DelayRetryStrategy` (e.g. `KingfisherOptionsInfoItem.RetryStrategy(new DelayRetryStrategy(3))`); *consuming* a Swift-vended strategy through the interface is unaffected.
- **SwiftUI (`KFImage`) is not bound.** The generated `Kingfisher.SwiftUIBridge` file emits no public surface — this package is UIKit-only.
- **`DownloadImageAsync` / `StoreAsync` overload ambiguity.** See the downloader section: overloads that differ only by an optional parameter need an explicit named argument or the parsed-options form.
- **One `…AsyncAsync` name on `ImageCache`.** The full-fidelity async cache probe is spelled `ImageCachedTypeAsyncAsync(key, identifier, forcedExtension, callbackQueue, ct)` — the generator refused `ImageCachedTypeAsync(string, string, CancellationToken)` as ambiguous with the four-parameter form and fell back to appending a second `Async`. The short forms you will normally use, `ImageCachedTypeAsync(key)` and `ImageCachedTypeAsync(key, identifier, forcedExtension)`, are unaffected; reach for the doubled name only when you need to pin the callback queue.

## Memory & threading

Generated Kingfisher types implement `ISwiftObject` / `IDisposable`. `using var` is the recommended pattern for deterministic cleanup; `Dispose` is safe on every generated type and double-`Dispose` is a no-op.

```csharp
using var processor = new RoundCornerImageProcessor(10.0);
using var expiration = StorageExpiration.Days(7);
using var result = await KingfisherManager.Shared.RetrieveImageAsync(resource);
```

- **Do not dispose the shared singletons.** `KingfisherManager.Shared`, `ImageCache.Default`, `ImageDownloader.Default` and `DefaultImageProcessor.Default` return cached instances owned by the library. Instances you create yourself (`new ImageCache("MyCache")`, `new ImageDownloader("MyDownloader")`) are yours to dispose.
- **Builder chains.** Each `KF.Builder` method returns a `KF.Builder`; hold the value you started the chain with in a `using var` and treat the intermediates as fire-and-forget — they are reclaimed by the finalizer.
- **Callbacks and completion handlers** run on Kingfisher's own queues unless you pass `KingfisherOptionsInfoItem.CallbackQueue(CallbackQueue.MainAsync)`. Marshal to the UI thread yourself (`InvokeOnMainThread`) before touching UIKit if you don't.
- **`await` continuations** resume on the captured synchronization context as usual; the `…Async` methods honour the `CancellationToken` you pass.
- `Source`, `RetrieveImageResult` and several option types are marked `[SwiftSendable]` and may be shared across .NET threads without external synchronization.

## Reference links

For full API semantics — option behaviour, cache lifecycle, processor maths — consult Kingfisher's own documentation and translate using the rules above:

- [Kingfisher documentation (DocC on Swift Package Index)](https://swiftpackageindex.com/onevcat/Kingfisher/master/documentation/kingfisher) — the vendor's canonical API reference
- [Getting started](https://swiftpackageindex.com/onevcat/kingfisher/master/documentation/kingfisher/gettingstarted) · [Common tasks](https://swiftpackageindex.com/onevcat/kingfisher/master/documentation/kingfisher/commontasks) · [Cache](https://swiftpackageindex.com/onevcat/kingfisher/master/documentation/kingfisher/commontasks_cache) · [Downloader](https://swiftpackageindex.com/onevcat/kingfisher/master/documentation/kingfisher/commontasks_downloader) · [Processor](https://swiftpackageindex.com/onevcat/kingfisher/master/documentation/kingfisher/commontasks_processor)
- [Kingfisher wiki](https://github.com/onevcat/Kingfisher/wiki) — cheat sheet and older guides
- [Kingfisher on GitHub](https://github.com/onevcat/Kingfisher) — upstream source (this package pins **8.11.0**)
- [SwiftBindings troubleshooting (SB0001 / SB0003 / SB0005 / SB0008 / SB0009 diagnostics)](https://github.com/justinwojo/swift-dotnet-bindings/wiki/Troubleshooting)
