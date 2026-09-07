// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#if IOS || TVOS || MACCATALYST
using Foundation;
using UIKit;

namespace SwiftBindings.MusicKit.Tests;

public class Application
{
    static void Main(string[] args) => UIApplication.Main(args, null, typeof(TestAppDelegate));
}

[Register("TestAppDelegate")]
public class TestAppDelegate : UIApplicationDelegate
{
    public override UIWindow? Window { get; set; }

    public override bool FinishedLaunching(UIApplication application, NSDictionary? launchOptions)
    {
        Window = new UIWindow(UIScreen.MainScreen.Bounds);
        Window.RootViewController = new TestMainViewController();
        Window.MakeKeyAndVisible();
        return true;
    }
}

public class TestMainViewController : UIViewController
{
    public override void ViewDidLoad()
    {
        base.ViewDidLoad();
        View!.BackgroundColor = UIColor.White;
        // The runner is async and MUST NOT be blocked on here (no Wait()/GetAwaiter().GetResult()):
        // this is the UIKit main thread, and every bounded await whose continuation is main-thread
        // affine — which is what a Swift async completion hopping back to the main queue produces —
        // would deadlock against a blocked runner. Start it and let the run loop pump continuations.
        _ = RunTestsAsync();
    }

    private static async System.Threading.Tasks.Task RunTestsAsync()
    {
        try
        {
            await Tests.RunAsync();
        }
        catch (Exception ex)
        {
            // An exception escaping the runner is a harness failure, never a silent success:
            // emit the failure marker the validators watch for.
            Tests.Log($"harness exception escaped the test runner: {ex}");
            Tests.Log("TEST FAILED: 1 failures", prefixed: false);
        }
    }
}
#endif
