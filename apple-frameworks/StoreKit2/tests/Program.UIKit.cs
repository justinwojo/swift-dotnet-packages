// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#if IOS || TVOS || MACCATALYST
using Foundation;
using UIKit;

namespace SwiftBindings.StoreKit2.Tests;

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
        Tests.Run();
    }
}
#endif
