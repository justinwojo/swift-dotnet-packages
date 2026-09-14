// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Swift.ActivityKit;

namespace SwiftBindings.ActivityKit.Tests;

/// <summary>
/// Capability-qualified physical-device test. Request preflight and observer
/// ownership have distinct verdicts so a provisioning failure cannot be
/// reported as an observer-lifetime regression.
/// </summary>
internal static class Tests
{
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

    public static async Task<int> RunAsync()
    {
        var failed = 0;
        if (ObjCRuntime.Runtime.Arch == ObjCRuntime.Arch.SIMULATOR)
        {
            Fail("push-token device app preflight", "dedicated capability app must never run on a simulator");
            return Finish();
        }
        if (!OperatingSystem.IsIOSVersionAtLeast(16, 2))
        {
            Fail("push-token device app preflight", "ActivityKit content API requires iOS 16.2+");
            return Finish();
        }
        if (!await WaitForForegroundActiveAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(true))
        {
            Fail("push-token request preflight", "app did not reach UIApplicationState.Active within 15s");
            return Finish();
        }
        if (!LiveActivity.AreActivitiesEnabled)
        {
            Fail("push-token request preflight", "Live Activities are disabled for this app; enable the device setting before rerunning");
            return Finish();
        }

        try
        {
            var environment = ActivityKitCapabilityReadiness.RequirePushTokenCapability();
            Log($"signed aps-environment={environment}");
        }
        catch (Exception ex)
        {
            Fail("push-token request preflight", $"{ex.GetType().Name}: {ex.Message}");
            return Finish();
        }

        LiveActivity? activity = null;
        try
        {
            try
            {
                activity = LiveActivity.Request(
                    name: "delivery",
                    attributesJson: "{\"title\":\"Capability-qualified request\"}",
                    contentStateJson: "{\"status\":\"ready\"}",
                    usePushToken: true);
                if (!activity.IsActive)
                    throw new InvalidOperationException("request returned a non-active handle");
                Pass("push-token request preflight");
            }
            catch (Exception ex)
            {
                Fail("push-token request preflight", $"{ex.GetType().Name}: {ex.Message}");
                return Finish();
            }

            try
            {
                if (!activity.ObservePushToken(_ => { }))
                    throw new InvalidOperationException("first observer registration returned false");
                if (!activity.ObservePushToken(_ => { }))
                    throw new InvalidOperationException("replacement observer registration returned false");
                if (!activity.End(immediate: true))
                    throw new InvalidOperationException("end after observer replacement returned false");
                if (activity.ObservePushToken(_ => { }))
                    throw new InvalidOperationException("observer registration on a released handle returned true");
                activity = null;
                Pass("push-token observer register/replace/release");
            }
            catch (Exception ex)
            {
                Fail("push-token observer register/replace/release", $"{ex.GetType().Name}: {ex.Message}");
            }
        }
        finally
        {
            try { activity?.End(immediate: true); }
            catch (Exception cleanup) { Log($"cleanup end failed: {cleanup.Message}"); }
        }

        return Finish();

        void Pass(string name) => Log($"PASS: {name}");
        void Fail(string name, string reason)
        {
            failed++;
            Log($"FAIL: {name} — {reason}");
        }
        int Finish()
        {
            Log(failed == 0 ? "TEST SUCCESS" : $"TEST FAILED: {failed} failures", prefixed: false);
            return failed;
        }
    }

    internal static void Log(string message, bool prefixed = true) =>
        Console.WriteLine(prefixed ? $"[ACTIVITYKIT-PUSH-TEST] {message}" : message);
}
