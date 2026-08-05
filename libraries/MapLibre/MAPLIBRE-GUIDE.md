# MapLibre for .NET — Usage Guide

`SwiftBindings.MapLibre` exposes [MapLibre Native iOS](https://maplibre.org/maplibre-native/ios/latest/documentation/maplibre/) — the open-source vector map rendering SDK — to C#. Unlike most packages in this repo, MapLibre's public iOS API is **pure Objective-C** (`MLNMapView` and the `MLN*` family, over a C++ rendering core), so the bindings go through the Objective-C `bgen` pipeline rather than .NET's native Swift interop. In practice that is good news: the projection is the same one Xamarin.iOS/MAUI developers have used for a decade, and the entire bound surface is callable — there are **no `SB0001` non-callable stubs** in this package.

This guide teaches the Objective-C → C# translation and walks the flows most apps need. For per-property semantics (what `circleRadius` actually does, what a style layer expects), read MapLibre's own docs — see [MapLibre documentation](#maplibre-documentation) — and translate with the rules below.

## Contents

- [Requirements & install](#requirements--install)
- [Objective-C → C# translation rules](#objective-c--c-translation-rules)
- [Quick start: a map view](#quick-start-a-map-view)
- [Delegate wiring](#delegate-wiring)
- [Camera & viewport](#camera--viewport)
- [Annotations & markers](#annotations--markers)
- [Runtime styling: sources, layers, expressions](#runtime-styling-sources-layers-expressions)
- [Querying rendered features](#querying-rendered-features)
- [User location](#user-location)
- [Offline packs](#offline-packs)
- [Snapshots](#snapshots)
- [Caveats & limitations](#caveats--limitations)
- [MapLibre documentation](#maplibre-documentation)

## Requirements & install

```
dotnet add package SwiftBindings.MapLibre
```

- .NET 10.0+, macOS host for development
- **Target framework: `net10.0-ios`** (iOS only — there is no macOS/tvOS/Mac Catalyst target). The package's `lib/` folder is pinned to the iOS SDK it was compiled against (`net10.0-ios26.0` in current builds), so your app's `<TargetFramework>` platform version must be **≥** that value or restore fails with `NU1202`.
- **Deployment minimum is separate.** MapLibre itself supports iOS 15.0+; set your app's real floor with `<SupportedOSPlatformVersion>`. A handful of members are gated with `[SupportedOSPlatform("ios14.0")]`-style attributes and the compiler will flag them.
- **No `<NativeReference>` needed.** The package embeds the native `MapLibre.xcframework` in the binding assembly's resource sidecar, so a NuGet consumer gets the framework copied and signed into `App.app/Frameworks/` automatically. (If you instead consume this repo by `<ProjectReference>` to `SwiftBindings.MapLibre.csproj`, you *do* need your own `<NativeReference Include="…/MapLibre.xcframework"><Kind>Framework</Kind></NativeReference>` — `NativeReference` does not flow across a project reference. See `libraries/MapLibre/tests/SwiftBindings.MapLibre.Tests.csproj`.)

```csharp
using MapLibre;        // MLNMapView, MLNStyle, MLNPointAnnotation, …
using CoreLocation;    // CLLocationCoordinate2D
using Foundation;      // NSUrl, NSExpression, NSPredicate, NSData
using UIKit;           // UIColor, UIView
```

The namespace is `MapLibre` (module-level, no `Swift.` prefix). No `using Swift;` / `using Swift.Runtime;` — those are for the Swift-interop packages; this one has no Swift runtime types at all.

## Objective-C → C# translation rules

| Objective-C | C# | Rule |
|---|---|---|
| `MLNMapView` | `MapLibre.MLNMapView` | class names are kept verbatim, `MLN` prefix included |
| `-[MLNStyle addLayer:]` | `style.AddLayer(layer)` | selectors → PascalCase methods; the first keyword names the method |
| `-[MLNStyle insertLayer:aboveLayer:]` | `style.InsertLayerAboveLayer(layer, sibling)` | multi-keyword selectors concatenate the keywords when overloading would be ambiguous — `insertLayer:atIndex:` and `insertLayer:belowLayer:` both collapse to overloads of `InsertLayer`, but `aboveLayer:` needed a distinct name |
| `@property NSURL *styleURL` | `map.StyleURL` | properties → PascalCase properties; `isEnabled`-style getters drop the `is` (`view.Enabled`) |
| `-initWithFrame:styleURL:` | `new MLNMapView(frame, styleUrl)` | initializers → constructors |
| `+[MLNPolyline polylineWithCoordinates:count:]` | `MLNPolyline.PolylineWithCoordinates(coords)` | class methods → static methods; a `T *` + `count:` pair collapses into a managed `T[]` |
| `@protocol MLNAnnotation` | `IMLNAnnotation` (interface) | protocols used as *types* project to `I`-prefixed interfaces |
| `@protocol MLNMapViewDelegate` | `MLNMapViewDelegate` (base class) | delegate protocols also get a `[Model]` base **class** you subclass and override |
| `id<MLNAnnotation>` param | `IMLNAnnotation` param | concrete types conform implicitly (`MLNPointAnnotation` is an `IMLNAnnotation`) |
| `NSArray<id<MLNAnnotation>> *` | `IMLNAnnotation[]` | ObjC arrays → managed arrays; `NSSet` stays `NSSet` (`style.Sources`) |
| `void (^)(NSError *)` block | `Action<NSError>` | blocks → `Action`/`Action<…>`; pass a lambda |
| `-removeSource:error:` | `bool RemoveSource(MLNSource s, out NSError err)` | trailing `NSError **` → `out NSError` + `bool` return |
| `MLNCoordinateBounds` (C struct) | `struct MLNCoordinateBounds { Sw; Ne; }` | C structs are blittable structs with PascalCase fields |
| `MLNOrnamentPositionBottomLeft` | `MLNOrnamentPosition.BottomLeft` | `NS_ENUM` cases get their common prefix stripped |
| `MLNMapDebugTileBoundariesMask` | `MLNMapDebugMaskOptions.MapDebugTileBoundariesMask` | `NS_OPTIONS` flag members are stripped too — the `MLN` module tag comes off |
| `MLNMapLibre` (a `MLNWellKnownTileServer`) | `MLNWellKnownTileServer.MapLibre` | same rule: the tag comes off even when the type name is not a prefix |

**Enum-case stripping rule (as of SDK 0.19.0).** A case first has the enum's own type name stripped if *every* case starts with it; failing that, the `MLN` module tag is stripped when every case carries it at a token boundary; failing that the case is left alone. The result is that `MLN` never survives on a case name in this package — including on `NS_OPTIONS` flags and `MLNWellKnownTileServer`, both of which kept it before 0.19.0. See [Caveats](#caveats--limitations) for the migration note.

Delegate methods drop the *receiver* segment of the selector — the protocol's own name with the `Delegate` role suffix removed. `mapViewDidFinishLoadingMap:` therefore loses only the leading `mapView` and keeps everything after it:

| Selector | C# override |
|---|---|
| `mapView:didFinishLoadingStyle:` | `DidFinishLoadingStyle(MLNMapView, MLNStyle)` |
| `mapViewDidFinishLoadingMap:` | `DidFinishLoadingMap(MLNMapView)` |
| `mapViewDidFailLoadingMap:withError:` | `DidFailLoadingMapWithError(MLNMapView, NSError)` |
| `mapViewDidFinishRenderingMap:fullyRendered:` | `DidFinishRenderingMapFullyRendered(MLNMapView, bool)` |
| `mapViewDidBecomeIdle:` | `DidBecomeIdle(MLNMapView)` |
| `mapView:regionDidChangeAnimated:` | `RegionDidChangeAnimated(MLNMapView, bool)` |
| `mapView:viewForAnnotation:` | `ViewForAnnotation(MLNMapView, IMLNAnnotation)` |

When in doubt, the authority is the generated `ApiDefinition.cs` (`obj/<Config>/net10.0-ios/swift-binding/ApiDefinition.cs`): every member carries its `[Export("selector")]`, so you can search by the ObjC selector from MapLibre's docs and read off the C# name.

**Object lifetime.** These are `NSObject`-derived peers, not Swift objects, so the usual .NET-for-iOS rules apply: retain a managed reference to anything the native side holds weakly (your delegate!), and `Dispose()`/`using var` is safe for short-lived value-ish objects (shapes, features, formatters, projections). Do **not** dispose an object the map still owns — remove it first (`RemoveAnnotation`, `RemoveLayer`, `RemoveFromSuperview`).

**Threading.** Every `MLNMapView` / `MLNStyle` API is main-thread only, and all delegate callbacks arrive on the main thread. Completion blocks (`FlyToCamera`, offline pack handlers) also run on the main queue unless you passed your own `DispatchQueue`.

## Quick start: a map view

```csharp
using CoreGraphics;
using CoreLocation;
using Foundation;
using MapLibre;
using UIKit;

public class MapViewController : UIViewController
{
    private MLNMapView? _map;
    private MyMapDelegate? _delegate;   // strong field — the map holds this weakly

    public override void ViewDidLoad()
    {
        base.ViewDidLoad();

        var styleUrl = new NSUrl("https://demotiles.maplibre.org/style.json");
        _map = new MLNMapView(View!.Bounds, styleUrl)
        {
            AutoresizingMask = UIViewAutoresizing.FlexibleWidth | UIViewAutoresizing.FlexibleHeight,
        };
        View.AddSubview(_map);

        _delegate = new MyMapDelegate();
        _map.WeakDelegate = _delegate;      // or: _map.Delegate = _delegate;

        // centre, zoom, animated
        _map.SetCenterCoordinate(new CLLocationCoordinate2D(48.8566, 2.3522), 11, false);
    }
}
```

Other constructors: `new MLNMapView(frame)` (default style), `new MLNMapView(frame, styleJson)` (inline style JSON), `new MLNMapView(frame, mlnMapOptions)`.

To point at a different tile server, use `MLNSettings` (static class properties) before creating the map:

```csharp
MLNSettings.UseWellKnownTileServer(MLNWellKnownTileServer.MapLibre);
MLNSettings.ApiKey = "…";   // only retained by tile servers that declare an API key
```

Built-in styles are enumerable without hardcoding URLs: `MLNStyle.PredefinedStyles()` returns `MLNDefaultStyle[]` (each with `Name` / `Version` / `Url`), and `MLNStyle.DefaultStyleURL()` gives the default.

## Delegate wiring

Subclass the generated `[Model]` class and override only what you need — every method is optional in ObjC and the base implementation no-ops.

```csharp
public sealed class MyMapDelegate : MLNMapViewDelegate
{
    public override void DidFinishLoadingStyle(MLNMapView mapView, MLNStyle style)
    {
        // The ONLY safe place to touch style sources/layers — mapView.Style is null
        // until this fires, and is reset whenever the style is reloaded.
        AddMyLayers(style);
    }

    public override void DidFinishLoadingMap(MLNMapView mapView) { }

    public override void DidFinishRenderingMapFullyRendered(MLNMapView mapView, bool fullyRendered) { }

    public override void RegionDidChangeAnimated(MLNMapView mapView, bool animated)
    {
        Console.WriteLine($"now at {mapView.CenterCoordinate.Latitude}, zoom {mapView.ZoomLevel}");
    }

    public override void DidFailLoadingMapWithError(MLNMapView mapView, NSError error)
        => Console.WriteLine($"style/map load failed: {error.LocalizedDescription}");
}
```

Keep a strong managed reference to the delegate for as long as the map lives — `WeakDelegate` is `ArgumentSemantic.Weak`, so a delegate held only by a local goes away and your callbacks silently stop.

Beyond the lifecycle callbacks, the same class carries the annotation hooks (`ViewForAnnotation`, `ImageForAnnotation`, `AnnotationCanShowCallout`, `DidSelectAnnotation`, `LineWidthForPolylineAnnotation`, `FillColorForPolygonAnnotation`, …), user-location hooks (`DidUpdateUserLocation`, `DidChangeUserTrackingModeAnimated`), and the low-level observability hooks (`SourceDidChange`, glyph/sprite/tile/shader events, `DidBecomeIdle`).

## Camera & viewport

```csharp
// Simple: centre + zoom (+ optional direction), with or without animation.
map.SetCenterCoordinate(new CLLocationCoordinate2D(51.5074, -0.1278), 12, animated: true);
map.SetZoomLevel(14, true);
map.SetDirection(90, true);
map.ResetNorth();

// Full camera: centre + altitude (metres) + pitch + heading.
var camera = MLNMapCamera.CameraLookingAtCenterCoordinateAltitudePitchHeading(
    new CLLocationCoordinate2D(35.6762, 139.6503), altitude: 9000, pitch: 30, heading: 45);
map.SetCamera(camera, animated: false);
map.Camera = camera;              // read/write property form
var current = map.Camera;         // .CenterCoordinate / .Altitude / .Pitch / .Heading / .ViewingDistance

// Animated flight with a completion block.
map.FlyToCamera(camera, duration: 1.5, () => Console.WriteLine("arrived"));

// Fit a bounding box.
var bounds = new MLNCoordinateBounds
{
    Sw = new CLLocationCoordinate2D(48.0, 1.0),
    Ne = new CLLocationCoordinate2D(50.0, 4.0),
};
map.SetVisibleCoordinateBounds(bounds, animated: true);
var fitted = map.CameraThatFitsCoordinateBounds(bounds, new UIEdgeInsets(20, 20, 20, 20));

// Limits + interaction toggles.
map.MinimumZoomLevel = 3; map.MaximumZoomLevel = 18; map.MaximumPitch = 60;
map.ScrollEnabled = true; map.RotateEnabled = false; map.PitchEnabled = true;
map.ShowsScale = true; map.CompassViewPosition = MLNOrnamentPosition.BottomLeft;

// Screen ↔ world projection (the second argument is the view the point is in).
CGPoint pt   = map.ConvertCoordinate(new CLLocationCoordinate2D(51.5, -0.12), map);
var    coord = map.ConvertPoint(pt, map);
double mpp   = map.MetersPerPointAtLatitude(51.5);
```

`MLNMapProjection` (`new MLNMapProjection(map)`) gives the same conversions as a standalone snapshot of the current camera, which is handy off the render loop.

Starting a new animated camera change **cancels** any flight in progress and its completion block never runs — sequence your animations.

## Annotations & markers

Annotations are the lightweight "pin" API (as opposed to style layers, which are the scalable, data-driven API).

```csharp
var pin = new MLNPointAnnotation
{
    Coordinate = new CLLocationCoordinate2D(51.5074, -0.1278),
};
pin.Title = "London";
pin.Subtitle = "UK";

map.AddAnnotation(pin);
map.AddAnnotations(new IMLNAnnotation[] { pin, other });

IMLNAnnotation[]? all = map.Annotations;
map.SelectAnnotation(pin, animated: true);      // shows the callout
map.ShowAnnotations(all!, new UIEdgeInsets(40, 40, 40, 40), animated: true);
map.RemoveAnnotations(new IMLNAnnotation[] { pin });
```

Custom marker appearance goes through the delegate, using either the image path (cheap, rendered by the map) or the view path (a real `UIView` per annotation):

```csharp
public override MLNAnnotationImage? ImageForAnnotation(MLNMapView mapView, IMLNAnnotation annotation)
{
    const string reuseId = "pin";
    var image = mapView.DequeueReusableAnnotationImageWithIdentifier(reuseId);
    if (image == null)
        image = MLNAnnotationImage.AnnotationImageWithImage(UIImage.FromBundle("pin")!, reuseId);
    return image;
}

public override MLNAnnotationView? ViewForAnnotation(MLNMapView mapView, IMLNAnnotation annotation)
{
    const string reuseId = "view";
    var view = mapView.DequeueReusableAnnotationViewWithIdentifier(reuseId)
               ?? new MLNAnnotationView(reuseId) { Frame = new CGRect(0, 0, 24, 24) };
    view.BackgroundColor = UIColor.SystemRed;
    return view;
}

public override bool AnnotationCanShowCallout(MLNMapView mapView, IMLNAnnotation annotation) => true;
public override void DidSelectAnnotation(MLNMapView mapView, IMLNAnnotation annotation) { }
```

Shape overlays (`MLNPolyline`, `MLNPolygon`, `MLNMultiPolyline`, `MLNPointCollection`) are built from coordinate arrays and added as overlays:

```csharp
var route = MLNPolyline.PolylineWithCoordinates(new CLLocationCoordinate2D[]
{
    new(51.5074, -0.1278), new(48.8566, 2.3522), new(52.5200, 13.4050),
});
route.Title = "route";
map.AddOverlays(new IMLNOverlay[] { route });

var bbox = route.OverlayBounds;                       // MLNCoordinateBounds
route.AppendCoordinates(new CLLocationCoordinate2D[] { new(41.9028, 12.4964) });
route.GetCoordinates(out CLLocationCoordinate2D first, new NSRange(0, 1));
```

Overlay styling comes from the delegate (`LineWidthForPolylineAnnotation`, `StrokeColorForShapeAnnotation`, `FillColorForPolygonAnnotation`, `AlphaForShapeAnnotation`).

## Runtime styling: sources, layers, expressions

This is MapLibre's real power: add a data *source*, add *style layers* that draw it, and drive every paint/layout property with an `NSExpression`. Do it from `DidFinishLoadingStyle` — `map.Style` is `null` before that.

```csharp
public override void DidFinishLoadingStyle(MLNMapView mapView, MLNStyle style)
{
    // 1. A GeoJSON-ish source built from features in memory.
    var berlin = new MLNPointFeature { Coordinate = new CLLocationCoordinate2D(52.52, 13.405) };
    berlin.Identifier = new NSString("berlin");
    berlin.Attributes = NSDictionary.FromObjectsAndKeys(
        new NSObject[] { new NSString("Berlin") },
        new NSObject[] { new NSString("name") });

    var source = new MLNShapeSource("poi", berlin, null);
    style.AddSource(source);

    // 2. A circle layer over it, styled with constant expressions.
    var circles = new MLNCircleStyleLayer("poi-circles", source)
    {
        CircleColor  = NSExpression.FromConstant(UIColor.Red),
        CircleRadius = NSExpression.FromConstant(NSNumber.FromDouble(8)),
    };
    style.AddLayer(circles);

    // 3. Labels, filtered with an NSPredicate.
    var labels = new MLNSymbolStyleLayer("poi-labels", source)
    {
        Text         = NSExpression.FromKeyPath("name"),
        TextFontSize = NSExpression.FromConstant(NSNumber.FromDouble(14)),
        TextColor    = NSExpression.FromConstant(UIColor.Black),
        Predicate    = NSPredicate.FromFormat("name != nil"),
    };
    style.AddLayer(labels);

    // 4. Ordering + lookup + removal.
    style.InsertLayerAboveLayer(labels, circles);
    var found = style.LayerWithIdentifier("poi-circles") as MLNCircleStyleLayer;
    found!.MinimumZoomLevel = 4;
    found.Visible = true;
    // style.RemoveLayer(found); style.RemoveSource(source, out NSError? err);
}
```

Layer types available: `MLNCircleStyleLayer`, `MLNFillStyleLayer`, `MLNFillExtrusionStyleLayer`, `MLNLineStyleLayer`, `MLNSymbolStyleLayer`, `MLNHeatmapStyleLayer`, `MLNHillshadeStyleLayer`, `MLNColorReliefStyleLayer`, `MLNRasterStyleLayer`, `MLNBackgroundStyleLayer`, plus `MLNCustomStyleLayer` / `MLNCustomDrawableStyleLayer` / `MLNPluginLayer` for Metal-level drawing.

Source types: `MLNShapeSource` (GeoJSON / in-memory shapes / a remote URL), `MLNVectorTileSource`, `MLNRasterTileSource`, `MLNRasterDEMSource`, `MLNImageSource`, `MLNComputedShapeSource`.

Zoom- and data-driven expressions use the `NSExpression` MapLibre additions, exposed as `NSExpression_MLNAdditions`:

```csharp
var zoom = NSExpression_MLNAdditions.ZoomLevelVariableExpression;
var stops = NSExpression.FromConstant(NSDictionary.FromObjectsAndKeys(
    new NSObject[] { NSNumber.FromDouble(2), NSNumber.FromDouble(20) },   // radii
    new NSObject[] { NSNumber.FromDouble(0), NSNumber.FromDouble(18) })); // zooms
circles.CircleRadius = NSExpression_MLNAdditions.Mgl_expressionForInterpolatingExpression(
    zoom, "linear", null, stops);
```

The other builders on the same class — `Mgl_expressionForSteppingExpression`, `Mgl_expressionForConditional`, `Mgl_expressionForMatchingExpression`, `Mgl_expressionForAttributedExpressions`, `ExpressionWithMLNJSONObject` — are static in the same way, as is `NSPredicate_MLNAdditions.PredicateWithMLNJSONObject`. (Each also still has the older instance-extension form that takes a throwaway `NSExpression`/`NSPredicate` receiver; prefer the static.)

`FeatureAttributesVariableExpression` (`$featureAttributes`) and `HeatmapDensityVariableExpression` are there too. Read [Predicates and expressions](https://maplibre.org/maplibre-native/ios/latest/documentation/maplibre-native-for-ios/predicates_and_expressions) for the full grammar — the C# side is a mechanical translation of the same `NSExpression`/`NSPredicate` objects.

Style images (sprites) for symbol layers:

```csharp
style.SetImage(myUIImage, "marker-icon");
symbolLayer.IconImageName = NSExpression.FromConstant(new NSString("marker-icon"));
// style.RemoveImageForName("marker-icon");
```

## Querying rendered features

```csharp
IMLNFeature[] hits = map.VisibleFeaturesAtPoint(tapPoint);

// The layer-identifier filter is an NSSet<NSString>, not a string[].
var layerIds = new NSSet<NSString>(new NSString("road-layer"));
IMLNFeature[] roads = map.VisibleFeaturesAtPoint(tapPoint, layerIds,
    NSPredicate.FromFormat("class == 'motorway'"));

foreach (var f in hits)
{
    var name = f.AttributeForKey("name") as NSString;
    var geo  = f.GeoJSONDictionary();
}
```

`VisibleFeaturesInRect(CGRect)` has the same three overloads. Parse/emit GeoJSON directly with `MLNShape.ShapeWithData(nsData, (ulong)NSStringEncoding.UTF8, out NSError? error)` and `shape.GeoJSONDataUsingEncoding(...)`.

## User location

```csharp
map.ShowsUserLocation = true;
map.SetUserTrackingMode(MLNUserTrackingMode.FollowWithHeading, animated: true);
var loc = map.UserLocation;            // MLNUserLocation (nullable until a fix arrives)
bool visible = map.UserLocationVisible;
```

Override `DidUpdateUserLocation`, `DidFailToLocateUserWithError`, `DidChangeUserTrackingModeAnimated`, and (iOS 14+) `DidChangeLocationManagerAuthorization` on your delegate. As with any iOS app, add `NSLocationWhenInUseUsageDescription` to `Info.plist` or Core Location refuses the request — see MapLibre's [User Location & Location Privacy](https://maplibre.org/maplibre-native/ios/latest/documentation/maplibre-native-for-ios/locationprivacyexample) article.

## Offline packs

```csharp
var storage = MLNOfflineStorage.SharedOfflineStorage;

var region = new MLNTilePyramidOfflineRegion(
    new NSUrl("https://demotiles.maplibre.org/style.json"),
    new MLNCoordinateBounds
    {
        Sw = new CLLocationCoordinate2D(51.49, -0.13),
        Ne = new CLLocationCoordinate2D(51.51, -0.11),
    },
    minimumZoomLevel: 12, maximumZoomLevel: 16);

var context = NSData.FromString("london", NSStringEncoding.UTF8);

storage.AddPackForRegion(region, context, (pack, error) =>
{
    if (error != null || pack == null) return;

    pack.Resume();                                  // creation does NOT start the download
    var p = pack.Progress;                          // MLNOfflinePackProgress struct
    Console.WriteLine($"{p.CountOfResourcesCompleted}/{p.CountOfResourcesExpected}");

    // storage.RemovePack(pack, err => { });
});

MLNOfflinePack[] existing = storage.Packs;
ulong bytes = storage.CountOfBytesCompleted;
storage.ReloadPacks();
```

`MLNShapeOfflineRegion` covers non-rectangular regions. Ambient cache management is on the same object: `SetMaximumAmbientCacheSize`, `InvalidateAmbientCacheWithCompletionHandler`, `ClearAmbientCacheWithCompletionHandler`, `ResetDatabaseWithCompletionHandler` — all taking an `Action<NSError>`.

**Progress reporting:** MapLibre publishes progress via `NSNotification`. The notification names and their `userInfo` keys are bound as real constants on `MapLibreConstants` (see [Caveats](#caveats--limitations)), so observe by symbol rather than by literal:

```csharp
NSNotificationCenter.DefaultCenter.AddObserver(
    MapLibreConstants.OfflinePackProgressChangedNotification,
    n => { var p = ((MLNOfflinePack)n.Object!).Progress; /* … */ },
    pack);

// Also: OfflinePackErrorNotification, OfflinePackMaximumMapboxTilesReachedNotification,
//       and the userInfo keys OfflinePackUserInfoKeyState / …KeyProgress /
//       …KeyError / …KeyMaximumCount.
```

Polling `pack.Progress` after `pack.RequestProgress()` also works and is what the binding test app does.

## Snapshots

`MLNMapSnapshotter` renders a still image off-screen, with no map view:

```csharp
var camera = MLNMapCamera.CameraLookingAtCenterCoordinateAltitudePitchHeading(
    new CLLocationCoordinate2D(37.7749, -122.4194), 15000, 0, 0);

var options = new MLNMapSnapshotOptions(styleUrl, camera, new CGSize(300, 200))
{
    ZoomLevel = 11,
    ShowsLogo = true,
};

var snapshotter = new MLNMapSnapshotter(options);
snapshotter.StartWithCompletionHandler((snapshot, error) =>
{
    if (error == null && snapshot != null)
        imageView.Image = snapshot.Image;           // UIImage
});
```

`StartWithOverlayHandler` gives you a `CGContext` (`MLNMapSnapshotOverlay`) to draw on top before the image is finalized; `Cancel()` aborts.

## Caveats & limitations

Verified against the binding test app (`libraries/MapLibre/tests/Program.cs`), which exercises 60+ API patterns on the simulator.

- **Names moved in SDK 0.19.0 — this is the one breaking change to plan for.** Three ObjC naming rules changed together and there are no compatibility shims, so an app compiled against an earlier `SwiftBindings.MapLibre` will not build until it is migrated. Every failure is a compile error (`CS0117` on an enum case, `CS0115` on a delegate override), and the new name is derivable:
  - **Enum cases** lost the `MLN` tag wherever it survived before: `MLNMapDebugMaskOptions.MLNMapDebugTileBoundariesMask` → `.MapDebugTileBoundariesMask` (and `…TileInfoMask`, `…TimestampsMask`, `…CollisionBoxesMask`, `…OverdrawVisualizationMask`); `MLNWellKnownTileServer.MLNMapTiler` / `.MLNMapLibre` / `.MLNMapbox` → `.MapTiler` / `.MapLibre` / `.Mapbox`. Enums already stripped under the old rule (`MLNUserTrackingMode`, `MLNOrnamentPosition`, `MLNLineCap`, …) are unchanged.
  - **Delegate overrides** now drop only the receiver segment instead of the whole first selector keyword. The renames that hit real code are `MapViewDidFinishLoadingMap` → `DidFinishLoadingMap`, `WithError` → `DidFailLoadingMapWithError`, `FullyRendered` → `DidFinishRenderingMapFullyRendered`, `MapViewDidBecomeIdle` → `DidBecomeIdle`; the same peel applies across `MLNMapViewDelegate` (16 methods), `MLNCalloutViewDelegate` (4), `MLNLocationManagerDelegate` (2) and `MLNMapSnapshotterDelegate` (1) — 23 in total. `DidFinishLoadingStyle`, `RegionDidChangeAnimated`, `ViewForAnnotation`, `ImageForAnnotation`, `DidSelectAnnotation`, `SourceDidChange`, `DidUpdateUserLocation` and the rest of the `mapView:…`-prefixed hooks are unchanged.
  - **Constants** lost the `MLN` tag as well — see the next bullet.

  One residual: the receiver-token peel strips a whole leading uppercase run rather than just the framework prefix, so a delegate whose protocol name starts with a multi-word acronym keeps its pre-0.19.0 name. No MapLibre protocol is affected; the un-peeled name is correct, just less Apple-like.
- **`extern NSString * const` constants are bound** as of SDK 0.19.0 — all 58 of them, as a static `MapLibreConstants` class, with the `MLN` tag stripped from each name. `MLNOfflinePackProgressChangedNotification` is `MapLibreConstants.OfflinePackProgressChangedNotification`, `MLNShapeSourceOptionClustered` is `MapLibreConstants.ShapeSourceOptionClustered`, and so on for the tile-source option keys, the exception names, `ErrorDomain`, the font attributes and the deceleration-rate / FPS numeric constants. Use them instead of literals:
  ```csharp
  var options = NSDictionary.FromObjectsAndKeys(
      new NSObject[] { NSNumber.FromBoolean(true), NSNumber.FromInt32(50) },
      new NSObject[] { MapLibreConstants.ShapeSourceOptionClustered,
                       MapLibreConstants.ShapeSourceOptionClusterRadius });
  MLNShape[] shapes = { pointFeature };
  var clustered = new MLNShapeSource("poi", shapes, options);
  ```
  These read their real value now; before 0.19.0 the symbols did not exist at all and literal strings were the only option.
- **`NSValue` category class methods have receiver-free static overloads.** `+[NSValue valueWithMLNCoordinate:]` is `NSValue_MLNAdditions.ValueWithMLNCoordinate(coord)`; the same holds for `…CoordinateSpan`, `…CoordinateBounds`, `…CoordinateQuad`, `…Transition` and the per-layer `NSValue_MLN*StyleLayerAdditions` boxers. The older instance form — a throwaway receiver, `NSValue.FromCGPoint(CGPoint.Empty).ValueWithMLNCoordinate(coord)` — still compiles and still works (it is what the binding test app uses), because bgen gives every generated category member a receiver that a class method never reads. Unboxing is unchanged and genuinely instance-shaped: `GetMLNCoordinateValue()` / `GetMLNCoordinateBoundsValue()` / `GetMLNCoordinateSpanValue()` / `GetMLNTransitionValue()`.
- **`map.Style` is null until `DidFinishLoadingStyle`**, and is replaced on every style reload. Sources and layers added to an old `MLNStyle` do not survive a style change — re-add them in the callback.
- **Selection and `VisibleAnnotations` need realized annotation views.** In a headless/off-screen map, `SelectAnnotation` may not take effect synchronously and `VisibleAnnotations` / `VisibleAnnotationsInRect` can return `null`. That is upstream UI behaviour, not a binding defect; on a real on-screen map they populate normally.
- **A new animated camera change cancels the previous one**, dropping its completion block.
- **`MLNMapView` requires a rendering surface.** It uses Metal on device and simulator; creating and disposing map views repeatedly is safe (covered by the test app), but do not drive the map from a background thread.
- **No `SB0001` stubs and no Swift-interop diagnostics at all** in this package — the binding carries zero `SB…` markers, so everything that reaches the generated API is callable as written. There is no "compiles but traps" surface here.
- **Twenty-one ObjC declarations are dropped before they reach the API**, all for structural reasons and none of them current API:
  - **Eight types with no native symbol in the shipped xcframework** — `MLNStyleValue`, `MLNConstantStyleValue`, `MLNStyleFunction`, `MLNCameraStyleFunction`, `MLNSourceStyleFunction`, `MLNCompositeStyleFunction` (the pre-`NSExpression` style-function API, deprecated upstream and superseded by the expressions covered in [Runtime styling](#runtime-styling-sources-layers-expressions)), plus `MLNBackendResource` and `MLNPluginStyleLayer`. The headers declare them; the binary does not export them, so binding them would only produce link failures.
  - **`MLNCoordinateSpanZero`** — the one extern constant that does not join the 58 above, because a struct-valued `[Field]` has no `Dlfcn` reader. Write `default(MLNCoordinateSpan)`, or `new MLNCoordinateSpan { LatitudeDelta = 0, LongitudeDelta = 0 }` — the struct has public fields and no constructor.
  - **Two long-form coordinate overloads** — `setVisibleCoordinates:count:edgePadding:direction:duration:animationTimingFunction:completionHandler:` and `replaceCoordinatesInRange:withCoordinates:` — whose `const CLLocationCoordinate2D *` parameter has no managed projection. The shorter forms bind fine and are what the array overloads above wrap: `SetVisibleCoordinates(coords, insets, animated)` and `ReplaceCoordinatesInRange(range, coords)`.
  - **`MapboxVersionNumber` / `MapboxVersionString` / `MLNEffectiveScaleFactorForView`** — unexported in the release build.
  - **Four duplicate selectors** resolved in favour of the inherited member or the property accessor (`MLNShapeCollectionFeature.Shapes`, `MLNImageSource.setURL:` / `setCoordinates:`, `MLNLoggingConfiguration.handler`) — the surviving member does the same job.
- **iOS only.** No macOS, Mac Catalyst, or tvOS target framework.

## MapLibre documentation

The bindings are a faithful projection of the ObjC API, so MapLibre's own documentation is the reference for semantics — translate names with the [rules above](#objective-c--c-translation-rules).

- **[MapLibre Native for iOS — API reference](https://maplibre.org/maplibre-native/ios/latest/documentation/maplibre/)** — the canonical DocC site; every `MLN*` class (e.g. [`MLNMapView`](https://maplibre.org/maplibre-native/ios/latest/documentation/maplibre/mlnmapview), [`MLNStyle`](https://maplibre.org/maplibre-native/ios/latest/documentation/maplibre/mlnstyle), [`MLNMapViewDelegate`](https://maplibre.org/maplibre-native/ios/latest/documentation/maplibre/mlnmapviewdelegate))
- **[Getting Started (iOS)](https://maplibre.org/maplibre-native/ios/latest/documentation/maplibre-native-for-ios/gettingstarted)** and the example articles: [Add Marker](https://maplibre.org/maplibre-native/ios/latest/documentation/maplibre-native-for-ios/addmarkersymbolexample), [Working with GeoJSON Data](https://maplibre.org/maplibre-native/ios/latest/documentation/maplibre-native-for-ios/geojson), [Custom Annotation View](https://maplibre.org/maplibre-native/ios/latest/documentation/maplibre-native-for-ios/annotationviewexample), [Download Offline Pack](https://maplibre.org/maplibre-native/ios/latest/documentation/maplibre-native-for-ios/offlinepackexample), [Making Snapshots](https://maplibre.org/maplibre-native/ios/latest/documentation/maplibre-native-for-ios/staticsnapshotexample)
- **[Predicates and expressions](https://maplibre.org/maplibre-native/ios/latest/documentation/maplibre-native-for-ios/predicates_and_expressions)** — required reading before writing runtime styling code
- **[Info.plist keys](https://maplibre.org/maplibre-native/ios/latest/documentation/maplibre-native-for-ios/info.plist_keys)** — location permissions and MapLibre-specific keys
- **[MapLibre Style Spec](https://maplibre.org/maplibre-style-spec/)** — what a style JSON contains; the layer/source properties map 1:1 onto the `MLN*StyleLayer` properties
- **[MapLibre Native developer docs](https://maplibre.org/maplibre-native/docs/book/)** and the upstream repo **[maplibre/maplibre-native](https://github.com/maplibre/maplibre-native)** (this package tracks the `ios-v6.28.0` release asset)
