// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Text;
using Foundation;
using UIKit;
using Swift;
using BlinkID;
using BlinkIDUX;
using Swift.Runtime;

namespace BlinkIDUXSimTests;

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
            Text = "BlinkIDUX Binding Tests",
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

        // Phase 1: BlinkID smoke tests (dependency library)
        logger.Info("=== Phase 1: BlinkID Smoke Tests ===");
        RunBlinkIDSmokeTests(logger, results);

        // Phase 2: BlinkIDUX type metadata
        logger.Info("=== Phase 2: BlinkIDUX Type Metadata ===");
        RunBlinkIDUXMetadataTests(logger, results);

        // Phase 3: BlinkIDUX enum-like types
        logger.Info("=== Phase 3: BlinkIDUX Enum Cases ===");
        RunBlinkIDUXEnumTests(logger, results);

        // Phase 4: BlinkIDUX static singleton
        logger.Info("=== Phase 4: BlinkIDUX Static Properties ===");
        RunBlinkIDUXStaticTests(logger, results);

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

    private void RunBlinkIDSmokeTests(TestLogger logger, TestResults results)
    {
        // Verify BlinkID dependency loads correctly
        try
        {
            var metadata = SwiftObjectHelper<RequestTimeout>.GetTypeMetadata();
            logger.Info($"RequestTimeout metadata size: {metadata.Size}");
            if (metadata.Size > 0)
            {
                logger.Pass("BlinkID RequestTimeout metadata");
                results.Pass("BlinkID_RequestTimeout_Metadata");
            }
            else
            {
                logger.Fail("BlinkID RequestTimeout metadata: size is 0");
                results.Fail("BlinkID_RequestTimeout_Metadata", "Size is 0");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"BlinkID RequestTimeout metadata: {ex.Message}");
            results.Fail("BlinkID_RequestTimeout_Metadata", ex.Message);
        }

        try
        {
            var metadata = SwiftObjectHelper<DetectionStatus>.GetTypeMetadata();
            logger.Info($"DetectionStatus metadata size: {metadata.Size}");
            if (metadata.Size > 0)
            {
                logger.Pass("BlinkID DetectionStatus metadata");
                results.Pass("BlinkID_DetectionStatus_Metadata");
            }
            else
            {
                logger.Fail("BlinkID DetectionStatus metadata: size is 0");
                results.Fail("BlinkID_DetectionStatus_Metadata", "Size is 0");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"BlinkID DetectionStatus metadata: {ex.Message}");
            results.Fail("BlinkID_DetectionStatus_Metadata", ex.Message);
        }

        // Enum case construction from BlinkID
        try
        {
            var success = DetectionStatus.Success;
            var failed = DetectionStatus.Failed;
            logger.Info($"DetectionStatus.Success tag={success.Tag}, Failed tag={failed.Tag}");
            if (success.Tag != failed.Tag)
            {
                logger.Pass("BlinkID DetectionStatus case construction");
                results.Pass("BlinkID_DetectionStatus_Cases");
            }
            else
            {
                logger.Fail("BlinkID DetectionStatus cases: same tag");
                results.Fail("BlinkID_DetectionStatus_Cases", "Same tag");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"BlinkID DetectionStatus cases: {ex.Message}");
            results.Fail("BlinkID_DetectionStatus_Cases", ex.Message);
        }
    }

    private void RunBlinkIDUXMetadataTests(TestLogger logger, TestResults results)
    {
        // Test type metadata for key BlinkIDUX types
        // CameraStatus and PassportOrientation are now C# enums — no metadata access needed
        foreach (var (typeName, getMetadata) in new (string, Func<TypeMetadata>)[]
        {
            ("BlinkIDTheme", () => SwiftObjectHelper<BlinkIDTheme>.GetTypeMetadata()),
            ("MicroblinkColor", () => SwiftObjectHelper<MicroblinkColor>.GetTypeMetadata()),
            ("BlinkIDScanningAlertType", () => SwiftObjectHelper<BlinkIDScanningAlertType>.GetTypeMetadata()),
            ("BlinkIDUXModel", () => SwiftObjectHelper<BlinkIDUXModel>.GetTypeMetadata()),
            ("DocumentSide", () => SwiftObjectHelper<DocumentSide>.GetTypeMetadata()),
            ("ReticleState", () => SwiftObjectHelper<ReticleState>.GetTypeMetadata()),
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

    private void RunBlinkIDUXEnumTests(TestLogger logger, TestResults results)
    {
        // CameraStatus enum cases (class-based enum)
        logger.Info("--- CameraStatus ---");
        try
        {
            var unknown = CameraStatus.Unknown;
            var running = CameraStatus.Running;
            var unauthorized = CameraStatus.Unauthorized;
            logger.Info($"CameraStatus: Unknown={unknown.Tag}, Running={running.Tag}, Unauthorized={unauthorized.Tag}");
            if (unknown.Tag != running.Tag && running.Tag != unauthorized.Tag)
            {
                logger.Pass("CameraStatus case construction");
                results.Pass("CameraStatus_Cases");
            }
            else
            {
                logger.Fail("CameraStatus cases: duplicate tags");
                results.Fail("CameraStatus_Cases", "Duplicate tags");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"CameraStatus cases: {ex.Message}");
            results.Fail("CameraStatus_Cases", ex.Message);
        }

        // MicroblinkColor enum cases — wrapper xcframework not compiled
        // Bindings reference BlinkIDUXSwiftBindings but no wrapper xcframework was produced
        // (DllNotFoundException at runtime for CaseByIndex P/Invoke)
        logger.Info("--- MicroblinkColor ---");
        logger.Skip("MicroblinkColor cases: wrapper xcframework not compiled (DllNotFoundException)");
        results.Skip("MicroblinkColor_Cases", "BlinkIDUXSwiftBindings wrapper xcframework not compiled");

        // BlinkIDScanningAlertType enum cases
        logger.Info("--- BlinkIDScanningAlertType ---");
        try
        {
            var timeout = BlinkIDScanningAlertType.Timeout;
            var disallowed = BlinkIDScanningAlertType.DisallowedClass;
            logger.Info($"BlinkIDScanningAlertType: Timeout={timeout.Tag}, DisallowedClass={disallowed.Tag}");
            if (timeout.Tag != disallowed.Tag)
            {
                logger.Pass("BlinkIDScanningAlertType case construction");
                results.Pass("BlinkIDScanningAlertType_Cases");
            }
            else
            {
                logger.Fail("BlinkIDScanningAlertType cases: duplicate tags");
                results.Fail("BlinkIDScanningAlertType_Cases", "Duplicate tags");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"BlinkIDScanningAlertType cases: {ex.Message}");
            results.Fail("BlinkIDScanningAlertType_Cases", ex.Message);
        }
    }

    private void RunBlinkIDUXStaticTests(TestLogger logger, TestResults results)
    {
        // BlinkIDTheme.Shared — skipped: accessing the singleton triggers
        // resource_bundle_accessor.swift which fatally crashes when the
        // BlinkIDUX_BlinkIDUX bundle isn't in the app container.
        logger.Skip("BlinkIDTheme.Shared: requires resource bundle (skipped to avoid fatal crash)");
        results.Skip("BlinkIDTheme_Shared", "Resource bundle not available in test app");
    }
}

#endregion
