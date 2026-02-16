// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Text;
using Foundation;
using UIKit;
using Swift;
using Swift.Nuke;
using Swift.Runtime;

namespace NukeSimTests;

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
            Text = "Nuke Simulator Tests",
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
        // ImagePipeline type metadata — verifies the Swift runtime initializes and types are resolvable
        try
        {
            var metadata = SwiftObjectHelper<ImagePipeline>.GetTypeMetadata();
            logger.Info($"ImagePipeline metadata size: {metadata.Size}");
            if (metadata.Size > 0)
            {
                logger.Pass("ImagePipeline metadata");
                results.Pass("ImagePipeline_Metadata");
            }
            else
            {
                logger.Fail("ImagePipeline metadata: size is 0");
                results.Fail("ImagePipeline_Metadata", "Size is 0");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"ImagePipeline metadata: {ex.Message}");
            results.Fail("ImagePipeline_Metadata", ex.Message);
        }
    }

    private void RunLibraryTests(TestLogger logger, TestResults results)
    {
        // ImagePipeline.Shared singleton access
        logger.Info("--- Singleton Access ---");
        try
        {
            var pipeline = ImagePipeline.Shared;
            logger.Info($"ImagePipeline.Shared: {pipeline}");
            if (pipeline != null)
            {
                logger.Pass("ImagePipeline.Shared access");
                results.Pass("ImagePipeline_Shared");
            }
            else
            {
                logger.Fail("ImagePipeline.Shared: returned null");
                results.Fail("ImagePipeline_Shared", "Returned null");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"ImagePipeline.Shared: {ex.Message}");
            results.Fail("ImagePipeline_Shared", ex.Message);
        }

        // ImageRequest construction + Description property
        logger.Info("--- ImageRequest ---");
        try
        {
            var request = new ImageRequest("https://example.com/test.jpg");
            var desc = request.Description;
            logger.Info($"ImageRequest description: {desc.Substring(0, Math.Min(60, desc.Length))}...");
            if (!string.IsNullOrEmpty(desc))
            {
                logger.Pass("ImageRequest construction + Description");
                results.Pass("ImageRequest_Construction");
            }
            else
            {
                logger.Fail("ImageRequest: description is empty");
                results.Fail("ImageRequest_Construction", "Empty description");
            }
            request.Dispose();
        }
        catch (Exception ex)
        {
            logger.Fail($"ImageRequest construction: {ex.Message}");
            results.Fail("ImageRequest_Construction", ex.Message);
        }

        // Priority enum cases
        logger.Info("--- Priority Enum ---");
        try
        {
            var veryLow = ImageRequest.PriorityInfo.VeryLow;
            var low = ImageRequest.PriorityInfo.Low;
            var normal = ImageRequest.PriorityInfo.Normal;
            var high = ImageRequest.PriorityInfo.High;
            var veryHigh = ImageRequest.PriorityInfo.VeryHigh;

            var tags = new[]
            {
                veryLow.Tag, low.Tag, normal.Tag, high.Tag, veryHigh.Tag,
            };

            logger.Info($"Priority tags: {string.Join(", ", tags)}");

            // All 5 should be distinct tag values
            var distinctCount = tags.Distinct().Count();
            if (distinctCount == 5)
            {
                logger.Pass("Priority enum: 5 distinct cases");
                results.Pass("Priority_EnumCases");
            }
            else
            {
                logger.Fail($"Priority enum: expected 5 distinct, got {distinctCount}");
                results.Fail("Priority_EnumCases", $"Expected 5 distinct, got {distinctCount}");
            }

            veryLow.Dispose();
            low.Dispose();
            normal.Dispose();
            high.Dispose();
            veryHigh.Dispose();
        }
        catch (Exception ex)
        {
            logger.Fail($"Priority enum: {ex.Message}");
            results.Fail("Priority_EnumCases", ex.Message);
        }

        // Priority FromRawValue with invalid value
        try
        {
            var invalid = ImageRequest.PriorityInfo.FromRawValue(999);
            if (invalid == null)
            {
                logger.Pass("Priority FromRawValue(999) returned null");
                results.Pass("Priority_InvalidRawValue");
            }
            else
            {
                logger.Fail("Priority FromRawValue(999) should return null");
                results.Fail("Priority_InvalidRawValue", "Expected null for invalid raw value");
                invalid.Dispose();
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"Priority FromRawValue: {ex.Message}");
            results.Fail("Priority_InvalidRawValue", ex.Message);
        }

        // Options static properties
        logger.Info("--- Options ---");
        try
        {
            var disableMemoryReads = ImageRequest.OptionsInfo.DisableMemoryCacheReads;
            var disableMemoryWrites = ImageRequest.OptionsInfo.DisableMemoryCacheWrites;
            var disableDiskReads = ImageRequest.OptionsInfo.DisableDiskCacheReads;
            var disableDiskWrites = ImageRequest.OptionsInfo.DisableDiskCacheWrites;

            logger.Info($"DisableMemoryCacheReads: {disableMemoryReads.GetType().Name}");
            logger.Info($"DisableMemoryCacheWrites: {disableMemoryWrites.GetType().Name}");
            logger.Info($"DisableDiskCacheReads: {disableDiskReads.GetType().Name}");
            logger.Info($"DisableDiskCacheWrites: {disableDiskWrites.GetType().Name}");

            logger.Pass("Options static properties");
            results.Pass("Options_StaticProperties");
        }
        catch (Exception ex)
        {
            logger.Fail($"Options static properties: {ex.Message}");
            results.Fail("Options_StaticProperties", ex.Message);
        }

        // ImageProcessingContext struct metadata
        logger.Info("--- ImageProcessingContext ---");
        try
        {
            var metadata = SwiftObjectHelper<ImageProcessingContext>.GetTypeMetadata();
            logger.Info($"ImageProcessingContext metadata size: {metadata.Size}");
            if (metadata.Size > 0)
            {
                logger.Pass("ImageProcessingContext metadata");
                results.Pass("ImageProcessingContext_Metadata");
            }
            else
            {
                logger.Fail("ImageProcessingContext metadata: size is 0");
                results.Fail("ImageProcessingContext_Metadata", "Size is 0");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"ImageProcessingContext metadata: {ex.Message}");
            results.Fail("ImageProcessingContext_Metadata", ex.Message);
        }
    }
}

#endregion
