// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using ActivityKit;
using Swift.ActivityKit;
using Swift.Runtime;

namespace SwiftBindings.ActivityKit.Tests;

internal static class Tests
{
    /// <summary>
    /// ActivityKit refuses <c>Activity.request</c> unless the app is foreground-ACTIVE; the system
    /// reports "Target is not foreground" through <see cref="LiveActivityException"/>. The test
    /// runner starts from <c>ViewDidLoad</c>, which runs before the app becomes active, so the
    /// lifecycle tests wait for that state first. Polls with a real await (the run loop must keep
    /// pumping for the state to ever change — a blocking wait here would never observe it).
    /// </summary>
    private static async Task<bool> WaitForForegroundActiveAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (UIKit.UIApplication.SharedApplication?.ApplicationState == UIKit.UIApplicationState.Active)
                return true;
            await Task.Delay(50).ConfigureAwait(true);
        }
        return UIKit.UIApplication.SharedApplication?.ApplicationState == UIKit.UIApplicationState.Active;
    }

    /// <summary>
    /// The exact ActivityKit precondition text for a request issued while the app is not
    /// foreground-active. Matching it converts that one environment condition into a NAMED skip;
    /// every other <see cref="LiveActivityException"/> is a real failure.
    /// </summary>
    private const string ForegroundPreconditionText = "Target is not foreground";

    public static async Task<int> RunAsync()
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
        // without enumerating. ActivityEnablementUpdatesType is ISwiftObject — do NOT Dispose.
        try
        {
            var info = new ActivityAuthorizationInfo();
            var updates = info.ActivityEnablementUpdates;
            Log($"ActivityEnablementUpdates type = {updates.GetType().Name}");
            Pass("ActivityAuthorizationInfo.ActivityEnablementUpdates");
        }
        catch (Exception ex)
        {
            Fail("ActivityAuthorizationInfo.ActivityEnablementUpdates", ex.Message);
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

        // ── Swift.ActivityKit.LiveActivity facade (Apple supplement) ──
        // These exercise the consumer-visible Live Activities surface that ships in
        // the SwiftBindings.Apple supplement and is pulled in transitively by this
        // package. They stay headless-safe: we never start a real activity (that
        // needs a foreground-active host + an embedded widget), only the calls that
        // are deterministic on a test runner — the enablement read and the input
        // validation that rejects payloads BEFORE any native ActivityKit call.

        // Test 20: AreActivitiesEnabled — the @_cdecl Int->bool projection. The VALUE is asserted on
        // the simulator: this app's Info.plist now carries NSSupportsLiveActivities and a fresh
        // simulator defaults the per-app toggle on, so `false` there means the capability declaration
        // or the projection is broken — not an environment quirk. On a physical device the toggle is
        // user-controlled Settings state the harness cannot pin, so there only completing the call is
        // the assertion.
        try
        {
            bool enabled = LiveActivity.AreActivitiesEnabled;
            Log($"LiveActivity.AreActivitiesEnabled = {enabled}");
            if (ObjCRuntime.Runtime.Arch == ObjCRuntime.Arch.SIMULATOR)
            {
                if (!enabled)
                    throw new InvalidOperationException(
                        "AreActivitiesEnabled is false on the simulator despite NSSupportsLiveActivities being declared");
                Pass("LiveActivity.AreActivitiesEnabled is true (NSSupportsLiveActivities declared)");
            }
            else
            {
                Log("device: the Live Activities toggle is user-controlled Settings state; value not asserted");
                Pass("LiveActivity.AreActivitiesEnabled");
            }
        }
        catch (Exception ex)
        {
            Fail("LiveActivity.AreActivitiesEnabled", ex.Message);
        }

        // Test 21: Request rejects malformed JSON attributes with ArgumentException
        // (a malformed payload would start an activity whose widget renders nothing,
        // so the facade fails fast before touching ActivityKit).
        try
        {
            LiveActivity.Request("delivery", attributesJson: "{not valid json");
            Fail("LiveActivity.Request rejects malformed JSON", "expected ArgumentException, none thrown");
        }
        catch (ArgumentException)
        {
            Pass("LiveActivity.Request rejects malformed JSON");
        }
        catch (Exception ex)
        {
            Fail("LiveActivity.Request rejects malformed JSON", $"wrong exception: {ex.GetType().Name}");
        }

        // Test 22: Request rejects a non-object JSON content state (the widget decodes
        // the payload as an object, so a top-level array/scalar is rejected).
        try
        {
            LiveActivity.Request("delivery", contentStateJson: "[1,2,3]");
            Fail("LiveActivity.Request rejects non-object JSON", "expected ArgumentException, none thrown");
        }
        catch (ArgumentException)
        {
            Pass("LiveActivity.Request rejects non-object JSON");
        }
        catch (Exception ex)
        {
            Fail("LiveActivity.Request rejects non-object JSON", $"wrong exception: {ex.GetType().Name}");
        }

        // Test 23: Request rejects an embedded NUL in the name (it crosses as a
        // null-terminated UTF-8 C string and would silently truncate on the Swift side).
        try
        {
            LiveActivity.Request("deli\0very");
            Fail("LiveActivity.Request rejects embedded NUL", "expected ArgumentException, none thrown");
        }
        catch (ArgumentException)
        {
            Pass("LiveActivity.Request rejects embedded NUL");
        }
        catch (Exception ex)
        {
            Fail("LiveActivity.Request rejects embedded NUL", $"wrong exception: {ex.GetType().Name}");
        }

        // Test 24: LiveActivityException carries the system-reported reason verbatim.
        try
        {
            var liveEx = new LiveActivityException("activities disabled");
            if (liveEx.Message != "activities disabled")
                throw new InvalidOperationException($"message not preserved: {liveEx.Message}");
            Pass("LiveActivityException message round-trip");
        }
        catch (Exception ex)
        {
            Fail("LiveActivityException message round-trip", ex.Message);
        }

        // ── Live Activity FOREGROUND LIFECYCLE ──
        // Everything above this line is validation that never reaches ActivityKit: no activity is
        // ever requested, updated or ended, so the whole .NET -> @_cdecl -> ActivityKit chain (the
        // thing the package exists to provide) was previously unexercised in this suite. Tests 25–27
        // drive the real lifecycle. They are ported from the swift-bindings BindingTests fixture
        // (RuntimeTestsApp/AppleSupplement/LiveActivityTests.cs) and every one of them ends the
        // activity in a `finally` so a mid-test failure cannot strand a live activity on the host.
        //
        // On-screen rendering is deliberately NOT asserted: the simulator does not composite
        // third-party Live Activities and there is no widget extension in this test app. request()
        // still succeeds and the system tracks the activity — that is the contract pinned here.
        //
        // Preconditions, both handled as NAMED environment skips rather than passes:
        //   * the app must be foreground-ACTIVE (ActivityKit answers "Target is not foreground"
        //     otherwise) — the runner starts in ViewDidLoad, before the app is active, so we wait;
        //   * Live Activities must be enabled for the app (NSSupportsLiveActivities in Info.plist +
        //     the user-facing toggle).
        bool activityKitOs = OperatingSystem.IsIOSVersionAtLeast(16, 2);
        bool foregroundActive = false;
        bool liveActivitiesReady = false;
        string lifecycleSkipReason = "";

        if (!activityKitOs)
        {
            lifecycleSkipReason = "the content-based ActivityKit API requires iOS 16.2+; this runtime is older";
        }
        else
        {
            foregroundActive = await WaitForForegroundActiveAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(true);
            if (!foregroundActive)
            {
                lifecycleSkipReason =
                    $"app never reached UIApplicationState.Active within 15s — ActivityKit's request() precondition (\"{ForegroundPreconditionText}\") cannot be met in this host";
            }
            else if (!LiveActivity.AreActivitiesEnabled)
            {
                lifecycleSkipReason =
                    "Live Activities are disabled for this app (ActivityAuthorizationInfo.areActivitiesEnabled == false); needs NSSupportsLiveActivities plus the user-facing toggle";
            }
            else
            {
                liveActivitiesReady = true;
            }
        }

        // Local helper: classify a request-time failure. Only the documented foreground precondition
        // becomes a named skip; anything else is a real failure.
        void RequestFailure(string name, Exception ex)
        {
            if (ex is LiveActivityException && ex.Message.Contains(ForegroundPreconditionText, StringComparison.OrdinalIgnoreCase))
                Skip(name, $"ActivityKit refused the request with its foreground precondition: \"{ex.Message}\"");
            else
                Fail(name, $"{ex.GetType().Name}: {ex.Message}");
        }

        // Test 25: request -> update (escaped + non-ASCII JSON) -> end, the happy path.
        if (!liveActivitiesReady)
        {
            Skip("LiveActivity lifecycle request/update/end", lifecycleSkipReason);
        }
        else
        {
            LiveActivity? activity = null;
            try
            {
                activity = LiveActivity.Request(
                    name: "delivery",
                    attributesJson: "{\"meta\":{\"id\":7,\"tags\":[\"a\",\"b\"]}}",
                    contentStateJson: "{\"status\":\"preparing\"}");
                if (!activity.IsActive)
                    throw new InvalidOperationException("request returned a handle that is not active");

                // Escaped quotes, a Latin-1 escape and an astral-plane surrogate pair must survive
                // the null-terminated UTF-8 crossing the shim uses for the payload blobs.
                if (!activity.Update("{\"note\":\"He said \\\"hi\\\" \\u00e9 \\ud83c\\udf0d\"}"))
                    throw new InvalidOperationException("update on a live handle returned false");

                if (!activity.End("{\"status\":\"delivered\"}", immediate: true))
                    throw new InvalidOperationException("end on a live handle returned false");
                if (activity.IsActive)
                    throw new InvalidOperationException("handle is still active after end");
                activity = null;
                Pass("LiveActivity lifecycle request/update/end");
            }
            catch (Exception ex)
            {
                RequestFailure("LiveActivity lifecycle request/update/end", ex);
            }
            finally
            {
                // Never strand a live activity if an assertion threw between request and end.
                try { activity?.End(immediate: true); } catch (Exception cleanup) { Log($"cleanup end failed: {cleanup.Message}"); }
            }
        }

        // Test 26: end is idempotent and update-after-end is a safe no-op — the use-after-free
        // hardening the Swift-side id->Activity registry buys. A second End() must return false
        // rather than double-free, and Update() on a dead handle must return false rather than
        // dereference a dangling Activity.
        if (!liveActivitiesReady)
        {
            Skip("LiveActivity end idempotence + update-after-end", lifecycleSkipReason);
        }
        else
        {
            LiveActivity? activity = null;
            try
            {
                activity = LiveActivity.Request("delivery");
                if (!activity.IsActive)
                    throw new InvalidOperationException("request returned a handle that is not active");
                if (!activity.End(immediate: true))
                    throw new InvalidOperationException("first end returned false");
                if (activity.End(immediate: true))
                    throw new InvalidOperationException("second end returned true — it must be a no-op");
                if (activity.Update("{\"status\":\"late\"}"))
                    throw new InvalidOperationException("update after end returned true — it must be a no-op");
                activity = null;
                Pass("LiveActivity end idempotence + update-after-end");
            }
            catch (Exception ex)
            {
                RequestFailure("LiveActivity end idempotence + update-after-end", ex);
            }
            finally
            {
                try { activity?.End(immediate: true); } catch (Exception cleanup) { Log($"cleanup end failed: {cleanup.Message}"); }
            }
        }

        // Test 27: push-token observation registers on a live handle, a second registration REPLACES
        // the first without crashing (the Swift side must cancel the prior observer task and release
        // its managed context rather than leave the old GCHandle rooted for a still-running task),
        // and observation on a dead handle returns false. No APNs capability is needed — no token
        // ever arrives on the simulator; this pins the register/replace/release path.
        if (!liveActivitiesReady)
        {
            Skip("LiveActivity push-token observer register/replace/release", lifecycleSkipReason);
        }
        else
        {
            LiveActivity? activity = null;
            try
            {
                activity = LiveActivity.Request("delivery", usePushToken: true);
                if (!activity.ObservePushToken(_ => { }))
                    throw new InvalidOperationException("first observe returned false on a live handle");
                if (!activity.ObservePushToken(_ => { }))
                    throw new InvalidOperationException("second observe (replace) returned false on a live handle");
                if (!activity.End(immediate: true))
                    throw new InvalidOperationException("end after observer replace returned false");
                if (activity.ObservePushToken(_ => { }))
                    throw new InvalidOperationException("observe on a dead handle returned true");
                activity = null;
                Pass("LiveActivity push-token observer register/replace/release");
            }
            catch (Exception ex)
            {
                RequestFailure("LiveActivity push-token observer register/replace/release", ex);
            }
            finally
            {
                try { activity?.End(immediate: true); } catch (Exception cleanup) { Log($"cleanup end failed: {cleanup.Message}"); }
            }
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
        Console.WriteLine(prefixed ? $"[ACTIVITYKIT-TEST] {msg}" : msg);
}
