// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using FamilyControls;
using Swift.Runtime;

namespace SwiftBindings.FamilyControls.Tests;

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

        // Local helper: verify metadata handle is non-zero for a given ISwiftObject type.
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

        // Test 1: AuthorizationCenter.Shared singleton is non-null.
        try
        {
            var center = AuthorizationCenter.Shared;
            if (center is null)
                throw new InvalidOperationException("AuthorizationCenter.Shared was null");
            Pass("AuthorizationCenter.Shared");
        }
        catch (Exception ex)
        {
            Fail("AuthorizationCenter.Shared", ex.Message);
        }

        // Test 2: AuthorizationStatus singleton cases are non-null and distinct.
        try
        {
            var notDetermined = AuthorizationStatus.NotDetermined;
            var denied = AuthorizationStatus.Denied;
            var approved = AuthorizationStatus.Approved;
            if (notDetermined is null || denied is null || approved is null)
                throw new InvalidOperationException("one of the singletons was null");
            Pass("AuthorizationStatus singletons");
        }
        catch (Exception ex)
        {
            Fail("AuthorizationStatus singletons", ex.Message);
        }

        // Test 3: AuthorizationStatus.CaseTag uint values match Swift ordering.
        try
        {
            if ((uint)AuthorizationStatus.CaseTag.NotDetermined != 0u)
                throw new InvalidOperationException($"NotDetermined expected 0, got {(uint)AuthorizationStatus.CaseTag.NotDetermined}");
            if ((uint)AuthorizationStatus.CaseTag.Denied != 1u)
                throw new InvalidOperationException($"Denied expected 1, got {(uint)AuthorizationStatus.CaseTag.Denied}");
            if ((uint)AuthorizationStatus.CaseTag.Approved != 2u)
                throw new InvalidOperationException($"Approved expected 2, got {(uint)AuthorizationStatus.CaseTag.Approved}");
            Pass("AuthorizationStatus.CaseTag values");
        }
        catch (Exception ex)
        {
            Fail("AuthorizationStatus.CaseTag values", ex.Message);
        }

        // Test 4: AuthorizationStatus.NotDetermined.Tag round-trip.
        try
        {
            var notDetermined = AuthorizationStatus.NotDetermined;
            if (notDetermined.Tag != AuthorizationStatus.CaseTag.NotDetermined)
                throw new InvalidOperationException($"Tag mismatch: expected NotDetermined, got {notDetermined.Tag}");
            Pass("AuthorizationStatus.NotDetermined.Tag round-trip");
        }
        catch (Exception ex)
        {
            Fail("AuthorizationStatus.NotDetermined.Tag round-trip", ex.Message);
        }

        // Test 5: FamilyControlsError plain enum — verify expected integer values.
        try
        {
            if ((int)FamilyControlsError.Restricted != 0)
                throw new InvalidOperationException($"Restricted expected 0, got {(int)FamilyControlsError.Restricted}");
            if ((int)FamilyControlsError.Unavailable != 1)
                throw new InvalidOperationException($"Unavailable expected 1, got {(int)FamilyControlsError.Unavailable}");
            if ((int)FamilyControlsError.AuthenticationMethodUnavailable != 7)
                throw new InvalidOperationException($"AuthenticationMethodUnavailable expected 7, got {(int)FamilyControlsError.AuthenticationMethodUnavailable}");
            Pass("FamilyControlsError values");
        }
        catch (Exception ex)
        {
            Fail("FamilyControlsError values", ex.Message);
        }

        // Test 6: FamilyControlsError.GetErrorDescription extension method round-trip.
        try
        {
            var desc = FamilyControlsError.Restricted.GetErrorDescription();
            // desc may be null on some OS versions — just verify the call didn't crash.
            Log($"FamilyControlsError.Restricted description = {desc ?? "<null>"}");
            Pass("FamilyControlsError.GetErrorDescription");
        }
        catch (Exception ex)
        {
            Fail("FamilyControlsError.GetErrorDescription", ex.Message);
        }

        // Test 7: FamilyControlsMember plain enum — verify Child=0 and Individual=1.
        try
        {
            if ((long)FamilyControlsMember.Child != 0)
                throw new InvalidOperationException($"Child expected 0, got {(long)FamilyControlsMember.Child}");
            if ((long)FamilyControlsMember.Individual != 1)
                throw new InvalidOperationException($"Individual expected 1, got {(long)FamilyControlsMember.Individual}");
            Pass("FamilyControlsMember values");
        }
        catch (Exception ex)
        {
            Fail("FamilyControlsMember values", ex.Message);
        }

        // Test 8: FamilyControlsMember.GetDescription extension method round-trip.
        try
        {
            var desc = FamilyControlsMember.Child.GetDescription();
            if (string.IsNullOrEmpty(desc))
                throw new InvalidOperationException("empty description");
            Log($"FamilyControlsMember.Child description = {desc}");
            Pass("FamilyControlsMember.GetDescription");
        }
        catch (Exception ex)
        {
            Fail("FamilyControlsMember.GetDescription", ex.Message);
        }

        // Test 9: FamilyActivitySelection no-arg constructor.
        try
        {
            var selection = new FamilyActivitySelection();
            if (selection is null)
                throw new InvalidOperationException("FamilyActivitySelection() returned null");
            Pass("FamilyActivitySelection no-arg ctor");
        }
        catch (Exception ex)
        {
            Fail("FamilyActivitySelection no-arg ctor", ex.Message);
        }

        // Test 10: FamilyActivitySelection bool constructor and IncludeEntireCategory property.
        try
        {
            var selectionTrue = new FamilyActivitySelection(includeEntireCategory: true);
            var selectionFalse = new FamilyActivitySelection(includeEntireCategory: false);
            bool valTrue = selectionTrue.IncludeEntireCategory;
            bool valFalse = selectionFalse.IncludeEntireCategory;
            if (!valTrue)
                throw new InvalidOperationException($"IncludeEntireCategory expected true, got {valTrue}");
            if (valFalse)
                throw new InvalidOperationException($"IncludeEntireCategory expected false, got {valFalse}");
            Pass("FamilyActivitySelection(includeEntireCategory) + IncludeEntireCategory");
        }
        catch (Exception ex)
        {
            Fail("FamilyActivitySelection(includeEntireCategory) + IncludeEntireCategory", ex.Message);
        }

        // Test 11: FamilyActivitySelection equality — two default instances are equal.
        try
        {
            var a = new FamilyActivitySelection();
            var b = new FamilyActivitySelection();
            if (a != b)
                throw new InvalidOperationException("two default FamilyActivitySelection instances should be equal");
            Pass("FamilyActivitySelection equality");
        }
        catch (Exception ex)
        {
            Fail("FamilyActivitySelection equality", ex.Message);
        }

        // Test 12: Metadata loads for key types.
        MetadataTest<AuthorizationStatus>("AuthorizationStatus");
        MetadataTest<FamilyActivitySelection>("FamilyActivitySelection");
        MetadataTest<AuthorizationCenter>("AuthorizationCenter");

        // Test 15: AuthorizationCenter.AuthorizationStatus property returns a non-null value.
        try
        {
            var center = AuthorizationCenter.Shared;
            var status = center.AuthorizationStatus;
            if (status is null)
                throw new InvalidOperationException("AuthorizationCenter.AuthorizationStatus was null");
            Log($"AuthorizationCenter.AuthorizationStatus.Tag = {status.Tag}");
            Pass("AuthorizationCenter.AuthorizationStatus");
        }
        catch (Exception ex)
        {
            Fail("AuthorizationCenter.AuthorizationStatus", ex.Message);
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
        Console.WriteLine(prefixed ? $"[FAMILYCONTROLS-TEST] {msg}" : msg);
}
