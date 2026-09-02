// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#if IOS || MACCATALYST
using Foundation;
using UIKit;

namespace SwiftBindings.VisionKit.Tests;

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

        // Started, not awaited: the suite awaits MainActor-isolated VisionKit members, whose
        // continuations need the main run loop to keep turning. Blocking ViewDidLoad on the
        // result would deadlock those awaits. RunAsync reports its own outcome to stdout.
        _ = Tests.RunAsync();
    }
}
#endif
