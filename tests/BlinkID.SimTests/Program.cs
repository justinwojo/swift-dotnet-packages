// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Text;
using Foundation;
using UIKit;
using Swift;
using Swift.BlinkID;
using Swift.Runtime;

namespace BlinkIDSimTests;

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
    // Framework resolution is handled automatically by the library assembly's
    // [ModuleInitializer] in the generated bindings — no manual resolver needed.
    static void Main(string[] args)
    {
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
            Text = "BlinkID Simulator Tests",
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
        // Type metadata access — verifies the Swift runtime initializes and types are resolvable
        try
        {
            var metadata = SwiftObjectHelper<RequestTimeout>.GetTypeMetadata();
            logger.Info($"RequestTimeout metadata size: {metadata.Size}");
            if (metadata.Size > 0)
            {
                logger.Pass("RequestTimeout metadata");
                results.Pass("RequestTimeout_Metadata");
            }
            else
            {
                logger.Fail("RequestTimeout metadata: size is 0");
                results.Fail("RequestTimeout_Metadata", "Size is 0");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"RequestTimeout metadata: {ex.Message}");
            results.Fail("RequestTimeout_Metadata", ex.Message);
        }

        try
        {
            var metadata = SwiftObjectHelper<DetectionStatus>.GetTypeMetadata();
            logger.Info($"DetectionStatus metadata size: {metadata.Size}");
            if (metadata.Size > 0)
            {
                logger.Pass("DetectionStatus metadata");
                results.Pass("DetectionStatus_Metadata");
            }
            else
            {
                logger.Fail("DetectionStatus metadata: size is 0");
                results.Fail("DetectionStatus_Metadata", "Size is 0");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"DetectionStatus metadata: {ex.Message}");
            results.Fail("DetectionStatus_Metadata", ex.Message);
        }

        try
        {
            var metadata = SwiftObjectHelper<Country>.GetTypeMetadata();
            logger.Info($"Country metadata size: {metadata.Size}");
            if (metadata.Size > 0)
            {
                logger.Pass("Country metadata");
                results.Pass("Country_Metadata");
            }
            else
            {
                logger.Fail("Country metadata: size is 0");
                results.Fail("Country_Metadata", "Size is 0");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"Country metadata: {ex.Message}");
            results.Fail("Country_Metadata", ex.Message);
        }
    }

    private void RunLibraryTests(TestLogger logger, TestResults results)
    {
        // Enum case construction
        logger.Info("--- Enum Cases ---");
        try
        {
            var success = DetectionStatus.Success;
            var failed = DetectionStatus.Failed;
            logger.Info($"DetectionStatus.Success tag={success.Tag}, Failed tag={failed.Tag}");
            if (success.Tag != failed.Tag)
            {
                logger.Pass("DetectionStatus case construction");
                results.Pass("DetectionStatus_Cases");
            }
            else
            {
                logger.Fail("DetectionStatus cases: Success and Failed have same tag");
                results.Fail("DetectionStatus_Cases", "Same tag");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"DetectionStatus cases: {ex.Message}");
            results.Fail("DetectionStatus_Cases", ex.Message);
        }

        try
        {
            var horizontal = DocumentOrientation.Horizontal;
            var vertical = DocumentOrientation.Vertical;
            logger.Info($"DocumentOrientation: Horizontal={horizontal.Tag}, Vertical={vertical.Tag}");
            if (horizontal.Tag != vertical.Tag)
            {
                logger.Pass("DocumentOrientation case construction");
                results.Pass("DocumentOrientation_Cases");
            }
            else
            {
                logger.Fail("DocumentOrientation cases: same tag");
                results.Fail("DocumentOrientation_Cases", "Same tag");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"DocumentOrientation cases: {ex.Message}");
            results.Fail("DocumentOrientation_Cases", ex.Message);
        }

        try
        {
            var zero = DocumentRotation.Zero;
            var cw90 = DocumentRotation.Clockwise90;
            var ccw90 = DocumentRotation.CounterClockwise90;
            var upside = DocumentRotation.UpsideDown;
            logger.Info($"DocumentRotation: Zero={zero.Tag}, CW90={cw90.Tag}, CCW90={ccw90.Tag}, Upside={upside.Tag}");
            if (zero.Tag != cw90.Tag && cw90.Tag != ccw90.Tag && ccw90.Tag != upside.Tag)
            {
                logger.Pass("DocumentRotation case construction");
                results.Pass("DocumentRotation_Cases");
            }
            else
            {
                logger.Fail("DocumentRotation cases: duplicate tags");
                results.Fail("DocumentRotation_Cases", "Duplicate tags");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"DocumentRotation cases: {ex.Message}");
            results.Fail("DocumentRotation_Cases", ex.Message);
        }

        // Enum raw values
        logger.Info("--- Enum Raw Values ---");
        try
        {
            var horizontal = DocumentOrientation.Horizontal;
            var rawValue = horizontal.RawValue;
            logger.Info($"DocumentOrientation.Horizontal raw value: {rawValue}");
            logger.Pass("DocumentOrientation raw value access");
            results.Pass("DocumentOrientation_RawValue");
        }
        catch (Exception ex)
        {
            logger.Fail($"DocumentOrientation raw value: {ex.Message}");
            results.Fail("DocumentOrientation_RawValue", ex.Message);
        }

        try
        {
            var none = Country.None;
            var rawValue = none.RawValue;
            logger.Info($"Country.None raw value: {rawValue}");
            logger.Pass("Country raw value access");
            results.Pass("Country_RawValue");
        }
        catch (Exception ex)
        {
            logger.Fail($"Country raw value: {ex.Message}");
            results.Fail("Country_RawValue", ex.Message);
        }

        // Enum FromRawValue round-trips
        logger.Info("--- Enum FromRawValue ---");
        try
        {
            var horizontal = DocumentOrientation.Horizontal;
            var rawValue = horizontal.RawValue;
            var roundTripped = DocumentOrientation.FromRawValue((long)rawValue);
            logger.Info($"DocumentOrientation round-trip: rawValue={rawValue}");
            if (roundTripped != null)
            {
                logger.Pass("DocumentOrientation FromRawValue round-trip");
                results.Pass("DocumentOrientation_FromRawValue");
            }
            else
            {
                logger.Fail("DocumentOrientation FromRawValue returned null");
                results.Fail("DocumentOrientation_FromRawValue", "Returned null");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"DocumentOrientation FromRawValue: {ex.Message}");
            results.Fail("DocumentOrientation_FromRawValue", ex.Message);
        }

        // Static property access
        logger.Info("--- Static Properties ---");
        try
        {
            var defaultTimeout = RequestTimeout.Default;
            logger.Info($"RequestTimeout.Default: {defaultTimeout}");
            if (defaultTimeout != null)
            {
                logger.Pass("RequestTimeout.Default access");
                results.Pass("RequestTimeout_Default");
            }
            else
            {
                logger.Fail("RequestTimeout.Default: returned null");
                results.Fail("RequestTimeout_Default", "Returned null");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"RequestTimeout.Default: {ex.Message}");
            results.Fail("RequestTimeout_Default", ex.Message);
        }

        // Extended type metadata
        logger.Info("--- Extended Metadata ---");
        foreach (var (typeName, getMetadata) in new (string, Func<TypeMetadata>)[]
        {
            ("DocumentImageColorStatus", () => SwiftObjectHelper<DocumentImageColorStatus>.GetTypeMetadata()),
            ("Region", () => SwiftObjectHelper<Region>.GetTypeMetadata()),
            ("Point", () => SwiftObjectHelper<Point>.GetTypeMetadata()),
            ("Quadrilateral", () => SwiftObjectHelper<Quadrilateral>.GetTypeMetadata()),
        })
        {
            try
            {
                var metadata = getMetadata();
                logger.Info($"{typeName} metadata size: {metadata.Size}");
                if (metadata.Size > 0)
                {
                    logger.Pass($"{typeName} metadata");
                    results.Pass($"{typeName}_Metadata");
                }
                else
                {
                    logger.Fail($"{typeName} metadata: size is 0");
                    results.Fail($"{typeName}_Metadata", "Size is 0");
                }
            }
            catch (Exception ex)
            {
                logger.Fail($"{typeName} metadata: {ex.Message}");
                results.Fail($"{typeName}_Metadata", ex.Message);
            }
        }
    }
}

#endregion
