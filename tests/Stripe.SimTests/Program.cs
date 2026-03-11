// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Text;
using Foundation;
using UIKit;
using Swift;
using Swift.Runtime;
using Stripe;
using StripeCore;
// using StripePayments; // excluded: generator produces invalid enum-as-NSObject bindings
using StripePaymentSheet;
using StripePaymentsUI;
using StripeApplePay;
using StripeIdentity;
using StripeIssuing;
using StripeCardScan;
using StripeFinancialConnections;
using StripeConnect;

namespace StripeSimTests;

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
            Text = "Stripe Binding Tests",
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
        // STPAPIClient type metadata (StripeCore)
        try
        {
            var metadata = SwiftObjectHelper<StripeCore.STPAPIClient>.GetTypeMetadata();
            logger.Info($"STPAPIClient metadata size: {metadata.Size}");
            if (metadata.Size > 0)
            {
                logger.Pass("STPAPIClient metadata");
                results.Pass("STPAPIClient_Metadata");
            }
            else
            {
                logger.Fail("STPAPIClient metadata: size is 0");
                results.Fail("STPAPIClient_Metadata", "Size is 0");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"STPAPIClient metadata: {ex.Message}");
            results.Fail("STPAPIClient_Metadata", ex.Message);
        }
    }

    private void RunLibraryTests(TestLogger logger, TestResults results)
    {
        // StripeCore: STPAPIClient.Shared singleton
        logger.Info("--- StripeCore ---");
        try
        {
            var client = StripeCore.STPAPIClient.Shared;
            logger.Info($"STPAPIClient.Shared: {client}");
            logger.Pass("STPAPIClient.Shared access");
            results.Pass("STPAPIClient_Shared");
        }
        catch (Exception ex)
        {
            logger.Fail($"STPAPIClient.Shared: {ex.Message}");
            results.Fail("STPAPIClient_Shared", ex.Message);
        }

        // StripeCore: STPSDKVersion static property
        try
        {
            var version = StripeCore.STPAPIClient.STPSDKVersion;
            logger.Info($"STPSDKVersion: {version}");
            if (!string.IsNullOrEmpty(version))
            {
                logger.Pass("STPSDKVersion access");
                results.Pass("STPSDKVersion");
            }
            else
            {
                logger.Fail("STPSDKVersion: empty or null");
                results.Fail("STPSDKVersion", "Empty or null");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"STPSDKVersion: {ex.Message}");
            results.Fail("STPSDKVersion", ex.Message);
        }

        // StripeCore: StripeAPI.DefaultPublishableKey — wrapper now compiles (Issue Q fixed R10)
        try
        {
            var key = StripeCore.StripeAPI.DefaultPublishableKey;
            logger.Info($"StripeAPI.DefaultPublishableKey: '{key}'");
            logger.Pass("StripeAPI.DefaultPublishableKey getter");
            results.Pass("StripeAPI_DefaultPublishableKey");
        }
        catch (Exception ex)
        {
            logger.Fail($"StripeAPI.DefaultPublishableKey: {ex.Message}");
            results.Fail("StripeAPI_DefaultPublishableKey", ex.Message);
        }

        // StripePaymentSheet: DownloadManager.SharedManager singleton
        // DownloadManager was @_spi — correctly suppressed by SPI filter (R9)
        logger.Info("--- StripePaymentSheet ---");
        logger.Skip("DownloadManager.SharedManager (@_spi type correctly suppressed)");
        results.Skip("DownloadManager_SharedManager", "@_spi type correctly suppressed by SPI filter");

        // StripePaymentsUI: STPImageLibrary — skipped: P/Invoke symbols not exported by wrapper framework
        logger.Info("--- StripePaymentsUI ---");
        logger.Skip("STPImageLibrary card images (P/Invoke entry points not resolved)");
        results.Skip("STPImageLibrary_Visa", "Wrapper framework does not export card image symbols");
        results.Skip("STPImageLibrary_Amex", "Wrapper framework does not export card image symbols");

        // Enum tag tests (no-payload cases only)
        logger.Info("--- Enum Tags ---");
        try
        {
            var canceled = StripeCardScan.CardScanSheetResult.Canceled;
            logger.Info($"CardScanSheetResult.Canceled tag: {canceled.Tag}");
            logger.Pass("CardScanSheetResult.Canceled tag");
            results.Pass("CardScanSheetResult_Canceled");
        }
        catch (Exception ex)
        {
            logger.Fail($"CardScanSheetResult.Canceled: {ex.Message}");
            results.Fail("CardScanSheetResult_Canceled", ex.Message);
        }

        try
        {
            var canceled = StripeFinancialConnections.FinancialConnectionsSheet.Result.Canceled;
            logger.Info($"FinancialConnectionsSheet.Result.Canceled tag: {canceled.Tag}");
            logger.Pass("FinancialConnectionsSheet.Result.Canceled tag");
            results.Pass("FinancialConnections_Canceled");
        }
        catch (Exception ex)
        {
            logger.Fail($"FinancialConnectionsSheet.Result.Canceled: {ex.Message}");
            results.Fail("FinancialConnections_Canceled", ex.Message);
        }

        try
        {
            var canceled = StripeIdentity.IdentityVerificationSheet.VerificationFlowResult.FlowCanceled;
            logger.Info($"VerificationFlowResult.FlowCanceled tag: {canceled.Tag}");
            logger.Pass("VerificationFlowResult.FlowCanceled tag");
            results.Pass("VerificationFlowResult_FlowCanceled");
        }
        catch (Exception ex)
        {
            logger.Fail($"VerificationFlowResult.FlowCanceled: {ex.Message}");
            results.Fail("VerificationFlowResult_FlowCanceled", ex.Message);
        }
    }
}

#endregion
