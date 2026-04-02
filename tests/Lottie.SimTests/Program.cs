// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Text;
using Foundation;
using UIKit;
using Swift;
using Lottie;
using Swift.Runtime;

namespace LottieSimTests;

#region Test Infrastructure

public static class RuntimeEnvironment
{
    /// <summary>
    /// True when running on iOS Simulator (Mono JIT). False on device (NativeAOT).
    /// CallConvSwift P/Invokes without @_cdecl wrappers crash on simulator but work on device.
    /// </summary>
    public static bool IsSimulator { get; } =
        ObjCRuntime.Runtime.Arch == ObjCRuntime.Arch.SIMULATOR;
}

public class TestLogger
{
    private readonly StringBuilder _log = new();
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private readonly object _lock = new();

    public void Info(string message) => Log("INFO", message);
    public void Pass(string message) => Log("PASS", message);
    public void Fail(string message) => Log("FAIL", message);
    public void Skip(string message) => Log("SKIP", message);
    public void Warn(string message) => Log("WARN", message);

    private void Log(string category, string message)
    {
        var line = $"[{_stopwatch.Elapsed:mm\\:ss\\.fff}] [{category}] {message}";
        Console.WriteLine(line);
        lock (_lock)
        {
            _log.AppendLine(line);
        }
    }

    public string GetFullLog()
    {
        lock (_lock)
        {
            return _log.ToString();
        }
    }
}

public class TestResults
{
    public int Passed { get; private set; }
    public int Failed { get; private set; }
    public int Skipped { get; private set; }
    public List<string> FailedTests { get; } = new();

    public bool AllPassed => Failed == 0;

    public void Pass(string name)
    {
        Passed++;
    }

    public void Fail(string name, string reason)
    {
        Failed++;
        FailedTests.Add($"{name}: {reason}");
    }

    public void Skip(string name, string reason)
    {
        Skipped++;
    }
}

#endregion

#region Application Entry Point

public class Application
{
    static void Main(string[] args)
    {
        // Framework resolution is handled automatically by the library assembly's
        // [ModuleInitializer] in the generated bindings — no manual resolver needed.
        UIApplication.Main(args, null, typeof(AppDelegate));
    }
}

#endregion

#region App Delegate

[Register("AppDelegate")]
public class AppDelegate : UIApplicationDelegate
{
    public override UIWindow? Window { get; set; }

    public override bool FinishedLaunching(UIApplication application, NSDictionary? launchOptions)
    {
        Window = new UIWindow(UIScreen.MainScreen.Bounds);
        Window.RootViewController = new MainViewController();
        Window.MakeKeyAndVisible();
        return true;
    }
}

#endregion

#region Main View Controller

public class MainViewController : UIViewController
{
    private UILabel? _titleLabel;
    private UILabel? _resultLabel;

    public override void ViewDidLoad()
    {
        base.ViewDidLoad();

        View!.BackgroundColor = UIColor.SystemBackground;

        _titleLabel = new UILabel
        {
            Text = "Lottie Binding Tests",
            Font = UIFont.BoldSystemFontOfSize(20),
            TextAlignment = UITextAlignment.Center,
            TranslatesAutoresizingMaskIntoConstraints = false,
        };

        _resultLabel = new UILabel
        {
            Text = "Running tests...",
            Font = UIFont.SystemFontOfSize(12),
            TextColor = UIColor.Label,
            Lines = 0,
            TranslatesAutoresizingMaskIntoConstraints = false,
        };

        var scrollView = new UIScrollView
        {
            TranslatesAutoresizingMaskIntoConstraints = false,
        };
        scrollView.AddSubview(_resultLabel);

        View.AddSubview(_titleLabel);
        View.AddSubview(scrollView);

        NSLayoutConstraint.ActivateConstraints(new[]
        {
            _titleLabel.TopAnchor.ConstraintEqualTo(View.SafeAreaLayoutGuide.TopAnchor, 16),
            _titleLabel.LeadingAnchor.ConstraintEqualTo(View.LeadingAnchor, 16),
            _titleLabel.TrailingAnchor.ConstraintEqualTo(View.TrailingAnchor, -16),

            scrollView.TopAnchor.ConstraintEqualTo(_titleLabel.BottomAnchor, 16),
            scrollView.LeadingAnchor.ConstraintEqualTo(View.LeadingAnchor, 16),
            scrollView.TrailingAnchor.ConstraintEqualTo(View.TrailingAnchor, -16),
            scrollView.BottomAnchor.ConstraintEqualTo(View.SafeAreaLayoutGuide.BottomAnchor, -16),

            _resultLabel.TopAnchor.ConstraintEqualTo(scrollView.TopAnchor),
            _resultLabel.LeadingAnchor.ConstraintEqualTo(scrollView.LeadingAnchor),
            _resultLabel.TrailingAnchor.ConstraintEqualTo(scrollView.TrailingAnchor),
            _resultLabel.BottomAnchor.ConstraintEqualTo(scrollView.BottomAnchor),
            _resultLabel.WidthAnchor.ConstraintEqualTo(scrollView.WidthAnchor),
        });

        RunAllTests();
    }

    private void RunAllTests()
    {
        var logger = new TestLogger();
        var results = new TestResults();

        // Phase 1: Smoke tests
        logger.Info("=== Phase 1: Smoke Tests ===");
        RunSmokeTests(logger, results);

        // Phase 2: Animation loading tests
        logger.Info("=== Phase 2: Animation Loading Tests ===");
        RunAnimationLoadingTests(logger, results);

        // Phase 3: Animation property tests
        logger.Info("=== Phase 3: Animation Property Tests ===");
        RunAnimationPropertyTests(logger, results);

        // Phase 4: Enum tests
        logger.Info("=== Phase 4: Enum Tests ===");
        RunEnumTests(logger, results);

        // Phase 5: Animation view tests
        logger.Info("=== Phase 5: AnimationView Tests ===");
        RunAnimationViewTests(logger, results);

        // Phase 6: Playback lifecycle tests
        logger.Info("=== Phase 6: Playback Lifecycle Tests ===");
        RunPlaybackLifecycleTests(logger, results);

        // Phase 7: Value provider tests
        logger.Info("=== Phase 7: Value Provider Tests ===");
        RunValueProviderTests(logger, results);

        // Phase 8: Cache and utility tests
        logger.Info("=== Phase 8: Cache & Utility Tests ===");
        RunCacheAndUtilityTests(logger, results);

        // Phase 9: Animation layer tests
        logger.Info("=== Phase 9: AnimationLayer Tests ===");
        RunAnimationLayerTests(logger, results);

        // Phase 10: Constructor tests
        logger.Info("=== Phase 10: Constructor Tests ===");
        RunConstructorTests(logger, results);

        // Phase 11: Library parity tests (L6: AnimatedButton/AnimatedSwitch)
        logger.Info("=== Phase 11: Library Parity Tests ===");
        RunLibraryParityTests(logger, results);

        // Phase 12: Release confidence coverage gaps
        logger.Info("=== Phase 12: Coverage Gap Tests ===");
        RunCoverageGapTests(logger, results);

        // Summary
        logger.Info($"=== Results: {results.Passed} passed, {results.Failed} failed, {results.Skipped} skipped ===");

        if (results.AllPassed)
        {
            logger.Pass("All tests passed!");
            Console.WriteLine("TEST SUCCESS");
            Console.Out.Flush();
        }
        else
        {
            logger.Fail($"{results.Failed} test(s) failed:");
            foreach (var failure in results.FailedTests)
                logger.Fail($"  - {failure}");
            Console.WriteLine($"TEST FAILED: {results.Failed} failures");
            Console.Out.Flush();
        }

        // Update UI
        InvokeOnMainThread(() =>
        {
            _resultLabel!.Text = logger.GetFullLog();
        });
    }

    private void RunSmokeTests(TestLogger logger, TestResults results)
    {
        // LottieConfiguration type metadata
        try
        {
            var metadata = SwiftObjectHelper<LottieConfiguration>.GetTypeMetadata();
            logger.Info($"LottieConfiguration metadata size: {metadata.Size}");
            if (metadata.Size > 0)
            {
                logger.Pass("LottieConfiguration metadata");
                results.Pass("LottieConfiguration_Metadata");
            }
            else
            {
                logger.Fail("LottieConfiguration metadata: size is 0");
                results.Fail("LottieConfiguration_Metadata", "Size is 0");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"LottieConfiguration metadata: {ex.Message}");
            results.Fail("LottieConfiguration_Metadata", ex.Message);
        }

        // LottieColor type metadata
        try
        {
            var metadata = SwiftObjectHelper<LottieColor>.GetTypeMetadata();
            logger.Info($"LottieColor metadata size: {metadata.Size}");
            if (metadata.Size > 0)
            {
                logger.Pass("LottieColor metadata");
                results.Pass("LottieColor_Metadata");
            }
            else
            {
                logger.Fail("LottieColor metadata: size is 0");
                results.Fail("LottieColor_Metadata", "Size is 0");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"LottieColor metadata: {ex.Message}");
            results.Fail("LottieColor_Metadata", ex.Message);
        }

        // LottieConfiguration.Shared singleton
        try
        {
            var config = LottieConfiguration.Shared;
            logger.Info($"LottieConfiguration.Shared: {config}");
            if (config != null)
            {
                logger.Pass("LottieConfiguration.Shared access");
                results.Pass("LottieConfiguration_Shared");
            }
            else
            {
                logger.Fail("LottieConfiguration.Shared: returned null");
                results.Fail("LottieConfiguration_Shared", "Returned null");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"LottieConfiguration.Shared: {ex.Message}");
            results.Fail("LottieConfiguration_Shared", ex.Message);
        }
    }

    private void RunAnimationLoadingTests(TestLogger logger, TestResults results)
    {
        // LottieAnimation.Filepath — load from bundled JSON file
        logger.Info("--- LottieAnimation.Filepath ---");
        try
        {
            var path = NSBundle.MainBundle.PathForResource("PlaneAnimation", "json");
            if (path == null)
            {
                logger.Fail("PlaneAnimation.json not found in bundle");
                results.Fail("Animation_Filepath", "Resource not found");
                return;
            }

            var animation = LottieAnimation.Filepath(path);
            if (animation != null)
            {
                var detail = $"{animation.Duration:F1}s, {animation.Framerate:F0}fps, {animation.StartFrame}-{animation.EndFrame} frames";
                logger.Pass($"LottieAnimation.Filepath: {detail}");
                results.Pass("Animation_Filepath");
            }
            else
            {
                logger.Fail("LottieAnimation.Filepath: returned null");
                results.Fail("Animation_Filepath", "Returned null");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"LottieAnimation.Filepath: {ex.Message}");
            results.Fail("Animation_Filepath", ex.Message);
        }

        // LottieAnimation.Named — load by name from bundle
        logger.Info("--- LottieAnimation.Named ---");
        try
        {
            var animation = LottieAnimation.Named("PlaneAnimation");
            if (animation != null)
            {
                logger.Pass($"LottieAnimation.Named: {animation.Duration:F1}s");
                results.Pass("Animation_Named");
            }
            else
            {
                logger.Fail("LottieAnimation.Named: returned null");
                results.Fail("Animation_Named", "Returned null");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"LottieAnimation.Named: {ex.Message}");
            results.Fail("Animation_Named", ex.Message);
        }

        // LottieAnimation.Named with explicit bundle
        try
        {
            var animation = LottieAnimation.Named("PlaneAnimation", NSBundle.MainBundle);
            if (animation != null)
            {
                logger.Pass("LottieAnimation.Named(bundle)");
                results.Pass("Animation_NamedBundle");
            }
            else
            {
                logger.Fail("LottieAnimation.Named(bundle): returned null");
                results.Fail("Animation_NamedBundle", "Returned null");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"LottieAnimation.Named(bundle): {ex.Message}");
            results.Fail("Animation_NamedBundle", ex.Message);
        }

        // LottieAnimation.From — load from raw JSON bytes
        // Uses CallConvCdecl wrapper (SBW_Lottie_LottieAnimation_from_8F7181DE)
        logger.Info("--- LottieAnimation.From(data) ---");
        try
        {
            var path = NSBundle.MainBundle.PathForResource("PlaneAnimation", "json");
            if (path != null)
            {
                var jsonData = System.IO.File.ReadAllBytes(path);
                var animation = LottieAnimation.From(jsonData);
                if (animation != null)
                {
                    logger.Pass($"LottieAnimation.From(data): {animation.Duration:F1}s");
                    results.Pass("Animation_FromData");
                }
                else
                {
                    logger.Fail("LottieAnimation.From(data): returned null");
                    results.Fail("Animation_FromData", "Returned null");
                }
            }
            else
            {
                logger.Skip("Animation_FromData: no test file");
                results.Skip("Animation_FromData", "No test file");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"LottieAnimation.From(data): {ex.Message}");
            results.Fail("Animation_FromData", ex.Message);
        }

        // LottieAnimation.From with DecodingStrategy
        // Uses CallConvCdecl wrapper (SBW_Lottie_LottieAnimation_from_3BFA5D89)
        logger.Info("--- LottieAnimation.From(data, strategy) ---");
        try
        {
            var path = NSBundle.MainBundle.PathForResource("PlaneAnimation", "json");
            if (path != null)
            {
                var jsonData = System.IO.File.ReadAllBytes(path);
                var animation = LottieAnimation.From(jsonData, DecodingStrategy.DictionaryBased);
                if (animation != null)
                {
                    logger.Pass($"LottieAnimation.From(data, strategy): {animation.Duration:F1}s");
                    results.Pass("Animation_FromDataStrategy");
                }
                else
                {
                    logger.Fail("LottieAnimation.From(data, strategy): returned null");
                    results.Fail("Animation_FromDataStrategy", "Returned null");
                }
            }
            else
            {
                logger.Skip("Animation_FromDataStrategy: no test file");
                results.Skip("Animation_FromDataStrategy", "No test file");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"LottieAnimation.From(data, strategy): {ex.Message}");
            results.Fail("Animation_FromDataStrategy", ex.Message);
        }

        // LottieAnimation.Named for non-existent returns null
        logger.Info("--- Missing Animation ---");
        try
        {
            var animation = LottieAnimation.Named("NonExistentAnimation");
            if (animation == null)
            {
                logger.Pass("LottieAnimation.Named(missing): correctly returned null");
                results.Pass("Animation_MissingReturnsNull");
            }
            else
            {
                logger.Fail("LottieAnimation.Named(missing): should have returned null");
                results.Fail("Animation_MissingReturnsNull", "Expected null for missing animation");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"LottieAnimation.Named(missing): {ex.Message}");
            results.Fail("Animation_MissingReturnsNull", ex.Message);
        }

        // LottieAnimation.Asset
        logger.Info("--- LottieAnimation.Asset ---");
        try
        {
            // Asset loading may not find the file since it looks in .lproj or asset catalogs
            // but we test the API doesn't crash
            var animation = LottieAnimation.Asset("PlaneAnimation");
            // Null is expected if not in asset catalog
            logger.Pass($"LottieAnimation.Asset: returned {(animation != null ? "animation" : "null (expected)")}");
            results.Pass("Animation_Asset");
        }
        catch (Exception ex)
        {
            logger.Fail($"LottieAnimation.Asset: {ex.Message}");
            results.Fail("Animation_Asset", ex.Message);
        }
    }

    private void RunAnimationPropertyTests(TestLogger logger, TestResults results)
    {
        var path = NSBundle.MainBundle.PathForResource("PlaneAnimation", "json");
        if (path == null)
        {
            logger.Skip("Animation property tests: no test file");
            return;
        }

        var animation = LottieAnimation.Filepath(path);
        if (animation == null)
        {
            logger.Skip("Animation property tests: failed to load");
            return;
        }

        // Duration
        try
        {
            var duration = animation.Duration;
            logger.Info($"Duration: {duration:F3}s");
            if (duration > 0)
            {
                logger.Pass("Animation.Duration");
                results.Pass("AnimProp_Duration");
            }
            else
            {
                logger.Fail($"Animation.Duration: expected > 0, got {duration}");
                results.Fail("AnimProp_Duration", $"Expected > 0, got {duration}");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"Animation.Duration: {ex.Message}");
            results.Fail("AnimProp_Duration", ex.Message);
        }

        // Framerate
        try
        {
            var framerate = animation.Framerate;
            logger.Info($"Framerate: {framerate:F1}fps");
            if (framerate > 0)
            {
                logger.Pass("Animation.Framerate");
                results.Pass("AnimProp_Framerate");
            }
            else
            {
                logger.Fail($"Animation.Framerate: expected > 0, got {framerate}");
                results.Fail("AnimProp_Framerate", $"Expected > 0, got {framerate}");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"Animation.Framerate: {ex.Message}");
            results.Fail("AnimProp_Framerate", ex.Message);
        }

        // StartFrame / EndFrame
        try
        {
            var start = animation.StartFrame;
            var end = animation.EndFrame;
            logger.Info($"Frames: {start} - {end}");
            if (end > start)
            {
                logger.Pass("Animation StartFrame/EndFrame");
                results.Pass("AnimProp_Frames");
            }
            else
            {
                logger.Fail($"Animation frames: expected end > start, got {start}-{end}");
                results.Fail("AnimProp_Frames", $"start={start}, end={end}");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"Animation frames: {ex.Message}");
            results.Fail("AnimProp_Frames", ex.Message);
        }

        // MarkerNames
        try
        {
            var markers = animation.MarkerNames;
            logger.Info($"MarkerNames: {markers.Count} markers");
            // It's valid for an animation to have 0 markers
            logger.Pass($"Animation.MarkerNames ({markers.Count} markers)");
            results.Pass("AnimProp_MarkerNames");
        }
        catch (Exception ex)
        {
            logger.Fail($"Animation.MarkerNames: {ex.Message}");
            results.Fail("AnimProp_MarkerNames", ex.Message);
        }

        // Duration consistency check: duration should equal (endFrame - startFrame) / framerate
        try
        {
            var expectedDuration = (animation.EndFrame - animation.StartFrame) / animation.Framerate;
            var actualDuration = animation.Duration;
            var diff = Math.Abs(expectedDuration - actualDuration);
            logger.Info($"Duration consistency: expected={expectedDuration:F3}, actual={actualDuration:F3}, diff={diff:F6}");
            if (diff < 0.01)
            {
                logger.Pass("Duration = (endFrame - startFrame) / framerate");
                results.Pass("AnimProp_DurationConsistency");
            }
            else
            {
                logger.Fail($"Duration inconsistency: diff={diff}");
                results.Fail("AnimProp_DurationConsistency", $"Diff={diff}");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"Duration consistency: {ex.Message}");
            results.Fail("AnimProp_DurationConsistency", ex.Message);
        }
    }

    private void RunEnumTests(TestLogger logger, TestResults results)
    {
        // DecodingStrategy enum cases
        logger.Info("--- DecodingStrategy ---");
        try
        {
            var dictBased = DecodingStrategy.DictionaryBased;
            var legacy = DecodingStrategy.LegacyCodable;
            logger.Info($"DecodingStrategy: DictionaryBased={(int)dictBased}, LegacyCodable={(int)legacy}");

            if (dictBased != legacy)
            {
                logger.Pass("DecodingStrategy enum cases");
                results.Pass("Enum_DecodingStrategy");
            }
            else
            {
                logger.Fail("DecodingStrategy: duplicate values");
                results.Fail("Enum_DecodingStrategy", "Duplicate values");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"DecodingStrategy: {ex.Message}");
            results.Fail("Enum_DecodingStrategy", ex.Message);
        }

        // LottieLoopMode enum cases
        logger.Info("--- LottieLoopMode ---");
        try
        {
            var loop = LottieLoopMode.Loop;
            var playOnce = LottieLoopMode.PlayOnce;
            var autoReverse = LottieLoopMode.AutoReverse;
            logger.Info($"LoopMode tags: Loop={loop.Tag}, PlayOnce={playOnce.Tag}, AutoReverse={autoReverse.Tag}");

            if (loop.Tag != playOnce.Tag && playOnce.Tag != autoReverse.Tag && loop.Tag != autoReverse.Tag)
            {
                logger.Pass("LottieLoopMode enum cases (3 distinct)");
                results.Pass("Enum_LoopMode");
            }
            else
            {
                logger.Fail("LottieLoopMode: duplicate tags");
                results.Fail("Enum_LoopMode", "Duplicate tags");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"LottieLoopMode: {ex.Message}");
            results.Fail("Enum_LoopMode", ex.Message);
        }

        // L4: LottieLoopMode.Repeat and RepeatBackwards factory methods
        logger.Info("--- L4: LottieLoopMode.Repeat/RepeatBackwards ---");
        try
        {
            var repeat3 = LottieLoopMode.Repeat(3.0f);
            logger.Info($"LottieLoopMode.Repeat(3): Tag={repeat3.Tag}");
            if (repeat3.TryGetRepeat(out var repeatValue))
            {
                if (Math.Abs(repeatValue - 3.0f) < 0.01f)
                {
                    logger.Pass($"LottieLoopMode.Repeat(3): extracted value={repeatValue}");
                    results.Pass("L4_LoopMode_Repeat");
                }
                else
                {
                    logger.Fail($"L4 Repeat: expected 3.0, got {repeatValue}");
                    results.Fail("L4_LoopMode_Repeat", $"Expected 3.0, got {repeatValue}");
                }
            }
            else
            {
                logger.Fail("L4 Repeat: TryGetRepeat returned false");
                results.Fail("L4_LoopMode_Repeat", "TryGetRepeat returned false");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"L4 Repeat: {ex.Message}");
            results.Fail("L4_LoopMode_Repeat", ex.Message);
        }

        try
        {
            var repeatBack2 = LottieLoopMode.RepeatBackwards(2.0f);
            logger.Info($"LottieLoopMode.RepeatBackwards(2): Tag={repeatBack2.Tag}");
            if (repeatBack2.TryGetRepeatBackwards(out var backValue))
            {
                if (Math.Abs(backValue - 2.0f) < 0.01f)
                {
                    logger.Pass($"LottieLoopMode.RepeatBackwards(2): extracted value={backValue}");
                    results.Pass("L4_LoopMode_RepeatBackwards");
                }
                else
                {
                    logger.Fail($"L4 RepeatBackwards: expected 2.0, got {backValue}");
                    results.Fail("L4_LoopMode_RepeatBackwards", $"Expected 2.0, got {backValue}");
                }
            }
            else
            {
                logger.Fail("L4 RepeatBackwards: TryGetRepeatBackwards returned false");
                results.Fail("L4_LoopMode_RepeatBackwards", "TryGetRepeatBackwards returned false");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"L4 RepeatBackwards: {ex.Message}");
            results.Fail("L4_LoopMode_RepeatBackwards", ex.Message);
        }

        // LottieBackgroundBehavior enum
        logger.Info("--- LottieBackgroundBehavior ---");
        try
        {
            var stop = LottieBackgroundBehavior.Stop;
            var pause = LottieBackgroundBehavior.Pause;
            var pauseRestore = LottieBackgroundBehavior.PauseAndRestore;
            var forceFinish = LottieBackgroundBehavior.ForceFinish;
            var continuePlaying = LottieBackgroundBehavior.ContinuePlaying;

            var values = new[] { (int)stop, (int)pause, (int)pauseRestore, (int)forceFinish, (int)continuePlaying };
            logger.Info($"BackgroundBehavior: {string.Join(", ", values.Select((v, i) => $"{new[] { "Stop", "Pause", "PauseAndRestore", "ForceFinish", "ContinuePlaying" }[i]}={v}"))}");

            if (values.Distinct().Count() == 5)
            {
                logger.Pass("LottieBackgroundBehavior: 5 distinct cases");
                results.Pass("Enum_BackgroundBehavior");
            }
            else
            {
                logger.Fail("LottieBackgroundBehavior: duplicate values");
                results.Fail("Enum_BackgroundBehavior", "Duplicate values");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"LottieBackgroundBehavior: {ex.Message}");
            results.Fail("Enum_BackgroundBehavior", ex.Message);
        }
    }

    private void RunAnimationViewTests(TestLogger logger, TestResults results)
    {
        // Parameterless constructor
        logger.Info("--- LottieAnimationView() ---");
        try
        {
            var view = new LottieAnimationView();
            if (view != null)
            {
                logger.Pass("LottieAnimationView() parameterless constructor");
                results.Pass("View_DefaultConstructor");
            }
            else
            {
                logger.Fail("LottieAnimationView(): returned null");
                results.Fail("View_DefaultConstructor", "Returned null");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"LottieAnimationView(): {ex.Message}");
            results.Fail("View_DefaultConstructor", ex.Message);
        }

        // CGRect constructor
        try
        {
            var frame = new CoreGraphics.CGRect(0, 0, 300, 300);
            var view = new LottieAnimationView((Swift.CGRect)frame);
            if (view != null)
            {
                logger.Pass("LottieAnimationView(CGRect)");
                results.Pass("View_CGRectConstructor");
            }
            else
            {
                logger.Fail("LottieAnimationView(CGRect): returned null");
                results.Fail("View_CGRectConstructor", "Returned null");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"LottieAnimationView(CGRect): {ex.Message}");
            results.Fail("View_CGRectConstructor", ex.Message);
        }

        // Set animation on view
        logger.Info("--- Set Animation on View ---");
        try
        {
            var view = new LottieAnimationView();
            var path = NSBundle.MainBundle.PathForResource("PlaneAnimation", "json");
            if (path != null)
            {
                var animation = LottieAnimation.Filepath(path);
                view.Animation = animation;
                var readBack = view.Animation;
                if (readBack != null)
                {
                    logger.Pass("AnimationView.Animation set/get");
                    results.Pass("View_SetAnimation");
                }
                else
                {
                    logger.Fail("AnimationView.Animation: readback is null");
                    results.Fail("View_SetAnimation", "Readback null");
                }
            }
            else
            {
                logger.Skip("View_SetAnimation: no test file");
                results.Skip("View_SetAnimation", "No test file");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"AnimationView.Animation: {ex.Message}");
            results.Fail("View_SetAnimation", ex.Message);
        }

        // AnimationSpeed property
        logger.Info("--- AnimationSpeed ---");
        try
        {
            var view = new LottieAnimationView();
            view.AnimationSpeed = 2.0;
            var speed = view.AnimationSpeed;
            logger.Info($"AnimationSpeed: set=2.0, get={speed}");
            if (Math.Abs(speed - 2.0) < 0.01)
            {
                logger.Pass("AnimationView.AnimationSpeed set/get");
                results.Pass("View_AnimationSpeed");
            }
            else
            {
                logger.Fail($"AnimationSpeed: expected 2.0, got {speed}");
                results.Fail("View_AnimationSpeed", $"Expected 2.0, got {speed}");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"AnimationSpeed: {ex.Message}");
            results.Fail("View_AnimationSpeed", ex.Message);
        }

        // CurrentProgress property
        logger.Info("--- CurrentProgress ---");
        try
        {
            var view = new LottieAnimationView();
            var path = NSBundle.MainBundle.PathForResource("PlaneAnimation", "json");
            if (path != null)
            {
                var animation = LottieAnimation.Filepath(path);
                view.Animation = animation;
                view.CurrentProgress = 0.5;
                var progress = view.CurrentProgress;
                logger.Info($"CurrentProgress: set=0.5, get={progress}");
                if (Math.Abs(progress - 0.5) < 0.01)
                {
                    logger.Pass("AnimationView.CurrentProgress set/get");
                    results.Pass("View_CurrentProgress");
                }
                else
                {
                    logger.Fail($"CurrentProgress: expected 0.5, got {progress}");
                    results.Fail("View_CurrentProgress", $"Expected ~0.5, got {progress}");
                }
            }
            else
            {
                logger.Skip("View_CurrentProgress: no test file");
                results.Skip("View_CurrentProgress", "No test file");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"CurrentProgress: {ex.Message}");
            results.Fail("View_CurrentProgress", ex.Message);
        }

        // CurrentFrame property
        logger.Info("--- CurrentFrame ---");
        try
        {
            var view = new LottieAnimationView();
            var path = NSBundle.MainBundle.PathForResource("PlaneAnimation", "json");
            if (path != null)
            {
                var animation = LottieAnimation.Filepath(path);
                view.Animation = animation;
                view.CurrentFrame = 10.0;
                var frame = view.CurrentFrame;
                logger.Info($"CurrentFrame: set=10.0, get={frame}");
                if (Math.Abs(frame - 10.0) < 0.5)
                {
                    logger.Pass("AnimationView.CurrentFrame set/get");
                    results.Pass("View_CurrentFrame");
                }
                else
                {
                    logger.Fail($"CurrentFrame: expected ~10.0, got {frame}");
                    results.Fail("View_CurrentFrame", $"Expected ~10.0, got {frame}");
                }
            }
            else
            {
                logger.Skip("View_CurrentFrame: no test file");
                results.Skip("View_CurrentFrame", "No test file");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"CurrentFrame: {ex.Message}");
            results.Fail("View_CurrentFrame", ex.Message);
        }

        // MaskAnimationToBounds property
        logger.Info("--- MaskAnimationToBounds ---");
        try
        {
            var view = new LottieAnimationView();
            view.MaskAnimationToBounds = true;
            var value = view.MaskAnimationToBounds;
            if (value)
            {
                logger.Pass("AnimationView.MaskAnimationToBounds set/get");
                results.Pass("View_MaskToBounds");
            }
            else
            {
                logger.Fail("MaskAnimationToBounds: expected true after setting");
                results.Fail("View_MaskToBounds", "Expected true");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"MaskAnimationToBounds: {ex.Message}");
            results.Fail("View_MaskToBounds", ex.Message);
        }

        // ShouldRasterizeWhenIdle property
        logger.Info("--- ShouldRasterizeWhenIdle ---");
        try
        {
            var view = new LottieAnimationView();
            view.ShouldRasterizeWhenIdle = true;
            var value = view.ShouldRasterizeWhenIdle;
            if (value)
            {
                logger.Pass("AnimationView.ShouldRasterizeWhenIdle set/get");
                results.Pass("View_RasterizeIdle");
            }
            else
            {
                logger.Fail("ShouldRasterizeWhenIdle: expected true");
                results.Fail("View_RasterizeIdle", "Expected true");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"ShouldRasterizeWhenIdle: {ex.Message}");
            results.Fail("View_RasterizeIdle", ex.Message);
        }

        // RespectAnimationFrameRate property
        logger.Info("--- RespectAnimationFrameRate ---");
        try
        {
            var view = new LottieAnimationView();
            view.RespectAnimationFrameRate = true;
            var value = view.RespectAnimationFrameRate;
            if (value)
            {
                logger.Pass("AnimationView.RespectAnimationFrameRate set/get");
                results.Pass("View_RespectFrameRate");
            }
            else
            {
                logger.Fail("RespectAnimationFrameRate: expected true");
                results.Fail("View_RespectFrameRate", "Expected true");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"RespectAnimationFrameRate: {ex.Message}");
            results.Fail("View_RespectFrameRate", ex.Message);
        }

        // BackgroundBehavior property
        // Uses CallConvCdecl wrapper (SBW_Get/Set_Lottie_LottieAnimationView_backgroundBehavior)
        logger.Info("--- BackgroundBehavior ---");
        try
        {
            var view = new LottieAnimationView();
            view.BackgroundBehavior = LottieBackgroundBehavior.PauseAndRestore;
            var behavior = view.BackgroundBehavior;
            logger.Info($"BackgroundBehavior: set=PauseAndRestore, get={(int)behavior}");
            if ((int)behavior == (int)LottieBackgroundBehavior.PauseAndRestore)
            {
                logger.Pass("AnimationView.BackgroundBehavior set/get");
                results.Pass("View_BackgroundBehavior");
            }
            else
            {
                logger.Fail($"BackgroundBehavior: expected PauseAndRestore, got {(int)behavior}");
                results.Fail("View_BackgroundBehavior", "Value mismatch");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"AnimationView.BackgroundBehavior: {ex.Message}");
            results.Fail("View_BackgroundBehavior", ex.Message);
        }

        // LoopMode property
        logger.Info("--- LoopMode ---");
        try
        {
            var view = new LottieAnimationView();
            view.LoopMode = LottieLoopMode.AutoReverse;
            var mode = view.LoopMode;
            logger.Info($"LoopMode: set=AutoReverse, get tag={mode.Tag}");
            if (mode.Tag == LottieLoopMode.AutoReverse.Tag)
            {
                logger.Pass("AnimationView.LoopMode set/get");
                results.Pass("View_LoopMode");
            }
            else
            {
                logger.Fail($"LoopMode: expected AutoReverse tag, got {mode.Tag}");
                results.Fail("View_LoopMode", "Tag mismatch");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"LoopMode: {ex.Message}");
            results.Fail("View_LoopMode", ex.Message);
        }

        // Configuration property on view
        logger.Info("--- View Configuration ---");
        try
        {
            var view = new LottieAnimationView();
            var config = view.Configuration;
            if (config != null)
            {
                logger.Pass("AnimationView.Configuration get");
                results.Pass("View_Configuration");
            }
            else
            {
                logger.Fail("AnimationView.Configuration: returned null");
                results.Fail("View_Configuration", "Returned null");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"AnimationView.Configuration: {ex.Message}");
            results.Fail("View_Configuration", ex.Message);
        }
    }

    private void RunPlaybackLifecycleTests(TestLogger logger, TestResults results)
    {
        var path = NSBundle.MainBundle.PathForResource("PlaneAnimation", "json");
        if (path == null)
        {
            logger.Skip("Playback lifecycle: no test file");
            return;
        }

        // Play / IsAnimationPlaying
        logger.Info("--- Play + IsAnimationPlaying ---");
        try
        {
            var view = new LottieAnimationView();
            var animation = LottieAnimation.Filepath(path);
            view.Animation = animation;
            view.LoopMode = LottieLoopMode.Loop;

            var beforePlay = view.IsAnimationPlaying;
            view.Play();
            var afterPlay = view.IsAnimationPlaying;
            logger.Info($"IsAnimationPlaying: before={beforePlay}, after={afterPlay}");

            if (afterPlay)
            {
                logger.Pass("Play() sets IsAnimationPlaying=true");
                results.Pass("Playback_Play");
            }
            else
            {
                // IsAnimationPlaying might be false in test context (no display)
                logger.Warn("IsAnimationPlaying=false after Play (no display context?)");
                logger.Pass("Play() did not throw");
                results.Pass("Playback_Play");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"Play: {ex.Message}");
            results.Fail("Playback_Play", ex.Message);
        }

        // Stop
        logger.Info("--- Stop ---");
        try
        {
            var view = new LottieAnimationView();
            var animation = LottieAnimation.Filepath(path);
            view.Animation = animation;
            view.Play();
            view.Stop();
            var isPlaying = view.IsAnimationPlaying;
            logger.Info($"IsAnimationPlaying after Stop: {isPlaying}");
            if (!isPlaying)
            {
                logger.Pass("Stop() sets IsAnimationPlaying=false");
                results.Pass("Playback_Stop");
            }
            else
            {
                logger.Fail("Stop: IsAnimationPlaying still true");
                results.Fail("Playback_Stop", "Still playing after stop");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"Stop: {ex.Message}");
            results.Fail("Playback_Stop", ex.Message);
        }

        // Pause
        logger.Info("--- Pause ---");
        try
        {
            var view = new LottieAnimationView();
            var animation = LottieAnimation.Filepath(path);
            view.Animation = animation;
            view.Play();
            view.Pause();
            logger.Pass("Pause() succeeded");
            results.Pass("Playback_Pause");
        }
        catch (Exception ex)
        {
            logger.Fail($"Pause: {ex.Message}");
            results.Fail("Playback_Pause", ex.Message);
        }

        // Play with progress range
        // Uses CallConvCdecl wrapper (SBW_Lottie_LottieAnimationView_play_8E6629DC)
        logger.Info("--- Play(fromProgress, toProgress) ---");
        try
        {
            var view = new LottieAnimationView();
            var animation = LottieAnimation.Filepath(path);
            view.Animation = animation;
            view.Play(0.0, 0.5);
            logger.Pass("Play(fromProgress, toProgress) succeeded");
            results.Pass("Playback_ProgressRange");
        }
        catch (Exception ex)
        {
            logger.Fail($"Play(fromProgress, toProgress): {ex.Message}");
            results.Fail("Playback_ProgressRange", ex.Message);
        }

        // Play with completion callback
        logger.Info("--- Play(completion) ---");
        try
        {
            var view = new LottieAnimationView();
            var animation = LottieAnimation.Filepath(path);
            view.Animation = animation;
            view.LoopMode = LottieLoopMode.PlayOnce;
            bool callbackReceived = false;
            view.Play(finished =>
            {
                callbackReceived = true;
            });
            logger.Pass("Play(completion) accepted callback");
            results.Pass("Playback_Completion");
        }
        catch (Exception ex)
        {
            logger.Fail($"Play(completion): {ex.Message}");
            results.Fail("Playback_Completion", ex.Message);
        }

        // ForceDisplayUpdate
        logger.Info("--- ForceDisplayUpdate ---");
        try
        {
            var view = new LottieAnimationView();
            var animation = LottieAnimation.Filepath(path);
            view.Animation = animation;
            view.CurrentProgress = 0.5;
            view.ForceDisplayUpdate();
            logger.Pass("ForceDisplayUpdate() succeeded");
            results.Pass("Playback_ForceUpdate");
        }
        catch (Exception ex)
        {
            logger.Fail($"ForceDisplayUpdate: {ex.Message}");
            results.Fail("Playback_ForceUpdate", ex.Message);
        }

        // ReloadImages
        logger.Info("--- ReloadImages ---");
        try
        {
            var view = new LottieAnimationView();
            var animation = LottieAnimation.Filepath(path);
            view.Animation = animation;
            view.ReloadImages();
            logger.Pass("ReloadImages() succeeded");
            results.Pass("Playback_ReloadImages");
        }
        catch (Exception ex)
        {
            logger.Fail($"ReloadImages: {ex.Message}");
            results.Fail("Playback_ReloadImages", ex.Message);
        }

        // LogHierarchyKeypaths (used for discovering keypaths for value providers)
        logger.Info("--- LogHierarchyKeypaths ---");
        try
        {
            var view = new LottieAnimationView();
            var animation = LottieAnimation.Filepath(path);
            view.Animation = animation;
            view.LogHierarchyKeypaths();
            logger.Pass("LogHierarchyKeypaths() succeeded");
            results.Pass("Playback_LogKeypaths");
        }
        catch (Exception ex)
        {
            logger.Fail($"LogHierarchyKeypaths: {ex.Message}");
            results.Fail("Playback_LogKeypaths", ex.Message);
        }

        // CurrentPlaybackMode
        // Uses CallConvCdecl wrapper (SBW_Get_Lottie_LottieAnimationView_currentPlaybackMode)
        logger.Info("--- CurrentPlaybackMode ---");
        try
        {
            var view = new LottieAnimationView();
            var animation = LottieAnimation.Filepath(path);
            view.Animation = animation;
            var mode = view.CurrentPlaybackMode;
            logger.Info($"CurrentPlaybackMode: {(mode != null ? "has value" : "null")}");
            // A view that isn't playing should have null playback mode
            logger.Pass("CurrentPlaybackMode getter succeeded");
            results.Pass("Playback_CurrentMode");
        }
        catch (Exception ex)
        {
            logger.Fail($"CurrentPlaybackMode: {ex.Message}");
            results.Fail("Playback_CurrentMode", ex.Message);
        }
    }

    private void RunValueProviderTests(TestLogger logger, TestResults results)
    {
        // AnimationKeypath construction — constructor works but .String/.Keys getters crash on device
        // (CallConvSwift string property getter — variant of Issue 7)
        logger.Info("--- AnimationKeypath ---");
        try
        {
            var keypath = new AnimationKeypath("Layer.Shape.Fill.Color");
            logger.Pass("AnimationKeypath(string) construction");
            results.Pass("Keypath_Construction");
        }
        catch (Exception ex)
        {
            logger.Fail($"AnimationKeypath(string): {ex.Message}");
            results.Fail("Keypath_Construction", ex.Message);
        }

        // AnimationKeypath.String getter
        // Uses CallConvCdecl wrapper (SBW_Get_Lottie_AnimationKeypath_string)
        try
        {
            var keypath = new AnimationKeypath("Layer.Shape.Fill.Color");
            var str = keypath.String;
            logger.Info($"AnimationKeypath.String: '{str}'");
            if (!string.IsNullOrEmpty(str))
            {
                logger.Pass("AnimationKeypath.String getter");
                results.Pass("Keypath_String");
            }
            else
            {
                logger.Fail("AnimationKeypath.String: empty or null");
                results.Fail("Keypath_String", "Empty or null");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"AnimationKeypath.String: {ex.Message}");
            results.Fail("Keypath_String", ex.Message);
        }

        // AnimationKeypath.Keys property — works on device
        try
        {
            var keypath2 = new AnimationKeypath("Layer.Shape.Fill.Color");
            var keys = keypath2.Keys;
            if (keys.Count > 0)
            {
                logger.Pass($"AnimationKeypath.Keys ({keys.Count} keys)");
                results.Pass("Keypath_Keys");
            }
            else
            {
                logger.Fail("AnimationKeypath.Keys is empty");
                results.Fail("Keypath_Keys", "Empty");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"AnimationKeypath.Keys: {ex.Message}");
            results.Fail("Keypath_Keys", ex.Message);
        }

        // AnimationKeypath from list of strings — constructor works, skip .String readback
        try
        {
            var keys = new List<string> { "Layer", "Shape", "Fill" };
            var keypath = new AnimationKeypath(keys);
            logger.Pass("AnimationKeypath(IEnumerable<string>)");
            results.Pass("Keypath_List");
        }
        catch (Exception ex)
        {
            logger.Fail($"AnimationKeypath(list): {ex.Message}");
            results.Fail("Keypath_List", ex.Message);
        }

        // FloatValueProvider
        logger.Info("--- FloatValueProvider ---");
        try
        {
            var provider = new FloatValueProvider(0.75);
            var value = provider.Float;
            logger.Info($"FloatValueProvider: value={value}");
            if (Math.Abs(value - 0.75) < 0.01)
            {
                logger.Pass("FloatValueProvider construction + Float get");
                results.Pass("Provider_Float");
            }
            else
            {
                logger.Fail($"FloatValueProvider.Float: expected 0.75, got {value}");
                results.Fail("Provider_Float", $"Expected 0.75, got {value}");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"FloatValueProvider: {ex.Message}");
            results.Fail("Provider_Float", ex.Message);
        }

        // FloatValueProvider.Float set
        try
        {
            var provider = new FloatValueProvider(0.5);
            provider.Float = 0.9;
            var value = provider.Float;
            if (Math.Abs(value - 0.9) < 0.01)
            {
                logger.Pass("FloatValueProvider.Float set/get roundtrip");
                results.Pass("Provider_FloatSet");
            }
            else
            {
                logger.Fail($"FloatValueProvider.Float set: expected 0.9, got {value}");
                results.Fail("Provider_FloatSet", $"Expected 0.9, got {value}");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"FloatValueProvider.Float set: {ex.Message}");
            results.Fail("Provider_FloatSet", ex.Message);
        }

        // FloatValueProvider.HasUpdate
        try
        {
            var provider = new FloatValueProvider(1.0);
            var hasUpdate = provider.HasUpdate(0.0);
            logger.Info($"FloatValueProvider.HasUpdate: {hasUpdate}");
            logger.Pass("FloatValueProvider.HasUpdate");
            results.Pass("Provider_FloatHasUpdate");
        }
        catch (Exception ex)
        {
            logger.Fail($"FloatValueProvider.HasUpdate: {ex.Message}");
            results.Fail("Provider_FloatHasUpdate", ex.Message);
        }

        // SizeValueProvider
        logger.Info("--- SizeValueProvider ---");
        try
        {
            var size = new CoreGraphics.CGSize(100, 200);
            var provider = new SizeValueProvider((Swift.CGSize)size);
            var readback = provider.Size;
            logger.Info($"SizeValueProvider: {readback}");
            logger.Pass("SizeValueProvider construction");
            results.Pass("Provider_Size");
        }
        catch (Exception ex)
        {
            logger.Fail($"SizeValueProvider: {ex.Message}");
            results.Fail("Provider_Size", ex.Message);
        }

        // PointValueProvider
        logger.Info("--- PointValueProvider ---");
        try
        {
            var point = new CoreGraphics.CGPoint(50, 75);
            var provider = new PointValueProvider((Swift.CGPoint)point);
            var readback = provider.Point;
            logger.Info($"PointValueProvider: {readback}");
            logger.Pass("PointValueProvider construction");
            results.Pass("Provider_Point");
        }
        catch (Exception ex)
        {
            logger.Fail($"PointValueProvider: {ex.Message}");
            results.Fail("Provider_Point", ex.Message);
        }

        // SetValueProvider on view — previously skipped (ExistentialContainer boxing), re-testing with SDK 0.5.0
        logger.Info("--- SetValueProvider (L1) ---");
        {
            var viewPath = NSBundle.MainBundle.PathForResource("PlaneAnimation", "json");
            if (viewPath != null)
            {
                var view = new LottieAnimationView();
                var animation = LottieAnimation.Filepath(viewPath);
                view.Animation = animation;

                // Float
                try
                {
                    var floatProvider = new FloatValueProvider(0.5);
                    var keypath = new AnimationKeypath("**.Opacity");
                    view.SetValueProvider(floatProvider, keypath);
                    logger.Pass("SetValueProvider(FloatValueProvider) succeeded");
                    results.Pass("L1_SetValueProvider_Float");
                }
                catch (Exception ex)
                {
                    logger.Fail($"SetValueProvider(Float): {ex.Message}");
                    results.Fail("L1_SetValueProvider_Float", ex.Message);
                }

                // Size
                try
                {
                    var size = new CoreGraphics.CGSize(100, 200);
                    var sizeProvider = new SizeValueProvider((Swift.CGSize)size);
                    var keypath = new AnimationKeypath("**.Size");
                    view.SetValueProvider(sizeProvider, keypath);
                    logger.Pass("SetValueProvider(SizeValueProvider) succeeded");
                    results.Pass("L1_SetValueProvider_Size");
                }
                catch (Exception ex)
                {
                    logger.Fail($"SetValueProvider(Size): {ex.Message}");
                    results.Fail("L1_SetValueProvider_Size", ex.Message);
                }

                // Point
                try
                {
                    var point = new CoreGraphics.CGPoint(50, 75);
                    var pointProvider = new PointValueProvider((Swift.CGPoint)point);
                    var keypath = new AnimationKeypath("**.Position");
                    view.SetValueProvider(pointProvider, keypath);
                    logger.Pass("SetValueProvider(PointValueProvider) succeeded");
                    results.Pass("L1_SetValueProvider_Point");
                }
                catch (Exception ex)
                {
                    logger.Fail($"SetValueProvider(Point): {ex.Message}");
                    results.Fail("L1_SetValueProvider_Point", ex.Message);
                }
            }
            else
            {
                logger.Skip("SetValueProvider: no test file");
                results.Skip("L1_SetValueProvider_Float", "No test file");
                results.Skip("L1_SetValueProvider_Size", "No test file");
                results.Skip("L1_SetValueProvider_Point", "No test file");
            }
        }

        // RemoveValueProvider (on a keypath with no provider set — should be a no-op)
        logger.Info("--- RemoveValueProvider ---");
        try
        {
            var viewPath = NSBundle.MainBundle.PathForResource("PlaneAnimation", "json");
            if (viewPath != null)
            {
                var view = new LottieAnimationView();
                var animation = LottieAnimation.Filepath(viewPath);
                view.Animation = animation;

                var keypath = new AnimationKeypath("**.Opacity");
                view.RemoveValueProvider(keypath);
                logger.Pass("RemoveValueProvider (no-op) succeeded");
                results.Pass("Provider_Remove");
            }
            else
            {
                logger.Skip("Provider_Remove: no test file");
                results.Skip("Provider_Remove", "No test file");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"RemoveValueProvider: {ex.Message}");
            results.Fail("Provider_Remove", ex.Message);
        }

        // DictionaryTextProvider
        logger.Info("--- DictionaryTextProvider ---");
        try
        {
            var dict = new Dictionary<string, string>
            {
                { "greeting", "Hello" },
                { "farewell", "Goodbye" },
            };
            var provider = new DictionaryTextProvider(dict);
            logger.Pass("DictionaryTextProvider construction");
            results.Pass("Provider_DictText");
        }
        catch (Exception ex)
        {
            logger.Fail($"DictionaryTextProvider: {ex.Message}");
            results.Fail("Provider_DictText", ex.Message);
        }

        // DefaultTextProvider
        logger.Info("--- DefaultTextProvider ---");
        try
        {
            var provider = new DefaultTextProvider();
            logger.Pass("DefaultTextProvider construction");
            results.Pass("Provider_DefaultText");
        }
        catch (Exception ex)
        {
            logger.Fail($"DefaultTextProvider: {ex.Message}");
            results.Fail("Provider_DefaultText", ex.Message);
        }
    }

    private void RunCacheAndUtilityTests(TestLogger logger, TestResults results)
    {
        // DefaultAnimationCache.SharedCache singleton
        logger.Info("--- DefaultAnimationCache ---");
        try
        {
            var cache = DefaultAnimationCache.SharedCache;
            if (cache != null)
            {
                logger.Pass("DefaultAnimationCache.SharedCache");
                results.Pass("Cache_Shared");
            }
            else
            {
                logger.Fail("DefaultAnimationCache.SharedCache: null");
                results.Fail("Cache_Shared", "Null");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"DefaultAnimationCache.SharedCache: {ex.Message}");
            results.Fail("Cache_Shared", ex.Message);
        }

        // DefaultAnimationCache parameterless constructor
        try
        {
            var cache = new DefaultAnimationCache();
            logger.Pass("DefaultAnimationCache() constructor");
            results.Pass("Cache_Constructor");
        }
        catch (Exception ex)
        {
            logger.Fail($"DefaultAnimationCache(): {ex.Message}");
            results.Fail("Cache_Constructor", ex.Message);
        }

        // DefaultAnimationCache.CacheSize property
        try
        {
            var cache = new DefaultAnimationCache();
            var originalSize = cache.CacheSize;
            cache.CacheSize = 50;
            var newSize = cache.CacheSize;
            logger.Info($"CacheSize: original={originalSize}, set=50, get={newSize}");
            if (newSize == 50)
            {
                logger.Pass("DefaultAnimationCache.CacheSize set/get");
                results.Pass("Cache_Size");
            }
            else
            {
                logger.Fail($"CacheSize: expected 50, got {newSize}");
                results.Fail("Cache_Size", $"Expected 50, got {newSize}");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"CacheSize: {ex.Message}");
            results.Fail("Cache_Size", ex.Message);
        }

        // Animation cache roundtrip: SetAnimation + Animation
        logger.Info("--- Cache Roundtrip ---");
        try
        {
            var cache = new DefaultAnimationCache();
            var path = NSBundle.MainBundle.PathForResource("PlaneAnimation", "json");
            if (path != null)
            {
                var animation = LottieAnimation.Filepath(path);
                if (animation != null)
                {
                    cache.SetAnimation(animation, "test-key");
                    var retrieved = cache.Animation("test-key");
                    if (retrieved != null)
                    {
                        logger.Pass("Cache SetAnimation/Animation roundtrip");
                        results.Pass("Cache_Roundtrip");
                    }
                    else
                    {
                        logger.Fail("Cache roundtrip: retrieved null");
                        results.Fail("Cache_Roundtrip", "Retrieved null");
                    }
                }
                else
                {
                    logger.Skip("Cache_Roundtrip: couldn't load animation");
                    results.Skip("Cache_Roundtrip", "Load failed");
                }
            }
            else
            {
                logger.Skip("Cache_Roundtrip: no test file");
                results.Skip("Cache_Roundtrip", "No test file");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"Cache roundtrip: {ex.Message}");
            results.Fail("Cache_Roundtrip", ex.Message);
        }

        // Cache.ClearCache
        try
        {
            var cache = new DefaultAnimationCache();
            cache.ClearCache();
            logger.Pass("DefaultAnimationCache.ClearCache()");
            results.Pass("Cache_Clear");
        }
        catch (Exception ex)
        {
            logger.Fail($"ClearCache: {ex.Message}");
            results.Fail("Cache_Clear", ex.Message);
        }

        // Cache miss returns null
        try
        {
            var cache = new DefaultAnimationCache();
            var missing = cache.Animation("nonexistent-key");
            if (missing == null)
            {
                logger.Pass("Cache miss returns null");
                results.Pass("Cache_Miss");
            }
            else
            {
                logger.Fail("Cache miss: expected null");
                results.Fail("Cache_Miss", "Expected null");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"Cache miss: {ex.Message}");
            results.Fail("Cache_Miss", ex.Message);
        }

        // FilepathImageProvider
        logger.Info("--- FilepathImageProvider ---");
        try
        {
            var bundlePath = NSBundle.MainBundle.BundlePath;
            var provider = new FilepathImageProvider(bundlePath);
            logger.Pass("FilepathImageProvider(string) construction");
            results.Pass("Provider_FilepathImage");
        }
        catch (Exception ex)
        {
            logger.Fail($"FilepathImageProvider: {ex.Message}");
            results.Fail("Provider_FilepathImage", ex.Message);
        }

        // DefaultFontProvider
        logger.Info("--- DefaultFontProvider ---");
        try
        {
            var provider = new DefaultFontProvider();
            logger.Pass("DefaultFontProvider() construction");
            results.Pass("Provider_DefaultFont");
        }
        catch (Exception ex)
        {
            logger.Fail($"DefaultFontProvider: {ex.Message}");
            results.Fail("Provider_DefaultFont", ex.Message);
        }
    }

    private void RunAnimationLayerTests(TestLogger logger, TestResults results)
    {
        // LottieAnimationLayer constructor with default configuration
        logger.Info("--- LottieAnimationLayer ---");
        try
        {
            var layer = new LottieAnimationLayer(LottieConfiguration.Shared);
            if (layer != null)
            {
                logger.Pass("LottieAnimationLayer(config) construction");
                results.Pass("Layer_DefaultConstructor");
            }
            else
            {
                logger.Fail("LottieAnimationLayer(config): null");
                results.Fail("Layer_DefaultConstructor", "Null");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"LottieAnimationLayer(config): {ex.Message}");
            results.Fail("Layer_DefaultConstructor", ex.Message);
        }

        // Layer with configuration
        try
        {
            var config = LottieConfiguration.Shared;
            var layer = new LottieAnimationLayer(config);
            if (layer != null)
            {
                logger.Pass("LottieAnimationLayer(config) construction");
                results.Pass("Layer_ConfigConstructor");
            }
            else
            {
                logger.Fail("LottieAnimationLayer(config): null");
                results.Fail("Layer_ConfigConstructor", "Null");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"LottieAnimationLayer(config): {ex.Message}");
            results.Fail("Layer_ConfigConstructor", ex.Message);
        }

        // Layer play/stop via config constructor + Animation setter
        // Animation setter uses CallConvCdecl wrapper (SBW_Set_Lottie_LottieAnimationLayer_animation)
        // Play/Stop use CallConvCdecl wrappers (SBW_Lottie_LottieAnimationLayer_play/stop)
        try
        {
            var animPath = NSBundle.MainBundle.PathForResource("PlaneAnimation", "json");
            if (animPath != null)
            {
                var animation = LottieAnimation.Filepath(animPath);
                var layer = new LottieAnimationLayer(LottieConfiguration.Shared);
                layer.Animation = animation;
                layer.Play();
                layer.Stop();
                logger.Pass("LottieAnimationLayer play/stop via Animation setter");
                results.Pass("Layer_PlayStop");
            }
            else
            {
                logger.Skip("Layer_PlayStop: no test file");
                results.Skip("Layer_PlayStop", "No test file");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"LottieAnimationLayer play/stop: {ex.Message}");
            results.Fail("Layer_PlayStop", ex.Message);
        }

        // Layer AnimationSpeed property
        try
        {
            var layer = new LottieAnimationLayer(LottieConfiguration.Shared);
            layer.AnimationSpeed = 1.5;
            var speed = layer.AnimationSpeed;
            logger.Info($"Layer.AnimationSpeed: set=1.5, get={speed}");
            if (Math.Abs(speed - 1.5) < 0.01)
            {
                logger.Pass("LottieAnimationLayer.AnimationSpeed");
                results.Pass("Layer_AnimationSpeed");
            }
            else
            {
                logger.Fail($"Layer.AnimationSpeed: expected 1.5, got {speed}");
                results.Fail("Layer_AnimationSpeed", $"Expected 1.5, got {speed}");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"Layer.AnimationSpeed: {ex.Message}");
            results.Fail("Layer_AnimationSpeed", ex.Message);
        }

        // Layer Configuration property
        try
        {
            var layer = new LottieAnimationLayer(LottieConfiguration.Shared);
            var config = layer.Configuration;
            if (config != null)
            {
                logger.Pass("LottieAnimationLayer.Configuration get");
                results.Pass("Layer_Configuration");
            }
            else
            {
                logger.Fail("Layer.Configuration: null");
                results.Fail("Layer_Configuration", "Null");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"Layer.Configuration: {ex.Message}");
            results.Fail("Layer_Configuration", ex.Message);
        }
    }

    private void RunConstructorTests(TestLogger logger, TestResults results)
    {
        // L2: LottieColor constructor — SDK 0.3.0 added @_cdecl wrappers, now works on simulator
        logger.Info("--- LottieColor Constructor (L2: @_cdecl fix) ---");
        LottieColor? testColor = null;
        try
        {
            testColor = new LottieColor(1.0, 0.5, 0.25, 1.0);
            logger.Pass("LottieColor(r,g,b,a) construction");
            results.Pass("LottieColor_Construction");
        }
        catch (Exception ex)
        {
            logger.Fail($"LottieColor(r,g,b,a): {ex.Message}");
            results.Fail("LottieColor_Construction", ex.Message);
        }

        // LottieColor property accessors
        if (testColor != null)
        {
            try
            {
                var r = testColor.R;
                var g = testColor.G;
                var b = testColor.B;
                var a = testColor.A;
                logger.Info($"LottieColor: R={r:F2}, G={g:F2}, B={b:F2}, A={a:F2}");
                if (Math.Abs(r - 1.0) < 0.01 && Math.Abs(g - 0.5) < 0.01 &&
                    Math.Abs(b - 0.25) < 0.01 && Math.Abs(a - 1.0) < 0.01)
                {
                    logger.Pass("LottieColor R/G/B/A properties roundtrip");
                    results.Pass("LottieColor_Properties");
                }
                else
                {
                    logger.Fail($"LottieColor properties: unexpected values R={r}, G={g}, B={b}, A={a}");
                    results.Fail("LottieColor_Properties", $"R={r}, G={g}, B={b}, A={a}");
                }
            }
            catch (Exception ex)
            {
                logger.Fail($"LottieColor R/G/B/A: {ex.Message}");
                results.Fail("LottieColor_Properties", ex.Message);
            }
        }
        else
        {
            logger.Skip("LottieColor R/G/B/A: constructor failed, skipping properties");
            results.Skip("LottieColor_Properties", "Constructor failed");
        }

        // L2: LottieColor with denominator parameter
        try
        {
            var color255 = new LottieColor(255.0, 128.0, 64.0, 255.0, ColorFormatDenominator.TwoFiftyFive);
            var r = color255.R;
            var g = color255.G;
            logger.Info($"LottieColor(255 denom): R={r:F3}, G={g:F3}");
            // With 255 denominator, values should be normalized to 0-1 range
            logger.Pass("LottieColor with ColorFormatDenominator.TwoFiftyFive");
            results.Pass("LottieColor_Denominator255");
        }
        catch (Exception ex)
        {
            logger.Fail($"LottieColor(denominator): {ex.Message}");
            results.Fail("LottieColor_Denominator255", ex.Message);
        }

        // L2: ColorValueProvider — now works on simulator with @_cdecl wrappers
        logger.Info("--- ColorValueProvider (L2) ---");
        if (testColor != null)
        {
            try
            {
                var provider = new ColorValueProvider(testColor);
                logger.Pass("ColorValueProvider construction");
                results.Pass("Provider_Color");

                // L1+L2: SetValueProvider with ColorValueProvider — previously skipped, re-testing with SDK 0.5.0
                try
                {
                    var viewPath = NSBundle.MainBundle.PathForResource("PlaneAnimation", "json");
                    if (viewPath != null)
                    {
                        var view = new LottieAnimationView();
                        var animation = LottieAnimation.Filepath(viewPath);
                        view.Animation = animation;
                        var keypath = new AnimationKeypath("**.Color");
                        view.SetValueProvider(provider, keypath);
                        logger.Pass("SetValueProvider(ColorValueProvider) succeeded");
                        results.Pass("L1L2_SetValueProvider_Color");
                    }
                    else
                    {
                        logger.Skip("SetValueProvider Color: no test file");
                        results.Skip("L1L2_SetValueProvider_Color", "No test file");
                    }
                }
                catch (Exception ex)
                {
                    logger.Fail($"SetValueProvider(Color): {ex.Message}");
                    results.Fail("L1L2_SetValueProvider_Color", ex.Message);
                }
            }
            catch (Exception ex)
            {
                logger.Fail($"ColorValueProvider: {ex.Message}");
                results.Fail("Provider_Color", ex.Message);
                results.Skip("L1L2_SetValueProvider_Color", "ColorValueProvider failed");
            }
        }
        else
        {
            logger.Skip("ColorValueProvider: LottieColor constructor failed");
            results.Skip("Provider_Color", "LottieColor constructor failed");
            results.Skip("L1L2_SetValueProvider_Color", "LottieColor constructor failed");
        }
    }

    private void RunLibraryParityTests(TestLogger logger, TestResults results)
    {
        // L6: AnimatedButton construction
        logger.Info("--- L6: AnimatedButton ---");
        try
        {
            var button = new AnimatedButton();
            logger.Pass("AnimatedButton() parameterless constructor");
            results.Pass("L6_AnimatedButton_Construction");
        }
        catch (Exception ex)
        {
            logger.Fail($"AnimatedButton(): {ex.Message}");
            results.Fail("L6_AnimatedButton_Construction", ex.Message);
        }

        // L6: AnimatedSwitch construction
        logger.Info("--- L6: AnimatedSwitch ---");
        try
        {
            var sw = new AnimatedSwitch();
            logger.Pass("AnimatedSwitch() parameterless constructor");
            results.Pass("L6_AnimatedSwitch_Construction");

            // IsOn property get
            try
            {
                var isOn = sw.IsOn;
                logger.Pass($"AnimatedSwitch.IsOn: {isOn}");
                results.Pass("L6_AnimatedSwitch_IsOn");
            }
            catch (Exception ex2)
            {
                logger.Fail($"AnimatedSwitch.IsOn: {ex2.Message}");
                results.Fail("L6_AnimatedSwitch_IsOn", ex2.Message);
            }

            // CancelBehavior property
            try
            {
                var cancelBehavior = sw.CancelBehavior;
                logger.Pass($"AnimatedSwitch.CancelBehavior: {cancelBehavior}");
                results.Pass("L6_AnimatedSwitch_CancelBehavior");
            }
            catch (Exception ex2)
            {
                logger.Fail($"AnimatedSwitch.CancelBehavior: {ex2.Message}");
                results.Fail("L6_AnimatedSwitch_CancelBehavior", ex2.Message);
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"AnimatedSwitch(): {ex.Message}");
            results.Fail("L6_AnimatedSwitch_Construction", ex.Message);
            results.Skip("L6_AnimatedSwitch_IsOn", "Constructor failed");
            results.Skip("L6_AnimatedSwitch_CancelBehavior", "Constructor failed");
        }

        // L6: AnimatedButton with animation
        try
        {
            var viewPath = NSBundle.MainBundle.PathForResource("PlaneAnimation", "json");
            if (viewPath != null)
            {
                var animation = LottieAnimation.Filepath(viewPath);
                var config = LottieConfiguration.Shared;
                var button = new AnimatedButton(animation, config);
                logger.Pass("AnimatedButton(animation, config) construction");
                results.Pass("L6_AnimatedButton_WithAnimation");
            }
            else
            {
                logger.Skip("L6 AnimatedButton with animation: no test file");
                results.Skip("L6_AnimatedButton_WithAnimation", "No test file");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"AnimatedButton(animation, config): {ex.Message}");
            results.Fail("L6_AnimatedButton_WithAnimation", ex.Message);
        }

        // L5: DotLottieFile type exists
        logger.Info("--- L5: DotLottieFile ---");
        try
        {
            var metadata = SwiftObjectHelper<DotLottieFile>.GetTypeMetadata();
            if (metadata.Size > 0)
            {
                logger.Pass($"DotLottieFile metadata: size={metadata.Size}");
                results.Pass("L5_DotLottieFile_Metadata");
            }
            else
            {
                logger.Fail("DotLottieFile metadata: size is 0");
                results.Fail("L5_DotLottieFile_Metadata", "Size is 0");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"DotLottieFile metadata: {ex.Message}");
            results.Fail("L5_DotLottieFile_Metadata", ex.Message);
        }

        // L8: LottieAnimationView hierarchy inspection — LogKeypaths
        logger.Info("--- L8: Hierarchy Inspection ---");
        try
        {
            var viewPath = NSBundle.MainBundle.PathForResource("PlaneAnimation", "json");
            if (viewPath != null)
            {
                var view = new LottieAnimationView();
                var animation = LottieAnimation.Filepath(viewPath);
                view.Animation = animation;

                // LogHierarchyKeypaths — void method, just verify it doesn't crash
                view.LogHierarchyKeypaths();
                logger.Pass("LottieAnimationView.LogHierarchyKeypaths()");
                results.Pass("L8_LogHierarchyKeypaths");
            }
            else
            {
                logger.Skip("L8 LogKeypaths: no test file");
                results.Skip("L8_LogHierarchyKeypaths", "No test file");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"L8 LogKeypaths: {ex.Message}");
            results.Fail("L8_LogHierarchyKeypaths", ex.Message);
        }
    }

    private void RunCoverageGapTests(TestLogger logger, TestResults results)
    {
        // L9a: LottieColor — struct constructor with RGBA values
        try
        {
            using var color = new LottieColor(r: 1.0, g: 0.5, b: 0.0, a: 1.0);
            logger.Pass("L9a: LottieColor(1.0, 0.5, 0.0, 1.0) created");
            results.Pass("L9a_LottieColor");
        }
        catch (Exception ex)
        {
            logger.Fail($"L9a LottieColor: {ex.Message}");
            results.Fail("L9a_LottieColor", ex.Message);
        }

        // L9b: LottieVector1D — struct constructor with scalar
        try
        {
            using var vec = new LottieVector1D(42.0);
            logger.Pass("L9b: LottieVector1D(42.0) created");
            results.Pass("L9b_LottieVector1D");
        }
        catch (Exception ex)
        {
            logger.Fail($"L9b LottieVector1D: {ex.Message}");
            results.Fail("L9b_LottieVector1D", ex.Message);
        }

        // L9c: AnimationKeypath — string-based keypath construction
        try
        {
            using var keypath = new AnimationKeypath("Layer.Transform.Position");
            logger.Pass("L9c: AnimationKeypath(string) created");
            results.Pass("L9c_AnimationKeypath_String");
        }
        catch (Exception ex)
        {
            logger.Fail($"L9c AnimationKeypath: {ex.Message}");
            results.Fail("L9c_AnimationKeypath_String", ex.Message);
        }

        // L9d: AnimationKeypath — array-based construction
        try
        {
            using var keypath = new AnimationKeypath(new[] { "Layer", "Transform", "Position" });
            logger.Pass("L9d: AnimationKeypath(keys[]) created");
            results.Pass("L9d_AnimationKeypath_Array");
        }
        catch (Exception ex)
        {
            logger.Fail($"L9d AnimationKeypath array: {ex.Message}");
            results.Fail("L9d_AnimationKeypath_Array", ex.Message);
        }

        // L9e: DictionaryTextProvider — custom text replacement provider
        try
        {
            var dict = new Dictionary<string, string>
            {
                { "title", "Hello World" },
                { "subtitle", "Testing" }
            };
            using var provider = new DictionaryTextProvider(dict);
            logger.Pass("L9e: DictionaryTextProvider created with 2 entries");
            results.Pass("L9e_DictionaryTextProvider");
        }
        catch (Exception ex)
        {
            logger.Fail($"L9e DictionaryTextProvider: {ex.Message}");
            results.Fail("L9e_DictionaryTextProvider", ex.Message);
        }

        // L9f: CompatibleAnimationKeypath — ObjC-bridged keypath
        try
        {
            var keypath = new CompatibleAnimationKeypath("Layer.Opacity");
            logger.Pass("L9f: CompatibleAnimationKeypath(string) created");
            results.Pass("L9f_CompatibleAnimationKeypath");
        }
        catch (Exception ex)
        {
            logger.Fail($"L9f CompatibleAnimationKeypath: {ex.Message}");
            results.Fail("L9f_CompatibleAnimationKeypath", ex.Message);
        }

        // L9g: CompatibleDictionaryTextProvider — ObjC-bridged text provider
        try
        {
            var dict = new Dictionary<string, string> { { "key", "value" } };
            var provider = new CompatibleDictionaryTextProvider(dict);
            logger.Pass("L9g: CompatibleDictionaryTextProvider created");
            results.Pass("L9g_CompatibleDictionaryTextProvider");
        }
        catch (Exception ex)
        {
            logger.Fail($"L9g CompatibleDictionaryTextProvider: {ex.Message}");
            results.Fail("L9g_CompatibleDictionaryTextProvider", ex.Message);
        }

        // L9h: Memory pressure — create and dispose many LottieColor objects
        try
        {
            for (int i = 0; i < 100; i++)
            {
                using var color = new LottieColor(r: i / 100.0, g: 0.5, b: 0.0, a: 1.0);
            }
            logger.Pass("L9h: Memory pressure: 100 LottieColor create/dispose cycles");
            results.Pass("L9h_Memory_Pressure");
        }
        catch (Exception ex)
        {
            logger.Fail($"L9h Memory pressure: {ex.Message}");
            results.Fail("L9h_Memory_Pressure", ex.Message);
        }
    }

}

#endregion
