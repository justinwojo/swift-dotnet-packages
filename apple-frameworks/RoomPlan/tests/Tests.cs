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

        // RoomCaptureSession.Instruction plain-enum values.
        try
        {
            if ((int)RoomCaptureSession.Instruction.MoveCloseToWall != 0 ||
                (int)RoomCaptureSession.Instruction.MoveAwayFromWall != 1 ||
                (int)RoomCaptureSession.Instruction.SlowDown != 2 ||
                (int)RoomCaptureSession.Instruction.TurnOnLight != 3 ||
                (int)RoomCaptureSession.Instruction.Normal != 4 ||
                (int)RoomCaptureSession.Instruction.LowTexture != 5)
                throw new InvalidOperationException("Instruction enum case values mismatch");
            Pass("RoomCaptureSession.Instruction case values");
        }
        catch (Exception ex)
        {
            Fail("RoomCaptureSession.Instruction case values", ex.Message);
        }

        // RoomBuilder.BuildError plain-enum values.
        try
        {
            if ((int)RoomBuilder.BuildError.InsufficientInput != 0 ||
                (int)RoomBuilder.BuildError.InvalidInput != 1 ||
                (int)RoomBuilder.BuildError.ExceedSceneSizeLimit != 2 ||
                (int)RoomBuilder.BuildError.DeviceNotSupported != 3 ||
                (int)RoomBuilder.BuildError.InternalError != 4)
                throw new InvalidOperationException("RoomBuilder.BuildError case values mismatch");
            Pass("RoomBuilder.BuildError case values");
        }
        catch (Exception ex)
        {
            Fail("RoomBuilder.BuildError case values", ex.Message);
        }

        // StructureBuilder.BuildError plain-enum values.
        try
        {
            if ((int)StructureBuilder.BuildError.InsufficientInput != 0 ||
                (int)StructureBuilder.BuildError.InvalidInput != 1 ||
                (int)StructureBuilder.BuildError.InvalidRoomLocation != 2 ||
                (int)StructureBuilder.BuildError.ExceedSceneSizeLimit != 3 ||
                (int)StructureBuilder.BuildError.DeviceNotSupported != 4 ||
                (int)StructureBuilder.BuildError.InternalError != 5)
                throw new InvalidOperationException("StructureBuilder.BuildError case values mismatch");
            Pass("StructureBuilder.BuildError case values");
        }
        catch (Exception ex)
        {
            Fail("StructureBuilder.BuildError case values", ex.Message);
        }

        // CapturedRoom.Error plain-enum values.
        try
        {
            if ((int)CapturedRoom.Error.UrlInvalidScheme != 0 ||
                (int)CapturedRoom.Error.UrlInvalidFilePath != 1 ||
                (int)CapturedRoom.Error.UrlMissingFileExtension != 2 ||
                (int)CapturedRoom.Error.UrlInvalidFileExtension != 3 ||
                (int)CapturedRoom.Error.DeviceNotSupported != 4)
                throw new InvalidOperationException("CapturedRoom.Error case values mismatch");
            Pass("CapturedRoom.Error case values");
        }
        catch (Exception ex)
        {
            Fail("CapturedRoom.Error case values", ex.Message);
        }

        // CapturedRoom.Confidence plain-enum values.
        try
        {
            if ((int)CapturedRoom.Confidence.High != 0 ||
                (int)CapturedRoom.Confidence.Medium != 1 ||
                (int)CapturedRoom.Confidence.Low != 2)
                throw new InvalidOperationException("CapturedRoom.Confidence case values mismatch");
            Pass("CapturedRoom.Confidence case values");
        }
        catch (Exception ex)
        {
            Fail("CapturedRoom.Confidence case values", ex.Message);
        }

        // CapturedElementCategory.CaseTag — discriminated union Surface=0, Object=1.
        try
        {
            if ((uint)CapturedElementCategory.CaseTag.Surface != 0u ||
                (uint)CapturedElementCategory.CaseTag.Object != 1u)
                throw new InvalidOperationException("CapturedElementCategory.CaseTag values mismatch");
            Pass("CapturedElementCategory.CaseTag values");
        }
        catch (Exception ex)
        {
            Fail("CapturedElementCategory.CaseTag values", ex.Message);
        }

        // CapturedRoom.Surface.CategoryType.CaseTag — Door=0..Floor=4.
        try
        {
            if ((uint)CapturedRoom.Surface.CategoryType.CaseTag.Door != 0u ||
                (uint)CapturedRoom.Surface.CategoryType.CaseTag.Wall != 1u ||
                (uint)CapturedRoom.Surface.CategoryType.CaseTag.Opening != 2u ||
                (uint)CapturedRoom.Surface.CategoryType.CaseTag.Window != 3u ||
                (uint)CapturedRoom.Surface.CategoryType.CaseTag.Floor != 4u)
                throw new InvalidOperationException("Surface.CategoryType.CaseTag values mismatch");
            Pass("CapturedRoom.Surface.CategoryType.CaseTag values");
        }
        catch (Exception ex)
        {
            Fail("CapturedRoom.Surface.CategoryType.CaseTag values", ex.Message);
        }

        // CapturedRoom.Section.LabelType.CaseTag — LivingRoom=0..Unidentified=5.
        try
        {
            if ((uint)CapturedRoom.Section.LabelType.CaseTag.LivingRoom != 0u ||
                (uint)CapturedRoom.Section.LabelType.CaseTag.Bedroom != 1u ||
                (uint)CapturedRoom.Section.LabelType.CaseTag.Bathroom != 2u ||
                (uint)CapturedRoom.Section.LabelType.CaseTag.Kitchen != 3u ||
                (uint)CapturedRoom.Section.LabelType.CaseTag.DiningRoom != 4u ||
                (uint)CapturedRoom.Section.LabelType.CaseTag.Unidentified != 5u)
                throw new InvalidOperationException("Section.LabelType.CaseTag values mismatch");
            Pass("CapturedRoom.Section.LabelType.CaseTag values");
        }
        catch (Exception ex)
        {
            Fail("CapturedRoom.Section.LabelType.CaseTag values", ex.Message);
        }

        // CapturedRoom.ModelProvider.Error.CaseTag — NonExistingFile=0, AttributeCombinationNotSupported=1.
        try
        {
            if ((uint)CapturedRoom.ModelProvider.Error.CaseTag.NonExistingFile != 0u ||
                (uint)CapturedRoom.ModelProvider.Error.CaseTag.AttributeCombinationNotSupported != 1u)
                throw new InvalidOperationException("ModelProvider.Error.CaseTag values mismatch");
            Pass("CapturedRoom.ModelProvider.Error.CaseTag values");
        }
        catch (Exception ex)
        {
            Fail("CapturedRoom.ModelProvider.Error.CaseTag values", ex.Message);
        }

        // ChairType.CaseTag — Dining=0, Stool=1, Swivel=2, Unidentified=3.
        // Access singleton to force lazy init and verify CaseTag round-trips.
        try
        {
            var dining = ChairType.Dining;
            if (dining.Tag != ChairType.CaseTag.Dining)
                throw new InvalidOperationException($"ChairType.Dining tag = {dining.Tag}, expected Dining");
            var stool = ChairType.Stool;
            if (stool.Tag != ChairType.CaseTag.Stool)
                throw new InvalidOperationException($"ChairType.Stool tag = {stool.Tag}, expected Stool");
            Pass("ChairType.CaseTag round-trip");
        }
        catch (Exception ex)
        {
            Fail("ChairType.CaseTag round-trip", ex.Message);
        }

        // SofaType.CaseTag — Rectangular=0, LShaped=1, Unidentified=4.
        try
        {
            var rectangular = SofaType.Rectangular;
            if (rectangular.Tag != SofaType.CaseTag.Rectangular)
                throw new InvalidOperationException($"SofaType.Rectangular tag = {rectangular.Tag}, expected Rectangular");
            var lShaped = SofaType.LShaped;
            if (lShaped.Tag != SofaType.CaseTag.LShaped)
                throw new InvalidOperationException($"SofaType.LShaped tag = {lShaped.Tag}, expected LShaped");
            Pass("SofaType.CaseTag round-trip");
        }
        catch (Exception ex)
        {
            Fail("SofaType.CaseTag round-trip", ex.Message);
        }

        // TableType.CaseTag — Coffee=0, Dining=1, Unidentified=2.
        try
        {
            var coffee = TableType.Coffee;
            if (coffee.Tag != TableType.CaseTag.Coffee)
                throw new InvalidOperationException($"TableType.Coffee tag = {coffee.Tag}, expected Coffee");
            var dining = TableType.Dining;
            if (dining.Tag != TableType.CaseTag.Dining)
                throw new InvalidOperationException($"TableType.Dining tag = {dining.Tag}, expected Dining");
            Pass("TableType.CaseTag round-trip");
        }
        catch (Exception ex)
        {
            Fail("TableType.CaseTag round-trip", ex.Message);
        }

        // StorageType.CaseTag — Cabinet=0, Shelf=1.
        try
        {
            var cabinet = StorageType.Cabinet;
            if (cabinet.Tag != StorageType.CaseTag.Cabinet)
                throw new InvalidOperationException($"StorageType.Cabinet tag = {cabinet.Tag}, expected Cabinet");
            var shelf = StorageType.Shelf;
            if (shelf.Tag != StorageType.CaseTag.Shelf)
                throw new InvalidOperationException($"StorageType.Shelf tag = {shelf.Tag}, expected Shelf");
            Pass("StorageType.CaseTag round-trip");
        }
        catch (Exception ex)
        {
            Fail("StorageType.CaseTag round-trip", ex.Message);
        }

        // RoomBuilder.BuildError.GetErrorDescription — extension cdecl round-trip.
        try
        {
            var desc = RoomBuilderBuildErrorExtensions.GetErrorDescription(
                RoomBuilder.BuildError.InsufficientInput);
            Log($"RoomBuilder.BuildError.InsufficientInput description = {desc}");
            Pass("RoomBuilder.BuildError.GetErrorDescription");
        }
        catch (Exception ex)
        {
            Fail("RoomBuilder.BuildError.GetErrorDescription", ex.Message);
        }

        // StructureBuilder.BuildError.GetErrorDescription — extension cdecl round-trip.
        try
        {
            var desc = StructureBuilderBuildErrorExtensions.GetErrorDescription(
                StructureBuilder.BuildError.InsufficientInput);
            Log($"StructureBuilder.BuildError.InsufficientInput description = {desc}");
            Pass("StructureBuilder.BuildError.GetErrorDescription");
        }
        catch (Exception ex)
        {
            Fail("StructureBuilder.BuildError.GetErrorDescription", ex.Message);
        }

        // CapturedRoom.Error.GetErrorDescription — extension cdecl round-trip.
        try
        {
            var desc = CapturedRoomErrorExtensions.GetErrorDescription(
                CapturedRoom.Error.UrlInvalidScheme);
            Log($"CapturedRoom.Error.UrlInvalidScheme description = {desc}");
            Pass("CapturedRoom.Error.GetErrorDescription");
        }
        catch (Exception ex)
        {
            Fail("CapturedRoom.Error.GetErrorDescription", ex.Message);
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
