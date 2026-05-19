// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#if MACOS
using Nuke;
using Swift.Runtime;

namespace NukeSimTests;

// macOS smoke test. The full UIKit test app under Program.UIKit.cs exercises
// image-loading workflows that depend on UIImage, which isn't available here.
// This entry point keeps the macOS slice honest by loading Swift metadata for
// a representative cross-section of Nuke types — proving the macos-arm64_x86_64
// xcframework slice loads, the wrapper resolves, and the runtime can talk to
// the Nuke Swift module on macOS.
internal static class MacEntry
{
    static int Main()
    {
        int passed = 0, failed = 0;

        void Check<T>(string name) where T : ISwiftObject
        {
            try
            {
                var md = SwiftObjectHelper<T>.GetTypeMetadata();
                if (md.Handle == IntPtr.Zero || md.Size == 0)
                    throw new InvalidOperationException($"metadata invalid (handle={md.Handle}, size={md.Size})");
                Console.WriteLine($"PASS: {name} (size={md.Size})");
                passed++;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"FAIL: {name} — {ex.Message}");
                failed++;
            }
        }

        Check<ImagePipeline>("ImagePipeline");
        Check<ImagePipeline.ConfigurationType>("ImagePipeline.ConfigurationType");
        Check<ImageRequest>("ImageRequest");
        Check<ImageCache>("ImageCache");
        Check<DataLoader>("DataLoader");
        Check<ImageTask>("ImageTask");

        Console.WriteLine($"Results: {passed} passed, {failed} failed");
        if (failed == 0)
        {
            Console.WriteLine("TEST SUCCESS");
            Console.Out.Flush();
            return 0;
        }

        Console.WriteLine($"TEST FAILED: {failed} failures");
        Console.Out.Flush();
        return 1;
    }
}
#endif
