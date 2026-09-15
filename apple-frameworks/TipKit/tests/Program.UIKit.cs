// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#if IOS || TVOS || MACCATALYST
using Foundation;
using UIKit;

namespace SwiftBindings.TipKit.Tests;

public class Application
{
    internal const string CatalystRunTokenArgument = "--swift-bindings-catalyst-run-token";
    internal static string? CatalystRunToken { get; private set; }

    static void Main(string[] args)
    {
#if MACCATALYST
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == CatalystRunTokenArgument)
            {
                CatalystRunToken = args[i + 1];
                break;
            }
        }
#endif
        UIApplication.Main(args, null, typeof(TestAppDelegate));
    }
}

[Register("TestAppDelegate")]
public class TestAppDelegate : UIApplicationDelegate
{
    public override UIWindow? Window { get; set; }

    public override bool FinishedLaunching(UIApplication application, NSDictionary? launchOptions)
    {
#if MACCATALYST
        if (Application.CatalystRunToken is { } runToken)
            Console.WriteLine($"[TIPKIT-CATALYST] MANAGED ENTRY run_token={runToken}");
#endif
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
