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
        // The window lives on the UIKit scene (Info.plist scene manifest; Xcode 27 MT7199).
        global::SwiftBindings.Testing.SwiftBindingsSceneDelegate.Install(() => new TestMainViewController());
        return true;
    }
}

public class TestMainViewController : UIViewController
{
#if IOS
    private bool _liveScanStarted;
#endif

    public override void ViewDidLoad()
    {
        base.ViewDidLoad();
        View!.BackgroundColor = UIColor.White;

#if IOS
        // The attended camera scenario replaces the unattended suite for this launch; it needs
        // the view on screen to present the scanner, so it starts from ViewDidAppear instead.
        if (LiveScanScenario.IsRequested)
        {
            var label = new UILabel(View.Bounds)
            {
                Text = "Live QR scan scenario",
                TextAlignment = UITextAlignment.Center,
                AutoresizingMask = UIViewAutoresizing.FlexibleDimensions,
            };
            View.AddSubview(label);
            return;
        }
#endif

        // Started, not awaited: the suite awaits MainActor-isolated VisionKit members, whose
        // continuations need the main run loop to keep turning. Blocking ViewDidLoad on the
        // result would deadlock those awaits. RunAsync reports its own outcome to stdout.
        _ = Tests.RunAsync();
    }

#if IOS
    public override void ViewDidAppear(bool animated)
    {
        base.ViewDidAppear(animated);
        if (!LiveScanScenario.IsRequested || _liveScanStarted)
            return;
        _liveScanStarted = true;
        LiveScanScenario.Start(this);
    }
#endif
}
#endif
