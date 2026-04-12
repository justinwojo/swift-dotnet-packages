// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RoomPlan;
using Swift.Runtime;

namespace SwiftBindings.RoomPlan.Tests;

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

        // Core room-capture types — exercises metadata symbol resolution for
        // both struct wrappers and the enum wrappers (CapturedElementCategory).
        MetadataTest<CapturedElementCategory>("CapturedElementCategory metadata");
        MetadataTest<CapturedRoomData>("CapturedRoomData metadata");
        MetadataTest<CapturedStructure>("CapturedStructure metadata");
        MetadataTest<CapturedRoom>("CapturedRoom metadata");
        MetadataTest<CapturedRoom.Section>("CapturedRoom.Section metadata");
        MetadataTest<CapturedRoom.Surface>("CapturedRoom.Surface metadata");
        MetadataTest<CapturedRoom.Object>("CapturedRoom.Object metadata");
        MetadataTest<CapturedRoom.USDExportOptions>("CapturedRoom.USDExportOptions metadata");
        MetadataTest<RoomBuilder.ConfigurationOptions>("RoomBuilder.ConfigurationOptions metadata");

        // Nested plain-enum values: exercise that the generated enum has the
        // correct layout and Swift case order.
        try
        {
            if ((int)RoomCaptureSession.CaptureError.ExceedSceneSizeLimit != 0 ||
                (int)RoomCaptureSession.CaptureError.InternalError != 5)
                throw new InvalidOperationException("CaptureError values mismatch");
            Pass("RoomCaptureSession.CaptureError values");
        }
        catch (Exception ex)
        {
            Fail("RoomCaptureSession.CaptureError values", ex.Message);
        }

        try
        {
            if ((int)RoomCaptureSession.Instruction.MoveCloseToWall != 0 ||
                (int)RoomCaptureSession.Instruction.LowTexture != 5)
                throw new InvalidOperationException("Instruction values mismatch");
            Pass("RoomCaptureSession.Instruction values");
        }
        catch (Exception ex)
        {
            Fail("RoomCaptureSession.Instruction values", ex.Message);
        }

        // Error-description extension — pure enum tag -> Swift cdecl round-trip.
        // This is the only call that actually crosses the ABI from managed to Swift
        // without needing permissions or an active session.
        try
        {
            var desc = RoomCaptureSessionCaptureErrorExtensions.GetErrorDescription(
                RoomCaptureSession.CaptureError.ExceedSceneSizeLimit);
            Log($"ExceedSceneSizeLimit description = {desc}");
            Pass("RoomCaptureSession.CaptureError.GetErrorDescription");
        }
        catch (Exception ex)
        {
            Fail("RoomCaptureSession.CaptureError.GetErrorDescription", ex.Message);
        }

        // AllCases on CapturedRoom.Surface.Edge — exercises the extension static
        // property path (reflection-based enum case enumeration, no Swift call).
        try
        {
            var all = CapturedRoomSurfaceEdgeExtensions.AllCases;
            if (all.Count == 0)
                throw new InvalidOperationException("Edge.AllCases is empty");
            Pass("CapturedRoom.Surface.Edge.AllCases");
        }
        catch (Exception ex)
        {
            Fail("CapturedRoom.Surface.Edge.AllCases", ex.Message);
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
        Console.WriteLine(prefixed ? $"[ROOMPLAN-TEST] {msg}" : msg);
}
