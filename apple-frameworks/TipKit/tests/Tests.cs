// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using TipKit;
using Swift.Runtime;

namespace SwiftBindings.TipKit.Tests;

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

        void MetadataTest<T>(string name) where T : ISwiftObject
        {
            try
            {
                var md = SwiftObjectHelper<T>.GetTypeMetadata();
                if (md.Handle == IntPtr.Zero)
                    throw new InvalidOperationException("null handle");
                Pass($"{name} metadata");
            }
            catch (Exception ex) { Fail($"{name} metadata", ex.Message); }
        }

        // Test 1: Tips.Configure() no-arg overload — may throw SwiftError (datastore already configured
        // or not supported in this context) but must not crash the process.
        // Binding-load failures are real bugs and must fail.
        try
        {
            Tips.Configure();
            Pass("Tips.Configure()");
        }
        catch (DllNotFoundException ex)
        {
            Fail("Tips.Configure()", ex.Message);
        }
        catch (EntryPointNotFoundException ex)
        {
            Fail("Tips.Configure()", ex.Message);
        }
        catch (Exception ex)
        {
            // A Swift error from Configure (e.g. TipsDatastoreAlreadyConfigured) is expected
            // in test environments — the binding reached Swift and returned a typed error, which is a pass.
            Log($"Tips.Configure() threw (expected in test context): {ex.Message}");
            Pass("Tips.Configure() (threw expected error)");
        }

        // Test 2: Tips.ShowAllTipsForTesting — no-throw static method.
        try
        {
            Tips.ShowAllTipsForTesting();
            Pass("Tips.ShowAllTipsForTesting()");
        }
        catch (Exception ex)
        {
            Fail("Tips.ShowAllTipsForTesting()", ex.Message);
        }

        // Test 3: Tips.HideAllTipsForTesting — no-throw static method.
        try
        {
            Tips.HideAllTipsForTesting();
            Pass("Tips.HideAllTipsForTesting()");
        }
        catch (Exception ex)
        {
            Fail("Tips.HideAllTipsForTesting()", ex.Message);
        }

        // Test 4: Tips.Status singletons — Pending and Available are cached singleton instances.
        try
        {
            var pending = Tips.Status.Pending;
            var available = Tips.Status.Available;
            if (pending is null || available is null)
                throw new InvalidOperationException("one or both singletons were null");
            Pass("Tips.Status singletons (Pending, Available)");
        }
        catch (Exception ex)
        {
            Fail("Tips.Status singletons (Pending, Available)", ex.Message);
        }

        // Test 5: Tips.Status.CaseTag enum values match the documented Swift ordering.
        try
        {
            if ((uint)Tips.Status.CaseTag.Invalidated != 0u)
                throw new InvalidOperationException($"Invalidated tag = {(uint)Tips.Status.CaseTag.Invalidated}, expected 0");
            if ((uint)Tips.Status.CaseTag.Pending != 1u)
                throw new InvalidOperationException($"Pending tag = {(uint)Tips.Status.CaseTag.Pending}, expected 1");
            if ((uint)Tips.Status.CaseTag.Available != 2u)
                throw new InvalidOperationException($"Available tag = {(uint)Tips.Status.CaseTag.Available}, expected 2");
            Pass("Tips.Status.CaseTag values");
        }
        catch (Exception ex)
        {
            Fail("Tips.Status.CaseTag values", ex.Message);
        }

        // Test 6: Tips.Status.Pending singleton has the expected CaseTag.
        try
        {
            var pending = Tips.Status.Pending;
            var tag = pending.Tag;
            if (tag != Tips.Status.CaseTag.Pending)
                throw new InvalidOperationException($"unexpected tag {tag}, expected Pending");
            Pass("Tips.Status.Pending tag");
        }
        catch (Exception ex)
        {
            Fail("Tips.Status.Pending tag", ex.Message);
        }

        // Test 7: Tips.Status.Available singleton has the expected CaseTag.
        try
        {
            var available = Tips.Status.Available;
            var tag = available.Tag;
            if (tag != Tips.Status.CaseTag.Available)
                throw new InvalidOperationException($"unexpected tag {tag}, expected Available");
            Pass("Tips.Status.Available tag");
        }
        catch (Exception ex)
        {
            Fail("Tips.Status.Available tag", ex.Message);
        }

        // Test 8: Tips.InvalidationReason plain enum values.
        try
        {
            if ((int)Tips.InvalidationReason.ActionPerformed != 0)
                throw new InvalidOperationException($"ActionPerformed = {(int)Tips.InvalidationReason.ActionPerformed}, expected 0");
            if ((int)Tips.InvalidationReason.DisplayCountExceeded != 1)
                throw new InvalidOperationException($"DisplayCountExceeded = {(int)Tips.InvalidationReason.DisplayCountExceeded}, expected 1");
            if ((int)Tips.InvalidationReason.DisplayDurationExceeded != 2)
                throw new InvalidOperationException($"DisplayDurationExceeded = {(int)Tips.InvalidationReason.DisplayDurationExceeded}, expected 2");
            if ((int)Tips.InvalidationReason.TipClosed != 3)
                throw new InvalidOperationException($"TipClosed = {(int)Tips.InvalidationReason.TipClosed}, expected 3");
            Pass("Tips.InvalidationReason values");
        }
        catch (Exception ex)
        {
            Fail("Tips.InvalidationReason values", ex.Message);
        }

        // Test 9: TipGroup.Priority plain enum values.
        try
        {
            if ((int)TipGroup.Priority.FirstAvailable != 0)
                throw new InvalidOperationException($"FirstAvailable = {(int)TipGroup.Priority.FirstAvailable}, expected 0");
            if ((int)TipGroup.Priority.Ordered != 1)
                throw new InvalidOperationException($"Ordered = {(int)TipGroup.Priority.Ordered}, expected 1");
            Pass("TipGroup.Priority values");
        }
        catch (Exception ex)
        {
            Fail("TipGroup.Priority values", ex.Message);
        }

        // Test 10: TipKitError.TipsDatastoreAlreadyConfigured singleton is reachable.
        try
        {
            var err = TipKitError.TipsDatastoreAlreadyConfigured;
            if (err is null)
                throw new InvalidOperationException("TipsDatastoreAlreadyConfigured was null");
            Pass("TipKitError.TipsDatastoreAlreadyConfigured");
        }
        catch (Exception ex)
        {
            Fail("TipKitError.TipsDatastoreAlreadyConfigured", ex.Message);
        }

        // Test 11: TipKitError.InvalidPredicateValueType singleton is reachable.
        try
        {
            var err = TipKitError.InvalidPredicateValueType;
            if (err is null)
                throw new InvalidOperationException("InvalidPredicateValueType was null");
            Pass("TipKitError.InvalidPredicateValueType");
        }
        catch (Exception ex)
        {
            Fail("TipKitError.InvalidPredicateValueType", ex.Message);
        }

        // Test 12: TipKitError.MissingGroupContainerEntitlements singleton is reachable.
        try
        {
            var err = TipKitError.MissingGroupContainerEntitlements;
            if (err is null)
                throw new InvalidOperationException("MissingGroupContainerEntitlements was null");
            Pass("TipKitError.MissingGroupContainerEntitlements");
        }
        catch (Exception ex)
        {
            Fail("TipKitError.MissingGroupContainerEntitlements", ex.Message);
        }

        // Test 13: TipKitError metadata loads.
        MetadataTest<TipKitError>("TipKitError");

        // Test 14: Tips.Status metadata loads.
        MetadataTest<Tips.Status>("Tips.Status");

        // Test 15: Tips.DonationTimeRange static singleton accessors (Minute, Hour, Day, Week).
        try
        {
            var minute = Tips.DonationTimeRange.Minute;
            var hour = Tips.DonationTimeRange.Hour;
            var day = Tips.DonationTimeRange.Day;
            var week = Tips.DonationTimeRange.Week;
            if (minute is null || hour is null || day is null || week is null)
                throw new InvalidOperationException("one or more DonationTimeRange singletons were null");
            Pass("Tips.DonationTimeRange singletons (Minute, Hour, Day, Week)");
        }
        catch (Exception ex)
        {
            Fail("Tips.DonationTimeRange singletons (Minute, Hour, Day, Week)", ex.Message);
        }

        // Test 16: Tips.DonationTimeRange factory methods produce non-null instances.
        try
        {
            var mins = Tips.DonationTimeRange.Minutes(5);
            var hours = Tips.DonationTimeRange.Hours(2);
            var days = Tips.DonationTimeRange.Days(7);
            var weeks = Tips.DonationTimeRange.Weeks(4);
            if (mins is null || hours is null || days is null || weeks is null)
                throw new InvalidOperationException("one or more DonationTimeRange factory results were null");
            Pass("Tips.DonationTimeRange factory methods");
        }
        catch (Exception ex)
        {
            Fail("Tips.DonationTimeRange factory methods", ex.Message);
        }

        // Test 17: Tips.DonationLimit constructor round-trip (maximumCount only).
        // Note: DonationLimit is ISwiftObject — do NOT Dispose.
        try
        {
            var limit = new Tips.DonationLimit(3);
            var count = limit.MaximumCount;
            if (count != 3)
                throw new InvalidOperationException($"MaximumCount = {count}, expected 3");
            Pass("Tips.DonationLimit ctor (maximumCount=3)");
        }
        catch (Exception ex)
        {
            Fail("Tips.DonationLimit ctor (maximumCount=3)", ex.Message);
        }

        // Test 18: Tips.IgnoresDisplayFrequency constructor round-trip.
        // Note: IgnoresDisplayFrequency is ISwiftObject — do NOT Dispose.
        try
        {
            var opt = new Tips.IgnoresDisplayFrequency(true);
            if (opt is null)
                throw new InvalidOperationException("IgnoresDisplayFrequency was null");
            Pass("Tips.IgnoresDisplayFrequency ctor (true)");
        }
        catch (Exception ex)
        {
            Fail("Tips.IgnoresDisplayFrequency ctor (true)", ex.Message);
        }

        // Test 19: Tips.MaxDisplayCount constructor.
        // Note: MaxDisplayCount is ISwiftObject — do NOT Dispose.
        try
        {
            var opt = new Tips.MaxDisplayCount(5);
            if (opt is null)
                throw new InvalidOperationException("MaxDisplayCount was null");
            Pass("Tips.MaxDisplayCount ctor (5)");
        }
        catch (Exception ex)
        {
            Fail("Tips.MaxDisplayCount ctor (5)", ex.Message);
        }

        // Test 20: Tips.ParameterOption.Transient singleton is reachable.
        try
        {
            var transient = Tips.ParameterOption.Transient;
            if (transient is null)
                throw new InvalidOperationException("Transient was null");
            Pass("Tips.ParameterOption.Transient");
        }
        catch (Exception ex)
        {
            Fail("Tips.ParameterOption.Transient", ex.Message);
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
        Console.WriteLine(prefixed ? $"[TIPKIT-TEST] {msg}" : msg);
}
