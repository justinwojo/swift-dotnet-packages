// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Foundation;
using Matter;
using MatterSupport;
using Swift.Runtime;

namespace SwiftBindings.MatterSupport.Tests;

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
                    throw new InvalidOperationException("metadata handle is null");
                Pass($"{name} metadata");
            }
            catch (Exception ex) { Fail($"{name} metadata", ex.Message); }
        }

        // Swift type-metadata smokes for the surface the issue #38 scenario
        // touches. MatterAddDeviceRequest is a Swift class, the rest are Swift
        // structs / enums; SwiftObjectHelper exercises the type-metadata
        // accessor symbol from MatterSupport.framework + the wrapper xcframework.
        MetadataTest<MatterAddDeviceRequest>("MatterAddDeviceRequest");
        MetadataTest<MatterAddDeviceRequest.TopologyType>("MatterAddDeviceRequest.Topology");
        MetadataTest<MatterAddDeviceRequest.Home>("MatterAddDeviceRequest.Home");
        MetadataTest<MatterAddDeviceRequest.Room>("MatterAddDeviceRequest.Room");
        MetadataTest<MatterAddDeviceRequest.DeviceCriteria>("MatterAddDeviceRequest.DeviceCriteria");
        MetadataTest<MatterAddDeviceExtensionRequestHandler.WiFiNetworkAssociation>("MatterAddDeviceExtensionRequestHandler.WiFiNetworkAssociation");

        // MatterAddDeviceRequest.IsSupported — iOS 17+ ABI smoke. Reads a
        // static bool through @_cdecl with no allocation; expected to be true
        // on any iOS 17+ simulator / device.
        try
        {
            bool supported = MatterAddDeviceRequest.IsSupported;
            Log($"MatterAddDeviceRequest.IsSupported = {supported}");
            Pass("MatterAddDeviceRequest.IsSupported");
        }
        catch (Exception ex) { Fail("MatterAddDeviceRequest.IsSupported", ex.Message); }

        // ─── Issue #38 cornerstone: cross-framework construction ────────────
        // Build a MatterAddDeviceRequest whose setupPayload is a Matter ObjC
        // type (MTRSetupPayload) reached through the sibling
        // SwiftBindings.Apple.Matter package. The Swift @_cdecl ctor wrapper
        // for MatterAddDeviceRequest.init(topology:setupPayload:) hands the
        // ObjC pointer back across the bridge into Swift's Matter.MTRSetupPayload
        // (the cross-module type the wrapper had to `import Matter` to see —
        // see /Users/wojo/Dev/swift-bindings/src/docs/matter-support-wrapper-import-gap.md).
        // Round-tripping the SetupPayload property through Swift and back
        // pins the wrapper-import fix against regressions.
        const string canonicalQrPayload = "MT:U9VJ0OMV172PX813000";
        try
        {
            using var payload = new MTRSetupPayload(canonicalQrPayload);
            if (payload.SetupPasscode is null || payload.SetupPasscode.UInt32Value == 0)
                throw new InvalidOperationException($"setup precondition failed: passcode parse produced {payload.SetupPasscode?.UInt32Value.ToString() ?? "null"}");
            uint originalPasscode = payload.SetupPasscode.UInt32Value;

            using var home = new MatterAddDeviceRequest.Home("Test Home");
            using var topology = new MatterAddDeviceRequest.TopologyType("Test Ecosystem", new[] { home });

            using var request = new MatterAddDeviceRequest(topology, setupPayload: payload);

            // Round-trip the SetupPayload property: getter unwraps the Swift
            // optional<Matter.MTRSetupPayload> back into the ObjC handle.
            var rt = request.SetupPayload;
            if (rt is null)
                throw new InvalidOperationException("SetupPayload getter returned null");
            if (rt.SetupPasscode is null || rt.SetupPasscode.UInt32Value != originalPasscode)
                throw new InvalidOperationException($"SetupPayload round-trip lost passcode: {rt.SetupPasscode?.UInt32Value} vs original {originalPasscode}");
            Pass("MatterAddDeviceRequest(topology, setupPayload) cross-framework ctor");
        }
        catch (Exception ex) { Fail("MatterAddDeviceRequest(topology, setupPayload) cross-framework ctor", ex.Message); }

        // Same surface, three-arg overload that also takes a DeviceCriteria.
        // Uses the cached `AllDevices` singleton so we don't have to build a
        // fabric/vendor/product specifier just to smoke-test the wrapper.
        try
        {
            using var payload = new MTRSetupPayload(canonicalQrPayload);
            using var home = new MatterAddDeviceRequest.Home("Test Home 2");
            using var topology = new MatterAddDeviceRequest.TopologyType("Test Ecosystem 2", new[] { home });
            using var criteria = MatterAddDeviceRequest.DeviceCriteria.AllDevices;

            using var request = new MatterAddDeviceRequest(topology, setupPayload: payload, deviceCriteria: criteria);
            var rt = request.SetupPayload;
            if (rt is null)
                throw new InvalidOperationException("SetupPayload getter returned null");
            Pass("MatterAddDeviceRequest(topology, setupPayload, deviceCriteria) ctor");
        }
        catch (Exception ex) { Fail("MatterAddDeviceRequest(topology, setupPayload, deviceCriteria) ctor", ex.Message); }

        // ShowDeviceCriteria property — round-trips the DeviceCriteria Swift
        // enum value back through C#.
        try
        {
            using var payload = new MTRSetupPayload(canonicalQrPayload);
            using var home = new MatterAddDeviceRequest.Home("Test Home 3");
            using var topology = new MatterAddDeviceRequest.TopologyType("Test Ecosystem 3", new[] { home });
            using var request = new MatterAddDeviceRequest(topology, setupPayload: payload);
            using var criteria = request.ShowDeviceCriteria;
            // The default criteria for the two-arg ctor is .allDevices.
            if (criteria.Tag != MatterAddDeviceRequest.DeviceCriteria.CaseTag.AllDevices)
                throw new InvalidOperationException($"unexpected default criteria tag: {criteria.Tag}");
            Pass("MatterAddDeviceRequest.ShowDeviceCriteria default");
        }
        catch (Exception ex) { Fail("MatterAddDeviceRequest.ShowDeviceCriteria default", ex.Message); }

        // request.perform() / PerformAsync is the actual UI flow — it drives
        // Apple's system commissioning sheet and requires HomeKit + an entitled
        // app + a real Matter accessory. Out of scope for a sim smoke test.
        Skip("MatterAddDeviceRequest.PerformAsync()",
            "drives Apple's system commissioning UI — requires HomeKit-paired Matter device + entitlement");

        // Summary
        Log($"Results: {passed} passed, {failed} failed, {skipped} skipped");
        if (failed == 0)
            Log("TEST SUCCESS", prefixed: false);
        else
            Log($"TEST FAILED: {failed} failures", prefixed: false);
        return failed;
    }

    internal static void Log(string msg, bool prefixed = true) =>
        Console.WriteLine(prefixed ? $"[MATTERSUPPORT-TEST] {msg}" : msg);
}
