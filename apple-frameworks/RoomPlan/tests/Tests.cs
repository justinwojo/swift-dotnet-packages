// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RoomPlan;
using Swift.Runtime;

namespace SwiftBindings.RoomPlan.Tests;

internal static class Tests
{
    /// <summary>
    /// Checks a generated enum (or <c>CaseTag</c>) against the binding's contract: each member's value
    /// identifies its Swift case by name, not by position, so the named members must be defined, no
    /// member may equal <c>default</c> (which names no case), and no two members may share a value.
    /// </summary>
    private static void AssertNameKeyedMembers<T>(params T[] named) where T : struct, Enum
    {
        foreach (var member in named)
        {
            if (!Enum.IsDefined(member))
                throw new InvalidOperationException($"{typeof(T).Name} value {member} is not a defined member");
        }
        var seen = new HashSet<T>();
        foreach (var member in Enum.GetValues<T>())
        {
            if (EqualityComparer<T>.Default.Equals(member, default))
                throw new InvalidOperationException($"{typeof(T).Name}.{member} equals default, which names no case");
            if (!seen.Add(member))
                throw new InvalidOperationException($"{typeof(T).Name}.{member} shares its value with another member");
        }
    }

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

        // Nested plain-enum values: exercise that the generated enum's members are
        // distinct, name-keyed cases.
        try
        {
            AssertNameKeyedMembers(RoomCaptureSession.CaptureError.ExceedSceneSizeLimit,
                RoomCaptureSession.CaptureError.InternalError);
            Pass("RoomCaptureSession.CaptureError values");
        }
        catch (Exception ex)
        {
            Fail("RoomCaptureSession.CaptureError values", ex.Message);
        }

        try
        {
            AssertNameKeyedMembers(RoomCaptureSession.Instruction.MoveCloseToWall,
                RoomCaptureSession.Instruction.LowTexture);
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
            AssertNameKeyedMembers(
                RoomCaptureSession.Instruction.MoveCloseToWall,
                RoomCaptureSession.Instruction.MoveAwayFromWall,
                RoomCaptureSession.Instruction.SlowDown,
                RoomCaptureSession.Instruction.TurnOnLight,
                RoomCaptureSession.Instruction.Normal,
                RoomCaptureSession.Instruction.LowTexture);
            Pass("RoomCaptureSession.Instruction case values");
        }
        catch (Exception ex)
        {
            Fail("RoomCaptureSession.Instruction case values", ex.Message);
        }

        // RoomBuilder.BuildError plain-enum values.
        try
        {
            AssertNameKeyedMembers(
                RoomBuilder.BuildError.InsufficientInput,
                RoomBuilder.BuildError.InvalidInput,
                RoomBuilder.BuildError.ExceedSceneSizeLimit,
                RoomBuilder.BuildError.DeviceNotSupported,
                RoomBuilder.BuildError.InternalError);
            Pass("RoomBuilder.BuildError case values");
        }
        catch (Exception ex)
        {
            Fail("RoomBuilder.BuildError case values", ex.Message);
        }

        // StructureBuilder.BuildError plain-enum values.
        try
        {
            AssertNameKeyedMembers(
                StructureBuilder.BuildError.InsufficientInput,
                StructureBuilder.BuildError.InvalidInput,
                StructureBuilder.BuildError.InvalidRoomLocation,
                StructureBuilder.BuildError.ExceedSceneSizeLimit,
                StructureBuilder.BuildError.DeviceNotSupported,
                StructureBuilder.BuildError.InternalError);
            Pass("StructureBuilder.BuildError case values");
        }
        catch (Exception ex)
        {
            Fail("StructureBuilder.BuildError case values", ex.Message);
        }

        // CapturedRoom.Error plain-enum values.
        try
        {
            AssertNameKeyedMembers(
                CapturedRoom.Error.UrlInvalidScheme,
                CapturedRoom.Error.UrlInvalidFilePath,
                CapturedRoom.Error.UrlMissingFileExtension,
                CapturedRoom.Error.UrlInvalidFileExtension,
                CapturedRoom.Error.DeviceNotSupported);
            Pass("CapturedRoom.Error case values");
        }
        catch (Exception ex)
        {
            Fail("CapturedRoom.Error case values", ex.Message);
        }

        // CapturedRoom.Confidence plain-enum values.
        try
        {
            AssertNameKeyedMembers(CapturedRoom.Confidence.High, CapturedRoom.Confidence.Medium, CapturedRoom.Confidence.Low);
            Pass("CapturedRoom.Confidence case values");
        }
        catch (Exception ex)
        {
            Fail("CapturedRoom.Confidence case values", ex.Message);
        }

        // CapturedElementCategory.CaseTag — discriminated union Surface, Object.
        try
        {
            AssertNameKeyedMembers(CapturedElementCategory.CaseTag.Surface, CapturedElementCategory.CaseTag.Object);
            Pass("CapturedElementCategory.CaseTag values");
        }
        catch (Exception ex)
        {
            Fail("CapturedElementCategory.CaseTag values", ex.Message);
        }

        // CapturedRoom.Surface.CategoryKind.CaseTag — Door..Floor.
        try
        {
            AssertNameKeyedMembers(
                CapturedRoom.Surface.CategoryKind.CaseTag.Door,
                CapturedRoom.Surface.CategoryKind.CaseTag.Wall,
                CapturedRoom.Surface.CategoryKind.CaseTag.Opening,
                CapturedRoom.Surface.CategoryKind.CaseTag.Window,
                CapturedRoom.Surface.CategoryKind.CaseTag.Floor);
            Pass("CapturedRoom.Surface.CategoryKind.CaseTag values");
        }
        catch (Exception ex)
        {
            Fail("CapturedRoom.Surface.CategoryKind.CaseTag values", ex.Message);
        }

        // CapturedRoom.Section.LabelKind.CaseTag — LivingRoom..Unidentified.
        try
        {
            AssertNameKeyedMembers(
                CapturedRoom.Section.LabelKind.CaseTag.LivingRoom,
                CapturedRoom.Section.LabelKind.CaseTag.Bedroom,
                CapturedRoom.Section.LabelKind.CaseTag.Bathroom,
                CapturedRoom.Section.LabelKind.CaseTag.Kitchen,
                CapturedRoom.Section.LabelKind.CaseTag.DiningRoom,
                CapturedRoom.Section.LabelKind.CaseTag.Unidentified);
            Pass("CapturedRoom.Section.LabelKind.CaseTag values");
        }
        catch (Exception ex)
        {
            Fail("CapturedRoom.Section.LabelKind.CaseTag values", ex.Message);
        }

        // CapturedRoom.ModelProvider.Error.CaseTag — NonExistingFile, AttributeCombinationNotSupported.
        try
        {
            AssertNameKeyedMembers(CapturedRoom.ModelProvider.Error.CaseTag.NonExistingFile,
                CapturedRoom.ModelProvider.Error.CaseTag.AttributeCombinationNotSupported);
            Pass("CapturedRoom.ModelProvider.Error.CaseTag values");
        }
        catch (Exception ex)
        {
            Fail("CapturedRoom.ModelProvider.Error.CaseTag values", ex.Message);
        }

        // ChairType.CaseTag — Dining, Stool, Swivel, Unidentified.
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

        // SofaType.CaseTag — Rectangular, LShaped, …, Unidentified.
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

        // TableType.CaseTag — Coffee, Dining, Unidentified.
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

        // StorageType.CaseTag — Cabinet, Shelf.
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

        // Reverse dispatch: Swift calling back INTO a C# implementation. RoomCaptureSession's
        // delegate sink is a class-bound existential on a `weak var`, so every call crosses the
        // two-word class-bound container, the protocol witness table, and the generated cdecl
        // receiver before reaching the implementation.
        //
        // The route is: assign the implementation, read the property back — the getter hands out
        // a SWIFT-BACKED proxy over the container Swift is actually holding, not the
        // implementation object — and then call a requirement on that proxy. A proxy built
        // directly around a C# implementation short-circuits to it without involving Swift at
        // all, so only the read-back proxy exercises the round trip. RoomPlan itself only calls
        // this delegate during a live LiDAR scan, which no simulator can run.
        Log($"RoomCaptureSession.IsSupported = {RoomCaptureSession.IsSupported}");

        RoomCaptureSession? session = null;
        var listener = new CaptureSessionDelegate();
        try
        {
            session = new RoomCaptureSession();
            session.Delegate = listener;
            var readBack = session.Delegate;
            if (readBack is null)
                throw new InvalidOperationException("Delegate read back null while the assigned implementation was still alive");
            if (ReferenceEquals(readBack, listener))
                throw new InvalidOperationException(
                    "Delegate read back the implementation object itself; the round trip needs the Swift-backed proxy over the container Swift holds");
            Log($"RoomCaptureSession.Delegate set to a C# implementation and read back as {readBack.GetType().Name}");
            (readBack as IDisposable)?.Dispose();
            Pass("RoomCaptureSession.Delegate assignment (existential marshalling)");
        }
        catch (Exception ex)
        {
            Fail("RoomCaptureSession.Delegate assignment (existential marshalling)", $"{ex.GetType().Name}: {ex.Message}");
        }

        if (session is null)
        {
            Skip("IRoomCaptureSessionDelegate reverse dispatch (captureSession:didStartWith:)",
                "no RoomCaptureSession could be constructed to hold the delegate");
            Skip("IRoomCaptureSessionDelegate dispatch after the read-back proxy is finalized",
                "no RoomCaptureSession could be constructed to hold the delegate");
            Skip("RoomCaptureSession.Delegate survives a collection (weak sink)",
                "no RoomCaptureSession could be constructed to hold the delegate");
            Skip("IRoomCaptureSessionDelegate non-dispatchable requirements refuse cleanly",
                "no RoomCaptureSession could be constructed to hold the delegate");
        }
        else
        {
            // captureSession(_:didStartWith:) is the one requirement whose whole signature is
            // dispatchable AND whose argument this host can build: Configuration has a public
            // initializer and one readable Bool, so the value can be varied and checked on the
            // far side. Both truth values are sent so a dropped or mis-read field cannot pass by
            // luck — the configuration is a value type, read out of the callback slot by copy.
            try
            {
                var proxy = session.Delegate;
                if (proxy is null)
                    throw new InvalidOperationException("Delegate read back null before the dispatch pass");

                using (var enabled = new RoomCaptureSession.Configuration { IsCoachingEnabled = true })
                {
                    proxy.CaptureSessionDidStartWith(session, enabled);
                }
                if (listener.DidStartCallbacks != 1)
                    throw new InvalidOperationException(
                        $"the call did not reach the C# implementation: DidStartCallbacks={listener.DidStartCallbacks}");
                if (listener.LastCoachingEnabled != true)
                    throw new InvalidOperationException(
                        $"configuration arrived with IsCoachingEnabled={listener.LastCoachingEnabled}, expected True");

                using (var disabled = new RoomCaptureSession.Configuration { IsCoachingEnabled = false })
                {
                    proxy.CaptureSessionDidStartWith(session, disabled);
                }
                if (listener.DidStartCallbacks != 2)
                    throw new InvalidOperationException(
                        $"the second call did not reach the C# implementation: DidStartCallbacks={listener.DidStartCallbacks}");
                if (listener.LastCoachingEnabled != false)
                    throw new InvalidOperationException(
                        $"configuration arrived with IsCoachingEnabled={listener.LastCoachingEnabled}, expected False");

                if (listener.LastSessionHandle == IntPtr.Zero)
                    throw new InvalidOperationException("the implementation received a null receiver");
                if (listener.LastSessionHandle != session.Payload.DangerousGetHandle())
                    throw new InvalidOperationException(
                        "the receiver handed to the implementation wraps a different Swift instance from the session under test");

                Log($"captureSession(_:didStartWith:) dispatched through the read-back proxy into C# twice, " +
                    $"IsCoachingEnabled round-tripped as True then False, receiver wraps the same Swift instance");
                (proxy as IDisposable)?.Dispose();
                Pass("IRoomCaptureSessionDelegate reverse dispatch (captureSession:didStartWith:)");
            }
            catch (Exception ex)
            {
                Fail("IRoomCaptureSessionDelegate reverse dispatch (captureSession:didStartWith:)",
                    $"{ex.GetType().Name}: {ex.Message}");
            }

            // The read-back proxy adopts the returned existential at +1 and releases it when it
            // is collected. Dropping one without disposing, running finalizers, then reading and
            // dispatching again catches an over-release of the container Swift still holds.
            try
            {
                var dropped = session.Delegate;
                if (dropped is null)
                    throw new InvalidOperationException("Delegate read back null before the finalizer pass");
                dropped = null;

                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();

                var second = session.Delegate;
                if (second is null)
                    throw new InvalidOperationException("Delegate read back null after the first proxy was finalized");
                int before = listener.DidStartCallbacks;
                using (var config = new RoomCaptureSession.Configuration())
                {
                    second.CaptureSessionDidStartWith(session, config);
                }
                if (listener.DidStartCallbacks != before + 1)
                    throw new InvalidOperationException(
                        $"the second dispatch did not reach the implementation: {before} -> {listener.DidStartCallbacks}");
                Log($"a finalized read-back proxy left the sink intact: second dispatch reached callback #{listener.DidStartCallbacks}");
                (second as IDisposable)?.Dispose();
                Pass("IRoomCaptureSessionDelegate dispatch after the read-back proxy is finalized");
            }
            catch (Exception ex)
            {
                Fail("IRoomCaptureSessionDelegate dispatch after the read-back proxy is finalized",
                    $"{ex.GetType().Name}: {ex.Message}");
            }

            // The sink is a Swift `weak var`. Nothing on the C# side holds the proxy Swift's weak
            // reference points at once the assignment expression is over, so unless the binding
            // roots it for as long as the session lives, a collection nils the sink and the
            // delegate silently stops firing. The consumer's own implementation stays alive here,
            // exactly as a real consumer would keep it.
            try
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                GC.WaitForPendingFinalizers();

                var survivor = session.Delegate;
                if (survivor is null)
                    throw new InvalidOperationException(
                        "Delegate read back null after a collection — the weak sink lost its proxy while the implementation was still alive");
                int before = listener.DidStartCallbacks;
                using (var config = new RoomCaptureSession.Configuration())
                {
                    survivor.CaptureSessionDidStartWith(session, config);
                }
                if (listener.DidStartCallbacks != before + 1)
                    throw new InvalidOperationException(
                        $"the surviving delegate no longer dispatches to the original implementation: {before} -> {listener.DidStartCallbacks}");
                Log($"the weak delegate sink survived two collections and still dispatches to the original implementation (callback #{listener.DidStartCallbacks})");
                (survivor as IDisposable)?.Dispose();
                Pass("RoomCaptureSession.Delegate survives a collection (weak sink)");
            }
            catch (Exception ex)
            {
                Fail("RoomCaptureSession.Delegate survives a collection (weak sink)",
                    $"{ex.GetType().Name}: {ex.Message}");
            }

            // captureSession(_:didProvide:) carries a nested Instruction value type that the
            // binding marks non-dispatchable, so it must refuse cleanly on a Swift-backed proxy
            // rather than marshalling something wrong.
            try
            {
                var proxy = session.Delegate;
                if (proxy is null)
                    throw new InvalidOperationException("Delegate read back null before the refusal pass");
                int callbacksBefore = listener.Callbacks;
                bool refused = false;
#pragma warning disable SB0003 // the refusal is what is under test
                try
                {
                    proxy.CaptureSessionDidProvide(session, RoomCaptureSession.Instruction.MoveCloseToWall);
                }
                catch (NotSupportedException)
                {
                    refused = true;
                }
#pragma warning restore SB0003
                if (!refused)
                    throw new InvalidOperationException(
                        "'CaptureSessionDidProvide' returned normally on a Swift-backed proxy; a non-dispatchable member must refuse");
                if (listener.Callbacks != callbacksBefore)
                    throw new InvalidOperationException(
                        $"the refused member still reached the implementation: {listener.Callbacks} callbacks vs {callbacksBefore} before the pass");
                Log("captureSession(_:didProvide:) refused with NotSupportedException on the Swift-backed proxy and never reached C#");
                (proxy as IDisposable)?.Dispose();
                Pass("IRoomCaptureSessionDelegate non-dispatchable requirements refuse cleanly");
            }
            catch (Exception ex)
            {
                Fail("IRoomCaptureSessionDelegate non-dispatchable requirements refuse cleanly",
                    $"{ex.GetType().Name}: {ex.Message}");
            }

            GC.KeepAlive(listener);
            session.Dispose();
        }

        // The remaining four requirements each carry a CapturedRoom, and the fifth carries a
        // CapturedRoomData. Neither type has a public initializer — RoomPlan vends them only from
        // a completed LiDAR scan (CapturedRoom.DecodeFromJson wants the exact JSON a real capture
        // produced), and no simulator has the sensor. There is no way to build an argument, so
        // these are not driven here at all rather than driven with a fabricated one.
        Skip("IRoomCaptureSessionDelegate CapturedRoom requirements (didUpdate/didAdd/didChange/didRemove)",
            "CapturedRoom has no public initializer and a simulator has no LiDAR sensor to produce one");
        Skip("IRoomCaptureSessionDelegate captureSession(_:didEndWith:error:)",
            "CapturedRoomData has no public initializer and a simulator has no LiDAR sensor to produce one");

        // Summary
        Log($"Results: {passed} passed, {failed} failed, {skipped} skipped");
        if (failed == 0)
            Log("TEST SUCCESS", prefixed: false);
        else
            Log($"TEST FAILED: {failed} failures", prefixed: false);
        return failed;
    }

    // Every member is implemented rather than left to the generated interface defaults: those
    // defaults throw, and a throw crossing back out of a native frame is not a test failure but
    // a crash. The recorded receiver handle and configuration value are what the reverse-dispatch
    // tests assert on.
    private sealed class CaptureSessionDelegate : IRoomCaptureSessionDelegate
    {
        public int Callbacks { get; private set; }

        public int DidStartCallbacks { get; private set; }

        public IntPtr LastSessionHandle { get; private set; }

        public bool? LastCoachingEnabled { get; private set; }

        public void CaptureSessionDidUpdate(RoomCaptureSession session, CapturedRoom room) => Record(session);

        public void CaptureSessionDidAdd(RoomCaptureSession session, CapturedRoom room) => Record(session);

        public void CaptureSessionDidChange(RoomCaptureSession session, CapturedRoom room) => Record(session);

        public void CaptureSessionDidRemove(RoomCaptureSession session, CapturedRoom room) => Record(session);

        public void CaptureSessionDidProvide(RoomCaptureSession session, RoomCaptureSession.Instruction instruction) =>
            Record(session);

        public void CaptureSessionDidStartWith(RoomCaptureSession session, RoomCaptureSession.Configuration configuration)
        {
            Record(session);
            DidStartCallbacks++;
            // Read through the argument inside the callback: a value read back out of it proves
            // the configuration arrived usable, not merely non-null.
            LastCoachingEnabled = configuration.IsCoachingEnabled;
        }

        public void CaptureSessionDidEndWithError(RoomCaptureSession session, CapturedRoomData data,
            Swift.Foundation.AnyError? error) => Record(session);

        private void Record(RoomCaptureSession session)
        {
            Callbacks++;
            LastSessionHandle = session.Payload.DangerousGetHandle();
        }
    }

    internal static void Log(string msg, bool prefixed = true) =>
        Console.WriteLine(prefixed ? $"[ROOMPLAN-TEST] {msg}" : msg);
}
