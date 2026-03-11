// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Text;
using Foundation;
using UIKit;
using Swift;
using Nuke;
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
            Text = "Nuke Binding Tests",
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

    private async void RunAllTests()
    {
        var logger = new TestLogger();
        var results = new TestResults();

        // Phase 1: Smoke tests
        logger.Info("=== Phase 1: Smoke Tests ===");
        RunSmokeTests(logger, results);

        // Phase 2: Library-specific tests
        logger.Info("=== Phase 2: Library-Specific Tests ===");
        RunLibraryTests(logger, results);

        // Phase 3: Async tests
        logger.Info("=== Phase 3: Async Tests ===");
        await RunAsyncTests(logger, results);

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
        }
        catch (Exception ex)
        {
            logger.Fail($"ImageRequest construction: {ex.Message}");
            results.Fail("ImageRequest_Construction", ex.Message);
        }

        // ImagePipeline.ConfigurationValue
        try
        {
            var pipeline = ImagePipeline.Shared;
            var config = pipeline.ConfigurationValue;
            if (config != null)
            {
                logger.Pass("ImagePipeline.Configuration access");
                results.Pass("ImagePipeline_Configuration");
            }
            else
            {
                logger.Fail("ImagePipeline.Configuration: returned null");
                results.Fail("ImagePipeline_Configuration", "Returned null");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"ImagePipeline.Configuration: {ex.Message}");
            results.Fail("ImagePipeline_Configuration", ex.Message);
        }

        // Priority enum cases
        logger.Info("--- Priority Enum ---");
        try
        {
            var veryLow = ImageRequest.Priority.VeryLow;
            var low = ImageRequest.Priority.Low;
            var normal = ImageRequest.Priority.Normal;
            var high = ImageRequest.Priority.High;
            var veryHigh = ImageRequest.Priority.VeryHigh;

            var values = new[]
            {
                (int)veryLow, (int)low, (int)normal, (int)high, (int)veryHigh,
            };

            logger.Info($"Priority values: {string.Join(", ", values)}");

            // All 5 should be distinct values
            var distinctCount = values.Distinct().Count();
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
        }
        catch (Exception ex)
        {
            logger.Fail($"Priority enum: {ex.Message}");
            results.Fail("Priority_EnumCases", ex.Message);
        }

        // Priority cast from invalid raw value
        try
        {
            var invalid = (ImageRequest.Priority)999;
            if (!Enum.IsDefined(typeof(ImageRequest.Priority), invalid))
            {
                logger.Pass("Priority cast(999) is not a defined enum value");
                results.Pass("Priority_InvalidRawValue");
            }
            else
            {
                logger.Fail("Priority cast(999) should not be defined");
                results.Fail("Priority_InvalidRawValue", "Expected undefined for invalid raw value");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"Priority invalid cast: {ex.Message}");
            results.Fail("Priority_InvalidRawValue", ex.Message);
        }

        // Options static properties
        logger.Info("--- Options ---");
        try
        {
            var disableMemoryReads = ImageRequest.Options.DisableMemoryCacheReads;
            var disableMemoryWrites = ImageRequest.Options.DisableMemoryCacheWrites;
            var disableDiskReads = ImageRequest.Options.DisableDiskCacheReads;
            var disableDiskWrites = ImageRequest.Options.DisableDiskCacheWrites;

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

        // DataCache construction
        logger.Info("--- DataCache ---");
        try
        {
            var cache = new DataCache("binding-test");
            if (cache != null)
            {
                logger.Pass("DataCache construction");
                results.Pass("DataCache_Construction");
            }
            else
            {
                logger.Fail("DataCache construction: returned null");
                results.Fail("DataCache_Construction", "Returned null");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"DataCache construction: {ex.Message}");
            results.Fail("DataCache_Construction", ex.Message);
        }
    }

    private async Task RunAsyncTests(TestLogger logger, TestResults results)
    {
        // Image load (network) — async via ImagePipeline
        logger.Info("--- Async Image Load ---");
        try
        {
            var pipeline = ImagePipeline.Shared;
            var request = new ImageRequest("https://picsum.photos/200");
            var image = await pipeline.ImageAsync(request);
            bool loaded = image != null;
            if (loaded)
            {
                logger.Pass($"Image load (network): {image!.Size.Width}x{image.Size.Height}");
                results.Pass("ImageLoad_Network");
            }
            else
            {
                logger.Fail("Image load (network): returned null");
                results.Fail("ImageLoad_Network", "Returned null");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"Image load (network): {ex.Message}");
            results.Fail("ImageLoad_Network", ex.Message);
        }
    }
}

#endregion
