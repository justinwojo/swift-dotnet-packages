// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Foundation;
using Matter;
using ObjCRuntime;

namespace SwiftBindings.Matter.Tests;

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

        // ObjC class registration smoke — verifies the [Register] symbol
        // resolves against the Apple-shipped Matter.framework on the device /
        // simulator. A miss here means the binding declared a class the OS
        // doesn't expose (deployment-target mismatch, framework not loaded).
        void ClassHandleTest<T>(string name) where T : NSObject
        {
            try
            {
                var cls = new Class(typeof(T));
                if (cls.Handle == IntPtr.Zero)
                    throw new InvalidOperationException("Class.GetHandle returned zero");
                Pass($"{name} class registration");
            }
            catch (Exception ex) { Fail($"{name} class registration", ex.Message); }
        }

        // Headline Matter types — the surface a real commissioning integration
        // would touch (per the package plan / issue #38 cornerstone scope).
        ClassHandleTest<MTRSetupPayload>("MTRSetupPayload");
        ClassHandleTest<MTRCommissioningParameters>("MTRCommissioningParameters");
        ClassHandleTest<MTRDeviceController>("MTRDeviceController");
        ClassHandleTest<MTRDeviceControllerFactory>("MTRDeviceControllerFactory");
        ClassHandleTest<MTRDeviceControllerStartupParams>("MTRDeviceControllerStartupParams");
        ClassHandleTest<MTRBaseDevice>("MTRBaseDevice");
        ClassHandleTest<MTRDevice>("MTRDevice");
        ClassHandleTest<MTRClusterStateCacheContainer>("MTRClusterStateCacheContainer");
        ClassHandleTest<MTRBaseClusterOnOff>("MTRBaseClusterOnOff");
        ClassHandleTest<MTROnboardingPayloadParser>("MTROnboardingPayloadParser");
        ClassHandleTest<MTRQRCodeSetupPayloadParser>("MTRQRCodeSetupPayloadParser");
        ClassHandleTest<MTRManualSetupPayloadParser>("MTRManualSetupPayloadParser");

        // MTRNetworkCommissioningWiFiBand — frozen enum from apple-frameworks.json
        // ("valueTypes"). Pinning case values guards against Apple silently
        // reordering between SDK versions; the generator emits underscored names
        // for digit-leading Swift cases.
        try
        {
            if ((int)MTRNetworkCommissioningWiFiBand._2G4 != 0)
                throw new InvalidOperationException($"_2G4 expected 0, got {(int)MTRNetworkCommissioningWiFiBand._2G4}");
            if ((int)MTRNetworkCommissioningWiFiBand._3G65 != 1)
                throw new InvalidOperationException($"_3G65 expected 1, got {(int)MTRNetworkCommissioningWiFiBand._3G65}");
            if ((int)MTRNetworkCommissioningWiFiBand._5G != 2)
                throw new InvalidOperationException($"_5G expected 2, got {(int)MTRNetworkCommissioningWiFiBand._5G}");
            if ((int)MTRNetworkCommissioningWiFiBand._6G != 3)
                throw new InvalidOperationException($"_6G expected 3, got {(int)MTRNetworkCommissioningWiFiBand._6G}");
            if ((int)MTRNetworkCommissioningWiFiBand._60G != 4)
                throw new InvalidOperationException($"_60G expected 4, got {(int)MTRNetworkCommissioningWiFiBand._60G}");
            if ((int)MTRNetworkCommissioningWiFiBand._1G != 5)
                throw new InvalidOperationException($"_1G expected 5, got {(int)MTRNetworkCommissioningWiFiBand._1G}");
            Pass("MTRNetworkCommissioningWiFiBand case values");
        }
        catch (Exception ex) { Fail("MTRNetworkCommissioningWiFiBand case values", ex.Message); }

        // MTRSetupPayload(passcode, discriminator) — the cleanest @_cdecl-free
        // construction path. We pre-set version/productID/vendorID = 0 per the
        // Apple docstring and round-trip the fields.
        try
        {
            const uint passcode = 20202021u;
            const ushort discriminator = 3840;
            using var payload = new MTRSetupPayload(
                NSNumber.FromUInt32(passcode),
                NSNumber.FromUInt16(discriminator));
            if (payload.SetupPasscode is null || payload.SetupPasscode.UInt32Value != passcode)
                throw new InvalidOperationException($"SetupPasscode round-trip failed: {payload.SetupPasscode?.UInt32Value}");
            if (payload.Discriminator is null || payload.Discriminator.UInt16Value != discriminator)
                throw new InvalidOperationException($"Discriminator round-trip failed: {payload.Discriminator?.UInt16Value}");
            Pass("MTRSetupPayload(passcode, discriminator) round-trip");
        }
        catch (Exception ex) { Fail("MTRSetupPayload(passcode, discriminator) round-trip", ex.Message); }

        // MTRSetupPayload(qrPayload) — Apple's QR string ingest path. The
        // sample below is a syntactically-valid Matter base38 QR string; the
        // decoded passcode is whatever Apple's parser produces. We cross-check
        // the three parsing paths against each other rather than pinning a
        // specific passcode value (different SDK revisions encode the sample
        // differently).
        const string canonicalQrPayload = "MT:U9VJ0OMV172PX813000";
        uint? directCtorPasscode = null;
        ushort? directCtorDiscriminator = null;
        try
        {
            using var payload = new MTRSetupPayload(canonicalQrPayload);
            if (payload.SetupPasscode is null)
                throw new InvalidOperationException("SetupPasscode null after QR parse");
            if (payload.Discriminator is null)
                throw new InvalidOperationException("Discriminator null after QR parse");
            directCtorPasscode = payload.SetupPasscode.UInt32Value;
            directCtorDiscriminator = payload.Discriminator.UInt16Value;
            if (directCtorPasscode == 0)
                throw new InvalidOperationException("SetupPasscode 0 (parse likely silently failed)");
            Pass("MTRSetupPayload(qrPayload) parse");
        }
        catch (Exception ex) { Fail("MTRSetupPayload(qrPayload) parse", ex.Message); }

        // MTRQRCodeSetupPayloadParser — the explicit parser surface. Exercises
        // the out-NSError marshal-back and confirms the parser produces a
        // payload equivalent to the direct ctor path above.
        try
        {
            using var parser = new MTRQRCodeSetupPayloadParser(canonicalQrPayload);
            var parsed = parser.PopulatePayload(out NSError? error);
            if (parsed is null)
                throw new InvalidOperationException($"PopulatePayload returned null (error: {error?.LocalizedDescription ?? "nil"})");
            if (error is not null)
                throw new InvalidOperationException($"PopulatePayload reported error: {error.LocalizedDescription}");
            if (parsed.SetupPasscode is null || parsed.SetupPasscode.UInt32Value != directCtorPasscode)
                throw new InvalidOperationException($"passcode mismatch vs direct ctor: {parsed.SetupPasscode?.UInt32Value} vs {directCtorPasscode}");
            if (parsed.Discriminator is null || parsed.Discriminator.UInt16Value != directCtorDiscriminator)
                throw new InvalidOperationException($"discriminator mismatch vs direct ctor: {parsed.Discriminator?.UInt16Value} vs {directCtorDiscriminator}");
            parsed.Dispose();
            Pass("MTRQRCodeSetupPayloadParser.PopulatePayload");
        }
        catch (Exception ex) { Fail("MTRQRCodeSetupPayloadParser.PopulatePayload", ex.Message); }

        // MTROnboardingPayloadParser.SetupPayloadForOnboardingPayload — the
        // static dispatcher that accepts either a QR or manual code.
        try
        {
            var parsed = MTROnboardingPayloadParser.SetupPayloadForOnboardingPayload(canonicalQrPayload, out NSError? error);
            if (parsed is null)
                throw new InvalidOperationException($"SetupPayloadForOnboardingPayload returned null (error: {error?.LocalizedDescription ?? "nil"})");
            if (parsed.SetupPasscode is null || parsed.SetupPasscode.UInt32Value != directCtorPasscode)
                throw new InvalidOperationException($"passcode mismatch vs direct ctor: {parsed.SetupPasscode?.UInt32Value} vs {directCtorPasscode}");
            parsed.Dispose();
            Pass("MTROnboardingPayloadParser.SetupPayloadForOnboardingPayload");
        }
        catch (Exception ex) { Fail("MTROnboardingPayloadParser.SetupPayloadForOnboardingPayload", ex.Message); }

        // MTRSetupPayload static random generators — pure-ObjC class methods,
        // no state required.
        try
        {
            nuint pin = MTRSetupPayload.GenerateRandomPIN();
            if (pin == 0)
                throw new InvalidOperationException("GenerateRandomPIN returned 0");
            var passcode = MTRSetupPayload.GenerateRandomSetupPasscode();
            if (passcode is null)
                throw new InvalidOperationException("GenerateRandomSetupPasscode returned null");
            Pass("MTRSetupPayload static random generators");
        }
        catch (Exception ex) { Fail("MTRSetupPayload static random generators", ex.Message); }

        // MTRCommissioningParameters() no-arg ctor — constructor-tail per the
        // CLAUDE.md convention. If the @_cdecl wrapper for the default init is
        // missing, all tests above still ran.
        try
        {
            using var p = new MTRCommissioningParameters();
            _ = p.Handle;
            Pass("MTRCommissioningParameters() constructor");
        }
        catch (Exception ex) { Fail("MTRCommissioningParameters() constructor", ex.Message); }

        // Controller construction is intentionally skipped — MTRDeviceController
        // requires an MTRDeviceControllerFactory + crypto / storage delegate
        // setup that doesn't fit a smoke test. Class registration above is
        // the meaningful signal that the binding surface is reachable.
        Skip("MTRDeviceController construction", "requires factory + storage delegate setup, not a smoke-test concern");

        // Summary
        Log($"Results: {passed} passed, {failed} failed, {skipped} skipped");
        if (failed == 0)
            Log("TEST SUCCESS", prefixed: false);
        else
            Log($"TEST FAILED: {failed} failures", prefixed: false);
        return failed;
    }

    internal static void Log(string msg, bool prefixed = true) =>
        Console.WriteLine(prefixed ? $"[MATTER-TEST] {msg}" : msg);
}
