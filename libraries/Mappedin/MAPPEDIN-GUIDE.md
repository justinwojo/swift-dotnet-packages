# Mappedin for .NET — Usage Guide

`SwiftBindings.Mappedin` binds the [Mappedin iOS SDK](https://developer.mappedin.com/ios-sdk/getting-started) (v6) to C# using .NET 10's native Swift interop. Mappedin v6 renders indoor maps in a `WKWebView` driven by a Swift bridge, so the C# surface mirrors the Swift SDK closely: you create a `MapView`, add its `View` to your UIKit hierarchy, load map data with your Mappedin credentials, then call into `Camera`, `Markers`, `Labels`, `Navigation`, `Search` and `BlueDot`.

This guide teaches the Swift→C# translation and the flows most apps need. For full API semantics always consult the [vendor documentation](#native-documentation-links).

## Contents

- [Requirements & install](#requirements--install)
- [Naming and translation conventions](#naming-and-translation-conventions)
- [Quick start: show a map](#quick-start-show-a-map)
- [Floors and floor stacks](#floors-and-floor-stacks)
- [Finding map features](#finding-map-features)
- [Events (click, hover, floor change)](#events-click-hover-floor-change)
- [Markers and labels](#markers-and-labels)
- [Camera](#camera)
- [Wayfinding](#wayfinding)
- [Blue Dot](#blue-dot)
- [Known limitations](#known-limitations)
- [Memory & threading](#memory--threading)
- [Native documentation links](#native-documentation-links)

## Requirements & install

```
dotnet add package SwiftBindings.Mappedin
```

| | |
|---|---|
| Target framework | `net10.0-ios` (single TFM) |
| Minimum OS | iOS 15.0 |
| SDK | .NET 10.0+, built on a macOS host |
| Upstream | Mappedin iOS SDK **6.7.0** ([MappedIn/ios](https://github.com/MappedIn/ios)) |

You also need a Mappedin developer account: a **key**, a **secret**, and a **map id**. Get them from the [Mappedin developer dashboard](https://developer.mappedin.com/docs/overview). The NuGet package ships the binding metadata and the Mappedin-licensed xcframework; usage is governed by Mappedin's terms.

Usings you will typically need:

```csharp
using Mappedin;          // the generated binding namespace (module-level, not Swift.Mappedin)
using Swift;             // SwiftResult, SwiftOptional, SwiftArray
using Swift.Runtime;     // ExistentialContainer0/1
using UIKit;             // hosting the map view
```

## Naming and translation conventions

**Async completion handlers become `Action<SwiftResult<…>>`.** Swift's `(Result<T, Error>) -> Void` completions surface as a trailing `Action` parameter. The failure side is always `ExistentialContainer1` — a boxed Swift `Error` existential with no readable message from C#, so treat failures as opaque:

```csharp
mapView.CurrentFloor(result =>
{
    if (!result.IsSuccess) { /* opaque Swift Error */ return; }
    Floor? floor = result.Success;       // SwiftOptional<Floor> converts implicitly to Floor?
    Console.WriteLine(floor?.Name);
});
```

Three success shapes recur:

| Swift result | C# generic argument | How to read it |
|---|---|---|
| `Result<T?, Error>` | `SwiftOptional<T>` | `result.Success` → implicit `T?`, or `.HasValue` / `.Value` |
| `Result<Void, Error>` / `Result<Any?, Error>` | `SwiftVoid` / `SwiftOptional<ExistentialContainer0>` | ignore the payload, check `result.IsSuccess` |
| `Result<[T], Error>` | `SwiftArray<T>` | implements `IReadOnlyList<T>`; `ToList()` / `foreach` |

`SwiftResult` also offers `TryGetSuccess(out …)`, `TryGetFailure(out …)` and `Match(onSuccess, onFailure)`.

**Swift enums with associated values become classes** with static factory methods, a `Tag` property of a nested `CaseTag` enum, and `TryGet…(out …)` accessors:

```csharp
using var target = NavigationTarget.Space(someSpace);

if (feature.TryGetSpace(out var space))
    Console.WriteLine(space.Name);
// or: switch (feature.Tag) { case MapFeatureOfType.CaseTag.Space: … }
```

Enums of this shape include `NavigationTarget`, `FocusTarget`, `MapFeatureOfType`, `QueryAtResult`, `Places`, and `FindNearestResult.FeatureKind`.

**Swift string-backed enums become classes** with cached static singletons plus `RawValue` / `FromRawValue`:

```csharp
FollowMode mode = FollowMode.PositionAndHeading;
string raw = mode.RawValue;                       // "positionAndHeading" — Swift declares no
                                                  // explicit raw values, so it is the case name
var parsed = FollowMode.FromRawValue(raw);        // null if unknown
```

Examples: `FollowMode`, `MapDataType`, `EasingFunction`, `BearingType`, `ConnectionType`, `CollisionRankingTier`, `BlueDotStatus`.

**Optionals become nullable types**, and option objects use optional C# parameters — `new Show3DMapOptions()` is valid and matches Swift's all-defaults initializer.

**Object lifetime.** Generated types implement `ISwiftObject` / `IDisposable`. Use `using var` for short-lived values (coordinates, option objects, targets); double-`Dispose` is a no-op. Long-lived objects like the `MapView` should be held in a field and disposed when the screen goes away.

## Quick start: show a map

`MapView` has a parameterless constructor. Its `View` property is a `MapViewController`, which derives from `WebKit.WKWebView` — add it to your view hierarchy like any `UIView`, then load data and render.

```csharp
using Mappedin;
using Swift;
using UIKit;

public sealed class MapScreen : UIViewController
{
    private MapView? _mapView;

    public override void ViewDidLoad()
    {
        base.ViewDidLoad();

        _mapView = new MapView();

        var webView = _mapView.View;            // MapViewController : WKWebView
        webView.Frame = View!.Bounds;
        webView.AutoresizingMask = UIViewAutoresizing.FlexibleWidth | UIViewAutoresizing.FlexibleHeight;
        View.AddSubview(webView);

        LoadMap();
    }

    private void LoadMap()
    {
        using var options = new GetMapDataWithCredentialsOptions(
            key: "mik_…",
            secret: "mis_…",
            mapId: "your-map-id");

#pragma warning disable SB0001    // see Known limitations
        _mapView!.GetMapData(options, result =>
        {
            if (!result.IsSuccess) return;

            using var show = new Show3DMapOptions();      // all defaults
            _mapView.Show3dMap(show, _ => { /* map is rendered */ });
        });
#pragma warning restore SB0001
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _mapView?.Dispose();
        base.Dispose(disposing);
    }
}
```

`GetMapDataWithCredentialsOptions` takes many further optional arguments (`language`, `environment`, `baseUri`, `search`, `analytics`, `layoutId`, `viewId`, …). If you mint access tokens server-side, use `GetMapDataWithAccessTokenOptions(accessToken, mapId, …)` instead — both implement `IGetMapDataOptions`.

`Show3DMapOptions` accepts `initialFloor`, `bearing`, `pitch`, `zoomLevel`, `preloadFloors`, `screenOffsets`, `outdoorView`, `style`, `shadingAndOutlines` and more, all optional.

Two non-obsolete alternatives to `GetMapData` exist if you cache map data yourself: `HydrateMapData(backup, options, onResult)` and `HydrateMapDataFromURL(url, onResult)`.

## Floors and floor stacks

```csharp
mapView.CurrentFloor(r => { Floor? f = r.Success; });
mapView.CurrentFloorStack(r => { FloorStack? fs = r.Success; });

mapView.SetFloor("floor-id", r => { if (r.IsSuccess) { /* switched */ } });
mapView.SetFloorStack("stack-id", _ => { });

mapView.PreloadFloors(new[] { "floor-1", "floor-2" });
```

`mapView.Navigation.Floors` and `Navigation.FloorStacks` expose the floors involved in an active multi-floor route.

## Finding map features

Mappedin's Swift `mapData.getByType(_:)` / `getById(_:_:)` family is **not bound** (see [Known limitations](#known-limitations)), so you cannot enumerate every space in a map. Use one of these bound entry points instead.

**Features currently in the viewport** — `GetInView(type, onResult)`, where `type` is a Mappedin data type string such as `"space"`, `"point-of-interest"`, `"door"`, `"connection"`:

```csharp
mapView.GetInView("space", result =>
{
    if (!result.IsSuccess) return;
    foreach (var feature in result.Success)          // SwiftArray<MapFeatureOfType>
        if (feature.TryGetSpace(out var space))
            Console.WriteLine($"{space.Name} on floor {space.Floor}");
});
```

**Search** — the most reliable way to resolve a place by name:

```csharp
var search = mapView.MapData.Search;
search.Enable(_ =>
{
    search.Query("food court", result =>
    {
        if (!result.IsSuccess) return;
        SearchResult? hits = result.Success;
        foreach (var place in hits?.Places ?? Array.Empty<SearchResultPlaces>())
            if (place.Item.TryGetSpace(out var space))
                Console.WriteLine($"{space.Name} (score {place.Score})");
    });
});
```

`Search.Suggest(term, onResult)` returns `SwiftArray<Suggestion>` for type-ahead.

**Spatial queries** — `mapView.MapData.Query`:

```csharp
using var here = mapView.CreateCoordinate(43.6532, -79.3832, floorId: "floor-1");

mapView.MapData.Query.At(here, r =>
{
    foreach (var hit in r.Success ?? default!) { /* QueryAtResult payload enum */ }
});

mapView.MapData.Query.Nearest(here, new[] { MapDataType.Space, MapDataType.PointOfInterest }, null, r =>
{
    foreach (var near in r.Success ?? default!)
        if (near.Feature.TryGetSpace(out var space))
            Console.WriteLine($"{space.Name} — {near.Distance:F1}m");
});
```

**Click payloads** (below) also hand you fully materialised `Space`, `Area`, `MapObject` and `Marker` objects, which is often the easiest source of a feature to act on.

## Events (click, hover, floor change)

Typed `MapView.on(...)` is not bound. Subscribe through the `MapViewController` bridge instead: pass the event name from the `Events` (or `BlueDotEvents`) constants and decode the untyped payload with the matching payload type's `FromBridgeData`.

```csharp
var view = mapView.View;    // MapViewController

// On/Off are [SwiftMainActor] — call them on the UI thread.
view.On(Events.Click.EventName, payload =>
{
    var click = ClickPayload.FromBridgeData(payload);
    var space = click?.Spaces?.FirstOrDefault();
    if (space is not null)
        Console.WriteLine($"tapped {space.Name}");
});

view.On(Events.FloorChange.EventName, payload =>
{
    var change = FloorChangePayload.FromBridgeData(payload);
    Console.WriteLine($"now on {change?.Floor?.Name}");
});

view.Off(Events.Click.EventName);        // unsubscribe
```

`Events` exposes: `Click`, `Hover`, `CameraChange`, `CameraScreenOffsetsChange`, `FloorChange`, `FloorChangeStart`, `FacadesInViewChange`, `NavigationActivePathChange`, `NavigationConnectionClick`, `NavigationStateChange`. `ClickPayload`/`HoverPayload` carry `Coordinate`, `PointerEvent` and lists of `Spaces`, `Areas`, `Objects`, `Markers`, `Labels`, `Paths`, `Shapes`, `Models`, `Images`, `Facades`, `Floors`.

Spaces are not interactive by default — mark them interactive before expecting clicks:

```csharp
using var state = new GeometryUpdateState(interactive: true);
mapView.UpdateState(space, state, _ => { });
```

## Markers and labels

Anything implementing `IAnchorable` — `Space`, `Coordinate`, `MapObject`, `Area`, … — can anchor a marker or a label.

```csharp
mapView.Markers.Add(space, "<div class='pin'>You are here</div>", null, r =>
{
    if (r.IsSuccess) { Marker marker = r.Success; /* keep to move/remove later */ }
});

using var labelOptions = new AddLabelOptions(interactive: true);
mapView.Labels.Add(space, space.Name, labelOptions, _ => { });

mapView.Markers.AnimateTo(marker, otherSpace, null, _ => { });
mapView.Markers.Remove(marker, _ => { });
mapView.Markers.RemoveAll(_ => { });
mapView.Labels.RemoveAll(_ => { });
```

Marker content is raw HTML rendered by the underlying web view. `AddMarkerOptions` controls `dynamicResize`, `enabled`, `interactive` (`AddMarkerOptions.InteractiveKind.True/.False/.PointerEventsAuto`) and low-priority pin behaviour. It has an all-defaults constructor (`new AddMarkerOptions()`) alongside the explicit overloads, and the `options` parameter is nullable, so passing `null` is equally fine when you don't need it.

## Camera

```csharp
var camera = mapView.Camera;

camera.FocusOn(space, _ => { });                       // also: Coordinate, Area, MapObject, Floor,
                                                       // Shape, FloorStack, EnterpriseLocation, FocusTarget
using var target = new CameraTarget(bearing: 90, pitch: 45, zoomLevel: 19);
camera.AnimateTo(target, _ => { });

camera.ZoomLevel(r => Console.WriteLine(r.Success));
camera.Bearing(r => { });
camera.Center(r => { });
camera.CancelAnimation(_ => { });

camera.Interactions.Disable(_ => { });                 // lock user gestures
```

`FocusOn(IEnumerable<FocusTarget>, options, onResult)` frames several targets at once; build members with `FocusTarget.Space(...)`, `FocusTarget.Coordinate(...)`, etc.

## Wayfinding

Directions come from `MapData`; drawing them is `Navigation`'s job.

```csharp
using var from = NavigationTarget.Space(entrance);
using var to   = NavigationTarget.Space(store);

mapView.MapData.GetDirections(from, to, result =>
{
    if (!result.IsSuccess) return;
    Directions? directions = result.Success;
    if (directions is null) return;

    Console.WriteLine($"{directions.Distance:F0} m, {directions.Instructions.Count} steps");
    foreach (var step in directions.Instructions)
        Console.WriteLine($"  {step.Action} — {step.Distance:F0} m");

    mapView.Navigation.Draw(directions, _ => { });
});
```

The single-target-to-single-target form takes exactly three arguments — there is no options parameter on it. Pass options through one of the collection overloads instead (see below).

`NavigationTarget` factories cover `Space`, `Coordinate`, `MapObject`, `Annotation`, `Door`, `PointOfInterest`, `Connection`, `EnterpriseLocation`, `Node`, `Area`, `Facade`, `LocationProfile`. Overloads accept `IEnumerable<NavigationTarget>` on either side for closest-of-many routing, and `GetDirectionsMultiDestination(from, to, options, onResult)` handles ordered multi-stop trips.

> On the collection overloads, **always pass the `options` argument** — even as `null`. The three-argument (options-less) collection forms are `SB0001` stubs; the four-argument forms taking `GetDirectionsOptions?` are not. This applies to `GetDirectionsMultiDestination` too: `GetDirectionsMultiDestination(from, to, options, onResult)` is fully callable, `GetDirectionsMultiDestination(from, to, onResult)` is a stub.

`GetDirectionsOptions(accessible: true)` restricts routes to accessible connections; other options cover excluded connections, non-public paths, smoothing and zones.

Other `Navigation` members: `Clear`, `SetActivePath`, `SetActivePathByIndex`, `HighlightPathSection(from, to, onResult)`, `ClearHighlightedPathSection`, `TravelledFraction`, `IsTracking`, `StopTracking`.

## Blue Dot

```csharp
var blueDot = mapView.BlueDot;

blueDot.Enable(_ => { });
blueDot.WatchDevicePosition(true, _ => { });
blueDot.WatchDeviceOrientation(true, _ => { });
blueDot.Follow(FollowMode.PositionAndHeading);

// Manual positioning (e.g. from your own positioning stack)
using var pos = new BlueDot.ForcePositionTarget(43.6532, -79.3832, heading: 90, floorLevel: (nint)1);
blueDot.ForcePosition(pos, _ => { });

blueDot.GetCoordinate(r => { Coordinate? c = r.Success; });
blueDot.GetStatus(r => { });
blueDot.Disable(_ => { });
```

Blue Dot events use the same bridge pattern as map events, via `BlueDotEvents.PositionUpdate`, `.StatusChange`, `.FollowChange`, `.Click`, `.Error`, `.AnchorSet` and friends — subscribe with `mapView.View.On(BlueDotEvents.PositionUpdate.EventName, …)`. `BlueDot.Update(...)` is marked `[Obsolete]` upstream; use `ForcePosition` or `ReportPosition` instead.

## Known limitations

**`SB0001` stubs.** 20 members across the module are emitted as `[Obsolete(DiagnosticId = "SB0001")]`: *"No `@_cdecl` wrapper or native thunk available. P/Invoke calling convention may not match Swift ABI."* They compile with a warning and may or may not behave correctly at runtime.

They share a shape: **a Swift collection, dictionary or protocol-existential parameter (`IEnumerable<T>`, `IDictionary<…>`, `IAnchorable`, `IGetMapDataOptions`) alongside a completion closure.** Where the same member also has an overload carrying an options argument, that longer overload is *not* stubbed — so the usual workaround is simply to call the longer form and pass `null` for options. All 20, grouped:

| Member | Workaround |
|---|---|
| `MapView.GetMapData(IGetMapDataOptions, …)` | none — it is the only credential entry point; suppress `SB0001` and validate on device. `HydrateMapData` / `HydrateMapDataFromURL` avoid it if you cache map data yourself. |
| `MapViewController.GetMapData` / `.Show3dMap` (dictionary overloads) | use the `MapView` equivalents |
| `MapData.GetDirections` — the three-argument collection overloads | call the four-argument form and pass `options: null` |
| `MapData.GetDirectionsMultiDestination` (3-arg) | call the 4-arg form, passing `options: null` |
| `MapData.GetGeoJSON` | read `Space.GeoJSON` / `Feature` properties directly |
| `Query.Nearest(origin, include, onResult)` (3-arg) | call `Nearest(origin, include, options, onResult)` with `options: null` |
| `MapView.Tween` | drive animation via `AnimateState` / `Camera.AnimateTo` |
| `Markers.SetPosition` | `Markers.AnimateTo(marker, target, options, onResult)` |
| `Camera.GetFocusOnTransform(IEnumerable<FocusTarget>, …)` | use `Camera.FocusOn` and read the camera back with `Bearing` / `Center` / `ZoomLevel` |
| `Icons.GetByTags(IEnumerable<string>, …)` | `Icons.GetByType(type, onResult)` |
| `Image3D.Add(IAnchorable, string, AddImageOptions, …)` | none — suppress and validate on device |
| `Text3D.Remove(IEnumerable<Text3DView>)` / `Remove(IEnumerable<string>)` | remove one at a time via the single-target overloads |
| `Analytics.Capture` / `Analytics.UpdateState` (dictionary overloads) | none |
| `Style.SetFromStyleCollection` | pass style through `Show3DMapOptions.style` |
| `BlueDot.Off<T>(BlueDotEvent<T>)` | unsubscribe through `mapView.View.Off(eventName)` instead |

**Closure-typed APIs are not bound at all.** The generator skips members whose Swift signature takes non-trivial closure parameters. Missing entirely:

- `MapView.on` / `MapData.on` / `BlueDot.on` — typed event subscription. (`BlueDot.Off<T>` *is* emitted, but only as an `SB0001` stub, and there is no matching `on` to pair it with.) Use the `MapViewController.On(eventName, Action<object?>)` bridge + `FromBridgeData` pattern shown above.
- **`MapData.getByType`, `getById`, `getByExternalId`** — the vendor's canonical way to enumerate spaces and POIs. This is the biggest gap: you cannot walk the full map graph from C#. Reach features via `GetInView`, `Search.Query`, `Query.At` / `Query.Nearest`, or click payloads instead.

**Bridge-typed values.** Some options and results surface as `IReadOnlyDictionary<string, object>` / `ExistentialContainer0` rather than typed models. The `MapView.Builders` helpers (`AccessToken`, `GetMapDataOptionsFromKeys`, `Show3dMapOptions`, …) build those dictionaries, but note they return `IReadOnlyDictionary` while `MapViewController.GetMapData` expects `IDictionary` — wrap with `new Dictionary<string, object>(builderResult)` if you go that route. Prefer the typed `GetMapDataWithCredentialsOptions` / `Show3DMapOptions` path.

## Memory & threading

- Types implementing `ISwiftObject` are `IDisposable`. Use `using var` for transient values (`Coordinate`, `NavigationTarget`, option objects) and dispose the `MapView` when the hosting controller goes away. Double-`Dispose` is safe.
- Do **not** dispose objects you have handed to the SDK and still need — e.g. a `Marker` you intend to move later, or a `Directions` you are about to draw.
- `MapViewController` members are annotated `[SwiftMainActor]` and assert the main thread — `On`, `Off`, `LoadBridge`, `CallMapView`, extension registration. Completion callbacks are invoked from the bridge; marshal to the UI thread (`UIApplication.SharedApplication.InvokeOnMainThread`) before touching UIKit or the controller.
- The map is a `WKWebView`. Standard web view rules apply: it must be in the view hierarchy and sized before `Show3dMap` renders anything meaningful.

## Native documentation links

For full API semantics — every option field, event payload shape and rendering behaviour — read Mappedin's own docs and translate using the conventions above.

- **iOS SDK getting started** — https://developer.mappedin.com/ios-sdk/getting-started
- **iOS SDK API reference** — https://developer.mappedin.com/ios-sdk/api-reference
- Spaces — https://developer.mappedin.com/ios-sdk/spaces
- Markers — https://developer.mappedin.com/ios-sdk/markers
- Labels — https://developer.mappedin.com/ios-sdk/labels
- Camera — https://developer.mappedin.com/ios-sdk/camera
- Wayfinding — https://developer.mappedin.com/ios-sdk/wayfinding
- Blue Dot — https://developer.mappedin.com/ios-sdk/blue-dot
- Migration guide (v5 → v6) — https://developer.mappedin.com/ios-sdk/migration-guide
- Release notes — https://developer.mappedin.com/ios-sdk/release-notes
- Platform overview & dashboard — https://developer.mappedin.com/docs/overview
- Upstream repo (pinned by `library.json`) — https://github.com/MappedIn/ios
