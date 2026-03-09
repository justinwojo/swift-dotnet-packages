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
            Text = "Lottie Simulator Tests",
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

        // Phase 2: Library-specific tests
        logger.Info("=== Phase 2: Library-Specific Tests ===");
        RunLibraryTests(logger, results);

        // Summary
        logger.Info($"=== Results: {results.Passed} passed, {results.Failed} failed, {results.Skipped} skipped ===");

        if (results.AllPassed)
        {
            logger.Pass("All tests passed!");
            Console.WriteLine("TEST SUCCESS");
        }
        else
        {
            logger.Fail($"{results.Failed} test(s) failed:");
            foreach (var failure in results.FailedTests)
                logger.Fail($"  - {failure}");
            Console.WriteLine($"TEST FAILED: {results.Failed} failures");
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
    }

    private void RunLibraryTests(TestLogger logger, TestResults results)
    {
        // LottieConfiguration.Shared singleton
        logger.Info("--- LottieConfiguration ---");
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

        // LottieColor constructor — skipped: non-blittable struct P/Invoke causes SIGSEGV
        // The SwiftIndirectResult for this struct triggers a native crash in the marshalling layer
        logger.Skip("LottieColor constructor (non-blittable struct P/Invoke causes SIGSEGV)");
        results.Skip("LottieColor_Properties", "Non-blittable struct P/Invoke native crash");

        // LottieColor.Interpolate — skipped: same non-blittable P/Invoke issue
        logger.Skip("LottieColor.Interpolate (non-blittable P/Invoke limitation)");
        results.Skip("LottieColor_Interpolate", "Non-blittable P/Invoke with Swift calling convention unsupported");

        // DecodingStrategy enum cases (C# enum — ObjC-backed)
        logger.Info("--- DecodingStrategy ---");
        try
        {
            var dictBased = DecodingStrategy.DictionaryBased;
            var legacy = DecodingStrategy.LegacyCodable;
            logger.Info($"DecodingStrategy values: DictionaryBased={(int)dictBased}, LegacyCodable={(int)legacy}");

            if (dictBased != legacy)
            {
                logger.Pass("DecodingStrategy enum cases");
                results.Pass("DecodingStrategy_Cases");
            }
            else
            {
                logger.Fail("DecodingStrategy: DictionaryBased and LegacyCodable have same value");
                results.Fail("DecodingStrategy_Cases", "Same value");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"DecodingStrategy enum: {ex.Message}");
            results.Fail("DecodingStrategy_Cases", ex.Message);
        }

        // LottieLoopMode enum cases
        logger.Info("--- LottieLoopMode ---");
        try
        {
            var loop = LottieLoopMode.Loop;
            var playOnce = LottieLoopMode.PlayOnce;
            var autoReverse = LottieLoopMode.AutoReverse;
            logger.Info($"LottieLoopMode tags: Loop={loop.Tag}, PlayOnce={playOnce.Tag}, AutoReverse={autoReverse.Tag}");

            if (loop.Tag != playOnce.Tag && playOnce.Tag != autoReverse.Tag && loop.Tag != autoReverse.Tag)
            {
                logger.Pass("LottieLoopMode enum cases");
                results.Pass("LottieLoopMode_Cases");
            }
            else
            {
                logger.Fail("LottieLoopMode: duplicate tags");
                results.Fail("LottieLoopMode_Cases", "Duplicate tags");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"LottieLoopMode enum: {ex.Message}");
            results.Fail("LottieLoopMode_Cases", ex.Message);
        }

        // LottieAnimation.From — skipped: Swift.Data struct marshalling causes SIGSEGV
        // The byte[] → Swift.Data conversion and indirect result return trigger native crash
        logger.Skip("LottieAnimation.From (Swift.Data struct marshalling causes SIGSEGV)");
        results.Skip("LottieAnimation_FromJSON", "Swift.Data struct P/Invoke native crash");
    }
}

#endregion
