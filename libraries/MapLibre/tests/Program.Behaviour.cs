// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Text;
using CoreGraphics;
using CoreLocation;
using Foundation;
using UIKit;
using MapLibre;

namespace SwiftBindings.MapLibre.Tests;

// The map used to be pointed at https://demotiles.maplibre.org, which made every
// delegate/render assertion depend on a third party being reachable and fast — so the
// only honest verdict for "did it finish rendering?" was a Skip. This serves a tiny
// style and its GeoJSON source out of this same process over loopback, so rendering is
// deterministic and the render callback can be a hard requirement.
internal static class LocalStyleServer
{
    private static LoopbackHttpServer? _server;
    private static readonly object Gate = new();

    private const string PointsGeoJson = """
    {
      "type": "FeatureCollection",
      "features": [
        { "type": "Feature", "properties": { "name": "a" },
          "geometry": { "type": "Point", "coordinates": [2.3522, 48.8566] } },
        { "type": "Feature", "properties": { "name": "b" },
          "geometry": { "type": "Point", "coordinates": [-0.1278, 51.5074] } },
        { "type": "Feature", "properties": { "name": "c" },
          "geometry": { "type": "Point", "coordinates": [13.4050, 52.5200] } }
      ]
    }
    """;

    public static LoopbackHttpServer Server
    {
        get
        {
            lock (Gate)
            {
                if (_server is not null) return _server;

                var server = new LoopbackHttpServer();
                server.Map("/points.geojson", new LoopbackResponse
                {
                    ContentType = "application/geo+json",
                    Body = Encoding.UTF8.GetBytes(PointsGeoJson),
                    // Storable: an offline pack has to be allowed to keep it.
                    CacheControl = "max-age=3600",
                });

                // No sprite and no glyphs: the style has no symbol layers, so nothing
                // here reaches outside this process.
                string style = $$"""
                {
                  "version": 8,
                  "name": "loopback-test-style",
                  "sources": {
                    "pts": { "type": "geojson", "data": "{{server.Url("/points.geojson")}}" }
                  },
                  "layers": [
                    { "id": "bg", "type": "background",
                      "paint": { "background-color": "#1d3557" } },
                    { "id": "pts-circles", "type": "circle", "source": "pts",
                      "paint": { "circle-radius": 14, "circle-color": "#e63946" } }
                  ]
                }
                """;
                server.Map("/style.json", new LoopbackResponse
                {
                    ContentType = "application/json",
                    Body = Encoding.UTF8.GetBytes(style),
                    CacheControl = "max-age=3600",
                });

                _server = server;
                return _server;
            }
        }
    }

    public static string StyleUrl => Server.Url("/style.json");

    public static int StyleHits => Server.HitsFor("/style.json");
}

public partial class MapTestViewController
{
    // Rendering, snapshotting, offline download progress, delegate lifetime and
    // renderer teardown — the parts that were previously either fixed-delay guesses,
    // construction-only probes, or Skips.
    private async Task RunBehaviourTests()
    {
        StyleServedLocally();
        await SnapshotRendersPixels();
        await OfflinePackDownloadsFromLocalStyle();
        await DelegateSurvivesCollection();
        await SecondMapRendersAndTearsDown();
    }

    // The style really came from this process: if the map had silently fallen back to
    // a cached or remote style, none of the render assertions would mean anything.
    private void StyleServedLocally()
    {
        TestState.Probe("local style: map fetched its style from the in-process loopback server", () =>
        {
            int hits = LocalStyleServer.StyleHits;
            var url = _map!.StyleURL;
            bool ok = hits >= 1 && url?.AbsoluteString == LocalStyleServer.StyleUrl;
            return (ok ? Status.Pass : Status.Fail,
                $"styleURL='{url?.AbsoluteString}', loopback /style.json hits={hits}");
        });
    }

    // The snapshotter was previously only constructed. Start it and assert the image
    // it hands back is the requested size and is not a blank canvas.
    private async Task SnapshotRendersPixels()
    {
        try
        {
            var camera = MLNMapCamera.CameraLookingAtCenterCoordinateAltitudePitchHeading(
                new CLLocationCoordinate2D(50.0, 6.0), 3000000, 0, 0);
            var options = new MLNMapSnapshotOptions(
                new NSUrl(LocalStyleServer.StyleUrl), camera, new CGSize(120, 90))
            {
                ZoomLevel = 3,
                ShowsLogo = false,
                ShowsAttribution = false,
            };

            var snapshotter = new MLNMapSnapshotter(options);
            MLNMapSnapshot? snapshot = null;
            NSError? error = null;
            bool done = false;

            snapshotter.StartWithCompletionHandler((s, e) =>
            {
                snapshot = s;
                error = e;
                done = true;
            });

            bool arrived = await Await.UntilAsync(() => done, 45000);
            if (!arrived)
            {
                TestState.Record("snapshot: startWithCompletionHandler produced an image",
                    Status.Fail, "completion handler never fired within 45s");
                return;
            }
            if (snapshot is null)
            {
                TestState.Record("snapshot: startWithCompletionHandler produced an image",
                    Status.Fail, $"completion delivered no snapshot (error={error?.LocalizedDescription ?? "null"})");
                return;
            }

            var image = snapshot.Image;
            int w = (int)Math.Round((double)image.Size.Width);
            int h = (int)Math.Round((double)image.Size.Height);
            if (w != 120 || h != 90)
            {
                TestState.Record("snapshot: startWithCompletionHandler produced an image",
                    Status.Fail, $"snapshot is {w}x{h}, expected the requested 120x90");
                return;
            }

            // Non-blank: sample a grid and require both opaque pixels and more than one
            // distinct colour, so an all-transparent or flat-white image fails.
            var seen = new HashSet<(int, int, int)>();
            int opaque = 0;
            for (int x = 5; x < 120; x += 10)
            {
                for (int y = 5; y < 90; y += 10)
                {
                    var px = ImageFixtures.PixelAt(image, x, y);
                    if (px.A > 200) opaque++;
                    seen.Add((px.R, px.G, px.B));
                }
            }

            bool ok = opaque > 50 && seen.Count > 1;
            TestState.Record("snapshot: startWithCompletionHandler produced an image",
                ok ? Status.Pass : Status.Fail,
                $"{w}x{h} snapshot, {opaque} opaque samples, {seen.Count} distinct sampled colours");
        }
        catch (Exception ex)
        {
            TestState.Record("snapshot: startWithCompletionHandler produced an image",
                Status.Fail, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    // The existing offline probe creates and removes a pack without downloading
    // anything. With the style local, an actual download is cheap and bounded, so
    // assert the pack really moves resources — the block-plus-struct progress path.
    private async Task OfflinePackDownloadsFromLocalStyle()
    {
        const string ProbeName = "offline: pack resume actually downloads (progress advances)";
        MLNOfflinePack? created = null;
        try
        {
            var bounds = new MLNCoordinateBounds
            {
                Sw = new CLLocationCoordinate2D(48.80, 2.25),
                Ne = new CLLocationCoordinate2D(48.90, 2.45),
            };
            var region = new MLNTilePyramidOfflineRegion(new NSUrl(LocalStyleServer.StyleUrl), bounds, 4, 5);
            var context = NSData.FromString("progress-pack", NSStringEncoding.UTF8);
            var storage = MLNOfflineStorage.SharedOfflineStorage;

            NSError? addError = null;
            bool added = false;
            storage.AddPackForRegion(region, context, (pack, error) =>
            {
                created = pack;
                addError = error;
                added = true;
            });

            if (!await Await.UntilAsync(() => added, 20000))
            {
                TestState.Record(ProbeName, Status.Fail, "addPackForRegion completion never fired within 20s");
                return;
            }
            if (created is null)
            {
                TestState.Record(ProbeName, Status.Fail,
                    $"addPackForRegion produced no pack (error={addError?.LocalizedDescription ?? "null"})");
                return;
            }

            created.Resume();

            // Poll through requestProgress: the pack's progress is computed lazily, so
            // reading the struct without asking for an update can legitimately read 0.
            ulong completed = 0, expected = 0, bytes = 0;
            bool advanced = await Await.UntilAsync(() =>
            {
                created.RequestProgress();
                var p = created.Progress;
                completed = p.CountOfResourcesCompleted;
                expected = p.CountOfResourcesExpected;
                bytes = p.CountOfBytesCompleted;
                return completed > 0 && bytes > 0;
            }, 45000, 250);

            created.Suspend();

            TestState.Record(ProbeName, advanced ? Status.Pass : Status.Fail,
                advanced
                    ? $"pack downloaded {completed}/{expected} resources ({bytes} bytes) from the loopback style; " +
                      $"state={created.State}"
                    : $"progress never advanced within 45s: completed={completed}, expected={expected}, " +
                      $"bytes={bytes}, state={created.State}");
        }
        catch (Exception ex)
        {
            TestState.Record(ProbeName, Status.Fail, $"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            if (created is not null)
            {
                bool removed = false;
                try
                {
                    MLNOfflineStorage.SharedOfflineStorage.RemovePack(created, _ => removed = true);
                    await Await.UntilAsync(() => removed, 15000);
                }
                catch
                {
                    // Cleanup only; the probe above is what is being graded.
                }
            }
        }
    }

    // WeakDelegate does not retain, so the only thing keeping the managed delegate
    // alive is the field holding it. Force a collection and prove both that the
    // association still resolves to the same managed object and that ObjC still
    // dispatches into it afterwards.
    private async Task DelegateSurvivesCollection()
    {
        const string ProbeName = "lifetime: delegate survives GC and still receives callbacks";
        try
        {
            var before = _delegate;
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            var associated = _map!.WeakDelegate;
            if (!ReferenceEquals(associated, before))
            {
                TestState.Record(ProbeName, Status.Fail,
                    $"after GC the map's delegate is {(associated is null ? "null" : associated.GetType().Name)}, " +
                    "not the managed instance the field holds");
                return;
            }

            // Dispatch through it once more: a resurrected-but-dead peer would throw or
            // silently stop delivering here rather than at assignment time.
            TestState.RegionDidChange = false;
            _map.SetCenterCoordinate(new CLLocationCoordinate2D(41.9028, 12.4964), 4.0, false);
            bool redelivered = await Await.UntilAsync(() => TestState.RegionDidChange, 10000);

            TestState.Record(ProbeName, redelivered ? Status.Pass : Status.Fail,
                redelivered
                    ? "delegate identity preserved across a forced GC and regionDidChange still dispatched into it"
                    : "delegate identity preserved across a forced GC but regionDidChange stopped dispatching " +
                      "within 10s of a fresh camera change");
        }
        catch (Exception ex)
        {
            TestState.Record(ProbeName, Status.Fail, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    // Renderer teardown: a second, off-screen map that actually loads and renders the
    // local style, then is removed and disposed. The on-screen map is deliberately left
    // alone so the run's screenshot stays intact.
    private async Task SecondMapRendersAndTearsDown()
    {
        const string ProbeName = "teardown: a second rendering MLNMapView disposes cleanly";
        try
        {
            var host = new UIView(new CGRect(0, 0, 80, 60)) { Hidden = true };
            View!.AddSubview(host);

            var second = new MLNMapView(new CGRect(0, 0, 80, 60), new NSUrl(LocalStyleServer.StyleUrl));
            var secondDelegate = new RenderSignalDelegate();
            second.WeakDelegate = secondDelegate;
            host.AddSubview(second);
            second.SetCenterCoordinate(new CLLocationCoordinate2D(48.85, 2.35), 3.0, false);

            bool loaded = await Await.UntilAsync(() => secondDelegate.LoadedStyle && secondDelegate.LoadedMap, 45000);
            if (!loaded)
            {
                second.RemoveFromSuperview();
                second.Dispose();
                host.RemoveFromSuperview();
                TestState.Record(ProbeName, Status.Fail,
                    $"second map never finished loading within 45s (style={secondDelegate.LoadedStyle}, " +
                    $"map={secondDelegate.LoadedMap})");
                return;
            }

            second.RemoveFromSuperview();
            second.WeakDelegate = null;
            second.Dispose();
            host.RemoveFromSuperview();
            host.Dispose();

            // The renderer is gone; touching the disposed peer must be inert. Inert can
            // legitimately mean either of two things — a managed ObjectDisposedException,
            // or a dead handle whose reads come back as the zero value — and the binding
            // is free to pick either. What it must not do is crash the process or keep
            // answering with the live map's state: this map was left at zoom 3, so a 3
            // here would say Dispose never let go of the native peer.
            bool disposedThrew = false;
            double zoomAfterDispose = double.NaN;
            try
            {
                zoomAfterDispose = second.ZoomLevel;
            }
            catch (ObjectDisposedException)
            {
                disposedThrew = true;
            }

            bool disposedInert = disposedThrew || zoomAfterDispose == 0.0;

            // And the process must still be able to stand a renderer back up.
            using var third = new MLNMapView(new CGRect(0, 0, 10, 10), new NSUrl(LocalStyleServer.StyleUrl));
            bool recreated = third.StyleURL?.AbsoluteString == LocalStyleServer.StyleUrl;

            bool ok = disposedInert && recreated;
            TestState.Record(ProbeName, ok ? Status.Pass : Status.Fail,
                $"second map rendered then disposed; disposed access inert={disposedInert} " +
                $"(ObjectDisposedException={disposedThrew}, ZoomLevel read back {zoomAfterDispose}), " +
                $"a fresh MLNMapView came back up={recreated}");
        }
        catch (Exception ex)
        {
            TestState.Record(ProbeName, Status.Fail, $"{ex.GetType().Name}: {ex.Message}");
        }
    }
}

// Minimal delegate for the teardown probe: it only needs to know when the second map
// has actually stood a renderer up.
public sealed class RenderSignalDelegate : MLNMapViewDelegate
{
    public volatile bool LoadedStyle;
    public volatile bool LoadedMap;

    public override void DidFinishLoadingStyle(MLNMapView mapView, MLNStyle style) => LoadedStyle = true;

    public override void DidFinishLoadingMap(MLNMapView mapView) => LoadedMap = true;
}
