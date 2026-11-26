// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#if MACOS
#nullable enable
using System;
using System.Threading.Tasks;
using Foundation;

namespace SwiftBindings.Testing;

/// <summary>
/// Entry point for a macOS console test app whose body is asynchronous.
/// </summary>
/// <remarks>
/// From the Xcode 27.2 workloads on, the macOS runtime installs AppKit's synchronization context on
/// the main thread at startup for every app, console apps included (ObjCRuntime.Runtime
/// .InitializePlatform calls NSApplication.InitializeApplication). An async <c>Main</c> blocks the
/// main thread in <c>GetAwaiter().GetResult()</c>, so the body's first context-capturing await
/// posts its continuation to a main thread that never runs again and the app hangs before its first
/// line of output. Running the body here, while the main run loop turns, lets those continuations
/// run on the main thread the way they do in an AppKit app (and in the UIKit test apps). Without
/// the context (the Xcode 26 workloads) the continuations run on the thread pool and the loop only
/// waits. Imported into every macOS *.Tests project by Directory.Build.targets.
/// </remarks>
public static class MacConsoleMain
{
    /// <summary>Run <paramref name="body"/> on the main thread, turning the main run loop until it completes.</summary>
    public static int Run(Func<Task<int>> body)
    {
        var task = body();
        while (!task.IsCompleted)
            NSRunLoop.Main.RunUntil(NSDate.FromTimeIntervalSinceNow(0.05));
        return task.GetAwaiter().GetResult();
    }
}
#endif
