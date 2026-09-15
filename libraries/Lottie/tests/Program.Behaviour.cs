// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.IO.Compression;
using System.Text;
using CoreGraphics;
using Foundation;
using UIKit;
using Lottie;

namespace LottieSimTests;

// Behavioural tests: a Lottie view is only proven by a window-attached view that
// actually advances and renders, and by .lottie bundles this process builds so both
// the valid and the malformed case are known-good inputs.
public partial class MainViewController
{
    private const string DifferentialFixtureName = "B11GradientDifferential";
    private const string DifferentialGradientKeypath =
        "B11 Gradient Layer.B11 Shape Group.B11 Unique Gradient.Colors";
    private const double DifferentialFrame = 30;

    [global::System.Runtime.InteropServices.UnmanagedCallConv(
        CallConvs = new[] { typeof(global::System.Runtime.CompilerServices.CallConvCdecl) })]
    [global::System.Runtime.InteropServices.LibraryImport(
        "LottieSwiftBindings", EntryPoint = "SBLottieB11InstallPureSwiftDynamicGradient")]
    private static partial int InstallPureSwiftDynamicGradient(IntPtr viewHandle);

    // Two probes below abort the process on today's binding. They run on every lane
    // anyway: an abort is the honest report of a broken route, and hiding it behind
    // an environment variable that no release path sets turns an open defect into a
    // green run. They are ordered last so every other result is already printed when
    // the process goes down.
    private UIView? _testHost;

    /// <summary>
    /// Puts a view in the live view hierarchy. Lottie drives playback from a display
    /// link that only runs for a view in a window, so playback assertions are
    /// meaningless without this.
    /// </summary>
    private void AttachToWindow(UIView view)
    {
        view.Frame = new CGRect(0, 0, 120, 120);
        _testHost ??= View!;
        _testHost.AddSubview(view);
        view.SetNeedsLayout();
        view.LayoutIfNeeded();
    }

    private void DetachFromWindow(UIView view)
    {
        view.RemoveFromSuperview();
    }

    /// <summary>Renders a view's layer into a bitmap and returns straight RGBA pixels.</summary>
    private static byte[] RenderPixels(UIView view, int width, int height)
    {
        var buffer = new byte[width * height * 4];
        unsafe
        {
            fixed (byte* p = buffer)
            {
                using var colorSpace = CGColorSpace.CreateDeviceRGB();
                using var ctx = new CGBitmapContext(
                    (IntPtr)p, width, height, 8, 4 * width, colorSpace, CGImageAlphaInfo.PremultipliedLast);
                ctx.ScaleCTM(width / view.Bounds.Width, height / view.Bounds.Height);
                view.Layer.RenderInContext(ctx);
            }
        }

        return buffer;
    }

    private static int NonTransparentPixels(byte[] rgba)
    {
        int count = 0;
        for (int i = 3; i < rgba.Length; i += 4)
        {
            if (rgba[i] > 8) count++;
        }

        return count;
    }

    private static int DifferingPixels(byte[] a, byte[] b)
    {
        int count = 0;
        for (int i = 0; i < a.Length; i += 4)
        {
            if (a[i] != b[i] || a[i + 1] != b[i + 1] || a[i + 2] != b[i + 2] || a[i + 3] != b[i + 3])
                count++;
        }

        return count;
    }

    private async Task RunBehaviourTests(TestLogger logger, TestResults results)
    {
        await RenderedFrameHasPixels(logger, results);
        await DotLottieAsyncRoutes(logger, results);
        await DotLottieFilepathCallbackRoute(logger, results);
        DotLottieDataCallbackRoute(logger, results);
        GradientProviderDifferential(logger, results);
    }

    // A view that plays but renders nothing is indistinguishable from a working one
    // unless the pixels are read back.
    private async Task RenderedFrameHasPixels(TestLogger logger, TestResults results)
    {
        try
        {
            var path = NSBundle.MainBundle.PathForResource("PlaneAnimation", "json");
            if (path is null)
            {
                logger.Fail("Rendered frame: bundled fixture PlaneAnimation.json is missing from the app bundle");
                results.Fail("Render_NonTransparentPixels", "bundled fixture missing");
                return;
            }

            // Built with the main-thread rendering engine on purpose. Lottie's default
            // engine is Core Animation, which drives a CAAnimation on the presentation
            // layer: the model layer this test can read back never changes, so every
            // frame would look identical no matter how well the binding worked. The
            // main-thread engine draws into the layer itself, which is what makes
            // "did the frame actually change?" an answerable question here.
            using var engine = RenderingEngineOption.Specific(RenderingEngine.MainThread);
            using var configuration = new LottieConfiguration(engine);
            var view = new LottieAnimationView(path, null, null, configuration);
            view.BackgroundColor = UIColor.Clear;
            AttachToWindow(view);

            // Sample several points rather than one pair: a single pair could sit
            // inside a stretch of the animation that genuinely does not move, which
            // would make a real redraw look broken. Every sampled progress is also read
            // back, so a setter that never reaches Swift is distinguishable from an
            // animation that looks the same at two frames.
            double[] progresses = { 0.0, 0.25, 0.5, 0.75 };
            var frames = new byte[progresses.Length][];
            var readback = new double[progresses.Length];
            for (int i = 0; i < progresses.Length; i++)
            {
                view.CurrentProgress = progresses[i];
                view.ForceDisplayUpdate();
                view.LayoutIfNeeded();
                await Task.Delay(250);
                readback[i] = view.CurrentProgress;
                frames[i] = RenderPixels(view, 120, 120);
            }

            DetachFromWindow(view);

            int opaque = NonTransparentPixels(frames[2]);
            int maxChanged = 0;
            string maxPair = "";
            for (int i = 0; i < frames.Length; i++)
            {
                for (int j = i + 1; j < frames.Length; j++)
                {
                    int d = DifferingPixels(frames[i], frames[j]);
                    if (d > maxChanged)
                    {
                        maxChanged = d;
                        maxPair = $"{progresses[i]}->{progresses[j]}";
                    }
                }
            }

            bool progressRoundTrips = true;
            for (int i = 0; i < progresses.Length; i++)
            {
                if (Math.Abs(readback[i] - progresses[i]) > 1e-6) progressRoundTrips = false;
            }

            string seen = string.Join(", ", readback.Select(v => v.ToString("0.###")));
            if (opaque < 100)
            {
                logger.Fail($"Rendered frame at progress 0.5 had only {opaque} non-transparent pixels of {120 * 120}");
                results.Fail("Render_NonTransparentPixels", $"{opaque} non-transparent pixels");
            }
            else if (!progressRoundTrips)
            {
                logger.Fail($"CurrentProgress did not round-trip: set 0, 0.25, 0.5, 0.75 but read back {seen}");
                results.Fail("Render_NonTransparentPixels", $"CurrentProgress read back as {seen}");
            }
            else if (maxChanged < 50)
            {
                logger.Fail($"No pair of progress values 0/0.25/0.5/0.75 changed more than {maxChanged} pixels; the frame is not being redrawn (CurrentProgress round-trips, read back {seen})");
                results.Fail("Render_NonTransparentPixels", $"max {maxChanged} pixels changed across four sampled frames");
            }
            else
            {
                logger.Pass($"Rendered frame carried {opaque} non-transparent pixels and {maxChanged} changed between progress {maxPair}");
                results.Pass("Render_NonTransparentPixels");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"Rendered frame: {ex.GetType().Name}: {ex.Message}");
            results.Fail("Render_NonTransparentPixels", $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Builds a .lottie (a zip carrying manifest.json plus animations/) around the
    /// bundled animation JSON, so the valid case is known-good without shipping a
    /// second binary fixture.
    /// </summary>
    private static string WriteDotLottie(string animationJsonPath, string name)
    {
        var dir = Path.Combine(Path.GetTempPath(), "lottie-fixtures");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name);
        if (File.Exists(path)) File.Delete(path);

        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            var manifest = zip.CreateEntry("manifest.json");
            using (var writer = new StreamWriter(manifest.Open(), Encoding.UTF8))
            {
                writer.Write("{\"version\":\"1.0\",\"generator\":\"swiftbindings-tests\",\"animations\":[{\"id\":\"anim\"}]}");
            }

            var entry = zip.CreateEntry("animations/anim.json");
            using var source = File.OpenRead(animationJsonPath);
            using var target = entry.Open();
            source.CopyTo(target);
        }

        return path;
    }

    private static string WriteMalformedDotLottie(string name)
    {
        var dir = Path.Combine(Path.GetTempPath(), "lottie-fixtures");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name);
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes("PK this is not a real zip archive"));
        return path;
    }

    private async Task DotLottieAsyncRoutes(TestLogger logger, TestResults results)
    {
        var jsonPath = NSBundle.MainBundle.PathForResource("PlaneAnimation", "json");
        if (jsonPath is null)
        {
            logger.Fail("DotLottie: bundled fixture PlaneAnimation.json is missing from the app bundle");
            results.Fail("DotLottie_LoadedFromAsync_Valid", "bundled fixture missing");
            results.Fail("DotLottie_LoadedFromAsync_Malformed", "bundled fixture missing");
            return;
        }

        // Valid .lottie through the async route.
        try
        {
            var valid = WriteDotLottie(jsonPath, "valid.lottie");
            var file = await Await.WithTimeoutAsync(
                DotLottieFile.LoadedFromAsync(valid),
                20000,
                "DotLottieFile.LoadedFromAsync(valid)");

            var animations = file.Animations;
            if (animations is null || animations.Count == 0)
            {
                logger.Fail("DotLottieFile.LoadedFromAsync(valid) returned a file with no animations");
                results.Fail("DotLottie_LoadedFromAsync_Valid", "no animations");
            }
            else
            {
                logger.Pass($"DotLottieFile.LoadedFromAsync(valid) returned {animations.Count} animation(s)");
                results.Pass("DotLottie_LoadedFromAsync_Valid");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"DotLottieFile.LoadedFromAsync(valid): {ex.GetType().Name}: {ex.Message}");
            results.Fail("DotLottie_LoadedFromAsync_Valid", $"{ex.GetType().Name}: {ex.Message}");
        }

        // Malformed .lottie must surface as an error, not a silent empty file.
        try
        {
            var malformed = WriteMalformedDotLottie("malformed.lottie");
            try
            {
                var file = await Await.WithTimeoutAsync(
                    DotLottieFile.LoadedFromAsync(malformed),
                    20000,
                    "DotLottieFile.LoadedFromAsync(malformed)");
                logger.Fail($"Malformed .lottie loaded successfully ({file.Animations?.Count ?? 0} animations) instead of failing");
                results.Fail("DotLottie_LoadedFromAsync_Malformed", "load unexpectedly succeeded");
            }
            catch (TimeoutException)
            {
                logger.Fail("Malformed .lottie load neither completed nor failed within 20s");
                results.Fail("DotLottie_LoadedFromAsync_Malformed", "timed out");
            }
            catch (Exception ex)
            {
                logger.Pass($"Malformed .lottie surfaced as {ex.GetType().Name}: {ex.Message}");
                results.Pass("DotLottie_LoadedFromAsync_Malformed");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"Malformed .lottie path: {ex.GetType().Name}: {ex.Message}");
            results.Fail("DotLottie_LoadedFromAsync_Malformed", $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    // The filepath + result-closure route. This overload boxes and retrieves the same
    // delegate type, so it is expected to deliver; it runs in the default suite.
    private async Task DotLottieFilepathCallbackRoute(TestLogger logger, TestResults results)
    {
        try
        {
            var jsonPath = NSBundle.MainBundle.PathForResource("PlaneAnimation", "json");
            if (jsonPath is null)
            {
                logger.Fail("DotLottie callback: bundled fixture PlaneAnimation.json is missing from the app bundle");
                results.Fail("DotLottie_LoadedFrom_Callback", "bundled fixture missing");
                return;
            }

            var valid = WriteDotLottie(jsonPath, "valid-callback.lottie");
            Swift.SwiftResult<DotLottieFile, Swift.Runtime.ExistentialContainer1>? received = null;
            DotLottieFile.LoadedFrom(valid, result => received = result);

            await Await.UntilAsync(() => received is not null, 10000);

            if (received is null)
            {
                logger.Fail("DotLottieFile.LoadedFrom(callback) never invoked its result closure within 10s");
                results.Fail("DotLottie_LoadedFrom_Callback", "closure never fired");
            }
            else if (!received.TryGetSuccess(out var file))
            {
                logger.Fail("DotLottieFile.LoadedFrom(callback) reported a failure for a valid .lottie");
                results.Fail("DotLottie_LoadedFrom_Callback", "result was a failure");
            }
            else
            {
                // The archive this route is handed is written with exactly one
                // animation, so an empty collection is a decode that silently
                // dropped its payload — a success Result carrying nothing. The
                // async route above rejects that; the callback route must too.
                var animations = file.Animations;
                if (animations is null || animations.Count == 0)
                {
                    logger.Fail("DotLottieFile.LoadedFrom(callback) delivered a file with no animations");
                    results.Fail("DotLottie_LoadedFrom_Callback", "no animations");
                }
                else
                {
                    logger.Pass($"DotLottieFile.LoadedFrom(callback) delivered {animations.Count} animation(s)");
                    results.Pass("DotLottie_LoadedFrom_Callback");
                }
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"DotLottie callback route: {ex.GetType().Name}: {ex.Message}");
            results.Fail("DotLottie_LoadedFrom_Callback", $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    // The data + dispatch-queue result-closure route: this overload boxes an
    // Action<SwiftResult<DotLottieFile, ExistentialContainer1>> and its trampoline
    // retrieves an Action<SwiftResult<DotLottieFile, AnyError>>, so delivery tears the
    // process down through FailFast. It is ordered near the end of the suite so the
    // abort costs as little other evidence as possible.
    private void DotLottieDataCallbackRoute(TestLogger logger, TestResults results)
    {
        try
        {
            var jsonPath = NSBundle.MainBundle.PathForResource("PlaneAnimation", "json");
            if (jsonPath is null)
            {
                logger.Fail("DotLottie data callback: bundled fixture PlaneAnimation.json is missing from the app bundle");
                results.Fail("DotLottie_LoadedFrom_DataCallback", "bundled fixture missing");
                return;
            }

            var bytes = File.ReadAllBytes(WriteDotLottie(jsonPath, "valid-data-callback.lottie"));
            Swift.SwiftResult<DotLottieFile, Swift.Runtime.ExistentialContainer1>? received = null;
            DotLottieFile.LoadedFrom(bytes, "valid-data-callback.lottie", Swift.DispatchQueue.Global(),
                result => received = result);

            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (received is null && DateTime.UtcNow < deadline)
                NSRunLoop.Current.RunUntil(NSDate.FromTimeIntervalSinceNow(0.05));

            if (received is null)
            {
                logger.Fail("DotLottieFile.LoadedFrom(data, queue, callback) never invoked its result closure within 10s");
                results.Fail("DotLottie_LoadedFrom_DataCallback", "closure never fired");
            }
            else if (!received.TryGetSuccess(out var file))
            {
                logger.Fail("DotLottieFile.LoadedFrom(data, queue, callback) reported a failure for a valid .lottie");
                results.Fail("DotLottie_LoadedFrom_DataCallback", "result was a failure");
            }
            else
            {
                var animations = file.Animations;
                if (animations is null || animations.Count == 0)
                {
                    logger.Fail("DotLottieFile.LoadedFrom(data, queue, callback) delivered a file with no animations");
                    results.Fail("DotLottie_LoadedFrom_DataCallback", "no animations");
                }
                else
                {
                    logger.Pass($"DotLottieFile.LoadedFrom(data, queue, callback) delivered {animations.Count} animation(s)");
                    results.Pass("DotLottie_LoadedFrom_DataCallback");
                }
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"DotLottie data callback route: {ex.GetType().Name}: {ex.Message}");
            results.Fail("DotLottie_LoadedFrom_DataCallback", $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static LottieColor[] DifferentialPalette() =>
    [
        new LottieColor(0.0, 1.0, 0.0, 1.0),
        new LottieColor(1.0, 1.0, 0.0, 1.0),
    ];

    private LottieAnimationView CreateDifferentialView(string path)
    {
        using var engine = RenderingEngineOption.Specific(RenderingEngine.MainThread);
        using var configuration = new LottieConfiguration(engine);
        var view = new LottieAnimationView(path, null, null, configuration)
        {
            BackgroundColor = UIColor.Clear,
        };
        AttachToWindow(view);
        view.CurrentFrame = DifferentialFrame;
        view.ForceDisplayUpdate();
        view.LayoutIfNeeded();
        if (view.CurrentRenderingEngine != RenderingEngine.MainThread)
            throw new InvalidOperationException(
                $"renderer drifted from MainThread to {view.CurrentRenderingEngine?.ToString() ?? "null"}");
        if (Math.Abs(view.CurrentFrame - DifferentialFrame) > 1e-6)
            throw new InvalidOperationException(
                $"frame drifted from {DifferentialFrame} to {view.CurrentFrame}");
        return view;
    }

    private byte[] RenderDifferentialArm(
        string path,
        Action<LottieAnimationView>? installProvider,
        out IReadOnlyList<string> hierarchy)
    {
        var view = CreateDifferentialView(path);
        try
        {
            hierarchy = view.GetAllHierarchyKeypaths();
            installProvider?.Invoke(view);
            view.CurrentFrame = DifferentialFrame;
            view.ForceDisplayUpdate();
            view.LayoutIfNeeded();
            return RenderPixels(view, 120, 120);
        }
        finally
        {
            DetachFromWindow(view);
            view.Dispose();
        }
    }

    // Four arms share one minimal fixture, exact keypath, MainThread renderer and
    // frame 30. Callback counts are diagnostics only; every provider arm must alter
    // the bitmap, and both dynamic arms must reproduce the static-provider image.
    private void GradientProviderDifferential(TestLogger logger, TestResults results)
    {
        try
        {
            var path = NSBundle.MainBundle.PathForResource(DifferentialFixtureName, "json");
            if (path is null)
            {
                logger.Fail($"B11 Lottie differential: bundled fixture {DifferentialFixtureName}.json is missing");
                results.Fail("Provider_GradientDifferential", "bundled fixture missing");
                return;
            }

            var noProvider = RenderDifferentialArm(path, null, out var hierarchy);
            var recordedHierarchy = hierarchy.OrderBy(keypath => keypath, StringComparer.Ordinal).ToArray();
            logger.Info($"[B11-CONTRACT] fixture={DifferentialFixtureName}.json renderer=MainThread frame={DifferentialFrame:0} keypath={DifferentialGradientKeypath}");
            foreach (var keypath in recordedHierarchy)
                logger.Info($"[B11-HIERARCHY] {keypath}");
            if (!recordedHierarchy.Contains(DifferentialGradientKeypath, StringComparer.Ordinal))
                throw new InvalidOperationException(
                    $"recorded hierarchy does not contain exact gradient keypath '{DifferentialGradientKeypath}'");

            GradientValueProvider? staticProvider = null;
            var staticPixels = RenderDifferentialArm(path, view =>
            {
                staticProvider = new GradientValueProvider(DifferentialPalette());
                view.SetValueProvider(staticProvider, new AnimationKeypath(DifferentialGradientKeypath));
            }, out _);

            var pureSwiftCalls = 0;
            var pureSwiftPixels = RenderDifferentialArm(path, view =>
            {
                pureSwiftCalls = InstallPureSwiftDynamicGradient((IntPtr)view.Handle);
            }, out _);

            var managedCalls = 0;
            GradientValueProvider? managedProvider = null;
            var managedPixels = RenderDifferentialArm(path, view =>
            {
                managedProvider = new GradientValueProvider(_ =>
                {
                    Interlocked.Increment(ref managedCalls);
                    return DifferentialPalette();
                });
                view.SetValueProvider(managedProvider, new AnimationKeypath(DifferentialGradientKeypath));
            }, out _);

            GC.KeepAlive(staticProvider);
            GC.KeepAlive(managedProvider);
            var staticEffect = DifferingPixels(noProvider, staticPixels);
            var pureSwiftEffect = DifferingPixels(noProvider, pureSwiftPixels);
            var managedEffect = DifferingPixels(noProvider, managedPixels);
            var pureSwiftVsStatic = DifferingPixels(staticPixels, pureSwiftPixels);
            var managedVsStatic = DifferingPixels(staticPixels, managedPixels);
            logger.Info(
                $"[B11-DIFFERENTIAL] no->static={staticEffect} no->swift-dynamic={pureSwiftEffect} " +
                $"no->managed-dynamic={managedEffect} swift-dynamic->static={pureSwiftVsStatic} " +
                $"managed-dynamic->static={managedVsStatic} swift-calls={pureSwiftCalls} managed-calls={managedCalls}");

            const int minimumEffectPixels = 1_000;
            const int maximumParityPixels = 4;
            if (staticEffect < minimumEffectPixels)
                throw new InvalidOperationException(
                    $"static provider changed only {staticEffect} pixels; fixture/keypath/render oracle is invalid");
            if (pureSwiftCalls <= 0 || pureSwiftEffect < minimumEffectPixels || pureSwiftVsStatic > maximumParityPixels)
                throw new InvalidOperationException(
                    $"pure-Swift dynamic arm failed: calls={pureSwiftCalls}, effect={pureSwiftEffect}, parityDelta={pureSwiftVsStatic}");
            if (managedCalls <= 0 || managedEffect < minimumEffectPixels || managedVsStatic > maximumParityPixels)
                throw new InvalidOperationException(
                    $"managed dynamic arm failed: calls={managedCalls}, effect={managedEffect}, parityDelta={managedVsStatic}");

            logger.Pass("B11 Lottie gradient differential: static, pure-Swift dynamic, and managed dynamic providers produced the same observable render effect");
            results.Pass("Provider_GradientDifferential");
        }
        catch (Exception ex)
        {
            logger.Fail($"B11 Lottie gradient differential: {ex.GetType().Name}: {ex.Message}");
            results.Fail("Provider_GradientDifferential", $"{ex.GetType().Name}: {ex.Message}");
        }
    }
}
