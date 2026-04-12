// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Foundation;
using UIKit;
using LiveCommunicationKit;
using Swift.Runtime;

namespace LiveCommunicationKitSimTests;

public class Application
{
    static void Main(string[] args)
    {
        UIApplication.Main(args, null, typeof(AppDelegate));
    }
}

[Register("AppDelegate")]
public class AppDelegate : UIApplicationDelegate
{
    public override UIWindow? Window { get; set; }

    public override bool FinishedLaunching(UIApplication application, NSDictionary? launchOptions)
    {
        Window = new UIWindow(UIScreen.MainScreen.Bounds);
        Window.RootViewController = new MainViewController();
        Window.MakeKeyAndVisible();
        return true;
    }
}

public class MainViewController : UIViewController
{
    public override void ViewDidLoad()
    {
        base.ViewDidLoad();
        View!.BackgroundColor = UIColor.White;

        int passed = 0, failed = 0;

        void Pass(string name)
        {
            passed++;
            Console.WriteLine($"[LCK-TEST] PASS: {name}");
        }

        void Fail(string name, string error)
        {
            failed++;
            Console.WriteLine($"[LCK-TEST] FAIL: {name} — {error}");
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

        Console.WriteLine($"[LCK-TEST] Results: {passed} passed, {failed} failed");
        if (failed == 0)
            Console.WriteLine("TEST SUCCESS");
        else
            Console.WriteLine($"TEST FAILED: {failed} failures");
    }
}
