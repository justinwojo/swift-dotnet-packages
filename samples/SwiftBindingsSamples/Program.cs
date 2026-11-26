using System.Runtime.InteropServices;
using UIKit;
using SwiftBindingsSamples.UI;

namespace SwiftBindingsSamples;

public class Program
{
    static void Main(string[] args)
    {
        RegisterDllImportResolvers();
        UIApplication.Main(args, null, typeof(AppDelegate));
    }

    private static void RegisterDllImportResolvers()
    {
        NativeLibrary.SetDllImportResolver(typeof(Program).Assembly, ResolveSwiftFramework);

        // Register for already-loaded assemblies
        foreach (var a in AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => a.GetName().Name?.StartsWith("SwiftBindings.") == true ||
                        a.GetName().Name?.StartsWith("Swift.") == true))
        {
            NativeLibrary.SetDllImportResolver(a, ResolveSwiftFramework);
        }

        // Register for assemblies loaded later (SwiftBindings.Nuke, etc. load on first use)
        AppDomain.CurrentDomain.AssemblyLoad += (_, e) =>
        {
            var name = e.LoadedAssembly.GetName().Name;
            if (name?.StartsWith("SwiftBindings.") == true ||
                name?.StartsWith("Swift.") == true)
            {
                NativeLibrary.SetDllImportResolver(e.LoadedAssembly, ResolveSwiftFramework);
            }
        };
    }

    private static IntPtr ResolveSwiftFramework(string name, System.Reflection.Assembly asm, DllImportSearchPath? path)
    {
        if (NativeLibrary.TryLoad($"@rpath/{name}.framework/{name}", out var handle))
            return handle;
        return IntPtr.Zero;
    }
}

[Register("AppDelegate")]
public class AppDelegate : UIApplicationDelegate
{
    public override UIWindow? Window { get; set; }

    public override bool FinishedLaunching(UIApplication application, NSDictionary? launchOptions)
    {
        // The window lives on the UIKit scene (Info.plist scene manifest; Xcode 27 MT7199).
        global::SwiftBindings.Testing.SwiftBindingsSceneDelegate.Install(
            () => new UINavigationController(new HomeViewController()));

        return true;
    }
}
