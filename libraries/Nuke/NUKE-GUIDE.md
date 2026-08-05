# Nuke for .NET — Usage Guide

`SwiftBindings.Nuke` exposes [Nuke](https://github.com/kean/Nuke) — Kean's image loading and caching framework for Apple platforms — to C# through .NET 10's native Swift interop. These are direct Swift calls, not Objective-C proxy wrappers.

Nuke's core model translates almost one-for-one: you build an `ImageRequest`, hand it to an `ImagePipeline`, and get a `UIImage` (or `NSImage` on macOS) back. Swift's `async/await` surfaces as `Task<T>` with `CancellationToken` support, so the loading path feels idiomatic in C#. The corners that don't translate cleanly — nested-type renames, the built-in processors, `OptionSet` flags — are covered explicitly below.

This guide is written against the bindings generated for **Nuke 13.0.6** with **SwiftBindings.Sdk 0.19.0**.

## Contents

- [Requirements & install](#requirements--install)
- [Naming conventions](#naming-conventions)
- [Quick start: load an image](#quick-start-load-an-image)
- [The pipeline](#the-pipeline)
- [Requests and options](#requests-and-options)
- [Cache management](#cache-management)
- [Prefetching](#prefetching)
- [Tasks, progress, and cancellation](#tasks-progress-and-cancellation)
- [Processors](#processors)
- [Errors](#errors)
- [Known limitations](#known-limitations)
- [Memory & threading](#memory--threading)
- [Reference links](#reference-links)

## Requirements & install

- .NET 10.0+
- **Multi-TFM package** — `net10.0-ios`, `net10.0-macos`, `net10.0-tvos`
- Deployment minimums: iOS 15.0, macOS 12.0, tvOS 15.0 (set your app's own floor with `<SupportedOSPlatformVersion>`)
- macOS host for development

```
dotnet add package SwiftBindings.Nuke
```

```csharp
using Nuke;
using Swift;            // runtime types (SwiftResult, CGSize, …)
using Swift.Runtime;    // ISwiftObject, SwiftObjectHelper<T>
```

> **Child namespaces aren't imported by `using Nuke;`.** Nuke's caseless-enum containers — `ImageProcessors`, `ImageDecoders`, `ImageProcessingOptions` — project as **child namespaces** of `Nuke`, not as static types. `using Nuke;` brings in the top-level types (`ImagePipeline`, `ImageRequest`, `ImageCache`, …) but not those. Reference them fully-qualified or alias them:
>
> ```csharp
> using ImageProcessors = Nuke.ImageProcessors;
> using ImageDecoders = Nuke.ImageDecoders;
> using ImageProcessingOptions = Nuke.ImageProcessingOptions;
> ```

**Platform image type.** The binding is generated per-TFM against the platform's native image type, so the same call has a different return type depending on your target:

| TFM | Image type |
|---|---|
| `net10.0-ios`, `net10.0-tvos` | `UIKit.UIImage` |
| `net10.0-macos` | `AppKit.NSImage` |

In multi-targeted code, guard with `#if IOS || TVOS` / `#if MACOS`, or type the result with `var`.

## Naming conventions

| Swift | C# | Rule |
|---|---|---|
| `ImagePipeline.Configuration` (nested type) + `pipeline.configuration` (property) | type → `ImagePipeline.ConfigurationInfo`; property stays `Configuration` | **A nested type whose name collides with a member gets a disambiguating suffix.** Non-enum types get `Info`; enums get `Kind`. The *member* keeps its natural name. |
| `ImagePipeline.Cache` / `pipeline.cache` | `ImagePipeline.CacheInfo` / `.Cache` | same rule |
| `ImageRequest.Options` / `request.options` | `ImageRequest.OptionsInfo` / `.Options` | same rule |
| `ImageRequest.Priority` / `request.priority` | `ImageRequest.PriorityKind` / `.Priority` | enum → `Kind` suffix |
| `ImageTask.State` / `task.state` | `ImageTask.StateKind` / `.State` | enum → `Kind` suffix |
| `ImageResponse.CacheType` / `response.cacheType` | `ImageResponse.CacheTypeKind` / `.CacheType` | enum → `Kind` suffix |
| `func image(for:) async throws -> UIImage` | `Task<UIImage> ImageAsync(…, CancellationToken)` | `async` methods get an `Async` suffix and an optional `CancellationToken` |
| `func imageTask(with:) -> ImageTask` | `ImageTask ImageTask(ImageRequest)` | method names are PascalCased; collisions with the return type are fine in C# |
| `func trim(toCost:)` / `func trim(toCount:)` | `TrimToCost(nint)` / `TrimToCount(nint)` | **Swift overloads that would collide in C# are named from their argument labels.** `ImageCache.Trim` is the only place in this binding where that applies. |
| `let (data, response) = try await pipeline.data(for:)` | `Task<(byte[], NSUrlResponse?)> DataAsync(…)` | Swift tuple returns become C# value tuples |
| `Data` | `byte[]` | `Foundation.Data` parameters/returns project as `byte[]` |
| `URL` / `URLRequest` | `Foundation.NSUrl` / `Foundation.NSUrlRequest` | Foundation reference types bridge through ObjC |
| `TimeInterval?` | `double?` | Swift optionals become C# nullables |
| `enum DataCachePolicy` (no payload) | `enum DataCachePolicy : int` | payload-free Swift enums become plain C# enums |
| `struct Options: OptionSet` | class with `RawValue` + static members + `new OptionsInfo(ushort)` + `\|` `&` `^` `~` `Contains` | option sets get their set operators — see below |
| `ImagePipeline.Error` (enum with payloads) | class with a `CaseTag` + static singletons + `TryGet…` accessors | payload-carrying Swift enums project as a class |

Two structural notes that follow from the table:

**Option sets combine with the usual operators.** `ImageRequest.OptionsInfo` and `ImagePipeline.CacheInfo.Caches` are Swift `OptionSet`s, and the generator emits `|`, `&`, `^`, `~` and `Contains` alongside the named members and `RawValue`:

```csharp
var opts = ImageRequest.OptionsInfo.DisableMemoryCacheReads
         | ImageRequest.OptionsInfo.DisableDiskCacheWrites;

bool skipsMemoryReads = opts.Contains(ImageRequest.OptionsInfo.DisableMemoryCacheReads);
```

`ImagePipeline.CacheInfo.Caches` works the same way but with `nint RawValue` and a `new Caches(nint)` constructor. It already ships `Memory`, `Disk`, and `All`, which covers most uses.

> **Each operator allocates.** These option sets project as C# *classes*, so every `|`/`&`/`^`/`~` builds a fresh native instance and the intermediates in a chain (`a | b | c`) are only reclaimed by the finalizer. That is fine for the once-per-request use above; don't build option sets in a hot loop without holding and disposing the result. The raw-value form (`new ImageRequest.OptionsInfo((ushort)(a.RawValue | b.RawValue))`) is still available and allocates once.

**Payload enums are classes.** `ImagePipeline.Error` has static singletons for payload-free cases (`Error.Cancelled`, `Error.DataIsEmpty`, `Error.PipelineInvalidated`, …), static factories for payload cases (`Error.DataLoadingFailed(err)`), a `Tag` property of type `Error.CaseTag`, and `TryGet…` accessors that unpack payloads.

## Quick start: load an image

```csharp
using Nuke;

var pipeline = ImagePipeline.Shared;

using var request = new ImageRequest("https://example.com/photo.jpg");
UIKit.UIImage image = await pipeline.ImageAsync(request);

imageView.Image = image;
```

`ImageAsync` takes an optional `CancellationToken`, and there's an `NSUrl` overload when you don't need a full request:

```csharp
using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
try
{
    var image = await pipeline.ImageAsync(new Foundation.NSUrl(url), cts.Token);
}
catch (OperationCanceledException)
{
    // cancelled; the underlying Swift task is cancelled too
}
```

Concurrent loads are just `Task.WhenAll` — the pipeline coalesces duplicate requests internally:

```csharp
var images = await Task.WhenAll(
    urls.Select(u => pipeline.ImageAsync(new ImageRequest(u))));
```

To fetch the raw bytes without decoding, use `DataAsync`, which returns a tuple:

```csharp
var (data, urlResponse) = await pipeline.DataAsync(request);
Console.WriteLine($"{data.Length} bytes, {urlResponse?.Url}");
```

## The pipeline

`ImagePipeline.Shared` is the default instance and is **settable** — the usual way to install app-wide configuration:

```csharp
var config = new ImagePipeline.ConfigurationInfo
{
    DataCachePolicy = ImagePipeline.DataCachePolicy.StoreAll,
    IsProgressiveDecodingEnabled = true,
    IsTaskCoalescingEnabled = true,
    IsRateLimiterEnabled = true,
};
config.DataCache = new DataCache("com.myapp.images");

ImagePipeline.Shared = new ImagePipeline(config);
```

> **Nuke 13 removed the parameterless `ImagePipeline()`.** A configuration is always required: `new ImagePipeline(new ImagePipeline.ConfigurationInfo())` is the "defaults" form. The constructor also takes an optional `ImagePipeline.IDelegate`.

Two ready-made configurations are exposed as static properties, plus a factory that names the on-disk cache:

```csharp
var httpCached = ImagePipeline.ConfigurationInfo.WithURLCache;   // HTTP disk cache
var aggressive = ImagePipeline.ConfigurationInfo.WithDataCache;  // Nuke DataCache, 150 MB
var named = ImagePipeline.ConfigurationInfo.WithDataCacheMethod("com.myapp.images", sizeLimit: 200_000_000);
```

Notable `ConfigurationInfo` members (all get/set unless noted):

| Member | Type | |
|---|---|---|
| `DataLoader` | `IDataLoading` | defaults to Nuke's `DataLoader`; you can read it but not build a replacement — see [Known limitations](#known-limitations) |
| `DataCache` | `IDataCaching?` | disk cache; `null` disables it |
| `ImageCache` | `IImageCaching?` | memory cache |
| `DataCachePolicy` | `ImagePipeline.DataCachePolicy` | `Automatic` / `StoreOriginalData` / `StoreEncodedImages` / `StoreAll` |
| `IsTaskCoalescingEnabled` | `bool` | dedupe identical in-flight requests |
| `IsDecompressionEnabled`, `IsRateLimiterEnabled`, `IsProgressiveDecodingEnabled`, `IsStoringPreviewsInMemoryCache`, `IsResumableDataEnabled`, `IsLocalResourcesSupportEnabled` | `bool` | behaviour toggles (the last covers `file://` / `data://` URLs) |
| `MaximumResponseDataSize` | `int?` | |
| `DataLoadingQueue`, `ImageDecodingQueue`, `ImageProcessingQueue`, `ImageEncodingQueue`, `ImageDecompressingQueue` | `TaskQueue` | concurrency knobs |
| `IsSignpostLoggingEnabled` | `static bool` | os_signpost instrumentation |

`pipeline.Configuration` and `pipeline.Cache` are read-only. `pipeline.Invalidate()` cancels outstanding work and rejects new requests.

`DataLoader` also exposes the underlying URLSession bits:

```csharp
Foundation.NSUrlSessionConfiguration cfg = DataLoader.DefaultConfiguration;
Foundation.NSUrlCache urlCache = DataLoader.SharedUrlCache;
```

## Requests and options

The simplest constructor takes a URL string:

```csharp
using var request = new ImageRequest("https://example.com/photo.jpg");
```

Everything else is mutable after construction, which is usually easier than the wide constructors:

```csharp
using var request = new ImageRequest(url)
{
    Priority = ImageRequest.PriorityKind.High,
    Scale = 2.0f,
    ImageID = "avatar-42",                                     // custom cache key
    Thumbnail = new ImageRequest.ThumbnailOptions(maxPixelSize: 400f),
};
```

`PriorityKind` is `VeryLow`, `Low`, `Normal`, `High`, `VeryHigh`.

Read-only properties worth knowing: `Url` (`NSUrl?`), `UrlRequest` (`NSUrlRequest?`), `Description`, `Processors` (`IReadOnlyList<IImageProcessing>`), `UserInfo`.

The wide constructors exist if you prefer them — note that `processors` is **not** optional in those overloads, so pass an empty array when you don't need any. There is a matching set taking an `NSUrlRequest`, which is how you attach custom headers:

```csharp
using var request = new ImageRequest(
    new Foundation.NSUrl(url),
    Array.Empty<IImageProcessing>(),
    ImageRequest.PriorityKind.High,
    ImageRequest.OptionsInfo.DisableDiskCacheWrites);

var urlRequest = new Foundation.NSMutableUrlRequest(new Foundation.NSUrl(url));
urlRequest.Headers = Foundation.NSDictionary.FromObjectAndKey(
    new Foundation.NSString("Bearer …"), new Foundation.NSString("Authorization"));
using var authed = new ImageRequest(urlRequest, Array.Empty<IImageProcessing>());
```

**`OptionsInfo` flags:** `DisableMemoryCacheReads`, `DisableMemoryCacheWrites`, `DisableMemoryCache`, `DisableDiskCacheReads`, `DisableDiskCacheWrites`, `DisableDiskCache`, `ReloadIgnoringCachedData`, `ReturnCacheDataDontLoad`, `SkipDecompression`, `SkipDataLoadingQueue`. Combine them with `|` as shown in [Naming conventions](#naming-conventions).

**Thumbnails** decode at a reduced size and are much cheaper than loading full-resolution images you're going to shrink anyway:

```csharp
using var thumb = new ImageRequest.ThumbnailOptions(maxPixelSize: 200f);
// or size-based:
using var thumb2 = new ImageRequest.ThumbnailOptions(
    new Swift.CGSize(200, 200),
    ImageProcessingOptions.Unit.Points,
    ImageProcessingOptions.ContentMode.AspectFill);

request.Thumbnail = thumb;
```

## Cache management

Nuke has three cache layers, reachable three different ways.

**1. The unified façade — `pipeline.Cache` (`ImagePipeline.CacheInfo`).** This is the one you want most of the time; it queries memory and disk together and speaks `ImageRequest`:

```csharp
var cache = ImagePipeline.Shared.Cache;

bool cached = cache.ContainsCachedImage(request);
ImageContainer? container = cache.CachedImage(request);
if (container is not null)
    imageView.Image = container.Image;

cache.StoreCachedImage(container!, otherRequest);
cache.RemoveCachedImage(request);
cache.RemoveAll();
```

Every method has an overload taking `ImagePipeline.CacheInfo.Caches` (`Memory`, `Disk`, `All`) to scope the operation:

```csharp
cache.RemoveAll(ImagePipeline.CacheInfo.Caches.Memory);
bool onDisk = cache.ContainsCachedImage(request, ImagePipeline.CacheInfo.Caches.Disk);
```

Raw encoded data lives behind `CachedData` / `StoreCachedData` / `ContainsData` / `RemoveCachedData`; key derivation is exposed via `MakeImageCacheKey(request)` and `MakeDataCacheKey(request)`; and `cache[request]` / `cache[nsUrl]` indexers read and evict (`cache[request] = null;`).

**2. Memory cache — `ImageCache`.**

```csharp
var mem = ImageCache.Shared;         // read-only static; construct your own to replace it
mem.CostLimit  = 50_000_000;         // bytes
mem.CountLimit = 200;
mem.Ttl        = 300.0;              // double?, seconds
mem.EntryCostLimit = 0.1;

int count = mem.TotalCount;
int cost  = mem.TotalCost;

mem.TrimToCost(10_000_000);          // Swift's trim(toCost:)
mem.TrimToCount(100);                // Swift's trim(toCount:)
mem.RemoveAll();
```

Constructors: `new ImageCache()`, `new ImageCache(nint costLimit)`, `new ImageCache(nint costLimit, nint countLimit)`. Entries are addressable by key — `using var key = new ImageCacheKey(request); var hit = mem[key];`.

**3. Disk cache — `DataCache`.** A standalone key/value store on disk, keyed by `string`:

```csharp
var disk = new DataCache("com.myapp.images");
disk.SizeLimit     = 100_000_000;
disk.SweepInterval = 60.0;
disk.IsSweepEnabled = true;

disk.StoreData(bytes, "my-key");
bool has = disk.ContainsData("my-key");
byte[]? back = disk.CachedData("my-key");
disk.RemoveData("my-key");

disk.Flush();          // force pending writes
disk.Sweep();          // enforce the size limit now
disk.RemoveAll();

Foundation.NSUrl path = disk.Path;
int bytesUsed = disk.TotalSize;
```

Also available: `new DataCache(NSUrl path)`, the `filenameGenerator` overloads, `DataCache.Filename(key)`, `disk.Url(key)`, and a `this[string]` indexer.

> `DataCache.IsCompressionEnabled` **was removed in Nuke 13.0** — compression is now a separate concern. Any migration guide or blog post showing it predates 13.0.

## Prefetching

```csharp
var prefetcher = new ImagePrefetcher(
    ImagePipeline.Shared,
    ImagePrefetcher.Destination.MemoryCache,   // or .DiskCache — data only, no decode
    maxConcurrentRequestCount: 2);

prefetcher.Priority = ImageRequest.PriorityKind.Low;

prefetcher.StartPrefetching(urls.Select(u => new Foundation.NSUrl(u)).ToArray());
// or with full requests:
prefetcher.StartPrefetching(requests);

prefetcher.IsPaused = true;             // e.g. while the user is scrolling fast

prefetcher.StopPrefetching(requests);   // scope to specific items
prefetcher.StopPrefetching();           // cancel everything
```

`prefetcher.DidComplete` is an `Action?` callback fired when the queue drains.

`Destination.DiskCache` skips decoding entirely (cheaper CPU) but is incompatible with `DataCachePolicy.Automatic` for requests with processors, and with `StoreEncodedImages`.

## Tasks, progress, and cancellation

`pipeline.ImageTask(request)` starts a load and hands back an `ImageTask` you can steer:

```csharp
var task = ImagePipeline.Shared.ImageTask(request);

task.Priority = ImageRequest.PriorityKind.VeryHigh;   // re-prioritise mid-flight

ImageTask.Progress p = task.CurrentProgress;
Console.WriteLine($"{p.Completed}/{p.Total} ({p.Fraction:P0})");

var image = await task.GetImageAsync();               // or GetResponseAsync()
task.Cancel();
```

`task.State` is `ImageTask.StateKind` (`Running`, `Cancelled`, `Completed`). `task.Request` returns the originating `ImageRequest`; `task.TaskId` is a stable `ulong`.

For streaming progress and progressive previews, `task.Events` is an `IAsyncEnumerable<ImageTask.Event>`:

```csharp
await foreach (var evt in task.Events)
{
    switch (evt.Tag)
    {
        case ImageTask.Event.CaseTag.Progress
            when evt.TryGetProgress(out var progress):
            UpdateProgressBar(progress.Fraction);
            break;

        case ImageTask.Event.CaseTag.Preview
            when evt.TryGetPreview(out var preview):
            imageView.Image = preview.Image;         // progressive JPEG scan
            break;

        case ImageTask.Event.CaseTag.Finished
            when evt.TryGetFinished(out var result):
            // result is SwiftResult<ImageResponse, ImagePipeline.Error>
            break;
    }
}
```

Callback-style loading is also bound, returning a `SwiftResult`:

```csharp
var task = ImagePipeline.Shared.LoadImage(request, result =>
{
    // result: SwiftResult<ImageResponse, ImagePipeline.Error>
});
```

An `ImageResponse` carries `Image`, `Container`, `Request`, `UrlResponse`, `IsPreview`, and `CacheType` (`ImageResponse.CacheTypeKind?` — `Memory`, `Disk`, or `null` for a network load). `ImageContainer` adds `Data`, `Type` (`AssetType?` — `Png`, `Jpeg`, `Gif`, `Heic`, `Webp`, `Mp4`, `M4v`, `Mov`, `Ico`), and `UserInfo`.

## Processors

The built-in processors under the `ImageProcessors` namespace construct and run:

```csharp
using ImageProcessors = Nuke.ImageProcessors;

using var resize  = new ImageProcessors.Resize(width: 200.0);
using var resize2 = new ImageProcessors.Resize(
    new Swift.CGSize(200, 200),
    ImageProcessingOptions.Unit.Points,
    ImageProcessingOptions.ContentMode.AspectFill,
    crop: true);

using var circle  = new ImageProcessors.Circle();
using var rounded = new ImageProcessors.RoundedCorners(radius: 10.0);
using var blur    = new ImageProcessors.GaussianBlur(radius: 8);

UIKit.UIImage? processed = resize.Process(sourceImage);
```

Each exposes `Identifier`, `Description`, and `Process(image)`. `RoundedCorners` and `Circle` take an optional `ImageProcessingOptions.Border(UIColor color, double width = 1, Unit unit = Points)`. `ImageProcessingOptions.Unit` is `Points` / `Pixels`; `ContentMode` is `AspectFill` / `AspectFit`; both have a `GetDescription()` extension.

Every built-in processor implements `IImageProcessing`, so they can also be handed to the pipeline rather than applied by hand:

```csharp
using var request = new ImageRequest(
    new Foundation.NSUrl(url),
    new IImageProcessing[] { new ImageProcessors.Resize(width: 200.0),
                             new ImageProcessors.RoundedCorners(radius: 10.0) });

using var chained = new ImageProcessors.Composition(
    new IImageProcessing[] { resize, circle });
```

> **Two `IImageProcessing` members come from a Swift protocol extension and only work on the concrete type.** `HashableIdentifier` and the `Process(ImageContainer, ImageProcessingContext)` overload are declared on the interface as default implementations that throw `NotSupportedException` — Swift supplies them from an extension, which has no witness to dispatch through. Call them on `Resize` / `Circle` / … directly. `Identifier` and `Process(UIImage)` are real requirements and dispatch normally.

`CoreImageFilter` bridges arbitrary Core Image filters:

```csharp
using var sepia = new ImageProcessors.CoreImageFilter("CISepiaTone");
var output = sepia.Process(sourceImage);

// or apply a configured CIFilter directly:
var applied = ImageProcessors.CoreImageFilter.Apply(myCIFilter, sourceImage);
```

Decoders are similar — `new ImageDecoders.Default()` and `new ImageDecoders.Empty()` both construct and expose `Decode(byte[])` / `DecodePartiallyDownloadedData(byte[])`. Note that *registering* one is not reachable from C#: `ImageDecoderRegistry.Register` only exists in its closure-matching form, which is an `[Obsolete("SB0005")]` stub (see [Known limitations](#known-limitations)). `ImageDecoderRegistry.Shared.Clear()` works.

## Errors

Pipeline failures surface as `ImagePipeline.Error`, a class projecting Nuke's payload-carrying Swift enum.

Payload-free cases are static singletons — `Error.Cancelled`, `Error.DataIsEmpty`, `Error.DataMissingInCache`, `Error.ImageRequestMissing`, `Error.PipelineInvalidated`, `Error.DataDownloadExceededMaximumSize`. Payload cases are unpacked with `TryGet…`:

```csharp
if (error.Tag == ImagePipeline.Error.CaseTag.DataLoadingFailed
    && error.TryGetDataLoadingFailed(out var underlying))
{
    Console.WriteLine(underlying);          // Swift.Foundation.AnyError
}

// shortcut for the common case:
Swift.Foundation.AnyError? cause = error.DataLoadingError;
Console.WriteLine(error.Description);
```

The other cases are `DecoderNotRegistered(context)`, `DecodingFailed(decoder, context, error)`, and `ProcessingFailed(processor, context, error)`.

On the `async` path (`ImageAsync` / `DataAsync`) a failure throws; `OperationCanceledException` is what you get from a cancelled `CancellationToken`. `DataLoader.Error.StatusCodeUnacceptable(code)` covers HTTP status rejections, and `DataLoader.Validate(response)` is exposed as a static if you want to run the default validation yourself.

## Known limitations

The binding surface is close to complete. What isn't reachable:

- **`IImageProcessing.HashableIdentifier` and `Process(container, context)` throw through the interface** — they are Swift protocol-extension defaults with no witness to dispatch through (see [Processors](#processors)). Use the concrete type.
- **`ImagePipeline(delegate:configure:)`** is emitted as an `[Obsolete("SB0009")]` stub (an optional wider than one machine word with no wrapper) and throws `NotSupportedException`. Build a `ConfigurationInfo` and use `new ImagePipeline(config, delegate)` instead.
- **A C#-authored `ImagePipeline.IDelegate` is never called back.** `new ImagePipeline(config, delegate)` itself works, but the parameter carries an `[Obsolete("SB0008")]` warning: the existential's reverse-dispatch proxy could not be generated, so only a delegate value that originated in Swift round-trips. Configure behaviour through `ConfigurationInfo` instead.
- **A C#-authored `IDataLoading` is never called back either.** The whole interface carries `[Obsolete("SB0010")]` — no requirement of Swift's `DataLoading` is reverse-dispatchable, so a custom loader written in C# compiles, installs, and never fires. *Consuming* Nuke's own `DataLoader` through the interface is unaffected. Use `DataLoader` and its `URLSessionConfiguration` knobs.
- **`DataLoader` cannot be constructed from C#, and decoders cannot be registered.** Both types expose exactly one initializer/registration entry point and both take a closure the bridge can't marshal, so both are `[Obsolete("SB0005")]` stubs that throw `NotSupportedException`: `new DataLoader(configuration, validate)` (the `validate` closure returns an `Error?`) and `ImageDecoderRegistry.Register(match)`. Use the `DataLoader` the pipeline already installs — read it from `ConfigurationInfo.DataLoader`, and reach the session knobs through the `DataLoader.DefaultConfiguration` / `DataLoader.SharedUrlCache` statics. `ImageDecoderRegistry.Clear()` works; `Decoder(context)` is `[Obsolete("SB0001")]`.
- **`ImagePipeline.LoadImage(request, progress, completion)`** — the 3-argument progress-reporting overload — is `[Obsolete("SB0001")]` (no `@_cdecl` wrapper). It is soft-deprecated upstream in Nuke 12.9 anyway; use `task.Events` for progress.
- **Protocol-typed values are restricted.** When you hold a `IDataCaching` / `IImageCaching` / `IImageDecoding` / `IImageEncoding` / `IImageProcessing` / `IDataLoading` *interface reference* rather than a concrete type, members whose signature carries `Foundation.Data`, an optional return, a subscript, or a closure are `[Obsolete("SB0003")]` and throw. Concrete types (`DataCache`, `ImageCache`, `ImageDecoders.Default`) have no such restriction — call those directly. This mainly bites if you assign your own cache to `config.DataCache` and then try to read it back through the interface.
- **Deprecated-upstream members** are carried through as `[Obsolete]`: `ConfigurationInfo.MaximumDecodedImageSize` (use `ImageRequest.ThumbnailOptions`), `ImageRequest.ImageId` (use `ImageID`), and the `UserInfoKey.ImageIdKey` / `ScaleKey` / `ThumbnailKey` constants.
- **`ImageCache.Shared` is read-only**, unlike `ImagePipeline.Shared`. To install a custom memory cache, set `ConfigurationInfo.ImageCache` and build a pipeline.
- **macOS is metadata-verified, not exercised.** The macOS test app validates that the `macos-arm64_x86_64` slice loads and Swift metadata resolves for the core types; the full loading workflows are exercised on iOS Simulator (Mono JIT) and physical device (NativeAOT). tvOS is build-only.

## Memory & threading

Generated Nuke types implement `ISwiftObject` / `IDisposable`. `using var` is the recommended pattern for deterministic cleanup — `Dispose` is safe on every generated type and double-`Dispose` is a no-op.

- **Struct-backed types especially.** `ImageRequest`, `ImageResponse`, `ImageContainer`, `OptionsInfo`, `ThumbnailOptions`, `ConfigurationInfo`, `CacheInfo`, and the processor types wrap Swift structs and hold a native buffer. Creating them in a hot loop without disposing leaks until the finalizer runs.
- **Writes through a struct-valued property hit a copy.** Because those structs project as classes, `pipeline.Configuration.IsRateLimiterEnabled = false` compiles, runs, and changes nothing — the getter handed you a temporary. Build the value, mutate it, then assign the whole thing (`ImagePipeline.Shared = new ImagePipeline(config)`). The SDK's `SB1003` analyzer warning flags this shape at compile time.
- **Long-lived objects.** `ImagePipeline`, `ImageCache`, `DataCache`, and `ImagePrefetcher` are reference types you normally keep for the app's lifetime — don't wrap those in `using`.
- **Don't dispose static singletons.** `ImagePipeline.Shared`, `ImageCache.Shared`, `ImageDecoderRegistry.Shared`, `ImagePipeline.Error.Cancelled`, and the `OptionsInfo` / `AssetType` static members return cached instances.
- **`Sendable`.** Most Nuke value types are marked `[SwiftSendable]` and may be shared across .NET threads. The pipeline itself is thread-safe by design — `ImageAsync` from multiple threads concurrently is the intended usage and is validated in the test app.
- **Callbacks run off the main thread.** `LoadImage` completions, `task.Events`, and `prefetcher.DidComplete` fire on Nuke's internal queues. Marshal to the main thread before touching UIKit/AppKit.

## Reference links

For full API semantics, behaviour of the caching layers, and performance guidance, consult the upstream documentation — this guide only covers the C# translation.

- [Nuke — official documentation](https://kean-docs.github.io/nuke/documentation/nuke) — DocC reference for the whole framework
- [Nuke — Getting Started](https://kean-docs.github.io/nuke/documentation/nuke/getting-started/)
- [Nuke — Performance Guide](https://kean-docs.github.io/nuke/documentation/nuke/performance-guide)
- [kean/Nuke on GitHub](https://github.com/kean/Nuke) — source, releases, migration guides
- [Nuke migration guides](https://github.com/kean/Nuke/tree/master/Documentation/Migrations) — including the 12 → 13 changes reflected in this binding
