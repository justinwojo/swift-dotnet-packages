// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Foundation;
using UIKit;
using WorkoutKit;
using Swift.Runtime;

namespace WorkoutKitSimTests;

public class Application
{
    static void Main(string[] args)
    {
        UIApplication.Main(args, null, typeof(AppDelegate));
    }
}

[Register("AppDelegate")]
public class AppDelegate : UIApplicationDelegate
{
    public override UIWindow? Window { get; set; }

    public override bool FinishedLaunching(UIApplication application, NSDictionary? launchOptions)
    {
        Window = new UIWindow(UIScreen.MainScreen.Bounds);
        Window.RootViewController = new MainViewController();
        Window.MakeKeyAndVisible();
        return true;
    }
}

public class MainViewController : UIViewController
{
    public override void ViewDidLoad()
    {
        base.ViewDidLoad();
        View!.BackgroundColor = UIColor.White;

        int passed = 0, failed = 0;

        void Pass(string name)
        {
            passed++;
            Console.WriteLine($"[WORKOUTKIT-TEST] PASS: {name}");
        }

        void Fail(string name, string error)
        {
            failed++;
            Console.WriteLine($"[WORKOUTKIT-TEST] FAIL: {name} — {error}");
        }

        void MetadataTest<T>(string name) where T : class, ISwiftObject
        {
            try
            {
                var metadata = SwiftObjectHelper<T>.GetTypeMetadata();
                if (metadata.Handle == IntPtr.Zero)
                    throw new InvalidOperationException("metadata handle is null");
                Pass(name);
            }
            catch (Exception ex)
            {
                Fail(name, ex.Message);
            }
        }

        // Metadata loads for the core struct wrappers exercise type metadata
        // symbol resolution, payload size queries, and the ModuleInitializer path.
        MetadataTest<HeartRateRangeAlert>("HeartRateRangeAlert metadata");
        MetadataTest<HeartRateZoneAlert>("HeartRateZoneAlert metadata");
        MetadataTest<IntervalStep>("IntervalStep metadata");
        MetadataTest<CustomWorkout>("CustomWorkout metadata");
        MetadataTest<CadenceRangeAlert>("CadenceRangeAlert metadata");
        MetadataTest<PacerWorkout>("PacerWorkout metadata");
        MetadataTest<SingleGoalWorkout>("SingleGoalWorkout metadata");
        MetadataTest<WorkoutPlan>("WorkoutPlan metadata");
        MetadataTest<IntervalBlock>("IntervalBlock metadata");
        MetadataTest<SwimBikeRunWorkout>("SwimBikeRunWorkout metadata");
        MetadataTest<ScheduledWorkoutPlan>("ScheduledWorkoutPlan metadata");

        // Plain enums exist with documented Swift values.
        try
        {
            if ((int)StateError.WatchNotPaired != 0 ||
                (int)StateError.WorkoutApplicationNotInstalled != 1)
                throw new InvalidOperationException("StateError values mismatch");
            Pass("StateError values");
        }
        catch (Exception ex)
        {
            Fail("StateError values", ex.Message);
        }

        try
        {
            if ((int)WorkoutAlertMetric.Current != 0 ||
                (int)WorkoutAlertMetric.Average != 1)
                throw new InvalidOperationException("WorkoutAlertMetric values mismatch");
            Pass("WorkoutAlertMetric values");
        }
        catch (Exception ex)
        {
            Fail("WorkoutAlertMetric values", ex.Message);
        }

        Console.WriteLine($"[WORKOUTKIT-TEST] Results: {passed} passed, {failed} failed");
        if (failed == 0)
            Console.WriteLine("TEST SUCCESS");
        else
            Console.WriteLine($"TEST FAILED: {failed} failures");
    }
}
