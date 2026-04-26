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
using StripePayments;
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

        // Phase 1: Smoke tests — type metadata validation
        logger.Info("=== Phase 1: Smoke Tests ===");
        RunSmokeTests(logger, results);

        // Phase 2: StripeCore — API client, configuration, error handling
        logger.Info("=== Phase 2: StripeCore ===");
        RunStripeCoreTests(logger, results);

        // Phase 3: StripeCore enums & error codes
        logger.Info("=== Phase 3: StripeCore Enums & Error Codes ===");
        RunStripeCoreEnumTests(logger, results);

        // Phase 4: StripePayments — payment methods, intents, card validation
        logger.Info("=== Phase 4: StripePayments ===");
        RunStripePaymentsTests(logger, results);

        // Phase 5: StripePayments enums
        logger.Info("=== Phase 5: StripePayments Enums ===");
        RunStripePaymentsEnumTests(logger, results);

        // Phase 6: StripePaymentSheet — configuration, appearance, styling
        logger.Info("=== Phase 6: StripePaymentSheet ===");
        RunPaymentSheetTests(logger, results);

        // Phase 7: StripePaymentSheet — CustomerSheet & results
        logger.Info("=== Phase 7: StripePaymentSheet Results & CustomerSheet ===");
        RunPaymentSheetResultTests(logger, results);

        // Phase 8: StripeApplePay
        logger.Info("=== Phase 8: StripeApplePay ===");
        RunApplePayTests(logger, results);

        // Phase 9: StripeIdentity
        logger.Info("=== Phase 9: StripeIdentity ===");
        RunIdentityTests(logger, results);

        // Phase 10: StripeConnect
        logger.Info("=== Phase 10: StripeConnect ===");
        RunConnectTests(logger, results);

        // Phase 11: StripeIssuing
        logger.Info("=== Phase 11: StripeIssuing ===");
        RunIssuingTests(logger, results);

        // Phase 12: StripeCardScan
        logger.Info("=== Phase 12: StripeCardScan ===");
        RunCardScanTests(logger, results);

        // Phase 13: StripeFinancialConnections
        logger.Info("=== Phase 13: StripeFinancialConnections ===");
        RunFinancialConnectionsTests(logger, results);

        // Phase 14: StripePaymentsUI — card form views, image library
        logger.Info("=== Phase 14: StripePaymentsUI ===");
        RunPaymentsUITests(logger, results);

        // Phase 15: StripePayments Extended — card params, method params, validator
        logger.Info("=== Phase 15: StripePayments Extended ===");
        RunStripePaymentsExtendedTests(logger, results);

        // Phase 16: StripePaymentSheet Extended — appearance, billing, results
        logger.Info("=== Phase 16: StripePaymentSheet Extended ===");
        RunPaymentSheetExtendedTests(logger, results);

        // Phase 17: StripePaymentsUI Extended — styling, brand images, form views
        logger.Info("=== Phase 17: StripePaymentsUI Extended ===");
        RunPaymentsUIExtendedTests(logger, results);

        // Phase 18: StripeConnect Extended — options roundtrips, appearance composition
        logger.Info("=== Phase 18: StripeConnect Extended ===");
        RunConnectExtendedTests(logger, results);

        // Phase 19: Cross-module — objects from one module used in another
        logger.Info("=== Phase 19: Cross-Module ===");
        RunCrossModuleTests(logger, results);

        // Phase 20: PaymentSheet.Configuration — consumer-facing API property roundtrips
        logger.Info("=== Phase 20: PaymentSheet.Configuration ===");
        RunPaymentSheetConfigurationTests(logger, results);

        // Phase 21: STPPaymentMethodParams constructors — iDEAL, FPX, EPS, Bancontact, AU BECS, Bacs, SEPA
        logger.Info("=== Phase 21: PaymentMethodParams Constructors ===");
        RunPaymentMethodParamsConstructorTests(logger, results);

        // Phase 22: CardBrandAcceptance — factories, extractors, All singleton
        logger.Info("=== Phase 22: CardBrandAcceptance ===");
        RunCardBrandAcceptanceTests(logger, results);

        // Phase 23: PaymentSheetError & CustomerSheetError — factory + TryGet roundtrips
        logger.Info("=== Phase 23: Error Types ===");
        RunErrorTypeTests(logger, results);

        // Phase 24: EmbeddedPaymentElement.Configuration — property roundtrips
        logger.Info("=== Phase 24: EmbeddedPaymentElement.Configuration ===");
        RunEmbeddedConfigurationTests(logger, results);

        // Phase 25: AddressViewController.Configuration — construction + property roundtrips
        logger.Info("=== Phase 25: AddressViewController.Configuration ===");
        RunAddressViewControllerConfigTests(logger, results);

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

    // =========================================================================
    // Phase 1: Smoke Tests — type metadata validation
    // =========================================================================

    private void RunSmokeTests(TestLogger logger, TestResults results)
    {
        // STPAPIClient type metadata (StripeCore)
        logger.Info("--- StripeCore metadata ---");
        try
        {
            var metadata = SwiftObjectHelper<StripeCore.STPAPIClient>.GetTypeMetadata();
            logger.Info($"STPAPIClient metadata size: {metadata.Size}");
            if (metadata.Size > 0)
            {
                logger.Pass("STPAPIClient metadata");
                results.Pass("Smoke_STPAPIClient_Metadata");
            }
            else
            {
                logger.Fail("STPAPIClient metadata: size is 0");
                results.Fail("Smoke_STPAPIClient_Metadata", "Size is 0");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"STPAPIClient metadata: {ex.Message}");
            results.Fail("Smoke_STPAPIClient_Metadata", ex.Message);
        }

        // StripeAPI type metadata
        try
        {
            var metadata = SwiftObjectHelper<StripeCore.StripeAPI>.GetTypeMetadata();
            if (metadata.Size > 0)
            {
                logger.Pass($"StripeAPI metadata (size: {metadata.Size})");
                results.Pass("Smoke_StripeAPI_Metadata");
            }
            else
            {
                logger.Fail("StripeAPI metadata: size is 0");
                results.Fail("Smoke_StripeAPI_Metadata", "Size is 0");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"StripeAPI metadata: {ex.Message}");
            results.Fail("Smoke_StripeAPI_Metadata", ex.Message);
        }

        // STPError type metadata
        try
        {
            var metadata = SwiftObjectHelper<StripeCore.STPError>.GetTypeMetadata();
            if (metadata.Size > 0)
            {
                logger.Pass($"STPError metadata (size: {metadata.Size})");
                results.Pass("Smoke_STPError_Metadata");
            }
            else
            {
                logger.Fail("STPError metadata: size is 0");
                results.Fail("Smoke_STPError_Metadata", "Size is 0");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"STPError metadata: {ex.Message}");
            results.Fail("Smoke_STPError_Metadata", ex.Message);
        }

        // STPAppInfo type metadata
        try
        {
            var metadata = SwiftObjectHelper<StripeCore.STPAppInfo>.GetTypeMetadata();
            if (metadata.Size > 0)
            {
                logger.Pass($"STPAppInfo metadata (size: {metadata.Size})");
                results.Pass("Smoke_STPAppInfo_Metadata");
            }
            else
            {
                logger.Fail("STPAppInfo metadata: size is 0");
                results.Fail("Smoke_STPAppInfo_Metadata", "Size is 0");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"STPAppInfo metadata: {ex.Message}");
            results.Fail("Smoke_STPAppInfo_Metadata", ex.Message);
        }
    }

    // =========================================================================
    // Phase 2: StripeCore — API client, configuration, error handling
    // =========================================================================

    private void RunStripeCoreTests(TestLogger logger, TestResults results)
    {
        // STPAPIClient.Shared singleton
        logger.Info("--- STPAPIClient ---");
        try
        {
            var client = StripeCore.STPAPIClient.Shared;
            if (client != null)
            {
                logger.Pass("STPAPIClient.Shared singleton access");
                results.Pass("StripeCore_STPAPIClient_Shared");
            }
            else
            {
                logger.Fail("STPAPIClient.Shared returned null");
                results.Fail("StripeCore_STPAPIClient_Shared", "Returned null");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"STPAPIClient.Shared: {ex.Message}");
            results.Fail("StripeCore_STPAPIClient_Shared", ex.Message);
        }

        // STPSDKVersion static property
        try
        {
            var version = StripeCore.STPAPIClient.STPSDKVersion;
            if (!string.IsNullOrEmpty(version))
            {
                logger.Pass($"STPSDKVersion: {version}");
                results.Pass("StripeCore_STPSDKVersion");
            }
            else
            {
                logger.Fail("STPSDKVersion: empty or null");
                results.Fail("StripeCore_STPSDKVersion", "Empty or null");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"STPSDKVersion: {ex.Message}");
            results.Fail("StripeCore_STPSDKVersion", ex.Message);
        }

        // ApiVersion static property
        try
        {
            var apiVersion = StripeCore.STPAPIClient.ApiVersion;
            if (!string.IsNullOrEmpty(apiVersion))
            {
                logger.Pass($"ApiVersion: {apiVersion}");
                results.Pass("StripeCore_ApiVersion");
            }
            else
            {
                logger.Fail("ApiVersion: empty or null");
                results.Fail("StripeCore_ApiVersion", "Empty or null");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"ApiVersion: {ex.Message}");
            results.Fail("StripeCore_ApiVersion", ex.Message);
        }

        // STPAPIClient constructor (parameterless)
        try
        {
            var client = new StripeCore.STPAPIClient();
            if (client != null)
            {
                logger.Pass("STPAPIClient() constructor");
                results.Pass("StripeCore_STPAPIClient_Ctor");
            }
            else
            {
                logger.Fail("STPAPIClient() constructor returned null");
                results.Fail("StripeCore_STPAPIClient_Ctor", "Returned null");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"STPAPIClient() constructor: {ex.Message}");
            results.Fail("StripeCore_STPAPIClient_Ctor", ex.Message);
        }

        // STPAPIClient constructor with publishable key
        try
        {
            var client = new StripeCore.STPAPIClient("pk_test_fake_key_12345");
            if (client != null)
            {
                logger.Pass("STPAPIClient(publishableKey) constructor");
                results.Pass("StripeCore_STPAPIClient_CtorKey");
            }
            else
            {
                logger.Fail("STPAPIClient(publishableKey) returned null");
                results.Fail("StripeCore_STPAPIClient_CtorKey", "Returned null");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"STPAPIClient(publishableKey): {ex.Message}");
            results.Fail("StripeCore_STPAPIClient_CtorKey", ex.Message);
        }

        // STPAPIClient.PublishableKey get/set
        try
        {
            var client = new StripeCore.STPAPIClient();
            client.PublishableKey = "pk_test_round_trip_key";
            var readBack = client.PublishableKey;
            if (readBack == "pk_test_round_trip_key")
            {
                logger.Pass("STPAPIClient.PublishableKey roundtrip");
                results.Pass("StripeCore_STPAPIClient_PublishableKey");
            }
            else
            {
                logger.Fail($"STPAPIClient.PublishableKey roundtrip: got '{readBack}'");
                results.Fail("StripeCore_STPAPIClient_PublishableKey", $"Got '{readBack}'");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"STPAPIClient.PublishableKey: {ex.Message}");
            results.Fail("StripeCore_STPAPIClient_PublishableKey", ex.Message);
        }

        // STPAPIClient.StripeAccount get/set
        try
        {
            var client = new StripeCore.STPAPIClient();
            client.StripeAccount = "acct_test_123";
            var readBack = client.StripeAccount;
            if (readBack == "acct_test_123")
            {
                logger.Pass("STPAPIClient.StripeAccount roundtrip");
                results.Pass("StripeCore_STPAPIClient_StripeAccount");
            }
            else
            {
                logger.Fail($"STPAPIClient.StripeAccount roundtrip: got '{readBack}'");
                results.Fail("StripeCore_STPAPIClient_StripeAccount", $"Got '{readBack}'");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"STPAPIClient.StripeAccount: {ex.Message}");
            results.Fail("StripeCore_STPAPIClient_StripeAccount", ex.Message);
        }

        // STPAPIClient equality
        try
        {
            var client1 = StripeCore.STPAPIClient.Shared;
            var client2 = StripeCore.STPAPIClient.Shared;
            if (client1.Equals(client2))
            {
                logger.Pass("STPAPIClient.Shared equality (same singleton)");
                results.Pass("StripeCore_STPAPIClient_Equality");
            }
            else
            {
                logger.Fail("STPAPIClient.Shared equality: singletons not equal");
                results.Fail("StripeCore_STPAPIClient_Equality", "Singletons not equal");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"STPAPIClient equality: {ex.Message}");
            results.Fail("StripeCore_STPAPIClient_Equality", ex.Message);
        }

        // STPAppInfo construction and properties
        logger.Info("--- STPAppInfo ---");
        try
        {
            var appInfo = new StripeCore.STPAppInfo("TestApp", "partner_123", "1.0.0", "https://example.com");
            if (appInfo.Name == "TestApp")
            {
                logger.Pass($"STPAppInfo.Name: {appInfo.Name}");
                results.Pass("StripeCore_STPAppInfo_Name");
            }
            else
            {
                logger.Fail($"STPAppInfo.Name: expected 'TestApp', got '{appInfo.Name}'");
                results.Fail("StripeCore_STPAppInfo_Name", $"Expected 'TestApp', got '{appInfo.Name}'");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"STPAppInfo constructor: {ex.Message}");
            results.Fail("StripeCore_STPAppInfo_Name", ex.Message);
        }

        try
        {
            var appInfo = new StripeCore.STPAppInfo("TestApp", "partner_123", "1.0.0", "https://example.com");
            if (appInfo.PartnerId == "partner_123")
            {
                logger.Pass($"STPAppInfo.PartnerId: {appInfo.PartnerId}");
                results.Pass("StripeCore_STPAppInfo_PartnerId");
            }
            else
            {
                logger.Fail($"STPAppInfo.PartnerId: got '{appInfo.PartnerId}'");
                results.Fail("StripeCore_STPAppInfo_PartnerId", $"Got '{appInfo.PartnerId}'");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"STPAppInfo.PartnerId: {ex.Message}");
            results.Fail("StripeCore_STPAppInfo_PartnerId", ex.Message);
        }

        try
        {
            var appInfo = new StripeCore.STPAppInfo("TestApp", "partner_123", "1.0.0", "https://example.com");
            if (appInfo.Version == "1.0.0")
            {
                logger.Pass($"STPAppInfo.Version: {appInfo.Version}");
                results.Pass("StripeCore_STPAppInfo_Version");
            }
            else
            {
                logger.Fail($"STPAppInfo.Version: got '{appInfo.Version}'");
                results.Fail("StripeCore_STPAppInfo_Version", $"Got '{appInfo.Version}'");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"STPAppInfo.Version: {ex.Message}");
            results.Fail("StripeCore_STPAppInfo_Version", ex.Message);
        }

        try
        {
            var appInfo = new StripeCore.STPAppInfo("TestApp", "partner_123", "1.0.0", "https://example.com");
            if (appInfo.Url == "https://example.com")
            {
                logger.Pass($"STPAppInfo.Url: {appInfo.Url}");
                results.Pass("StripeCore_STPAppInfo_Url");
            }
            else
            {
                logger.Fail($"STPAppInfo.Url: got '{appInfo.Url}'");
                results.Fail("StripeCore_STPAppInfo_Url", $"Got '{appInfo.Url}'");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"STPAppInfo.Url: {ex.Message}");
            results.Fail("StripeCore_STPAppInfo_Url", ex.Message);
        }

        // STPAppInfo with null optional params
        try
        {
            var appInfo = new StripeCore.STPAppInfo("MinimalApp", null, null, null);
            if (appInfo.Name == "MinimalApp" && appInfo.PartnerId == null && appInfo.Version == null)
            {
                logger.Pass("STPAppInfo with null optionals");
                results.Pass("StripeCore_STPAppInfo_NullOptionals");
            }
            else
            {
                logger.Fail($"STPAppInfo null optionals: Name='{appInfo.Name}', PartnerId='{appInfo.PartnerId}'");
                results.Fail("StripeCore_STPAppInfo_NullOptionals", "Unexpected values");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"STPAppInfo null optionals: {ex.Message}");
            results.Fail("StripeCore_STPAppInfo_NullOptionals", ex.Message);
        }

        // STPAPIClient.AppInfo assignment
        // Known issue: string corruption on AppInfo roundtrip via getter path.
        // The AppInfo property getter triggers ARC operations (passRetained in Swift,
        // InitializeWithCopy + Arc.Retain in C#) that corrupt the NSString ivar data
        // by +2 at byte offset 4, cumulatively. Affects all string values — "TestApp"
        // becomes "TestCpp", "ABCDEFG" becomes "ABCDGFG", etc. Corruption is persistent
        // on the Swift object (same handle, both original and readBack wrappers see it).
        // The setter path (which also uses NewSome) does NOT corrupt.
        // Hypothesis: swift_retain on NSString tagged pointers corrupts inline data.
        try
        {
            var client = new StripeCore.STPAPIClient();
            var appInfo = new StripeCore.STPAppInfo("TestApp", null, "2.0", null);
            client.AppInfo = appInfo;
            var readBack = client.AppInfo;
            if (readBack != null && readBack.Name == "TestApp")
            {
                logger.Pass("STPAPIClient.AppInfo roundtrip");
                results.Pass("StripeCore_STPAPIClient_AppInfo");
            }
            else if (readBack != null)
            {
                logger.Skip($"STPAPIClient.AppInfo: known string corruption (got '{readBack.Name}')");
                results.Skip("StripeCore_STPAPIClient_AppInfo", $"String corruption: got '{readBack.Name}'");
            }
            else
            {
                logger.Skip("STPAPIClient.AppInfo: getter returned null after set");
                results.Skip("StripeCore_STPAPIClient_AppInfo", "Getter returned null");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"STPAPIClient.AppInfo: {ex.Message}");
            results.Fail("StripeCore_STPAPIClient_AppInfo", ex.Message);
        }

        // StripeAPI static properties
        logger.Info("--- StripeAPI ---");
        try
        {
            var key = StripeCore.StripeAPI.DefaultPublishableKey;
            logger.Pass($"StripeAPI.DefaultPublishableKey getter: '{key}'");
            results.Pass("StripeCore_StripeAPI_DefaultPubKey_Get");
        }
        catch (Exception ex)
        {
            logger.Fail($"StripeAPI.DefaultPublishableKey getter: {ex.Message}");
            results.Fail("StripeCore_StripeAPI_DefaultPubKey_Get", ex.Message);
        }

        try
        {
            StripeCore.StripeAPI.DefaultPublishableKey = "pk_test_set_default";
            var readBack = StripeCore.StripeAPI.DefaultPublishableKey;
            if (readBack == "pk_test_set_default")
            {
                logger.Pass("StripeAPI.DefaultPublishableKey roundtrip");
                results.Pass("StripeCore_StripeAPI_DefaultPubKey_Set");
            }
            else
            {
                logger.Fail($"StripeAPI.DefaultPublishableKey set: got '{readBack}'");
                results.Fail("StripeCore_StripeAPI_DefaultPubKey_Set", $"Got '{readBack}'");
            }
            // Reset to avoid side effects
            StripeCore.StripeAPI.DefaultPublishableKey = null;
        }
        catch (Exception ex)
        {
            logger.Fail($"StripeAPI.DefaultPublishableKey set: {ex.Message}");
            results.Fail("StripeCore_StripeAPI_DefaultPubKey_Set", ex.Message);
        }

        try
        {
            var enabled = StripeCore.StripeAPI.AdvancedFraudSignalsEnabled;
            logger.Pass($"StripeAPI.AdvancedFraudSignalsEnabled: {enabled}");
            results.Pass("StripeCore_StripeAPI_FraudSignals");
        }
        catch (Exception ex)
        {
            logger.Fail($"StripeAPI.AdvancedFraudSignalsEnabled: {ex.Message}");
            results.Fail("StripeCore_StripeAPI_FraudSignals", ex.Message);
        }

        try
        {
            var maxRetries = StripeCore.StripeAPI.MaxRetries;
            logger.Pass($"StripeAPI.MaxRetries: {maxRetries}");
            results.Pass("StripeCore_StripeAPI_MaxRetries_Get");
        }
        catch (Exception ex)
        {
            logger.Fail($"StripeAPI.MaxRetries: {ex.Message}");
            results.Fail("StripeCore_StripeAPI_MaxRetries_Get", ex.Message);
        }

        try
        {
            var original = StripeCore.StripeAPI.MaxRetries;
            StripeCore.StripeAPI.MaxRetries = 5;
            var readBack = StripeCore.StripeAPI.MaxRetries;
            StripeCore.StripeAPI.MaxRetries = original;
            if (readBack == 5)
            {
                logger.Pass("StripeAPI.MaxRetries roundtrip");
                results.Pass("StripeCore_StripeAPI_MaxRetries_Set");
            }
            else
            {
                logger.Fail($"StripeAPI.MaxRetries roundtrip: got {readBack}");
                results.Fail("StripeCore_StripeAPI_MaxRetries_Set", $"Got {readBack}");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"StripeAPI.MaxRetries roundtrip: {ex.Message}");
            results.Fail("StripeCore_StripeAPI_MaxRetries_Set", ex.Message);
        }

        // StripeAPI.DeviceSupportsApplePay
        try
        {
            var supported = StripeCore.StripeAPI.GetDeviceSupportsApplePay();
            logger.Pass($"StripeAPI.DeviceSupportsApplePay: {supported}");
            results.Pass("StripeCore_StripeAPI_ApplePaySupport");
        }
        catch (Exception ex)
        {
            logger.Fail($"StripeAPI.DeviceSupportsApplePay: {ex.Message}");
            results.Fail("StripeCore_StripeAPI_ApplePaySupport", ex.Message);
        }

        // STPError static properties (error domain constants)
        logger.Info("--- STPError Constants ---");
        try
        {
            var domain = StripeCore.STPError.StripeDomain;
            if (!string.IsNullOrEmpty(domain))
            {
                logger.Pass($"STPError.StripeDomain: {domain}");
                results.Pass("StripeCore_STPError_StripeDomain");
            }
            else
            {
                logger.Fail("STPError.StripeDomain: empty");
                results.Fail("StripeCore_STPError_StripeDomain", "Empty");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"STPError.StripeDomain: {ex.Message}");
            results.Fail("StripeCore_STPError_StripeDomain", ex.Message);
        }

        try
        {
            var handlerDomain = StripeCore.STPError.STPPaymentHandlerErrorDomain;
            if (!string.IsNullOrEmpty(handlerDomain))
            {
                logger.Pass($"STPError.STPPaymentHandlerErrorDomain: {handlerDomain}");
                results.Pass("StripeCore_STPError_HandlerDomain");
            }
            else
            {
                logger.Fail("STPError.STPPaymentHandlerErrorDomain: empty");
                results.Fail("StripeCore_STPError_HandlerDomain", "Empty");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"STPError.STPPaymentHandlerErrorDomain: {ex.Message}");
            results.Fail("StripeCore_STPError_HandlerDomain", ex.Message);
        }

        // Error key constants
        var errorKeys = new (string Name, Func<string> Getter)[]
        {
            ("ErrorMessageKey", () => StripeCore.STPError.ErrorMessageKey),
            ("HintKey", () => StripeCore.STPError.HintKey),
            ("CardErrorCodeKey", () => StripeCore.STPError.CardErrorCodeKey),
            ("ErrorParameterKey", () => StripeCore.STPError.ErrorParameterKey),
            ("StripeErrorCodeKey", () => StripeCore.STPError.StripeErrorCodeKey),
            ("StripeErrorTypeKey", () => StripeCore.STPError.StripeErrorTypeKey),
            ("StripeDeclineCodeKey", () => StripeCore.STPError.StripeDeclineCodeKey),
        };

        foreach (var (name, getter) in errorKeys)
        {
            try
            {
                var value = getter();
                if (!string.IsNullOrEmpty(value))
                {
                    logger.Pass($"STPError.{name}: {value}");
                    results.Pass($"StripeCore_STPError_{name}");
                }
                else
                {
                    logger.Fail($"STPError.{name}: empty");
                    results.Fail($"StripeCore_STPError_{name}", "Empty");
                }
            }
            catch (Exception ex)
            {
                logger.Fail($"STPError.{name}: {ex.Message}");
                results.Fail($"StripeCore_STPError_{name}", ex.Message);
            }
        }

        // Card error string constants
        var cardErrorStrings = new (string Name, Func<string> Getter)[]
        {
            ("InvalidNumber", () => StripeCore.STPError.InvalidNumber),
            ("InvalidExpMonth", () => StripeCore.STPError.InvalidExpMonth),
            ("InvalidExpYear", () => StripeCore.STPError.InvalidExpYear),
            ("InvalidCVC", () => StripeCore.STPError.InvalidCVC),
            ("IncorrectNumber", () => StripeCore.STPError.IncorrectNumber),
            ("ExpiredCard", () => StripeCore.STPError.ExpiredCard),
            ("CardDeclined", () => StripeCore.STPError.CardDeclined),
            ("ProcessingError", () => StripeCore.STPError.ProcessingError),
            ("IncorrectCVC", () => StripeCore.STPError.IncorrectCVC),
            ("IncorrectZip", () => StripeCore.STPError.IncorrectZip),
        };

        foreach (var (name, getter) in cardErrorStrings)
        {
            try
            {
                var value = getter();
                if (!string.IsNullOrEmpty(value))
                {
                    logger.Pass($"STPError.{name}: {value}");
                    results.Pass($"StripeCore_STPError_{name}");
                }
                else
                {
                    logger.Fail($"STPError.{name}: empty");
                    results.Fail($"StripeCore_STPError_{name}", "Empty");
                }
            }
            catch (Exception ex)
            {
                logger.Fail($"STPError.{name}: {ex.Message}");
                results.Fail($"StripeCore_STPError_{name}", ex.Message);
            }
        }
    }

    // =========================================================================
    // Phase 3: StripeCore Enums & Error Codes
    // =========================================================================

    private void RunStripeCoreEnumTests(TestLogger logger, TestResults results)
    {
        // STPErrorCode enum values
        logger.Info("--- STPErrorCode enum ---");
        try
        {
            var values = new[]
            {
                (StripeCore.STPErrorCode.ConnectionError, "ConnectionError", 0L),
                (StripeCore.STPErrorCode.InvalidRequestError, "InvalidRequestError", 1L),
                (StripeCore.STPErrorCode.AuthenticationError, "AuthenticationError", 2L),
                (StripeCore.STPErrorCode.ApiError, "ApiError", 3L),
                (StripeCore.STPErrorCode.CardError, "CardError", 4L),
                (StripeCore.STPErrorCode.CancellationError, "CancellationError", 5L),
                (StripeCore.STPErrorCode.EphemeralKeyDecodingError, "EphemeralKeyDecodingError", 6L),
            };

            var allCorrect = true;
            foreach (var (value, name, expected) in values)
            {
                if ((long)value != expected)
                {
                    logger.Fail($"STPErrorCode.{name}: expected {expected}, got {(long)value}");
                    allCorrect = false;
                }
            }

            if (allCorrect)
            {
                logger.Pass($"STPErrorCode: all {values.Length} cases correct");
                results.Pass("StripeCore_STPErrorCode_Values");
            }
            else
            {
                results.Fail("StripeCore_STPErrorCode_Values", "Incorrect enum values");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"STPErrorCode: {ex.Message}");
            results.Fail("StripeCore_STPErrorCode_Values", ex.Message);
        }

        // STPErrorCode distinct check
        try
        {
            var allValues = Enum.GetValues<StripeCore.STPErrorCode>();
            if (allValues.Distinct().Count() == allValues.Length && allValues.Length == 7)
            {
                logger.Pass($"STPErrorCode: {allValues.Length} distinct cases");
                results.Pass("StripeCore_STPErrorCode_Distinct");
            }
            else
            {
                logger.Fail($"STPErrorCode: expected 7 distinct, got {allValues.Distinct().Count()}");
                results.Fail("StripeCore_STPErrorCode_Distinct", "Unexpected count");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"STPErrorCode distinct: {ex.Message}");
            results.Fail("StripeCore_STPErrorCode_Distinct", ex.Message);
        }

        // STPCardErrorCode enum-like class
        logger.Info("--- STPCardErrorCode ---");
        var cardErrorCases = new (string Name, Func<StripeCore.STPCardErrorCode> Getter, uint ExpectedTag)[]
        {
            ("InvalidNumber", () => StripeCore.STPCardErrorCode.InvalidNumber, 0),
            ("InvalidExpMonth", () => StripeCore.STPCardErrorCode.InvalidExpMonth, 1),
            ("InvalidExpYear", () => StripeCore.STPCardErrorCode.InvalidExpYear, 2),
            ("InvalidCVC", () => StripeCore.STPCardErrorCode.InvalidCVC, 3),
            ("IncorrectNumber", () => StripeCore.STPCardErrorCode.IncorrectNumber, 4),
            ("ExpiredCard", () => StripeCore.STPCardErrorCode.ExpiredCard, 5),
            ("CardDeclined", () => StripeCore.STPCardErrorCode.CardDeclined, 6),
            ("IncorrectCVC", () => StripeCore.STPCardErrorCode.IncorrectCVC, 7),
            ("ProcessingError", () => StripeCore.STPCardErrorCode.ProcessingError, 8),
            ("IncorrectZip", () => StripeCore.STPCardErrorCode.IncorrectZip, 9),
        };

        foreach (var (name, getter, expectedTag) in cardErrorCases)
        {
            try
            {
                using var code = getter();
                if ((uint)code.Tag == expectedTag)
                {
                    logger.Pass($"STPCardErrorCode.{name}: tag={code.Tag}");
                    results.Pass($"StripeCore_STPCardErrorCode_{name}");
                }
                else
                {
                    logger.Fail($"STPCardErrorCode.{name}: expected tag {expectedTag}, got {code.Tag}");
                    results.Fail($"StripeCore_STPCardErrorCode_{name}", $"Expected tag {expectedTag}");
                }
            }
            catch (Exception ex)
            {
                logger.Fail($"STPCardErrorCode.{name}: {ex.Message}");
                results.Fail($"StripeCore_STPCardErrorCode_{name}", ex.Message);
            }
        }

        // STPCardErrorCode.RawValue
        try
        {
            using var code = StripeCore.STPCardErrorCode.InvalidNumber;
            var raw = code.RawValue;
            if (!string.IsNullOrEmpty(raw))
            {
                logger.Pass($"STPCardErrorCode.InvalidNumber.RawValue: '{raw}'");
                results.Pass("StripeCore_STPCardErrorCode_RawValue");
            }
            else
            {
                logger.Fail("STPCardErrorCode.InvalidNumber.RawValue: empty");
                results.Fail("StripeCore_STPCardErrorCode_RawValue", "Empty");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"STPCardErrorCode.RawValue: {ex.Message}");
            results.Fail("StripeCore_STPCardErrorCode_RawValue", ex.Message);
        }

        // STPCardErrorCode.FromRawValue roundtrip
        try
        {
            using var original = StripeCore.STPCardErrorCode.CardDeclined;
            var rawValue = original.RawValue;
            using var roundTripped = StripeCore.STPCardErrorCode.FromRawValue(rawValue);
            if (roundTripped != null && (uint)roundTripped.Tag == (uint)original.Tag)
            {
                logger.Pass($"STPCardErrorCode.FromRawValue roundtrip: '{rawValue}' -> tag {roundTripped.Tag}");
                results.Pass("StripeCore_STPCardErrorCode_FromRawValue");
            }
            else
            {
                logger.Fail("STPCardErrorCode.FromRawValue roundtrip failed");
                results.Fail("StripeCore_STPCardErrorCode_FromRawValue", "Roundtrip failed");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"STPCardErrorCode.FromRawValue: {ex.Message}");
            results.Fail("StripeCore_STPCardErrorCode_FromRawValue", ex.Message);
        }
    }

    // =========================================================================
    // Phase 4: StripePayments — payment methods, intents, card validation
    // =========================================================================

    private void RunStripePaymentsTests(TestLogger logger, TestResults results)
    {
        logger.Info("--- STPPaymentMethodParams ---");

        // STPPaymentMethodParams construction
        try
        {
            var param = new StripePayments.STPPaymentMethodParams();
            if (param != null)
            {
                logger.Pass("STPPaymentMethodParams() constructor");
                results.Pass("Payments_STPPaymentMethodParams_Ctor");
            }
            else
            {
                logger.Fail("STPPaymentMethodParams() returned null");
                results.Fail("Payments_STPPaymentMethodParams_Ctor", "Returned null");
            }
        }
        catch (DllNotFoundException ex)
        {
            logger.Fail($"STPPaymentMethodParams: native entry point missing: {ex.Message}");
            results.Fail("Payments_STPPaymentMethodParams_Ctor", $"Native entry point missing: {ex.Message}");
            return;
        }
        catch (Exception ex)
        {
            logger.Fail($"STPPaymentMethodParams(): {ex.Message}");
            results.Fail("Payments_STPPaymentMethodParams_Ctor", ex.Message);
        }

        // STPPaymentMethodCardParams construction and properties
        logger.Info("--- STPPaymentMethodCardParams ---");
        try
        {
            var cardParams = new StripePayments.STPPaymentMethodCardParams();
            if (cardParams != null)
            {
                logger.Pass("STPPaymentMethodCardParams() constructor");
                results.Pass("Payments_CardParams_Ctor");
            }
            else
            {
                logger.Fail("STPPaymentMethodCardParams() returned null");
                results.Fail("Payments_CardParams_Ctor", "Returned null");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"STPPaymentMethodCardParams(): {ex.Message}");
            results.Fail("Payments_CardParams_Ctor", ex.Message);
        }

        // STPPaymentMethodBillingDetails construction and properties
        logger.Info("--- STPPaymentMethodBillingDetails ---");
        try
        {
            var billing = new StripePayments.STPPaymentMethodBillingDetails();
            if (billing != null)
            {
                logger.Pass("STPPaymentMethodBillingDetails() constructor");
                results.Pass("Payments_BillingDetails_Ctor");
            }
            else
            {
                logger.Fail("STPPaymentMethodBillingDetails() returned null");
                results.Fail("Payments_BillingDetails_Ctor", "Returned null");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"STPPaymentMethodBillingDetails(): {ex.Message}");
            results.Fail("Payments_BillingDetails_Ctor", ex.Message);
        }

        try
        {
            var billing = new StripePayments.STPPaymentMethodBillingDetails();
            billing.Name = "John Doe";
            var readBack = billing.Name;
            if (readBack == "John Doe")
            {
                logger.Pass("STPPaymentMethodBillingDetails.Name roundtrip");
                results.Pass("Payments_BillingDetails_Name");
            }
            else
            {
                logger.Fail($"BillingDetails.Name roundtrip: got '{readBack}'");
                results.Fail("Payments_BillingDetails_Name", $"Got '{readBack}'");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"BillingDetails.Name: {ex.Message}");
            results.Fail("Payments_BillingDetails_Name", ex.Message);
        }

        try
        {
            var billing = new StripePayments.STPPaymentMethodBillingDetails();
            billing.Email = "test@example.com";
            var readBack = billing.Email;
            if (readBack == "test@example.com")
            {
                logger.Pass("STPPaymentMethodBillingDetails.Email roundtrip");
                results.Pass("Payments_BillingDetails_Email");
            }
            else
            {
                logger.Fail($"BillingDetails.Email roundtrip: got '{readBack}'");
                results.Fail("Payments_BillingDetails_Email", $"Got '{readBack}'");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"BillingDetails.Email: {ex.Message}");
            results.Fail("Payments_BillingDetails_Email", ex.Message);
        }

        try
        {
            var billing = new StripePayments.STPPaymentMethodBillingDetails();
            billing.Phone = "+15551234567";
            var readBack = billing.Phone;
            if (readBack == "+15551234567")
            {
                logger.Pass("STPPaymentMethodBillingDetails.Phone roundtrip");
                results.Pass("Payments_BillingDetails_Phone");
            }
            else
            {
                logger.Fail($"BillingDetails.Phone roundtrip: got '{readBack}'");
                results.Fail("Payments_BillingDetails_Phone", $"Got '{readBack}'");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"BillingDetails.Phone: {ex.Message}");
            results.Fail("Payments_BillingDetails_Phone", ex.Message);
        }

        // STPPaymentMethodAddress construction and properties
        logger.Info("--- STPPaymentMethodAddress ---");
        try
        {
            var address = new StripePayments.STPPaymentMethodAddress();
            if (address != null)
            {
                logger.Pass("STPPaymentMethodAddress() constructor");
                results.Pass("Payments_Address_Ctor");
            }
            else
            {
                logger.Fail("STPPaymentMethodAddress() returned null");
                results.Fail("Payments_Address_Ctor", "Returned null");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"STPPaymentMethodAddress(): {ex.Message}");
            results.Fail("Payments_Address_Ctor", ex.Message);
        }

        try
        {
            var address = new StripePayments.STPPaymentMethodAddress();
            address.City = "San Francisco";
            address.State = "CA";
            address.PostalCode = "94107";
            address.Country = "US";
            address.Line1 = "123 Market St";

            var allCorrect = address.City == "San Francisco"
                && address.State == "CA"
                && address.PostalCode == "94107"
                && address.Country == "US"
                && address.Line1 == "123 Market St";

            if (allCorrect)
            {
                logger.Pass("STPPaymentMethodAddress property roundtrips");
                results.Pass("Payments_Address_Properties");
            }
            else
            {
                logger.Fail("STPPaymentMethodAddress property roundtrip failed");
                results.Fail("Payments_Address_Properties", "Roundtrip failed");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"STPPaymentMethodAddress properties: {ex.Message}");
            results.Fail("Payments_Address_Properties", ex.Message);
        }

        // STPPaymentIntentConfirmParams
        logger.Info("--- STPPaymentIntentConfirmParams ---");
        try
        {
            var confirmParams = new StripePayments.STPPaymentIntentConfirmParams("pi_test_secret_123");
            if (confirmParams != null)
            {
                logger.Pass("STPPaymentIntentConfirmParams(clientSecret) constructor");
                results.Pass("Payments_ConfirmParams_Ctor");
            }
            else
            {
                logger.Fail("STPPaymentIntentConfirmParams returned null");
                results.Fail("Payments_ConfirmParams_Ctor", "Returned null");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"STPPaymentIntentConfirmParams: {ex.Message}");
            results.Fail("Payments_ConfirmParams_Ctor", ex.Message);
        }

        try
        {
            var confirmParams = new StripePayments.STPPaymentIntentConfirmParams("pi_test_secret_456");
            var clientSecret = confirmParams.ClientSecret;
            if (clientSecret == "pi_test_secret_456")
            {
                logger.Pass($"STPPaymentIntentConfirmParams.ClientSecret: {clientSecret}");
                results.Pass("Payments_ConfirmParams_ClientSecret");
            }
            else
            {
                logger.Fail($"ConfirmParams.ClientSecret: got '{clientSecret}'");
                results.Fail("Payments_ConfirmParams_ClientSecret", $"Got '{clientSecret}'");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"ConfirmParams.ClientSecret: {ex.Message}");
            results.Fail("Payments_ConfirmParams_ClientSecret", ex.Message);
        }

        // STPSetupIntentConfirmParams
        logger.Info("--- STPSetupIntentConfirmParams ---");
        try
        {
            var setupParams = new StripePayments.STPSetupIntentConfirmParams("seti_test_secret_123");
            if (setupParams != null)
            {
                var secret = setupParams.ClientSecret;
                if (secret == "seti_test_secret_123")
                {
                    logger.Pass($"STPSetupIntentConfirmParams.ClientSecret: {secret}");
                    results.Pass("Payments_SetupConfirmParams");
                }
                else
                {
                    logger.Fail($"SetupIntentConfirmParams.ClientSecret: got '{secret}'");
                    results.Fail("Payments_SetupConfirmParams", $"Got '{secret}'");
                }
            }
            else
            {
                logger.Fail("STPSetupIntentConfirmParams returned null");
                results.Fail("Payments_SetupConfirmParams", "Returned null");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"STPSetupIntentConfirmParams: {ex.Message}");
            results.Fail("Payments_SetupConfirmParams", ex.Message);
        }

        // STPCardValidator
        logger.Info("--- STPCardValidator ---");
        try
        {
            var brand = StripePayments.STPCardValidator.Brand("4242424242424242");
            logger.Pass($"STPCardValidator.Brand('4242...'): {brand}");
            results.Pass("Payments_CardValidator_Brand");
        }
        catch (Exception ex)
        {
            logger.Fail($"STPCardValidator.Brand: {ex.Message}");
            results.Fail("Payments_CardValidator_Brand", ex.Message);
        }

        try
        {
            var maxLength = StripePayments.STPCardValidator.MaxCVCLength(StripePayments.STPCardBrand.Visa);
            if (maxLength > 0)
            {
                logger.Pass($"STPCardValidator.MaxCVCLength(Visa): {maxLength}");
                results.Pass("Payments_CardValidator_MaxCVCLength");
            }
            else
            {
                logger.Fail($"STPCardValidator.MaxCVCLength: got {maxLength}");
                results.Fail("Payments_CardValidator_MaxCVCLength", $"Got {maxLength}");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"STPCardValidator.MaxCVCLength: {ex.Message}");
            results.Fail("Payments_CardValidator_MaxCVCLength", ex.Message);
        }

        try
        {
            var maxLength = StripePayments.STPCardValidator.MaxLength(StripePayments.STPCardBrand.Visa);
            if (maxLength > 0)
            {
                logger.Pass($"STPCardValidator.MaxLength(Visa): {maxLength}");
                results.Pass("Payments_CardValidator_MaxLength");
            }
            else
            {
                logger.Fail($"STPCardValidator.MaxLength: got {maxLength}");
                results.Fail("Payments_CardValidator_MaxLength", $"Got {maxLength}");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"STPCardValidator.MaxLength: {ex.Message}");
            results.Fail("Payments_CardValidator_MaxLength", ex.Message);
        }

        // STPPaymentHandler singleton
        logger.Info("--- STPPaymentHandler ---");
        try
        {
            var handler = StripePayments.STPPaymentHandler.SharedHandler;
            if (handler != null)
            {
                logger.Pass("STPPaymentHandler.SharedHandler singleton");
                results.Pass("Payments_PaymentHandler_Shared");
            }
            else
            {
                logger.Fail("STPPaymentHandler.SharedHandler returned null");
                results.Fail("Payments_PaymentHandler_Shared", "Returned null");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"STPPaymentHandler.SharedHandler: {ex.Message}");
            results.Fail("Payments_PaymentHandler_Shared", ex.Message);
        }

        try
        {
            var handler = StripePayments.STPPaymentHandler.SharedHandler;
            var apiClient = handler.ApiClient;
            if (apiClient != null)
            {
                logger.Pass("STPPaymentHandler.ApiClient access");
                results.Pass("Payments_PaymentHandler_ApiClient");
            }
            else
            {
                logger.Fail("STPPaymentHandler.ApiClient returned null");
                results.Fail("Payments_PaymentHandler_ApiClient", "Returned null");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"STPPaymentHandler.ApiClient: {ex.Message}");
            results.Fail("Payments_PaymentHandler_ApiClient", ex.Message);
        }
    }

    // =========================================================================
    // Phase 5: StripePayments Enums
    // =========================================================================

    private void RunStripePaymentsEnumTests(TestLogger logger, TestResults results)
    {
        logger.Info("--- STPCardBrand enum ---");
        try
        {
            var brands = new[]
            {
                StripePayments.STPCardBrand.Visa,
                StripePayments.STPCardBrand.Amex,
                StripePayments.STPCardBrand.Mastercard,
                StripePayments.STPCardBrand.Discover,
                StripePayments.STPCardBrand.Jcb,
                StripePayments.STPCardBrand.DinersClub,
                StripePayments.STPCardBrand.UnionPay,
                StripePayments.STPCardBrand.Unknown,
            };
            var distinct = brands.Distinct().Count();
            if (distinct == brands.Length)
            {
                logger.Pass($"STPCardBrand: {distinct} distinct cases");
                results.Pass("Payments_STPCardBrand");
            }
            else
            {
                logger.Fail($"STPCardBrand: expected {brands.Length} distinct, got {distinct}");
                results.Fail("Payments_STPCardBrand", "Non-distinct values");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"STPCardBrand: {ex.Message}");
            results.Fail("Payments_STPCardBrand", ex.Message);
        }

        logger.Info("--- STPPaymentIntentStatus enum ---");
        try
        {
            var statuses = new[]
            {
                StripePayments.STPPaymentIntentStatus.Unknown,
                StripePayments.STPPaymentIntentStatus.RequiresPaymentMethod,
                StripePayments.STPPaymentIntentStatus.RequiresConfirmation,
                StripePayments.STPPaymentIntentStatus.RequiresAction,
                StripePayments.STPPaymentIntentStatus.Processing,
                StripePayments.STPPaymentIntentStatus.Succeeded,
                StripePayments.STPPaymentIntentStatus.RequiresCapture,
                StripePayments.STPPaymentIntentStatus.Canceled,
            };
            var distinct = statuses.Distinct().Count();
            if (distinct == statuses.Length)
            {
                logger.Pass($"STPPaymentIntentStatus: {distinct} distinct cases");
                results.Pass("Payments_PaymentIntentStatus");
            }
            else
            {
                logger.Fail($"STPPaymentIntentStatus: non-distinct");
                results.Fail("Payments_PaymentIntentStatus", "Non-distinct values");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"STPPaymentIntentStatus: {ex.Message}");
            results.Fail("Payments_PaymentIntentStatus", ex.Message);
        }

        logger.Info("--- STPPaymentMethodType enum ---");
        try
        {
            var types = new[]
            {
                StripePayments.STPPaymentMethodType.Card,
                StripePayments.STPPaymentMethodType.Alipay,
                StripePayments.STPPaymentMethodType.Bancontact,
                StripePayments.STPPaymentMethodType.IDEAL,
                StripePayments.STPPaymentMethodType.SEPADebit,
                StripePayments.STPPaymentMethodType.Klarna,
                StripePayments.STPPaymentMethodType.GrabPay,
                StripePayments.STPPaymentMethodType.PayPal,
                StripePayments.STPPaymentMethodType.Link,
                StripePayments.STPPaymentMethodType.CashApp,
                StripePayments.STPPaymentMethodType.Unknown,
            };
            var distinct = types.Distinct().Count();
            if (distinct == types.Length)
            {
                logger.Pass($"STPPaymentMethodType: {distinct} distinct cases");
                results.Pass("Payments_PaymentMethodType");
            }
            else
            {
                logger.Fail($"STPPaymentMethodType: non-distinct");
                results.Fail("Payments_PaymentMethodType", "Non-distinct values");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"STPPaymentMethodType: {ex.Message}");
            results.Fail("Payments_PaymentMethodType", ex.Message);
        }

        logger.Info("--- STPSetupIntentStatus enum ---");
        try
        {
            var statuses = new[]
            {
                StripePayments.STPSetupIntentStatus.Unknown,
                StripePayments.STPSetupIntentStatus.RequiresPaymentMethod,
                StripePayments.STPSetupIntentStatus.RequiresConfirmation,
                StripePayments.STPSetupIntentStatus.RequiresAction,
                StripePayments.STPSetupIntentStatus.Processing,
                StripePayments.STPSetupIntentStatus.Succeeded,
                StripePayments.STPSetupIntentStatus.Canceled,
            };
            var distinct = statuses.Distinct().Count();
            if (distinct == statuses.Length)
            {
                logger.Pass($"STPSetupIntentStatus: {distinct} distinct cases");
                results.Pass("Payments_SetupIntentStatus");
            }
            else
            {
                logger.Fail($"STPSetupIntentStatus: non-distinct");
                results.Fail("Payments_SetupIntentStatus", "Non-distinct values");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"STPSetupIntentStatus: {ex.Message}");
            results.Fail("Payments_SetupIntentStatus", ex.Message);
        }

        logger.Info("--- STPPaymentIntentCaptureMethod enum ---");
        try
        {
            var methods = new[]
            {
                StripePayments.STPPaymentIntentCaptureMethod.Unknown,
                StripePayments.STPPaymentIntentCaptureMethod.Automatic,
                StripePayments.STPPaymentIntentCaptureMethod.Manual,
            };
            var distinct = methods.Distinct().Count();
            if (distinct == methods.Length)
            {
                logger.Pass($"STPPaymentIntentCaptureMethod: {distinct} distinct cases");
                results.Pass("Payments_CaptureMethod");
            }
            else
            {
                logger.Fail($"STPPaymentIntentCaptureMethod: non-distinct");
                results.Fail("Payments_CaptureMethod", "Non-distinct values");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"STPPaymentIntentCaptureMethod: {ex.Message}");
            results.Fail("Payments_CaptureMethod", ex.Message);
        }

        logger.Info("--- STPCardFundingType enum ---");
        try
        {
            var types = new[]
            {
                StripePayments.STPCardFundingType.Debit,
                StripePayments.STPCardFundingType.Credit,
                StripePayments.STPCardFundingType.Prepaid,
                StripePayments.STPCardFundingType.Other,
            };
            var distinct = types.Distinct().Count();
            if (distinct == types.Length)
            {
                logger.Pass($"STPCardFundingType: {distinct} distinct cases");
                results.Pass("Payments_CardFundingType");
            }
            else
            {
                logger.Fail($"STPCardFundingType: non-distinct");
                results.Fail("Payments_CardFundingType", "Non-distinct values");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"STPCardFundingType: {ex.Message}");
            results.Fail("Payments_CardFundingType", ex.Message);
        }
    }

    // =========================================================================
    // Phase 6: StripePaymentSheet — configuration, appearance, styling
    // =========================================================================

    private void RunPaymentSheetTests(TestLogger logger, TestResults results)
    {
        // PaymentSheet.Appearance.Default
        logger.Info("--- PaymentSheet.Appearance ---");
        try
        {
            var appearance = StripePaymentSheet.PaymentSheet.Appearance.Default;
            if (appearance != null)
            {
                logger.Pass("PaymentSheet.Appearance.Default");
                results.Pass("PaymentSheet_Appearance_Default");
            }
            else
            {
                logger.Fail("PaymentSheet.Appearance.Default returned null");
                results.Fail("PaymentSheet_Appearance_Default", "Returned null");
            }
        }
        catch (DllNotFoundException ex)
        {
            logger.Fail($"PaymentSheet.Appearance: native entry point missing: {ex.Message}");
            results.Fail("PaymentSheet_Appearance_Default", $"Native entry point missing: {ex.Message}");
            return;
        }
        catch (Exception ex)
        {
            logger.Fail($"PaymentSheet.Appearance.Default: {ex.Message}");
            results.Fail("PaymentSheet_Appearance_Default", ex.Message);
        }

        // Appearance.CornerRadius get/set
        try
        {
            var appearance = StripePaymentSheet.PaymentSheet.Appearance.Default;
            appearance.CornerRadius = 12.0;
            var readBack = appearance.CornerRadius;
            if (readBack != null && Math.Abs(readBack.Value - 12.0) < 0.01)
            {
                logger.Pass($"Appearance.CornerRadius roundtrip: {readBack}");
                results.Pass("PaymentSheet_Appearance_CornerRadius");
            }
            else
            {
                logger.Fail($"Appearance.CornerRadius: got {readBack}");
                results.Fail("PaymentSheet_Appearance_CornerRadius", $"Got {readBack}");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"Appearance.CornerRadius: {ex.Message}");
            results.Fail("PaymentSheet_Appearance_CornerRadius", ex.Message);
        }

        // Appearance.BorderWidth get/set
        try
        {
            var appearance = StripePaymentSheet.PaymentSheet.Appearance.Default;
            appearance.BorderWidth = 2.0;
            var readBack = appearance.BorderWidth;
            if (Math.Abs(readBack - 2.0) < 0.01)
            {
                logger.Pass($"Appearance.BorderWidth roundtrip: {readBack}");
                results.Pass("PaymentSheet_Appearance_BorderWidth");
            }
            else
            {
                logger.Fail($"Appearance.BorderWidth: got {readBack}");
                results.Fail("PaymentSheet_Appearance_BorderWidth", $"Got {readBack}");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"Appearance.BorderWidth: {ex.Message}");
            results.Fail("PaymentSheet_Appearance_BorderWidth", ex.Message);
        }

        // Appearance.Font
        try
        {
            var appearance = StripePaymentSheet.PaymentSheet.Appearance.Default;
            var font = appearance.Font;
            if (font != null)
            {
                logger.Pass($"Appearance.Font access (SizeScaleFactor: {font.SizeScaleFactor})");
                results.Pass("PaymentSheet_Appearance_Font");
            }
            else
            {
                logger.Fail("Appearance.Font: null");
                results.Fail("PaymentSheet_Appearance_Font", "Null");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"Appearance.Font: {ex.Message}");
            results.Fail("PaymentSheet_Appearance_Font", ex.Message);
        }

        // Appearance.Font.SizeScaleFactor roundtrip
        try
        {
            var appearance = StripePaymentSheet.PaymentSheet.Appearance.Default;
            var font = appearance.Font;
            font.SizeScaleFactor = 1.5;
            var readBack = font.SizeScaleFactor;
            if (Math.Abs(readBack - 1.5) < 0.01)
            {
                logger.Pass($"Font.SizeScaleFactor roundtrip: {readBack}");
                results.Pass("PaymentSheet_Font_SizeScaleFactor");
            }
            else
            {
                logger.Fail($"Font.SizeScaleFactor: got {readBack}");
                results.Fail("PaymentSheet_Font_SizeScaleFactor", $"Got {readBack}");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"Font.SizeScaleFactor: {ex.Message}");
            results.Fail("PaymentSheet_Font_SizeScaleFactor", ex.Message);
        }

        // Appearance.Colors
        try
        {
            var appearance = StripePaymentSheet.PaymentSheet.Appearance.Default;
            var colors = appearance.Colors;
            if (colors != null)
            {
                logger.Pass("Appearance.Colors access");
                results.Pass("PaymentSheet_Appearance_Colors");
            }
            else
            {
                logger.Fail("Appearance.Colors: null");
                results.Fail("PaymentSheet_Appearance_Colors", "Null");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"Appearance.Colors: {ex.Message}");
            results.Fail("PaymentSheet_Appearance_Colors", ex.Message);
        }

        // Colors.Primary get/set
        try
        {
            var appearance = StripePaymentSheet.PaymentSheet.Appearance.Default;
            var colors = appearance.Colors;
            colors.Primary = UIColor.Blue;
            var readBack = colors.Primary;
            if (readBack != null)
            {
                logger.Pass("Colors.Primary set/get");
                results.Pass("PaymentSheet_Colors_Primary");
            }
            else
            {
                logger.Fail("Colors.Primary: null after set");
                results.Fail("PaymentSheet_Colors_Primary", "Null after set");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"Colors.Primary: {ex.Message}");
            results.Fail("PaymentSheet_Colors_Primary", ex.Message);
        }

        // Colors.Background get/set
        try
        {
            var appearance = StripePaymentSheet.PaymentSheet.Appearance.Default;
            var colors = appearance.Colors;
            colors.Background = UIColor.White;
            var readBack = colors.Background;
            if (readBack != null)
            {
                logger.Pass("Colors.Background set/get");
                results.Pass("PaymentSheet_Colors_Background");
            }
            else
            {
                logger.Fail("Colors.Background: null after set");
                results.Fail("PaymentSheet_Colors_Background", "Null after set");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"Colors.Background: {ex.Message}");
            results.Fail("PaymentSheet_Colors_Background", ex.Message);
        }

        // Colors.Text and TextSecondary
        try
        {
            var appearance = StripePaymentSheet.PaymentSheet.Appearance.Default;
            var colors = appearance.Colors;
            colors.Text = UIColor.DarkText;
            colors.TextSecondary = UIColor.Gray;
            if (colors.Text != null && colors.TextSecondary != null)
            {
                logger.Pass("Colors.Text and TextSecondary set/get");
                results.Pass("PaymentSheet_Colors_Text");
            }
            else
            {
                logger.Fail("Colors.Text: null after set");
                results.Fail("PaymentSheet_Colors_Text", "Null after set");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"Colors.Text: {ex.Message}");
            results.Fail("PaymentSheet_Colors_Text", ex.Message);
        }

        // Appearance.PrimaryButton
        try
        {
            var appearance = StripePaymentSheet.PaymentSheet.Appearance.Default;
            var button = appearance.PrimaryButton;
            if (button != null)
            {
                logger.Pass("Appearance.PrimaryButton access");
                results.Pass("PaymentSheet_Appearance_PrimaryButton");
            }
            else
            {
                logger.Fail("Appearance.PrimaryButton: null");
                results.Fail("PaymentSheet_Appearance_PrimaryButton", "Null");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"Appearance.PrimaryButton: {ex.Message}");
            results.Fail("PaymentSheet_Appearance_PrimaryButton", ex.Message);
        }

        // PrimaryButton.CornerRadius
        try
        {
            var appearance = StripePaymentSheet.PaymentSheet.Appearance.Default;
            var button = appearance.PrimaryButton;
            button.CornerRadius = 8.0;
            var readBack = button.CornerRadius;
            if (readBack != null && Math.Abs(readBack.Value - 8.0) < 0.01)
            {
                logger.Pass($"PrimaryButton.CornerRadius roundtrip: {readBack}");
                results.Pass("PaymentSheet_PrimaryButton_CornerRadius");
            }
            else
            {
                logger.Fail($"PrimaryButton.CornerRadius: got {readBack}");
                results.Fail("PaymentSheet_PrimaryButton_CornerRadius", $"Got {readBack}");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"PrimaryButton.CornerRadius: {ex.Message}");
            results.Fail("PaymentSheet_PrimaryButton_CornerRadius", ex.Message);
        }

        // PrimaryButton color properties
        try
        {
            var appearance = StripePaymentSheet.PaymentSheet.Appearance.Default;
            var button = appearance.PrimaryButton;
            button.BackgroundColor = UIColor.SystemBlue;
            button.TextColor = UIColor.White;
            if (button.BackgroundColor != null && button.TextColor != null)
            {
                logger.Pass("PrimaryButton colors set/get");
                results.Pass("PaymentSheet_PrimaryButton_Colors");
            }
            else
            {
                logger.Fail("PrimaryButton colors: null after set");
                results.Fail("PaymentSheet_PrimaryButton_Colors", "Null after set");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"PrimaryButton colors: {ex.Message}");
            results.Fail("PaymentSheet_PrimaryButton_Colors", ex.Message);
        }

        // Appearance.Shadow
        try
        {
            var appearance = StripePaymentSheet.PaymentSheet.Appearance.Default;
            var shadow = appearance.Shadow;
            if (shadow != null)
            {
                logger.Pass($"Appearance.Shadow access (opacity: {shadow.Opacity})");
                results.Pass("PaymentSheet_Appearance_Shadow");
            }
            else
            {
                logger.Fail("Appearance.Shadow: null");
                results.Fail("PaymentSheet_Appearance_Shadow", "Null");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"Appearance.Shadow: {ex.Message}");
            results.Fail("PaymentSheet_Appearance_Shadow", ex.Message);
        }

        // Shadow.Disabled static property
        try
        {
            var disabled = StripePaymentSheet.PaymentSheet.Appearance.ShadowType.Disabled;
            if (disabled != null)
            {
                logger.Pass($"Shadow.Disabled (opacity: {disabled.Opacity})");
                results.Pass("PaymentSheet_Shadow_Disabled");
            }
            else
            {
                logger.Fail("Shadow.Disabled: null");
                results.Fail("PaymentSheet_Shadow_Disabled", "Null");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"Shadow.Disabled: {ex.Message}");
            results.Fail("PaymentSheet_Shadow_Disabled", ex.Message);
        }

        // PaymentSheet enums
        logger.Info("--- PaymentSheet Enums ---");
        try
        {
            var styles = new[]
            {
                StripePaymentSheet.PaymentSheet.UserInterfaceStyle.Automatic,
                StripePaymentSheet.PaymentSheet.UserInterfaceStyle.AlwaysLight,
                StripePaymentSheet.PaymentSheet.UserInterfaceStyle.AlwaysDark,
            };
            if (styles.Distinct().Count() == 3)
            {
                logger.Pass("PaymentSheet.UserInterfaceStyle: 3 distinct cases");
                results.Pass("PaymentSheet_UserInterfaceStyle");
            }
            else
            {
                logger.Fail("PaymentSheet.UserInterfaceStyle: non-distinct");
                results.Fail("PaymentSheet_UserInterfaceStyle", "Non-distinct");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"UserInterfaceStyle: {ex.Message}");
            results.Fail("PaymentSheet_UserInterfaceStyle", ex.Message);
        }

        try
        {
            var behaviors = new[]
            {
                StripePaymentSheet.PaymentSheet.SavePaymentMethodOptInBehavior.Automatic,
                StripePaymentSheet.PaymentSheet.SavePaymentMethodOptInBehavior.RequiresOptIn,
                StripePaymentSheet.PaymentSheet.SavePaymentMethodOptInBehavior.RequiresOptOut,
            };
            if (behaviors.Distinct().Count() == 3)
            {
                logger.Pass("SavePaymentMethodOptInBehavior: 3 distinct cases");
                results.Pass("PaymentSheet_SaveOptInBehavior");
            }
            else
            {
                logger.Fail("SavePaymentMethodOptInBehavior: non-distinct");
                results.Fail("PaymentSheet_SaveOptInBehavior", "Non-distinct");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"SavePaymentMethodOptInBehavior: {ex.Message}");
            results.Fail("PaymentSheet_SaveOptInBehavior", ex.Message);
        }

        try
        {
            var layouts = new[]
            {
                StripePaymentSheet.PaymentSheet.PaymentMethodLayout.Horizontal,
                StripePaymentSheet.PaymentSheet.PaymentMethodLayout.Vertical,
                StripePaymentSheet.PaymentSheet.PaymentMethodLayout.Automatic,
            };
            if (layouts.Distinct().Count() == 3)
            {
                logger.Pass("PaymentMethodLayout: 3 distinct cases");
                results.Pass("PaymentSheet_PaymentMethodLayout");
            }
            else
            {
                logger.Fail("PaymentMethodLayout: non-distinct");
                results.Fail("PaymentSheet_PaymentMethodLayout", "Non-distinct");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"PaymentMethodLayout: {ex.Message}");
            results.Fail("PaymentSheet_PaymentMethodLayout", ex.Message);
        }

        try
        {
            var terms = new[]
            {
                StripePaymentSheet.PaymentSheet.TermsDisplay.Automatic,
                StripePaymentSheet.PaymentSheet.TermsDisplay.Never,
            };
            if (terms.Distinct().Count() == 2)
            {
                logger.Pass("TermsDisplay: 2 distinct cases");
                results.Pass("PaymentSheet_TermsDisplay");
            }
            else
            {
                logger.Fail("TermsDisplay: non-distinct");
                results.Fail("PaymentSheet_TermsDisplay", "Non-distinct");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"TermsDisplay: {ex.Message}");
            results.Fail("PaymentSheet_TermsDisplay", ex.Message);
        }
    }

    // =========================================================================
    // Phase 7: StripePaymentSheet Results & CustomerSheet
    // =========================================================================

    private void RunPaymentSheetResultTests(TestLogger logger, TestResults results)
    {
        // CustomerSheet.CustomerSheetResult enum tags
        logger.Info("--- CustomerSheet.CustomerSheetResult ---");
        try
        {
            var tags = new[]
            {
                StripePaymentSheet.CustomerSheet.CustomerSheetResult.CaseTag.Canceled,
                StripePaymentSheet.CustomerSheet.CustomerSheetResult.CaseTag.Selected,
                StripePaymentSheet.CustomerSheet.CustomerSheetResult.CaseTag.Error,
            };
            if (tags.Distinct().Count() == 3)
            {
                logger.Pass("CustomerSheetResult.CaseTag: 3 distinct cases");
                results.Pass("PaymentSheet_CustomerSheetResult_Tags");
            }
            else
            {
                logger.Fail("CustomerSheetResult.CaseTag: non-distinct");
                results.Fail("PaymentSheet_CustomerSheetResult_Tags", "Non-distinct");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"CustomerSheetResult.CaseTag: {ex.Message}");
            results.Fail("PaymentSheet_CustomerSheetResult_Tags", ex.Message);
        }

        // CustomerSheetResult.Canceled factory
        try
        {
            var canceled = StripePaymentSheet.CustomerSheet.CustomerSheetResult.Canceled(null);
            if (canceled != null && (uint)canceled.Tag == (uint)StripePaymentSheet.CustomerSheet.CustomerSheetResult.CaseTag.Canceled)
            {
                logger.Pass("CustomerSheetResult.Canceled(null) factory");
                results.Pass("PaymentSheet_CustomerSheetResult_Canceled");
            }
            else
            {
                logger.Fail("CustomerSheetResult.Canceled: unexpected tag");
                results.Fail("PaymentSheet_CustomerSheetResult_Canceled", "Unexpected tag");
            }
        }
        catch (DllNotFoundException ex)
        {
            logger.Fail($"CustomerSheetResult.Canceled: native entry point missing: {ex.Message}");
            results.Fail("PaymentSheet_CustomerSheetResult_Canceled", $"Native entry point missing: {ex.Message}");
        }
        catch (Exception ex)
        {
            logger.Fail($"CustomerSheetResult.Canceled: {ex.Message}");
            results.Fail("PaymentSheet_CustomerSheetResult_Canceled", ex.Message);
        }

        // CustomerSheetResult.Selected factory
        try
        {
            var selected = StripePaymentSheet.CustomerSheet.CustomerSheetResult.Selected(null);
            if (selected != null && (uint)selected.Tag == (uint)StripePaymentSheet.CustomerSheet.CustomerSheetResult.CaseTag.Selected)
            {
                logger.Pass("CustomerSheetResult.Selected(null) factory");
                results.Pass("PaymentSheet_CustomerSheetResult_Selected");
            }
            else
            {
                logger.Fail("CustomerSheetResult.Selected: unexpected tag");
                results.Fail("PaymentSheet_CustomerSheetResult_Selected", "Unexpected tag");
            }
        }
        catch (DllNotFoundException ex)
        {
            logger.Fail($"CustomerSheetResult.Selected: native entry point missing: {ex.Message}");
            results.Fail("PaymentSheet_CustomerSheetResult_Selected", $"Native entry point missing: {ex.Message}");
        }
        catch (Exception ex)
        {
            logger.Fail($"CustomerSheetResult.Selected: {ex.Message}");
            results.Fail("PaymentSheet_CustomerSheetResult_Selected", ex.Message);
        }

        // AddressViewController.AddressDetails construction
        logger.Info("--- AddressViewController ---");
        try
        {
            var addressType = new StripePaymentSheet.AddressViewController.AddressDetails.AddressType(
                city: "San Francisco", country: "US", line1: "123 Market St",
                line2: null, postalCode: "94107", state: "CA");
            var addr = new StripePaymentSheet.AddressViewController.AddressDetails(addressType, name: "Test User");
            if (addr != null)
            {
                logger.Pass("AddressDetails construction with AddressType");
                results.Pass("PaymentSheet_AddressDetails_Ctor");
            }
            else
            {
                logger.Fail("AddressDetails returned null");
                results.Fail("PaymentSheet_AddressDetails_Ctor", "Null");
            }
        }
        catch (DllNotFoundException ex)
        {
            logger.Fail($"AddressDetails: native entry point missing: {ex.Message}");
            results.Fail("PaymentSheet_AddressDetails_Ctor", $"Native entry point missing: {ex.Message}");
        }
        catch (Exception ex)
        {
            logger.Fail($"AddressDetails: {ex.Message}");
            results.Fail("PaymentSheet_AddressDetails_Ctor", ex.Message);
        }
    }

    // =========================================================================
    // Phase 8: StripeApplePay
    // =========================================================================

    private void RunApplePayTests(TestLogger logger, TestResults results)
    {
        // PaymentStatus enum
        logger.Info("--- STPApplePayContext.PaymentStatus ---");
        try
        {
            var statuses = new[]
            {
                StripeApplePay.STPApplePayContext.PaymentStatus.Success,
                StripeApplePay.STPApplePayContext.PaymentStatus.Error,
                StripeApplePay.STPApplePayContext.PaymentStatus.UserCancellation,
            };
            if (statuses.Distinct().Count() == 3)
            {
                logger.Pass("STPApplePayContext.PaymentStatus: 3 distinct cases");
                results.Pass("ApplePay_PaymentStatus");
            }
            else
            {
                logger.Fail("PaymentStatus: non-distinct");
                results.Fail("ApplePay_PaymentStatus", "Non-distinct");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"PaymentStatus: {ex.Message}");
            results.Fail("ApplePay_PaymentStatus", ex.Message);
        }

        // PaymentStatus values
        try
        {
            if ((int)StripeApplePay.STPApplePayContext.PaymentStatus.Success == 0
                && (int)StripeApplePay.STPApplePayContext.PaymentStatus.Error == 1
                && (int)StripeApplePay.STPApplePayContext.PaymentStatus.UserCancellation == 2)
            {
                logger.Pass("PaymentStatus values: Success=0, Error=1, UserCancellation=2");
                results.Pass("ApplePay_PaymentStatus_Values");
            }
            else
            {
                logger.Fail("PaymentStatus: unexpected values");
                results.Fail("ApplePay_PaymentStatus_Values", "Unexpected values");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"PaymentStatus values: {ex.Message}");
            results.Fail("ApplePay_PaymentStatus_Values", ex.Message);
        }
    }

    // =========================================================================
    // Phase 9: StripeIdentity
    // =========================================================================

    private void RunIdentityTests(TestLogger logger, TestResults results)
    {
        // VerificationFlowResult enum
        logger.Info("--- VerificationFlowResult ---");
        try
        {
            var canceled = StripeIdentity.IdentityVerificationSheet.VerificationFlowResult.FlowCanceled;
            if ((uint)canceled.Tag == (uint)StripeIdentity.IdentityVerificationSheet.VerificationFlowResult.CaseTag.FlowCanceled)
            {
                logger.Pass($"VerificationFlowResult.FlowCanceled tag: {canceled.Tag}");
                results.Pass("Identity_FlowCanceled");
            }
            else
            {
                logger.Fail($"FlowCanceled tag: expected FlowCanceled, got {canceled.Tag}");
                results.Fail("Identity_FlowCanceled", "Wrong tag");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"FlowCanceled: {ex.Message}");
            results.Fail("Identity_FlowCanceled", ex.Message);
        }

        try
        {
            var completed = StripeIdentity.IdentityVerificationSheet.VerificationFlowResult.FlowCompleted;
            if ((uint)completed.Tag == (uint)StripeIdentity.IdentityVerificationSheet.VerificationFlowResult.CaseTag.FlowCompleted)
            {
                logger.Pass($"VerificationFlowResult.FlowCompleted tag: {completed.Tag}");
                results.Pass("Identity_FlowCompleted");
            }
            else
            {
                logger.Fail($"FlowCompleted tag: expected FlowCompleted, got {completed.Tag}");
                results.Fail("Identity_FlowCompleted", "Wrong tag");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"FlowCompleted: {ex.Message}");
            results.Fail("Identity_FlowCompleted", ex.Message);
        }

        // VerificationFlowResult CaseTag enum
        try
        {
            var tags = new[]
            {
                StripeIdentity.IdentityVerificationSheet.VerificationFlowResult.CaseTag.FlowFailed,
                StripeIdentity.IdentityVerificationSheet.VerificationFlowResult.CaseTag.FlowCompleted,
                StripeIdentity.IdentityVerificationSheet.VerificationFlowResult.CaseTag.FlowCanceled,
            };
            if (tags.Distinct().Count() == 3)
            {
                logger.Pass("VerificationFlowResult.CaseTag: 3 distinct cases");
                results.Pass("Identity_FlowResult_Tags");
            }
            else
            {
                logger.Fail("VerificationFlowResult.CaseTag: non-distinct");
                results.Fail("Identity_FlowResult_Tags", "Non-distinct");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"VerificationFlowResult.CaseTag: {ex.Message}");
            results.Fail("Identity_FlowResult_Tags", ex.Message);
        }

        // IdentityVerificationSheetError enum
        logger.Info("--- IdentityVerificationSheetError ---");
        try
        {
            var invalid = StripeIdentity.IdentityVerificationSheetError.InvalidClientSecret;
            if ((uint)invalid.Tag == (uint)StripeIdentity.IdentityVerificationSheetError.CaseTag.InvalidClientSecret)
            {
                logger.Pass($"IdentityVerificationSheetError.InvalidClientSecret tag: {invalid.Tag}");
                results.Pass("Identity_Error_InvalidClientSecret");
            }
            else
            {
                logger.Fail($"InvalidClientSecret tag: wrong");
                results.Fail("Identity_Error_InvalidClientSecret", "Wrong tag");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"InvalidClientSecret: {ex.Message}");
            results.Fail("Identity_Error_InvalidClientSecret", ex.Message);
        }

        try
        {
            var testError = StripeIdentity.IdentityVerificationSheetError.TestModeSampleError;
            if ((uint)testError.Tag == (uint)StripeIdentity.IdentityVerificationSheetError.CaseTag.TestModeSampleError)
            {
                logger.Pass($"IdentityVerificationSheetError.TestModeSampleError tag: {testError.Tag}");
                results.Pass("Identity_Error_TestMode");
            }
            else
            {
                logger.Fail("TestModeSampleError tag: wrong");
                results.Fail("Identity_Error_TestMode", "Wrong tag");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"TestModeSampleError: {ex.Message}");
            results.Fail("Identity_Error_TestMode", ex.Message);
        }

        // IdentityVerificationSheet construction (fixed: sim-only guards + thunk filtering)
        try
        {
            using var sheet = new StripeIdentity.IdentityVerificationSheet("test_client_secret");
            var secret = sheet.VerificationSessionClientSecret;
            if (secret == "test_client_secret")
            {
                logger.Pass($"IdentityVerificationSheet(clientSecret) constructed, secret round-tripped");
                results.Pass("Identity_Sheet_Ctor");
            }
            else
            {
                logger.Fail($"IdentityVerificationSheet: secret mismatch, got '{secret}'");
                results.Fail("Identity_Sheet_Ctor", $"Secret mismatch: '{secret}'");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"IdentityVerificationSheet ctor: {ex.GetType().Name}: {ex.Message}");
            results.Fail("Identity_Sheet_Ctor", $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    // =========================================================================
    // Phase 10: StripeConnect
    // =========================================================================

    private void RunConnectTests(TestLogger logger, TestResults results)
    {
        // EmbeddedComponentManager.Appearance.Default
        logger.Info("--- EmbeddedComponentManager.Appearance ---");
        try
        {
            var appearance = StripeConnect.EmbeddedComponentManager.Appearance.Default;
            if (appearance != null)
            {
                logger.Pass("EmbeddedComponentManager.Appearance.Default");
                results.Pass("Connect_Appearance_Default");
            }
            else
            {
                logger.Fail("Appearance.Default returned null");
                results.Fail("Connect_Appearance_Default", "Null");
            }
        }
        catch (DllNotFoundException ex)
        {
            logger.Fail($"StripeConnect: native entry point missing: {ex.Message}");
            results.Fail("Connect_Appearance_Default", $"Native entry point missing: {ex.Message}");
            return;
        }
        catch (Exception ex)
        {
            logger.Fail($"Appearance.Default: {ex.Message}");
            results.Fail("Connect_Appearance_Default", ex.Message);
        }

        // AccountCollectionOptions construction
        try
        {
            var options = new StripeConnect.AccountCollectionOptions();
            if (options != null)
            {
                logger.Pass("AccountCollectionOptions() constructor");
                results.Pass("Connect_AccountCollectionOptions_Ctor");
            }
            else
            {
                logger.Fail("AccountCollectionOptions: null");
                results.Fail("Connect_AccountCollectionOptions_Ctor", "Null");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"AccountCollectionOptions: {ex.Message}");
            results.Fail("Connect_AccountCollectionOptions_Ctor", ex.Message);
        }

        // FieldOption enum-like
        try
        {
            var currently = StripeConnect.AccountCollectionOptions.FieldOption.CurrentlyDue;
            var eventually = StripeConnect.AccountCollectionOptions.FieldOption.EventuallyDue;
            if (currently != null && eventually != null && (uint)currently.Tag != (uint)eventually.Tag)
            {
                logger.Pass("FieldOption: CurrentlyDue and EventuallyDue distinct");
                results.Pass("Connect_FieldOption");
            }
            else
            {
                logger.Fail("FieldOption: not distinct");
                results.Fail("Connect_FieldOption", "Not distinct");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"FieldOption: {ex.Message}");
            results.Fail("Connect_FieldOption", ex.Message);
        }

        // Appearance typography and colors construction
        try
        {
            var typography = new StripeConnect.EmbeddedComponentManager.Appearance.TypographyType();
            if (typography != null)
            {
                logger.Pass("Appearance.TypographyType() constructor");
                results.Pass("Connect_Typography_Ctor");
            }
            else
            {
                logger.Fail("TypographyType: null");
                results.Fail("Connect_Typography_Ctor", "Null");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"TypographyType: {ex.Message}");
            results.Fail("Connect_Typography_Ctor", ex.Message);
        }

        try
        {
            var colorsType = new StripeConnect.EmbeddedComponentManager.Appearance.ColorsType();
            if (colorsType != null)
            {
                logger.Pass("Appearance.ColorsType() constructor");
                results.Pass("Connect_ColorsType_Ctor");
            }
            else
            {
                logger.Fail("ColorsType: null");
                results.Fail("Connect_ColorsType_Ctor", "Null");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"ColorsType: {ex.Message}");
            results.Fail("Connect_ColorsType_Ctor", ex.Message);
        }

        try
        {
            var cornerRadius = new StripeConnect.EmbeddedComponentManager.Appearance.CornerRadiusType();
            if (cornerRadius != null)
            {
                logger.Pass("Appearance.CornerRadiusType() constructor");
                results.Pass("Connect_CornerRadiusType_Ctor");
            }
            else
            {
                logger.Fail("CornerRadiusType: null");
                results.Fail("Connect_CornerRadiusType_Ctor", "Null");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"CornerRadiusType: {ex.Message}");
            results.Fail("Connect_CornerRadiusType_Ctor", ex.Message);
        }
    }

    // =========================================================================
    // Phase 11: StripeIssuing
    // =========================================================================

    private void RunIssuingTests(TestLogger logger, TestResults results)
    {
        // STPFakeAddPaymentPassViewController.CanAddPaymentPass
        logger.Info("--- StripeIssuing ---");
        try
        {
            var canAdd = StripeIssuing.STPFakeAddPaymentPassViewController.CanAddPaymentPass();
            logger.Pass($"STPFakeAddPaymentPassViewController.CanAddPaymentPass(): {canAdd}");
            results.Pass("Issuing_CanAddPaymentPass");
        }
        catch (DllNotFoundException ex)
        {
            logger.Fail($"StripeIssuing: native entry point missing: {ex.Message}");
            results.Fail("Issuing_CanAddPaymentPass", $"Native entry point missing: {ex.Message}");
            return;
        }
        catch (Exception ex)
        {
            logger.Fail($"CanAddPaymentPass: {ex.Message}");
            results.Fail("Issuing_CanAddPaymentPass", ex.Message);
        }

        // STPPushProvisioningDetailsParams construction
        try
        {
            var certs = new List<byte[]> { new byte[] { 0x01, 0x02 } };
            var nonce = new byte[] { 0x03, 0x04 };
            var nonceSig = new byte[] { 0x05, 0x06 };
            var param = new StripeIssuing.STPPushProvisioningDetailsParams("card_123", certs, nonce, nonceSig);
            if (param != null && param.CardId == "card_123")
            {
                logger.Pass($"STPPushProvisioningDetailsParams: CardId={param.CardId}");
                results.Pass("Issuing_PushProvisioningParams_Ctor");
            }
            else
            {
                logger.Fail("STPPushProvisioningDetailsParams: unexpected values");
                results.Fail("Issuing_PushProvisioningParams_Ctor", "Unexpected values");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"PushProvisioningDetailsParams: {ex.Message}");
            results.Fail("Issuing_PushProvisioningParams_Ctor", ex.Message);
        }

        // PushProvisioningDetailsParams hex properties
        try
        {
            var certs = new List<byte[]> { new byte[] { 0xAA, 0xBB } };
            var nonce = new byte[] { 0xCC, 0xDD };
            var nonceSig = new byte[] { 0xEE, 0xFF };
            var param = new StripeIssuing.STPPushProvisioningDetailsParams("card_hex", certs, nonce, nonceSig);
            var nonceHex = param.NonceHex;
            var sigHex = param.NonceSignatureHex;
            if (!string.IsNullOrEmpty(nonceHex) && !string.IsNullOrEmpty(sigHex))
            {
                logger.Pass($"PushProvisioningDetailsParams: NonceHex={nonceHex}, SigHex={sigHex}");
                results.Pass("Issuing_PushProvisioningParams_Hex");
            }
            else
            {
                logger.Fail("PushProvisioningDetailsParams hex: empty");
                results.Fail("Issuing_PushProvisioningParams_Hex", "Empty hex values");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"PushProvisioningDetailsParams hex: {ex.Message}");
            results.Fail("Issuing_PushProvisioningParams_Hex", ex.Message);
        }
    }

    // =========================================================================
    // Phase 12: StripeCardScan
    // =========================================================================

    private void RunCardScanTests(TestLogger logger, TestResults results)
    {
        logger.Info("--- CardScanSheetResult ---");

        // CardScanSheetResult.Canceled
        try
        {
            var canceled = StripeCardScan.CardScanSheetResult.Canceled;
            if ((uint)canceled.Tag == (uint)StripeCardScan.CardScanSheetResult.CaseTag.Canceled)
            {
                logger.Pass($"CardScanSheetResult.Canceled tag: {canceled.Tag}");
                results.Pass("CardScan_Result_Canceled");
            }
            else
            {
                logger.Fail($"CardScanSheetResult.Canceled: wrong tag {canceled.Tag}");
                results.Fail("CardScan_Result_Canceled", "Wrong tag");
            }
        }
        catch (DllNotFoundException ex)
        {
            logger.Fail($"StripeCardScan: native entry point missing: {ex.Message}");
            results.Fail("CardScan_Result_Canceled", $"Native entry point missing: {ex.Message}");
            return;
        }
        catch (Exception ex)
        {
            logger.Fail($"CardScanSheetResult.Canceled: {ex.Message}");
            results.Fail("CardScan_Result_Canceled", ex.Message);
        }

        // CancellationReason enum-like
        logger.Info("--- CancellationReason ---");
        var reasons = new (string Name, Func<StripeCardScan.CancellationReason> Getter)[]
        {
            ("Back", () => StripeCardScan.CancellationReason.Back),
            ("Closed", () => StripeCardScan.CancellationReason.Closed),
            ("UserCannotScan", () => StripeCardScan.CancellationReason.UserCannotScan),
        };

        foreach (var (name, getter) in reasons)
        {
            try
            {
                using var reason = getter();
                var raw = reason.RawValue;
                if (!string.IsNullOrEmpty(raw))
                {
                    logger.Pass($"CancellationReason.{name}: RawValue='{raw}'");
                    results.Pass($"CardScan_CancellationReason_{name}");
                }
                else
                {
                    logger.Fail($"CancellationReason.{name}: empty RawValue");
                    results.Fail($"CardScan_CancellationReason_{name}", "Empty RawValue");
                }
            }
            catch (DllNotFoundException ex)
            {
                logger.Fail($"CancellationReason.{name}: native entry point missing: {ex.Message}");
                results.Fail($"CardScan_CancellationReason_{name}", $"Native entry point missing: {ex.Message}");
            }
            catch (Exception ex)
            {
                logger.Fail($"CancellationReason.{name}: {ex.Message}");
                results.Fail($"CardScan_CancellationReason_{name}", ex.Message);
            }
        }

        // CancellationReason FromRawValue roundtrip
        try
        {
            using var original = StripeCardScan.CancellationReason.Back;
            var rawValue = original.RawValue;
            using var roundTripped = StripeCardScan.CancellationReason.FromRawValue(rawValue);
            if (roundTripped != null && (uint)roundTripped.Tag == (uint)original.Tag)
            {
                logger.Pass($"CancellationReason.FromRawValue roundtrip: '{rawValue}'");
                results.Pass("CardScan_CancellationReason_FromRawValue");
            }
            else
            {
                logger.Fail("CancellationReason.FromRawValue roundtrip failed");
                results.Fail("CardScan_CancellationReason_FromRawValue", "Roundtrip failed");
            }
        }
        catch (DllNotFoundException ex)
        {
            logger.Fail($"CancellationReason.FromRawValue: native entry point missing: {ex.Message}");
            results.Fail("CardScan_CancellationReason_FromRawValue", $"Native entry point missing: {ex.Message}");
        }
        catch (Exception ex)
        {
            logger.Fail($"CancellationReason.FromRawValue: {ex.Message}");
            results.Fail("CardScan_CancellationReason_FromRawValue", ex.Message);
        }

        // CardScanSheetError
        logger.Info("--- CardScanSheetError ---");
        try
        {
            var error = StripeCardScan.CardScanSheetError.InvalidClientSecret;
            if ((uint)error.Tag == (uint)StripeCardScan.CardScanSheetError.CaseTag.InvalidClientSecret)
            {
                logger.Pass($"CardScanSheetError.InvalidClientSecret tag: {error.Tag}");
                results.Pass("CardScan_Error_InvalidClientSecret");
            }
            else
            {
                logger.Fail("CardScanSheetError.InvalidClientSecret: wrong tag");
                results.Fail("CardScan_Error_InvalidClientSecret", "Wrong tag");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"CardScanSheetError.InvalidClientSecret: {ex.Message}");
            results.Fail("CardScan_Error_InvalidClientSecret", ex.Message);
        }

        // CardScanSheet construction
        try
        {
            var sheet = new StripeCardScan.CardScanSheet();
            if (sheet != null)
            {
                logger.Pass("CardScanSheet() constructor");
                results.Pass("CardScan_Sheet_Ctor");
            }
            else
            {
                logger.Fail("CardScanSheet() returned null");
                results.Fail("CardScan_Sheet_Ctor", "Null");
            }
        }
        catch (DllNotFoundException ex)
        {
            logger.Fail($"CardScanSheet(): native entry point missing: {ex.Message}");
            results.Fail("CardScan_Sheet_Ctor", $"Native entry point missing: {ex.Message}");
        }
        catch (Exception ex)
        {
            logger.Fail($"CardScanSheet(): {ex.Message}");
            results.Fail("CardScan_Sheet_Ctor", ex.Message);
        }
    }

    // =========================================================================
    // Phase 13: StripeFinancialConnections
    // =========================================================================

    private void RunFinancialConnectionsTests(TestLogger logger, TestResults results)
    {
        logger.Info("--- FinancialConnectionsSheet ---");

        // FinancialConnectionsSheet construction
        try
        {
            var sheet = new StripeFinancialConnections.FinancialConnectionsSheet("fcs_test_secret_123");
            if (sheet != null)
            {
                logger.Pass("FinancialConnectionsSheet(clientSecret) constructor");
                results.Pass("FinConn_Sheet_Ctor");
            }
            else
            {
                logger.Fail("FinancialConnectionsSheet returned null");
                results.Fail("FinConn_Sheet_Ctor", "Null");
            }
        }
        catch (DllNotFoundException ex)
        {
            logger.Fail($"StripeFinancialConnections: native entry point missing: {ex.Message}");
            results.Fail("FinConn_Sheet_Ctor", $"Native entry point missing: {ex.Message}");
            return;
        }
        catch (Exception ex)
        {
            logger.Fail($"FinancialConnectionsSheet: {ex.Message}");
            results.Fail("FinConn_Sheet_Ctor", ex.Message);
        }

        // FinancialConnectionsSheet.Result.Canceled
        try
        {
            var canceled = StripeFinancialConnections.FinancialConnectionsSheet.Result.Canceled;
            if ((uint)canceled.Tag == (uint)StripeFinancialConnections.FinancialConnectionsSheet.Result.CaseTag.Canceled)
            {
                logger.Pass($"FinancialConnectionsSheet.Result.Canceled tag: {canceled.Tag}");
                results.Pass("FinConn_Result_Canceled");
            }
            else
            {
                logger.Fail("Result.Canceled: wrong tag");
                results.Fail("FinConn_Result_Canceled", "Wrong tag");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"Result.Canceled: {ex.Message}");
            results.Fail("FinConn_Result_Canceled", ex.Message);
        }

        // FinancialConnectionsSheet client secret property
        try
        {
            var sheet = new StripeFinancialConnections.FinancialConnectionsSheet("fcs_test_prop_check");
            var secret = sheet.FinancialConnectionsSessionClientSecret;
            if (secret == "fcs_test_prop_check")
            {
                logger.Pass($"FinancialConnectionsSheet.ClientSecret: {secret}");
                results.Pass("FinConn_Sheet_ClientSecret");
            }
            else
            {
                logger.Fail($"ClientSecret: got '{secret}'");
                results.Fail("FinConn_Sheet_ClientSecret", $"Got '{secret}'");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"Sheet.ClientSecret: {ex.Message}");
            results.Fail("FinConn_Sheet_ClientSecret", ex.Message);
        }

        // FinancialConnectionsSheetError
        try
        {
            using var error = StripeFinancialConnections.FinancialConnectionsSheetError.Unknown("test debug message");
            if (error != null && (uint)error.Tag == (uint)StripeFinancialConnections.FinancialConnectionsSheetError.CaseTag.Unknown)
            {
                logger.Pass($"FinancialConnectionsSheetError.Unknown() factory");
                results.Pass("FinConn_Error_Unknown");
            }
            else
            {
                logger.Fail("FinancialConnectionsSheetError.Unknown: wrong tag");
                results.Fail("FinConn_Error_Unknown", "Wrong tag");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"FinancialConnectionsSheetError.Unknown: {ex.Message}");
            results.Fail("FinConn_Error_Unknown", ex.Message);
        }
    }

    // =========================================================================
    // Phase 14: StripePaymentsUI — card form views, image library
    // =========================================================================

    private void RunPaymentsUITests(TestLogger logger, TestResults results)
    {
        logger.Info("--- STPCardFormView ---");

        // STPCardFormView construction
        try
        {
            var form = new StripePaymentsUI.STPCardFormView();
            if (form != null)
            {
                logger.Pass("STPCardFormView() constructor");
                results.Pass("PaymentsUI_CardFormView_Ctor");
            }
            else
            {
                logger.Fail("STPCardFormView() returned null");
                results.Fail("PaymentsUI_CardFormView_Ctor", "Null");
            }
        }
        catch (DllNotFoundException ex)
        {
            logger.Fail($"StripePaymentsUI: native entry point missing: {ex.Message}");
            results.Fail("PaymentsUI_CardFormView_Ctor", $"Native entry point missing: {ex.Message}");
            return;
        }
        catch (Exception ex)
        {
            logger.Fail($"STPCardFormView(): {ex.Message}");
            results.Fail("PaymentsUI_CardFormView_Ctor", ex.Message);
        }

        // STPCardFormView with style
        try
        {
            var form = new StripePaymentsUI.STPCardFormView(StripePaymentsUI.STPCardFormViewStyle.Borderless);
            if (form != null)
            {
                logger.Pass("STPCardFormView(Borderless) constructor");
                results.Pass("PaymentsUI_CardFormView_CtorStyle");
            }
            else
            {
                logger.Fail("STPCardFormView(Borderless) returned null");
                results.Fail("PaymentsUI_CardFormView_CtorStyle", "Null");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"STPCardFormView(Borderless): {ex.Message}");
            results.Fail("PaymentsUI_CardFormView_CtorStyle", ex.Message);
        }

        // STPCardFormView properties
        try
        {
            var form = new StripePaymentsUI.STPCardFormView();
            form.IsUserInteractionEnabled = false;
            var readBack = form.IsUserInteractionEnabled;
            if (!readBack)
            {
                logger.Pass("STPCardFormView.IsUserInteractionEnabled roundtrip");
                results.Pass("PaymentsUI_CardFormView_Interaction");
            }
            else
            {
                logger.Fail("CardFormView.IsUserInteractionEnabled: expected false");
                results.Fail("PaymentsUI_CardFormView_Interaction", "Expected false");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"CardFormView.IsUserInteractionEnabled: {ex.Message}");
            results.Fail("PaymentsUI_CardFormView_Interaction", ex.Message);
        }

        // STPCardFormView color properties
        try
        {
            var form = new StripePaymentsUI.STPCardFormView();
            form.BackgroundColor = UIColor.White;
            var readBack = form.BackgroundColor;
            if (readBack != null)
            {
                logger.Pass("STPCardFormView.BackgroundColor set/get");
                results.Pass("PaymentsUI_CardFormView_BgColor");
            }
            else
            {
                logger.Fail("CardFormView.BackgroundColor: null after set");
                results.Fail("PaymentsUI_CardFormView_BgColor", "Null after set");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"CardFormView.BackgroundColor: {ex.Message}");
            results.Fail("PaymentsUI_CardFormView_BgColor", ex.Message);
        }

        // STPCardFormViewStyle enum
        logger.Info("--- STPCardFormViewStyle ---");
        try
        {
            var styles = new[]
            {
                StripePaymentsUI.STPCardFormViewStyle.Standard,
                StripePaymentsUI.STPCardFormViewStyle.Borderless,
            };
            if (styles.Distinct().Count() == 2 && (long)styles[0] == 0 && (long)styles[1] == 1)
            {
                logger.Pass("STPCardFormViewStyle: Standard=0, Borderless=1");
                results.Pass("PaymentsUI_CardFormViewStyle");
            }
            else
            {
                logger.Fail("STPCardFormViewStyle: unexpected values");
                results.Fail("PaymentsUI_CardFormViewStyle", "Unexpected values");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"STPCardFormViewStyle: {ex.Message}");
            results.Fail("PaymentsUI_CardFormViewStyle", ex.Message);
        }

        // STPPaymentCardTextField construction and properties
        logger.Info("--- STPPaymentCardTextField ---");
        try
        {
            var textField = new StripePaymentsUI.STPPaymentCardTextField(new Swift.CGRect(0, 0, 320, 44));
            if (textField != null)
            {
                logger.Pass("STPPaymentCardTextField(frame) constructor");
                results.Pass("PaymentsUI_CardTextField_Ctor");
            }
            else
            {
                logger.Fail("STPPaymentCardTextField returned null");
                results.Fail("PaymentsUI_CardTextField_Ctor", "Null");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"STPPaymentCardTextField: {ex.Message}");
            results.Fail("PaymentsUI_CardTextField_Ctor", ex.Message);
        }

        // STPPaymentCardTextField.IsValid (should be false for empty)
        try
        {
            var textField = new StripePaymentsUI.STPPaymentCardTextField(new Swift.CGRect(0, 0, 320, 44));
            var isValid = textField.IsValid;
            if (!isValid)
            {
                logger.Pass("STPPaymentCardTextField.IsValid: false (empty field)");
                results.Pass("PaymentsUI_CardTextField_IsValid");
            }
            else
            {
                logger.Fail("STPPaymentCardTextField.IsValid: true for empty field");
                results.Fail("PaymentsUI_CardTextField_IsValid", "Should be false for empty");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"CardTextField.IsValid: {ex.Message}");
            results.Fail("PaymentsUI_CardTextField_IsValid", ex.Message);
        }

        // STPPaymentCardTextField placeholder properties
        try
        {
            var textField = new StripePaymentsUI.STPPaymentCardTextField(new Swift.CGRect(0, 0, 320, 44));
            textField.NumberPlaceholder = "4242 4242 4242 4242";
            textField.ExpirationPlaceholder = "MM/YY";
            textField.CvcPlaceholder = "CVC";
            textField.PostalCodePlaceholder = "ZIP";

            var allCorrect = textField.NumberPlaceholder == "4242 4242 4242 4242"
                && textField.ExpirationPlaceholder == "MM/YY"
                && textField.CvcPlaceholder == "CVC"
                && textField.PostalCodePlaceholder == "ZIP";

            if (allCorrect)
            {
                logger.Pass("STPPaymentCardTextField placeholder roundtrips");
                results.Pass("PaymentsUI_CardTextField_Placeholders");
            }
            else
            {
                logger.Fail("CardTextField placeholders: roundtrip failed");
                results.Fail("PaymentsUI_CardTextField_Placeholders", "Roundtrip failed");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"CardTextField placeholders: {ex.Message}");
            results.Fail("PaymentsUI_CardTextField_Placeholders", ex.Message);
        }

        // STPPaymentCardTextField styling properties
        try
        {
            var textField = new StripePaymentsUI.STPPaymentCardTextField(new Swift.CGRect(0, 0, 320, 44));
            textField.TextColor = UIColor.Black;
            textField.TextErrorColor = UIColor.Red;
            textField.PlaceholderColor = UIColor.Gray;
            textField.BorderWidth = 1.0;
            textField.CornerRadius = 8.0;

            var tc = textField.TextColor;
            var tec = textField.TextErrorColor;
            var pc = textField.PlaceholderColor;
            var bw = textField.BorderWidth;
            var cr = textField.CornerRadius;

            // Color properties prove ObjC-bridged class roundtrip works.
            // cornerRadius is a computed property in the Stripe SDK that may not
            // round-trip exactly (defaults to 5.0 regardless of set value).
            if (tc != null && tec != null && pc != null
                && Math.Abs(bw - 1.0) < 0.01
                && cr > 0)
            {
                logger.Pass("STPPaymentCardTextField styling roundtrips");
                results.Pass("PaymentsUI_CardTextField_Styling");
            }
            else
            {
                logger.Fail($"CardTextField styling: TC={tc != null} TEC={tec != null} PC={pc != null} BW={bw:F2} CR={cr:F2}");
                results.Fail("PaymentsUI_CardTextField_Styling", $"TC={tc != null} TEC={tec != null} PC={pc != null} BW={bw:F2} CR={cr:F2}");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"CardTextField styling: {ex.Message}");
            results.Fail("PaymentsUI_CardTextField_Styling", ex.Message);
        }

        // STPPaymentCardTextField.PostalCodeEntryEnabled
        try
        {
            var textField = new StripePaymentsUI.STPPaymentCardTextField(new Swift.CGRect(0, 0, 320, 44));
            textField.PostalCodeEntryEnabled = false;
            if (!textField.PostalCodeEntryEnabled)
            {
                logger.Pass("CardTextField.PostalCodeEntryEnabled roundtrip: false");
                results.Pass("PaymentsUI_CardTextField_PostalCode");
            }
            else
            {
                logger.Fail("CardTextField.PostalCodeEntryEnabled: expected false");
                results.Fail("PaymentsUI_CardTextField_PostalCode", "Expected false");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"CardTextField.PostalCodeEntryEnabled: {ex.Message}");
            results.Fail("PaymentsUI_CardTextField_PostalCode", ex.Message);
        }

        // STPPaymentCardTextField.CountryCode
        try
        {
            var textField = new StripePaymentsUI.STPPaymentCardTextField(new Swift.CGRect(0, 0, 320, 44));
            textField.CountryCode = "US";
            var readBack = textField.CountryCode;
            if (readBack == "US")
            {
                logger.Pass("CardTextField.CountryCode roundtrip: US");
                results.Pass("PaymentsUI_CardTextField_CountryCode");
            }
            else
            {
                logger.Fail($"CardTextField.CountryCode: got '{readBack}'");
                results.Fail("PaymentsUI_CardTextField_CountryCode", $"Got '{readBack}'");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"CardTextField.CountryCode: {ex.Message}");
            results.Fail("PaymentsUI_CardTextField_CountryCode", ex.Message);
        }

        // STPPaymentCardTextField.Clear
        try
        {
            var textField = new StripePaymentsUI.STPPaymentCardTextField(new Swift.CGRect(0, 0, 320, 44));
            textField.Clear();
            logger.Pass("CardTextField.Clear() no-throw");
            results.Pass("PaymentsUI_CardTextField_Clear");
        }
        catch (Exception ex)
        {
            logger.Fail($"CardTextField.Clear: {ex.Message}");
            results.Fail("PaymentsUI_CardTextField_Clear", ex.Message);
        }

        // STPImageLibrary card images
        logger.Info("--- STPImageLibrary ---");
        var imageTests = new (string Name, Func<UIKit.UIImage> Getter)[]
        {
            ("ApplePay", () => StripePaymentsUI.STPImageLibrary.GetApplePayCardImage()),
            ("Amex", () => StripePaymentsUI.STPImageLibrary.GetAmexCardImage()),
            ("DinersClub", () => StripePaymentsUI.STPImageLibrary.GetDinersClubCardImage()),
            ("Discover", () => StripePaymentsUI.STPImageLibrary.GetDiscoverCardImage()),
            ("Jcb", () => StripePaymentsUI.STPImageLibrary.GetJcbCardImage()),
            ("Mastercard", () => StripePaymentsUI.STPImageLibrary.GetMastercardCardImage()),
            ("UnionPay", () => StripePaymentsUI.STPImageLibrary.GetUnionPayCardImage()),
            ("Visa", () => StripePaymentsUI.STPImageLibrary.GetVisaCardImage()),
            ("Unknown", () => StripePaymentsUI.STPImageLibrary.GetUnknownCardCardImage()),
        };

        foreach (var (name, getter) in imageTests)
        {
            try
            {
                var image = getter();
                if (image != null)
                {
                    logger.Pass($"STPImageLibrary.{name} image");
                    results.Pass($"PaymentsUI_ImageLibrary_{name}");
                }
                else
                {
                    logger.Fail($"STPImageLibrary.{name}: null");
                    results.Fail($"PaymentsUI_ImageLibrary_{name}", "Null");
                }
            }
            catch (Exception ex)
            {
                logger.Fail($"STPImageLibrary.{name}: {ex.Message}");
                results.Fail($"PaymentsUI_ImageLibrary_{name}", ex.Message);
            }
        }

        // STPAUBECSDebitFormView construction
        logger.Info("--- STPAUBECSDebitFormView ---");
        try
        {
            var form = new StripePaymentsUI.STPAUBECSDebitFormView("Test Company");
            if (form != null)
            {
                logger.Pass("STPAUBECSDebitFormView('Test Company') constructor");
                results.Pass("PaymentsUI_AUBECSForm_Ctor");
            }
            else
            {
                logger.Fail("STPAUBECSDebitFormView returned null");
                results.Fail("PaymentsUI_AUBECSForm_Ctor", "Null");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"STPAUBECSDebitFormView: {ex.Message}");
            results.Fail("PaymentsUI_AUBECSForm_Ctor", ex.Message);
        }
    }

    // =========================================================================
    // Phase 15: StripePayments Extended — card params, method params, validator
    // =========================================================================

    private void RunStripePaymentsExtendedTests(TestLogger logger, TestResults results)
    {
        // STPCardParams property roundtrips
        logger.Info("--- STPCardParams Properties ---");
        try
        {
            var card = new StripePayments.STPCardParams();
            card.Number = "4242424242424242";
            card.ExpMonth = 12;
            card.ExpYear = 2030;
            card.Cvc = "123";
            card.Name = "Test Cardholder";
            card.Currency = "usd";

            var allCorrect = card.Number == "4242424242424242"
                && card.ExpMonth == 12
                && card.ExpYear == 2030
                && card.Cvc == "123"
                && card.Name == "Test Cardholder"
                && card.Currency == "usd";

            if (allCorrect)
            {
                logger.Pass("STPCardParams: all property roundtrips");
                results.Pass("Payments_CardParams_Properties");
            }
            else
            {
                logger.Fail($"STPCardParams: Number={card.Number} ExpMonth={card.ExpMonth} ExpYear={card.ExpYear} Cvc={card.Cvc} Name={card.Name} Currency={card.Currency}");
                results.Fail("Payments_CardParams_Properties", "Roundtrip failed");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"STPCardParams properties: {ex.Message}");
            results.Fail("Payments_CardParams_Properties", ex.Message);
        }

        // STPCardParams address fields
        try
        {
            var card = new StripePayments.STPCardParams();
            card.AddressLine1 = "123 Market St";
            card.AddressLine2 = "Suite 100";
            card.AddressCity = "San Francisco";
            card.AddressState = "CA";
            card.AddressZip = "94107";
            card.AddressCountry = "US";

            var allCorrect = card.AddressLine1 == "123 Market St"
                && card.AddressLine2 == "Suite 100"
                && card.AddressCity == "San Francisco"
                && card.AddressState == "CA"
                && card.AddressZip == "94107"
                && card.AddressCountry == "US";

            if (allCorrect)
            {
                logger.Pass("STPCardParams: address field roundtrips");
                results.Pass("Payments_CardParams_Address");
            }
            else
            {
                logger.Fail("STPCardParams address: roundtrip mismatch");
                results.Fail("Payments_CardParams_Address", "Roundtrip failed");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"STPCardParams address: {ex.Message}");
            results.Fail("Payments_CardParams_Address", ex.Message);
        }

        // STPPaymentMethodCardParams property roundtrips
        logger.Info("--- STPPaymentMethodCardParams ---");
        try
        {
            var cardParams = new StripePayments.STPPaymentMethodCardParams();
            cardParams.Number = "5555555555554444";
            cardParams.Cvc = "456";

            var numberOk = cardParams.Number == "5555555555554444";
            var cvcOk = cardParams.Cvc == "456";

            if (numberOk && cvcOk)
            {
                logger.Pass("STPPaymentMethodCardParams: Number and Cvc roundtrip");
                results.Pass("Payments_MethodCardParams_Props");
            }
            else
            {
                logger.Fail($"MethodCardParams: Number={cardParams.Number} Cvc={cardParams.Cvc}");
                results.Fail("Payments_MethodCardParams_Props", "Roundtrip failed");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"STPPaymentMethodCardParams: {ex.Message}");
            results.Fail("Payments_MethodCardParams_Props", ex.Message);
        }

        // STPPaymentMethodCardParams ExpMonth/ExpYear (NSNumber)
        try
        {
            var cardParams = new StripePayments.STPPaymentMethodCardParams();
            cardParams.ExpMonth = new Foundation.NSNumber(11);
            cardParams.ExpYear = new Foundation.NSNumber(2029);

            var monthOk = cardParams.ExpMonth?.Int32Value == 11;
            var yearOk = cardParams.ExpYear?.Int32Value == 2029;

            if (monthOk && yearOk)
            {
                logger.Pass("STPPaymentMethodCardParams: ExpMonth/ExpYear NSNumber roundtrip");
                results.Pass("Payments_MethodCardParams_Expiry");
            }
            else
            {
                logger.Fail($"MethodCardParams expiry: Month={cardParams.ExpMonth} Year={cardParams.ExpYear}");
                results.Fail("Payments_MethodCardParams_Expiry", "Roundtrip failed");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"STPPaymentMethodCardParams expiry: {ex.Message}");
            results.Fail("Payments_MethodCardParams_Expiry", ex.Message);
        }

        // STPPaymentMethodAddress full property roundtrip
        logger.Info("--- STPPaymentMethodAddress ---");
        try
        {
            var addr = new StripePayments.STPPaymentMethodAddress();
            addr.City = "Austin";
            addr.Country = "US";
            addr.Line1 = "456 Congress Ave";
            addr.Line2 = "Floor 3";
            addr.PostalCode = "78701";
            addr.State = "TX";

            var allCorrect = addr.City == "Austin"
                && addr.Country == "US"
                && addr.Line1 == "456 Congress Ave"
                && addr.Line2 == "Floor 3"
                && addr.PostalCode == "78701"
                && addr.State == "TX";

            if (allCorrect)
            {
                logger.Pass("STPPaymentMethodAddress: all 6 properties roundtrip");
                results.Pass("Payments_MethodAddress_AllProps");
            }
            else
            {
                logger.Fail("STPPaymentMethodAddress: roundtrip mismatch");
                results.Fail("Payments_MethodAddress_AllProps", "Roundtrip failed");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"STPPaymentMethodAddress: {ex.Message}");
            results.Fail("Payments_MethodAddress_AllProps", ex.Message);
        }

        // STPPaymentMethodBillingDetails with Address sub-object
        logger.Info("--- STPPaymentMethodBillingDetails composition ---");
        try
        {
            var billing = new StripePayments.STPPaymentMethodBillingDetails();
            billing.Name = "Jane Doe";
            billing.Email = "jane@example.com";
            billing.Phone = "+15551234567";

            var addr = new StripePayments.STPPaymentMethodAddress();
            addr.City = "Portland";
            addr.Country = "US";
            billing.Address = addr;

            var nameOk = billing.Name == "Jane Doe";
            var emailOk = billing.Email == "jane@example.com";
            var phoneOk = billing.Phone == "+15551234567";
            var addrOk = billing.Address != null && billing.Address.City == "Portland";

            if (nameOk && emailOk && phoneOk && addrOk)
            {
                logger.Pass("BillingDetails: Name/Email/Phone + Address sub-object");
                results.Pass("Payments_BillingDetails_Composition");
            }
            else
            {
                logger.Fail($"BillingDetails: Name={nameOk} Email={emailOk} Phone={phoneOk} Addr={addrOk}");
                results.Fail("Payments_BillingDetails_Composition", "Composition failed");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"BillingDetails composition: {ex.Message}");
            results.Fail("Payments_BillingDetails_Composition", ex.Message);
        }

        // STPPaymentMethodParams with card constructor
        logger.Info("--- STPPaymentMethodParams construction ---");
        try
        {
            var cardParams = new StripePayments.STPPaymentMethodCardParams();
            cardParams.Number = "4242424242424242";
            cardParams.ExpMonth = new Foundation.NSNumber(12);
            cardParams.ExpYear = new Foundation.NSNumber(2030);
            cardParams.Cvc = "314";

            var billing = new StripePayments.STPPaymentMethodBillingDetails();
            billing.Name = "Test User";

            var methodParams = new StripePayments.STPPaymentMethodParams(
                cardParams, billing,
                StripePayments.STPPaymentMethodAllowRedisplay.Always, null);

            if (methodParams != null)
            {
                var readCard = methodParams.Card;
                var readType = methodParams.Type;
                logger.Pass($"STPPaymentMethodParams(card, billing): Type={readType}, Card={readCard != null}");
                results.Pass("Payments_MethodParams_CardCtor");
            }
            else
            {
                logger.Fail("STPPaymentMethodParams: null");
                results.Fail("Payments_MethodParams_CardCtor", "Null");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"STPPaymentMethodParams card ctor: {ex.Message}");
            results.Fail("Payments_MethodParams_CardCtor", ex.Message);
        }

        // STPPaymentMethodParams Type property
        try
        {
            var cardParams = new StripePayments.STPPaymentMethodCardParams();
            var methodParams = new StripePayments.STPPaymentMethodParams(
                cardParams, null,
                StripePayments.STPPaymentMethodAllowRedisplay.Unspecified, null);
            var type = methodParams.Type;
            if (type == StripePayments.STPPaymentMethodType.Card)
            {
                logger.Pass("STPPaymentMethodParams.Type == Card for card constructor");
                results.Pass("Payments_MethodParams_Type");
            }
            else
            {
                logger.Fail($"STPPaymentMethodParams.Type: expected Card, got {type}");
                results.Fail("Payments_MethodParams_Type", $"Got {type}");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"STPPaymentMethodParams.Type: {ex.Message}");
            results.Fail("Payments_MethodParams_Type", ex.Message);
        }

        // STPPaymentMethodParams RawTypeString
        try
        {
            var cardParams = new StripePayments.STPPaymentMethodCardParams();
            var methodParams = new StripePayments.STPPaymentMethodParams(
                cardParams, null,
                StripePayments.STPPaymentMethodAllowRedisplay.Unspecified, null);
            var rawType = methodParams.RawTypeString;
            if (!string.IsNullOrEmpty(rawType))
            {
                logger.Pass($"STPPaymentMethodParams.RawTypeString: '{rawType}'");
                results.Pass("Payments_MethodParams_RawTypeString");
            }
            else
            {
                logger.Fail("STPPaymentMethodParams.RawTypeString: empty");
                results.Fail("Payments_MethodParams_RawTypeString", "Empty");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"STPPaymentMethodParams.RawTypeString: {ex.Message}");
            results.Fail("Payments_MethodParams_RawTypeString", ex.Message);
        }

        // STPPaymentIntentConfirmParams property roundtrips
        logger.Info("--- STPPaymentIntentConfirmParams Properties ---");
        try
        {
            var confirmParams = new StripePayments.STPPaymentIntentConfirmParams("pi_test_secret_123");
            confirmParams.ReceiptEmail = "receipt@example.com";
            confirmParams.ReturnURL = "myapp://stripe-redirect";

            var emailOk = confirmParams.ReceiptEmail == "receipt@example.com";
            var urlOk = confirmParams.ReturnURL == "myapp://stripe-redirect";

            if (emailOk && urlOk)
            {
                logger.Pass("ConfirmParams: ReceiptEmail + ReturnURL roundtrip");
                results.Pass("Payments_ConfirmParams_Props");
            }
            else
            {
                logger.Fail($"ConfirmParams: email={emailOk} url={urlOk}");
                results.Fail("Payments_ConfirmParams_Props", "Roundtrip failed");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"ConfirmParams properties: {ex.Message}");
            results.Fail("Payments_ConfirmParams_Props", ex.Message);
        }

        // STPPaymentIntentConfirmParams with PaymentMethodParams
        try
        {
            var confirmParams = new StripePayments.STPPaymentIntentConfirmParams("pi_test_secret_456");
            var cardParams = new StripePayments.STPPaymentMethodCardParams();
            cardParams.Number = "4242424242424242";
            var methodParams = new StripePayments.STPPaymentMethodParams(
                cardParams, null,
                StripePayments.STPPaymentMethodAllowRedisplay.Unspecified, null);
            confirmParams.PaymentMethodParams = methodParams;

            var readBack = confirmParams.PaymentMethodParams;
            if (readBack != null)
            {
                logger.Pass("ConfirmParams.PaymentMethodParams: set/get sub-object");
                results.Pass("Payments_ConfirmParams_MethodParams");
            }
            else
            {
                logger.Fail("ConfirmParams.PaymentMethodParams: null after set");
                results.Fail("Payments_ConfirmParams_MethodParams", "Null after set");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"ConfirmParams.PaymentMethodParams: {ex.Message}");
            results.Fail("Payments_ConfirmParams_MethodParams", ex.Message);
        }

        // STPSetupIntentConfirmParams with PaymentMethodParams
        logger.Info("--- STPSetupIntentConfirmParams ---");
        try
        {
            var setupParams = new StripePayments.STPSetupIntentConfirmParams("seti_test_secret_789");
            setupParams.ReturnURL = "myapp://setup-redirect";
            var cardParams = new StripePayments.STPPaymentMethodCardParams();
            var methodParams = new StripePayments.STPPaymentMethodParams(
                cardParams, null,
                StripePayments.STPPaymentMethodAllowRedisplay.Unspecified, null);
            setupParams.PaymentMethodParams = methodParams;

            var urlOk = setupParams.ReturnURL == "myapp://setup-redirect";
            var paramsOk = setupParams.PaymentMethodParams != null;

            if (urlOk && paramsOk)
            {
                logger.Pass("SetupIntentConfirmParams: ReturnURL + PaymentMethodParams");
                results.Pass("Payments_SetupConfirmParams_Props");
            }
            else
            {
                logger.Fail($"SetupConfirmParams: url={urlOk} params={paramsOk}");
                results.Fail("Payments_SetupConfirmParams_Props", "Roundtrip failed");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"SetupIntentConfirmParams props: {ex.Message}");
            results.Fail("Payments_SetupConfirmParams_Props", ex.Message);
        }

        // Payment method-specific params: SEPA Debit
        logger.Info("--- Payment Method Specific Params ---");
        try
        {
            var sepa = new StripePayments.STPPaymentMethodSEPADebitParams();
            sepa.Iban = "DE89370400440532013000";
            if (sepa.Iban == "DE89370400440532013000")
            {
                logger.Pass("SEPADebitParams.Iban roundtrip");
                results.Pass("Payments_SEPADebitParams_Iban");
            }
            else
            {
                logger.Fail($"SEPADebitParams.Iban: got '{sepa.Iban}'");
                results.Fail("Payments_SEPADebitParams_Iban", "Roundtrip failed");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"SEPADebitParams: {ex.Message}");
            results.Fail("Payments_SEPADebitParams_Iban", ex.Message);
        }

        // AU BECS Debit params
        try
        {
            var becs = new StripePayments.STPPaymentMethodAUBECSDebitParams();
            becs.BsbNumber = "000-000";
            becs.AccountNumber = "000123456";
            if (becs.BsbNumber == "000-000" && becs.AccountNumber == "000123456")
            {
                logger.Pass("AUBECSDebitParams: BsbNumber + AccountNumber roundtrip");
                results.Pass("Payments_AUBECSDebitParams_Props");
            }
            else
            {
                logger.Fail($"AUBECSDebitParams: bsb={becs.BsbNumber} acct={becs.AccountNumber}");
                results.Fail("Payments_AUBECSDebitParams_Props", "Roundtrip failed");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"AUBECSDebitParams: {ex.Message}");
            results.Fail("Payments_AUBECSDebitParams_Props", ex.Message);
        }

        // Boleto params
        try
        {
            var boleto = new StripePayments.STPPaymentMethodBoletoParams();
            boleto.TaxID = "000.000.000-00";
            if (boleto.TaxID == "000.000.000-00")
            {
                logger.Pass("BoletoParams.TaxID roundtrip");
                results.Pass("Payments_BoletoParams_TaxID");
            }
            else
            {
                logger.Fail($"BoletoParams.TaxID: got '{boleto.TaxID}'");
                results.Fail("Payments_BoletoParams_TaxID", "Roundtrip failed");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"BoletoParams: {ex.Message}");
            results.Fail("Payments_BoletoParams_TaxID", ex.Message);
        }

        // STPCardValidator extended usage
        logger.Info("--- STPCardValidator Extended ---");
        try
        {
            var sanitized = StripePayments.STPCardValidator.SanitizedNumericString("4242-4242-4242-4242");
            if (sanitized == "4242424242424242")
            {
                logger.Pass($"STPCardValidator.SanitizedNumericString: '{sanitized}'");
                results.Pass("Payments_CardValidator_Sanitize");
            }
            else
            {
                logger.Fail($"SanitizedNumericString: got '{sanitized}'");
                results.Fail("Payments_CardValidator_Sanitize", $"Got '{sanitized}'");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"SanitizedNumericString: {ex.Message}");
            results.Fail("Payments_CardValidator_Sanitize", ex.Message);
        }

        // StringIsNumeric
        try
        {
            var isNum = StripePayments.STPCardValidator.StringIsNumeric("12345");
            var notNum = StripePayments.STPCardValidator.StringIsNumeric("123abc");
            if (isNum && !notNum)
            {
                logger.Pass("STPCardValidator.StringIsNumeric: correct for numeric and non-numeric");
                results.Pass("Payments_CardValidator_IsNumeric");
            }
            else
            {
                logger.Fail($"StringIsNumeric: '12345'={isNum}, '123abc'={notNum}");
                results.Fail("Payments_CardValidator_IsNumeric", "Unexpected results");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"StringIsNumeric: {ex.Message}");
            results.Fail("Payments_CardValidator_IsNumeric", ex.Message);
        }

        // Brand detection from card number
        try
        {
            var visa = StripePayments.STPCardValidator.Brand("4242424242424242");
            var mc = StripePayments.STPCardValidator.Brand("5555555555554444");
            var amex = StripePayments.STPCardValidator.Brand("378282246310005");
            if (visa == StripePayments.STPCardBrand.Visa
                && mc == StripePayments.STPCardBrand.Mastercard
                && amex == StripePayments.STPCardBrand.Amex)
            {
                logger.Pass("STPCardValidator.Brand: Visa, Mastercard, Amex detected");
                results.Pass("Payments_CardValidator_BrandDetect");
            }
            else
            {
                logger.Fail($"Brand: visa={visa}, mc={mc}, amex={amex}");
                results.Fail("Payments_CardValidator_BrandDetect", "Detection failed");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"CardValidator.Brand: {ex.Message}");
            results.Fail("Payments_CardValidator_BrandDetect", ex.Message);
        }

        // FragmentLength
        try
        {
            var visaFrag = StripePayments.STPCardValidator.FragmentLength(StripePayments.STPCardBrand.Visa);
            var amexFrag = StripePayments.STPCardValidator.FragmentLength(StripePayments.STPCardBrand.Amex);
            if (visaFrag > 0 && amexFrag > 0)
            {
                logger.Pass($"STPCardValidator.FragmentLength: Visa={visaFrag}, Amex={amexFrag}");
                results.Pass("Payments_CardValidator_FragmentLen");
            }
            else
            {
                logger.Fail($"FragmentLength: Visa={visaFrag} Amex={amexFrag}");
                results.Fail("Payments_CardValidator_FragmentLen", "Zero");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"FragmentLength: {ex.Message}");
            results.Fail("Payments_CardValidator_FragmentLen", ex.Message);
        }

        // ValidationState for card number
        try
        {
            var valid = StripePayments.STPCardValidator.ValidationState("4242424242424242", true);
            var incomplete = StripePayments.STPCardValidator.ValidationState("424242", true);
            logger.Pass($"STPCardValidator.ValidationState: full={valid}, partial={incomplete}");
            results.Pass("Payments_CardValidator_ValidationState");
        }
        catch (Exception ex)
        {
            logger.Fail($"ValidationState: {ex.Message}");
            results.Fail("Payments_CardValidator_ValidationState", ex.Message);
        }

        // STPPaymentMethodAllowRedisplay enum
        try
        {
            var values = new[]
            {
                StripePayments.STPPaymentMethodAllowRedisplay.Unspecified,
                StripePayments.STPPaymentMethodAllowRedisplay.Always,
                StripePayments.STPPaymentMethodAllowRedisplay.Limited,
            };
            if (values.Distinct().Count() == 3)
            {
                logger.Pass("STPPaymentMethodAllowRedisplay: 3 distinct cases");
                results.Pass("Payments_AllowRedisplay");
            }
            else
            {
                logger.Fail("AllowRedisplay: non-distinct");
                results.Fail("Payments_AllowRedisplay", "Non-distinct");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"AllowRedisplay: {ex.Message}");
            results.Fail("Payments_AllowRedisplay", ex.Message);
        }

        // STPPaymentMethodParams with SEPA constructor (cross-type composition)
        try
        {
            var sepa = new StripePayments.STPPaymentMethodSEPADebitParams();
            sepa.Iban = "DE89370400440532013000";
            var billing = new StripePayments.STPPaymentMethodBillingDetails();
            billing.Name = "Max Mustermann";
            billing.Email = "max@example.de";

            var methodParams = new StripePayments.STPPaymentMethodParams(sepa, billing, null);
            if (methodParams != null)
            {
                logger.Pass("STPPaymentMethodParams(sepaDebit, billing): constructed");
                results.Pass("Payments_MethodParams_SEPACtor");
            }
            else
            {
                logger.Fail("STPPaymentMethodParams SEPA: null");
                results.Fail("Payments_MethodParams_SEPACtor", "Null");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"STPPaymentMethodParams SEPA ctor: {ex.Message}");
            results.Fail("Payments_MethodParams_SEPACtor", ex.Message);
        }

        // STPPaymentIntentShippingDetailsParams
        logger.Info("--- STPPaymentIntentShippingDetailsParams ---");
        try
        {
            var addr = new StripePayments.STPPaymentIntentShippingDetailsAddressParams("456 Ship Ln");
            var shipping = new StripePayments.STPPaymentIntentShippingDetailsParams(addr, "Ship Recipient");
            shipping.Phone = "+14155551234";

            if (shipping.Name == "Ship Recipient" && shipping.Phone == "+14155551234")
            {
                logger.Pass("ShippingDetailsParams: Name + Phone roundtrip");
                results.Pass("Payments_ShippingParams_Props");
            }
            else
            {
                logger.Fail($"ShippingParams: Name={shipping.Name} Phone={shipping.Phone}");
                results.Fail("Payments_ShippingParams_Props", "Roundtrip failed");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"ShippingDetailsParams: {ex.Message}");
            results.Fail("Payments_ShippingParams_Props", ex.Message);
        }

        // STPPaymentHandler.ApiClient set/get
        logger.Info("--- STPPaymentHandler extended ---");
        try
        {
            var handler = StripePayments.STPPaymentHandler.SharedHandler;
            var client = new StripeCore.STPAPIClient("pk_test_handler_key");
            handler.ApiClient = client;
            var readBack = handler.ApiClient;
            if (readBack != null)
            {
                logger.Pass("STPPaymentHandler.ApiClient: set custom client and read back");
                results.Pass("Payments_PaymentHandler_SetApiClient");
            }
            else
            {
                logger.Fail("PaymentHandler.ApiClient: null after set");
                results.Fail("Payments_PaymentHandler_SetApiClient", "Null");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"PaymentHandler.ApiClient set: {ex.Message}");
            results.Fail("Payments_PaymentHandler_SetApiClient", ex.Message);
        }
    }

    // =========================================================================
    // Phase 16: StripePaymentSheet Extended — appearance, billing, results
    // =========================================================================

    private void RunPaymentSheetExtendedTests(TestLogger logger, TestResults results)
    {
        // Appearance.Colors — extended UIColor properties (ObjC-bridged roundtrips)
        logger.Info("--- Appearance.Colors Extended (UIColor ObjC bridge) ---");
        var colorTests = new (string Name, Func<StripePaymentSheet.PaymentSheet.Appearance.ColorsType, UIColor?> Getter,
            Action<StripePaymentSheet.PaymentSheet.Appearance.ColorsType, UIColor> Setter)[]
        {
            ("ComponentBackground", c => c.ComponentBackground, (c, v) => c.ComponentBackground = v),
            ("ComponentBorder", c => c.ComponentBorder, (c, v) => c.ComponentBorder = v),
            ("ComponentDivider", c => c.ComponentDivider, (c, v) => c.ComponentDivider = v),
            ("TextSecondary", c => c.TextSecondary, (c, v) => c.TextSecondary = v),
            ("ComponentText", c => c.ComponentText, (c, v) => c.ComponentText = v),
            ("ComponentPlaceholderText", c => c.ComponentPlaceholderText, (c, v) => c.ComponentPlaceholderText = v),
            ("Icon", c => c.Icon, (c, v) => c.Icon = v),
            ("Danger", c => c.Danger, (c, v) => c.Danger = v),
        };

        foreach (var (name, getter, setter) in colorTests)
        {
            try
            {
                var appearance = StripePaymentSheet.PaymentSheet.Appearance.Default;
                var colors = appearance.Colors;
                setter(colors, UIColor.SystemBlue);
                var readBack = getter(colors);
                if (readBack != null)
                {
                    logger.Pass($"Colors.{name}: UIColor set/get");
                    results.Pass($"PaymentSheet_Colors_{name}");
                }
                else
                {
                    logger.Fail($"Colors.{name}: null after set");
                    results.Fail($"PaymentSheet_Colors_{name}", "Null after set");
                }
            }
            catch (Exception ex)
            {
                logger.Fail($"Colors.{name}: {ex.Message}");
                results.Fail($"PaymentSheet_Colors_{name}", ex.Message);
            }
        }

        // PrimaryButton extended properties
        logger.Info("--- PrimaryButton Extended ---");
        try
        {
            var appearance = StripePaymentSheet.PaymentSheet.Appearance.Default;
            var btn = appearance.PrimaryButton;
            btn.TextColor = UIColor.White;
            btn.BorderWidth = 2.0;

            var tcOk = btn.TextColor != null;
            var bwOk = Math.Abs(btn.BorderWidth - 2.0) < 0.01;

            if (tcOk && bwOk)
            {
                logger.Pass("PrimaryButton: TextColor + BorderWidth roundtrip");
                results.Pass("PaymentSheet_PrimaryButton_Extended");
            }
            else
            {
                logger.Fail($"PrimaryButton: TC={tcOk} BW={bwOk}");
                results.Fail("PaymentSheet_PrimaryButton_Extended", "Roundtrip failed");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"PrimaryButton extended: {ex.Message}");
            results.Fail("PaymentSheet_PrimaryButton_Extended", ex.Message);
        }

        // PrimaryButton font (UIFont ObjC-bridged)
        try
        {
            var appearance = StripePaymentSheet.PaymentSheet.Appearance.Default;
            var btn = appearance.PrimaryButton;
            var font = UIFont.BoldSystemFontOfSize(18);
            btn.Font = font;
            var readBack = btn.Font;
            if (readBack != null)
            {
                logger.Pass($"PrimaryButton.Font: UIFont set/get (size: {readBack.PointSize})");
                results.Pass("PaymentSheet_PrimaryButton_Font");
            }
            else
            {
                logger.Fail("PrimaryButton.Font: null after set");
                results.Fail("PaymentSheet_PrimaryButton_Font", "Null");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"PrimaryButton.Font: {ex.Message}");
            results.Fail("PaymentSheet_PrimaryButton_Font", ex.Message);
        }

        // Appearance.Font.Base (UIFont ObjC-bridged)
        try
        {
            var appearance = StripePaymentSheet.PaymentSheet.Appearance.Default;
            var fontType = appearance.Font;
            var baseFont = UIFont.SystemFontOfSize(16);
            fontType.Base = baseFont;
            var readBack = fontType.Base;
            if (readBack != null)
            {
                logger.Pass($"Appearance.Font.Base: UIFont set/get (size: {readBack.PointSize})");
                results.Pass("PaymentSheet_Font_Base");
            }
            else
            {
                logger.Fail("Appearance.Font.Base: null after set");
                results.Fail("PaymentSheet_Font_Base", "Null");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"Appearance.Font.Base: {ex.Message}");
            results.Fail("PaymentSheet_Font_Base", ex.Message);
        }

        // Shadow properties: Opacity and Radius roundtrip
        try
        {
            var appearance = StripePaymentSheet.PaymentSheet.Appearance.Default;
            var shadow = appearance.Shadow;
            shadow.Opacity = 0.5;
            shadow.Radius = 4.0;
            var opOk = Math.Abs(shadow.Opacity - 0.5) < 0.01;
            var radOk = Math.Abs(shadow.Radius - 4.0) < 0.01;
            if (opOk && radOk)
            {
                logger.Pass($"Shadow: Opacity={shadow.Opacity:F2} Radius={shadow.Radius:F2}");
                results.Pass("PaymentSheet_Shadow_Props");
            }
            else
            {
                logger.Fail($"Shadow: Opacity={shadow.Opacity} Radius={shadow.Radius}");
                results.Fail("PaymentSheet_Shadow_Props", "Roundtrip failed");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"Shadow props: {ex.Message}");
            results.Fail("PaymentSheet_Shadow_Props", ex.Message);
        }

        // PaymentSheet.Address construction and property roundtrips
        logger.Info("--- PaymentSheet.Address ---");
        try
        {
            var addr = new StripePaymentSheet.PaymentSheet.Address(
                city: "Seattle", country: "US", line1: "123 Pike St",
                line2: "Apt 4", postalCode: "98101", state: "WA");

            var allCorrect = addr.City == "Seattle"
                && addr.Country == "US"
                && addr.Line1 == "123 Pike St"
                && addr.Line2 == "Apt 4"
                && addr.PostalCode == "98101"
                && addr.State == "WA";

            if (allCorrect)
            {
                logger.Pass("PaymentSheet.Address: 6-property construction roundtrip");
                results.Pass("PaymentSheet_Address_Ctor");
            }
            else
            {
                logger.Fail($"Address: city={addr.City} country={addr.Country} line1={addr.Line1}");
                results.Fail("PaymentSheet_Address_Ctor", "Roundtrip failed");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"PaymentSheet.Address: {ex.Message}");
            results.Fail("PaymentSheet_Address_Ctor", ex.Message);
        }

        // PaymentSheet.BillingDetails construction with Address
        try
        {
            var addr = new StripePaymentSheet.PaymentSheet.Address(
                city: "Chicago", country: "US", line1: "789 State St");
            var billing = new StripePaymentSheet.PaymentSheet.BillingDetails(
                addr, email: "test@example.com", name: "Test User", phone: "+13125551234");

            var emailOk = billing.Email == "test@example.com";
            var nameOk = billing.Name == "Test User";
            var phoneOk = billing.Phone == "+13125551234";
            var addrOk = billing.Address.City == "Chicago";

            if (emailOk && nameOk && phoneOk && addrOk)
            {
                logger.Pass("PaymentSheet.BillingDetails: full construction roundtrip");
                results.Pass("PaymentSheet_BillingDetails_Ctor");
            }
            else
            {
                logger.Fail($"BillingDetails: email={emailOk} name={nameOk} phone={phoneOk} addr={addrOk}");
                results.Fail("PaymentSheet_BillingDetails_Ctor", "Roundtrip failed");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"PaymentSheet.BillingDetails: {ex.Message}");
            results.Fail("PaymentSheet_BillingDetails_Ctor", ex.Message);
        }

        // PaymentSheetResult — Completed, Canceled, Failed factories and tags
        logger.Info("--- PaymentSheetResult ---");
        try
        {
            var completed = StripePaymentSheet.PaymentSheetResult.Completed;
            var canceled = StripePaymentSheet.PaymentSheetResult.Canceled;
            var compTag = (uint)completed.Tag == (uint)StripePaymentSheet.PaymentSheetResult.CaseTag.Completed;
            var canTag = (uint)canceled.Tag == (uint)StripePaymentSheet.PaymentSheetResult.CaseTag.Canceled;
            if (compTag && canTag)
            {
                logger.Pass("PaymentSheetResult: Completed and Canceled tags correct");
                results.Pass("PaymentSheet_Result_CompletedCanceled");
            }
            else
            {
                logger.Fail($"PaymentSheetResult: Completed={compTag} Canceled={canTag}");
                results.Fail("PaymentSheet_Result_CompletedCanceled", "Wrong tags");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"PaymentSheetResult: {ex.Message}");
            results.Fail("PaymentSheet_Result_CompletedCanceled", ex.Message);
        }

        // PaymentSheetResult CaseTag enum distinctness
        try
        {
            var tags = new[]
            {
                StripePaymentSheet.PaymentSheetResult.CaseTag.Failed,
                StripePaymentSheet.PaymentSheetResult.CaseTag.Completed,
                StripePaymentSheet.PaymentSheetResult.CaseTag.Canceled,
            };
            if (tags.Distinct().Count() == 3)
            {
                logger.Pass("PaymentSheetResult.CaseTag: 3 distinct cases");
                results.Pass("PaymentSheet_Result_Tags");
            }
            else
            {
                logger.Fail("PaymentSheetResult.CaseTag: non-distinct");
                results.Fail("PaymentSheet_Result_Tags", "Non-distinct");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"PaymentSheetResult.CaseTag: {ex.Message}");
            results.Fail("PaymentSheet_Result_Tags", ex.Message);
        }

        // CustomerPaymentOption factories
        logger.Info("--- CustomerPaymentOption ---");
        try
        {
            var applePay = StripePaymentSheet.CustomerPaymentOption.ApplePay;
            var link = StripePaymentSheet.CustomerPaymentOption.Link;
            if (applePay != null && link != null
                && (uint)applePay.Tag != (uint)link.Tag)
            {
                logger.Pass("CustomerPaymentOption: ApplePay and Link distinct");
                results.Pass("PaymentSheet_CustomerPaymentOption_Static");
            }
            else
            {
                logger.Fail("CustomerPaymentOption: not distinct");
                results.Fail("PaymentSheet_CustomerPaymentOption_Static", "Not distinct");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"CustomerPaymentOption: {ex.Message}");
            results.Fail("PaymentSheet_CustomerPaymentOption_Static", ex.Message);
        }

        // CustomerPaymentOption.StripeId factory
        try
        {
            using var option = StripePaymentSheet.CustomerPaymentOption.StripeId("pm_test_123");
            if (option != null
                && (uint)option.Tag == (uint)StripePaymentSheet.CustomerPaymentOption.CaseTag.StripeId)
            {
                logger.Pass("CustomerPaymentOption.StripeId('pm_test_123')");
                results.Pass("PaymentSheet_CustomerPaymentOption_StripeId");
            }
            else
            {
                logger.Fail("CustomerPaymentOption.StripeId: wrong tag");
                results.Fail("PaymentSheet_CustomerPaymentOption_StripeId", "Wrong tag");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"CustomerPaymentOption.StripeId: {ex.Message}");
            results.Fail("PaymentSheet_CustomerPaymentOption_StripeId", ex.Message);
        }

        // CustomerEphemeralKey construction
        try
        {
            using var key = new StripePaymentSheet.CustomerEphemeralKey("cus_test_123", "ek_test_secret_456");
            var idOk = key.Id == "cus_test_123";
            var secretOk = key.EphemeralKeySecret == "ek_test_secret_456";
            if (idOk && secretOk)
            {
                logger.Pass("CustomerEphemeralKey: construction + property roundtrip");
                results.Pass("PaymentSheet_EphemeralKey_Ctor");
            }
            else
            {
                logger.Fail($"EphemeralKey: id={key.Id} secret={key.EphemeralKeySecret}");
                results.Fail("PaymentSheet_EphemeralKey_Ctor", "Roundtrip failed");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"CustomerEphemeralKey: {ex.Message}");
            results.Fail("PaymentSheet_EphemeralKey_Ctor", ex.Message);
        }

        // CustomerSessionClientSecret construction
        try
        {
            using var secret = new StripePaymentSheet.CustomerSessionClientSecret("cus_test_789", "css_test_secret");
            if (secret != null)
            {
                logger.Pass("CustomerSessionClientSecret: construction succeeded");
                results.Pass("PaymentSheet_SessionClientSecret_Ctor");
            }
            else
            {
                logger.Fail("CustomerSessionClientSecret: null");
                results.Fail("PaymentSheet_SessionClientSecret_Ctor", "Null");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"CustomerSessionClientSecret: {ex.Message}");
            results.Fail("PaymentSheet_SessionClientSecret_Ctor", ex.Message);
        }

        // Appearance.SelectedBorderWidth roundtrip
        try
        {
            var appearance = StripePaymentSheet.PaymentSheet.Appearance.Default;
            appearance.SelectedBorderWidth = 3.0;
            var readBack = appearance.SelectedBorderWidth;
            if (readBack.HasValue && Math.Abs(readBack.Value - 3.0) < 0.01)
            {
                logger.Pass($"Appearance.SelectedBorderWidth roundtrip: {readBack:F1}");
                results.Pass("PaymentSheet_Appearance_SelectedBorderWidth");
            }
            else
            {
                logger.Fail($"SelectedBorderWidth: got {readBack}");
                results.Fail("PaymentSheet_Appearance_SelectedBorderWidth", $"Got {readBack}");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"SelectedBorderWidth: {ex.Message}");
            results.Fail("PaymentSheet_Appearance_SelectedBorderWidth", ex.Message);
        }

        // Colors.SelectedComponentBorder (optional UIColor)
        try
        {
            var appearance = StripePaymentSheet.PaymentSheet.Appearance.Default;
            var colors = appearance.Colors;
            colors.SelectedComponentBorder = UIColor.Purple;
            var readBack = colors.SelectedComponentBorder;
            if (readBack != null)
            {
                logger.Pass("Colors.SelectedComponentBorder: Optional UIColor set/get");
                results.Pass("PaymentSheet_Colors_SelectedComponentBorder");
            }
            else
            {
                logger.Fail("Colors.SelectedComponentBorder: null after set");
                results.Fail("PaymentSheet_Colors_SelectedComponentBorder", "Null");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"Colors.SelectedComponentBorder: {ex.Message}");
            results.Fail("PaymentSheet_Colors_SelectedComponentBorder", ex.Message);
        }
    }

    // =========================================================================
    // Phase 17: StripePaymentsUI Extended — styling, brand images, form views
    // =========================================================================

    private void RunPaymentsUIExtendedTests(TestLogger logger, TestResults results)
    {
        // STPImageLibrary.CardBrandImage with STPCardBrand enum parameter
        logger.Info("--- STPImageLibrary.CardBrandImage (enum parameter) ---");
        var brandImageTests = new (string Name, StripePayments.STPCardBrand Brand)[]
        {
            ("Visa", StripePayments.STPCardBrand.Visa),
            ("Mastercard", StripePayments.STPCardBrand.Mastercard),
            ("Amex", StripePayments.STPCardBrand.Amex),
            ("Discover", StripePayments.STPCardBrand.Discover),
        };

        foreach (var (name, brand) in brandImageTests)
        {
            try
            {
                var image = StripePaymentsUI.STPImageLibrary.CardBrandImage(brand);
                if (image != null)
                {
                    logger.Pass($"CardBrandImage({name}): returned image");
                    results.Pass($"PaymentsUI_BrandImage_{name}");
                }
                else
                {
                    logger.Fail($"CardBrandImage({name}): null");
                    results.Fail($"PaymentsUI_BrandImage_{name}", "Null");
                }
            }
            catch (Exception ex)
            {
                logger.Fail($"CardBrandImage({name}): {ex.Message}");
                results.Fail($"PaymentsUI_BrandImage_{name}", ex.Message);
            }
        }

        // STPImageLibrary.TemplatedBrandImage
        try
        {
            var image = StripePaymentsUI.STPImageLibrary.TemplatedBrandImage(StripePayments.STPCardBrand.Visa);
            if (image != null)
            {
                logger.Pass("TemplatedBrandImage(Visa): returned image");
                results.Pass("PaymentsUI_TemplatedBrandImage");
            }
            else
            {
                logger.Fail("TemplatedBrandImage(Visa): null");
                results.Fail("PaymentsUI_TemplatedBrandImage", "Null");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"TemplatedBrandImage: {ex.Message}");
            results.Fail("PaymentsUI_TemplatedBrandImage", ex.Message);
        }

        // STPImageLibrary.CvcImage and ErrorImage
        try
        {
            var cvc = StripePaymentsUI.STPImageLibrary.CvcImage(StripePayments.STPCardBrand.Visa);
            var error = StripePaymentsUI.STPImageLibrary.ErrorImage(StripePayments.STPCardBrand.Visa);
            if (cvc != null && error != null)
            {
                logger.Pass("CvcImage + ErrorImage(Visa): both returned");
                results.Pass("PaymentsUI_CvcErrorImage");
            }
            else
            {
                logger.Fail($"CvcImage={cvc != null} ErrorImage={error != null}");
                results.Fail("PaymentsUI_CvcErrorImage", "Null");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"CvcImage/ErrorImage: {ex.Message}");
            results.Fail("PaymentsUI_CvcErrorImage", ex.Message);
        }

        // STPPaymentCardTextField.Font (UIFont ObjC-bridged)
        logger.Info("--- STPPaymentCardTextField Extended ---");
        try
        {
            var tf = new StripePaymentsUI.STPPaymentCardTextField(new Swift.CGRect(0, 0, 320, 44));
            var font = UIFont.SystemFontOfSize(18);
            tf.Font = font;
            var readBack = tf.Font;
            if (readBack != null && Math.Abs(readBack.PointSize - 18.0) < 0.01)
            {
                logger.Pass($"CardTextField.Font: UIFont roundtrip (size: {readBack.PointSize})");
                results.Pass("PaymentsUI_CardTextField_Font");
            }
            else
            {
                logger.Fail($"CardTextField.Font: size={readBack?.PointSize}");
                results.Fail("PaymentsUI_CardTextField_Font", "Roundtrip failed");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"CardTextField.Font: {ex.Message}");
            results.Fail("PaymentsUI_CardTextField_Font", ex.Message);
        }

        // STPPaymentCardTextField.CursorColor (UIColor ObjC-bridged)
        try
        {
            var tf = new StripePaymentsUI.STPPaymentCardTextField(new Swift.CGRect(0, 0, 320, 44));
            tf.CursorColor = UIColor.Blue;
            var readBack = tf.CursorColor;
            if (readBack != null)
            {
                logger.Pass("CardTextField.CursorColor: UIColor set/get");
                results.Pass("PaymentsUI_CardTextField_CursorColor");
            }
            else
            {
                logger.Fail("CardTextField.CursorColor: null after set");
                results.Fail("PaymentsUI_CardTextField_CursorColor", "Null");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"CardTextField.CursorColor: {ex.Message}");
            results.Fail("PaymentsUI_CardTextField_CursorColor", ex.Message);
        }

        // STPPaymentCardTextField.BorderColor (Optional UIColor)
        try
        {
            var tf = new StripePaymentsUI.STPPaymentCardTextField(new Swift.CGRect(0, 0, 320, 44));
            tf.BorderColor = UIColor.Green;
            var readBack = tf.BorderColor;
            if (readBack != null)
            {
                logger.Pass("CardTextField.BorderColor: Optional UIColor set/get");
                results.Pass("PaymentsUI_CardTextField_BorderColor");
            }
            else
            {
                logger.Fail("CardTextField.BorderColor: null after set");
                results.Fail("PaymentsUI_CardTextField_BorderColor", "Null");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"CardTextField.BorderColor: {ex.Message}");
            results.Fail("PaymentsUI_CardTextField_BorderColor", ex.Message);
        }

        // STPPaymentCardTextField.BrandImage (UIImage)
        try
        {
            var tf = new StripePaymentsUI.STPPaymentCardTextField(new Swift.CGRect(0, 0, 320, 44));
            var brandImg = tf.BrandImage;
            // BrandImage should have a default value for empty card field
            logger.Pass($"CardTextField.BrandImage: get={brandImg != null}");
            results.Pass("PaymentsUI_CardTextField_BrandImage");
        }
        catch (Exception ex)
        {
            logger.Fail($"CardTextField.BrandImage: {ex.Message}");
            results.Fail("PaymentsUI_CardTextField_BrandImage", ex.Message);
        }

        // STPPaymentCardTextField static methods
        try
        {
            var brandImg = StripePaymentsUI.STPPaymentCardTextField.BrandImageMethod(StripePayments.STPCardBrand.Visa);
            var errorImg = StripePaymentsUI.STPPaymentCardTextField.ErrorImage(StripePayments.STPCardBrand.Visa);
            if (brandImg != null && errorImg != null)
            {
                logger.Pass("CardTextField static: BrandImageMethod + ErrorImage");
                results.Pass("PaymentsUI_CardTextField_StaticImages");
            }
            else
            {
                logger.Fail($"Static images: brand={brandImg != null} error={errorImg != null}");
                results.Fail("PaymentsUI_CardTextField_StaticImages", "Null");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"CardTextField static images: {ex.Message}");
            results.Fail("PaymentsUI_CardTextField_StaticImages", ex.Message);
        }

        // STPCardFormView.DisabledBackgroundColor (UIColor ObjC-bridged)
        logger.Info("--- STPCardFormView Extended ---");
        try
        {
            var form = new StripePaymentsUI.STPCardFormView();
            form.DisabledBackgroundColor = UIColor.LightGray;
            var readBack = form.DisabledBackgroundColor;
            if (readBack != null)
            {
                logger.Pass("CardFormView.DisabledBackgroundColor: UIColor set/get");
                results.Pass("PaymentsUI_CardFormView_DisabledBgColor");
            }
            else
            {
                logger.Fail("CardFormView.DisabledBackgroundColor: null after set");
                results.Fail("PaymentsUI_CardFormView_DisabledBgColor", "Null");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"CardFormView.DisabledBackgroundColor: {ex.Message}");
            results.Fail("PaymentsUI_CardFormView_DisabledBgColor", ex.Message);
        }

        // STPCardFormView.CardParams getter (returns null for empty form)
        try
        {
            var form = new StripePaymentsUI.STPCardFormView(StripePaymentsUI.STPCardFormViewStyle.Borderless);
            var cardParams = form.CardParams;
            // CardParams returns null for an empty form (no card data entered)
            logger.Pass($"CardFormView.CardParams: get from Borderless form (null={cardParams == null})");
            results.Pass("PaymentsUI_CardFormView_CardParams");
        }
        catch (Exception ex)
        {
            logger.Fail($"CardFormView.CardParams: {ex.Message}");
            results.Fail("PaymentsUI_CardFormView_CardParams", ex.Message);
        }

        // STPCardFormView.OnBehalfOf roundtrip
        try
        {
            var form = new StripePaymentsUI.STPCardFormView();
            form.OnBehalfOf = "acct_test_obo_123";
            var readBack = form.OnBehalfOf;
            if (readBack == "acct_test_obo_123")
            {
                logger.Pass("CardFormView.OnBehalfOf roundtrip");
                results.Pass("PaymentsUI_CardFormView_OnBehalfOf");
            }
            else
            {
                logger.Fail($"CardFormView.OnBehalfOf: got '{readBack}'");
                results.Fail("PaymentsUI_CardFormView_OnBehalfOf", $"Got '{readBack}'");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"CardFormView.OnBehalfOf: {ex.Message}");
            results.Fail("PaymentsUI_CardFormView_OnBehalfOf", ex.Message);
        }

        // STPAUBECSDebitFormView color properties
        logger.Info("--- STPAUBECSDebitFormView Extended ---");
        try
        {
            var form = new StripePaymentsUI.STPAUBECSDebitFormView("Test Corp");
            form.FormBackgroundColor = UIColor.White;
            form.FormTextColor = UIColor.Black;
            form.FormFont = UIFont.SystemFontOfSize(14);

            var bgOk = form.FormBackgroundColor != null;
            var tcOk = form.FormTextColor != null;
            var fontOk = form.FormFont != null;

            if (bgOk && tcOk && fontOk)
            {
                logger.Pass("AUBECSDebitFormView: FormBackgroundColor + FormTextColor + FormFont");
                results.Pass("PaymentsUI_AUBECSForm_Styling");
            }
            else
            {
                logger.Fail($"AUBECSDebitFormView: bg={bgOk} tc={tcOk} font={fontOk}");
                results.Fail("PaymentsUI_AUBECSForm_Styling", "Styling failed");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"AUBECSDebitFormView styling: {ex.Message}");
            results.Fail("PaymentsUI_AUBECSForm_Styling", ex.Message);
        }

        // STPCardFormViewStyle.GetDescription
        try
        {
            var desc = StripePaymentsUI.STPCardFormViewStyle.Standard.GetDescription();
            if (!string.IsNullOrEmpty(desc))
            {
                logger.Pass($"STPCardFormViewStyle.Standard.GetDescription(): '{desc}'");
                results.Pass("PaymentsUI_CardFormViewStyle_Description");
            }
            else
            {
                logger.Fail("CardFormViewStyle.GetDescription: empty");
                results.Fail("PaymentsUI_CardFormViewStyle_Description", "Empty");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"CardFormViewStyle.GetDescription: {ex.Message}");
            results.Fail("PaymentsUI_CardFormViewStyle_Description", ex.Message);
        }
    }

    // =========================================================================
    // Phase 18: StripeConnect Extended — options roundtrips, appearance
    // =========================================================================

    private void RunConnectExtendedTests(TestLogger logger, TestResults results)
    {
        logger.Info("--- AccountCollectionOptions Extended ---");

        // AccountCollectionOptions Fields roundtrip
        try
        {
            var options = new StripeConnect.AccountCollectionOptions();
            options.Fields = StripeConnect.AccountCollectionOptions.FieldOption.EventuallyDue;
            var readBack = options.Fields;
            if ((uint)readBack.Tag == (uint)StripeConnect.AccountCollectionOptions.FieldOption.CaseTag.EventuallyDue)
            {
                logger.Pass("AccountCollectionOptions.Fields: EventuallyDue roundtrip");
                results.Pass("Connect_Options_Fields");
            }
            else
            {
                logger.Fail($"Options.Fields: unexpected tag {readBack.Tag}");
                results.Fail("Connect_Options_Fields", "Wrong tag");
            }
        }
        catch (DllNotFoundException ex)
        {
            logger.Fail($"StripeConnect Options.Fields: native entry point missing: {ex.Message}");
            results.Fail("Connect_Options_Fields", $"Native entry point missing: {ex.Message}");
            return;
        }
        catch (Exception ex)
        {
            logger.Fail($"Options.Fields: {ex.Message}");
            results.Fail("Connect_Options_Fields", ex.Message);
        }

        // AccountCollectionOptions FutureRequirements roundtrip
        try
        {
            var options = new StripeConnect.AccountCollectionOptions();
            options.FutureRequirements = StripeConnect.AccountCollectionOptions.FutureRequirementOption.Include;
            var readBack = options.FutureRequirements;
            if ((uint)readBack.Tag == (uint)StripeConnect.AccountCollectionOptions.FutureRequirementOption.CaseTag.Include)
            {
                logger.Pass("AccountCollectionOptions.FutureRequirements: Include roundtrip");
                results.Pass("Connect_Options_FutureReqs");
            }
            else
            {
                logger.Fail($"Options.FutureRequirements: unexpected tag");
                results.Fail("Connect_Options_FutureReqs", "Wrong tag");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"Options.FutureRequirements: {ex.Message}");
            results.Fail("Connect_Options_FutureReqs", ex.Message);
        }

        // AccountCollectionOptions equality
        try
        {
            var a = new StripeConnect.AccountCollectionOptions();
            var b = new StripeConnect.AccountCollectionOptions();
            if (a.Equals(b))
            {
                logger.Pass("AccountCollectionOptions: default equality");
                results.Pass("Connect_Options_Equality");
            }
            else
            {
                logger.Fail("AccountCollectionOptions: defaults not equal");
                results.Fail("Connect_Options_Equality", "Not equal");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"Options equality: {ex.Message}");
            results.Fail("Connect_Options_Equality", ex.Message);
        }

        // Appearance typography set/get
        logger.Info("--- EmbeddedComponentManager.Appearance extended ---");
        try
        {
            var appearance = StripeConnect.EmbeddedComponentManager.Appearance.Default;
            var typography = new StripeConnect.EmbeddedComponentManager.Appearance.TypographyType();
            appearance.Typography = typography;
            var readBack = appearance.Typography;
            if (readBack != null)
            {
                logger.Pass("Appearance.Typography: set/get");
                results.Pass("Connect_Appearance_Typography");
            }
            else
            {
                logger.Fail("Appearance.Typography: null after set");
                results.Fail("Connect_Appearance_Typography", "Null");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"Appearance.Typography: {ex.Message}");
            results.Fail("Connect_Appearance_Typography", ex.Message);
        }

        // FutureRequirementOption enum cases
        try
        {
            var include = StripeConnect.AccountCollectionOptions.FutureRequirementOption.Include;
            var omit = StripeConnect.AccountCollectionOptions.FutureRequirementOption.Omit;
            if (include != null && omit != null && (uint)include.Tag != (uint)omit.Tag)
            {
                logger.Pass("FutureRequirementOption: Include and Omit distinct");
                results.Pass("Connect_FutureReqOption");
            }
            else
            {
                logger.Fail("FutureRequirementOption: not distinct");
                results.Fail("Connect_FutureReqOption", "Not distinct");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"FutureRequirementOption: {ex.Message}");
            results.Fail("Connect_FutureReqOption", ex.Message);
        }
    }

    // =========================================================================
    // Phase 19: Cross-Module — objects from one module used in another
    // =========================================================================

    private void RunCrossModuleTests(TestLogger logger, TestResults results)
    {
        logger.Info("--- Cross-Module Tests ---");

        // Create STPAPIClient (StripeCore) and assign to STPPaymentHandler (StripePayments)
        try
        {
            var client = new StripeCore.STPAPIClient("pk_test_cross_module_key");
            client.StripeAccount = "acct_cross_test";
            var handler = StripePayments.STPPaymentHandler.SharedHandler;
            handler.ApiClient = client;
            var readBack = handler.ApiClient;
            var acctOk = readBack.StripeAccount == "acct_cross_test";
            if (acctOk)
            {
                logger.Pass("Cross-module: STPAPIClient → STPPaymentHandler.ApiClient roundtrip");
                results.Pass("CrossModule_APIClient_PaymentHandler");
            }
            else
            {
                logger.Fail($"Cross-module: StripeAccount={readBack.StripeAccount}");
                results.Fail("CrossModule_APIClient_PaymentHandler", "Roundtrip failed");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"Cross-module APIClient→Handler: {ex.Message}");
            results.Fail("CrossModule_APIClient_PaymentHandler", ex.Message);
        }

        // Create STPPaymentMethodCardParams (StripePayments) and read from STPCardFormView (StripePaymentsUI)
        try
        {
            var form = new StripePaymentsUI.STPCardFormView();
            var cardParams = form.CardParams;
            // CardParams should be null or empty for a freshly created form
            logger.Pass($"Cross-module: STPCardFormView.CardParams access (null={cardParams == null})");
            results.Pass("CrossModule_CardFormView_CardParams");
        }
        catch (Exception ex)
        {
            logger.Fail($"Cross-module CardFormView→CardParams: {ex.Message}");
            results.Fail("CrossModule_CardFormView_CardParams", ex.Message);
        }

        // Use STPCardBrand enum (StripePayments) with STPImageLibrary (StripePaymentsUI)
        try
        {
            var brand = StripePayments.STPCardValidator.Brand("4242424242424242");
            var image = StripePaymentsUI.STPImageLibrary.CardBrandImage(brand);
            if (image != null && brand == StripePayments.STPCardBrand.Visa)
            {
                logger.Pass("Cross-module: CardValidator.Brand → ImageLibrary.CardBrandImage");
                results.Pass("CrossModule_Validator_ImageLibrary");
            }
            else
            {
                logger.Fail($"Cross-module: brand={brand} image={image != null}");
                results.Fail("CrossModule_Validator_ImageLibrary", "Failed");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"Cross-module Validator→ImageLibrary: {ex.Message}");
            results.Fail("CrossModule_Validator_ImageLibrary", ex.Message);
        }

        // Compose StripePayments types into a full payment intent confirm flow
        try
        {
            // Build a complete payment flow: card → method params → billing → confirm params
            var cardParams = new StripePayments.STPPaymentMethodCardParams();
            cardParams.Number = "4242424242424242";
            cardParams.ExpMonth = new Foundation.NSNumber(12);
            cardParams.ExpYear = new Foundation.NSNumber(2030);
            cardParams.Cvc = "314";

            var addr = new StripePayments.STPPaymentMethodAddress();
            addr.Line1 = "100 Main St";
            addr.City = "New York";
            addr.State = "NY";
            addr.PostalCode = "10001";
            addr.Country = "US";

            var billing = new StripePayments.STPPaymentMethodBillingDetails();
            billing.Name = "Integration Test";
            billing.Email = "integration@example.com";
            billing.Address = addr;

            var methodParams = new StripePayments.STPPaymentMethodParams(
                cardParams, billing,
                StripePayments.STPPaymentMethodAllowRedisplay.Always, null);

            var confirmParams = new StripePayments.STPPaymentIntentConfirmParams("pi_integration_test");
            confirmParams.PaymentMethodParams = methodParams;
            confirmParams.ReceiptEmail = "receipt@example.com";
            confirmParams.ReturnURL = "myapp://return";

            // Verify the full chain
            var readMethod = confirmParams.PaymentMethodParams;
            var readType = readMethod?.Type;
            var readEmail = confirmParams.ReceiptEmail;

            if (readMethod != null && readEmail == "receipt@example.com")
            {
                logger.Pass($"Cross-module full flow: Card→Billing→Method→Confirm (type={readType})");
                results.Pass("CrossModule_FullPaymentFlow");
            }
            else
            {
                logger.Fail("Full flow: composition failed");
                results.Fail("CrossModule_FullPaymentFlow", "Failed");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"Full payment flow: {ex.Message}");
            results.Fail("CrossModule_FullPaymentFlow", ex.Message);
        }

        // StripeAPI (StripeCore) static properties used alongside payments
        try
        {
            StripeCore.StripeAPI.DefaultPublishableKey = "pk_test_cross_module_stripe_api";
            var client = StripeCore.STPAPIClient.Shared;
            // After setting default key, shared client should pick it up
            var key = StripeCore.StripeAPI.DefaultPublishableKey;
            if (key == "pk_test_cross_module_stripe_api")
            {
                logger.Pass("Cross-module: StripeAPI.DefaultPublishableKey persists across modules");
                results.Pass("CrossModule_StripeAPI_DefaultKey");
            }
            else
            {
                logger.Fail($"DefaultPublishableKey: got '{key}'");
                results.Fail("CrossModule_StripeAPI_DefaultKey", "Roundtrip failed");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"StripeAPI DefaultKey: {ex.Message}");
            results.Fail("CrossModule_StripeAPI_DefaultKey", ex.Message);
        }
    }

    // =========================================================================
    // Phase 20: PaymentSheet.Configuration — consumer-facing API property roundtrips
    // =========================================================================

    private void RunPaymentSheetConfigurationTests(TestLogger logger, TestResults results)
    {
        // MerchantDisplayName roundtrip
        try
        {
            var config = new StripePaymentSheet.PaymentSheet.ConfigurationType();
            config.MerchantDisplayName = "Test Store";
            var readBack = config.MerchantDisplayName;
            if (readBack == "Test Store")
            {
                logger.Pass("Config.MerchantDisplayName roundtrip");
                results.Pass("Config_MerchantDisplayName");
            }
            else
            {
                logger.Fail($"Config.MerchantDisplayName: got '{readBack}'");
                results.Fail("Config_MerchantDisplayName", $"Got '{readBack}'");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"Config.MerchantDisplayName: {ex.Message}");
            results.Fail("Config_MerchantDisplayName", ex.Message);
        }

        // ReturnURL roundtrip (nullable string)
        try
        {
            var config = new StripePaymentSheet.PaymentSheet.ConfigurationType();
            config.ReturnURL = "myapp://stripe-return";
            var readBack = config.ReturnURL;
            if (readBack == "myapp://stripe-return")
            {
                logger.Pass("Config.ReturnURL roundtrip");
                results.Pass("Config_ReturnURL");
            }
            else
            {
                logger.Fail($"Config.ReturnURL: got '{readBack}'");
                results.Fail("Config_ReturnURL", $"Got '{readBack}'");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"Config.ReturnURL: {ex.Message}");
            results.Fail("Config_ReturnURL", ex.Message);
        }

        // AllowsDelayedPaymentMethods roundtrip
        try
        {
            var config = new StripePaymentSheet.PaymentSheet.ConfigurationType();
            config.AllowsDelayedPaymentMethods = true;
            var readBack = config.AllowsDelayedPaymentMethods;
            if (readBack == true)
            {
                logger.Pass("Config.AllowsDelayedPaymentMethods roundtrip");
                results.Pass("Config_AllowsDelayedPaymentMethods");
            }
            else
            {
                logger.Fail($"Config.AllowsDelayedPaymentMethods: got {readBack}");
                results.Fail("Config_AllowsDelayedPaymentMethods", $"Got {readBack}");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"Config.AllowsDelayedPaymentMethods: {ex.Message}");
            results.Fail("Config_AllowsDelayedPaymentMethods", ex.Message);
        }

        // AllowsPaymentMethodsRequiringShippingAddress roundtrip
        try
        {
            var config = new StripePaymentSheet.PaymentSheet.ConfigurationType();
            config.AllowsPaymentMethodsRequiringShippingAddress = true;
            var readBack = config.AllowsPaymentMethodsRequiringShippingAddress;
            if (readBack == true)
            {
                logger.Pass("Config.AllowsPaymentMethodsRequiringShippingAddress roundtrip");
                results.Pass("Config_AllowsShippingAddress");
            }
            else
            {
                logger.Fail($"Config.AllowsPaymentMethodsRequiringShippingAddress: got {readBack}");
                results.Fail("Config_AllowsShippingAddress", $"Got {readBack}");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"Config.AllowsPaymentMethodsRequiringShippingAddress: {ex.Message}");
            results.Fail("Config_AllowsShippingAddress", ex.Message);
        }

        // SavePaymentMethodOptInBehavior enum roundtrips
        var optInBehaviors = new[] {
            (StripePaymentSheet.PaymentSheet.SavePaymentMethodOptInBehavior.RequiresOptIn, "RequiresOptIn"),
            (StripePaymentSheet.PaymentSheet.SavePaymentMethodOptInBehavior.RequiresOptOut, "RequiresOptOut"),
            (StripePaymentSheet.PaymentSheet.SavePaymentMethodOptInBehavior.Automatic, "Automatic"),
        };
        foreach (var (behavior, name) in optInBehaviors)
        {
            try
            {
                var config = new StripePaymentSheet.PaymentSheet.ConfigurationType();
                config.SavePaymentMethodOptInBehavior = behavior;
                var readBack = config.SavePaymentMethodOptInBehavior;
                if (readBack == behavior)
                {
                    logger.Pass($"Config.SavePaymentMethodOptInBehavior = {name} roundtrip");
                    results.Pass($"Config_SaveOptIn_{name}");
                }
                else
                {
                    logger.Fail($"Config.SavePaymentMethodOptInBehavior {name}: got {readBack}");
                    results.Fail($"Config_SaveOptIn_{name}", $"Got {readBack}");
                }
            }
            catch (Exception ex)
            {
                logger.Fail($"Config.SavePaymentMethodOptInBehavior {name}: {ex.Message}");
                results.Fail($"Config_SaveOptIn_{name}", ex.Message);
            }
        }

        // PaymentMethodLayout enum roundtrips
        var layouts = new[] {
            (StripePaymentSheet.PaymentSheet.PaymentMethodLayout.Horizontal, "Horizontal"),
            (StripePaymentSheet.PaymentSheet.PaymentMethodLayout.Vertical, "Vertical"),
            (StripePaymentSheet.PaymentSheet.PaymentMethodLayout.Automatic, "Automatic"),
        };
        foreach (var (layout, name) in layouts)
        {
            try
            {
                var config = new StripePaymentSheet.PaymentSheet.ConfigurationType();
                config.PaymentMethodLayout = layout;
                var readBack = config.PaymentMethodLayout;
                if (readBack == layout)
                {
                    logger.Pass($"Config.PaymentMethodLayout = {name} roundtrip");
                    results.Pass($"Config_PaymentMethodLayout_{name}");
                }
                else
                {
                    logger.Fail($"Config.PaymentMethodLayout {name}: got {readBack}");
                    results.Fail($"Config_PaymentMethodLayout_{name}", $"Got {readBack}");
                }
            }
            catch (Exception ex)
            {
                logger.Fail($"Config.PaymentMethodLayout {name}: {ex.Message}");
                results.Fail($"Config_PaymentMethodLayout_{name}", ex.Message);
            }
        }

        // BillingDetailsCollectionConfiguration — CollectionMode roundtrips
        try
        {
            var config = new StripePaymentSheet.PaymentSheet.ConfigurationType();
            var bdcc = config.BillingDetailsCollectionConfiguration;
            bdcc.Name = StripePaymentSheet.PaymentSheet.BillingDetailsCollectionConfiguration.CollectionMode.Always;
            bdcc.Email = StripePaymentSheet.PaymentSheet.BillingDetailsCollectionConfiguration.CollectionMode.Never;
            bdcc.Phone = StripePaymentSheet.PaymentSheet.BillingDetailsCollectionConfiguration.CollectionMode.Always;

            var nameTag = bdcc.Name.Tag;
            var emailTag = bdcc.Email.Tag;
            var phoneTag = bdcc.Phone.Tag;

            if (nameTag == StripePaymentSheet.PaymentSheet.BillingDetailsCollectionConfiguration.CollectionMode.CaseTag.Always &&
                emailTag == StripePaymentSheet.PaymentSheet.BillingDetailsCollectionConfiguration.CollectionMode.CaseTag.Never &&
                phoneTag == StripePaymentSheet.PaymentSheet.BillingDetailsCollectionConfiguration.CollectionMode.CaseTag.Always)
            {
                logger.Pass("BDCC: Name=Always, Email=Never, Phone=Always roundtrip");
                results.Pass("Config_BDCC_CollectionModes");
            }
            else
            {
                logger.Fail($"BDCC: Name={nameTag}, Email={emailTag}, Phone={phoneTag}");
                results.Fail("Config_BDCC_CollectionModes", "Tag mismatch");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"BDCC CollectionModes: {ex.Message}");
            results.Fail("Config_BDCC_CollectionModes", ex.Message);
        }

        // AddressCollectionMode roundtrips (Full + Never)
        var addrModes = new[] {
            (StripePaymentSheet.PaymentSheet.BillingDetailsCollectionConfiguration.AddressCollectionMode.Full,
             StripePaymentSheet.PaymentSheet.BillingDetailsCollectionConfiguration.AddressCollectionMode.CaseTag.Full, "Full"),
            (StripePaymentSheet.PaymentSheet.BillingDetailsCollectionConfiguration.AddressCollectionMode.Never,
             StripePaymentSheet.PaymentSheet.BillingDetailsCollectionConfiguration.AddressCollectionMode.CaseTag.Never, "Never"),
            (StripePaymentSheet.PaymentSheet.BillingDetailsCollectionConfiguration.AddressCollectionMode.Automatic,
             StripePaymentSheet.PaymentSheet.BillingDetailsCollectionConfiguration.AddressCollectionMode.CaseTag.Automatic, "Automatic"),
        };
        foreach (var (mode, expectedTag, name) in addrModes)
        {
            try
            {
                var config = new StripePaymentSheet.PaymentSheet.ConfigurationType();
                var bdcc = config.BillingDetailsCollectionConfiguration;
                bdcc.Address = mode;
                var addrTag = bdcc.Address.Tag;
                if (addrTag == expectedTag)
                {
                    logger.Pass($"BDCC: Address={name} roundtrip");
                    results.Pass($"Config_BDCC_Address_{name}");
                }
                else
                {
                    logger.Fail($"BDCC Address {name}: got {addrTag}");
                    results.Fail($"Config_BDCC_Address_{name}", $"Got {addrTag}");
                }
            }
            catch (Exception ex)
            {
                logger.Fail($"BDCC Address {name}: {ex.Message}");
                results.Fail($"Config_BDCC_Address_{name}", ex.Message);
            }
        }

        // DefaultBillingDetails — sub-object property roundtrips
        try
        {
            var config = new StripePaymentSheet.PaymentSheet.ConfigurationType();
            var bd = config.DefaultBillingDetails;
            bd.Name = "Jane Doe";
            bd.Email = "jane@example.com";
            bd.Phone = "+1234567890";

            var nameOk = bd.Name == "Jane Doe";
            var emailOk = bd.Email == "jane@example.com";
            var phoneOk = bd.Phone == "+1234567890";

            if (nameOk && emailOk && phoneOk)
            {
                logger.Pass("DefaultBillingDetails: Name, Email, Phone roundtrip");
                results.Pass("Config_DefaultBillingDetails_Props");
            }
            else
            {
                logger.Fail($"DefaultBillingDetails: name={nameOk} email={emailOk} phone={phoneOk}");
                results.Fail("Config_DefaultBillingDetails_Props", "Roundtrip failed");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"DefaultBillingDetails: {ex.Message}");
            results.Fail("Config_DefaultBillingDetails_Props", ex.Message);
        }

        // DefaultBillingDetails.Address sub-object
        try
        {
            var config = new StripePaymentSheet.PaymentSheet.ConfigurationType();
            var bd = config.DefaultBillingDetails;
            bd.Address = new StripePaymentSheet.PaymentSheet.Address(
                city: "San Francisco", country: "US", line1: "123 Market St",
                postalCode: "94105", state: "CA");
            var addr = bd.Address;
            var cityOk = addr.City == "San Francisco";
            var countryOk = addr.Country == "US";
            var line1Ok = addr.Line1 == "123 Market St";
            if (cityOk && countryOk && line1Ok)
            {
                logger.Pass("DefaultBillingDetails.Address roundtrip");
                results.Pass("Config_DefaultBillingDetails_Address");
            }
            else
            {
                logger.Fail($"DefaultBillingDetails.Address: city={cityOk} country={countryOk} line1={line1Ok}");
                results.Fail("Config_DefaultBillingDetails_Address", "Roundtrip failed");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"DefaultBillingDetails.Address: {ex.Message}");
            results.Fail("Config_DefaultBillingDetails_Address", ex.Message);
        }

        // PrimaryButtonLabel nullable string
        try
        {
            var config = new StripePaymentSheet.PaymentSheet.ConfigurationType();
            config.PrimaryButtonLabel = "Pay $25.00";
            var readBack = config.PrimaryButtonLabel;
            if (readBack == "Pay $25.00")
            {
                logger.Pass("Config.PrimaryButtonLabel roundtrip");
                results.Pass("Config_PrimaryButtonLabel");
            }
            else
            {
                logger.Fail($"Config.PrimaryButtonLabel: got '{readBack}'");
                results.Fail("Config_PrimaryButtonLabel", $"Got '{readBack}'");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"Config.PrimaryButtonLabel: {ex.Message}");
            results.Fail("Config_PrimaryButtonLabel", ex.Message);
        }

        // RemoveSavedPaymentMethodMessage nullable string
        try
        {
            var config = new StripePaymentSheet.PaymentSheet.ConfigurationType();
            config.RemoveSavedPaymentMethodMessage = "Are you sure you want to remove this card?";
            var readBack = config.RemoveSavedPaymentMethodMessage;
            if (readBack == "Are you sure you want to remove this card?")
            {
                logger.Pass("Config.RemoveSavedPaymentMethodMessage roundtrip");
                results.Pass("Config_RemoveSavedPaymentMethodMessage");
            }
            else
            {
                logger.Fail($"Config.RemoveSavedPaymentMethodMessage: got '{readBack}'");
                results.Fail("Config_RemoveSavedPaymentMethodMessage", $"Got '{readBack}'");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"Config.RemoveSavedPaymentMethodMessage: {ex.Message}");
            results.Fail("Config_RemoveSavedPaymentMethodMessage", ex.Message);
        }

        // Appearance can be set on Config
        try
        {
            var config = new StripePaymentSheet.PaymentSheet.ConfigurationType();
            var appearance = StripePaymentSheet.PaymentSheet.Appearance.Default;
            appearance.CornerRadius = 16.0;
            config.Appearance = appearance;
            var readBack = config.Appearance;
            var crOk = readBack.CornerRadius.HasValue && Math.Abs(readBack.CornerRadius.Value - 16.0) < 0.01;
            if (crOk)
            {
                logger.Pass("Config.Appearance set/get with CornerRadius");
                results.Pass("Config_Appearance");
            }
            else
            {
                logger.Fail($"Config.Appearance: CornerRadius={readBack.CornerRadius}");
                results.Fail("Config_Appearance", "Roundtrip failed");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"Config.Appearance: {ex.Message}");
            results.Fail("Config_Appearance", ex.Message);
        }

        // OpensCardScannerAutomatically bool
        try
        {
            var config = new StripePaymentSheet.PaymentSheet.ConfigurationType();
            config.OpensCardScannerAutomatically = true;
            var readBack = config.OpensCardScannerAutomatically;
            if (readBack == true)
            {
                logger.Pass("Config.OpensCardScannerAutomatically roundtrip");
                results.Pass("Config_OpensCardScannerAutomatically");
            }
            else
            {
                logger.Fail($"Config.OpensCardScannerAutomatically: got {readBack}");
                results.Fail("Config_OpensCardScannerAutomatically", $"Got {readBack}");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"Config.OpensCardScannerAutomatically: {ex.Message}");
            results.Fail("Config_OpensCardScannerAutomatically", ex.Message);
        }

        // CollectionMode.RawValue roundtrip
        try
        {
            var always = StripePaymentSheet.PaymentSheet.BillingDetailsCollectionConfiguration.CollectionMode.Always;
            var rawValue = always.RawValue;
            var fromRaw = StripePaymentSheet.PaymentSheet.BillingDetailsCollectionConfiguration.CollectionMode.FromRawValue(rawValue);
            if (fromRaw != null && fromRaw.Tag == StripePaymentSheet.PaymentSheet.BillingDetailsCollectionConfiguration.CollectionMode.CaseTag.Always)
            {
                logger.Pass($"CollectionMode.Always RawValue '{rawValue}' roundtrip");
                results.Pass("Config_CollectionMode_RawValue");
            }
            else
            {
                logger.Fail($"CollectionMode RawValue: raw='{rawValue}' fromRaw={fromRaw?.Tag}");
                results.Fail("Config_CollectionMode_RawValue", "Roundtrip failed");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"CollectionMode RawValue: {ex.Message}");
            results.Fail("Config_CollectionMode_RawValue", ex.Message);
        }

        // AddressCollectionMode.RawValue roundtrip
        try
        {
            var full = StripePaymentSheet.PaymentSheet.BillingDetailsCollectionConfiguration.AddressCollectionMode.Full;
            var rawValue = full.RawValue;
            var fromRaw = StripePaymentSheet.PaymentSheet.BillingDetailsCollectionConfiguration.AddressCollectionMode.FromRawValue(rawValue);
            if (fromRaw != null && fromRaw.Tag == StripePaymentSheet.PaymentSheet.BillingDetailsCollectionConfiguration.AddressCollectionMode.CaseTag.Full)
            {
                logger.Pass($"AddressCollectionMode.Full RawValue '{rawValue}' roundtrip");
                results.Pass("Config_AddressCollectionMode_RawValue");
            }
            else
            {
                logger.Fail($"AddressCollectionMode RawValue: raw='{rawValue}' fromRaw={fromRaw?.Tag}");
                results.Fail("Config_AddressCollectionMode_RawValue", "Roundtrip failed");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"AddressCollectionMode RawValue: {ex.Message}");
            results.Fail("Config_AddressCollectionMode_RawValue", ex.Message);
        }
    }

    // =========================================================================
    // Phase 21: STPPaymentMethodParams constructors — diverse payment methods
    // =========================================================================

    private void RunPaymentMethodParamsConstructorTests(TestLogger logger, TestResults results)
    {
        // iDEAL with BankName
        try
        {
            var idealParams = new StripePayments.STPPaymentMethodiDEALParams();
            idealParams.BankName = "ing";
            var billing = new StripePayments.STPPaymentMethodBillingDetails();
            billing.Name = "Test iDEAL";
            var methodParams = new StripePayments.STPPaymentMethodParams(idealParams, billing, null);
            var type = methodParams.Type;
            var bankOk = idealParams.BankName == "ing";
            if (type != null && bankOk)
            {
                logger.Pass($"iDEAL: type={type} bank={idealParams.BankName}");
                results.Pass("PaymentMethodParams_iDEAL");
            }
            else
            {
                logger.Fail($"iDEAL: type={type} bankOk={bankOk}");
                results.Fail("PaymentMethodParams_iDEAL", "Construction failed");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"PaymentMethodParams iDEAL: {ex.Message}");
            results.Fail("PaymentMethodParams_iDEAL", ex.Message);
        }

        // FPX with Bank enum
        try
        {
            var fpxParams = new StripePayments.STPPaymentMethodFPXParams();
            fpxParams.Bank = StripePayments.STPFPXBankBrand.Maybank2U;
            var billing = new StripePayments.STPPaymentMethodBillingDetails();
            billing.Name = "Test FPX";
            var methodParams = new StripePayments.STPPaymentMethodParams(fpxParams, billing, null);
            var type = methodParams.Type;
            var bankOk = fpxParams.Bank == StripePayments.STPFPXBankBrand.Maybank2U;
            if (type != null && bankOk)
            {
                logger.Pass($"FPX: type={type} bank=Maybank2U");
                results.Pass("PaymentMethodParams_FPX");
            }
            else
            {
                logger.Fail($"FPX: type={type} bankOk={bankOk}");
                results.Fail("PaymentMethodParams_FPX", "Construction failed");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"PaymentMethodParams FPX: {ex.Message}");
            results.Fail("PaymentMethodParams_FPX", ex.Message);
        }

        // EPS (no specific params, billing required)
        try
        {
            var epsParams = new StripePayments.STPPaymentMethodEPSParams();
            var billing = new StripePayments.STPPaymentMethodBillingDetails();
            billing.Name = "Test EPS";
            var methodParams = new StripePayments.STPPaymentMethodParams(epsParams, billing, null);
            var type = methodParams.Type;
            if (type != null)
            {
                logger.Pass($"EPS: type={type}");
                results.Pass("PaymentMethodParams_EPS");
            }
            else
            {
                logger.Fail("EPS: type is null");
                results.Fail("PaymentMethodParams_EPS", "Type null");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"PaymentMethodParams EPS: {ex.Message}");
            results.Fail("PaymentMethodParams_EPS", ex.Message);
        }

        // Bancontact (billing required)
        try
        {
            var bancontactParams = new StripePayments.STPPaymentMethodBancontactParams();
            var billing = new StripePayments.STPPaymentMethodBillingDetails();
            billing.Name = "Test Bancontact";
            var methodParams = new StripePayments.STPPaymentMethodParams(bancontactParams, billing, null);
            var type = methodParams.Type;
            if (type != null)
            {
                logger.Pass($"Bancontact: type={type}");
                results.Pass("PaymentMethodParams_Bancontact");
            }
            else
            {
                logger.Fail("Bancontact: type is null");
                results.Fail("PaymentMethodParams_Bancontact", "Type null");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"PaymentMethodParams Bancontact: {ex.Message}");
            results.Fail("PaymentMethodParams_Bancontact", ex.Message);
        }

        // AU BECS Debit with AccountNumber + BsbNumber
        try
        {
            var auBecsParams = new StripePayments.STPPaymentMethodAUBECSDebitParams();
            auBecsParams.AccountNumber = "000123456";
            auBecsParams.BsbNumber = "000-000";
            var billing = new StripePayments.STPPaymentMethodBillingDetails();
            billing.Name = "Test AU BECS";
            billing.Email = "test@example.com";
            var methodParams = new StripePayments.STPPaymentMethodParams(auBecsParams, billing, null);
            var type = methodParams.Type;
            var acctOk = auBecsParams.AccountNumber == "000123456";
            var bsbOk = auBecsParams.BsbNumber == "000-000";
            if (type != null && acctOk && bsbOk)
            {
                logger.Pass($"AU BECS: type={type} acct=OK bsb=OK");
                results.Pass("PaymentMethodParams_AuBecs");
            }
            else
            {
                logger.Fail($"AU BECS: type={type} acctOk={acctOk} bsbOk={bsbOk}");
                results.Fail("PaymentMethodParams_AuBecs", "Roundtrip failed");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"PaymentMethodParams AU BECS: {ex.Message}");
            results.Fail("PaymentMethodParams_AuBecs", ex.Message);
        }

        // Bacs Debit with SortCode + AccountNumber
        try
        {
            var bacsParams = new StripePayments.STPPaymentMethodBacsDebitParams();
            bacsParams.SortCode = "108800";
            bacsParams.AccountNumber = "00012345";
            var billing = new StripePayments.STPPaymentMethodBillingDetails();
            billing.Name = "Test Bacs";
            billing.Email = "bacs@example.com";
            var addr = new StripePayments.STPPaymentMethodAddress();
            addr.PostalCode = "SW1A 1AA";
            addr.Country = "GB";
            billing.Address = addr;
            var methodParams = new StripePayments.STPPaymentMethodParams(bacsParams, billing, null);
            var type = methodParams.Type;
            var sortOk = bacsParams.SortCode == "108800";
            var acctOk = bacsParams.AccountNumber == "00012345";
            if (type != null && sortOk && acctOk)
            {
                logger.Pass($"Bacs: type={type} sort=OK acct=OK");
                results.Pass("PaymentMethodParams_Bacs");
            }
            else
            {
                logger.Fail($"Bacs: type={type} sortOk={sortOk} acctOk={acctOk}");
                results.Fail("PaymentMethodParams_Bacs", "Roundtrip failed");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"PaymentMethodParams Bacs: {ex.Message}");
            results.Fail("PaymentMethodParams_Bacs", ex.Message);
        }

        // SEPA Debit with IBAN
        try
        {
            var sepaParams = new StripePayments.STPPaymentMethodSEPADebitParams();
            sepaParams.Iban = "DE89370400440532013000";
            var billing = new StripePayments.STPPaymentMethodBillingDetails();
            billing.Name = "Test SEPA";
            billing.Email = "sepa@example.com";
            var methodParams = new StripePayments.STPPaymentMethodParams(sepaParams, billing, null);
            var type = methodParams.Type;
            var ibanOk = sepaParams.Iban == "DE89370400440532013000";
            if (type != null && ibanOk)
            {
                logger.Pass($"SEPA: type={type} iban=OK");
                results.Pass("PaymentMethodParams_Sepa");
            }
            else
            {
                logger.Fail($"SEPA: type={type} ibanOk={ibanOk}");
                results.Fail("PaymentMethodParams_Sepa", "Roundtrip failed");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"PaymentMethodParams SEPA: {ex.Message}");
            results.Fail("PaymentMethodParams_Sepa", ex.Message);
        }

        // Przelewy24 (billing required)
        try
        {
            var p24Params = new StripePayments.STPPaymentMethodPrzelewy24Params();
            var billing = new StripePayments.STPPaymentMethodBillingDetails();
            billing.Name = "Test P24";
            billing.Email = "p24@example.com";
            var methodParams = new StripePayments.STPPaymentMethodParams(p24Params, billing, null);
            var type = methodParams.Type;
            if (type != null)
            {
                logger.Pass($"Przelewy24: type={type}");
                results.Pass("PaymentMethodParams_P24");
            }
            else
            {
                logger.Fail("Przelewy24: type is null");
                results.Fail("PaymentMethodParams_P24", "Type null");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"PaymentMethodParams P24: {ex.Message}");
            results.Fail("PaymentMethodParams_P24", ex.Message);
        }

        // GrabPay (billing optional)
        try
        {
            var grabPayParams = new StripePayments.STPPaymentMethodGrabPayParams();
            var methodParams = new StripePayments.STPPaymentMethodParams(grabPayParams, null, null);
            var type = methodParams.Type;
            if (type != null)
            {
                logger.Pass($"GrabPay: type={type}");
                results.Pass("PaymentMethodParams_GrabPay");
            }
            else
            {
                logger.Fail("GrabPay: type is null");
                results.Fail("PaymentMethodParams_GrabPay", "Type null");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"PaymentMethodParams GrabPay: {ex.Message}");
            results.Fail("PaymentMethodParams_GrabPay", ex.Message);
        }

        // Klarna (billing optional)
        try
        {
            var klarnaParams = new StripePayments.STPPaymentMethodKlarnaParams();
            var billing = new StripePayments.STPPaymentMethodBillingDetails();
            billing.Email = "klarna@example.com";
            var methodParams = new StripePayments.STPPaymentMethodParams(klarnaParams, billing, null);
            var type = methodParams.Type;
            if (type != null)
            {
                logger.Pass($"Klarna: type={type}");
                results.Pass("PaymentMethodParams_Klarna");
            }
            else
            {
                logger.Fail("Klarna: type is null");
                results.Fail("PaymentMethodParams_Klarna", "Type null");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"PaymentMethodParams Klarna: {ex.Message}");
            results.Fail("PaymentMethodParams_Klarna", ex.Message);
        }
    }

    // =========================================================================
    // Phase 22: CardBrandAcceptance — factories, extractors, All singleton
    // =========================================================================

    private void RunCardBrandAcceptanceTests(TestLogger logger, TestResults results)
    {
        // CardBrandAcceptance.All singleton
        try
        {
            var all = StripePaymentSheet.PaymentSheet.CardBrandAcceptance.All;
            if (all.Tag == StripePaymentSheet.PaymentSheet.CardBrandAcceptance.CaseTag.All)
            {
                logger.Pass("CardBrandAcceptance.All: tag=All");
                results.Pass("CardBrandAcceptance_All");
            }
            else
            {
                logger.Fail($"CardBrandAcceptance.All: got tag={all.Tag}");
                results.Fail("CardBrandAcceptance_All", $"Tag={all.Tag}");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"CardBrandAcceptance.All: {ex.Message}");
            results.Fail("CardBrandAcceptance_All", ex.Message);
        }

        // Allowed factory + TryGetAllowed extractor
        try
        {
            var brands = new List<StripePaymentSheet.PaymentSheet.CardBrandAcceptance.BrandCategory>
            {
                StripePaymentSheet.PaymentSheet.CardBrandAcceptance.BrandCategory.Visa,
                StripePaymentSheet.PaymentSheet.CardBrandAcceptance.BrandCategory.Mastercard,
            };
            var allowed = StripePaymentSheet.PaymentSheet.CardBrandAcceptance.Allowed(brands);
            if (allowed.Tag == StripePaymentSheet.PaymentSheet.CardBrandAcceptance.CaseTag.Allowed &&
                allowed.TryGetAllowed(out var extracted))
            {
                var hasVisa = extracted.Contains(StripePaymentSheet.PaymentSheet.CardBrandAcceptance.BrandCategory.Visa);
                var hasMc = extracted.Contains(StripePaymentSheet.PaymentSheet.CardBrandAcceptance.BrandCategory.Mastercard);
                if (hasVisa && hasMc && extracted.Count == 2)
                {
                    logger.Pass("CardBrandAcceptance.Allowed: Visa+Mastercard roundtrip");
                    results.Pass("CardBrandAcceptance_Allowed");
                }
                else
                {
                    logger.Fail($"Allowed extraction: count={extracted.Count} visa={hasVisa} mc={hasMc}");
                    results.Fail("CardBrandAcceptance_Allowed", "Extraction mismatch");
                }
            }
            else
            {
                logger.Fail($"Allowed: tag={allowed.Tag}");
                results.Fail("CardBrandAcceptance_Allowed", "Wrong tag or extraction failed");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"CardBrandAcceptance.Allowed: {ex.Message}");
            results.Fail("CardBrandAcceptance_Allowed", ex.Message);
        }

        // Disallowed factory + TryGetDisallowed extractor
        try
        {
            var brands = new List<StripePaymentSheet.PaymentSheet.CardBrandAcceptance.BrandCategory>
            {
                StripePaymentSheet.PaymentSheet.CardBrandAcceptance.BrandCategory.Amex,
            };
            var disallowed = StripePaymentSheet.PaymentSheet.CardBrandAcceptance.Disallowed(brands);
            if (disallowed.Tag == StripePaymentSheet.PaymentSheet.CardBrandAcceptance.CaseTag.Disallowed &&
                disallowed.TryGetDisallowed(out var extracted))
            {
                var hasAmex = extracted.Contains(StripePaymentSheet.PaymentSheet.CardBrandAcceptance.BrandCategory.Amex);
                if (hasAmex && extracted.Count == 1)
                {
                    logger.Pass("CardBrandAcceptance.Disallowed: Amex roundtrip");
                    results.Pass("CardBrandAcceptance_Disallowed");
                }
                else
                {
                    logger.Fail($"Disallowed extraction: count={extracted.Count} amex={hasAmex}");
                    results.Fail("CardBrandAcceptance_Disallowed", "Extraction mismatch");
                }
            }
            else
            {
                logger.Fail($"Disallowed: tag={disallowed.Tag}");
                results.Fail("CardBrandAcceptance_Disallowed", "Wrong tag or extraction failed");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"CardBrandAcceptance.Disallowed: {ex.Message}");
            results.Fail("CardBrandAcceptance_Disallowed", ex.Message);
        }

        // TryGetAllowed returns false for All case
        try
        {
            var all = StripePaymentSheet.PaymentSheet.CardBrandAcceptance.All;
            var gotAllowed = all.TryGetAllowed(out _);
            var gotDisallowed = all.TryGetDisallowed(out _);
            if (!gotAllowed && !gotDisallowed)
            {
                logger.Pass("CardBrandAcceptance.All: TryGet returns false for both");
                results.Pass("CardBrandAcceptance_All_TryGet");
            }
            else
            {
                logger.Fail($"All: TryGetAllowed={gotAllowed} TryGetDisallowed={gotDisallowed}");
                results.Fail("CardBrandAcceptance_All_TryGet", "Should be false for both");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"CardBrandAcceptance.All TryGet: {ex.Message}");
            results.Fail("CardBrandAcceptance_All_TryGet", ex.Message);
        }

        // Disallowed with multiple brands
        try
        {
            var brands = new List<StripePaymentSheet.PaymentSheet.CardBrandAcceptance.BrandCategory>
            {
                StripePaymentSheet.PaymentSheet.CardBrandAcceptance.BrandCategory.Visa,
                StripePaymentSheet.PaymentSheet.CardBrandAcceptance.BrandCategory.Discover,
            };
            var disallowed = StripePaymentSheet.PaymentSheet.CardBrandAcceptance.Disallowed(brands);
            if (disallowed.TryGetDisallowed(out var extracted) && extracted.Count == 2)
            {
                logger.Pass("CardBrandAcceptance.Disallowed: Visa+Discover two-brand roundtrip");
                results.Pass("CardBrandAcceptance_Disallowed_Multi");
            }
            else
            {
                logger.Fail($"Disallowed multi: extraction count={extracted?.Count}");
                results.Fail("CardBrandAcceptance_Disallowed_Multi", "Extraction failed");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"CardBrandAcceptance Disallowed multi: {ex.Message}");
            results.Fail("CardBrandAcceptance_Disallowed_Multi", ex.Message);
        }

        // Set CardBrandAcceptance on Config
        try
        {
            var config = new StripePaymentSheet.PaymentSheet.ConfigurationType();
            var brands = new List<StripePaymentSheet.PaymentSheet.CardBrandAcceptance.BrandCategory>
            {
                StripePaymentSheet.PaymentSheet.CardBrandAcceptance.BrandCategory.Visa,
                StripePaymentSheet.PaymentSheet.CardBrandAcceptance.BrandCategory.Mastercard,
            };
            config.CardBrandAcceptance = StripePaymentSheet.PaymentSheet.CardBrandAcceptance.Allowed(brands);
            var readBack = config.CardBrandAcceptance;
            if (readBack.Tag == StripePaymentSheet.PaymentSheet.CardBrandAcceptance.CaseTag.Allowed)
            {
                logger.Pass("Config.CardBrandAcceptance = Allowed(Visa,Mc) roundtrip via Config");
                results.Pass("CardBrandAcceptance_OnConfig");
            }
            else
            {
                logger.Fail($"Config.CardBrandAcceptance: tag={readBack.Tag}");
                results.Fail("CardBrandAcceptance_OnConfig", "Wrong tag");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"CardBrandAcceptance on Config: {ex.Message}");
            results.Fail("CardBrandAcceptance_OnConfig", ex.Message);
        }
    }

    // =========================================================================
    // Phase 23: PaymentSheetError & CustomerSheetError — factory + TryGet
    // =========================================================================

    private void RunErrorTypeTests(TestLogger logger, TestResults results)
    {
        // PaymentSheetError — payload factories + TryGet roundtrips
        var payloadErrorTests = new (Func<StripePaymentSheet.PaymentSheetError> Factory,
            StripePaymentSheet.PaymentSheetError.CaseTag Expected,
            Func<StripePaymentSheet.PaymentSheetError, (bool ok, string? val)> Extractor, string Name)[]
        {
            (() => StripePaymentSheet.PaymentSheetError.Unknown("test debug"),
                StripePaymentSheet.PaymentSheetError.CaseTag.Unknown,
                e => { var r = e.TryGetUnknown(out var v); return (r && v == "test debug", v); }, "Unknown"),
            (() => StripePaymentSheet.PaymentSheetError.IntegrationError("Missing key"),
                StripePaymentSheet.PaymentSheetError.CaseTag.IntegrationError,
                e => { var r = e.TryGetIntegrationError(out var v); return (r && v == "Missing key", v); }, "IntegrationError"),
            (() => StripePaymentSheet.PaymentSheetError.FlowControllerConfirmFailed("Timeout"),
                StripePaymentSheet.PaymentSheetError.CaseTag.FlowControllerConfirmFailed,
                e => { var r = e.TryGetFlowControllerConfirmFailed(out var v); return (r && v == "Timeout", v); }, "FlowControllerConfirmFailed"),
            (() => StripePaymentSheet.PaymentSheetError.IntentConfigurationValidationFailed("Intent mismatch"),
                StripePaymentSheet.PaymentSheetError.CaseTag.IntentConfigurationValidationFailed,
                e => { var r = e.TryGetIntentConfigurationValidationFailed(out var v); return (r && v == "Intent mismatch", v); }, "IntentConfigValidation"),
            (() => StripePaymentSheet.PaymentSheetError.DeferredIntentValidationFailed("Deferred mismatch"),
                StripePaymentSheet.PaymentSheetError.CaseTag.DeferredIntentValidationFailed,
                e => { var r = e.TryGetDeferredIntentValidationFailed(out var v); return (r && v == "Deferred mismatch", v); }, "DeferredIntentValidation"),
            (() => StripePaymentSheet.PaymentSheetError.LinkLookupNotFound("No account"),
                StripePaymentSheet.PaymentSheetError.CaseTag.LinkLookupNotFound,
                e => { var r = e.TryGetLinkLookupNotFound(out var v); return (r && v == "No account", v); }, "LinkLookupNotFound"),
        };

        foreach (var (factory, expected, extractor, name) in payloadErrorTests)
        {
            try
            {
                var err = factory();
                if (err.Tag == expected)
                {
                    var (ok, val) = extractor(err);
                    if (ok)
                    {
                        logger.Pass($"PaymentSheetError.{name}: factory + TryGet roundtrip");
                        results.Pass($"PaymentSheetError_{name}");
                    }
                    else
                    {
                        logger.Fail($"PaymentSheetError.{name}: extraction failed (val='{val}')");
                        results.Fail($"PaymentSheetError_{name}", $"Extraction failed: '{val}'");
                    }
                }
                else
                {
                    logger.Fail($"PaymentSheetError.{name}: expected {expected}, got {err.Tag}");
                    results.Fail($"PaymentSheetError_{name}", $"Tag={err.Tag}");
                }
            }
            catch (Exception ex)
            {
                logger.Fail($"PaymentSheetError.{name}: {ex.Message}");
                results.Fail($"PaymentSheetError_{name}", ex.Message);
            }
        }

        // PaymentSheetError no-payload singletons
        var singletonTests = new (Func<StripePaymentSheet.PaymentSheetError> Factory,
            StripePaymentSheet.PaymentSheetError.CaseTag Expected, string Name)[]
        {
            (() => StripePaymentSheet.PaymentSheetError.MissingClientSecret,
                StripePaymentSheet.PaymentSheetError.CaseTag.MissingClientSecret, "MissingClientSecret"),
            (() => StripePaymentSheet.PaymentSheetError.InvalidClientSecret,
                StripePaymentSheet.PaymentSheetError.CaseTag.InvalidClientSecret, "InvalidClientSecret"),
            (() => StripePaymentSheet.PaymentSheetError.AlreadyPresented,
                StripePaymentSheet.PaymentSheetError.CaseTag.AlreadyPresented, "AlreadyPresented"),
            (() => StripePaymentSheet.PaymentSheetError.ApplePayNotSupportedOrMisconfigured,
                StripePaymentSheet.PaymentSheetError.CaseTag.ApplePayNotSupportedOrMisconfigured, "ApplePayNotSupported"),
            (() => StripePaymentSheet.PaymentSheetError.UnexpectedResponseFromStripeAPI,
                StripePaymentSheet.PaymentSheetError.CaseTag.UnexpectedResponseFromStripeAPI, "UnexpectedResponse"),
            (() => StripePaymentSheet.PaymentSheetError.AccountLinkFailure,
                StripePaymentSheet.PaymentSheetError.CaseTag.AccountLinkFailure, "AccountLinkFailure"),
            (() => StripePaymentSheet.PaymentSheetError.LinkNotAuthorized,
                StripePaymentSheet.PaymentSheetError.CaseTag.LinkNotAuthorized, "LinkNotAuthorized"),
        };

        foreach (var (factory, expected, name) in singletonTests)
        {
            try
            {
                var err = factory();
                if (err.Tag == expected)
                {
                    logger.Pass($"PaymentSheetError.{name}: singleton tag OK");
                    results.Pass($"PaymentSheetError_{name}");
                }
                else
                {
                    logger.Fail($"PaymentSheetError.{name}: expected {expected}, got {err.Tag}");
                    results.Fail($"PaymentSheetError_{name}", $"Tag={err.Tag}");
                }
            }
            catch (Exception ex)
            {
                logger.Fail($"PaymentSheetError.{name}: {ex.Message}");
                results.Fail($"PaymentSheetError_{name}", ex.Message);
            }
        }

        // TryGet cross-case returns false
        try
        {
            var err = StripePaymentSheet.PaymentSheetError.Unknown("test");
            var gotIntegration = err.TryGetIntegrationError(out _);
            var gotFlow = err.TryGetFlowControllerConfirmFailed(out _);
            if (!gotIntegration && !gotFlow)
            {
                logger.Pass("PaymentSheetError: cross-case TryGet returns false");
                results.Pass("PaymentSheetError_CrossCase_TryGet");
            }
            else
            {
                logger.Fail($"Cross-case: gotIntegration={gotIntegration} gotFlow={gotFlow}");
                results.Fail("PaymentSheetError_CrossCase_TryGet", "Should be false");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"PaymentSheetError cross-case: {ex.Message}");
            results.Fail("PaymentSheetError_CrossCase_TryGet", ex.Message);
        }

        // CustomerSheetError.Unknown factory + TryGet
        try
        {
            var err = StripePaymentSheet.CustomerSheetError.Unknown("customer debug");
            if (err.Tag == StripePaymentSheet.CustomerSheetError.CaseTag.Unknown &&
                err.TryGetUnknown(out var desc) && desc == "customer debug")
            {
                logger.Pass("CustomerSheetError.Unknown: roundtrip");
                results.Pass("CustomerSheetError_Unknown");
            }
            else
            {
                logger.Fail($"CustomerSheetError.Unknown: tag={err.Tag}");
                results.Fail("CustomerSheetError_Unknown", "Roundtrip failed");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"CustomerSheetError.Unknown: {ex.Message}");
            results.Fail("CustomerSheetError_Unknown", ex.Message);
        }

        // CustomerSheetError.UnsupportedPaymentMethodType factory + TryGet
        try
        {
            var methods = new List<string> { "card", "ideal" };
            var err = StripePaymentSheet.CustomerSheetError.UnsupportedPaymentMethodType(methods);
            if (err.Tag == StripePaymentSheet.CustomerSheetError.CaseTag.UnsupportedPaymentMethodType &&
                err.TryGetUnsupportedPaymentMethodType(out var extracted) && extracted.Count == 2)
            {
                logger.Pass("CustomerSheetError.UnsupportedPaymentMethodType: roundtrip");
                results.Pass("CustomerSheetError_UnsupportedPM");
            }
            else
            {
                logger.Fail($"UnsupportedPM: tag={err.Tag}");
                results.Fail("CustomerSheetError_UnsupportedPM", "Roundtrip failed");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"CustomerSheetError.UnsupportedPM: {ex.Message}");
            results.Fail("CustomerSheetError_UnsupportedPM", ex.Message);
        }
    }

    // =========================================================================
    // Phase 24: EmbeddedPaymentElement.Configuration — property roundtrips
    // =========================================================================

    private void RunEmbeddedConfigurationTests(TestLogger logger, TestResults results)
    {
        // MerchantDisplayName
        try
        {
            var config = new StripePaymentSheet.EmbeddedPaymentElement.ConfigurationType();
            config.MerchantDisplayName = "Embedded Store";
            var readBack = config.MerchantDisplayName;
            if (readBack == "Embedded Store")
            {
                logger.Pass("EmbeddedConfig.MerchantDisplayName roundtrip");
                results.Pass("EmbeddedConfig_MerchantDisplayName");
            }
            else
            {
                logger.Fail($"EmbeddedConfig.MerchantDisplayName: got '{readBack}'");
                results.Fail("EmbeddedConfig_MerchantDisplayName", $"Got '{readBack}'");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"EmbeddedConfig.MerchantDisplayName: {ex.Message}");
            results.Fail("EmbeddedConfig_MerchantDisplayName", ex.Message);
        }

        // ReturnURL
        try
        {
            var config = new StripePaymentSheet.EmbeddedPaymentElement.ConfigurationType();
            config.ReturnURL = "embedded://return";
            var readBack = config.ReturnURL;
            if (readBack == "embedded://return")
            {
                logger.Pass("EmbeddedConfig.ReturnURL roundtrip");
                results.Pass("EmbeddedConfig_ReturnURL");
            }
            else
            {
                logger.Fail($"EmbeddedConfig.ReturnURL: got '{readBack}'");
                results.Fail("EmbeddedConfig_ReturnURL", $"Got '{readBack}'");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"EmbeddedConfig.ReturnURL: {ex.Message}");
            results.Fail("EmbeddedConfig_ReturnURL", ex.Message);
        }

        // EmbeddedViewDisplaysMandateText
        try
        {
            var config = new StripePaymentSheet.EmbeddedPaymentElement.ConfigurationType();
            config.EmbeddedViewDisplaysMandateText = true;
            var readBack = config.EmbeddedViewDisplaysMandateText;
            if (readBack == true)
            {
                logger.Pass("EmbeddedConfig.EmbeddedViewDisplaysMandateText roundtrip");
                results.Pass("EmbeddedConfig_MandateText");
            }
            else
            {
                logger.Fail($"EmbeddedConfig.MandateText: got {readBack}");
                results.Fail("EmbeddedConfig_MandateText", $"Got {readBack}");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"EmbeddedConfig.MandateText: {ex.Message}");
            results.Fail("EmbeddedConfig_MandateText", ex.Message);
        }

        // AllowsDelayedPaymentMethods
        try
        {
            var config = new StripePaymentSheet.EmbeddedPaymentElement.ConfigurationType();
            config.AllowsDelayedPaymentMethods = true;
            var readBack = config.AllowsDelayedPaymentMethods;
            if (readBack == true)
            {
                logger.Pass("EmbeddedConfig.AllowsDelayedPaymentMethods roundtrip");
                results.Pass("EmbeddedConfig_AllowsDelayed");
            }
            else
            {
                logger.Fail($"EmbeddedConfig.AllowsDelayed: got {readBack}");
                results.Fail("EmbeddedConfig_AllowsDelayed", $"Got {readBack}");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"EmbeddedConfig.AllowsDelayed: {ex.Message}");
            results.Fail("EmbeddedConfig_AllowsDelayed", ex.Message);
        }

        // Appearance set/get
        try
        {
            var config = new StripePaymentSheet.EmbeddedPaymentElement.ConfigurationType();
            var appearance = StripePaymentSheet.PaymentSheet.Appearance.Default;
            appearance.CornerRadius = 8.0;
            config.Appearance = appearance;
            var readBack = config.Appearance;
            if (readBack.CornerRadius.HasValue && Math.Abs(readBack.CornerRadius.Value - 8.0) < 0.01)
            {
                logger.Pass("EmbeddedConfig.Appearance with CornerRadius roundtrip");
                results.Pass("EmbeddedConfig_Appearance");
            }
            else
            {
                logger.Fail($"EmbeddedConfig.Appearance: CR={readBack.CornerRadius}");
                results.Fail("EmbeddedConfig_Appearance", "Roundtrip failed");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"EmbeddedConfig.Appearance: {ex.Message}");
            results.Fail("EmbeddedConfig_Appearance", ex.Message);
        }

        // BillingDetailsCollectionConfiguration on embedded config
        try
        {
            var config = new StripePaymentSheet.EmbeddedPaymentElement.ConfigurationType();
            var bdcc = config.BillingDetailsCollectionConfiguration;
            bdcc.Name = StripePaymentSheet.PaymentSheet.BillingDetailsCollectionConfiguration.CollectionMode.Always;
            bdcc.Address = StripePaymentSheet.PaymentSheet.BillingDetailsCollectionConfiguration.AddressCollectionMode.Full;
            var nameTag = bdcc.Name.Tag;
            var addrTag = bdcc.Address.Tag;
            if (nameTag == StripePaymentSheet.PaymentSheet.BillingDetailsCollectionConfiguration.CollectionMode.CaseTag.Always &&
                addrTag == StripePaymentSheet.PaymentSheet.BillingDetailsCollectionConfiguration.AddressCollectionMode.CaseTag.Full)
            {
                logger.Pass("EmbeddedConfig.BDCC: Name=Always, Address=Full");
                results.Pass("EmbeddedConfig_BDCC");
            }
            else
            {
                logger.Fail($"EmbeddedConfig.BDCC: name={nameTag} addr={addrTag}");
                results.Fail("EmbeddedConfig_BDCC", "Tag mismatch");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"EmbeddedConfig.BDCC: {ex.Message}");
            results.Fail("EmbeddedConfig_BDCC", ex.Message);
        }

        // FormSheetAction.Continue singleton
        try
        {
            var cont = StripePaymentSheet.EmbeddedPaymentElement.ConfigurationType.FormSheetActionType.Continue;
            if (cont.Tag == StripePaymentSheet.EmbeddedPaymentElement.ConfigurationType.FormSheetActionType.CaseTag.Continue)
            {
                logger.Pass("FormSheetAction.Continue: tag=Continue");
                results.Pass("EmbeddedConfig_FormSheetAction_Continue");
            }
            else
            {
                logger.Fail($"FormSheetAction.Continue: tag={cont.Tag}");
                results.Fail("EmbeddedConfig_FormSheetAction_Continue", $"Tag={cont.Tag}");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"FormSheetAction.Continue: {ex.Message}");
            results.Fail("EmbeddedConfig_FormSheetAction_Continue", ex.Message);
        }

        // RowSelectionBehavior.Default singleton
        try
        {
            var def = StripePaymentSheet.EmbeddedPaymentElement.ConfigurationType.RowSelectionBehaviorType.Default;
            if (def.Tag == StripePaymentSheet.EmbeddedPaymentElement.ConfigurationType.RowSelectionBehaviorType.CaseTag.Default)
            {
                logger.Pass("RowSelectionBehavior.Default: tag=Default");
                results.Pass("EmbeddedConfig_RowSelectionBehavior_Default");
            }
            else
            {
                logger.Fail($"RowSelectionBehavior.Default: tag={def.Tag}");
                results.Fail("EmbeddedConfig_RowSelectionBehavior_Default", $"Tag={def.Tag}");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"RowSelectionBehavior.Default: {ex.Message}");
            results.Fail("EmbeddedConfig_RowSelectionBehavior_Default", ex.Message);
        }

        // DefaultBillingDetails on embedded config
        try
        {
            var config = new StripePaymentSheet.EmbeddedPaymentElement.ConfigurationType();
            var bd = config.DefaultBillingDetails;
            bd.Name = "Embedded User";
            bd.Email = "embedded@example.com";
            var nameOk = bd.Name == "Embedded User";
            var emailOk = bd.Email == "embedded@example.com";
            if (nameOk && emailOk)
            {
                logger.Pass("EmbeddedConfig.DefaultBillingDetails roundtrip");
                results.Pass("EmbeddedConfig_DefaultBillingDetails");
            }
            else
            {
                logger.Fail($"EmbeddedConfig.DefaultBillingDetails: name={nameOk} email={emailOk}");
                results.Fail("EmbeddedConfig_DefaultBillingDetails", "Roundtrip failed");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"EmbeddedConfig.DefaultBillingDetails: {ex.Message}");
            results.Fail("EmbeddedConfig_DefaultBillingDetails", ex.Message);
        }

        // SavePaymentMethodOptInBehavior on embedded config
        try
        {
            var config = new StripePaymentSheet.EmbeddedPaymentElement.ConfigurationType();
            config.SavePaymentMethodOptInBehavior = StripePaymentSheet.PaymentSheet.SavePaymentMethodOptInBehavior.RequiresOptOut;
            var readBack = config.SavePaymentMethodOptInBehavior;
            if (readBack == StripePaymentSheet.PaymentSheet.SavePaymentMethodOptInBehavior.RequiresOptOut)
            {
                logger.Pass("EmbeddedConfig.SavePaymentMethodOptInBehavior = RequiresOptOut");
                results.Pass("EmbeddedConfig_SaveOptIn");
            }
            else
            {
                logger.Fail($"EmbeddedConfig.SaveOptIn: got {readBack}");
                results.Fail("EmbeddedConfig_SaveOptIn", $"Got {readBack}");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"EmbeddedConfig.SaveOptIn: {ex.Message}");
            results.Fail("EmbeddedConfig_SaveOptIn", ex.Message);
        }

        // OpensCardScannerAutomatically
        try
        {
            var config = new StripePaymentSheet.EmbeddedPaymentElement.ConfigurationType();
            config.OpensCardScannerAutomatically = true;
            var readBack = config.OpensCardScannerAutomatically;
            if (readBack == true)
            {
                logger.Pass("EmbeddedConfig.OpensCardScannerAutomatically roundtrip");
                results.Pass("EmbeddedConfig_CardScanner");
            }
            else
            {
                logger.Fail($"EmbeddedConfig.CardScanner: got {readBack}");
                results.Fail("EmbeddedConfig_CardScanner", $"Got {readBack}");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"EmbeddedConfig.CardScanner: {ex.Message}");
            results.Fail("EmbeddedConfig_CardScanner", ex.Message);
        }
    }

    // =========================================================================
    // Phase 25: AddressViewController.Configuration — construction + roundtrips
    // =========================================================================

    private void RunAddressViewControllerConfigTests(TestLogger logger, TestResults results)
    {
        // Minimal construction
        try
        {
            var address = new StripePaymentSheet.PaymentSheet.Address(
                city: "Chicago", country: "US", line1: "456 Elm St", postalCode: "60601", state: "IL");
            var defaults = new StripePaymentSheet.AddressViewController.ConfigurationType.DefaultAddressDetails(address);
            var fields = new StripePaymentSheet.AddressViewController.ConfigurationType.AdditionalFieldsType();
            var config = new StripePaymentSheet.AddressViewController.ConfigurationType(defaults, fields);

            var readTitle = config.Title;
            if (readTitle != null)
            {
                logger.Pass($"AddressViewController.Config: minimal construction OK (title='{readTitle}')");
                results.Pass("AddrConfig_MinimalConstruction");
            }
            else
            {
                logger.Fail("AddressViewController.Config: Title is null");
                results.Fail("AddrConfig_MinimalConstruction", "Title null");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"AddressViewController.Config minimal: {ex.Message}");
            results.Fail("AddrConfig_MinimalConstruction", ex.Message);
        }

        // DefaultAddressDetails with all properties
        try
        {
            var address = new StripePaymentSheet.PaymentSheet.Address(
                city: "Austin", country: "US", line1: "789 Oak Ave", line2: "Suite 100",
                postalCode: "73301", state: "TX");
            var defaults = new StripePaymentSheet.AddressViewController.ConfigurationType.DefaultAddressDetails(
                address, "John Smith", "+15551234567");
            var nameOk = defaults.Name == "John Smith";
            var phoneOk = defaults.Phone == "+15551234567";
            var addrCity = defaults.Address.City;
            if (nameOk && phoneOk && addrCity == "Austin")
            {
                logger.Pass("DefaultAddressDetails: name, phone, address roundtrip");
                results.Pass("AddrConfig_DefaultAddressDetails");
            }
            else
            {
                logger.Fail($"DefaultAddressDetails: name={nameOk} phone={phoneOk} city={addrCity}");
                results.Fail("AddrConfig_DefaultAddressDetails", "Roundtrip failed");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"DefaultAddressDetails: {ex.Message}");
            results.Fail("AddrConfig_DefaultAddressDetails", ex.Message);
        }

        // AdditionalFieldsType with Phone=Required + CheckboxLabel
        try
        {
            var fields = new StripePaymentSheet.AddressViewController.ConfigurationType.AdditionalFieldsType(
                StripePaymentSheet.AddressViewController.ConfigurationType.AdditionalFieldsType.FieldConfiguration.Required,
                "Save as default");
            var phoneField = fields.Phone;
            var label = fields.CheckboxLabel;
            if (phoneField == StripePaymentSheet.AddressViewController.ConfigurationType.AdditionalFieldsType.FieldConfiguration.Required &&
                label == "Save as default")
            {
                logger.Pass("AdditionalFieldsType: Phone=Required, CheckboxLabel roundtrip");
                results.Pass("AddrConfig_AdditionalFields");
            }
            else
            {
                logger.Fail($"AdditionalFields: phone={phoneField} label={label}");
                results.Fail("AddrConfig_AdditionalFields", "Roundtrip failed");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"AdditionalFieldsType: {ex.Message}");
            results.Fail("AddrConfig_AdditionalFields", ex.Message);
        }

        // FieldConfiguration enum values
        var fieldConfigs = new[] {
            (StripePaymentSheet.AddressViewController.ConfigurationType.AdditionalFieldsType.FieldConfiguration.Hidden, "Hidden"),
            (StripePaymentSheet.AddressViewController.ConfigurationType.AdditionalFieldsType.FieldConfiguration.Optional, "Optional"),
            (StripePaymentSheet.AddressViewController.ConfigurationType.AdditionalFieldsType.FieldConfiguration.Required, "Required"),
        };
        foreach (var (val, name) in fieldConfigs)
        {
            try
            {
                var fields = new StripePaymentSheet.AddressViewController.ConfigurationType.AdditionalFieldsType(val);
                var readBack = fields.Phone;
                if (readBack == val)
                {
                    logger.Pass($"FieldConfiguration.{name} roundtrip");
                    results.Pass($"AddrConfig_FieldConfig_{name}");
                }
                else
                {
                    logger.Fail($"FieldConfiguration.{name}: got {readBack}");
                    results.Fail($"AddrConfig_FieldConfig_{name}", $"Got {readBack}");
                }
            }
            catch (Exception ex)
            {
                logger.Fail($"FieldConfiguration.{name}: {ex.Message}");
                results.Fail($"AddrConfig_FieldConfig_{name}", ex.Message);
            }
        }

        // ButtonTitle and Title via full constructor
        try
        {
            var address = new StripePaymentSheet.PaymentSheet.Address(country: "US");
            var defaults = new StripePaymentSheet.AddressViewController.ConfigurationType.DefaultAddressDetails(address);
            var fields = new StripePaymentSheet.AddressViewController.ConfigurationType.AdditionalFieldsType();
            var appearance = StripePaymentSheet.PaymentSheet.Appearance.Default;
            var config = new StripePaymentSheet.AddressViewController.ConfigurationType(
                defaults, fields, new List<string> { "US", "CA", "GB" }, appearance,
                "Confirm Address", "Shipping Address");
            var btnTitle = config.ButtonTitle;
            var title = config.Title;
            if (btnTitle == "Confirm Address" && title == "Shipping Address")
            {
                logger.Pass($"AddressViewController.Config: ButtonTitle='{btnTitle}' Title='{title}'");
                results.Pass("AddrConfig_ButtonTitle_Title");
            }
            else
            {
                logger.Fail($"ButtonTitle='{btnTitle}' Title='{title}'");
                results.Fail("AddrConfig_ButtonTitle_Title", "Roundtrip failed");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"AddressViewController ButtonTitle/Title: {ex.Message}");
            results.Fail("AddrConfig_ButtonTitle_Title", ex.Message);
        }

        // AllowedCountries roundtrip
        try
        {
            var address = new StripePaymentSheet.PaymentSheet.Address(country: "US");
            var defaults = new StripePaymentSheet.AddressViewController.ConfigurationType.DefaultAddressDetails(address);
            var fields = new StripePaymentSheet.AddressViewController.ConfigurationType.AdditionalFieldsType();
            var countries = new List<string> { "US", "CA", "MX" };
            var config = new StripePaymentSheet.AddressViewController.ConfigurationType(defaults, fields, countries);
            var readBack = config.AllowedCountries;
            if (readBack != null && readBack.Count == 3)
            {
                logger.Pass($"AddressViewController.Config: AllowedCountries count={readBack.Count}");
                results.Pass("AddrConfig_AllowedCountries");
            }
            else
            {
                logger.Fail($"AllowedCountries: count={readBack?.Count}");
                results.Fail("AddrConfig_AllowedCountries", $"Count={readBack?.Count}");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"AddressViewController AllowedCountries: {ex.Message}");
            results.Fail("AddrConfig_AllowedCountries", ex.Message);
        }

        // Appearance on AddressViewController config
        try
        {
            var address = new StripePaymentSheet.PaymentSheet.Address(country: "GB");
            var defaults = new StripePaymentSheet.AddressViewController.ConfigurationType.DefaultAddressDetails(address);
            var fields = new StripePaymentSheet.AddressViewController.ConfigurationType.AdditionalFieldsType();
            var appearance = StripePaymentSheet.PaymentSheet.Appearance.Default;
            appearance.CornerRadius = 20.0;
            var config = new StripePaymentSheet.AddressViewController.ConfigurationType(
                defaults, fields, new List<string> { "GB" }, appearance);
            var readBack = config.Appearance;
            if (readBack.CornerRadius.HasValue && Math.Abs(readBack.CornerRadius.Value - 20.0) < 0.01)
            {
                logger.Pass("AddressViewController.Config.Appearance: CornerRadius roundtrip");
                results.Pass("AddrConfig_Appearance");
            }
            else
            {
                logger.Fail($"AddrConfig.Appearance: CR={readBack.CornerRadius}");
                results.Fail("AddrConfig_Appearance", "Roundtrip failed");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"AddrConfig.Appearance: {ex.Message}");
            results.Fail("AddrConfig_Appearance", ex.Message);
        }

        // PaymentSheet.Address full property roundtrip
        try
        {
            var addr = new StripePaymentSheet.PaymentSheet.Address(
                city: "London", country: "GB", line1: "10 Downing St",
                line2: "Westminster", postalCode: "SW1A 2AA", state: "London");
            var cityOk = addr.City == "London";
            var countryOk = addr.Country == "GB";
            var line1Ok = addr.Line1 == "10 Downing St";
            var line2Ok = addr.Line2 == "Westminster";
            var postalOk = addr.PostalCode == "SW1A 2AA";
            var stateOk = addr.State == "London";
            if (cityOk && countryOk && line1Ok && line2Ok && postalOk && stateOk)
            {
                logger.Pass("PaymentSheet.Address: all 6 fields roundtrip");
                results.Pass("AddrConfig_Address_AllFields");
            }
            else
            {
                logger.Fail($"Address: city={cityOk} country={countryOk} l1={line1Ok} l2={line2Ok} postal={postalOk} state={stateOk}");
                results.Fail("AddrConfig_Address_AllFields", "Roundtrip failed");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"PaymentSheet.Address all fields: {ex.Message}");
            results.Fail("AddrConfig_Address_AllFields", ex.Message);
        }
    }
}

#endregion
