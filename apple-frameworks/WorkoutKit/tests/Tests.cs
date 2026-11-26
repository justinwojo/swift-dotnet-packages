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
    /// <summary>
    /// Checks a generated enum (or <c>CaseTag</c>) against the binding's contract: each member's value
    /// identifies its Swift case by name, not by position, so the named members must be defined, no
    /// member may equal <c>default</c> (which names no case), and no two members may share a value.
    /// </summary>
    private static void AssertNameKeyedMembers<T>(params T[] named) where T : struct, Enum
    {
        foreach (var member in named)
        {
            if (!Enum.IsDefined(member))
                throw new InvalidOperationException($"{typeof(T).Name} value {member} is not a defined member");
        }
        var seen = new HashSet<T>();
        foreach (var member in Enum.GetValues<T>())
        {
            if (EqualityComparer<T>.Default.Equals(member, default))
                throw new InvalidOperationException($"{typeof(T).Name}.{member} equals default, which names no case");
            if (!seen.Add(member))
                throw new InvalidOperationException($"{typeof(T).Name}.{member} shares its value with another member");
        }
    }

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

        // Plain enums exist with distinct, name-keyed members.
        try
        {
            AssertNameKeyedMembers(StateError.WatchNotPaired, StateError.WorkoutApplicationNotInstalled);
            Pass("StateError values");
        }
        catch (Exception ex)
        {
            Fail("StateError values", ex.Message);
        }

        try
        {
            AssertNameKeyedMembers(WorkoutAlertMetric.Current, WorkoutAlertMetric.Average);
            Pass("WorkoutAlertMetric values");
        }
        catch (Exception ex)
        {
            Fail("WorkoutAlertMetric values", ex.Message);
        }

        // IntervalStep.PurposeKind enum values
        try
        {
            AssertNameKeyedMembers(IntervalStep.PurposeKind.Work, IntervalStep.PurposeKind.Recovery);
            Pass("IntervalStep.PurposeKind values");
        }
        catch (Exception ex)
        {
            Fail("IntervalStep.PurposeKind values", ex.Message);
        }

        // WorkoutScheduler.AuthorizationStateKind enum values
        try
        {
            AssertNameKeyedMembers(
                WorkoutScheduler.AuthorizationStateKind.NotDetermined,
                WorkoutScheduler.AuthorizationStateKind.Restricted,
                WorkoutScheduler.AuthorizationStateKind.Denied,
                WorkoutScheduler.AuthorizationStateKind.Authorized);
            Pass("WorkoutScheduler.AuthorizationStateKind values");
        }
        catch (Exception ex)
        {
            Fail("WorkoutScheduler.AuthorizationStateKind values", ex.Message);
        }

        // WorkoutGoal.CaseTag enum values
        try
        {
            AssertNameKeyedMembers(WorkoutGoal.CaseTag.Distance, WorkoutGoal.CaseTag.Time, WorkoutGoal.CaseTag.Energy,
                WorkoutGoal.CaseTag.Open);
            Pass("WorkoutGoal.CaseTag values");
        }
        catch (Exception ex)
        {
            Fail("WorkoutGoal.CaseTag values", ex.Message);
        }

        // WorkoutPlan.WorkoutKind.CaseTag enum values
        try
        {
            AssertNameKeyedMembers(
                WorkoutPlan.WorkoutKind.CaseTag.Goal,
                WorkoutPlan.WorkoutKind.CaseTag.Custom,
                WorkoutPlan.WorkoutKind.CaseTag.Pacer,
                WorkoutPlan.WorkoutKind.CaseTag.SwimBikeRun);
            Pass("WorkoutPlan.WorkoutKind.CaseTag values");
        }
        catch (Exception ex)
        {
            Fail("WorkoutPlan.WorkoutKind.CaseTag values", ex.Message);
        }

        // SwimBikeRunWorkout.Activity.CaseTag enum values
        try
        {
            AssertNameKeyedMembers(SwimBikeRunWorkout.Activity.CaseTag.Swimming, SwimBikeRunWorkout.Activity.CaseTag.Cycling,
                SwimBikeRunWorkout.Activity.CaseTag.Running);
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

        // IntervalStep constructor with PurposeKind only
        try
        {
            var step = new IntervalStep(IntervalStep.PurposeKind.Work);
            if (step is null)
                throw new InvalidOperationException("IntervalStep(Work) returned null");
            if (step.Purpose != IntervalStep.PurposeKind.Work)
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
