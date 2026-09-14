// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Runtime.InteropServices;
using SwiftBindings.Build.Models;

namespace SwiftBindings.ActivityKit.Tests;

/// <summary>
/// Reads the entitlement from the running process's code signature. The host
/// harness verifies the bundle before install; this second check keeps manual
/// or re-signed launches fail-closed before the ActivityKit request itself.
/// </summary>
internal static class ActivityKitCapabilityReadiness
{
    private const string SecurityFramework = "/System/Library/Frameworks/Security.framework/Security";
    private const string CoreFoundationFramework = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const uint CfStringEncodingUtf8 = 0x08000100;

    [DllImport(SecurityFramework)]
    private static extern IntPtr SecTaskCreateFromSelf(IntPtr allocator);

    [DllImport(SecurityFramework)]
    private static extern IntPtr SecTaskCopyValueForEntitlement(
        IntPtr task,
        IntPtr entitlement,
        IntPtr error);

    [DllImport(CoreFoundationFramework)]
    private static extern void CFRelease(IntPtr value);

    [DllImport(CoreFoundationFramework)]
    private static extern nuint CFGetTypeID(IntPtr value);

    [DllImport(CoreFoundationFramework)]
    private static extern nuint CFStringGetTypeID();

    [DllImport(CoreFoundationFramework)]
    private static extern nint CFStringGetLength(IntPtr value);

    [DllImport(CoreFoundationFramework)]
    private static extern nint CFStringGetMaximumSizeForEncoding(nint length, uint encoding);

    [DllImport(CoreFoundationFramework)]
    private static extern byte CFStringGetCString(
        IntPtr value,
        IntPtr buffer,
        nint bufferSize,
        uint encoding);

    internal static string RequirePushTokenCapability()
    {
        var task = SecTaskCreateFromSelf(IntPtr.Zero);
        if (task == IntPtr.Zero)
            return ActivityKitEntitlementGate.RequireRuntimeEnvironment(null);

        try
        {
            using var entitlement = new Foundation.NSString(ActivityKitEntitlementGate.RequiredEntitlement);
            var value = SecTaskCopyValueForEntitlement(task, entitlement.Handle, IntPtr.Zero);
            if (value == IntPtr.Zero)
                return ActivityKitEntitlementGate.RequireRuntimeEnvironment(null);
            try
            {
                if (CFGetTypeID(value) != CFStringGetTypeID())
                    return ActivityKitEntitlementGate.RequireRuntimeEnvironment(null);
                var length = CFStringGetLength(value);
                var capacity = CFStringGetMaximumSizeForEncoding(length, CfStringEncodingUtf8) + 1;
                if (capacity <= 1)
                    return ActivityKitEntitlementGate.RequireRuntimeEnvironment(null);

                var buffer = Marshal.AllocHGlobal(checked((int)capacity));
                try
                {
                    var environment = CFStringGetCString(value, buffer, capacity, CfStringEncodingUtf8) != 0
                        ? Marshal.PtrToStringUTF8(buffer)
                        : null;
                    return ActivityKitEntitlementGate.RequireRuntimeEnvironment(environment);
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
            finally
            {
                CFRelease(value);
            }
        }
        finally
        {
            CFRelease(task);
        }
    }
}
