// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#if IOS || TVOS || MACCATALYST
#nullable enable
using System;
using System.Diagnostics.CodeAnalysis;
using Foundation;
using UIKit;

namespace SwiftBindings.Testing;

/// <summary>
/// The UIKit scene delegate every test app's Info.plist names (UIApplicationSceneManifest →
/// UISceneDelegateClassName "SceneDelegate"). Xcode 27's workloads refuse an iOS or Mac Catalyst
/// app without a scene manifest (MT7199) because on iOS/macOS 27 such an app crashes at launch,
/// so the window each app used to create in FinishedLaunching is created here, on the scene.
/// Imported into every *.Tests project by Directory.Build.targets; an app hands over its root view
/// controller with <see cref="Install"/> from FinishedLaunching, which runs before any scene connects.
/// </summary>
[Register("SceneDelegate")]
public sealed class SwiftBindingsSceneDelegate : UIWindowSceneDelegate
{
    static Func<UIViewController>? s_rootViewControllerFactory;

    public override UIWindow? Window { get; set; }

    /// <summary>Set the factory for the root view controller of the app's window.</summary>
    /// <remarks>UIKit instantiates this class by its Objective-C name from Info.plist, which the
    /// trimmer cannot see, so the dependency keeps it whole in NativeAOT and trimmed builds.</remarks>
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(SwiftBindingsSceneDelegate))]
    public static void Install(Func<UIViewController> rootViewControllerFactory) =>
        s_rootViewControllerFactory = rootViewControllerFactory;

    public override void WillConnect(UIScene scene, UISceneSession session, UISceneConnectionOptions connectionOptions)
    {
        if (scene is not UIWindowScene windowScene)
            return;

        Window = new UIWindow(windowScene)
        {
            RootViewController = s_rootViewControllerFactory?.Invoke() ?? new UIViewController(),
        };
        Window.MakeKeyAndVisible();
    }
}
#endif
