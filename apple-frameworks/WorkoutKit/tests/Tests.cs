// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using WorkoutKit;
using Swift.Runtime;

namespace SwiftBindings.WorkoutKit.Tests;

internal static class Tests
{
    public static int Run()
    {
        int passed = 0, failed = 0, skipped = 0;

        void Pass(string name)
        {
            passed++;
            Log($"PASS: {name}");
        }

        void Fail(string name, string error)
        {
            failed++;
            Log($"FAIL: {name} — {error}");
        }

        void Skip(string name, string reason)
        {
            skipped++;
            Log($"SKIP: {name} — {reason}");
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

        // Summary
        Log($"Results: {passed} passed, {failed} failed, {skipped} skipped");
        if (failed == 0)
            Log("TEST SUCCESS", prefixed: false);
        else
            Log($"TEST FAILED: {failed} failures", prefixed: false);
        return failed;
    }

    internal static void Log(string msg, bool prefixed = true) =>
        Console.WriteLine(prefixed ? $"[WORKOUTKIT-TEST] {msg}" : msg);
}
