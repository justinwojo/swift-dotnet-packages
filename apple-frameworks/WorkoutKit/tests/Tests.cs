// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Foundation;
using Swift;
using Swift.Foundation;
using Swift.Runtime;
using WorkoutKit;

namespace SwiftBindings.WorkoutKit.Tests;

internal static class Tests
{
    public static int Run()
    {
        int passed = 0, failed = 0;

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

        // IntervalStep.PurposeType enum values
        try
        {
            if ((int)IntervalStep.PurposeType.Work != 0 ||
                (int)IntervalStep.PurposeType.Recovery != 1)
                throw new InvalidOperationException("IntervalStep.PurposeType values mismatch");
            Pass("IntervalStep.PurposeType values");
        }
        catch (Exception ex)
        {
            Fail("IntervalStep.PurposeType values", ex.Message);
        }

        // WorkoutScheduler.AuthorizationStateType enum values
        try
        {
            if ((long)WorkoutScheduler.AuthorizationStateType.NotDetermined != 0 ||
                (long)WorkoutScheduler.AuthorizationStateType.Restricted != 1 ||
                (long)WorkoutScheduler.AuthorizationStateType.Denied != 2 ||
                (long)WorkoutScheduler.AuthorizationStateType.Authorized != 3)
                throw new InvalidOperationException("WorkoutScheduler.AuthorizationStateType values mismatch");
            Pass("WorkoutScheduler.AuthorizationStateType values");
        }
        catch (Exception ex)
        {
            Fail("WorkoutScheduler.AuthorizationStateType values", ex.Message);
        }

        // WorkoutGoal.CaseTag enum values
        try
        {
            if ((uint)WorkoutGoal.CaseTag.Distance != 0u ||
                (uint)WorkoutGoal.CaseTag.Time != 1u ||
                (uint)WorkoutGoal.CaseTag.Energy != 2u ||
                (uint)WorkoutGoal.CaseTag.Open != 4u)
                throw new InvalidOperationException("WorkoutGoal.CaseTag values mismatch");
            Pass("WorkoutGoal.CaseTag values");
        }
        catch (Exception ex)
        {
            Fail("WorkoutGoal.CaseTag values", ex.Message);
        }

        // WorkoutPlan.WorkoutType.CaseTag enum values
        try
        {
            if ((uint)WorkoutPlan.WorkoutType.CaseTag.Goal != 0u ||
                (uint)WorkoutPlan.WorkoutType.CaseTag.Custom != 1u ||
                (uint)WorkoutPlan.WorkoutType.CaseTag.Pacer != 2u ||
                (uint)WorkoutPlan.WorkoutType.CaseTag.SwimBikeRun != 3u)
                throw new InvalidOperationException("WorkoutPlan.WorkoutType.CaseTag values mismatch");
            Pass("WorkoutPlan.WorkoutType.CaseTag values");
        }
        catch (Exception ex)
        {
            Fail("WorkoutPlan.WorkoutType.CaseTag values", ex.Message);
        }

        // SwimBikeRunWorkout.Activity.CaseTag enum values
        try
        {
            if ((uint)SwimBikeRunWorkout.Activity.CaseTag.Swimming != 0u ||
                (uint)SwimBikeRunWorkout.Activity.CaseTag.Cycling != 1u ||
                (uint)SwimBikeRunWorkout.Activity.CaseTag.Running != 2u)
                throw new InvalidOperationException("SwimBikeRunWorkout.Activity.CaseTag values mismatch");
            Pass("SwimBikeRunWorkout.Activity.CaseTag values");
        }
        catch (Exception ex)
        {
            Fail("SwimBikeRunWorkout.Activity.CaseTag values", ex.Message);
        }

        // WorkoutGoal.Open singleton — no-payload case, cached lazy
        try
        {
            var open = WorkoutGoal.Open;
            if (open is null)
                throw new InvalidOperationException("WorkoutGoal.Open is null");
            if (open.Tag != WorkoutGoal.CaseTag.Open)
                throw new InvalidOperationException($"WorkoutGoal.Open tag wrong: {open.Tag}");
            Pass("WorkoutGoal.Open singleton tag");
        }
        catch (Exception ex)
        {
            Fail("WorkoutGoal.Open singleton tag", ex.Message);
        }

        // WorkoutScheduler.Shared singleton
        try
        {
            var scheduler = WorkoutScheduler.Shared;
            if (scheduler is null)
                throw new InvalidOperationException("WorkoutScheduler.Shared is null");
            Pass("WorkoutScheduler.Shared singleton");
        }
        catch (Exception ex)
        {
            Fail("WorkoutScheduler.Shared singleton", ex.Message);
        }

        // WorkoutScheduler.MaxAllowedScheduledWorkoutCount static accessor
        try
        {
            var count = WorkoutScheduler.MaxAllowedScheduledWorkoutCount;
            // Just verifies no crash; value is typically positive on supported platforms
            Pass("WorkoutScheduler.MaxAllowedScheduledWorkoutCount");
        }
        catch (Exception ex)
        {
            Fail("WorkoutScheduler.MaxAllowedScheduledWorkoutCount", ex.Message);
        }

        // WorkoutScheduler.IsSupported static accessor
        try
        {
            var supported = WorkoutScheduler.IsSupported;
            // Just verifies no crash; value is platform-dependent
            Pass("WorkoutScheduler.IsSupported");
        }
        catch (Exception ex)
        {
            Fail("WorkoutScheduler.IsSupported", ex.Message);
        }

        // WorkoutStep default constructor
        try
        {
            var step = new WorkoutStep();
            if (step is null)
                throw new InvalidOperationException("WorkoutStep() returned null");
            Pass("WorkoutStep default constructor");
        }
        catch (Exception ex)
        {
            Fail("WorkoutStep default constructor", ex.Message);
        }

        // IntervalStep constructor with PurposeType only
        try
        {
            var step = new IntervalStep(IntervalStep.PurposeType.Work);
            if (step is null)
                throw new InvalidOperationException("IntervalStep(Work) returned null");
            if (step.Purpose != IntervalStep.PurposeType.Work)
                throw new InvalidOperationException($"Purpose mismatch: {step.Purpose}");
            Pass("IntervalStep constructor + Purpose roundtrip");
        }
        catch (Exception ex)
        {
            Fail("IntervalStep constructor + Purpose roundtrip", ex.Message);
        }

        // IntervalBlock default constructor
        try
        {
            var block = new IntervalBlock();
            if (block is null)
                throw new InvalidOperationException("IntervalBlock() returned null");
            Pass("IntervalBlock default constructor");
        }
        catch (Exception ex)
        {
            Fail("IntervalBlock default constructor", ex.Message);
        }

        // Range-alert construction (HeartRate/Cadence/Power/Speed RangeAlert).
        //
        // The end-to-end path is: public `Measurement<T>(value, unit)` (Swift.Runtime
        // Foundation/Measurement.cs, routed through the SBW_Measurement_InitFromValueUnit
        // @_cdecl shim that builds Foundation.Measurement(value:unit:) and ARC-retains the
        // NSUnit reference) → `SwiftClosedRange<Bound>(lower, upper)` (Comparable witness
        // table on Measurement registered in its cctor) → range-alert ctor. Disposes are
        // safe to interleave because both Measurement and SwiftClosedRange copy bounds
        // into their own buffers via VWT InitializeWithCopy.
        try
        {
            using var lo = new Measurement<NSUnitFrequency>(60.0, NSUnitFrequency.Hertz);
            using var hi = new Measurement<NSUnitFrequency>(150.0, NSUnitFrequency.Hertz);
            using var range = new SwiftClosedRange<Measurement<NSUnitFrequency>>(lo, hi);
            using var alert = new HeartRateRangeAlert(range);
            if (alert is null)
                throw new InvalidOperationException("ctor returned null");
            Pass("HeartRateRangeAlert construction");
        }
        catch (Exception ex)
        {
            Fail("HeartRateRangeAlert construction", ex.Message);
        }

        try
        {
            using var lo = new Measurement<NSUnitFrequency>(80.0, NSUnitFrequency.Hertz);
            using var hi = new Measurement<NSUnitFrequency>(95.0, NSUnitFrequency.Hertz);
            using var range = new SwiftClosedRange<Measurement<NSUnitFrequency>>(lo, hi);
            using var alert = new CadenceRangeAlert(range);
            if (alert is null)
                throw new InvalidOperationException("ctor returned null");
            Pass("CadenceRangeAlert construction");
        }
        catch (Exception ex)
        {
            Fail("CadenceRangeAlert construction", ex.Message);
        }

        try
        {
            using var lo = new Measurement<NSUnitPower>(150.0, NSUnitPower.Watts);
            using var hi = new Measurement<NSUnitPower>(250.0, NSUnitPower.Watts);
            using var range = new SwiftClosedRange<Measurement<NSUnitPower>>(lo, hi);
            using var alert = new PowerRangeAlert(range);
            if (alert is null)
                throw new InvalidOperationException("ctor returned null");
            Pass("PowerRangeAlert construction");
        }
        catch (Exception ex)
        {
            Fail("PowerRangeAlert construction", ex.Message);
        }

        try
        {
            using var lo = new Measurement<NSUnitSpeed>(3.0, NSUnitSpeed.MetersPerSecond);
            using var hi = new Measurement<NSUnitSpeed>(5.0, NSUnitSpeed.MetersPerSecond);
            using var range = new SwiftClosedRange<Measurement<NSUnitSpeed>>(lo, hi);
            using var alert = new SpeedRangeAlert(range, WorkoutAlertMetric.Current);
            if (alert is null)
                throw new InvalidOperationException("ctor returned null");
            Pass("SpeedRangeAlert construction");
        }
        catch (Exception ex)
        {
            Fail("SpeedRangeAlert construction", ex.Message);
        }

        // Summary
        Log($"Results: {passed} passed, {failed} failed");
        if (failed == 0)
            Log("TEST SUCCESS", prefixed: false);
        else
            Log($"TEST FAILED: {failed} failures", prefixed: false);
        return failed;
    }

    internal static void Log(string msg, bool prefixed = true) =>
        Console.WriteLine(prefixed ? $"[WORKOUTKIT-TEST] {msg}" : msg);
}
