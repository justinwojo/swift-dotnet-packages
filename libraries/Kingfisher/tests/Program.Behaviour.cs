// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Foundation;
using UIKit;
using Swift;
using Kingfisher;

namespace KingfisherSimTests;

/// <summary>
/// A C#-side <see cref="IResource"/> so retrieval can be driven at a URL this
/// process serves to itself, with a cache key the test controls.
/// </summary>
public sealed class TestResource : IResource
{
    public TestResource(string url, string cacheKey)
    {
        DownloadURL = new NSUrl(url);
        CacheKey = cacheKey;
    }

    public string CacheKey { get; }

    public NSUrl DownloadURL { get; }
}

// Behavioural tests: everything here loads real bytes through Kingfisher rather than
// asserting that a call did not throw. The bytes come from an in-process loopback
// server and images are generated at runtime, so every expected pixel value and
// every network hit count is known exactly.
public partial class MainViewController
{
    // Edge length of the throttled fixture image. Large enough that the encoded PNG is
    // tens of kilobytes, small enough to generate and decode in well under a second.
    private const int ThrottledImageEdge = 384;

    // Floor on the encoded size of that fixture. If PNG ever compresses it below this
    // the response would fit in one transport read and the throttled probe would stop
    // measuring anything, so the probe reports that as a failure rather than passing.
    private const int ThrottledBodyFloorBytes = 48 * 1024;

    // Retrieval is the headline API of this binding, and on today's generated
    // output it takes the process down: KingfisherManager.retrieveImage is emitted
    // with two @convention(c) closure parameters and no null handling, so passing
    // "no progress closure" lands on a null function pointer inside
    // PInvoke_retrieveImage and SIGSEGVs. That is a real, open generator defect, so
    // these probes run on every lane — a broken headline API has to be visible in
    // the ordinary release path, not behind an environment variable nobody sets.
    //
    // A SIGSEGV prints no result line and no verdict, which is exactly why the
    // ordering below puts every probe that does NOT depend on retrieval first: the
    // crash still reds the run (no verdict line means the harness reports a crash),
    // and everything the suite could establish before it is already on the wire.
    private async Task RunBehaviourTests(TestLogger logger, TestResults results)
    {
        LoopbackHttpServer? server = null;
        try
        {
            server = new LoopbackHttpServer();
        }
        catch (Exception ex)
        {
            logger.Fail($"Loopback server failed to start: {ex.GetType().Name}: {ex.Message}");
            results.Fail("Loopback_ServerStart", $"{ex.GetType().Name}: {ex.Message}");
            return;
        }

        using (server)
        {
            server.Map("/red.png", new LoopbackResponse
            {
                ContentType = "image/png",
                Body = ImageFixtures.SolidPng(24, 16, 1.0, 0.0, 0.0),
            });
            server.Map("/green.png", new LoopbackResponse
            {
                ContentType = "image/png",
                Body = ImageFixtures.SolidPng(32, 32, 0.0, 1.0, 0.0),
            });
            server.Map("/cached.png", new LoopbackResponse
            {
                ContentType = "image/png",
                Body = ImageFixtures.SolidPng(20, 10, 0.0, 0.0, 1.0),
            });
            server.Map("/prefetch.png", new LoopbackResponse
            {
                ContentType = "image/png",
                Body = ImageFixtures.SolidPng(12, 12, 1.0, 1.0, 0.0),
            });
            server.Map("/corrupt.png", new LoopbackResponse
            {
                ContentType = "image/png",
                Body = ImageFixtures.CorruptImageBytes(),
            });
            server.Map("/stall.png", new LoopbackResponse { ContentType = "image/png", Stall = true });

            // A body big enough that a solid fill would not do: block noise resists PNG
            // compression, so the payload stays large, and the server hands it over in
            // timed slices under a correct Content-Length. That is what turns "the
            // native progress closure reports while bytes are still arriving" into
            // something the assertions can require rather than hope for.
            var throttledBody = ImageFixtures.NoisePng(ThrottledImageEdge, ThrottledImageEdge, seed: 0x4B46u, cell: 2);
            // The gate makes the mid-download suspension a handshake rather than a race: the
            // server stops after roughly a third of the body and waits for the probe to say it
            // has collected garbage with bytes still outstanding.
            var throttledGate = new SliceGate(pauseAfterBytes: throttledBody.Length / 3);
            server.Map("/throttled.png", new LoopbackResponse
            {
                ContentType = "image/png",
                Body = throttledBody,
                ChunkBytes = Math.Max(8192, throttledBody.Length / 12),
                ChunkDelayMs = 60,
                Gate = throttledGate,
            });
            // /missing.png is deliberately unmapped so the server answers 404.

            // Probes that never enter the retrieval path, so their results are
            // already printed if a later one aborts the process.
            await ProcessorOutputPixels(logger, results, server);
            await DelayedCancellation(logger, results, server);
            CacheLimitRoundTrips(logger, results);

            // Retrieval-path probes.
            await RetrieveWithoutProgress(logger, results, server);
            await RetrieveWithProgress(logger, results, server);
            await RetrieveWithThrottledProgress(logger, results, server, throttledBody.Length, throttledGate);
            await CacheHitOnSecondLoad(logger, results, server);
            await PrefetchCompletes(logger, results, server);
            await CorruptImageFails(logger, results, server);
            await NotFoundFails(logger, results, server);
        }
    }

    // Retrieval with no progress callback: the null-closure arm of the async
    // overload. Asserts the decoded image and the cache type it reports.
    private async Task RetrieveWithoutProgress(TestLogger logger, TestResults results, LoopbackHttpServer server)
    {
        try
        {
            var manager = KingfisherManager.Shared;
            var resource = new TestResource(server.Url("/red.png"), $"kf-red-{Guid.NewGuid():N}");
            var result = await Await.WithTimeoutAsync(
                manager.RetrieveImageAsync(resource, null, null),
                20000,
                "RetrieveImageAsync(no progress)");

            var image = result.Image;
            var px = ImageFixtures.CenterPixel(image);
            bool sized = (int)image.Size.Width == 24 && (int)image.Size.Height == 16;
            bool red = px.R > 240 && px.G < 15 && px.B < 15 && px.A == 255;
            if (sized && red)
            {
                logger.Pass($"RetrieveImageAsync(progress=null) decoded 24x16 centre pixel {px}");
                results.Pass("Kingfisher_RetrieveImageAsync_NoProgress");
            }
            else
            {
                logger.Fail($"RetrieveImageAsync(progress=null) produced {image.Size.Width}x{image.Size.Height} centre pixel {px}, expected 24x16 red");
                results.Fail("Kingfisher_RetrieveImageAsync_NoProgress", $"{image.Size.Width}x{image.Size.Height} px={px}");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"RetrieveImageAsync(progress=null): {ex.GetType().Name}: {ex.Message}");
            results.Fail("Kingfisher_RetrieveImageAsync_NoProgress", $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    // The same call with a NON-null progress closure: the optional-callback arm of
    // an async binding, exercised end to end.
    private async Task RetrieveWithProgress(TestLogger logger, TestResults results, LoopbackHttpServer server)
    {
        try
        {
            var manager = KingfisherManager.Shared;
            var resource = new TestResource(server.Url("/green.png"), $"kf-green-{Guid.NewGuid():N}");
            long lastReceived = -1, lastExpected = -1;
            int progressCalls = 0;

            var result = await Await.WithTimeoutAsync(
                manager.RetrieveImageAsync(resource, null, (received, expected) =>
                {
                    Interlocked.Increment(ref progressCalls);
                    Volatile.Write(ref lastReceived, received);
                    Volatile.Write(ref lastExpected, expected);
                }),
                20000,
                "RetrieveImageAsync(with progress)");

            var image = result.Image;
            var px = ImageFixtures.CenterPixel(image);
            bool sized = (int)image.Size.Width == 32 && (int)image.Size.Height == 32;
            bool green = px.G > 240 && px.R < 15 && px.B < 15 && px.A == 255;
            if (!sized || !green)
            {
                logger.Fail($"RetrieveImageAsync(progress set) produced {image.Size.Width}x{image.Size.Height} centre pixel {px}, expected 32x32 green");
                results.Fail("Kingfisher_RetrieveImageAsync_WithProgress", $"{image.Size.Width}x{image.Size.Height} px={px}");
                return;
            }

            // The progress closure is optional to Kingfisher, so it is legitimate for a
            // small body to complete without an intermediate report; what is not
            // legitimate is a report carrying nonsense.
            int calls = Volatile.Read(ref progressCalls);
            long received = Volatile.Read(ref lastReceived);
            long expected = Volatile.Read(ref lastExpected);
            if (calls > 0 && (received <= 0 || expected < received))
            {
                logger.Fail($"RetrieveImageAsync progress closure reported received={received} expected={expected} over {calls} call(s)");
                results.Fail("Kingfisher_RetrieveImageAsync_WithProgress", $"received={received} expected={expected}");
                return;
            }

            logger.Pass($"RetrieveImageAsync(progress set) decoded 32x32 centre pixel {px}; progress calls={calls} last=({received}/{expected})");
            results.Pass("Kingfisher_RetrieveImageAsync_WithProgress");
        }
        catch (Exception ex)
        {
            logger.Fail($"RetrieveImageAsync(progress set): {ex.GetType().Name}: {ex.Message}");
            results.Fail("Kingfisher_RetrieveImageAsync_WithProgress", $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    // The same optional-callback arm against a body that cannot arrive in one read.
    // Where the probe above tolerates a small response completing with no intermediate
    // report, this one requires the native closure to actually deliver progress while
    // the download is in flight, requires the byte counts it carries to be sane and
    // non-decreasing against the declared Content-Length, and runs a full collection at
    // a point where the transfer is PROVABLY suspended: the server has paused with bytes
    // outstanding, at least one progress callback has already returned, and no callback
    // is on the stack. The closure then has to be invoked again once delivery resumes. A
    // closure the runtime stops keeping alive between invocations surfaces as a crash or
    // a missing report instead of a green line — and a run that cannot establish that
    // suspended state fails outright rather than claiming the coverage.
    private async Task RetrieveWithThrottledProgress(
        TestLogger logger, TestResults results, LoopbackHttpServer server, int bodyLength, SliceGate sliceGate)
    {
        const string TestId = "Kingfisher_RetrieveImageAsync_ThrottledProgress";
        try
        {
            if (bodyLength < ThrottledBodyFloorBytes)
            {
                logger.Fail($"Throttled fixture encoded to {bodyLength} bytes, below the {ThrottledBodyFloorBytes}-byte floor the probe needs");
                results.Fail(TestId, $"fixture too small: {bodyLength} bytes");
                return;
            }

            var manager = KingfisherManager.Shared;
            var resource = new TestResource(server.Url("/throttled.png"), $"kf-throttled-{Guid.NewGuid():N}");

            var gate = new object();
            var reports = new List<(long Received, long Expected)>();
            var firstReport = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            int callbacksInFlight = 0;

            var retrieve = manager.RetrieveImageAsync(resource, null, (received, expected) =>
            {
                Interlocked.Increment(ref callbacksInFlight);
                try
                {
                    lock (gate) { reports.Add((received, expected)); }
                    firstReport.TrySetResult(true);
                }
                finally
                {
                    Interlocked.Decrement(ref callbacksInFlight);
                }
            });

            void Red(string why)
            {
                logger.Fail($"RetrieveImageAsync(throttled progress): {why}");
                results.Fail(TestId, why);
            }

            // Preconditions for the suspended-state collection, each required rather than
            // hoped for: the server has paused with bytes outstanding, and the closure has
            // already been called at least once for the bytes delivered before the pause.
            var pauseWait = Task.WhenAll(sliceGate.Paused, firstReport.Task);
            await Task.WhenAny(pauseWait, retrieve, Task.Delay(30000)).ConfigureAwait(true);
            if (retrieve.IsCompleted)
            {
                sliceGate.Resume();
                Red($"the retrieval completed before the server paused — the fixture never suspended the transfer " +
                    $"(server paused: {sliceGate.IsPaused}, reports so far: {reports.Count})");
                return;
            }

            if (!sliceGate.IsPaused)
            {
                sliceGate.Resume();
                Red("the server did not reach its pause point within 30s");
                return;
            }

            long pausedAtBytes = await sliceGate.Paused.ConfigureAwait(true);
            if (!firstReport.Task.IsCompleted)
            {
                sliceGate.Resume();
                Red($"the server paused at {pausedAtBytes}/{bodyLength} bytes but the progress closure had not been invoked once within 30s");
                return;
            }

            // Let the last in-flight callback for the delivered bytes return, so the
            // collection below runs with no managed frame standing on the closure.
            bool callbacksQuiesced = await Await.UntilAsync(
                () => Volatile.Read(ref callbacksInFlight) == 0, 5000, pollMs: 10).ConfigureAwait(true);
            if (!callbacksQuiesced)
            {
                sliceGate.Resume();
                Red("a progress callback was still on the stack 5s after the server paused");
                return;
            }

            int reportsAtSuspendedGc;
            long receivedAtSuspendedGc;
            lock (gate)
            {
                reportsAtSuspendedGc = reports.Count;
                receivedAtSuspendedGc = reports[^1].Received;
            }

            if (receivedAtSuspendedGc >= bodyLength)
            {
                sliceGate.Resume();
                Red($"the closure had already reported the full {bodyLength}-byte body while the server was paused at {pausedAtBytes} bytes");
                return;
            }

            // The suspended-state collection: transfer paused, bytes outstanding, no callback
            // on the stack. Whatever keeps the native side's reference to this closure alive
            // has to survive this, and the closure has to be called again afterwards.
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            sliceGate.Resume();

            var result = await Await.WithTimeoutAsync(retrieve, 30000, "RetrieveImageAsync(throttled progress)");

            List<(long Received, long Expected)> snapshot;
            lock (gate) { snapshot = new List<(long Received, long Expected)>(reports); }

            var image = result.Image;
            if ((int)image.Size.Width != ThrottledImageEdge || (int)image.Size.Height != ThrottledImageEdge)
            {
                Red($"decoded {image.Size.Width}x{image.Size.Height}, expected {ThrottledImageEdge}x{ThrottledImageEdge}");
                return;
            }

            if (snapshot.Count == 0)
            {
                Red($"the progress closure was never invoked for a {bodyLength}-byte body delivered in timed slices");
                return;
            }

            long previous = 0;
            for (int i = 0; i < snapshot.Count; i++)
            {
                var (received, expected) = snapshot[i];
                if (expected != bodyLength)
                {
                    Red($"report {i + 1}/{snapshot.Count} carried expected={expected}, but the server declared Content-Length {bodyLength}");
                    return;
                }

                if (received <= 0 || received > expected)
                {
                    Red($"report {i + 1}/{snapshot.Count} carried received={received} against expected={expected}");
                    return;
                }

                if (received < previous)
                {
                    Red($"report {i + 1}/{snapshot.Count} went backwards: received={received} after {previous}");
                    return;
                }

                previous = received;
            }

            if (previous != bodyLength)
            {
                Red($"the last report stopped at received={previous} of {bodyLength}");
                return;
            }

            // The interval that exercises native retention is BETWEEN invocations: a full
            // collection with no managed frame standing on the closure, followed by another
            // native call into it. The server was paused with slices still to write when the
            // collection ran, so the closure has to have been invoked again afterwards.
            if (snapshot.Count == reportsAtSuspendedGc)
            {
                Red($"a collection ran between callbacks with {bodyLength - receivedAtSuspendedGc} byte(s) still " +
                    $"outstanding (server paused at {pausedAtBytes}), and the closure was never invoked again " +
                    $"({snapshot.Count} report(s) total)");
                return;
            }

            logger.Pass(
                $"RetrieveImageAsync(throttled progress) decoded {ThrottledImageEdge}x{ThrottledImageEdge}; " +
                $"{snapshot.Count} progress report(s) rising to {previous}/{bodyLength}; " +
                $"collected while suspended at {receivedAtSuspendedGc}/{bodyLength} (server paused at {pausedAtBytes}) " +
                $"after {reportsAtSuspendedGc} report(s); {snapshot.Count - reportsAtSuspendedGc} further report(s) followed it");
            results.Pass(TestId);
        }
        catch (Exception ex)
        {
            logger.Fail($"RetrieveImageAsync(throttled progress): {ex.GetType().Name}: {ex.Message}");
            results.Fail(TestId, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    // "It was cached" is measured against the server's own hit counter rather than
    // inferred from the CacheType the binding reports.
    private async Task CacheHitOnSecondLoad(TestLogger logger, TestResults results, LoopbackHttpServer server)
    {
        try
        {
            var manager = KingfisherManager.Shared;
            var key = $"kf-cached-{Guid.NewGuid():N}";
            var resource = new TestResource(server.Url("/cached.png"), key);

            var first = await Await.WithTimeoutAsync(manager.RetrieveImageAsync(resource), 20000, "first cached load");
            int hitsAfterFirst = server.HitsFor("/cached.png");
            var second = await Await.WithTimeoutAsync(manager.RetrieveImageAsync(resource), 20000, "second cached load");
            int hitsAfterSecond = server.HitsFor("/cached.png");

            bool cachedType = second.CacheType != CacheType.None;
            bool noExtraHit = hitsAfterSecond == hitsAfterFirst;
            bool sameSize = (int)second.Image.Size.Width == (int)first.Image.Size.Width
                            && (int)second.Image.Size.Width == 20;

            if (hitsAfterFirst == 1 && noExtraHit && cachedType && sameSize)
            {
                logger.Pass($"Second retrieval served from cache (CacheType={second.CacheType}, server hits stayed at {hitsAfterSecond})");
                results.Pass("Kingfisher_CacheHitSecondLoad");
            }
            else
            {
                logger.Fail($"Cache hit: hits {hitsAfterFirst} -> {hitsAfterSecond}, CacheType={second.CacheType}, size={second.Image.Size.Width}x{second.Image.Size.Height}");
                results.Fail("Kingfisher_CacheHitSecondLoad", $"hits {hitsAfterFirst}->{hitsAfterSecond} type={second.CacheType}");
            }

            // In-memory retrieval by the same key must hand back the same image.
            var inMemory = ImageCache.Default.RetrieveImageInMemoryCache(key);
            if (inMemory is not null && (int)inMemory.Size.Width == 20 && (int)inMemory.Size.Height == 10)
            {
                logger.Pass("RetrieveImageInMemoryCache returned the 20x10 cached image");
                results.Pass("Kingfisher_RetrieveImageInMemoryCache");
            }
            else
            {
                logger.Fail($"RetrieveImageInMemoryCache returned {(inMemory is null ? "null" : $"{inMemory.Size.Width}x{inMemory.Size.Height}")}, expected 20x10");
                results.Fail("Kingfisher_RetrieveImageInMemoryCache", inMemory is null ? "null" : $"{inMemory.Size.Width}x{inMemory.Size.Height}");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"Cache hit on second load: {ex.GetType().Name}: {ex.Message}");
            results.Fail("Kingfisher_CacheHitSecondLoad", $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    // A processor is only proven by the pixels it produces: a round-corner processor
    // must clear the corner and leave the centre alone.
    private async Task ProcessorOutputPixels(TestLogger logger, TestResults results, LoopbackHttpServer server)
    {
        try
        {
            // The subject here is the processor, not the loader, so the source image is
            // built in-process: routing it through RetrieveImageAsync would only make
            // this test inherit the retrieval path's crash.
            var source = ImageFixtures.SolidUIImage(32, 32, 0.0, 1.0, 0.0);
            await Task.Yield();

            var processor = new RoundCornerImageProcessor(Radius.Point(16));
            var item = ImageProcessItem.Image(source);
            var processed = processor.Process(item, new KingfisherParsedOptionsInfo(null));

            if (processed is null)
            {
                logger.Fail("RoundCornerImageProcessor.Process returned null");
                results.Fail("Kingfisher_ProcessorPixels", "null output");
                return;
            }

            var corner = ImageFixtures.PixelAt(processed, 0, 0);
            var centre = ImageFixtures.PixelAt(processed, 16, 16);
            bool cornerCleared = corner.A < 40;
            bool centreGreen = centre.G > 200 && centre.R < 60 && centre.B < 60 && centre.A > 200;
            if (cornerCleared && centreGreen)
            {
                logger.Pass($"RoundCornerImageProcessor cleared the corner (alpha {corner.A}) and kept the centre green {centre}");
                results.Pass("Kingfisher_ProcessorPixels");
            }
            else
            {
                logger.Fail($"RoundCornerImageProcessor corner={corner} centre={centre}; expected a transparent corner and a green centre");
                results.Fail("Kingfisher_ProcessorPixels", $"corner={corner} centre={centre}");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"Processor output pixels: {ex.GetType().Name}: {ex.Message}");
            results.Fail("Kingfisher_ProcessorPixels", $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    // ImagePrefetcher exposes no completion closure in this binding, so completion is
    // observed where it is actually visible: the image lands in the cache and a later
    // retrieval no longer touches the network.
    private async Task PrefetchCompletes(TestLogger logger, TestResults results, LoopbackHttpServer server)
    {
        try
        {
            var url = new NSUrl(server.Url("/prefetch.png"));
            var prefetcher = new ImagePrefetcher(new[] { url });
            prefetcher.Start();

            bool fetched = await Await.UntilAsync(() => server.HitsFor("/prefetch.png") >= 1, 20000);
            if (!fetched)
            {
                logger.Fail("ImagePrefetcher.Start() never requested the URL within 20s");
                results.Fail("Kingfisher_PrefetchCompletes", "no server hit");
                return;
            }

            bool cached = await Await.UntilAsync(
                () => ImageCache.Default.IsCached(url.AbsoluteString!),
                20000);
            if (!cached)
            {
                logger.Fail("Prefetched image never appeared in ImageCache.Default within 20s");
                results.Fail("Kingfisher_PrefetchCompletes", "never cached");
                return;
            }

            int hitsAfterPrefetch = server.HitsFor("/prefetch.png");
            var manager = KingfisherManager.Shared;
            var result = await Await.WithTimeoutAsync(
                manager.RetrieveImageAsync(new TestResource(server.Url("/prefetch.png"), url.AbsoluteString!)),
                20000,
                "post-prefetch retrieval");

            bool servedFromCache = server.HitsFor("/prefetch.png") == hitsAfterPrefetch && result.CacheType != CacheType.None;
            bool sized = (int)result.Image.Size.Width == 12 && (int)result.Image.Size.Height == 12;
            if (servedFromCache && sized)
            {
                logger.Pass($"Prefetch completed: retrieval served from {result.CacheType} with no extra server hit");
                results.Pass("Kingfisher_PrefetchCompletes");
            }
            else
            {
                logger.Fail($"Prefetch: CacheType={result.CacheType}, hits {hitsAfterPrefetch} -> {server.HitsFor("/prefetch.png")}, size {result.Image.Size.Width}x{result.Image.Size.Height}");
                results.Fail("Kingfisher_PrefetchCompletes", $"type={result.CacheType} hits={server.HitsFor("/prefetch.png")}");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"Prefetch completion: {ex.GetType().Name}: {ex.Message}");
            results.Fail("Kingfisher_PrefetchCompletes", $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    // Cancellation after the request is provably in flight — the stalling route
    // guarantees the download cannot have finished before Cancel is called.
    private async Task DelayedCancellation(TestLogger logger, TestResults results, LoopbackHttpServer server)
    {
        try
        {
            using var cts = new CancellationTokenSource();
            var downloader = ImageDownloader.Default;
            var loadTask = downloader.DownloadImageAsync(
                new NSUrl(server.Url("/stall.png")),
                (IReadOnlyList<KingfisherOptionsInfoItem>?)null,
                cts.Token);

            bool inFlight = await Await.UntilAsync(() => server.HitsFor("/stall.png") >= 1, 10000);
            if (!inFlight)
            {
                logger.Fail("Stalling download never reached the server within 10s");
                results.Fail("Kingfisher_DelayedCancellation", "request never reached the server");
                return;
            }

            if (loadTask.IsCompleted)
            {
                logger.Fail("Stalling download completed before it could be cancelled");
                results.Fail("Kingfisher_DelayedCancellation", "completed while stalled");
                return;
            }

            cts.Cancel();

            try
            {
                await Await.WithTimeoutAsync(loadTask, 15000, "cancelled download");
                logger.Fail("Cancelled download produced a result instead of throwing");
                results.Fail("Kingfisher_DelayedCancellation", "no cancellation observed");
            }
            catch (OperationCanceledException)
            {
                logger.Pass("Delayed cancellation of an in-flight download threw OperationCanceledException");
                results.Pass("Kingfisher_DelayedCancellation");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"Delayed cancellation: {ex.GetType().Name}: {ex.Message}");
            results.Fail("Kingfisher_DelayedCancellation", $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    // 200 with undecodable bytes must surface as a failure, not a null image or a hang.
    private async Task CorruptImageFails(TestLogger logger, TestResults results, LoopbackHttpServer server)
    {
        try
        {
            var manager = KingfisherManager.Shared;
            var resource = new TestResource(server.Url("/corrupt.png"), $"kf-corrupt-{Guid.NewGuid():N}");
            try
            {
                var result = await Await.WithTimeoutAsync(manager.RetrieveImageAsync(resource), 20000, "corrupt image load");
                logger.Fail($"Corrupt image decoded to {result.Image.Size.Width}x{result.Image.Size.Height} instead of failing");
                results.Fail("Kingfisher_CorruptImageFails", "decode unexpectedly succeeded");
            }
            catch (TimeoutException)
            {
                logger.Fail("Corrupt image load neither completed nor failed within 20s");
                results.Fail("Kingfisher_CorruptImageFails", "timed out");
            }
            catch (Exception ex)
            {
                logger.Pass($"Corrupt image surfaced as {ex.GetType().Name}: {ex.Message}");
                results.Pass("Kingfisher_CorruptImageFails");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"Corrupt image path: {ex.GetType().Name}: {ex.Message}");
            results.Fail("Kingfisher_CorruptImageFails", $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private async Task NotFoundFails(TestLogger logger, TestResults results, LoopbackHttpServer server)
    {
        try
        {
            var manager = KingfisherManager.Shared;
            var resource = new TestResource(server.Url("/missing.png"), $"kf-404-{Guid.NewGuid():N}");
            try
            {
                var result = await Await.WithTimeoutAsync(manager.RetrieveImageAsync(resource), 20000, "404 load");
                logger.Fail($"404 produced an image ({result.Image.Size.Width}x{result.Image.Size.Height}) instead of failing");
                results.Fail("Kingfisher_NotFoundFails", "404 unexpectedly succeeded");
            }
            catch (TimeoutException)
            {
                logger.Fail("404 load neither completed nor failed within 20s");
                results.Fail("Kingfisher_NotFoundFails", "timed out");
            }
            catch (Exception ex)
            {
                if (server.HitsFor("/missing.png") < 1)
                {
                    logger.Fail($"404 test failed before reaching the server: {ex.GetType().Name}: {ex.Message}");
                    results.Fail("Kingfisher_NotFoundFails", "never reached the server");
                    return;
                }

                logger.Pass($"404 surfaced as {ex.GetType().Name}: {ex.Message}");
                results.Pass("Kingfisher_NotFoundFails");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"404 path: {ex.GetType().Name}: {ex.Message}");
            results.Fail("Kingfisher_NotFoundFails", $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    // Cache limits: Swift declares these as pointer-width Int/UInt, and the generated
    // surface projects each of them twice — a narrowed 32-bit accessor that throws
    // OverflowException instead of truncating, plus a "{Name}Native" sibling at the
    // width Swift declares. A value inside the narrow range must read back unchanged
    // through the narrow accessor; a value above it must throw there (naming the
    // member, the value and the sibling) and must still be readable and settable
    // losslessly through the sibling. Small, 2^31 and 2^32 are checked separately so
    // the report says exactly which half of that contract broke.
    private void CacheLimitRoundTrips(TestLogger logger, TestResults results)
    {
        void DiskCase(string name, nuint limit)
        {
            try
            {
                using var config = new global::Kingfisher.DiskStorage.Config($"kf-limit-{Guid.NewGuid():N}", limit);
                var readBack = config.SizeLimit;
                if ((nuint)readBack == limit)
                {
                    logger.Pass($"DiskStorage.Config.SizeLimit round-tripped {limit}");
                    results.Pass(name);
                }
                else
                {
                    logger.Fail($"DiskStorage.Config.SizeLimit: set {limit}, read back {readBack}");
                    results.Fail(name, $"set {limit}, read back {readBack}");
                }
            }
            catch (Exception ex)
            {
                logger.Fail($"{name}: {ex.GetType().Name}: {ex.Message}");
                results.Fail(name, $"{ex.GetType().Name}: {ex.Message}");
            }
        }

        void MemoryCase(string name, nint limit)
        {
            try
            {
                using var config = new global::Kingfisher.MemoryStorage.Config(limit);
                var readBack = config.TotalCostLimit;
                if ((nint)readBack == limit)
                {
                    logger.Pass($"MemoryStorage.Config.TotalCostLimit round-tripped {limit}");
                    results.Pass(name);
                }
                else
                {
                    logger.Fail($"MemoryStorage.Config.TotalCostLimit: set {limit}, read back {readBack}");
                    results.Fail(name, $"set {limit}, read back {readBack}");
                }
            }
            catch (Exception ex)
            {
                logger.Fail($"{name}: {ex.GetType().Name}: {ex.Message}");
                results.Fail(name, $"{ex.GetType().Name}: {ex.Message}");
            }
        }

        // Above the narrow accessor's range: the narrow read must throw and point at the
        // sibling, and the sibling must carry the value both ways without converting.
        void DiskOverflowCase(string name, nuint limit)
        {
            try
            {
                using var config = new global::Kingfisher.DiskStorage.Config($"kf-limit-{Guid.NewGuid():N}", limit);

                try
                {
                    var narrowed = config.SizeLimit;
                    logger.Fail($"DiskStorage.Config.SizeLimit returned {narrowed} for {limit} instead of throwing OverflowException");
                    results.Fail(name, $"narrow accessor returned {narrowed}, expected OverflowException");
                    return;
                }
                catch (OverflowException ex)
                {
                    var missing = DescribeMissingOverflowDetail(ex.Message, "SizeLimit", "SizeLimitNative", limit.ToString());
                    if (missing is not null)
                    {
                        logger.Fail($"DiskStorage.Config.SizeLimit overflow message {missing}: {ex.Message}");
                        results.Fail(name, $"overflow message {missing}");
                        return;
                    }
                }

                var native = config.SizeLimitNative;
                if (native != limit)
                {
                    logger.Fail($"DiskStorage.Config.SizeLimitNative read back {native}, expected {limit}");
                    results.Fail(name, $"native read back {native}, expected {limit}");
                    return;
                }

                // The sibling has to be a two-way accessor, not just a lossless read.
                var rewritten = limit + (nuint)4096;
                config.SizeLimitNative = rewritten;
                var readBack = config.SizeLimitNative;
                if (readBack != rewritten)
                {
                    logger.Fail($"DiskStorage.Config.SizeLimitNative set {rewritten}, read back {readBack}");
                    results.Fail(name, $"native set {rewritten}, read back {readBack}");
                    return;
                }

                logger.Pass($"DiskStorage.Config.SizeLimit threw OverflowException for {limit}; SizeLimitNative round-tripped {limit} and {rewritten}");
                results.Pass(name);
            }
            catch (Exception ex)
            {
                logger.Fail($"{name}: {ex.GetType().Name}: {ex.Message}");
                results.Fail(name, $"{ex.GetType().Name}: {ex.Message}");
            }
        }

        void MemoryOverflowCase(string name, nint limit)
        {
            try
            {
                using var config = new global::Kingfisher.MemoryStorage.Config(limit);

                try
                {
                    var narrowed = config.TotalCostLimit;
                    logger.Fail($"MemoryStorage.Config.TotalCostLimit returned {narrowed} for {limit} instead of throwing OverflowException");
                    results.Fail(name, $"narrow accessor returned {narrowed}, expected OverflowException");
                    return;
                }
                catch (OverflowException ex)
                {
                    var missing = DescribeMissingOverflowDetail(ex.Message, "TotalCostLimit", "TotalCostLimitNative", limit.ToString());
                    if (missing is not null)
                    {
                        logger.Fail($"MemoryStorage.Config.TotalCostLimit overflow message {missing}: {ex.Message}");
                        results.Fail(name, $"overflow message {missing}");
                        return;
                    }
                }

                var native = config.TotalCostLimitNative;
                if (native != limit)
                {
                    logger.Fail($"MemoryStorage.Config.TotalCostLimitNative read back {native}, expected {limit}");
                    results.Fail(name, $"native read back {native}, expected {limit}");
                    return;
                }

                var rewritten = limit + (nint)4096;
                config.TotalCostLimitNative = rewritten;
                var readBack = config.TotalCostLimitNative;
                if (readBack != rewritten)
                {
                    logger.Fail($"MemoryStorage.Config.TotalCostLimitNative set {rewritten}, read back {readBack}");
                    results.Fail(name, $"native set {rewritten}, read back {readBack}");
                    return;
                }

                logger.Pass($"MemoryStorage.Config.TotalCostLimit threw OverflowException for {limit}; TotalCostLimitNative round-tripped {limit} and {rewritten}");
                results.Pass(name);
            }
            catch (Exception ex)
            {
                logger.Fail($"{name}: {ex.GetType().Name}: {ex.Message}");
                results.Fail(name, $"{ex.GetType().Name}: {ex.Message}");
            }
        }

        DiskCase("Kingfisher_DiskSizeLimit_Small", 1024 * 1024);
        // 2^31 still fits in the unsigned narrow accessor, so this one stays a plain
        // round trip — it is the control that says the checked read does not over-throw.
        DiskCase("Kingfisher_DiskSizeLimit_2Pow31", (nuint)2147483648UL);
        DiskOverflowCase("Kingfisher_DiskSizeLimit_2Pow32", (nuint)4294967296UL);

        MemoryCase("Kingfisher_MemoryCostLimit_Small", 1024 * 1024);
        MemoryOverflowCase("Kingfisher_MemoryCostLimit_2Pow31", (nint)2147483648L);
        MemoryOverflowCase("Kingfisher_MemoryCostLimit_2Pow32", (nint)4294967296L);
    }

    // An overflow from a narrowed accessor is only actionable if it says which member
    // overflowed, what the value was, and which sibling reads it losslessly. Returns a
    // description of the first missing part, or null when the message carries all three.
    private static string? DescribeMissingOverflowDetail(string message, string member, string companion, string value)
    {
        if (!message.Contains(member, StringComparison.Ordinal))
            return $"does not name the member '{member}'";
        if (!message.Contains(companion, StringComparison.Ordinal))
            return $"does not name the companion '{companion}'";
        if (!message.Contains(value, StringComparison.Ordinal))
            return $"does not report the value {value}";
        return null;
    }
}
