// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.
//
// MapLibre Native iOS binding SPIKE test app.
//
// This is NOT a simple "types resolve" smoke test. The headline gate is a
// rendered map frame (captured by an external screenshot), but the real interop
// risk for a pure Objective-C framework is whether the generator emits USABLE
// bindings for the ObjC patterns MapLibre relies on: delegate/optional-protocol
// callbacks firing back into C#, NSExpression-based runtime styling, ObjC
// collection mutation, gestures, and deterministic object teardown.
//
// The app exercises an "API-pattern coverage" matrix and prints each result, so
// the spike report can state precisely which patterns WORK vs BREAK.

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
    public static string RuntimeStyleDetail = "delegate did not fire";
    public static Status RuntimeStyleStatus = Status.Fail;

    public static void Record(string name, Status status, string detail)
    {
        lock (Gate) { Results.Add(new PatternResult(name, status, detail)); }
        Console.WriteLine($"[PATTERN] {status.ToString().ToUpper(),-4} {name} :: {detail}");
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
// -> managed callback bridge is broken — a headline finding for the spike.
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

            // Read it back to confirm the mutation round-trips through ObjC.
            var found = style.LayerWithIdentifier("spike-circle");
            if (found != null)
            {
                TestState.RuntimeStyleStatus = Status.Pass;
                TestState.RuntimeStyleDetail =
                    "MLNShapeSource + MLNCircleStyleLayer added; CircleColor/Radius set via NSExpression; layer re-fetched OK";
            }
            else
            {
                TestState.RuntimeStyleStatus = Status.Fail;
                TestState.RuntimeStyleDetail = "AddLayer succeeded but layerWithIdentifier returned null";
            }
        }
        catch (Exception ex)
        {
            TestState.RuntimeStyleStatus = Status.Fail;
            TestState.RuntimeStyleDetail = $"{ex.GetType().Name}: {ex.Message}";
        }
    }

    public override void MapViewDidFinishLoadingMap(MLNMapView mapView)
    {
        TestState.DidFinishLoadingMap = true;
        Console.WriteLine("[DELEGATE] mapViewDidFinishLoadingMap: fired into C#");
    }

    public override void FullyRendered(MLNMapView mapView, bool fullyRendered)
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

        // Programmatic camera change → should drive region* delegate callbacks
        // (pattern c surrogate: the gesture/camera pipeline + delegate dispatch).
        _map.SetCenterCoordinate(new CLLocationCoordinate2D(48.8566, 2.3522), 3.0, true);

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

    private void FinishAndReport()
    {
        // Pattern (a): delegate callbacks fired into C#
        TestState.Record("delegate: mapView:didFinishLoadingStyle:",
            TestState.DidFinishLoadingStyle ? Status.Pass : Status.Fail,
            TestState.DidFinishLoadingStyle ? "ObjC -> C# override invoked" : "override never called");
        TestState.Record("delegate: mapViewDidFinishLoadingMap:",
            TestState.DidFinishLoadingMap ? Status.Pass : Status.Fail,
            TestState.DidFinishLoadingMap ? "ObjC -> C# override invoked" : "override never called");
        TestState.Record("delegate: mapViewDidFinishRenderingMap:fullyRendered:",
            TestState.DidFinishRenderingMapFully ? Status.Pass : Status.Fail,
            TestState.DidFinishRenderingMapFully ? "fullyRendered=true delivered" : "never delivered");
        TestState.Record("delegate: mapView:regionDidChangeAnimated: (camera pipeline)",
            TestState.RegionDidChange ? Status.Pass : Status.Fail,
            TestState.RegionDidChange ? "programmatic camera change drove region callback" : "no region callback");

        // Pattern (b): runtime source + layer + NSExpression mutation
        TestState.Record("runtime-style: MLNShapeSource + MLNCircleStyleLayer + NSExpression",
            TestState.RuntimeStyleStatus, TestState.RuntimeStyleDetail);

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
        lock (TestState.Gate)
        {
            foreach (var r in TestState.Results)
            {
                sb.AppendLine($"  [{r.Status.ToString().ToUpper(),-4}] {r.Name}");
                sb.AppendLine($"         {r.Detail}");
                switch (r.Status)
                {
                    case Status.Pass: pass++; break;
                    case Status.Fail: fail++; break;
                    default: skip++; break;
                }
            }
        }
        sb.AppendLine($"  ---- {pass} pass, {fail} fail, {skip} skip ----");
        sb.AppendLine("=====================================================");
        Console.WriteLine(sb.ToString());

        // The code-level smoke gate is the CORE interop proof: the map view
        // constructs AND the ObjC delegate callbacks actually fire back into C#
        // (didFinishLoadingStyle + didFinishLoadingMap). Any peripheral per-API break
        // is reported in the table above but does NOT gate the smoke pass — the
        // headline render proof is the screenshot, judged separately.
        bool smokePass = TestState.DidFinishLoadingStyle && TestState.DidFinishLoadingMap;
        Console.WriteLine($"[GATE] construct+delegate-callbacks smoke gate: {(smokePass ? "PASS" : "FAIL")} " +
                          $"(coverage: {pass} pass / {fail} fail / {skip} skip)");
        if (smokePass)
            Console.WriteLine("TEST SUCCESS");
        else
            Console.WriteLine("TEST FAILED: core interop gate (construct + delegate callbacks) did not pass");
    }
}

#endregion
