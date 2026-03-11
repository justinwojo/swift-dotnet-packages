// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Text;
using Foundation;
using UIKit;
using Swift;
using BlinkID;
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
            Text = "BlinkID Binding Tests",
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
            logger.Info($"DocumentOrientation: Horizontal={(int)horizontal}, Vertical={(int)vertical}");
            if (horizontal != vertical)
            {
                logger.Pass("DocumentOrientation simple enum");
                results.Pass("DocumentOrientation_Cases");
            }
            else
            {
                logger.Fail("DocumentOrientation cases: same value");
                results.Fail("DocumentOrientation_Cases", "Same value");
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
            logger.Info($"DocumentRotation: Zero={(int)zero}, CW90={(int)cw90}, CCW90={(int)ccw90}, Upside={(int)upside}");
            if (zero != cw90 && cw90 != ccw90 && ccw90 != upside)
            {
                logger.Pass("DocumentRotation simple enum");
                results.Pass("DocumentRotation_Cases");
            }
            else
            {
                logger.Fail("DocumentRotation cases: duplicate values");
                results.Fail("DocumentRotation_Cases", "Duplicate values");
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
            var rawValue = (int)horizontal;
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

        // Enum value comparison (simple enums use direct comparison)
        logger.Info("--- Enum Value Comparison ---");
        try
        {
            var horizontal = DocumentOrientation.Horizontal;
            if (horizontal == DocumentOrientation.Horizontal && horizontal != DocumentOrientation.Vertical)
            {
                logger.Pass("DocumentOrientation value comparison");
                results.Pass("DocumentOrientation_FromRawValue");
            }
            else
            {
                logger.Fail("DocumentOrientation value mismatch");
                results.Fail("DocumentOrientation_FromRawValue", "Value mismatch");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"DocumentOrientation value comparison: {ex.Message}");
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

        // DocumentImageColorStatus — now a simple C# enum
        logger.Info("--- DocumentImageColorStatus ---");
        try
        {
            var notAvail = DocumentImageColorStatus.NotAvailable;
            var bw = DocumentImageColorStatus.BlackAndWhite;
            var color = DocumentImageColorStatus.Color;
            logger.Info($"DocumentImageColorStatus: NotAvailable={(int)notAvail}, BlackAndWhite={(int)bw}, Color={(int)color}");
            if (notAvail != bw && bw != color)
            {
                logger.Pass("DocumentImageColorStatus simple enum values");
                results.Pass("DocumentImageColorStatus_Values");
            }
            else
            {
                logger.Fail("DocumentImageColorStatus: duplicate values");
                results.Fail("DocumentImageColorStatus_Values", "Duplicate values");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"DocumentImageColorStatus: {ex.Message}");
            results.Fail("DocumentImageColorStatus_Values", ex.Message);
        }

        // Extended type metadata
        logger.Info("--- Extended Metadata ---");
        foreach (var (typeName, getMetadata) in new (string, Func<TypeMetadata>)[]
        {
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
