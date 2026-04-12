// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using ActivityKit;
using Swift.Runtime;

namespace SwiftBindings.ActivityKit.Tests;

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

        // Test 1: ActivityStyle plain enum — Standard=0, Transient=1.
        try
        {
            if ((int)ActivityStyle.Standard != 0)
                throw new InvalidOperationException($"Standard expected 0, got {(int)ActivityStyle.Standard}");
            if ((int)ActivityStyle.Transient != 1)
                throw new InvalidOperationException($"Transient expected 1, got {(int)ActivityStyle.Transient}");
            Pass("ActivityStyle values");
        }
        catch (Exception ex)
        {
            Fail("ActivityStyle values", ex.Message);
        }

        // Test 2: ActivityState plain enum — five states with expected integer tags.
        try
        {
            if ((int)ActivityState.Pending != 0)
                throw new InvalidOperationException($"Pending expected 0, got {(int)ActivityState.Pending}");
            if ((int)ActivityState.Active != 1)
                throw new InvalidOperationException($"Active expected 1, got {(int)ActivityState.Active}");
            if ((int)ActivityState.Ended != 2)
                throw new InvalidOperationException($"Ended expected 2, got {(int)ActivityState.Ended}");
            if ((int)ActivityState.Dismissed != 3)
                throw new InvalidOperationException($"Dismissed expected 3, got {(int)ActivityState.Dismissed}");
            if ((int)ActivityState.Stale != 4)
                throw new InvalidOperationException($"Stale expected 4, got {(int)ActivityState.Stale}");
            Pass("ActivityState values");
        }
        catch (Exception ex)
        {
            Fail("ActivityState values", ex.Message);
        }

        // Test 3: ActivityAuthorizationError plain enum — verify boundary values.
        try
        {
            if ((int)ActivityAuthorizationError.AttributesTooLarge != 0)
                throw new InvalidOperationException($"AttributesTooLarge expected 0, got {(int)ActivityAuthorizationError.AttributesTooLarge}");
            if ((int)ActivityAuthorizationError.Unentitled != 9)
                throw new InvalidOperationException($"Unentitled expected 9, got {(int)ActivityAuthorizationError.Unentitled}");
            if ((int)ActivityAuthorizationError.ReconnectNotPermitted != 11)
                throw new InvalidOperationException($"ReconnectNotPermitted expected 11, got {(int)ActivityAuthorizationError.ReconnectNotPermitted}");
            Pass("ActivityAuthorizationError values");
        }
        catch (Exception ex)
        {
            Fail("ActivityAuthorizationError values", ex.Message);
        }

        // Test 4: ActivityAuthorizationErrorExtensions.ErrorDomain — static string, non-empty.
        try
        {
            var domain = ActivityAuthorizationErrorExtensions.ErrorDomain;
            if (string.IsNullOrEmpty(domain))
                throw new InvalidOperationException("ErrorDomain is null or empty");
            Log($"ErrorDomain = {domain}");
            Pass("ActivityAuthorizationError.ErrorDomain");
        }
        catch (Exception ex)
        {
            Fail("ActivityAuthorizationError.ErrorDomain", ex.Message);
        }

        // Test 5: ActivityAuthorizationError.GetErrorCode — cdecl round-trip for each known case.
        try
        {
            var code0 = ActivityAuthorizationError.AttributesTooLarge.GetErrorCode();
            var code9 = ActivityAuthorizationError.Unentitled.GetErrorCode();
            Log($"AttributesTooLarge errorCode={code0}, Unentitled errorCode={code9}");
            Pass("ActivityAuthorizationError.GetErrorCode");
        }
        catch (Exception ex)
        {
            Fail("ActivityAuthorizationError.GetErrorCode", ex.Message);
        }

        // Test 6: ActivityAuthorizationError.GetFailureReason — extension method, may return null or string.
        try
        {
            var reason = ActivityAuthorizationError.Denied.GetFailureReason();
            Log($"Denied.GetFailureReason = {reason ?? "<null>"}");
            Pass("ActivityAuthorizationError.GetFailureReason");
        }
        catch (Exception ex)
        {
            Fail("ActivityAuthorizationError.GetFailureReason", ex.Message);
        }

        // Test 7: ActivityAuthorizationError.GetRecoverySuggestion — extension method round-trip.
        try
        {
            var suggestion = ActivityAuthorizationError.Unsupported.GetRecoverySuggestion();
            Log($"Unsupported.GetRecoverySuggestion = {suggestion ?? "<null>"}");
            Pass("ActivityAuthorizationError.GetRecoverySuggestion");
        }
        catch (Exception ex)
        {
            Fail("ActivityAuthorizationError.GetRecoverySuggestion", ex.Message);
        }

        // Test 8: ActivityAuthorizationInfo() no-arg constructor — verify object is non-null.
        try
        {
            var info = new ActivityAuthorizationInfo();
            if (info is null)
                throw new InvalidOperationException("ActivityAuthorizationInfo() returned null");
            Pass("ActivityAuthorizationInfo ctor");
        }
        catch (Exception ex)
        {
            Fail("ActivityAuthorizationInfo ctor", ex.Message);
        }

        // Test 9: ActivityAuthorizationInfo.AreActivitiesEnabled — property read; any bool is valid.
        try
        {
            var info = new ActivityAuthorizationInfo();
            bool enabled = info.AreActivitiesEnabled;
            Log($"AreActivitiesEnabled = {enabled}");
            Pass("ActivityAuthorizationInfo.AreActivitiesEnabled");
        }
        catch (Exception ex)
        {
            Fail("ActivityAuthorizationInfo.AreActivitiesEnabled", ex.Message);
        }

        // Test 10: ActivityAuthorizationInfo.FrequentPushesEnabled — property read (iOS 16.2+).
        try
        {
            var info = new ActivityAuthorizationInfo();
            bool enabled = info.FrequentPushesEnabled;
            Log($"FrequentPushesEnabled = {enabled}");
            Pass("ActivityAuthorizationInfo.FrequentPushesEnabled");
        }
        catch (Exception ex)
        {
            Fail("ActivityAuthorizationInfo.FrequentPushesEnabled", ex.Message);
        }

        // Test 11: ActivityAuthorizationInfo.ActivityEnablementUpdates — retrieve async sequence
        // and dispose immediately without enumerating (non-ISwiftObject struct — safe to Dispose).
        try
        {
            var info = new ActivityAuthorizationInfo();
            var updates = info.ActivityEnablementUpdates;
            updates.Dispose();
            Pass("ActivityAuthorizationInfo.ActivityEnablementUpdates (create + dispose)");
        }
        catch (Exception ex)
        {
            Fail("ActivityAuthorizationInfo.ActivityEnablementUpdates (create + dispose)", ex.Message);
        }

        // Test 12: ActivityUIDismissalPolicy.Default singleton — non-null.
        try
        {
            var policy = ActivityUIDismissalPolicy.Default;
            if (policy is null)
                throw new InvalidOperationException("ActivityUIDismissalPolicy.Default is null");
            Pass("ActivityUIDismissalPolicy.Default");
        }
        catch (Exception ex)
        {
            Fail("ActivityUIDismissalPolicy.Default", ex.Message);
        }

        // Test 13: ActivityUIDismissalPolicy.Immediate singleton — non-null.
        try
        {
            var policy = ActivityUIDismissalPolicy.Immediate;
            if (policy is null)
                throw new InvalidOperationException("ActivityUIDismissalPolicy.Immediate is null");
            Pass("ActivityUIDismissalPolicy.Immediate");
        }
        catch (Exception ex)
        {
            Fail("ActivityUIDismissalPolicy.Immediate", ex.Message);
        }

        // Test 14: AlertConfiguration.AlertSound.Default singleton — non-null.
        try
        {
            var sound = AlertConfiguration.AlertSound.Default;
            if (sound is null)
                throw new InvalidOperationException("AlertConfiguration.AlertSound.Default is null");
            Pass("AlertConfiguration.AlertSound.Default");
        }
        catch (Exception ex)
        {
            Fail("AlertConfiguration.AlertSound.Default", ex.Message);
        }

        // Test 15: PushType.Token singleton — non-null (iOS 16.1+).
        try
        {
            var token = PushType.Token;
            if (token is null)
                throw new InvalidOperationException("PushType.Token is null");
            Pass("PushType.Token");
        }
        catch (Exception ex)
        {
            Fail("PushType.Token", ex.Message);
        }

        // Test 16–19: Metadata loads for core ActivityKit types.
        MetadataTest<ActivityAuthorizationInfo>("ActivityAuthorizationInfo");
        MetadataTest<AlertConfiguration>("AlertConfiguration");
        MetadataTest<AlertConfiguration.AlertSound>("AlertConfiguration.AlertSound");
        MetadataTest<ActivityUIDismissalPolicy>("ActivityUIDismissalPolicy");

        // Summary
        Log($"Results: {passed} passed, {failed} failed, {skipped} skipped");
        if (failed == 0)
            Log("TEST SUCCESS", prefixed: false);
        else
            Log($"TEST FAILED: {failed} failures", prefixed: false);
        return failed;
    }

    internal static void Log(string msg, bool prefixed = true) =>
        Console.WriteLine(prefixed ? $"[ACTIVITYKIT-TEST] {msg}" : msg);
}
