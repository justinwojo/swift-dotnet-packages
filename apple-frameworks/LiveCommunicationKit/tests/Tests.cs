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
#endif

        // Summary
        Log($"Results: {passed} passed, {failed} failed, {skipped} skipped");
        if (failed == 0)
            Log("TEST SUCCESS", prefixed: false);
        else
            Log($"TEST FAILED: {failed} failures", prefixed: false);
        return failed;
    }

    internal static void Log(string msg, bool prefixed = true) =>
        Console.WriteLine(prefixed ? $"[LCK-TEST] {msg}" : msg);
}
