// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using CoreGraphics;
using Foundation;
using UIKit;
using Mappedin;
using Swift.Runtime;
using SwiftBuilderSlot = Swift.SwiftInOut<Swift.SwiftDictionary<Swift.SwiftString, Swift.Runtime.ExistentialContainer0>>;
using SwiftString = Swift.SwiftString;

namespace MappedinSimTests;

// Behavioural tests: a MapView that can be created, put on screen and torn down, a
// Coordinate that survives a JSON round trip, and the two closure routes that abort
// the process on today's binding.
public partial class MainViewController
{
    // The two closure-route probes tear the process down on today's binding. They run
    // on every lane regardless: an abort with no verdict line is how the harness
    // learns the route is broken, whereas hiding them behind an environment variable
    // no release path sets reports a green run over an open defect. They are ordered
    // last so every other result is already printed when the process goes down.
    private async Task RunBehaviourTests(TestLogger logger, TestResults results)
    {
        await MapViewCreateAttachDestroy(logger, results);
        CoordinateJsonRoundTrip(logger, results);
        await GetMapDataFailurePath(logger, results);
        BuilderMutationSurvives(logger, results);
    }

    // Create a MapView, put its web view on screen, and tear it down.
    private async Task MapViewCreateAttachDestroy(TestLogger logger, TestResults results)
    {
        MapView? mapView = null;
        try
        {
            mapView = new MapView();
            var webView = mapView.View;
            if (webView is null)
            {
                logger.Fail("MapView.View returned null");
                results.Fail("MapView_CreateAttach", "MapView.View was null");
                return;
            }

            webView.Frame = new CGRect(0, 0, 200, 200);
            View!.AddSubview(webView);
            webView.LayoutIfNeeded();

            // Give the web view a run-loop turn so its content process starts; a view
            // that cannot be laid out on screen would show up here rather than in a
            // later, unrelated test.
            await Task.Delay(500);

            bool attached = webView.Superview is not null && webView.Window is not null;
            var size = webView.Frame.Size;
            webView.RemoveFromSuperview();

            if (!attached)
            {
                logger.Fail("MapView.View never joined the window hierarchy");
                results.Fail("MapView_CreateAttach", "view not in a window");
            }
            else if ((int)size.Width != 200 || (int)size.Height != 200)
            {
                logger.Fail($"MapView.View laid out at {size.Width}x{size.Height}, expected 200x200");
                results.Fail("MapView_CreateAttach", $"{size.Width}x{size.Height}");
            }
            else
            {
                logger.Pass("MapView created, its WKWebView attached to the window at 200x200, and detached");
                results.Pass("MapView_CreateAttach");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"MapView create/attach: {ex.GetType().Name}: {ex.Message}");
            results.Fail("MapView_CreateAttach", $"{ex.GetType().Name}: {ex.Message}");
        }

        // Destroy also delivers through a Result closure, but unlike GetMapData's it
        // round-trips today, so it is safe to order ahead of the aborting probes.
        try
        {
            // The closure firing at all is only half the claim: Destroy reports its
            // outcome IN the Result, so a teardown that failed would invoke the
            // closure exactly the same way. Success is what this test asserts.
            bool destroyed = false;
            bool succeeded = false;
            string failureDetail = "";
            mapView!.Destroy(result =>
            {
                bool ok = result.IsSuccess;
                if (!ok)
                {
                    try { failureDetail = result.Failure.ToString() ?? "(failure carried no description)"; }
                    catch (Exception ex) { failureDetail = $"failure value unreadable: {ex.GetType().Name}: {ex.Message}"; }
                }
                Volatile.Write(ref succeeded, ok);
                // Written last, so a waiter that observes `destroyed` also sees the outcome.
                Volatile.Write(ref destroyed, true);
            });
            bool arrived = await Await.UntilAsync(() => Volatile.Read(ref destroyed), 15000);
            if (!arrived)
            {
                logger.Fail("MapView.Destroy never invoked its result closure within 15s");
                results.Fail("MapView_Destroy", "closure never fired");
            }
            else if (!Volatile.Read(ref succeeded))
            {
                logger.Fail($"MapView.Destroy delivered a failure result: {failureDetail}");
                results.Fail("MapView_Destroy", $"failure result: {failureDetail}");
            }
            else
            {
                logger.Pass("MapView.Destroy delivered a success result to its closure");
                results.Pass("MapView_Destroy");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"MapView.Destroy: {ex.GetType().Name}: {ex.Message}");
            results.Fail("MapView_Destroy", $"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            mapView?.Dispose();
        }
    }

    // Coordinate.ToJson/FromJson is the serialization boundary the SDK uses to talk to
    // its JS layer: every field has to survive the trip, including the optional floor
    // id and the vertical offset.
    private void CoordinateJsonRoundTrip(TestLogger logger, TestResults results)
    {
        try
        {
            var original = new Coordinate(43.861, -79.372, "floor-3", 1.5, "coord-under-test");
            var json = original.ToJson();
            if (json is null || json.Count == 0)
            {
                logger.Fail("Coordinate.ToJson() produced an empty dictionary");
                results.Fail("Coordinate_JsonRoundTrip", "empty json");
                return;
            }

            var restored = Coordinate.FromJson(json);
            if (restored is null)
            {
                logger.Fail($"Coordinate.FromJson() returned null for a dictionary this binding produced ({json.Count} keys)");
                results.Fail("Coordinate_JsonRoundTrip", "FromJson returned null");
                return;
            }

            bool lat = Math.Abs(restored.Latitude - 43.861) < 1e-9;
            bool lon = Math.Abs(restored.Longitude - (-79.372)) < 1e-9;
            bool floor = restored.FloorId == "floor-3";
            bool offset = Math.Abs(restored.VerticalOffset - 1.5) < 1e-9;

            if (lat && lon && floor && offset)
            {
                logger.Pass($"Coordinate survived a ToJson/FromJson round trip ({json.Count} keys): {restored.Latitude}, {restored.Longitude}, floor={restored.FloorId}, offset={restored.VerticalOffset}");
                results.Pass("Coordinate_JsonRoundTrip");
            }
            else
            {
                logger.Fail($"Coordinate round trip lost data: lat={restored.Latitude} lon={restored.Longitude} floor={restored.FloorId} offset={restored.VerticalOffset}");
                results.Fail("Coordinate_JsonRoundTrip", $"lat={restored.Latitude} lon={restored.Longitude} floor={restored.FloorId} offset={restored.VerticalOffset}");
            }

            // FromJson must reject a dictionary that is not a coordinate rather than
            // manufacturing one out of nothing.
            var bogus = Coordinate.FromJson(new Dictionary<string, object> { ["not"] = "a coordinate" });
            if (bogus is null)
            {
                logger.Pass("Coordinate.FromJson(unrelated dictionary) returned null");
                results.Pass("Coordinate_FromJson_Invalid");
            }
            else
            {
                logger.Fail($"Coordinate.FromJson(unrelated dictionary) produced {bogus.Latitude}, {bogus.Longitude}");
                results.Fail("Coordinate_FromJson_Invalid", "non-null for an unrelated dictionary");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"Coordinate JSON round trip: {ex.GetType().Name}: {ex.Message}");
            results.Fail("Coordinate_JsonRoundTrip", $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    // The builder hands a Swift dictionary slot to managed code, expects the slot's
    // final value to be written back, and returns that value. Mutating a projected
    // Dictionary<string, object> would lose Swift's inout semantics, so this uses the
    // generated SwiftInOut carrier and its raw Swift dictionary element types.
    private void BuilderMutationSurvives(TestLogger logger, TestResults results)
    {
        try
        {
            int blockCalls = 0;
            int seededCount = -1;
            int expectedCount = -1;
            Action<SwiftBuilderSlot> builder = slot =>
            {
                Interlocked.Increment(ref blockCalls);
                var dict = slot.Value;
                seededCount = dict.Count;

                using var floorKey = new SwiftString("initialFloorId");
                using var zoomKey = new SwiftString("zoomLevel");
                expectedCount = seededCount
                    + (dict.ContainsKey(floorKey) ? 0 : 1)
                    + (dict.ContainsKey(zoomKey) ? 0 : 1);
                dict[floorKey] = ExistentialContainer0.Box("floor-under-test");
                dict[zoomKey] = ExistentialContainer0.Box(18.5);

                // Publishing the final carrier through Value is the semantic part of an
                // inout callback: the generated trampoline closes the slot and writes this
                // Swift value back into Show3dMapOptions' storage.
                slot.Value = dict;
            };
            var options = MapView.Builders.Show3dMapOptions(builder);

            if (Volatile.Read(ref blockCalls) == 0)
            {
                logger.Fail("Show3dMapOptions never invoked the builder block");
                results.Fail("Builder_MutationSurvives", "block never invoked");
                return;
            }

            if (options is null || !options.TryGetValue("initialFloorId", out var floor) || floor as string != "floor-under-test")
            {
                logger.Fail($"Show3dMapOptions dropped the builder's mutation (keys: {(options is null ? "null" : string.Join(",", options.Keys))})");
                results.Fail("Builder_MutationSurvives", "mutation not present in the returned dictionary");
                return;
            }

            bool zoomOk = options.TryGetValue("zoomLevel", out var zoom)
                && Math.Abs(Convert.ToDouble(zoom) - 18.5) < 1e-9;

            if (zoomOk && options.Count == expectedCount)
            {
                logger.Pass($"Show3dMapOptions wrote the slot's two inserted values back to Swift (seeded={seededCount}, returned={options.Count})");
                results.Pass("Builder_MutationSurvives");
            }
            else
            {
                logger.Fail($"Show3dMapOptions did not preserve the complete slot writeback (seeded={seededCount}, expected={expectedCount}, returned={options.Count}, zoomOk={zoomOk})");
                results.Fail("Builder_MutationSurvives", "slot writeback was incomplete");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"Builders.Show3dMapOptions: {ex.GetType().Name}: {ex.Message}");
            results.Fail("Builder_MutationSurvives", $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    // The documented map-loading entry point, driven with deliberately invalid
    // credentials so no account is needed: the failure must be delivered to the
    // result closure. That delivery is exactly what aborts today, so it is ordered
    // near the end of the suite.
    private async Task GetMapDataFailurePath(TestLogger logger, TestResults results)
    {
        MapView? mapView = null;
        try
        {
            mapView = new MapView();
            var options = new GetMapDataWithCredentialsOptions(
                "invalid-key-for-tests", "invalid-secret-for-tests", "invalid-map-id-for-tests");

            bool delivered = false;
            bool wasFailure = false;
            mapView.GetMapData(options, result =>
            {
                delivered = true;
                wasFailure = result.IsFailure;
            });

            bool arrived = await Await.UntilAsync(() => delivered, 30000);
            if (!arrived)
            {
                logger.Fail("MapView.GetMapData never delivered a result for invalid credentials within 30s");
                results.Fail("MapView_GetMapData_InvalidCredentials", "no result delivered");
            }
            else if (!wasFailure)
            {
                logger.Fail("MapView.GetMapData reported success for deliberately invalid credentials");
                results.Fail("MapView_GetMapData_InvalidCredentials", "unexpected success");
            }
            else
            {
                logger.Pass("MapView.GetMapData delivered a failure result for invalid credentials");
                results.Pass("MapView_GetMapData_InvalidCredentials");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"MapView.GetMapData: {ex.GetType().Name}: {ex.Message}");
            results.Fail("MapView_GetMapData_InvalidCredentials", $"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            mapView?.Dispose();
        }
    }
}
