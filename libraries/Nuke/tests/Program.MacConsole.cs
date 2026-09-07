// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#if MACOS
using Nuke;
using Swift.Runtime;

namespace NukeSimTests;

// macOS entry point. Metadata loading proves the macos-arm64_x86_64 xcframework
// slice resolves, but it says nothing about whether an image can actually be
// loaded and decoded on this platform — so the metadata checks are only a
// preamble here, and the substance is a real load through ImagePipeline of an
// image this process generated and serves to itself over loopback.
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
        Check<ImagePipeline.ConfigurationInfo>("ImagePipeline.ConfigurationInfo");
        Check<ImageRequest>("ImageRequest");
        Check<ImageCache>("ImageCache");
        Check<DataLoader>("DataLoader");
        Check<ImageTask>("ImageTask");

        RunImageLoadTests(ref passed, ref failed);

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

    private static void RunImageLoadTests(ref int passed, ref int failed)
    {
        int localPassed = 0, localFailed = 0;

        void Pass(string message)
        {
            Console.WriteLine($"PASS: {message}");
            localPassed++;
        }

        void Fail(string message)
        {
            Console.WriteLine($"FAIL: {message}");
            localFailed++;
        }

        try
        {
            using var server = new LoopbackHttpServer();
            var png = ImageFixtures.SolidPng(24, 16, 0.0, 0.0, 1.0);
            server.Map("/mac.png", new LoopbackResponse { ContentType = "image/png", Body = png });

            var pipeline = ImagePipeline.Shared;
            // Bounded: a hung load must fail the run rather than stall it forever.
            using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(30));
            var image = pipeline
                .ImageAsync(new ImageRequest(server.Url("/mac.png")), cts.Token)
                .GetAwaiter()
                .GetResult();

            if (image is null)
            {
                Fail("ImagePipeline.ImageAsync returned null on macOS");
            }
            else
            {
                var px = ImageFixtures.CenterPixel(image);
                bool sized = (int)image.Size.Width == 24 && (int)image.Size.Height == 16;
                bool blue = px.B > 240 && px.R < 15 && px.G < 15 && px.A == 255;
                if (sized && blue)
                    Pass($"macOS ImagePipeline load decoded 24x16 with centre pixel {px}");
                else
                    Fail($"macOS ImagePipeline load produced {image.Size.Width}x{image.Size.Height} centre pixel {px}, expected 24x16 solid blue");

                if (server.HitsFor("/mac.png") == 1)
                    Pass("macOS load hit the loopback server exactly once");
                else
                    Fail($"macOS load produced {server.HitsFor("/mac.png")} server hits, expected exactly 1");
            }

            // A second load of the same URL must be served from the pipeline's cache.
            var beforeSecond = server.HitsFor("/mac.png");
            var second = pipeline
                .ImageAsync(new ImageRequest(server.Url("/mac.png")), cts.Token)
                .GetAwaiter()
                .GetResult();
            if (second is not null && server.HitsFor("/mac.png") == beforeSecond)
                Pass("macOS second load was served from cache without touching the network");
            else
                Fail($"macOS second load: image={(second is null ? "null" : "ok")} hits {beforeSecond} -> {server.HitsFor("/mac.png")}");
        }
        catch (Exception ex)
        {
            Fail($"macOS image load: {ex.GetType().Name}: {ex.Message}");
        }

        passed += localPassed;
        failed += localFailed;
    }
}
#endif
