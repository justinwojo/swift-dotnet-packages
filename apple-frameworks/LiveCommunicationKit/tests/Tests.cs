// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using LiveCommunicationKit;
using Swift.Runtime;

namespace SwiftBindings.LiveCommunicationKit.Tests;

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

#if IOS
        // Conversation is the core class — ARC-managed Swift class wrapper.
        // Loading its metadata exercises the class protocol fallback path.
        MetadataTest<Conversation>("Conversation metadata");
        MetadataTest<Conversation.Event>("Conversation.Event metadata");
        MetadataTest<Conversation.Update>("Conversation.Update metadata");
        MetadataTest<Conversation.Capabilities>("Conversation.Capabilities metadata");

        // Action struct wrappers — struct metadata path.
        MetadataTest<ConversationAction>("ConversationAction metadata");
        MetadataTest<StartConversationAction>("StartConversationAction metadata");
        MetadataTest<StartCellularConversationAction>("StartCellularConversationAction metadata");
        MetadataTest<JoinConversationAction>("JoinConversationAction metadata");
        MetadataTest<EndConversationAction>("EndConversationAction metadata");
        MetadataTest<MergeConversationAction>("MergeConversationAction metadata");
        MetadataTest<UnmergeConversationAction>("UnmergeConversationAction metadata");
        MetadataTest<MuteConversationAction>("MuteConversationAction metadata");
        MetadataTest<PauseConversationAction>("PauseConversationAction metadata");
        MetadataTest<PlayToneAction>("PlayToneAction metadata");
        MetadataTest<SetTranslatingAction>("SetTranslatingAction metadata");

        // Supporting types
        MetadataTest<Handle>("Handle metadata");
        MetadataTest<CellularService>("CellularService metadata");

        // Plain enum values on SetTranslatingAction.TranslationEngine.
        try
        {
            if ((int)SetTranslatingAction.TranslationEngine.Default != 0 ||
                (int)SetTranslatingAction.TranslationEngine.Custom != 1)
                throw new InvalidOperationException("TranslationEngine values mismatch");
            Pass("SetTranslatingAction.TranslationEngine values");
        }
        catch (Exception ex)
        {
            Fail("SetTranslatingAction.TranslationEngine values", ex.Message);
        }

        // Reverse dispatch: Swift calling back INTO a C# implementation. ConversationManager's
        // delegate sink is a class-bound existential on a `weak var`, so every call crosses the
        // two-word class-bound container, the protocol witness table, and the generated cdecl
        // receiver before reaching the implementation.
        //
        // The route is: assign the implementation, read the property back — the getter hands out
        // a SWIFT-BACKED proxy over the container Swift is actually holding, not the
        // implementation object — and then call a requirement on that proxy. A proxy built
        // directly around a C# implementation short-circuits to it without involving Swift at
        // all, so only the read-back proxy exercises the round trip. LiveCommunicationKit itself
        // calls this delegate only for real system call events, which a test host never receives.
        ConversationManager? manager = null;
        var listener = new ManagerDelegate();
        try
        {
            using var configuration = new ConversationManager.ConfigurationInfo(
                ringtoneName: null,
                iconTemplateImageData: null,
                maximumConversationGroups: 1,
                maximumConversationsPerConversationGroup: 1,
                includesConversationInRecents: false,
                supportsVideo: false,
                supportedHandleTypes: new HashSet<Handle.Kind> { Handle.Kind.Generic });
            manager = new ConversationManager(configuration);
            manager.Delegate = listener;
            var readBack = manager.Delegate;
            if (readBack is null)
                throw new InvalidOperationException("Delegate read back null while the assigned implementation was still alive");
            if (ReferenceEquals(readBack, listener))
                throw new InvalidOperationException(
                    "Delegate read back the implementation object itself; the round trip needs the Swift-backed proxy over the container Swift holds");
            Log($"ConversationManager.Delegate set to a C# implementation and read back as {readBack.GetType().Name}");
            (readBack as IDisposable)?.Dispose();
            Pass("ConversationManager.Delegate assignment (existential marshalling)");
        }
        catch (Exception ex)
        {
            Fail("ConversationManager.Delegate assignment (existential marshalling)", $"{ex.GetType().Name}: {ex.Message}");
        }

        if (manager is null)
        {
            Skip("IConversationManagerDelegate reverse dispatch (every dispatchable requirement)",
                "no ConversationManager could be constructed to hold the delegate");
            Skip("IConversationManagerDelegate dispatch after the read-back proxy is finalized",
                "no ConversationManager could be constructed to hold the delegate");
            Skip("ConversationManager.Delegate survives a collection (weak sink)",
                "no ConversationManager could be constructed to hold the delegate");
            Skip("IConversationManagerDelegate non-dispatchable requirements refuse cleanly",
                "no ConversationManager could be constructed to hold the delegate");
        }
        else
        {
            // Four of the seven requirements are dispatchable end to end. The two action-carrying
            // ones are driven with a ConversationAction built from a known UUID, so the argument's
            // identity can be checked on the far side rather than only its presence.
            try
            {
                var proxy = manager.Delegate;
                if (proxy is null)
                    throw new InvalidOperationException("Delegate read back null before the dispatch pass");

                proxy.ConversationManagerDidBegin(manager);
                if (listener.DidBeginCallbacks != 1)
                    throw new InvalidOperationException(
                        $"conversationManagerDidBegin did not reach the implementation: calls={listener.DidBeginCallbacks}");

                proxy.ConversationManagerDidReset(manager);
                if (listener.DidResetCallbacks != 1)
                    throw new InvalidOperationException(
                        $"conversationManagerDidReset did not reach the implementation: calls={listener.DidResetCallbacks}");

                var performUuid = Guid.NewGuid();
                using (var action = new ConversationAction(performUuid))
                {
                    proxy.ConversationManagerPerform(manager, action);
                }
                if (listener.PerformCallbacks != 1)
                    throw new InvalidOperationException(
                        $"conversationManager(_:perform:) did not reach the implementation: calls={listener.PerformCallbacks}");
                if (listener.LastActionConversationUuid != performUuid)
                    throw new InvalidOperationException(
                        $"the action arrived with conversationUUID {listener.LastActionConversationUuid}, expected {performUuid}");

                var timeoutUuid = Guid.NewGuid();
                using (var action = new ConversationAction(timeoutUuid))
                {
                    proxy.ConversationManagerTimedOutPerforming(manager, action);
                }
                if (listener.TimedOutCallbacks != 1)
                    throw new InvalidOperationException(
                        $"conversationManager(_:timedOutPerforming:) did not reach the implementation: calls={listener.TimedOutCallbacks}");
                if (listener.LastActionConversationUuid != timeoutUuid)
                    throw new InvalidOperationException(
                        $"the timed-out action arrived with conversationUUID {listener.LastActionConversationUuid}, expected {timeoutUuid}");

                if (listener.LastManagerHandle == IntPtr.Zero)
                    throw new InvalidOperationException("the implementation received a null receiver");
                if (listener.LastManagerHandle != manager.Payload.DangerousGetHandle())
                    throw new InvalidOperationException(
                        "the receiver handed to the implementation wraps a different Swift instance from the manager under test");

                Log("all four dispatchable requirements reached C# through the read-back proxy: " +
                    "didBegin, didReset, perform and timedOutPerforming; both actions carried their conversationUUID " +
                    "intact and the receiver wraps the same Swift instance");
                (proxy as IDisposable)?.Dispose();
                Pass("IConversationManagerDelegate reverse dispatch (every dispatchable requirement)");
            }
            catch (Exception ex)
            {
                Fail("IConversationManagerDelegate reverse dispatch (every dispatchable requirement)",
                    $"{ex.GetType().Name}: {ex.Message}");
            }

            // The read-back proxy adopts the returned existential at +1 and releases it when it
            // is collected. Dropping one without disposing, running finalizers, then reading and
            // dispatching again catches an over-release of the container Swift still holds.
            try
            {
                var dropped = manager.Delegate;
                if (dropped is null)
                    throw new InvalidOperationException("Delegate read back null before the finalizer pass");
                dropped = null;

                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();

                var second = manager.Delegate;
                if (second is null)
                    throw new InvalidOperationException("Delegate read back null after the first proxy was finalized");
                int before = listener.DidBeginCallbacks;
                second.ConversationManagerDidBegin(manager);
                if (listener.DidBeginCallbacks != before + 1)
                    throw new InvalidOperationException(
                        $"the second dispatch did not reach the implementation: {before} -> {listener.DidBeginCallbacks}");
                Log($"a finalized read-back proxy left the sink intact: second dispatch reached callback #{listener.DidBeginCallbacks}");
                (second as IDisposable)?.Dispose();
                Pass("IConversationManagerDelegate dispatch after the read-back proxy is finalized");
            }
            catch (Exception ex)
            {
                Fail("IConversationManagerDelegate dispatch after the read-back proxy is finalized",
                    $"{ex.GetType().Name}: {ex.Message}");
            }

            // The sink is a Swift `weak var`. Nothing on the C# side holds the proxy Swift's weak
            // reference points at once the assignment expression is over, so unless the binding
            // roots it for as long as the manager lives, a collection nils the sink and the
            // delegate silently stops firing. The consumer's own implementation stays alive here,
            // exactly as a real consumer would keep it.
            try
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                GC.WaitForPendingFinalizers();

                var survivor = manager.Delegate;
                if (survivor is null)
                    throw new InvalidOperationException(
                        "Delegate read back null after a collection — the weak sink lost its proxy while the implementation was still alive");
                int before = listener.DidBeginCallbacks;
                survivor.ConversationManagerDidBegin(manager);
                if (listener.DidBeginCallbacks != before + 1)
                    throw new InvalidOperationException(
                        $"the surviving delegate no longer dispatches to the original implementation: {before} -> {listener.DidBeginCallbacks}");
                Log($"the weak delegate sink survived two collections and still dispatches to the original implementation (callback #{listener.DidBeginCallbacks})");
                (survivor as IDisposable)?.Dispose();
                Pass("ConversationManager.Delegate survives a collection (weak sink)");
            }
            catch (Exception ex)
            {
                Fail("ConversationManager.Delegate survives a collection (weak sink)",
                    $"{ex.GetType().Name}: {ex.Message}");
            }

            // The two audio-session requirements carry an AVFoundation class the binding marks
            // non-dispatchable, so they must refuse cleanly on a Swift-backed proxy rather than
            // marshalling something wrong.
            try
            {
                var proxy = manager.Delegate;
                if (proxy is null)
                    throw new InvalidOperationException("Delegate read back null before the refusal pass");
                var audioSession = AVFoundation.AVAudioSession.SharedInstance();
                int callbacksBefore = listener.Callbacks;
                int refusals = 0;
                void ExpectRefusal(string member, Action call)
                {
                    try
                    {
                        call();
                        throw new InvalidOperationException(
                            $"'{member}' returned normally on a Swift-backed proxy; a non-dispatchable member must refuse");
                    }
                    catch (NotSupportedException)
                    {
                        refusals++;
                    }
                }

#pragma warning disable SB0003 // the refusal is what is under test
                ExpectRefusal(nameof(IConversationManagerDelegate.ConversationManagerDidActivate),
                    () => proxy.ConversationManagerDidActivate(manager, audioSession));
                ExpectRefusal(nameof(IConversationManagerDelegate.ConversationManagerDidDeactivate),
                    () => proxy.ConversationManagerDidDeactivate(manager, audioSession));
#pragma warning restore SB0003

                if (refusals != 2)
                    throw new InvalidOperationException($"expected 2 clean refusals, counted {refusals}");
                if (listener.Callbacks != callbacksBefore)
                    throw new InvalidOperationException(
                        $"a refused member still reached the implementation: {listener.Callbacks} callbacks vs {callbacksBefore} before the pass");
                Log("the two AVAudioSession requirements refused with NotSupportedException on the Swift-backed proxy and never reached C#");
                (proxy as IDisposable)?.Dispose();
                Pass("IConversationManagerDelegate non-dispatchable requirements refuse cleanly");
            }
            catch (Exception ex)
            {
                Fail("IConversationManagerDelegate non-dispatchable requirements refuse cleanly",
                    $"{ex.GetType().Name}: {ex.Message}");
            }

            GC.KeepAlive(listener);
            manager.Dispose();
        }

        // conversationManager(_:conversationChanged:) is dispatchable, but its argument is not
        // constructible: Conversation has no public initializer, and the manager's own
        // Conversations collection is empty until the system reports a real call, which a test
        // host never receives. It is left undriven rather than driven with a fabricated argument.
        Skip("IConversationManagerDelegate conversationManager(_:conversationChanged:)",
            "Conversation has no public initializer and ConversationManager.Conversations is empty without a real system call");
#endif

        // Summary
        Log($"Results: {passed} passed, {failed} failed, {skipped} skipped");
        if (failed == 0)
            Log("TEST SUCCESS", prefixed: false);
        else
            Log($"TEST FAILED: {failed} failures", prefixed: false);
        return failed;
    }

#if IOS
    // Records what Swift hands back across the class-bound existential: how often each
    // requirement fired, which Swift instance the receiver wraps, and the identity of the
    // action argument.
    private sealed class ManagerDelegate : IConversationManagerDelegate
    {
        public int Callbacks { get; private set; }
        public int DidBeginCallbacks { get; private set; }
        public int DidResetCallbacks { get; private set; }
        public int PerformCallbacks { get; private set; }
        public int TimedOutCallbacks { get; private set; }
        public IntPtr LastManagerHandle { get; private set; }
        public Guid LastActionConversationUuid { get; private set; }

        public void ConversationManagerDidBegin(ConversationManager manager)
        {
            Record(manager);
            DidBeginCallbacks++;
        }

        public void ConversationManagerDidReset(ConversationManager manager)
        {
            Record(manager);
            DidResetCallbacks++;
        }

        public void ConversationManagerPerform(ConversationManager manager, ConversationAction action)
        {
            Record(manager);
            PerformCallbacks++;
            LastActionConversationUuid = action.ConversationUUID;
        }

        public void ConversationManagerTimedOutPerforming(ConversationManager manager, ConversationAction action)
        {
            Record(manager);
            TimedOutCallbacks++;
            LastActionConversationUuid = action.ConversationUUID;
        }

        public void ConversationManagerConversationChanged(ConversationManager manager, Conversation conversation) =>
            Record(manager);

        public void ConversationManagerDidActivate(ConversationManager manager, AVFoundation.AVAudioSession audioSession) =>
            Record(manager);

        public void ConversationManagerDidDeactivate(ConversationManager manager, AVFoundation.AVAudioSession audioSession) =>
            Record(manager);

        private void Record(ConversationManager manager)
        {
            Callbacks++;
            LastManagerHandle = manager.Payload.DangerousGetHandle();
        }
    }
#endif

    internal static void Log(string msg, bool prefixed = true) =>
        Console.WriteLine(prefixed ? $"[LCK-TEST] {msg}" : msg);
}
