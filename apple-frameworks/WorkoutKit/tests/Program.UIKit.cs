// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#if IOS || MACCATALYST
using Foundation;
using UIKit;

namespace SwiftBindings.WorkoutKit.Tests;

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
        // The window lives on the UIKit scene (Info.plist scene manifest; Xcode 27 MT7199).
        global::SwiftBindings.Testing.SwiftBindingsSceneDelegate.Install(() => new TestMainViewController());
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
