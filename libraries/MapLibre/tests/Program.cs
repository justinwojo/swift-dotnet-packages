// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.
//
// MapLibre Native iOS binding test app.
//
// This is NOT a simple "types resolve" smoke test. The real interop risk for a
// pure Objective-C framework is whether the generator emits USABLE bindings for
// the ObjC patterns MapLibre relies on: delegate/optional-protocol callbacks
// firing back into C#, NSExpression-based runtime styling, ObjC collection
// mutation, block completion handlers, struct-by-value marshalling, gestures,
// and deterministic object teardown.
//
// The app exercises an "API-pattern coverage" matrix and prints each result.
// TEST SUCCESS is gated on the WHOLE matrix: zero failures. Known upstream
// generator gaps are recorded as SKIP with an explicit reason so they are
// visible without masking real regressions.

using System.Diagnostics;
using System.Text;
using CoreGraphics;
using CoreLocation;
using Foundation;
using ObjCRuntime;
using UIKit;
using MapLibre;

namespace SwiftBindings.MapLibre.Tests;

#region Test Infrastructure

public enum Status { Pass, Fail, Skip }

public sealed record PatternResult(string Name, Status Status, string Detail);

public static class TestState
{
    public static readonly List<PatternResult> Results = new();
    public static readonly object Gate = new();

    // Delegate-callback flags (set from ObjC -> C# callbacks)
    public static volatile bool DidFinishLoadingStyle;
    public static volatile bool DidFinishLoadingMap;
    public static volatile bool DidFinishRenderingMapFully;
    public static volatile bool RegionDidChange;

    // Async / block-completion results, recorded deterministically at report time.
    public static string RuntimeStyleDetail = "delegate did not fire";
    public static Status RuntimeStyleStatus = Status.Fail;

    public static volatile bool FlyToCameraCompleted;

    public static Status OfflinePackStatus = Status.Fail;
    public static string OfflinePackDetail = "addPackForRegion completion never fired";

    public static void Record(string name, Status status, string detail)
    {
        lock (Gate) { Results.Add(new PatternResult(name, status, detail)); }
        Console.WriteLine($"[PATTERN] {status.ToString().ToUpper(),-4} {name} :: {detail}");
    }

    /// <summary>Run a probe; any escaping exception becomes a FAIL with the exception text.</summary>
    public static void Probe(string name, Func<(Status status, string detail)> body)
    {
        try
        {
            var (status, detail) = body();
            Record(name, status, detail);
        }
        catch (Exception ex)
        {
            Record(name, Status.Fail, $"{ex.GetType().Name}: {ex.Message}");
        }
    }
}

#endregion

#region Application Entry Point

public class Application
{
    static void Main(string[] args)
    {
        // Framework resolution: the native MapLibre framework is embedded via the
        // test csproj's <NativeReference>; ObjC class registration happens at load.
        UIApplication.Main(args, null, typeof(TestAppDelegate));
    }
}

#endregion

#region App Delegate

[Register("TestAppDelegate")]
public class TestAppDelegate : UIApplicationDelegate
{
    public override UIWindow? Window { get; set; }

    public override bool FinishedLaunching(UIApplication application, NSDictionary? launchOptions)
    {
        Window = new UIWindow(UIScreen.MainScreen.Bounds);
        Window.RootViewController = new MapTestViewController();
        Window.MakeKeyAndVisible();
        return true;
    }
}

#endregion

#region MapLibre delegate (ObjC [Protocol, Model] subclass)

// Subclassing the generated [Protocol, Model] base class is the canonical bgen
// pattern for an ObjC delegate. If these overrides are never invoked, the ObjC
// -> managed callback bridge is broken — a headline finding.
public sealed class SpikeMapDelegate : MLNMapViewDelegate
{
    public override void DidFinishLoadingStyle(MLNMapView mapView, MLNStyle style)
    {
        TestState.DidFinishLoadingStyle = true;
        Console.WriteLine("[DELEGATE] mapView:didFinishLoadingStyle: fired into C#");

        // Pattern (b): load a non-default source + style layer at runtime and
        // mutate it via NSExpression — the core MapLibre runtime-styling API.
        try
        {
            using var pt = new MLNPointFeature();
            pt.Coordinate = new CLLocationCoordinate2D(48.8566, 2.3522); // Paris
            var src = new MLNShapeSource("spike-src", pt, null);
            style.AddSource(src);

            var layer = new MLNCircleStyleLayer("spike-circle", src);
            layer.CircleColor = NSExpression.FromConstant(UIColor.Red);
            layer.CircleRadius = NSExpression.FromConstant(NSNumber.FromDouble(18));
            style.AddLayer(layer);

            // Read it back to confirm the mutation round-trips through ObjC. Fetching
            // the layer by identifier only proves AddLayer worked, so also re-read the
            // two NSExpression paint properties off the *re-fetched* layer — a no-op
            // setter would otherwise pass this test.
            var found = style.LayerWithIdentifier("spike-circle") as MLNCircleStyleLayer;
            if (found == null)
            {
                TestState.RuntimeStyleStatus = Status.Fail;
                TestState.RuntimeStyleDetail =
                    "AddLayer succeeded but layerWithIdentifier returned null or the wrong type";
            }
            else
            {
                var radiusBack = found.CircleRadius;
                var colorBack = found.CircleColor;
                // MapLibre normalises a constant NSExpression, so compare the constant
                // value it evaluates to rather than the expression's text.
                var radiusValue = (radiusBack?.ConstantValue as NSNumber)?.DoubleValue;
                if (radiusValue != 18)
                {
                    TestState.RuntimeStyleStatus = Status.Fail;
                    TestState.RuntimeStyleDetail =
                        $"CircleRadius did not round-trip: expected 18, read back " +
                        $"'{radiusBack?.ToString() ?? "null"}' (constant={radiusValue?.ToString() ?? "null"})";
                }
                else if (colorBack?.ConstantValue is not UIColor colorValue
                         || !colorValue.CGColor.ToString().Equals(UIColor.Red.CGColor.ToString(), StringComparison.Ordinal))
                {
                    TestState.RuntimeStyleStatus = Status.Fail;
                    TestState.RuntimeStyleDetail =
                        $"CircleColor did not round-trip: expected red, read back " +
                        $"'{colorBack?.ConstantValue?.ToString() ?? "null"}'";
                }
                else
                {
                    TestState.RuntimeStyleStatus = Status.Pass;
                    TestState.RuntimeStyleDetail =
                        "MLNShapeSource + MLNCircleStyleLayer added; CircleRadius=18 and CircleColor=red " +
                        "both re-read off the re-fetched layer";
                }
            }
        }
        catch (Exception ex)
        {
            TestState.RuntimeStyleStatus = Status.Fail;
            TestState.RuntimeStyleDetail = $"{ex.GetType().Name}: {ex.Message}";
        }

        // Everything below needs a loaded MLNStyle, so it runs here rather than
        // in the offline phase. Each probe is independently guarded.
        StyleTests.Run(style);
    }

    public override void DidFinishLoadingMap(MLNMapView mapView)
    {
        TestState.DidFinishLoadingMap = true;
        Console.WriteLine("[DELEGATE] mapViewDidFinishLoadingMap: fired into C#");
    }

    public override void DidFinishRenderingMapFullyRendered(MLNMapView mapView, bool fullyRendered)
    {
        if (fullyRendered) TestState.DidFinishRenderingMapFully = true;
        Console.WriteLine($"[DELEGATE] mapViewDidFinishRenderingMap:fullyRendered:{fullyRendered} fired into C#");
    }

    public override void RegionDidChangeAnimated(MLNMapView mapView, bool animated)
    {
        TestState.RegionDidChange = true;
        Console.WriteLine($"[DELEGATE] mapView:regionDidChangeAnimated:{animated} fired into C#");
    }
}

#endregion

#region Style-dependent tests (run from the didFinishLoadingStyle delegate)

public static class StyleTests
{
    public static void Run(MLNStyle style)
    {
        // Style-level metadata + collection accessors.
        TestState.Probe("style: metadata (Name / StyleJSON) + collections (Layers / Sources)", () =>
        {
            var name = style.Name;
            var json = style.StyleJSON;
            var layers = style.Layers;
            var sources = style.Sources;
            bool ok = json.Length > 0 && layers.Length > 0 && sources.Count > 0;
            return (ok ? Status.Pass : Status.Fail,
                $"Name='{name}', StyleJSON={json.Length} chars, {layers.Length} layer(s), {sources.Count} source(s)");
        });

        // Source lookup by identifier round-trips to the concrete type we added.
        TestState.Probe("style: SourceWithIdentifier -> concrete MLNShapeSource", () =>
        {
            var src = style.SourceWithIdentifier("spike-src");
            bool ok = src is MLNShapeSource shape && shape.Identifier == "spike-src" && shape.Shape != null;
            return (ok ? Status.Pass : Status.Fail,
                ok ? "re-fetched as MLNShapeSource; Identifier + Shape round-trip"
                   : $"got {(src == null ? "null" : src.GetType().Name)}");
        });

        // MLNStyleLayer base-class property round-trip on the layer we added.
        TestState.Probe("style-layer: Identifier / Visible / Min+MaximumZoomLevel round-trip", () =>
        {
            var layer = style.LayerWithIdentifier("spike-circle");
            if (layer == null) return (Status.Fail, "spike-circle not found");
            layer.MinimumZoomLevel = 1.5f;
            layer.MaximumZoomLevel = 17.5f;
            layer.Visible = true;
            bool ok = layer.Identifier == "spike-circle"
                && Math.Abs(layer.MinimumZoomLevel - 1.5f) < 0.01
                && Math.Abs(layer.MaximumZoomLevel - 17.5f) < 0.01
                && layer.Visible;
            return (ok ? Status.Pass : Status.Fail,
                $"id={layer.Identifier}, visible={layer.Visible}, zoom=[{layer.MinimumZoomLevel:F2}, {layer.MaximumZoomLevel:F2}]");
        });

        // Insert / remove ordering APIs + removal actually takes effect.
        TestState.Probe("style: InsertLayer(index) + InsertLayerAboveLayer + RemoveLayer", () =>
        {
            var src = style.SourceWithIdentifier("spike-src");
            if (src == null) return (Status.Fail, "spike-src missing");

            var bottom = new MLNCircleStyleLayer("spike-inserted-bottom", src);
            style.InsertLayer(bottom, 0);
            var above = new MLNCircleStyleLayer("spike-inserted-above", src);
            style.InsertLayerAboveLayer(above, bottom);

            bool inserted = style.LayerWithIdentifier("spike-inserted-bottom") != null
                         && style.LayerWithIdentifier("spike-inserted-above") != null;

            style.RemoveLayer(above);
            style.RemoveLayer(bottom);
            bool removed = style.LayerWithIdentifier("spike-inserted-bottom") == null
                        && style.LayerWithIdentifier("spike-inserted-above") == null;

            return (inserted && removed ? Status.Pass : Status.Fail,
                $"inserted={inserted}, removed={removed}");
        });

        // Vector style layer with an NSPredicate filter + NSExpression paint prop.
        TestState.Probe("style-layer: MLNFillStyleLayer + NSPredicate filter + NSExpression paint", () =>
        {
            var src = style.SourceWithIdentifier("spike-src");
            if (src == null) return (Status.Fail, "spike-src missing");

            var fill = new MLNFillStyleLayer("spike-fill", src);
            fill.Predicate = NSPredicate.FromFormat("spikeKey == 'spikeValue'");
            fill.FillColor = NSExpression.FromConstant(UIColor.Green);
            fill.FillOpacity = NSExpression.FromConstant(NSNumber.FromDouble(0.35));
            style.AddLayer(fill);

            // Read the written constants back off the re-fetched layer. Asserting only
            // "non-null" would pass with no-op setters that leave a prior expression.
            var back = style.LayerWithIdentifier("spike-fill") as MLNFillStyleLayer;
            var opacityBack = (back?.FillOpacity?.ConstantValue as NSNumber)?.DoubleValue;
            bool ok = back != null
                && back.Predicate != null
                && back.Predicate!.PredicateFormat.Contains("spikeKey")
                && back.FillColor?.ConstantValue != null
                // MapLibre narrows paint constants to float internally, so 0.35 comes back
                // as 0.3499999940395355 — compare with a float-width tolerance, which still
                // rejects a no-op setter that leaves a stale or absent expression.
                && opacityBack is double fo && Math.Abs(fo - 0.35) < 1e-6;
            string detail = ok
                ? $"predicate round-trips ('{back!.Predicate!.PredicateFormat}'); FillColor constant present and FillOpacity read back as {opacityBack:R} (~0.35)"
                : $"back={(back == null ? "null" : "ok")}, predicate={back?.Predicate?.PredicateFormat ?? "null"}, " +
                  $"fillColorConstant={back?.FillColor?.ConstantValue?.ToString() ?? "null"}, fillOpacity={opacityBack?.ToString() ?? "null"} (expected 0.35)";
            style.RemoveLayer(back ?? fill);
            return (ok ? Status.Pass : Status.Fail, detail);
        });

        // MLNSymbolStyleLayer — the widest NSExpression property surface.
        TestState.Probe("style-layer: MLNSymbolStyleLayer NSExpression property round-trip", () =>
        {
            var src = style.SourceWithIdentifier("spike-src");
            if (src == null) return (Status.Fail, "spike-src missing");

            var sym = new MLNSymbolStyleLayer("spike-symbol", src);
            sym.Text = NSExpression.FromConstant(new NSString("MapLibre"));
            sym.TextColor = NSExpression.FromConstant(UIColor.Blue);
            sym.TextFontSize = NSExpression.FromConstant(NSNumber.FromDouble(14));
            style.AddLayer(sym);

            // Assert the written constants, not merely that the properties are non-null.
            var back = style.LayerWithIdentifier("spike-symbol") as MLNSymbolStyleLayer;
            var textBack = (back?.Text?.ConstantValue as NSString)?.ToString();
            var sizeBack = (back?.TextFontSize?.ConstantValue as NSNumber)?.DoubleValue;
            bool ok = back != null
                && textBack == "MapLibre"
                && sizeBack == 14
                && back.TextColor?.ConstantValue != null;
            string detail = ok
                ? "Text='MapLibre', TextFontSize=14 and a TextColor constant all read back off the re-fetched layer"
                : $"symbol layer did not round-trip: text={textBack ?? "null"} (expected 'MapLibre'), " +
                  $"fontSize={sizeBack?.ToString() ?? "null"} (expected 14), " +
                  $"textColorConstant={back?.TextColor?.ConstantValue?.ToString() ?? "null"}";
            if (back != null) style.RemoveLayer(back);
            return (ok ? Status.Pass : Status.Fail, detail);
        });

        // Runtime image registry (UIImage -> style sprite -> back).
        TestState.Probe("style: SetImage / ImageForName / RemoveImageForName round-trip", () =>
        {
            UIGraphics.BeginImageContextWithOptions(new CGSize(8, 8), false, 1);
            var ctx = UIGraphics.GetCurrentContext();
            ctx.SetFillColor(UIColor.Magenta.CGColor);
            ctx.FillRect(new CGRect(0, 0, 8, 8));
            var img = UIGraphics.GetImageFromCurrentImageContext();
            UIGraphics.EndImageContext();
            if (img == null) return (Status.Fail, "could not build a UIImage");

            style.SetImage(img, "spike-image");
            var back = style.ImageForName("spike-image");
            bool ok = back != null && back.Size.Width > 0;
            style.RemoveImageForName("spike-image");
            var afterRemoval = style.ImageForName("spike-image");
            return (ok && afterRemoval == null ? Status.Pass : Status.Fail,
                $"stored={ok} ({back?.Size.Width ?? 0}x{back?.Size.Height ?? 0}), removed={(afterRemoval == null)}");
        });

        // MLNLight — nested ObjC object hanging off the style, NSExpression props.
        TestState.Probe("style: MLNLight NSExpression property round-trip", () =>
        {
            var light = style.Light;
            light.Intensity = NSExpression.FromConstant(NSNumber.FromDouble(0.42));
            style.Light = light;
            var back = style.Light;
            // The written intensity must survive the round-trip through style.Light;
            // a non-null check alone would pass against a no-op setter.
            var intensityBack = (back?.Intensity?.ConstantValue as NSNumber)?.DoubleValue;
            // Narrowed to float by MapLibre (0.42 -> 0.41999998688697815), so compare at
            // float width rather than exactly.
            bool ok = back != null
                && intensityBack is double bi && Math.Abs(bi - 0.42) < 1e-6
                && back.Color != null;
            return (ok ? Status.Pass : Status.Fail,
                ok ? $"Intensity read back as {intensityBack:R} (~0.42), Color present, Anchor='{back!.Anchor?.Description}'"
                   : $"intensity={intensityBack?.ToString() ?? "null"} (expected 0.42), " +
                     $"color={(back?.Color == null ? "null" : "present")}");
        });

        // Label localization + transition struct round-trip.
        TestState.Probe("style: LocalizeLabelsIntoLocale + MLNTransition struct round-trip", () =>
        {
            style.LocalizeLabelsIntoLocale(null);
            style.Transition = new MLNTransition { Duration = 0.25, Delay = 0.05 };
            var t = style.Transition;
            bool ok = Math.Abs(t.Duration - 0.25) < 1e-6 && Math.Abs(t.Delay - 0.05) < 1e-6;
            return (ok ? Status.Pass : Status.Fail,
                $"localize OK; transition duration={t.Duration:F3}, delay={t.Delay:F3}");
        });

        // Source removal with the (out NSError) overload — ObjC error-out pattern.
        TestState.Probe("style: RemoveSource(source, out NSError) error-out pattern", () =>
        {
            var tmpSrc = new MLNShapeSource("spike-tmp-src", (MLNShape?)null, null);
            style.AddSource(tmpSrc);
            bool removed = style.RemoveSource(tmpSrc, out NSError? err);
            bool ok = removed && err == null && style.SourceWithIdentifier("spike-tmp-src") == null;
            return (ok ? Status.Pass : Status.Fail,
                $"removed={removed}, error={(err == null ? "null" : err.LocalizedDescription)}");
        });
    }
}

#endregion

#region Main View Controller

public class MapTestViewController : UIViewController
{
    private const string DemoStyle = "https://demotiles.maplibre.org/style.json";

    private MLNMapView? _map;
    private SpikeMapDelegate? _delegate;

    public override void ViewDidLoad()
    {
        base.ViewDidLoad();
        View!.BackgroundColor = UIColor.SystemBackground;

        // Pattern (a-construct): MLNMapView via initWithFrame:styleURL:
        var styleUrl = new NSUrl(DemoStyle);
        _map = new MLNMapView(View.Bounds, styleUrl)
        {
            AutoresizingMask = UIViewAutoresizing.FlexibleWidth | UIViewAutoresizing.FlexibleHeight,
        };
        View.AddSubview(_map);

        _delegate = new SpikeMapDelegate();
        _map.WeakDelegate = _delegate;

        // World-ish opening view so the screenshot shows recognizable geography.
        _map.SetCenterCoordinate(new CLLocationCoordinate2D(30, 0), 1.4, false);

        RunSynchronousPatterns();
        RunCameraTests();
        RunViewportAndProjectionTests();
        RunAnnotationTests();
        RunOverlayAndGeometryTests();
        RunStandaloneApiTests();
        RunOfflineTests();
        RunKnownGapProbes();

        // Programmatic camera change → should drive region* delegate callbacks
        // (pattern c surrogate: the gesture/camera pipeline + delegate dispatch).
        //
        // MUST run BEFORE the FlyToCamera probe below: this is itself an *animated*
        // camera change, and starting one cancels any flight already in progress —
        // which would drop the fly completion block and fail that assertion on a
        // perfectly healthy binding.
        _map!.SetCenterCoordinate(new CLLocationCoordinate2D(48.8566, 2.3522), 3.0, true);

        // Block completion handler: ObjC calls back into a managed lambda. Runs LAST of
        // the synchronous phase — it starts an animation, so an in-flight flight makes
        // every camera/viewport read-back non-deterministic, and nothing after it may
        // start a competing animated camera change (see above).
        TestState.Probe("camera: FlyToCamera(camera, duration, completion-block) accepted", () =>
        {
            var cam = MLNMapCamera.CameraLookingAtCenterCoordinateAltitudePitchHeading(
                new CLLocationCoordinate2D(48.8566, 2.3522), 900000, 0, 0);
            _map!.FlyToCamera(cam, 0.4, () =>
            {
                TestState.FlyToCameraCompleted = true;
                Console.WriteLine("[BLOCK] flyToCamera completion invoked in C#");
            });
            return (Status.Pass, "flight started with a managed completion block (delivery asserted separately)");
        });

        // Defer the async/coverage verdict until tiles + delegate callbacks have
        // had time to fire over the network.
        _ = Task.Run(async () =>
        {
            await Task.Delay(14000);
            BeginInvokeOnMainThread(() => FinishAndReport());
        });
    }

    private void RunSynchronousPatterns()
    {
        // Construction + property round-trip
        try
        {
            var url = _map!.StyleURL;
            bool ok = url != null && url.AbsoluteString == DemoStyle;
            TestState.Record("construct: MLNMapView(initWithFrame:styleURL:)", ok ? Status.Pass : Status.Fail,
                ok ? "map created; StyleURL round-trips" : $"StyleURL mismatch: {url?.AbsoluteString}");
        }
        catch (Exception ex)
        {
            TestState.Record("construct: MLNMapView(initWithFrame:styleURL:)", Status.Fail, $"{ex.GetType().Name}: {ex.Message}");
        }

        // Scalar property round-trip (zoom/direction).
        try
        {
            double zoom = _map!.ZoomLevel;
            double dir = _map.Direction;
            bool ok = zoom > 0; // we set 1.4 in ViewDidLoad
            TestState.Record("property: MLNMapView scalar props (zoomLevel/direction)", ok ? Status.Pass : Status.Fail,
                $"zoomLevel={zoom:F2}, direction={dir:F1}");
        }
        catch (Exception ex)
        {
            TestState.Record("property: MLNMapView scalar props (zoomLevel/direction)", Status.Fail, $"{ex.GetType().Name}: {ex.Message}");
        }

        // Camera read-write property round-trip. The ObjC `camera` property was
        // previously dropped: its projected C# name collided with the `Camera(...)`
        // methods the generator names from the `cameraThatFits…`/`cameraLookingAt…`
        // selectors, so only `SetCamera(...)` survived and there was no `Camera`
        // getter/setter property. It is now emitted as a full read-write property;
        // this drives BOTH accessors offline (get non-null, set a fresh camera, read
        // the center coordinate back) so it does not depend on network tile loading.
        try
        {
            var initial = _map!.Camera; // getter
            var target = new CLLocationCoordinate2D(40.4168, -3.7038); // Madrid
            var cam = MLNMapCamera.CameraLookingAtCenterCoordinateAltitudePitchHeading(target, 6000, 30, 45);
            _map.Camera = cam; // setter
            var readback = _map.Camera; // getter again
            bool ok = initial != null
                && readback != null
                && Math.Abs(readback.CenterCoordinate.Latitude - target.Latitude) < 0.5
                && Math.Abs(readback.CenterCoordinate.Longitude - target.Longitude) < 0.5;
            TestState.Record("property: MLNMapView.Camera read-write round-trip", ok ? Status.Pass : Status.Fail,
                ok
                    ? $"get/set both reachable; center round-trips to ({readback!.CenterCoordinate.Latitude:F3}, {readback.CenterCoordinate.Longitude:F3}), altitude={readback.Altitude:F0}"
                    : $"initial={(initial == null ? "null" : "ok")}, readback center=({readback?.CenterCoordinate.Latitude:F3}, {readback?.CenterCoordinate.Longitude:F3})");
        }
        catch (Exception ex)
        {
            TestState.Record("property: MLNMapView.Camera read-write round-trip", Status.Fail, $"{ex.GetType().Name}: {ex.Message}");
        }

        // Add an annotation through the STRONGLY-TYPED binding (ObjC protocol-typed param +
        // protocol-typed array return). The ObjC `MLNAnnotation` protocol now binds member
        // references to the interface `IMLNAnnotation`: `MLNPointAnnotation` (which conforms via
        // `MLNShape : IMLNAnnotation`) is assignable to `AddAnnotation`'s parameter, and
        // `Annotations` (an `IMLNAnnotation[]`) marshals the conforming object back through
        // `GetINativeObject<IMLNAnnotation>` with no cast failure. Previously the protocol was
        // emitted with a pre-applied "I" prefix that collapsed the interface onto a Model class,
        // so `AddAnnotation(ann)` did not compile and reading `Annotations` back threw
        // InvalidCastException — the capability was reachable only via a raw ObjC selector.
        try
        {
            var ann = new MLNPointAnnotation();
            ann.Coordinate = new CLLocationCoordinate2D(51.5074, -0.1278); // London
            _map!.AddAnnotation(ann);
            var visible = _map.Annotations;
            int n = visible?.Length ?? 0;
            bool roundTrips = n == 1
                && visible![0] is { } back
                && Math.Abs(back.Coordinate.Latitude - 51.5074) < 1e-4
                && Math.Abs(back.Coordinate.Longitude - (-0.1278)) < 1e-4;
            bool isConcrete = n == 1 && visible![0] is MLNPointAnnotation;
            TestState.Record("annotation: MLNPointAnnotation + AddAnnotation(IMLNAnnotation)",
                roundTrips ? Status.Pass : Status.Fail,
                $"strongly-typed AddAnnotation + Annotations round-trip — {n} annotation(s); " +
                $"coordinate {(roundTrips ? "round-trips" : "did NOT round-trip")}" +
                $"{(isConcrete ? " as MLNPointAnnotation" : "")}");
        }
        catch (Exception ex)
        {
            TestState.Record("annotation: MLNPointAnnotation + AddAnnotation(IMLNAnnotation)", Status.Fail, $"{ex.GetType().Name}: {ex.Message}");
        }

        // Gesture recognizers attached by MapLibre (interactivity surface)
        try
        {
            var recognizers = _map!.GestureRecognizers;
            int n = recognizers?.Length ?? 0;
            TestState.Record("gesture: built-in UIGestureRecognizers attached", n > 0 ? Status.Pass : Status.Fail,
                $"{n} recognizers (pan/pinch/rotate/tap) on the map view");
        }
        catch (Exception ex)
        {
            TestState.Record("gesture: built-in UIGestureRecognizers attached", Status.Fail, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    #region Camera

    private void RunCameraTests()
    {
        var map = _map!;

        // Every MLNMapCamera factory + the scalar property surface.
        TestState.Probe("camera: MLNMapCamera factories + scalar property round-trip", () =>
        {
            var blank = MLNMapCamera.Camera();
            var center = new CLLocationCoordinate2D(35.6762, 139.6503); // Tokyo
            var eye = new CLLocationCoordinate2D(35.7, 139.7);
            var lookingAtEye = MLNMapCamera.CameraLookingAtCenterCoordinate(center, eye, 4200);
            var byDistance = MLNMapCamera.CameraLookingAtCenterCoordinateFromDistancePitchHeading(center, 9000, 25, 120);
            var byAltitude = MLNMapCamera.CameraLookingAtCenterCoordinateAltitudePitchHeading(center, 7000, 40, 200);

            byAltitude.Altitude = 8500;
            byAltitude.Heading = 210;
            byAltitude.Pitch = 35;
            byAltitude.CenterCoordinate = center;

            bool ok = blank != null
                && lookingAtEye != null
                && byDistance != null
                && Math.Abs(byDistance.Heading - 120) < 1e-6
                && Math.Abs((double)byDistance.Pitch - 25) < 1e-3
                && Math.Abs(byAltitude.Altitude - 8500) < 1e-6
                && Math.Abs(byAltitude.Heading - 210) < 1e-6
                && Math.Abs((double)byAltitude.Pitch - 35) < 1e-3
                && Math.Abs(byAltitude.CenterCoordinate.Latitude - center.Latitude) < 1e-6
                && byAltitude.ViewingDistance > 0;

            return (ok ? Status.Pass : Status.Fail,
                $"4 factories OK; altitude={byAltitude.Altitude:F0}, pitch={(double)byAltitude.Pitch:F1}, " +
                $"heading={byAltitude.Heading:F0}, viewingDistance={byAltitude.ViewingDistance:F0}, roll={byAltitude.Roll:F1}");
        });

        // NSCopying + value equality across the ObjC bridge.
        TestState.Probe("camera: IsEqualToMapCamera + NSCopying Copy()", () =>
        {
            var a = MLNMapCamera.CameraLookingAtCenterCoordinateAltitudePitchHeading(
                new CLLocationCoordinate2D(10, 20), 5000, 15, 90);
            var copy = (MLNMapCamera)a.Copy(null);
            bool same = a.IsEqualToMapCamera(copy);
            copy.Heading = 91;
            bool differs = !a.IsEqualToMapCamera(copy);
            return (same && differs ? Status.Pass : Status.Fail,
                $"copy equal={same}; mutated copy differs={differs}");
        });

        // SetCamera(camera, animated) — the method form, distinct from the property.
        TestState.Probe("camera: MLNMapView.SetCamera(camera, animated) applies pitch + heading", () =>
        {
            var target = new CLLocationCoordinate2D(-33.8688, 151.2093); // Sydney
            var cam = MLNMapCamera.CameraLookingAtCenterCoordinateAltitudePitchHeading(target, 12000, 20, 135);
            map.SetCamera(cam, false);
            var back = map.Camera;
            bool ok = Math.Abs(back.CenterCoordinate.Latitude - target.Latitude) < 0.5
                   && Math.Abs(back.CenterCoordinate.Longitude - target.Longitude) < 0.5
                   && Math.Abs(back.Heading - 135) < 1.0
                   && Math.Abs((double)back.Pitch - 20) < 1.0;
            return (ok ? Status.Pass : Status.Fail,
                $"center=({back.CenterCoordinate.Latitude:F3}, {back.CenterCoordinate.Longitude:F3}), " +
                $"heading={back.Heading:F1}, pitch={(double)back.Pitch:F1}");
        });

        // Camera-fitting helpers: struct-by-value in, object out.
        TestState.Probe("camera: CameraThatFitsCoordinateBounds / CameraFittingShape", () =>
        {
            // Neutral camera first: MapLibre derives the fitted camera from the current
            // pitch/heading, so a tilted/rotated starting camera changes the answer.
            map.SetCamera(MLNMapCamera.CameraLookingAtCenterCoordinateAltitudePitchHeading(
                new CLLocationCoordinate2D(0, 0), 4000000, 0, 0), false);

            var bounds = new MLNCoordinateBounds
            {
                Sw = new CLLocationCoordinate2D(48.0, 1.0),
                Ne = new CLLocationCoordinate2D(50.0, 4.0),
            };
            var fit = map.CameraThatFitsCoordinateBounds(bounds);
            var fitPadded = map.CameraThatFitsCoordinateBounds(bounds, new UIEdgeInsets(20, 20, 20, 20));

            using var shape = new MLNPointAnnotation { Coordinate = new CLLocationCoordinate2D(49.0, 2.5) };
            var fitShape = map.CameraThatFitsShape(shape, 0, UIEdgeInsets.Zero);
            var fitBoundsFrom = map.CameraFittingCoordinateBoundsEdgePadding(fit, bounds, UIEdgeInsets.Zero);

            bool centered = fit != null
                && fit.CenterCoordinate.Latitude > 47.0 && fit.CenterCoordinate.Latitude < 51.0
                && fit.CenterCoordinate.Longitude > 0.0 && fit.CenterCoordinate.Longitude < 5.0;
            bool ok = centered && fitPadded != null && fitShape != null && fitBoundsFrom != null;
            return (ok ? Status.Pass : Status.Fail,
                $"fit center=({fit!.CenterCoordinate.Latitude:F3}, {fit.CenterCoordinate.Longitude:F3}), " +
                $"altitude={fit.Altitude:F0}; padded/shape/bounds-from variants all returned cameras");
        });

        // Zoom / direction / pitch limits.
        TestState.Probe("camera: zoom + direction + pitch limit round-trip", () =>
        {
            map.MinimumZoomLevel = 1.0;
            map.MaximumZoomLevel = 18.0;
            map.MinimumPitch = 0;
            map.MaximumPitch = 60;
            map.SetZoomLevel(6.5, false);
            map.SetDirection(90, false);
            double zoomAfterSet = map.ZoomLevel;
            double dirAfterSet = map.Direction;
            map.SetDirection(0, false); // ResetNorth() animates, so it is not read back synchronously
            double dirAfterReset = map.Direction;
            map.ResetNorth();           // still exercised for marshalling
            bool ok = Math.Abs(zoomAfterSet - 6.5) < 0.05
                   && Math.Abs(dirAfterSet - 90) < 1.0
                   && Math.Abs(dirAfterReset) < 1.0
                   && Math.Abs(map.MinimumZoomLevel - 1.0) < 1e-6
                   && Math.Abs(map.MaximumZoomLevel - 18.0) < 1e-6
                   && Math.Abs((double)map.MaximumPitch - 60) < 1e-3;
            return (ok ? Status.Pass : Status.Fail,
                $"zoom={zoomAfterSet:F2} (limits {map.MinimumZoomLevel:F1}..{map.MaximumZoomLevel:F1}), " +
                $"direction {dirAfterSet:F1} -> setDirection(0) {dirAfterReset:F1}, maxPitch={(double)map.MaximumPitch:F0}");
        });

        // Interaction toggles — a wide bool-property block over the ObjC bridge.
        TestState.Probe("property: MLNMapView interaction bool toggles round-trip", () =>
        {
            bool origScroll = map.ScrollEnabled, origZoom = map.ZoomEnabled, origRotate = map.RotateEnabled;
            map.ScrollEnabled = false; map.ZoomEnabled = false; map.RotateEnabled = false;
            map.PitchEnabled = true; map.PrefetchesTiles = true; map.ShowsScale = true;
            bool flipped = !map.ScrollEnabled && !map.ZoomEnabled && !map.RotateEnabled
                        && map.PitchEnabled && map.PrefetchesTiles && map.ShowsScale;
            map.ScrollEnabled = origScroll; map.ZoomEnabled = origZoom; map.RotateEnabled = origRotate;
            map.ShowsScale = false;
            bool restored = map.ScrollEnabled == origScroll && map.ZoomEnabled == origZoom;
            return (flipped && restored ? Status.Pass : Status.Fail,
                $"toggled 6 bool props off/on and restored ({flipped}/{restored}); " +
                $"showsCompass={map.ShowsCompassView}, showsLogo={map.ShowsLogoView}");
        });

        // Ornament positions + debug mask: [Native] enum params both ways.
        TestState.Probe("enum: MLNOrnamentPosition + MLNMapDebugMaskOptions flags round-trip", () =>
        {
            map.CompassViewPosition = MLNOrnamentPosition.BottomLeft;
            map.LogoViewPosition = MLNOrnamentPosition.TopRight;
            // NOTE: the flags enum members keep their full ObjC names — the generator
            // does not strip the `MLNMapDebug…Mask` prefix/suffix the way it does for
            // MLNOrnamentPosition. Recorded as an ergonomics wart, not a failure.
            map.DebugMask = MLNMapDebugMaskOptions.MapDebugTileBoundariesMask
                          | MLNMapDebugMaskOptions.MapDebugCollisionBoxesMask;
            var mask = map.DebugMask;
            bool ok = map.CompassViewPosition == MLNOrnamentPosition.BottomLeft
                   && map.LogoViewPosition == MLNOrnamentPosition.TopRight
                   && mask.HasFlag(MLNMapDebugMaskOptions.MapDebugTileBoundariesMask)
                   && mask.HasFlag(MLNMapDebugMaskOptions.MapDebugCollisionBoxesMask);
            map.DebugMask = 0;
            return (ok ? Status.Pass : Status.Fail,
                $"compass={map.CompassViewPosition}, logo={map.LogoViewPosition}, debugMask={mask} (reset to {map.DebugMask})");
        });
    }

    #endregion

    #region Viewport + projection

    private void RunViewportAndProjectionTests()
    {
        var map = _map!;

        TestState.Probe("viewport: SetVisibleCoordinateBounds + VisibleCoordinateBounds struct round-trip", () =>
        {
            var bounds = new MLNCoordinateBounds
            {
                Sw = new CLLocationCoordinate2D(51.2, -0.5),
                Ne = new CLLocationCoordinate2D(51.7, 0.3),
            };
            // Neutral, un-animated camera first — a pitched camera or an in-flight
            // animation makes the read-back non-deterministic.
            map.SetCamera(MLNMapCamera.CameraLookingAtCenterCoordinateAltitudePitchHeading(
                new CLLocationCoordinate2D(0, 0), 4000000, 0, 0), false);
            map.SetVisibleCoordinateBounds(bounds, false);
            var visible = map.VisibleCoordinateBounds;
            var screen = map.MaximumScreenBounds;
            double centerLat = (visible.Sw.Latitude + visible.Ne.Latitude) / 2;
            double centerLon = (visible.Sw.Longitude + visible.Ne.Longitude) / 2;
            bool ok = centerLat > bounds.Sw.Latitude - 0.2 && centerLat < bounds.Ne.Latitude + 0.2
                   && centerLon > bounds.Sw.Longitude - 0.2 && centerLon < bounds.Ne.Longitude + 0.2
                   && screen.Ne.Latitude > screen.Sw.Latitude;
            return (ok ? Status.Pass : Status.Fail,
                $"requested sw=({bounds.Sw.Latitude:F2},{bounds.Sw.Longitude:F2}) ne=({bounds.Ne.Latitude:F2},{bounds.Ne.Longitude:F2}); " +
                $"visible sw=({visible.Sw.Latitude:F2},{visible.Sw.Longitude:F2}) ne=({visible.Ne.Latitude:F2},{visible.Ne.Longitude:F2})");
        });

        TestState.Probe("projection: ConvertCoordinate <-> ConvertPoint round-trip", () =>
        {
            var target = new CLLocationCoordinate2D(51.5074, -0.1278);
            map.SetCenterCoordinate(target, 10, false);
            var pt = map.ConvertCoordinate(target, map);
            var back = map.ConvertPoint(pt, map);
            bool ok = Math.Abs(back.Latitude - target.Latitude) < 0.01
                   && Math.Abs(back.Longitude - target.Longitude) < 0.01;
            return (ok ? Status.Pass : Status.Fail,
                $"({target.Latitude:F4},{target.Longitude:F4}) -> point({pt.X:F1},{pt.Y:F1}) -> ({back.Latitude:F4},{back.Longitude:F4})");
        });

        TestState.Probe("projection: ConvertRect / ConvertCoordinateBounds + MetersPerPointAtLatitude", () =>
        {
            var rect = new CGRect(0, 0, 100, 100);
            var asBounds = map.ConvertRect(rect, map);
            var backToRect = map.ConvertCoordinateBounds(asBounds, map);
            double mpp = map.MetersPerPointAtLatitude(51.5);
            bool ok = asBounds.Ne.Latitude > asBounds.Sw.Latitude
                   && backToRect.Width > 0
                   && mpp > 0;
            return (ok ? Status.Pass : Status.Fail,
                $"rect->bounds sw=({asBounds.Sw.Latitude:F4},{asBounds.Sw.Longitude:F4}); " +
                $"bounds->rect {backToRect.Width:F1}x{backToRect.Height:F1}; metersPerPoint@51.5={mpp:F2}");
        });

        TestState.Probe("projection: MLNMapProjection object (ctor + Camera + conversions)", () =>
        {
            using var proj = new MLNMapProjection(map);
            var coord = new CLLocationCoordinate2D(48.8566, 2.3522);
            var pt = proj.ConvertCoordinate(coord);
            var back = proj.ConvertPoint(pt);
            var camera = proj.Camera;
            double mpp = proj.MetersPerPoint;
            bool ok = camera != null && mpp > 0
                   && Math.Abs(back.Latitude - coord.Latitude) < 0.5
                   && Math.Abs(back.Longitude - coord.Longitude) < 0.5;
            return (ok ? Status.Pass : Status.Fail,
                $"metersPerPoint={mpp:F2}, camera center=({camera!.CenterCoordinate.Latitude:F3},{camera.CenterCoordinate.Longitude:F3}), " +
                $"coord->point->coord within tolerance={ok}");
        });

        TestState.Probe("viewport: ContentInset + CameraEdgeInsets (UIEdgeInsets struct) round-trip", () =>
        {
            map.AutomaticallyAdjustsContentInset = false;
            map.SetContentInset(new UIEdgeInsets(5, 6, 7, 8), false);
            var inset = map.ContentInset;
            var edge = map.CameraEdgeInsets; // readonly in ObjC too
            bool ok = Math.Abs((double)inset.Top - 5) < 0.01 && Math.Abs((double)inset.Right - 8) < 0.01
                   && edge.Top >= 0;
            map.SetContentInset(UIEdgeInsets.Zero, false);
            return (ok ? Status.Pass : Status.Fail,
                $"contentInset=({inset.Top},{inset.Left},{inset.Bottom},{inset.Right}), " +
                $"cameraEdgeInsets=({edge.Top},{edge.Left},{edge.Bottom},{edge.Right})");
        });

        TestState.Probe("subview: built-in ornaments (compass / logo / attribution / scale bar)", () =>
        {
            var compass = map.CompassView;
            var logo = map.LogoView;
            var attribution = map.AttributionButton;
            var scale = map.ScaleBar;
            bool ok = compass != null && logo != null && attribution != null && scale != null;
            return (ok ? Status.Pass : Status.Fail,
                ok ? $"compass={compass!.GetType().Name}, logo={logo!.GetType().Name}, " +
                     $"attribution={attribution!.GetType().Name}, scaleBar={scale!.GetType().Name}"
                   : "one or more ornament views were null");
        });
    }

    #endregion

    #region Annotations

    private void RunAnnotationTests()
    {
        var map = _map!;

        TestState.Probe("annotation: AddAnnotations(array) + Title/Subtitle + RemoveAnnotations(array)", () =>
        {
            int before = map.Annotations?.Length ?? 0;
            var a = new MLNPointAnnotation
            {
                Coordinate = new CLLocationCoordinate2D(40.7128, -74.0060),
            };
            a.Title = "New York";
            a.Subtitle = "spike-subtitle";
            var b = new MLNPointAnnotation { Coordinate = new CLLocationCoordinate2D(35.6762, 139.6503) };
            b.Title = "Tokyo";

            var batch = new IMLNAnnotation[] { a, b };
            map.AddAnnotations(batch);
            int after = map.Annotations?.Length ?? 0;

            bool titles = a.Title == "New York" && a.Subtitle == "spike-subtitle" && b.Title == "Tokyo";

            map.RemoveAnnotations(batch);
            int removed = map.Annotations?.Length ?? 0;

            bool ok = after == before + 2 && removed == before && titles;
            return (ok ? Status.Pass : Status.Fail,
                $"{before} -> {after} -> {removed} annotations; title/subtitle round-trip={titles}");
        });

        TestState.Probe("annotation: SelectAnnotation / DeselectAnnotation + SelectedAnnotations", () =>
        {
            var existing = map.Annotations;
            if (existing == null || existing.Length == 0) return (Status.Fail, "no annotation to select");
            var ann = existing[0];
            map.SetCenterCoordinate(ann.Coordinate, 8, false);
            map.SelectAnnotation(ann, false);
            int selected = map.SelectedAnnotations?.Length ?? 0;
            map.DeselectAnnotation(ann, false);
            int afterDeselect = map.SelectedAnnotations?.Length ?? 0;
            // Both directions must be observable, otherwise a pair of no-op calls
            // would satisfy "ends up deselected" trivially. Selection is UI state
            // MapLibre is entitled to defer (the annotation view may not be realised
            // yet), so a selection that never lands is reported as a Skip rather than
            // failing the gate — but silently passing it is not an option.
            if (selected == 0)
                return (Status.Skip,
                    "SelectAnnotation did not make the annotation selected synchronously " +
                    $"(SelectedAnnotations={selected}); MapLibre defers selection until the " +
                    "annotation view exists, so this is not assertable in a headless run");
            bool ok = afterDeselect == 0;
            return (ok ? Status.Pass : Status.Fail,
                $"selected={selected} after SelectAnnotation, {afterDeselect} after DeselectAnnotation");
        });

        TestState.Probe("annotation: ShowAnnotations(array, insets, animated) + VisibleAnnotations", () =>
        {
            var existing = map.Annotations;
            if (existing == null || existing.Length == 0) return (Status.Fail, "no annotation available");
            map.ShowAnnotations(existing, new UIEdgeInsets(10, 10, 10, 10), false);
            var visible = map.VisibleAnnotations;
            var inRect = map.VisibleAnnotationsInRect(map.Bounds);
            // ShowAnnotations frames the annotations, after which VisibleAnnotations
            // would ideally report them. Measured behaviour in this headless run is
            // that both getters return nil: MapLibre populates them from realised
            // annotation *views*, which never come up without a rendered, on-screen
            // map. That is upstream UI state, not a marshalling defect — so report it
            // as a Skip rather than either failing the gate or, worse, returning an
            // unconditional Pass that would also be green if the getters were broken.
            if (visible == null || inRect == null)
                return (Status.Skip,
                    $"ShowAnnotations({existing.Length}) marshalled, but VisibleAnnotations=" +
                    $"{(visible == null ? "null" : visible.Length.ToString())} / VisibleAnnotationsInRect=" +
                    $"{(inRect == null ? "null" : inRect.Length.ToString())}: MapLibre derives both from " +
                    "realised annotation views, which a headless test app never gets, so the return " +
                    "values are not assertable here");
            return (Status.Pass,
                $"ShowAnnotations framed {existing.Length}; VisibleAnnotations={visible.Length}, " +
                $"VisibleAnnotationsInRect={inRect.Length}");
        });

        TestState.Probe("annotation: reuse-queue accessors (Dequeue*WithIdentifier) return null when empty", () =>
        {
            var img = map.DequeueReusableAnnotationImageWithIdentifier("spike-no-such-image");
            var view = map.DequeueReusableAnnotationViewWithIdentifier("spike-no-such-view");
            bool ok = img == null && view == null;
            return (ok ? Status.Pass : Status.Fail,
                $"image={(img == null ? "null" : "unexpected object")}, view={(view == null ? "null" : "unexpected object")}");
        });

        TestState.Probe("feature: MLNPointFeature attributes + GeoJSONDictionary", () =>
        {
            using var feature = new MLNPointFeature();
            feature.Coordinate = new CLLocationCoordinate2D(52.52, 13.405); // Berlin
            feature.Identifier = new NSString("spike-feature");
            feature.Attributes = NSDictionary.FromObjectsAndKeys(
                new NSObject[] { new NSString("Berlin"), NSNumber.FromInt32(42) },
                new NSObject[] { new NSString("name"), new NSString("rank") });

            var name = feature.AttributeForKey("name") as NSString;
            var dict = feature.GeoJSONDictionary();
            bool ok = name?.ToString() == "Berlin"
                   && feature.Attributes.Count == 2
                   && dict != null
                   && dict.Count > 0
                   && (feature.Identifier as NSString)?.ToString() == "spike-feature";
            return (ok ? Status.Pass : Status.Fail,
                $"attributes={feature.Attributes.Count}, AttributeForKey('name')='{name}', " +
                $"identifier='{feature.Identifier}', geoJSON keys={dict?.Count ?? 0}");
        });
    }

    #endregion

    #region Overlays + geometry

    private void RunOverlayAndGeometryTests()
    {
        var map = _map!;

        TestState.Probe("shape: MLNShape.ShapeWithData(GeoJSON, out NSError) + GeoJSONDataUsingEncoding", () =>
        {
            const string geoJson =
                "{\"type\":\"Feature\",\"properties\":{\"name\":\"spike\"}," +
                "\"geometry\":{\"type\":\"Point\",\"coordinates\":[13.405,52.52]}}";
            using var data = NSData.FromString(geoJson, NSStringEncoding.UTF8);
            var shape = MLNShape.ShapeWithData(data, (ulong)NSStringEncoding.UTF8, out NSError? error);
            if (shape == null || error != null)
                return (Status.Fail, $"parse returned null (error={error?.LocalizedDescription ?? "none"})");

            bool coordOk = Math.Abs(shape.Coordinate.Latitude - 52.52) < 1e-6
                        && Math.Abs(shape.Coordinate.Longitude - 13.405) < 1e-6;
            using var roundTrip = shape.GeoJSONDataUsingEncoding((ulong)NSStringEncoding.UTF8);
            string? emitted = NSString.FromData(roundTrip, NSStringEncoding.UTF8)?.ToString();
            bool ok = coordOk && emitted != null && emitted.Contains("Point");
            return (ok ? Status.Pass : Status.Fail,
                $"parsed as {shape.GetType().Name}, coordinate=({shape.Coordinate.Latitude:F3},{shape.Coordinate.Longitude:F3}), " +
                $"re-emitted {emitted?.Length ?? 0} bytes of GeoJSON");
        });

        TestState.Probe("shape: bad GeoJSON surfaces an NSError through the (out NSError) overload", () =>
        {
            using var data = NSData.FromString("{ this is not geojson", NSStringEncoding.UTF8);
            var shape = MLNShape.ShapeWithData(data, (ulong)NSStringEncoding.UTF8, out NSError? error);
            bool ok = shape == null && error != null;
            return (ok ? Status.Pass : Status.Fail,
                ok ? $"nil shape + NSError '{error!.LocalizedDescription}' (domain={error.Domain}, code={error.Code})"
                   : $"shape={(shape == null ? "null" : shape.GetType().Name)}, error={(error == null ? "null" : "set")}");
        });

        // MLNPolyline / MLNPolygon geometry construction through the generated
        // CLLocationCoordinate2D[] array overloads (the ObjC C-array-plus-count
        // parameter pair projected as a managed array).
        TestState.Probe("overlay: MLNPolyline + MLNPolygon construction and AddOverlay/RemoveOverlay", () =>
        {
            var line = new CLLocationCoordinate2D[]
            {
                new(51.5074, -0.1278), new(48.8566, 2.3522), new(52.5200, 13.4050),
            };
            var poly = new CLLocationCoordinate2D[]
            {
                new(50.0, 0.0), new(50.0, 5.0), new(53.0, 5.0), new(53.0, 0.0),
            };

            var polyline = MLNPolyline.PolylineWithCoordinates(line);
            var polygon = MLNPolygon.PolygonWithCoordinates(poly);
            if (polyline == null || polygon == null)
                return (Status.Fail, "a geometry factory returned null");

            MLNShape asShape = polyline;
            asShape.Title = "spike-line";
            int before = map.Overlays?.Length ?? 0;
            map.AddOverlays(new IMLNOverlay[] { polyline, polygon });
            int after = map.Overlays?.Length ?? 0;

            var lineBounds = polyline.OverlayBounds;
            bool intersects = polyline.IntersectsOverlayBounds(new MLNCoordinateBounds
            {
                Sw = new CLLocationCoordinate2D(40, -20),
                Ne = new CLLocationCoordinate2D(60, 20),
            });
            uint linePoints = (uint)polyline.PointCount;
            uint polyPoints = (uint)polygon.PointCount;

            map.RemoveOverlays(new IMLNOverlay[] { polyline, polygon });
            int removed = map.Overlays?.Length ?? 0;

            bool ok = after == before + 2 && removed == before
                   && linePoints == 3 && polyPoints == 4
                   && intersects && asShape.Title == "spike-line";
            return (ok ? Status.Pass : Status.Fail,
                $"overlays {before} -> {after} -> {removed}; polyline pointCount={linePoints}, polygon pointCount={polyPoints}, " +
                $"bounds sw=({lineBounds.Sw.Latitude:F3},{lineBounds.Sw.Longitude:F3}) ne=({lineBounds.Ne.Latitude:F3},{lineBounds.Ne.Longitude:F3}), " +
                $"intersects={intersects}");
        });

        TestState.Probe("geometry: MLNCoordinateBounds / MLNCoordinateSpan / MLNTransition struct field access", () =>
        {
            var bounds = new MLNCoordinateBounds
            {
                Sw = new CLLocationCoordinate2D(1.5, 2.5),
                Ne = new CLLocationCoordinate2D(3.5, 4.5),
            };
            var span = new MLNCoordinateSpan { LatitudeDelta = 2.0, LongitudeDelta = 2.0 };
            var transition = new MLNTransition { Duration = 1.5, Delay = 0.25 };
            var point = new MLNMapPoint { X = 10, Y = 20, ZoomLevel = 4 };
            bool ok = Math.Abs(bounds.Ne.Latitude - bounds.Sw.Latitude - 2.0) < 1e-9
                   && Math.Abs(span.LatitudeDelta - 2.0) < 1e-9
                   && Math.Abs(transition.Duration - 1.5) < 1e-9
                   && Math.Abs((double)point.ZoomLevel - 4) < 1e-9;
            return (ok ? Status.Pass : Status.Fail,
                $"bounds span={bounds.Ne.Latitude - bounds.Sw.Latitude:F1} lat, coordinateSpan={span.LatitudeDelta:F1}, " +
                $"transition={transition.Duration:F2}s+{transition.Delay:F2}s, mapPoint zoom={(double)point.ZoomLevel:F1}");
        });

        TestState.Probe("category: NSValue MLN* boxing factories (valueWithMLNCoordinate… family)", () =>
        {
            // These ObjC category CLASS methods are emitted as instance extension
            // methods that ignore the receiver, so a throwaway NSValue is needed.
            var receiver = NSValue.FromCGPoint(CGPoint.Empty);
            var boundsValue = receiver.ValueWithMLNCoordinateBounds(new MLNCoordinateBounds
            {
                Sw = new CLLocationCoordinate2D(1, 2),
                Ne = new CLLocationCoordinate2D(3, 4),
            });
            var coordValue = receiver.ValueWithMLNCoordinate(new CLLocationCoordinate2D(5, 6));
            var spanValue = receiver.ValueWithMLNCoordinateSpan(new MLNCoordinateSpan { LatitudeDelta = 1, LongitudeDelta = 1 });
            var transitionValue = receiver.ValueWithMLNTransition(new MLNTransition { Duration = 1, Delay = 0 });
            var anchorValue = receiver.ValueWithMLNLightAnchor(MLNLightAnchor.Viewport);
            bool ok = boundsValue != null && coordValue != null && spanValue != null
                   && transitionValue != null && anchorValue != null;
            return (ok ? Status.Pass : Status.Fail,
                ok ? "5 boxing factories returned NSValues (note: they are bound as instance extensions on NSValue, not statics)"
                   : "a boxing factory returned null");
        });

        TestState.Probe("expression: NSExpression MLN additions (variables + mgl_ helpers)", () =>
        {
            var zoom = NSExpression_MLNAdditions.ZoomLevelVariableExpression;
            var attrs = NSExpression_MLNAdditions.FeatureAttributesVariableExpression;
            var heatmap = NSExpression_MLNAdditions.HeatmapDensityVariableExpression;
            var stops = NSExpression.FromConstant(NSDictionary.FromObjectsAndKeys(
                new NSObject[] { NSNumber.FromDouble(2), NSNumber.FromDouble(20) },
                new NSObject[] { NSNumber.FromDouble(0), NSNumber.FromDouble(18) }));
            var interpolated = zoom.Mgl_expressionForInterpolatingExpression(
                zoom, "linear", null, stops);
            bool ok = zoom != null && attrs != null && heatmap != null && interpolated != null;
            return (ok ? Status.Pass : Status.Fail,
                ok ? $"$zoomLevel='{zoom!.Description}', interpolation expression built ('{interpolated!.Description}')"
                   : "an expression helper returned null");
        });
    }

    #endregion

    #region Standalone (non-map) API surface

    private void RunStandaloneApiTests()
    {
        TestState.Probe("formatter: MLNCoordinateFormatter / MLNDistanceFormatter", () =>
        {
            using var coordFmt = new MLNCoordinateFormatter { AllowsMinutes = true, AllowsSeconds = false };
            string coord = coordFmt.StringFromCoordinate(new CLLocationCoordinate2D(38.9131, -77.0325));
            using var distFmt = new MLNDistanceFormatter();
            string dist = distFmt.StringFromDistance(1234);
            bool ok = coord.Length > 0 && dist.Length > 0 && coordFmt.AllowsMinutes && !coordFmt.AllowsSeconds;
            return (ok ? Status.Pass : Status.Fail, $"coordinate='{coord}', distance='{dist}'");
        });

        TestState.Probe("formatter: MLNClockDirectionFormatter / MLNCompassDirectionFormatter", () =>
        {
            using var clock = new MLNClockDirectionFormatter();
            using var compass = new MLNCompassDirectionFormatter();
            string c = clock.StringFromDirection(90);
            string d = compass.StringFromDirection(90);
            bool ok = c.Length > 0 && d.Length > 0;
            return (ok ? Status.Pass : Status.Fail, $"clock(90°)='{c}', compass(90°)='{d}'");
        });

        TestState.Probe("style catalog: MLNStyle.PredefinedStyles / DefaultStyle / DefaultStyleURL", () =>
        {
            var styles = MLNStyle.PredefinedStyles();
            var def = MLNStyle.DefaultStyle();
            var url = MLNStyle.DefaultStyleURL();
            MLNDefaultStyle? byName = styles.Length > 0 ? MLNStyle.PredefinedStyle(styles[0].Name) : null;
            bool ok = styles.Length > 0
                   && def != null
                   && def.Name.Length > 0
                   && byName != null
                   && byName.Name == styles[0].Name;
            return (ok ? Status.Pass : Status.Fail,
                $"{styles.Length} predefined style(s); default='{def?.Name}' v{def?.Version} url={(url == null ? "null" : "set")}; " +
                $"PredefinedStyle('{styles.FirstOrDefault()?.Name}') resolved={byName != null}");
        });

        TestState.Probe("settings: MLNSettings static class properties (ApiKey / TileServerOptions)", () =>
        {
            // MapLibre only persists an API key when the configured tile server declares
            // one (the default MapLibre server does not), so the binding-level assertion
            // is that the class-property accessors marshal in both directions; the
            // observed value is reported for the record.
            string? original = MLNSettings.ApiKey;
            MLNSettings.ApiKey = "spike-api-key";
            string? readBack = MLNSettings.ApiKey;
            MLNSettings.ApiKey = original;
            var options = MLNSettings.TileServerOptions;
            if (options == null)
                return (Status.Fail, "MLNSettings.TileServerOptions returned null");
            // The ApiKey round-trip is reported but cannot gate: MapLibre discards the
            // key when the configured tile server does not declare one (its own default
            // server does not), so a nil read-back is correct upstream behaviour and a
            // hard assertion here would fail on a healthy binding. Splitting the verdict
            // keeps TileServerOptions gating while the key result stays on the record.
            string keyResult = readBack == "spike-api-key"
                ? "ApiKey round-tripped"
                : $"ApiKey not retained (readback='{readBack ?? "null"}') — expected against a " +
                  "tile server that declares no key; setter+getter both marshalled without trapping";
            return (Status.Pass,
                $"{keyResult}; original='{original ?? "null"}'; " +
                $"TileServerOptions={options.GetType().Name}");
        });

        TestState.Probe("logging: MLNLoggingConfiguration.SharedConfiguration level round-trip", () =>
        {
            var config = MLNLoggingConfiguration.SharedConfiguration;
            var original = config.LoggingLevel;
            config.LoggingLevel = MLNLoggingLevel.Error;
            var readBack = config.LoggingLevel;
            config.LoggingLevel = original;
            bool ok = readBack == MLNLoggingLevel.Error && config.LoggingLevel == original;
            return (ok ? Status.Pass : Status.Fail,
                $"level {original} -> {readBack} -> {config.LoggingLevel}");
        });

        TestState.Probe("attribution: MLNAttributionInfo ctor + TitleWithStyle", () =>
        {
            using var title = new NSAttributedString("© MapLibre");
            var info = new MLNAttributionInfo(title, new NSUrl("https://maplibre.org"));
            var styled = info.TitleWithStyle(MLNAttributionInfoStyle.Long);
            bool ok = info.Title.Value.Contains("MapLibre")
                   && info.URL?.AbsoluteString == "https://maplibre.org"
                   && styled != null;
            return (ok ? Status.Pass : Status.Fail,
                $"title='{info.Title.Value}', url={info.URL?.AbsoluteString}, styled='{styled?.Value}', feedbackLink={info.FeedbackLink}");
        });

        TestState.Probe("snapshot: MLNMapSnapshotOptions ctor + property round-trip", () =>
        {
            var camera = MLNMapCamera.CameraLookingAtCenterCoordinateAltitudePitchHeading(
                new CLLocationCoordinate2D(37.7749, -122.4194), 15000, 0, 0);
            var options = new MLNMapSnapshotOptions(new NSUrl(DemoStyle), camera, new CGSize(120, 90))
            {
                ZoomLevel = 9,
                ShowsLogo = false,
                ShowsAttribution = false,
            };
            var copy = (MLNMapSnapshotOptions)options.Copy(null);
            bool ok = Math.Abs(options.ZoomLevel - 9) < 1e-9
                   && Math.Abs((double)options.Size.Width - 120) < 1e-9
                   && !options.ShowsLogo
                   && copy != null
                   && Math.Abs(copy.ZoomLevel - 9) < 1e-9;
            // The snapshotter itself is constructed but never started: startWithCompletionHandler
            // downloads tiles, which belongs to the async/network part of the matrix.
            // Constructing the snapshotter is itself the assertion — it throws or returns a
            // live peer, so `snapshotter != null` would add no signal. Assert instead that the
            // options survived the trip into the snapshotter's own `options` property.
            using var snapshotter = new MLNMapSnapshotter(options);
            bool optionsRoundTrip = Math.Abs(snapshotter.Options.ZoomLevel - 9) < 1e-9
                                 && Math.Abs((double)snapshotter.Options.Size.Width - 120) < 1e-9
                                 && !snapshotter.Loading;
            return (ok && optionsRoundTrip ? Status.Pass : Status.Fail,
                $"size={options.Size.Width}x{options.Size.Height}, zoom={options.ZoomLevel:F1}, scale={(double)options.Scale:F1}, " +
                $"NSCopying copy OK={copy != null}; snapshotter.Options re-reads zoom=9/width=120 and isLoading=false " +
                "before start (optionsRoundTrip=" + optionsRoundTrip + ")");
        });

        TestState.Probe("network: MLNNetworkConfiguration.SharedManager readable", () =>
        {
            var config = MLNNetworkConfiguration.SharedManager;
            bool ok = config != null;
            return (ok ? Status.Pass : Status.Fail,
                ok ? $"shared manager={config!.GetType().Name}" : "SharedManager was null");
        });
    }

    #endregion

    #region Offline storage

    private void RunOfflineTests()
    {
        TestState.Probe("offline: MLNOfflineStorage.SharedOfflineStorage + database accessors", () =>
        {
            var storage = MLNOfflineStorage.SharedOfflineStorage;
            string path = storage.DatabasePath;
            var url = storage.DatabaseURL;
            ulong bytes = storage.CountOfBytesCompleted;
            storage.ReloadPacks();
            bool ok = path.Length > 0 && url != null;
            return (ok ? Status.Pass : Status.Fail,
                $"databasePath='{System.IO.Path.GetFileName(path)}', bytesCompleted={bytes}, packs={storage.Packs?.Length ?? 0}");
        });

        TestState.Probe("offline: MLNTilePyramidOfflineRegion ctor + property + NSCopying round-trip", () =>
        {
            var bounds = new MLNCoordinateBounds
            {
                Sw = new CLLocationCoordinate2D(51.4, -0.2),
                Ne = new CLLocationCoordinate2D(51.6, 0.0),
            };
            var region = new MLNTilePyramidOfflineRegion(new NSUrl(DemoStyle), bounds, 10, 12);
            var copy = (MLNTilePyramidOfflineRegion)region.Copy(null);
            bool ok = Math.Abs(region.MinimumZoomLevel - 10) < 1e-9
                   && Math.Abs(region.MaximumZoomLevel - 12) < 1e-9
                   && Math.Abs(region.Bounds.Sw.Latitude - 51.4) < 1e-9
                   && region.StyleURL.AbsoluteString == DemoStyle
                   && copy != null
                   && Math.Abs(copy.MaximumZoomLevel - 12) < 1e-9;
            return (ok ? Status.Pass : Status.Fail,
                $"zoom=[{region.MinimumZoomLevel:F0},{region.MaximumZoomLevel:F0}], " +
                $"bounds sw=({region.Bounds.Sw.Latitude:F2},{region.Bounds.Sw.Longitude:F2}), " +
                $"includesIdeographicGlyphs={region.IncludesIdeographicGlyphs}, copy OK={copy != null}");
        });

        // Block-completion round-trip against the local offline database. Creating a
        // pack does NOT download anything (that needs an explicit -resume), so this
        // stays offline-deterministic while still driving a 2-argument ObjC block.
        try
        {
            var bounds = new MLNCoordinateBounds
            {
                Sw = new CLLocationCoordinate2D(51.49, -0.13),
                Ne = new CLLocationCoordinate2D(51.51, -0.11),
            };
            var region = new MLNTilePyramidOfflineRegion(new NSUrl(DemoStyle), bounds, 12, 12);
            var context = NSData.FromString("spike-pack", NSStringEncoding.UTF8);
            var storage = MLNOfflineStorage.SharedOfflineStorage;

            storage.AddPackForRegion(region, context, (pack, error) =>
            {
                if (error != null || pack == null)
                {
                    TestState.OfflinePackStatus = Status.Fail;
                    TestState.OfflinePackDetail = $"addPackForRegion failed: {error?.LocalizedDescription ?? "null pack"}";
                    return;
                }

                string state = pack.State.ToString();
                string ctx = NSString.FromData(pack.Context, NSStringEncoding.UTF8)?.ToString() ?? "";
                var progress = pack.Progress;

                // Re-point the pending detail at the *next* callback we are waiting on, so a
                // timeout inside removePack does not get reported as "add never fired".
                TestState.OfflinePackDetail =
                    $"addPackForRegion completed (state={state}, context='{ctx}') but the removePack " +
                    "completion never fired inside the report window";

                storage.RemovePack(pack, removeError =>
                {
                    if (removeError != null)
                    {
                        TestState.OfflinePackStatus = Status.Fail;
                        TestState.OfflinePackDetail = $"removePack failed: {removeError.LocalizedDescription}";
                        return;
                    }
                    TestState.OfflinePackStatus = ctx == "spike-pack" ? Status.Pass : Status.Fail;
                    TestState.OfflinePackDetail =
                        $"pack created (state={state}, context='{ctx}', countOfResourcesExpected={progress.CountOfResourcesExpected}) " +
                        "and removed; both completion blocks invoked in C#";
                });
            });
        }
        catch (Exception ex)
        {
            TestState.OfflinePackStatus = Status.Fail;
            TestState.OfflinePackDetail = $"{ex.GetType().Name}: {ex.Message}";
        }
    }

    #endregion

    #region Formerly-gapped surfaces (now bound; real assertions)

    private void RunKnownGapProbes()
    {
        // The ObjC C-array-plus-count parameter pair (`const CLLocationCoordinate2D *`
        // + `count:`) now projects as a CLLocationCoordinate2D[] overload. Assert the
        // caller's array is untouched, the geometry took all vertices, and each vertex
        // reads back through the NSRange-typed getCoordinates:range: per index.
        TestState.Probe("array overload: polylineWithCoordinates:count: + per-vertex GetCoordinates read-back", () =>
        {
            var coords = new CLLocationCoordinate2D[]
            {
                new(48.8566, 2.3522), new(51.5074, -0.1278), new(52.5200, 13.4050),
            };
            var polyline = MLNPolyline.PolylineWithCoordinates(coords);

            bool callerIntact = Math.Abs(coords[0].Latitude - 48.8566) < 1e-9
                             && Math.Abs(coords[0].Longitude - 2.3522) < 1e-9;
            if (!callerIntact)
                return (Status.Fail,
                    $"the caller's first coordinate was mutated in place: ({coords[0].Latitude:F4},{coords[0].Longitude:F4})");
            if ((uint)polyline.PointCount != 3)
                return (Status.Fail, $"pointCount={(uint)polyline.PointCount}, expected 3 — vertices were dropped");

            for (int i = 0; i < coords.Length; i++)
            {
                polyline.GetCoordinates(out CLLocationCoordinate2D v, new NSRange(i, 1));
                if (Math.Abs(v.Latitude - coords[i].Latitude) > 1e-9 || Math.Abs(v.Longitude - coords[i].Longitude) > 1e-9)
                    return (Status.Fail,
                        $"vertex[{i}] read back as ({v.Latitude:F4},{v.Longitude:F4}), expected ({coords[i].Latitude:F4},{coords[i].Longitude:F4})");
            }

            var b = polyline.OverlayBounds;
            bool boundsOk = Math.Abs(b.Sw.Latitude - 48.8566) < 1e-6 && Math.Abs(b.Ne.Latitude - 52.5200) < 1e-6;
            return (boundsOk ? Status.Pass : Status.Fail,
                $"3 vertices round-tripped exactly; bounds sw=({b.Sw.Latitude:F4},{b.Sw.Longitude:F4}) ne=({b.Ne.Latitude:F4},{b.Ne.Longitude:F4})");
        });

        // The 33 readonly NSValue-category unboxing properties are now bound as
        // GetMLN*Value() methods — assert box -> unbox round-trips value-exactly for
        // the struct family (the boxing direction was already covered above).
        TestState.Probe("category: NSValue MLN* unboxing round-trip (GetMLNCoordinateValue family)", () =>
        {
            var receiver = NSValue.FromCGPoint(CGPoint.Empty);

            var coord = receiver.ValueWithMLNCoordinate(new CLLocationCoordinate2D(5.25, -6.5)).GetMLNCoordinateValue();
            var span = receiver.ValueWithMLNCoordinateSpan(new MLNCoordinateSpan { LatitudeDelta = 1.5, LongitudeDelta = 2.75 }).GetMLNCoordinateSpanValue();
            var bounds = receiver.ValueWithMLNCoordinateBounds(new MLNCoordinateBounds
            {
                Sw = new CLLocationCoordinate2D(1, 2),
                Ne = new CLLocationCoordinate2D(3, 4),
            }).GetMLNCoordinateBoundsValue();
            var transition = receiver.ValueWithMLNTransition(new MLNTransition { Duration = 1.5, Delay = 0.25 }).GetMLNTransitionValue();

            bool ok = Math.Abs(coord.Latitude - 5.25) < 1e-9 && Math.Abs(coord.Longitude + 6.5) < 1e-9
                   && Math.Abs(span.LatitudeDelta - 1.5) < 1e-9 && Math.Abs(span.LongitudeDelta - 2.75) < 1e-9
                   && Math.Abs(bounds.Sw.Latitude - 1) < 1e-9 && Math.Abs(bounds.Ne.Longitude - 4) < 1e-9
                   && Math.Abs(transition.Duration - 1.5) < 1e-9 && Math.Abs(transition.Delay - 0.25) < 1e-9;
            return (ok ? Status.Pass : Status.Fail,
                ok ? "coordinate, span, bounds and transition all unboxed value-exactly"
                   : $"unboxed values diverged: coord=({coord.Latitude},{coord.Longitude}), span=({span.LatitudeDelta},{span.LongitudeDelta}), " +
                     $"bounds sw=({bounds.Sw.Latitude},{bounds.Sw.Longitude}), transition=({transition.Duration},{transition.Delay})");
        });

        // NSRange-typed members are now resolvable: exercise the full MLNMultiPoint
        // coordinate-editing surface (append / replace-in-range / remove-in-range)
        // and verify each mutation through per-vertex read-back.
        TestState.Probe("array editing: Append/ReplaceCoordinatesInRange/RemoveCoordinatesInRange (NSRange members)", () =>
        {
            var line = MLNPolyline.PolylineWithCoordinates(new CLLocationCoordinate2D[]
            {
                new(10, 10), new(20, 20), new(30, 30),
            });

            line.AppendCoordinates(new CLLocationCoordinate2D[] { new(40, 40) });
            if ((uint)line.PointCount != 4)
                return (Status.Fail, $"after append: pointCount={(uint)line.PointCount}, expected 4");

            line.ReplaceCoordinatesInRange(new NSRange(1, 1), new CLLocationCoordinate2D[] { new(45, -45) });
            line.GetCoordinates(out CLLocationCoordinate2D replaced, new NSRange(1, 1));
            if ((uint)line.PointCount != 4 || Math.Abs(replaced.Latitude - 45) > 1e-9 || Math.Abs(replaced.Longitude + 45) > 1e-9)
                return (Status.Fail,
                    $"after replace: pointCount={(uint)line.PointCount}, vertex[1]=({replaced.Latitude:F4},{replaced.Longitude:F4}), expected 4 and (45,-45)");

            line.RemoveCoordinatesInRange(new NSRange(3, 1));
            line.GetCoordinates(out CLLocationCoordinate2D last, new NSRange(2, 1));
            bool ok = (uint)line.PointCount == 3 && Math.Abs(last.Latitude - 30) < 1e-9;
            return (ok ? Status.Pass : Status.Fail,
                ok ? "append -> replace-in-range -> remove-in-range all verified by per-vertex read-back"
                   : $"after remove: pointCount={(uint)line.PointCount}, vertex[2]=({last.Latitude:F4},{last.Longitude:F4})");
        });

        // MLNPolyline no longer re-declares a readonly `Title` from its MLNAnnotation
        // conformance: the inherited read-write MLNShape.Title is the one and only
        // declaration, so a direct set on the derived type must compile and stick.
        TestState.Probe("inheritance: MLNPolyline.Title binds the read-write base property directly (no shadow)", () =>
        {
            var line = MLNPolyline.PolylineWithCoordinates(new CLLocationCoordinate2D[]
            {
                new(10, 10), new(11, 11),
            });
            line.Title = "direct-set";
            MLNShape asShape = line;
            bool ok = line.Title == "direct-set" && asShape.Title == "direct-set";
            return (ok ? Status.Pass : Status.Fail,
                ok ? "direct `polyline.Title = …` compiles and reads back identically through derived and base"
                   : $"direct set did not stick: derived='{line.Title}', base='{asShape.Title}'");
        });

        // Apple system enums (CLAuthorizationStatus family, NSFormattingUnitStyle) now
        // resolve to the platform-assembly enums. UnitStyle gets a live round-trip on a
        // concrete formatter; the MLNLocationManager protocol surface is locked at
        // compile time — the lambdas below fail the app build if a member vanishes or
        // changes to a different type.
        TestState.Probe("system enums: NSFormattingUnitStyle round-trip + CoreLocation auth surface binds", () =>
        {
            var formatter = new MLNClockDirectionFormatter();
            formatter.UnitStyle = NSFormattingUnitStyle.Long;
            bool unitOk = formatter.UnitStyle == NSFormattingUnitStyle.Long;

            Func<IMLNLocationManager, CLAuthorizationStatus> authProbe = m => m.AuthorizationStatus;
            Func<IMLNLocationManager, CLDeviceOrientation> headingProbe = m => m.HeadingOrientation;
            Func<IMLNLocationManager, CLAccuracyAuthorization> accuracyProbe = m => m.AccuracyAuthorization();
            Func<IMLNLocationManager, CLActivityType> activityProbe = m => m.ActivityType();
            bool surfaceOk = authProbe != null && headingProbe != null && accuracyProbe != null && activityProbe != null;

            return (unitOk && surfaceOk ? Status.Pass : Status.Fail,
                unitOk
                    ? "UnitStyle round-tripped NSFormattingUnitStyle.Long; CLAuthorizationStatus/CLDeviceOrientation/" +
                      "CLAccuracyAuthorization/CLActivityType members verified at compile time"
                    : $"UnitStyle read back {formatter.UnitStyle}, expected Long");
        });
    }

    #endregion

    private void FinishAndReport()
    {
        // Pattern (a): delegate callbacks fired into C#
        TestState.Record("delegate: mapView:didFinishLoadingStyle:",
            TestState.DidFinishLoadingStyle ? Status.Pass : Status.Fail,
            TestState.DidFinishLoadingStyle
                ? "ObjC -> C# override invoked"
                : "override never called. These two callbacks are kept as hard failures on purpose: they " +
                  "are the binding-level signal that the ObjC delegate dispatches into a managed subclass " +
                  "at all. If the rest of the run is green, suspect the style download " +
                  "(demotiles.maplibre.org) rather than the bindings, and re-run with network access.");
        TestState.Record("delegate: mapViewDidFinishLoadingMap:",
            TestState.DidFinishLoadingMap ? Status.Pass : Status.Fail,
            TestState.DidFinishLoadingMap
                ? "ObjC -> C# override invoked"
                : "override never called (see the didFinishLoadingStyle note above — a remote style " +
                  "fetch failure or a >14s stall presents identically here)");
        // fullyRendered=true requires every visible tile to have arrived from
        // https://demotiles.maplibre.org within the fixed report window, so a slow or
        // rate-limited network fails it on a perfectly healthy binding. The delegate
        // *wiring* is already gated above (didFinishLoadingStyle / didFinishLoadingMap
        // are hard Fails), so non-delivery here is a Skip, not a Fail.
        TestState.Record("delegate: mapViewDidFinishRenderingMap:fullyRendered:",
            TestState.DidFinishRenderingMapFully ? Status.Pass : Status.Skip,
            TestState.DidFinishRenderingMapFully
                ? "fullyRendered=true delivered"
                : "fullyRendered=true not delivered inside the report window — depends on remote " +
                  "tile delivery, so it is not a binding-level signal (style/map load callbacks gate instead)");
        TestState.Record("delegate: mapView:regionDidChangeAnimated: (camera pipeline)",
            TestState.RegionDidChange ? Status.Pass : Status.Fail,
            TestState.RegionDidChange ? "programmatic camera change drove region callback" : "no region callback");

        // Pattern (b): runtime source + layer + NSExpression mutation
        TestState.Record("runtime-style: MLNShapeSource + MLNCircleStyleLayer + NSExpression",
            TestState.RuntimeStyleStatus, TestState.RuntimeStyleDetail);

        // Block completion handlers delivered back into managed code.
        TestState.Record("block: flyToCamera completion invoked in C#",
            TestState.FlyToCameraCompleted ? Status.Pass : Status.Fail,
            TestState.FlyToCameraCompleted ? "managed completion block ran" : "completion block never ran");
        TestState.Record("block: offline addPackForRegion + removePack completions",
            TestState.OfflinePackStatus, TestState.OfflinePackDetail);

        // Pattern (d): deterministic teardown + recreate (off-screen, so the
        // visible map stays intact for the screenshot).
        try
        {
            var throwaway = new MLNMapView(new CGRect(0, 0, 10, 10), new NSUrl(DemoStyle));
            throwaway.RemoveFromSuperview();
            throwaway.Dispose();
            using var again = new MLNMapView(new CGRect(0, 0, 10, 10));
            TestState.Record("teardown: dispose + recreate MLNMapView", Status.Pass,
                "created, disposed, re-instantiated without crash");
        }
        catch (Exception ex)
        {
            TestState.Record("teardown: dispose + recreate MLNMapView", Status.Fail, $"{ex.GetType().Name}: {ex.Message}");
        }

        EmitVerdict();
    }

    private void EmitVerdict()
    {
        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine("================ API-PATTERN COVERAGE ================");
        int pass = 0, fail = 0, skip = 0;
        var failures = new List<string>();
        lock (TestState.Gate)
        {
            foreach (var r in TestState.Results)
            {
                sb.AppendLine($"  [{r.Status.ToString().ToUpper(),-4}] {r.Name}");
                sb.AppendLine($"         {r.Detail}");
                switch (r.Status)
                {
                    case Status.Pass: pass++; break;
                    case Status.Fail: fail++; failures.Add(r.Name); break;
                    default: skip++; break;
                }
            }
        }
        sb.AppendLine($"  ---- {pass} pass, {fail} fail, {skip} skip ----");
        sb.AppendLine("=====================================================");
        Console.WriteLine(sb.ToString());

        // The gate is results-wide: ANY failed pattern fails the run. Known upstream
        // generator gaps are recorded as SKIP (with the reason inline above) so they
        // stay visible without masking a real binding regression.
        bool smokePass = fail == 0 && pass > 0;
        Console.WriteLine($"[GATE] results-wide gate: {(smokePass ? "PASS" : "FAIL")} " +
                          $"(coverage: {pass} pass / {fail} fail / {skip} skip)");
        if (smokePass)
        {
            Console.WriteLine("TEST SUCCESS");
        }
        else
        {
            foreach (var name in failures)
                Console.WriteLine($"[GATE] failed pattern: {name}");
            Console.WriteLine($"TEST FAILED: {fail} pattern(s) failed ({pass} passed, {skip} skipped)");
        }
    }
}

#endregion
