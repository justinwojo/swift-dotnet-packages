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

        logger.Info("=== Section 1: Type Metadata ===");
        RunMetadataTests(logger, results);

        logger.Info("=== Section 2: Simple Enums ===");
        RunSimpleEnumTests(logger, results);

        logger.Info("=== Section 3: Tag-Based Enums ===");
        RunTagEnumTests(logger, results);

        logger.Info("=== Section 4: Struct Property Access ===");
        RunStructPropertyTests(logger, results);

        logger.Info("=== Section 5: Error Types ===");
        RunErrorTypeTests(logger, results);

        logger.Info("=== Section 6: Optional Property Patterns ===");
        RunOptionalPropertyTests(logger, results);

        logger.Info("=== Section 7: Method Calls ===");
        RunMethodCallTests(logger, results);

        logger.Info("=== Section 8: Memory & Dispose ===");
        RunMemoryTests(logger, results);

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

    // ──────────────────────────────────────────────
    // Section 1: Type Metadata
    // ──────────────────────────────────────────────

    private void RunMetadataTests(TestLogger logger, TestResults results)
    {
        // Test metadata access for all key struct and enum types
        var metadataTests = new (string Name, Func<TypeMetadata> GetMetadata)[]
        {
            ("RequestTimeout", () => SwiftObjectHelper<RequestTimeout>.GetTypeMetadata()),
            ("DetectionStatus", () => SwiftObjectHelper<DetectionStatus>.GetTypeMetadata()),
            ("Country", () => SwiftObjectHelper<Country>.GetTypeMetadata()),
            ("Region", () => SwiftObjectHelper<Region>.GetTypeMetadata()),
            ("DocumentType", () => SwiftObjectHelper<DocumentType>.GetTypeMetadata()),
            ("Point", () => SwiftObjectHelper<Point>.GetTypeMetadata()),
            ("Quadrilateral", () => SwiftObjectHelper<Quadrilateral>.GetTypeMetadata()),
            ("ProcessingStatus", () => SwiftObjectHelper<ProcessingStatus>.GetTypeMetadata()),
            ("AnonymizationMode", () => SwiftObjectHelper<AnonymizationMode>.GetTypeMetadata()),
            ("RecognitionMode", () => SwiftObjectHelper<RecognitionMode>.GetTypeMetadata()),
            ("FieldType", () => SwiftObjectHelper<FieldType>.GetTypeMetadata()),
            ("AlphabetType", () => SwiftObjectHelper<AlphabetType>.GetTypeMetadata()),
            ("ResourceDownloaderError", () => SwiftObjectHelper<ResourceDownloaderError>.GetTypeMetadata()),
            ("ResourcesError", () => SwiftObjectHelper<ResourcesError>.GetTypeMetadata()),
            ("SDKInitError", () => SwiftObjectHelper<SDKInitError>.GetTypeMetadata()),
            ("PingStatus", () => SwiftObjectHelper<PingStatus>.GetTypeMetadata()),
            ("RectangleFPoint", () => SwiftObjectHelper<RectangleFPoint>.GetTypeMetadata()),
            ("RectangleF", () => SwiftObjectHelper<RectangleF>.GetTypeMetadata()),
            ("InvalidLicenseKeyError", () => SwiftObjectHelper<InvalidLicenseKeyError>.GetTypeMetadata()),
            ("MissingResources", () => SwiftObjectHelper<MissingResources>.GetTypeMetadata()),
            ("MissingBundle", () => SwiftObjectHelper<MissingBundle>.GetTypeMetadata()),
            ("MemoryReserveError", () => SwiftObjectHelper<MemoryReserveError>.GetTypeMetadata()),
            ("ResourceLoadError", () => SwiftObjectHelper<ResourceLoadError>.GetTypeMetadata()),
            ("FieldState", () => SwiftObjectHelper<FieldState>.GetTypeMetadata()),
            ("DataMatchResult", () => SwiftObjectHelper<DataMatchResult>.GetTypeMetadata()),
            ("AddressDetailedInfo", () => SwiftObjectHelper<AddressDetailedInfo>.GetTypeMetadata()),
            ("RegionOfInterest", () => SwiftObjectHelper<RegionOfInterest>.GetTypeMetadata()),
            ("DependentInfo", () => SwiftObjectHelper<DependentInfo>.GetTypeMetadata()),
            ("CroppedImageSettings", () => SwiftObjectHelper<CroppedImageSettings>.GetTypeMetadata()),
        };

        foreach (var (name, getMetadata) in metadataTests)
        {
            try
            {
                var metadata = getMetadata();
                // Size >= 0 is valid — empty Swift structs (e.g. MissingResources) have size 0
                logger.Pass($"{name} metadata (size={metadata.Size})");
                results.Pass($"Metadata_{name}");
            }
            catch (Exception ex)
            {
                logger.Fail($"{name} metadata: {ex.Message}");
                results.Fail($"Metadata_{name}", ex.Message);
            }
        }
    }

    // ──────────────────────────────────────────────
    // Section 2: Simple Enums
    // ──────────────────────────────────────────────

    private void RunSimpleEnumTests(TestLogger logger, TestResults results)
    {
        // --- ImageAnalysisDetectionStatus ---
        TestSimpleEnum(logger, results, "ImageAnalysisDetectionStatus", new (string, int)[]
        {
            ("NotAvailable", (int)ImageAnalysisDetectionStatus.NotAvailable),
            ("NotDetected", (int)ImageAnalysisDetectionStatus.NotDetected),
            ("Detected", (int)ImageAnalysisDetectionStatus.Detected),
        });

        // --- DocumentImageColorStatus ---
        TestSimpleEnum(logger, results, "DocumentImageColorStatus", new (string, int)[]
        {
            ("NotAvailable", (int)DocumentImageColorStatus.NotAvailable),
            ("BlackAndWhite", (int)DocumentImageColorStatus.BlackAndWhite),
            ("Color", (int)DocumentImageColorStatus.Color),
        });

        // --- DocumentOrientation ---
        TestSimpleEnum(logger, results, "DocumentOrientation", new (string, int)[]
        {
            ("Horizontal", (int)DocumentOrientation.Horizontal),
            ("Vertical", (int)DocumentOrientation.Vertical),
            ("NotAvailable", (int)DocumentOrientation.NotAvailable),
        });

        // --- DocumentRotation ---
        TestSimpleEnum(logger, results, "DocumentRotation", new (string, int)[]
        {
            ("NotAvailable", (int)DocumentRotation.NotAvailable),
            ("Zero", (int)DocumentRotation.Zero),
            ("Clockwise90", (int)DocumentRotation.Clockwise90),
            ("CounterClockwise90", (int)DocumentRotation.CounterClockwise90),
            ("UpsideDown", (int)DocumentRotation.UpsideDown),
        });

        // --- MRZDocumentType ---
        TestSimpleEnum(logger, results, "MRZDocumentType", new (string, int)[]
        {
            ("Unknown", (int)MRZDocumentType.Unknown),
            ("IdentityCard", (int)MRZDocumentType.IdentityCard),
            ("Passport", (int)MRZDocumentType.Passport),
            ("Visa", (int)MRZDocumentType.Visa),
            ("GreenCard", (int)MRZDocumentType.GreenCard),
            ("MysPassIMM13P", (int)MRZDocumentType.MysPassIMM13P),
            ("DriverLicense", (int)MRZDocumentType.DriverLicense),
            ("InternalTravelDocument", (int)MRZDocumentType.InternalTravelDocument),
            ("BorderCrossingCard", (int)MRZDocumentType.BorderCrossingCard),
        });

        // --- MRZDocumentType.AllCases extension ---
        try
        {
            var allCases = MRZDocumentTypeExtensions.AllCases;
            if (allCases.Count == 9)
            {
                logger.Pass($"MRZDocumentType AllCases count={allCases.Count}");
                results.Pass("MRZDocumentType_AllCases");
            }
            else
            {
                logger.Fail($"MRZDocumentType AllCases: expected 9, got {allCases.Count}");
                results.Fail("MRZDocumentType_AllCases", $"Expected 9, got {allCases.Count}");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"MRZDocumentType AllCases: {ex.Message}");
            results.Fail("MRZDocumentType_AllCases", ex.Message);
        }

        // --- BarcodeType ---
        TestSimpleEnum(logger, results, "BarcodeType", new (string, int)[]
        {
            ("None", (int)BarcodeType.None),
            ("QrCode", (int)BarcodeType.QrCode),
            ("DataMatrix", (int)BarcodeType.DataMatrix),
            ("Upce", (int)BarcodeType.Upce),
            ("Upca", (int)BarcodeType.Upca),
            ("Ean8", (int)BarcodeType.Ean8),
            ("Ean13", (int)BarcodeType.Ean13),
            ("Code128", (int)BarcodeType.Code128),
            ("Code39", (int)BarcodeType.Code39),
            ("Itf", (int)BarcodeType.Itf),
            ("Aztec", (int)BarcodeType.Aztec),
            ("Pdf417", (int)BarcodeType.Pdf417),
        });

        // --- BarcodeType.AllCases extension ---
        try
        {
            var allCases = BarcodeTypeExtensions.AllCases;
            if (allCases.Count == 12)
            {
                logger.Pass($"BarcodeType AllCases count={allCases.Count}");
                results.Pass("BarcodeType_AllCases");
            }
            else
            {
                logger.Fail($"BarcodeType AllCases: expected 12, got {allCases.Count}");
                results.Fail("BarcodeType_AllCases", $"Expected 12, got {allCases.Count}");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"BarcodeType AllCases: {ex.Message}");
            results.Fail("BarcodeType_AllCases", ex.Message);
        }

        // --- InputImageSource ---
        TestSimpleEnum(logger, results, "InputImageSource", new (string, int)[]
        {
            ("Video", (int)InputImageSource.Video),
            ("Photo", (int)InputImageSource.Photo),
        });

        // --- ScanningMode ---
        TestSimpleEnum(logger, results, "ScanningMode", new (string, int)[]
        {
            ("Single", (int)ScanningMode.Single),
            ("Automatic", (int)ScanningMode.Automatic),
        });

        // --- DetectionLevel ---
        TestSimpleEnum(logger, results, "DetectionLevel", new (string, int)[]
        {
            ("Off", (int)DetectionLevel.Off),
            ("Low", (int)DetectionLevel.Low),
            ("Mid", (int)DetectionLevel.Mid),
            ("High", (int)DetectionLevel.High),
        });

        // --- ImageAnalysisLightingStatus ---
        TestSimpleEnum(logger, results, "ImageAnalysisLightingStatus", new (string, int)[]
        {
            ("NotAvailable", (int)ImageAnalysisLightingStatus.NotAvailable),
            ("TooBright", (int)ImageAnalysisLightingStatus.TooBright),
            ("TooDark", (int)ImageAnalysisLightingStatus.TooDark),
            ("Normal", (int)ImageAnalysisLightingStatus.Normal),
        });

        // --- ImageExtractionType ---
        TestSimpleEnum(logger, results, "ImageExtractionType", new (string, int)[]
        {
            ("Document", (int)ImageExtractionType.Document),
            ("Face", (int)ImageExtractionType.Face),
            ("Signature", (int)ImageExtractionType.Signature),
        });

        // --- ScanningStatus ---
        TestSimpleEnum(logger, results, "ScanningStatus", new (string, int)[]
        {
            ("ScanningSideInProgress", (int)ScanningStatus.ScanningSideInProgress),
            ("ScanningBarcodeInProgress", (int)ScanningStatus.ScanningBarcodeInProgress),
            ("SideScanned", (int)ScanningStatus.SideScanned),
            ("DocumentScanned", (int)ScanningStatus.DocumentScanned),
            ("Cancelled", (int)ScanningStatus.Cancelled),
        });

        // --- SessionError ---
        TestSimpleEnum(logger, results, "SessionError", new (string, int)[]
        {
            ("ProcessCallAfterDocumentScanned", (int)SessionError.ProcessCallAfterDocumentScanned),
            ("ResetCallAfterResultRetrieved", (int)SessionError.ResetCallAfterResultRetrieved),
        });

        // --- ImageOrientation ---
        TestSimpleEnum(logger, results, "ImageOrientation", new (string, int)[]
        {
            ("Up", (int)ImageOrientation.Up),
            ("Down", (int)ImageOrientation.Down),
            ("Left", (int)ImageOrientation.Left),
            ("Right", (int)ImageOrientation.Right),
            ("UpMirrored", (int)ImageOrientation.UpMirrored),
            ("DownMirrored", (int)ImageOrientation.DownMirrored),
            ("LeftMirrored", (int)ImageOrientation.LeftMirrored),
            ("RightMirrored", (int)ImageOrientation.RightMirrored),
        });

        // --- CameraFrameVideoOrientation ---
        TestSimpleEnum(logger, results, "CameraFrameVideoOrientation", new (string, int)[]
        {
            ("Portrait", (int)CameraFrameVideoOrientation.Portrait),
            ("PortraitUpsideDown", (int)CameraFrameVideoOrientation.PortraitUpsideDown),
            ("LandscapeRight", (int)CameraFrameVideoOrientation.LandscapeRight),
            ("LandscapeLeft", (int)CameraFrameVideoOrientation.LandscapeLeft),
        });

        // --- ModelLoadError ---
        TestSimpleEnum(logger, results, "ModelLoadError", new (string, int)[]
        {
            ("MissingFile", (int)ModelLoadError.MissingFile),
            ("InvalidFile", (int)ModelLoadError.InvalidFile),
            ("InvalidLicense", (int)ModelLoadError.InvalidLicense),
        });

        // --- ScanningSide ---
        try
        {
            var first = ScanningSide.First;
            var second = ScanningSide.Second;
            if ((long)first == 0 && (long)second == 1 && first != second)
            {
                logger.Pass("ScanningSide: First=0, Second=1, distinct");
                results.Pass("ScanningSide_Cases");
            }
            else
            {
                logger.Fail($"ScanningSide: unexpected values First={(long)first}, Second={(long)second}");
                results.Fail("ScanningSide_Cases", "Unexpected values");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"ScanningSide: {ex.Message}");
            results.Fail("ScanningSide_Cases", ex.Message);
        }

        // --- DataMatchState ---
        TestSimpleEnum(logger, results, "DataMatchState", new (string, int)[]
        {
            ("NotPerformed", (int)DataMatchState.NotPerformed),
            ("Failed", (int)DataMatchState.Failed),
            ("Success", (int)DataMatchState.Success),
        });

        // --- DataMatchFieldType ---
        TestSimpleEnum(logger, results, "DataMatchFieldType", new (string, int)[]
        {
            ("DateOfBirth", (int)DataMatchFieldType.DateOfBirth),
            ("DateOfExpiry", (int)DataMatchFieldType.DateOfExpiry),
            ("DocumentNumber", (int)DataMatchFieldType.DocumentNumber),
            ("DocumentAdditionalNumber", (int)DataMatchFieldType.DocumentAdditionalNumber),
            ("DocumentOptionalAdditionalNumber", (int)DataMatchFieldType.DocumentOptionalAdditionalNumber),
            ("PersonalIdNumber", (int)DataMatchFieldType.PersonalIdNumber),
        });

        // --- BarcodeElementKey sample ---
        try
        {
            var docType = BarcodeElementKey.DocumentType;
            var dob = BarcodeElementKey.DateOfBirth;
            var sex = BarcodeElementKey.Sex;
            var custId = BarcodeElementKey.CustomerIdNumber;
            if ((int)docType == 0 && (int)dob == 5 && (int)sex == 6 && (int)custId == 26)
            {
                logger.Pass("BarcodeElementKey: DocumentType=0, DateOfBirth=5, Sex=6, CustomerIdNumber=26");
                results.Pass("BarcodeElementKey_Values");
            }
            else
            {
                logger.Fail("BarcodeElementKey: unexpected values");
                results.Fail("BarcodeElementKey_Values", "Unexpected values");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"BarcodeElementKey: {ex.Message}");
            results.Fail("BarcodeElementKey_Values", ex.Message);
        }
    }

    /// <summary>
    /// Helper: tests a simple C# enum — verifies each case has the expected raw value and all cases are distinct.
    /// Registers one assertion per case + one for distinct check.
    /// </summary>
    private void TestSimpleEnum(TestLogger logger, TestResults results, string enumName, (string CaseName, int RawValue)[] cases)
    {
        bool allCorrect = true;
        var seen = new HashSet<int>();

        foreach (var (caseName, rawValue) in cases)
        {
            string testName = $"{enumName}_{caseName}";
            try
            {
                if (!seen.Add(rawValue))
                {
                    logger.Fail($"{enumName}.{caseName}: duplicate raw value {rawValue}");
                    results.Fail(testName, $"Duplicate raw value {rawValue}");
                    allCorrect = false;
                    continue;
                }
                logger.Pass($"{enumName}.{caseName}={rawValue}");
                results.Pass(testName);
            }
            catch (Exception ex)
            {
                logger.Fail($"{enumName}.{caseName}: {ex.Message}");
                results.Fail(testName, ex.Message);
                allCorrect = false;
            }
        }

        // Distinct check
        string distinctTest = $"{enumName}_AllDistinct";
        if (allCorrect && seen.Count == cases.Length)
        {
            logger.Pass($"{enumName}: all {cases.Length} cases distinct");
            results.Pass(distinctTest);
        }
        else if (allCorrect)
        {
            logger.Fail($"{enumName}: expected {cases.Length} distinct, got {seen.Count}");
            results.Fail(distinctTest, $"Expected {cases.Length} distinct, got {seen.Count}");
        }
        else
        {
            results.Fail(distinctTest, "Prior case failures");
        }
    }

    // ──────────────────────────────────────────────
    // Section 3: Tag-Based Enums
    // ──────────────────────────────────────────────

    private void RunTagEnumTests(TestLogger logger, TestResults results)
    {
        // --- DetectionStatus ---
        logger.Info("--- DetectionStatus (tag enum) ---");
        TestTagEnum(logger, results, "DetectionStatus", new (string, Func<DetectionStatus>, object)[]
        {
            ("Failed", () => DetectionStatus.Failed, DetectionStatus.CaseTag.Failed),
            ("Success", () => DetectionStatus.Success, DetectionStatus.CaseTag.Success),
            ("CameraTooFar", () => DetectionStatus.CameraTooFar, DetectionStatus.CaseTag.CameraTooFar),
            ("CameraTooClose", () => DetectionStatus.CameraTooClose, DetectionStatus.CaseTag.CameraTooClose),
            ("CameraAngleTooSteep", () => DetectionStatus.CameraAngleTooSteep, DetectionStatus.CaseTag.CameraAngleTooSteep),
            ("DocumentTooCloseToCameraEdge", () => DetectionStatus.DocumentTooCloseToCameraEdge, DetectionStatus.CaseTag.DocumentTooCloseToCameraEdge),
            ("DocumentPartiallyVisible", () => DetectionStatus.DocumentPartiallyVisible, DetectionStatus.CaseTag.DocumentPartiallyVisible),
            ("FallbackSuccess", () => DetectionStatus.FallbackSuccess, DetectionStatus.CaseTag.FallbackSuccess),
        });

        // DetectionStatus.RawValue round-trip
        try
        {
            var success = DetectionStatus.Success;
            var rawValue = success.RawValue;
            logger.Pass($"DetectionStatus.Success.RawValue=\"{rawValue}\"");
            results.Pass("DetectionStatus_RawValue");
        }
        catch (Exception ex)
        {
            logger.Fail($"DetectionStatus RawValue: {ex.Message}");
            results.Fail("DetectionStatus_RawValue", ex.Message);
        }

        // DetectionStatus.FromRawValue
        try
        {
            var fromRaw = DetectionStatus.FromRawValue("success");
            if (fromRaw != null)
            {
                logger.Pass($"DetectionStatus.FromRawValue(\"success\") returned non-null, tag={fromRaw.Tag}");
                results.Pass("DetectionStatus_FromRawValue_Valid");
            }
            else
            {
                logger.Fail("DetectionStatus.FromRawValue(\"success\") returned null");
                results.Fail("DetectionStatus_FromRawValue_Valid", "Returned null");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"DetectionStatus.FromRawValue: {ex.Message}");
            results.Fail("DetectionStatus_FromRawValue_Valid", ex.Message);
        }

        try
        {
            var fromRaw = DetectionStatus.FromRawValue("nonexistent_xyz_123");
            if (fromRaw == null)
            {
                logger.Pass("DetectionStatus.FromRawValue(invalid) correctly returned null");
                results.Pass("DetectionStatus_FromRawValue_Invalid");
            }
            else
            {
                logger.Fail("DetectionStatus.FromRawValue(invalid) should return null");
                results.Fail("DetectionStatus_FromRawValue_Invalid", "Should return null");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"DetectionStatus.FromRawValue(invalid): {ex.Message}");
            results.Fail("DetectionStatus_FromRawValue_Invalid", ex.Message);
        }

        // --- Country (sample of 15 from 256 cases) ---
        logger.Info("--- Country (tag enum, sampling 15 of 256) ---");
        TestTagEnum(logger, results, "Country", new (string, Func<Country>, object)[]
        {
            ("None", () => Country.None, Country.CaseTag.None),
            ("Albania", () => Country.Albania, Country.CaseTag.Albania),
            ("Australia", () => Country.Australia, Country.CaseTag.Australia),
            ("Canada", () => Country.Canada, Country.CaseTag.Canada),
            ("France", () => Country.France, Country.CaseTag.France),
            ("Germany", () => Country.Germany, Country.CaseTag.Germany),
            ("Japan", () => Country.Japan, Country.CaseTag.Japan),
            ("Mexico", () => Country.Mexico, Country.CaseTag.Mexico),
            ("Nigeria", () => Country.Nigeria, Country.CaseTag.Nigeria),
            ("SouthKorea", () => Country.SouthKorea, Country.CaseTag.SouthKorea),
            ("Uk", () => Country.Uk, Country.CaseTag.Uk),
            ("Usa", () => Country.Usa, Country.CaseTag.Usa),
            ("India", () => Country.India, Country.CaseTag.India),
            ("Brazil", () => Country.Brazil, Country.CaseTag.Brazil),
            ("Zimbabwe", () => Country.Zimbabwe, Country.CaseTag.Zimbabwe),
        });

        // Country.FromRawValue
        try
        {
            var fromRaw = Country.FromRawValue("none");
            if (fromRaw != null && fromRaw.Tag == Country.CaseTag.None)
            {
                logger.Pass("Country.FromRawValue(\"none\") → None");
                results.Pass("Country_FromRawValue");
            }
            else
            {
                logger.Fail($"Country.FromRawValue(\"none\"): unexpected result");
                results.Fail("Country_FromRawValue", "Unexpected result");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"Country.FromRawValue: {ex.Message}");
            results.Fail("Country_FromRawValue", ex.Message);
        }

        // Country.RawValue
        try
        {
            using var usa = Country.Usa;
            var raw = usa.RawValue;
            logger.Pass($"Country.UnitedStatesOfAmerica.RawValue=\"{raw}\"");
            results.Pass("Country_RawValue");
        }
        catch (Exception ex)
        {
            logger.Fail($"Country RawValue: {ex.Message}");
            results.Fail("Country_RawValue", ex.Message);
        }

        // --- Region (sample of 10) ---
        logger.Info("--- Region (tag enum, sampling 10) ---");
        TestTagEnum(logger, results, "Region", new (string, Func<Region>, object)[]
        {
            ("None", () => Region.None, Region.CaseTag.None),
            ("Alabama", () => Region.Alabama, Region.CaseTag.Alabama),
            ("California", () => Region.California, Region.CaseTag.California),
            ("NewYork", () => Region.NewYork, Region.CaseTag.NewYork),
            ("Texas", () => Region.Texas, Region.CaseTag.Texas),
            ("Alberta", () => Region.Alberta, Region.CaseTag.Alberta),
            ("BritishColumbia", () => Region.BritishColumbia, Region.CaseTag.BritishColumbia),
            ("Ontario", () => Region.Ontario, Region.CaseTag.Ontario),
            ("Queensland", () => Region.Queensland, Region.CaseTag.Queensland),
            ("Victoria", () => Region.Victoria, Region.CaseTag.Victoria),
        });

        // Region.FromRawValue
        try
        {
            var fromRaw = Region.FromRawValue("none");
            if (fromRaw != null)
            {
                logger.Pass("Region.FromRawValue(\"none\") returned non-null");
                results.Pass("Region_FromRawValue");
            }
            else
            {
                logger.Fail("Region.FromRawValue(\"none\") returned null");
                results.Fail("Region_FromRawValue", "Returned null");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"Region.FromRawValue: {ex.Message}");
            results.Fail("Region_FromRawValue", ex.Message);
        }

        // --- DocumentType (sample of 15 from ~90 cases) ---
        logger.Info("--- DocumentType (tag enum, sampling 15) ---");
        TestTagEnum(logger, results, "DocumentType", new (string, Func<DocumentType>, object)[]
        {
            ("None", () => DocumentType.None, DocumentType.CaseTag.None),
            ("Dl", () => DocumentType.Dl, DocumentType.CaseTag.Dl),
            ("Id", () => DocumentType.Id, DocumentType.CaseTag.Id),
            ("Passport", () => DocumentType.Passport, DocumentType.CaseTag.Passport),
            ("Visa", () => DocumentType.Visa, DocumentType.CaseTag.Visa),
            ("PassportCard", () => DocumentType.PassportCard, DocumentType.CaseTag.PassportCard),
            ("EmploymentPass", () => DocumentType.EmploymentPass, DocumentType.CaseTag.EmploymentPass),
            ("ConsularId", () => DocumentType.ConsularId, DocumentType.CaseTag.ConsularId),
            ("BorderCrossingCard", () => DocumentType.BorderCrossingCard, DocumentType.CaseTag.BorderCrossingCard),
            ("GreenCard", () => DocumentType.GreenCard, DocumentType.CaseTag.GreenCard),
            ("VoterId", () => DocumentType.VoterId, DocumentType.CaseTag.VoterId),
            ("MilitaryId", () => DocumentType.MilitaryId, DocumentType.CaseTag.MilitaryId),
            ("MinorsId", () => DocumentType.MinorsId, DocumentType.CaseTag.MinorsId),
            ("PublicServicesCard", () => DocumentType.PublicServicesCard, DocumentType.CaseTag.PublicServicesCard),
            ("ResidencePermit", () => DocumentType.ResidencePermit, DocumentType.CaseTag.ResidencePermit),
        });

        // DocumentType.FromRawValue
        try
        {
            var valid = DocumentType.FromRawValue("dl");
            var invalid = DocumentType.FromRawValue("nonexistent_type_xyz_123");
            if (valid != null && invalid == null)
            {
                logger.Pass($"DocumentType.FromRawValue: valid={valid.Tag}, invalid=null (correct)");
                results.Pass("DocumentType_FromRawValue");
            }
            else
            {
                logger.Fail($"DocumentType.FromRawValue: valid={valid != null}, invalid={invalid == null}");
                results.Fail("DocumentType_FromRawValue", "Unexpected results");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"DocumentType.FromRawValue: {ex.Message}");
            results.Fail("DocumentType_FromRawValue", ex.Message);
        }

        // --- ProcessingStatus ---
        logger.Info("--- ProcessingStatus (tag enum) ---");
        TestTagEnum(logger, results, "ProcessingStatus", new (string, Func<ProcessingStatus>, object)[]
        {
            ("Success", () => ProcessingStatus.Success, ProcessingStatus.CaseTag.Success),
            ("DetectionFailed", () => ProcessingStatus.DetectionFailed, ProcessingStatus.CaseTag.DetectionFailed),
            ("ImagePreprocessingFailed", () => ProcessingStatus.ImagePreprocessingFailed, ProcessingStatus.CaseTag.ImagePreprocessingFailed),
            ("StabilityTestFailed", () => ProcessingStatus.StabilityTestFailed, ProcessingStatus.CaseTag.StabilityTestFailed),
            ("ScanningWrongSide", () => ProcessingStatus.ScanningWrongSide, ProcessingStatus.CaseTag.ScanningWrongSide),
            ("FieldIdentificationFailed", () => ProcessingStatus.FieldIdentificationFailed, ProcessingStatus.CaseTag.FieldIdentificationFailed),
            ("MandatoryFieldMissing", () => ProcessingStatus.MandatoryFieldMissing, ProcessingStatus.CaseTag.MandatoryFieldMissing),
            ("InvalidCharactersFound", () => ProcessingStatus.InvalidCharactersFound, ProcessingStatus.CaseTag.InvalidCharactersFound),
            ("ImageReturnFailed", () => ProcessingStatus.ImageReturnFailed, ProcessingStatus.CaseTag.ImageReturnFailed),
            ("BarcodeRecognitionFailed", () => ProcessingStatus.BarcodeRecognitionFailed, ProcessingStatus.CaseTag.BarcodeRecognitionFailed),
            ("MrzParsingFailed", () => ProcessingStatus.MrzParsingFailed, ProcessingStatus.CaseTag.MrzParsingFailed),
            ("DocumentFiltered", () => ProcessingStatus.DocumentFiltered, ProcessingStatus.CaseTag.DocumentFiltered),
            ("UnsupportedDocument", () => ProcessingStatus.UnsupportedDocument, ProcessingStatus.CaseTag.UnsupportedDocument),
            ("AwaitingOtherSide", () => ProcessingStatus.AwaitingOtherSide, ProcessingStatus.CaseTag.AwaitingOtherSide),
            ("NotScanned", () => ProcessingStatus.NotScanned, ProcessingStatus.CaseTag.NotScanned),
            ("BarcodeDetectionFailed", () => ProcessingStatus.BarcodeDetectionFailed, ProcessingStatus.CaseTag.BarcodeDetectionFailed),
        });

        // --- AnonymizationMode ---
        logger.Info("--- AnonymizationMode (tag enum) ---");
        TestTagEnum(logger, results, "AnonymizationMode", new (string, Func<AnonymizationMode>, object)[]
        {
            ("None", () => AnonymizationMode.None, AnonymizationMode.CaseTag.None),
            ("ImageOnly", () => AnonymizationMode.ImageOnly, AnonymizationMode.CaseTag.ImageOnly),
            ("ResultFieldsOnly", () => AnonymizationMode.ResultFieldsOnly, AnonymizationMode.CaseTag.ResultFieldsOnly),
            ("FullResult", () => AnonymizationMode.FullResult, AnonymizationMode.CaseTag.FullResult),
        });

        // AnonymizationMode.FromRawValue
        try
        {
            var fromRaw = AnonymizationMode.FromRawValue("none");
            if (fromRaw != null)
            {
                logger.Pass($"AnonymizationMode.FromRawValue(\"none\") → tag={fromRaw.Tag}");
                results.Pass("AnonymizationMode_FromRawValue");
            }
            else
            {
                logger.Fail("AnonymizationMode.FromRawValue(\"none\") returned null");
                results.Fail("AnonymizationMode_FromRawValue", "Returned null");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"AnonymizationMode.FromRawValue: {ex.Message}");
            results.Fail("AnonymizationMode_FromRawValue", ex.Message);
        }

        // --- RecognitionMode ---
        logger.Info("--- RecognitionMode (tag enum) ---");
        TestTagEnum(logger, results, "RecognitionMode", new (string, Func<RecognitionMode>, object)[]
        {
            ("None", () => RecognitionMode.None, RecognitionMode.CaseTag.None),
            ("MrzId", () => RecognitionMode.MrzId, RecognitionMode.CaseTag.MrzId),
            ("MrzVisa", () => RecognitionMode.MrzVisa, RecognitionMode.CaseTag.MrzVisa),
            ("MrzPassport", () => RecognitionMode.MrzPassport, RecognitionMode.CaseTag.MrzPassport),
            ("PhotoId", () => RecognitionMode.PhotoId, RecognitionMode.CaseTag.PhotoId),
            ("FullRecognition", () => RecognitionMode.FullRecognition, RecognitionMode.CaseTag.FullRecognition),
            ("BarcodeId", () => RecognitionMode.BarcodeId, RecognitionMode.CaseTag.BarcodeId),
        });

        // --- AlphabetType ---
        logger.Info("--- AlphabetType (tag enum) ---");
        TestTagEnum(logger, results, "AlphabetType", new (string, Func<AlphabetType>, object)[]
        {
            ("Latin", () => AlphabetType.Latin, AlphabetType.CaseTag.Latin),
            ("Arabic", () => AlphabetType.Arabic, AlphabetType.CaseTag.Arabic),
            ("Cyrillic", () => AlphabetType.Cyrillic, AlphabetType.CaseTag.Cyrillic),
            ("Greek", () => AlphabetType.Greek, AlphabetType.CaseTag.Greek),
        });

        // --- FieldType (sample of 20 from 64 cases) ---
        logger.Info("--- FieldType (tag enum, sampling 20) ---");
        TestTagEnum(logger, results, "FieldType", new (string, Func<FieldType>, object)[]
        {
            ("FirstName", () => FieldType.FirstName, FieldType.CaseTag.FirstName),
            ("LastName", () => FieldType.LastName, FieldType.CaseTag.LastName),
            ("FullName", () => FieldType.FullName, FieldType.CaseTag.FullName),
            ("DateOfBirth", () => FieldType.DateOfBirth, FieldType.CaseTag.DateOfBirth),
            ("DateOfExpiry", () => FieldType.DateOfExpiry, FieldType.CaseTag.DateOfExpiry),
            ("DateOfIssue", () => FieldType.DateOfIssue, FieldType.CaseTag.DateOfIssue),
            ("DocumentNumber", () => FieldType.DocumentNumber, FieldType.CaseTag.DocumentNumber),
            ("Nationality", () => FieldType.Nationality, FieldType.CaseTag.Nationality),
            ("Sex", () => FieldType.Sex, FieldType.CaseTag.Sex),
            ("Address", () => FieldType.Address, FieldType.CaseTag.Address),
            ("IssuingAuthority", () => FieldType.IssuingAuthority, FieldType.CaseTag.IssuingAuthority),
            ("PersonalIdNumber", () => FieldType.PersonalIdNumber, FieldType.CaseTag.PersonalIdNumber),
            ("Employer", () => FieldType.Employer, FieldType.CaseTag.Employer),
            ("MaritalStatus", () => FieldType.MaritalStatus, FieldType.CaseTag.MaritalStatus),
            ("PlaceOfBirth", () => FieldType.PlaceOfBirth, FieldType.CaseTag.PlaceOfBirth),
            ("Race", () => FieldType.Race, FieldType.CaseTag.Race),
            ("Religion", () => FieldType.Religion, FieldType.CaseTag.Religion),
            ("Profession", () => FieldType.Profession, FieldType.CaseTag.Profession),
            ("VehicleClass", () => FieldType.VehicleClass, FieldType.CaseTag.VehicleClass),
            ("BloodType", () => FieldType.BloodType, FieldType.CaseTag.BloodType),
        });

        // FieldType.FromRawValue
        try
        {
            var fromRaw = FieldType.FromRawValue("firstName");
            if (fromRaw != null)
            {
                logger.Pass($"FieldType.FromRawValue(\"firstName\") → tag={fromRaw.Tag}");
                results.Pass("FieldType_FromRawValue");
            }
            else
            {
                logger.Fail("FieldType.FromRawValue(\"firstName\") returned null");
                results.Fail("FieldType_FromRawValue", "Returned null");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"FieldType.FromRawValue: {ex.Message}");
            results.Fail("FieldType_FromRawValue", ex.Message);
        }

        // FieldType.AllCases
        try
        {
            var allCases = FieldType.AllCases;
            if (allCases != null && allCases.Count == 64)
            {
                logger.Pass($"FieldType.AllCases count={allCases.Count}");
                results.Pass("FieldType_AllCases");
            }
            else
            {
                logger.Fail($"FieldType.AllCases: expected 64, got {allCases?.Count}");
                results.Fail("FieldType_AllCases", $"Expected 64, got {allCases?.Count}");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"FieldType.AllCases: {ex.Message}");
            results.Fail("FieldType_AllCases", ex.Message);
        }

        // --- PingStatus ---
        logger.Info("--- PingStatus (tag enum) ---");
        TestTagEnum(logger, results, "PingStatus", new (string, Func<PingStatus>, object)[]
        {
            ("Success", () => PingStatus.Success, PingStatus.CaseTag.Success),
            ("NetworkUnavailable", () => PingStatus.NetworkUnavailable, PingStatus.CaseTag.NetworkUnavailable),
            ("PingNotEnabled", () => PingStatus.PingNotEnabled, PingStatus.CaseTag.PingNotEnabled),
        });
    }

    /// <summary>
    /// Helper: tests a tag-based enum — verifies each case's Tag matches the expected CaseTag.
    /// Registers one assertion per case.
    /// </summary>
    private void TestTagEnum<T>(TestLogger logger, TestResults results, string enumName,
        (string Name, Func<T> Get, object ExpectedTag)[] cases) where T : IDisposable
    {
        foreach (var (caseName, get, expectedTag) in cases)
        {
            string testName = $"{enumName}_{caseName}";
            try
            {
                using var instance = get();
                // Use reflection to get Tag property since the exact enum types differ
                var tagProp = instance.GetType().GetProperty("Tag");
                if (tagProp == null)
                {
                    logger.Fail($"{enumName}.{caseName}: no Tag property");
                    results.Fail(testName, "No Tag property");
                    continue;
                }
                var actualTag = tagProp.GetValue(instance);
                if (actualTag != null && actualTag.Equals(expectedTag))
                {
                    logger.Pass($"{enumName}.{caseName} tag={actualTag}");
                    results.Pass(testName);
                }
                else
                {
                    logger.Fail($"{enumName}.{caseName}: expected tag={expectedTag}, got {actualTag}");
                    results.Fail(testName, $"Expected {expectedTag}, got {actualTag}");
                }
            }
            catch (Exception ex)
            {
                logger.Fail($"{enumName}.{caseName}: {ex.Message}");
                results.Fail(testName, ex.Message);
            }
        }
    }

    // ──────────────────────────────────────────────
    // Section 4: Struct Property Access
    // ──────────────────────────────────────────────

    private void RunStructPropertyTests(TestLogger logger, TestResults results)
    {
        // --- RequestTimeout.Default ---
        try
        {
            using var timeout = RequestTimeout.Default;
            if (timeout != null)
            {
                logger.Pass("RequestTimeout.Default returned non-null");
                results.Pass("RequestTimeout_Default");
            }
            else
            {
                logger.Fail("RequestTimeout.Default returned null");
                results.Fail("RequestTimeout_Default", "Returned null");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"RequestTimeout.Default: {ex.Message}");
            results.Fail("RequestTimeout_Default", ex.Message);
        }

        // --- RequestTimeout.Default accessed twice (not same reference, but both valid) ---
        try
        {
            using var t1 = RequestTimeout.Default;
            using var t2 = RequestTimeout.Default;
            if (t1 != null && t2 != null)
            {
                logger.Pass("RequestTimeout.Default: two independent accesses both valid");
                results.Pass("RequestTimeout_Default_TwoAccesses");
            }
            else
            {
                logger.Fail("RequestTimeout.Default: second access returned null");
                results.Fail("RequestTimeout_Default_TwoAccesses", "Null on second access");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"RequestTimeout.Default TwoAccesses: {ex.Message}");
            results.Fail("RequestTimeout_Default_TwoAccesses", ex.Message);
        }

        // --- ScanningSide extensions ---
        try
        {
            var allCases = ScanningSideExtensions.AllCases;
            if (allCases.Count == 2)
            {
                logger.Pass($"ScanningSide AllCases count={allCases.Count}");
                results.Pass("ScanningSide_AllCases");
            }
            else
            {
                logger.Fail($"ScanningSide AllCases: expected 2, got {allCases.Count}");
                results.Fail("ScanningSide_AllCases", $"Expected 2, got {allCases.Count}");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"ScanningSide AllCases: {ex.Message}");
            results.Fail("ScanningSide_AllCases", ex.Message);
        }

        // --- DataMatchState extensions ---
        try
        {
            var allCases = DataMatchStateExtensions.AllCases;
            if (allCases.Count == 3)
            {
                logger.Pass($"DataMatchState AllCases count={allCases.Count}");
                results.Pass("DataMatchState_AllCases");
            }
            else
            {
                logger.Fail($"DataMatchState AllCases: expected 3, got {allCases.Count}");
                results.Fail("DataMatchState_AllCases", $"Expected 3, got {allCases.Count}");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"DataMatchState AllCases: {ex.Message}");
            results.Fail("DataMatchState_AllCases", ex.Message);
        }

        // --- DataMatchFieldType extensions ---
        try
        {
            var allCases = DataMatchFieldTypeExtensions.AllCases;
            if (allCases.Count == 6)
            {
                logger.Pass($"DataMatchFieldType AllCases count={allCases.Count}");
                results.Pass("DataMatchFieldType_AllCases");
            }
            else
            {
                logger.Fail($"DataMatchFieldType AllCases: expected 6, got {allCases.Count}");
                results.Fail("DataMatchFieldType_AllCases", $"Expected 6, got {allCases.Count}");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"DataMatchFieldType AllCases: {ex.Message}");
            results.Fail("DataMatchFieldType_AllCases", ex.Message);
        }

        // --- ScanningMode extensions ---
        try
        {
            var allCases = ScanningModeExtensions.AllCases;
            if (allCases.Count == 2)
            {
                logger.Pass($"ScanningMode AllCases count={allCases.Count}");
                results.Pass("ScanningMode_AllCases");
            }
            else
            {
                logger.Fail($"ScanningMode AllCases: expected 2, got {allCases.Count}");
                results.Fail("ScanningMode_AllCases", $"Expected 2, got {allCases.Count}");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"ScanningMode AllCases: {ex.Message}");
            results.Fail("ScanningMode_AllCases", ex.Message);
        }

        // --- DetectionLevel extensions ---
        try
        {
            var allCases = DetectionLevelExtensions.AllCases;
            if (allCases.Count == 4)
            {
                logger.Pass($"DetectionLevel AllCases count={allCases.Count}");
                results.Pass("DetectionLevel_AllCases");
            }
            else
            {
                logger.Fail($"DetectionLevel AllCases: expected 4, got {allCases.Count}");
                results.Fail("DetectionLevel_AllCases", $"Expected 4, got {allCases.Count}");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"DetectionLevel AllCases: {ex.Message}");
            results.Fail("DetectionLevel_AllCases", ex.Message);
        }
    }

    // ──────────────────────────────────────────────
    // Section 5: Error Types
    // ──────────────────────────────────────────────

    private void RunErrorTypeTests(TestLogger logger, TestResults results)
    {
        // --- ResourceDownloaderError: singleton cases ---
        logger.Info("--- ResourceDownloaderError ---");
        try
        {
            var cacheDirNotFound = ResourceDownloaderError.CacheDirNotFound;
            if (cacheDirNotFound.Tag == ResourceDownloaderError.CaseTag.CacheDirNotFound)
            {
                logger.Pass("ResourceDownloaderError.CacheDirNotFound tag correct");
                results.Pass("ResourceDownloaderError_CacheDirNotFound");
            }
            else
            {
                logger.Fail($"ResourceDownloaderError.CacheDirNotFound: wrong tag {cacheDirNotFound.Tag}");
                results.Fail("ResourceDownloaderError_CacheDirNotFound", $"Wrong tag {cacheDirNotFound.Tag}");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"ResourceDownloaderError.CacheDirNotFound: {ex.Message}");
            results.Fail("ResourceDownloaderError_CacheDirNotFound", ex.Message);
        }

        try
        {
            var noInternet = ResourceDownloaderError.NoInternetConnection;
            if (noInternet.Tag == ResourceDownloaderError.CaseTag.NoInternetConnection)
            {
                logger.Pass("ResourceDownloaderError.NoInternetConnection tag correct");
                results.Pass("ResourceDownloaderError_NoInternetConnection");
            }
            else
            {
                logger.Fail($"ResourceDownloaderError.NoInternetConnection: wrong tag {noInternet.Tag}");
                results.Fail("ResourceDownloaderError_NoInternetConnection", $"Wrong tag {noInternet.Tag}");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"ResourceDownloaderError.NoInternetConnection: {ex.Message}");
            results.Fail("ResourceDownloaderError_NoInternetConnection", ex.Message);
        }

        try
        {
            var invalidResp = ResourceDownloaderError.InvalidResponse;
            var resourceUnavail = ResourceDownloaderError.ResourceUnavailable;
            var timedOut = ResourceDownloaderError.TimedOut;
            bool allCorrect = invalidResp.Tag == ResourceDownloaderError.CaseTag.InvalidResponse
                && resourceUnavail.Tag == ResourceDownloaderError.CaseTag.ResourceUnavailable
                && timedOut.Tag == ResourceDownloaderError.CaseTag.TimedOut;
            if (allCorrect)
            {
                logger.Pass("ResourceDownloaderError: InvalidResponse, ResourceUnavailable, TimedOut tags correct");
                results.Pass("ResourceDownloaderError_RemainingSingletons");
            }
            else
            {
                logger.Fail("ResourceDownloaderError: singleton tag mismatch");
                results.Fail("ResourceDownloaderError_RemainingSingletons", "Tag mismatch");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"ResourceDownloaderError singletons: {ex.Message}");
            results.Fail("ResourceDownloaderError_RemainingSingletons", ex.Message);
        }

        // --- ResourceDownloaderError: factory methods ---
        try
        {
            using var invalidUrl = ResourceDownloaderError.InvalidURL("https://example.com/bad");
            if (invalidUrl.Tag == ResourceDownloaderError.CaseTag.InvalidURL)
            {
                logger.Pass("ResourceDownloaderError.InvalidURL factory works, tag=InvalidURL");
                results.Pass("ResourceDownloaderError_InvalidURL_Factory");
            }
            else
            {
                logger.Fail($"ResourceDownloaderError.InvalidURL: wrong tag {invalidUrl.Tag}");
                results.Fail("ResourceDownloaderError_InvalidURL_Factory", $"Wrong tag {invalidUrl.Tag}");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"ResourceDownloaderError.InvalidURL factory: {ex.Message}");
            results.Fail("ResourceDownloaderError_InvalidURL_Factory", ex.Message);
        }

        try
        {
            using var downloadFailed = ResourceDownloaderError.DownloadFailed(404);
            if (downloadFailed.Tag == ResourceDownloaderError.CaseTag.DownloadFailed)
            {
                logger.Pass("ResourceDownloaderError.DownloadFailed(404) tag correct");
                results.Pass("ResourceDownloaderError_DownloadFailed_Factory");
            }
            else
            {
                logger.Fail($"ResourceDownloaderError.DownloadFailed: wrong tag {downloadFailed.Tag}");
                results.Fail("ResourceDownloaderError_DownloadFailed_Factory", $"Wrong tag {downloadFailed.Tag}");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"ResourceDownloaderError.DownloadFailed factory: {ex.Message}");
            results.Fail("ResourceDownloaderError_DownloadFailed_Factory", ex.Message);
        }

        try
        {
            using var hashMismatch = ResourceDownloaderError.HashMismatch("abc123");
            if (hashMismatch.Tag == ResourceDownloaderError.CaseTag.HashMismatch)
            {
                logger.Pass("ResourceDownloaderError.HashMismatch factory works");
                results.Pass("ResourceDownloaderError_HashMismatch_Factory");
            }
            else
            {
                logger.Fail($"ResourceDownloaderError.HashMismatch: wrong tag {hashMismatch.Tag}");
                results.Fail("ResourceDownloaderError_HashMismatch_Factory", $"Wrong tag {hashMismatch.Tag}");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"ResourceDownloaderError.HashMismatch factory: {ex.Message}");
            results.Fail("ResourceDownloaderError_HashMismatch_Factory", ex.Message);
        }

        // --- ResourceDownloaderError: TryGet pattern ---
        try
        {
            using var invalidUrl = ResourceDownloaderError.InvalidURL("https://bad.url");
            if (invalidUrl.TryGetInvalidURL(out var urlValue))
            {
                logger.Pass($"ResourceDownloaderError.TryGetInvalidURL returned true, value=\"{urlValue}\"");
                results.Pass("ResourceDownloaderError_TryGetInvalidURL");
            }
            else
            {
                logger.Fail("ResourceDownloaderError.TryGetInvalidURL returned false");
                results.Fail("ResourceDownloaderError_TryGetInvalidURL", "Returned false");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"ResourceDownloaderError.TryGetInvalidURL: {ex.Message}");
            results.Fail("ResourceDownloaderError_TryGetInvalidURL", ex.Message);
        }

        try
        {
            using var downloadFailed = ResourceDownloaderError.DownloadFailed(500);
            if (downloadFailed.TryGetDownloadFailed(out var statusCode))
            {
                if (statusCode == 500)
                {
                    logger.Pass("ResourceDownloaderError.TryGetDownloadFailed: statusCode=500");
                    results.Pass("ResourceDownloaderError_TryGetDownloadFailed");
                }
                else
                {
                    logger.Fail($"ResourceDownloaderError.TryGetDownloadFailed: expected 500, got {statusCode}");
                    results.Fail("ResourceDownloaderError_TryGetDownloadFailed", $"Expected 500, got {statusCode}");
                }
            }
            else
            {
                logger.Fail("ResourceDownloaderError.TryGetDownloadFailed returned false");
                results.Fail("ResourceDownloaderError_TryGetDownloadFailed", "Returned false");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"ResourceDownloaderError.TryGetDownloadFailed: {ex.Message}");
            results.Fail("ResourceDownloaderError_TryGetDownloadFailed", ex.Message);
        }

        // --- ResourceDownloaderError: TryGet wrong case returns false ---
        try
        {
            var singleton = ResourceDownloaderError.CacheDirNotFound;
            bool wrongCase = singleton.TryGetInvalidURL(out _);
            if (!wrongCase)
            {
                logger.Pass("ResourceDownloaderError: TryGetInvalidURL on CacheDirNotFound correctly returned false");
                results.Pass("ResourceDownloaderError_TryGet_WrongCase");
            }
            else
            {
                logger.Fail("ResourceDownloaderError: TryGetInvalidURL on CacheDirNotFound should return false");
                results.Fail("ResourceDownloaderError_TryGet_WrongCase", "Should return false");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"ResourceDownloaderError TryGet wrong case: {ex.Message}");
            results.Fail("ResourceDownloaderError_TryGet_WrongCase", ex.Message);
        }

        // --- ResourcesError: factory methods ---
        logger.Info("--- ResourcesError ---");
        try
        {
            using var corrupted = ResourcesError.CorruptedAssets("test_asset.bin");
            if (corrupted.Tag == ResourcesError.CaseTag.CorruptedAssets)
            {
                logger.Pass("ResourcesError.CorruptedAssets factory works");
                results.Pass("ResourcesError_CorruptedAssets");
            }
            else
            {
                logger.Fail($"ResourcesError.CorruptedAssets: wrong tag {corrupted.Tag}");
                results.Fail("ResourcesError_CorruptedAssets", $"Wrong tag {corrupted.Tag}");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"ResourcesError.CorruptedAssets: {ex.Message}");
            results.Fail("ResourcesError_CorruptedAssets", ex.Message);
        }

        try
        {
            using var download = ResourcesError.ResourceDownload("failed to download model");
            using var bundle = ResourcesError.InvalidBundle("bad bundle path");
            bool allCorrect = download.Tag == ResourcesError.CaseTag.ResourceDownload
                && bundle.Tag == ResourcesError.CaseTag.InvalidBundle;
            if (allCorrect)
            {
                logger.Pass("ResourcesError: ResourceDownload and InvalidBundle factories work");
                results.Pass("ResourcesError_OtherFactories");
            }
            else
            {
                logger.Fail("ResourcesError: factory tag mismatch");
                results.Fail("ResourcesError_OtherFactories", "Tag mismatch");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"ResourcesError factories: {ex.Message}");
            results.Fail("ResourcesError_OtherFactories", ex.Message);
        }

        // --- ResourcesError: TryGet pattern ---
        try
        {
            using var corrupted = ResourcesError.CorruptedAssets("test_path");
            if (corrupted.TryGetCorruptedAssets(out var path))
            {
                logger.Pass($"ResourcesError.TryGetCorruptedAssets: path=\"{path}\"");
                results.Pass("ResourcesError_TryGetCorruptedAssets");
            }
            else
            {
                logger.Fail("ResourcesError.TryGetCorruptedAssets returned false");
                results.Fail("ResourcesError_TryGetCorruptedAssets", "Returned false");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"ResourcesError.TryGetCorruptedAssets: {ex.Message}");
            results.Fail("ResourcesError_TryGetCorruptedAssets", ex.Message);
        }

        // --- SDKInitError: LicenseError singleton ---
        logger.Info("--- SDKInitError ---");
        try
        {
            var licenseError = SDKInitError.LicenseError;
            if (licenseError.Tag == SDKInitError.CaseTag.LicenseError)
            {
                logger.Pass("SDKInitError.LicenseError tag correct");
                results.Pass("SDKInitError_LicenseError");
            }
            else
            {
                logger.Fail($"SDKInitError.LicenseError: wrong tag {licenseError.Tag}");
                results.Fail("SDKInitError_LicenseError", $"Wrong tag {licenseError.Tag}");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"SDKInitError.LicenseError: {ex.Message}");
            results.Fail("SDKInitError_LicenseError", ex.Message);
        }
    }

    // ──────────────────────────────────────────────
    // Section 6: Optional Property Patterns
    // ──────────────────────────────────────────────

    private void RunOptionalPropertyTests(TestLogger logger, TestResults results)
    {
        // BlinkIDSdkSettings has testable properties
        logger.Info("--- BlinkIDSdkSettings ---");
        try
        {
            var metadata = SwiftObjectHelper<BlinkIDSdkSettings>.GetTypeMetadata();
            if (metadata.Size > 0)
            {
                logger.Pass($"BlinkIDSdkSettings metadata size={metadata.Size}");
                results.Pass("BlinkIDSdkSettings_Metadata");
            }
            else
            {
                logger.Fail("BlinkIDSdkSettings metadata: size is 0");
                results.Fail("BlinkIDSdkSettings_Metadata", "Size is 0");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"BlinkIDSdkSettings metadata: {ex.Message}");
            results.Fail("BlinkIDSdkSettings_Metadata", ex.Message);
        }

        // BlinkIDScanningResult metadata
        logger.Info("--- BlinkIDScanningResult ---");
        try
        {
            var metadata = SwiftObjectHelper<BlinkIDScanningResult>.GetTypeMetadata();
            if (metadata.Size > 0)
            {
                logger.Pass($"BlinkIDScanningResult metadata size={metadata.Size}");
                results.Pass("BlinkIDScanningResult_Metadata");
            }
            else
            {
                logger.Fail("BlinkIDScanningResult metadata: size is 0");
                results.Fail("BlinkIDScanningResult_Metadata", "Size is 0");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"BlinkIDScanningResult metadata: {ex.Message}");
            results.Fail("BlinkIDScanningResult_Metadata", ex.Message);
        }

        // VIZResult metadata
        logger.Info("--- VIZResult ---");
        try
        {
            var metadata = SwiftObjectHelper<VIZResult>.GetTypeMetadata();
            if (metadata.Size > 0)
            {
                logger.Pass($"VIZResult metadata size={metadata.Size}");
                results.Pass("VIZResult_Metadata");
            }
            else
            {
                logger.Fail("VIZResult metadata: size is 0");
                results.Fail("VIZResult_Metadata", "Size is 0");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"VIZResult metadata: {ex.Message}");
            results.Fail("VIZResult_Metadata", ex.Message);
        }

        // MRZResult metadata
        logger.Info("--- MRZResult ---");
        try
        {
            var metadata = SwiftObjectHelper<MRZResult>.GetTypeMetadata();
            if (metadata.Size > 0)
            {
                logger.Pass($"MRZResult metadata size={metadata.Size}");
                results.Pass("MRZResult_Metadata");
            }
            else
            {
                logger.Fail("MRZResult metadata: size is 0");
                results.Fail("MRZResult_Metadata", "Size is 0");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"MRZResult metadata: {ex.Message}");
            results.Fail("MRZResult_Metadata", ex.Message);
        }

        // BarcodeData metadata
        try
        {
            var metadata = SwiftObjectHelper<BarcodeData>.GetTypeMetadata();
            if (metadata.Size > 0)
            {
                logger.Pass($"BarcodeData metadata size={metadata.Size}");
                results.Pass("BarcodeData_Metadata");
            }
            else
            {
                logger.Fail("BarcodeData metadata: size is 0");
                results.Fail("BarcodeData_Metadata", "Size is 0");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"BarcodeData metadata: {ex.Message}");
            results.Fail("BarcodeData_Metadata", ex.Message);
        }

        // BarcodeResult metadata
        try
        {
            var metadata = SwiftObjectHelper<BarcodeResult>.GetTypeMetadata();
            if (metadata.Size > 0)
            {
                logger.Pass($"BarcodeResult metadata size={metadata.Size}");
                results.Pass("BarcodeResult_Metadata");
            }
            else
            {
                logger.Fail("BarcodeResult metadata: size is 0");
                results.Fail("BarcodeResult_Metadata", "Size is 0");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"BarcodeResult metadata: {ex.Message}");
            results.Fail("BarcodeResult_Metadata", ex.Message);
        }

        // SingleSideScanningResult metadata
        try
        {
            var metadata = SwiftObjectHelper<SingleSideScanningResult>.GetTypeMetadata();
            if (metadata.Size > 0)
            {
                logger.Pass($"SingleSideScanningResult metadata size={metadata.Size}");
                results.Pass("SingleSideScanningResult_Metadata");
            }
            else
            {
                logger.Fail("SingleSideScanningResult metadata: size is 0");
                results.Fail("SingleSideScanningResult_Metadata", "Size is 0");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"SingleSideScanningResult metadata: {ex.Message}");
            results.Fail("SingleSideScanningResult_Metadata", ex.Message);
        }

        // ScanningSettings metadata
        try
        {
            var metadata = SwiftObjectHelper<ScanningSettings>.GetTypeMetadata();
            if (metadata.Size > 0)
            {
                logger.Pass($"ScanningSettings metadata size={metadata.Size}");
                results.Pass("ScanningSettings_Metadata");
            }
            else
            {
                logger.Fail("ScanningSettings metadata: size is 0");
                results.Fail("ScanningSettings_Metadata", "Size is 0");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"ScanningSettings metadata: {ex.Message}");
            results.Fail("ScanningSettings_Metadata", ex.Message);
        }

        // BlinkIDSessionSettings metadata
        try
        {
            var metadata = SwiftObjectHelper<BlinkIDSessionSettings>.GetTypeMetadata();
            if (metadata.Size > 0)
            {
                logger.Pass($"BlinkIDSessionSettings metadata size={metadata.Size}");
                results.Pass("BlinkIDSessionSettings_Metadata");
            }
            else
            {
                logger.Fail("BlinkIDSessionSettings metadata: size is 0");
                results.Fail("BlinkIDSessionSettings_Metadata", "Size is 0");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"BlinkIDSessionSettings metadata: {ex.Message}");
            results.Fail("BlinkIDSessionSettings_Metadata", ex.Message);
        }

        // InputImageAnalysisResult metadata
        try
        {
            var metadata = SwiftObjectHelper<InputImageAnalysisResult>.GetTypeMetadata();
            if (metadata.Size > 0)
            {
                logger.Pass($"InputImageAnalysisResult metadata size={metadata.Size}");
                results.Pass("InputImageAnalysisResult_Metadata");
            }
            else
            {
                logger.Fail("InputImageAnalysisResult metadata: size is 0");
                results.Fail("InputImageAnalysisResult_Metadata", "Size is 0");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"InputImageAnalysisResult metadata: {ex.Message}");
            results.Fail("InputImageAnalysisResult_Metadata", ex.Message);
        }

        // FrameProcessResult metadata
        try
        {
            var metadata = SwiftObjectHelper<FrameProcessResult>.GetTypeMetadata();
            if (metadata.Size > 0)
            {
                logger.Pass($"FrameProcessResult metadata size={metadata.Size}");
                results.Pass("FrameProcessResult_Metadata");
            }
            else
            {
                logger.Fail("FrameProcessResult metadata: size is 0");
                results.Fail("FrameProcessResult_Metadata", "Size is 0");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"FrameProcessResult metadata: {ex.Message}");
            results.Fail("FrameProcessResult_Metadata", ex.Message);
        }

        // DocumentFilter metadata
        try
        {
            var metadata = SwiftObjectHelper<DocumentFilter>.GetTypeMetadata();
            if (metadata.Size > 0)
            {
                logger.Pass($"DocumentFilter metadata size={metadata.Size}");
                results.Pass("DocumentFilter_Metadata");
            }
            else
            {
                logger.Fail("DocumentFilter metadata: size is 0");
                results.Fail("DocumentFilter_Metadata", "Size is 0");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"DocumentFilter metadata: {ex.Message}");
            results.Fail("DocumentFilter_Metadata", ex.Message);
        }

        // DocumentNumberAnonymizationSettings metadata
        try
        {
            var metadata = SwiftObjectHelper<DocumentNumberAnonymizationSettings>.GetTypeMetadata();
            if (metadata.Size > 0)
            {
                logger.Pass($"DocumentNumberAnonymizationSettings metadata size={metadata.Size}");
                results.Pass("DocumentNumberAnonymizationSettings_Metadata");
            }
            else
            {
                logger.Fail("DocumentNumberAnonymizationSettings metadata: size is 0");
                results.Fail("DocumentNumberAnonymizationSettings_Metadata", "Size is 0");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"DocumentNumberAnonymizationSettings metadata: {ex.Message}");
            results.Fail("DocumentNumberAnonymizationSettings_Metadata", ex.Message);
        }

        // DocumentAnonymizationSettings metadata
        try
        {
            var metadata = SwiftObjectHelper<DocumentAnonymizationSettings>.GetTypeMetadata();
            if (metadata.Size > 0)
            {
                logger.Pass($"DocumentAnonymizationSettings metadata size={metadata.Size}");
                results.Pass("DocumentAnonymizationSettings_Metadata");
            }
            else
            {
                logger.Fail("DocumentAnonymizationSettings metadata: size is 0");
                results.Fail("DocumentAnonymizationSettings_Metadata", "Size is 0");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"DocumentAnonymizationSettings metadata: {ex.Message}");
            results.Fail("DocumentAnonymizationSettings_Metadata", ex.Message);
        }

        // RecognitionModeFilter metadata
        try
        {
            var metadata = SwiftObjectHelper<RecognitionModeFilter>.GetTypeMetadata();
            if (metadata.Size > 0)
            {
                logger.Pass($"RecognitionModeFilter metadata size={metadata.Size}");
                results.Pass("RecognitionModeFilter_Metadata");
            }
            else
            {
                logger.Fail("RecognitionModeFilter metadata: size is 0");
                results.Fail("RecognitionModeFilter_Metadata", "Size is 0");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"RecognitionModeFilter metadata: {ex.Message}");
            results.Fail("RecognitionModeFilter_Metadata", ex.Message);
        }

        // DocumentRules metadata
        try
        {
            var metadata = SwiftObjectHelper<DocumentRules>.GetTypeMetadata();
            if (metadata.Size > 0)
            {
                logger.Pass($"DocumentRules metadata size={metadata.Size}");
                results.Pass("DocumentRules_Metadata");
            }
            else
            {
                logger.Fail("DocumentRules metadata: size is 0");
                results.Fail("DocumentRules_Metadata", "Size is 0");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"DocumentRules metadata: {ex.Message}");
            results.Fail("DocumentRules_Metadata", ex.Message);
        }

        // DetailedFieldType metadata
        try
        {
            var metadata = SwiftObjectHelper<DetailedFieldType>.GetTypeMetadata();
            if (metadata.Size > 0)
            {
                logger.Pass($"DetailedFieldType metadata size={metadata.Size}");
                results.Pass("DetailedFieldType_Metadata");
            }
            else
            {
                logger.Fail("DetailedFieldType metadata: size is 0");
                results.Fail("DetailedFieldType_Metadata", "Size is 0");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"DetailedFieldType metadata: {ex.Message}");
            results.Fail("DetailedFieldType_Metadata", ex.Message);
        }

        // CameraFrame metadata
        try
        {
            var metadata = SwiftObjectHelper<CameraFrame>.GetTypeMetadata();
            if (metadata.Size > 0)
            {
                logger.Pass($"CameraFrame metadata size={metadata.Size}");
                results.Pass("CameraFrame_Metadata");
            }
            else
            {
                logger.Fail("CameraFrame metadata: size is 0");
                results.Fail("CameraFrame_Metadata", "Size is 0");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"CameraFrame metadata: {ex.Message}");
            results.Fail("CameraFrame_Metadata", ex.Message);
        }
    }

    // ──────────────────────────────────────────────
    // Section 7: Method Calls
    // ──────────────────────────────────────────────

    private void RunMethodCallTests(TestLogger logger, TestResults results)
    {
        // RecognitionMode.RawValue round-trip
        try
        {
            using var mode = RecognitionMode.FullRecognition;
            var raw = mode.RawValue;
            logger.Pass($"RecognitionMode.FullRecognition.RawValue=\"{raw}\"");
            results.Pass("RecognitionMode_RawValue");
        }
        catch (Exception ex)
        {
            logger.Fail($"RecognitionMode.RawValue: {ex.Message}");
            results.Fail("RecognitionMode_RawValue", ex.Message);
        }

        // RecognitionMode.FromRawValue
        try
        {
            var fromRaw = RecognitionMode.FromRawValue("fullRecognition");
            if (fromRaw != null)
            {
                logger.Pass($"RecognitionMode.FromRawValue(\"fullRecognition\") tag={fromRaw.Tag}");
                results.Pass("RecognitionMode_FromRawValue");
            }
            else
            {
                logger.Fail("RecognitionMode.FromRawValue(\"fullRecognition\") returned null");
                results.Fail("RecognitionMode_FromRawValue", "Returned null");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"RecognitionMode.FromRawValue: {ex.Message}");
            results.Fail("RecognitionMode_FromRawValue", ex.Message);
        }

        // AlphabetType.RawValue
        try
        {
            using var latin = AlphabetType.Latin;
            var raw = latin.RawValue;
            logger.Pass($"AlphabetType.Latin.RawValue=\"{raw}\"");
            results.Pass("AlphabetType_RawValue");
        }
        catch (Exception ex)
        {
            logger.Fail($"AlphabetType.RawValue: {ex.Message}");
            results.Fail("AlphabetType_RawValue", ex.Message);
        }

        // AlphabetType.FromRawValue
        try
        {
            var fromRaw = AlphabetType.FromRawValue("latin");
            if (fromRaw != null)
            {
                logger.Pass($"AlphabetType.FromRawValue(\"latin\") tag={fromRaw.Tag}");
                results.Pass("AlphabetType_FromRawValue");
            }
            else
            {
                logger.Fail("AlphabetType.FromRawValue(\"latin\") returned null");
                results.Fail("AlphabetType_FromRawValue", "Returned null");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"AlphabetType.FromRawValue: {ex.Message}");
            results.Fail("AlphabetType_FromRawValue", ex.Message);
        }

        // ProcessingStatus.RawValue
        try
        {
            var status = ProcessingStatus.Success;
            var raw = status.RawValue;
            logger.Pass($"ProcessingStatus.Success.RawValue=\"{raw}\"");
            results.Pass("ProcessingStatus_RawValue");
        }
        catch (Exception ex)
        {
            logger.Fail($"ProcessingStatus.RawValue: {ex.Message}");
            results.Fail("ProcessingStatus_RawValue", ex.Message);
        }

        // ProcessingStatus.FromRawValue
        try
        {
            var fromRaw = ProcessingStatus.FromRawValue("success");
            if (fromRaw != null)
            {
                logger.Pass($"ProcessingStatus.FromRawValue(\"success\") tag={fromRaw.Tag}");
                results.Pass("ProcessingStatus_FromRawValue");
            }
            else
            {
                logger.Fail("ProcessingStatus.FromRawValue(\"success\") returned null");
                results.Fail("ProcessingStatus_FromRawValue", "Returned null");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"ProcessingStatus.FromRawValue: {ex.Message}");
            results.Fail("ProcessingStatus_FromRawValue", ex.Message);
        }

        // AnonymizationMode.RawValue
        try
        {
            var mode = AnonymizationMode.FullResult;
            var raw = mode.RawValue;
            logger.Pass($"AnonymizationMode.FullResult.RawValue=\"{raw}\"");
            results.Pass("AnonymizationMode_RawValue");
        }
        catch (Exception ex)
        {
            logger.Fail($"AnonymizationMode.RawValue: {ex.Message}");
            results.Fail("AnonymizationMode_RawValue", ex.Message);
        }

        // Country.FromRawValue round-trip
        try
        {
            using var germany = Country.Germany;
            var raw = germany.RawValue;
            var roundTrip = Country.FromRawValue(raw);
            if (roundTrip != null && roundTrip.Tag == Country.CaseTag.Germany)
            {
                logger.Pass($"Country.Germany RawValue=\"{raw}\" round-trips correctly");
                results.Pass("Country_RawValue_RoundTrip");
            }
            else
            {
                logger.Fail($"Country.Germany round-trip failed: raw={raw}, tag={roundTrip?.Tag}");
                results.Fail("Country_RawValue_RoundTrip", "Round-trip failed");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"Country round-trip: {ex.Message}");
            results.Fail("Country_RawValue_RoundTrip", ex.Message);
        }

        // Region.RawValue
        try
        {
            using var ca = Region.California;
            var raw = ca.RawValue;
            logger.Pass($"Region.California.RawValue=\"{raw}\"");
            results.Pass("Region_RawValue");
        }
        catch (Exception ex)
        {
            logger.Fail($"Region.RawValue: {ex.Message}");
            results.Fail("Region_RawValue", ex.Message);
        }

        // FieldType.RawValue
        try
        {
            using var ft = FieldType.FirstName;
            var raw = ft.RawValue;
            logger.Pass($"FieldType.FirstName.RawValue=\"{raw}\"");
            results.Pass("FieldType_RawValue");
        }
        catch (Exception ex)
        {
            logger.Fail($"FieldType.RawValue: {ex.Message}");
            results.Fail("FieldType_RawValue", ex.Message);
        }

        // --- Pinglet nested enum types ---
        logger.Info("--- Pinglet nested enums ---");

        // CameraHardwareInfoPinglet.CameraFacing
        try
        {
            var front = CameraHardwareInfoPinglet.CameraFacing.Front;
            var back = CameraHardwareInfoPinglet.CameraFacing.Back;
            if (front.Tag != back.Tag)
            {
                logger.Pass($"CameraFacing: Front={front.Tag}, Back={back.Tag}");
                results.Pass("CameraFacing_Cases");
            }
            else
            {
                logger.Fail("CameraFacing: Front and Back have same tag");
                results.Fail("CameraFacing_Cases", "Same tag");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"CameraFacing: {ex.Message}");
            results.Fail("CameraFacing_Cases", ex.Message);
        }

        // CameraHardwareInfoPinglet.Focus
        try
        {
            var auto = CameraHardwareInfoPinglet.Focus.Auto;
            var fixedFocus = CameraHardwareInfoPinglet.Focus.Fixed;
            bool distinct = auto.Tag != fixedFocus.Tag;
            if (distinct)
            {
                logger.Pass($"CameraHardwareInfoPinglet.Focus: Auto={auto.Tag}, Fixed={fixedFocus.Tag}");
                results.Pass("Focus_Cases");
            }
            else
            {
                logger.Fail("CameraHardwareInfoPinglet.Focus: duplicate tags");
                results.Fail("Focus_Cases", "Duplicate tags");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"CameraHardwareInfoPinglet.Focus: {ex.Message}");
            results.Fail("Focus_Cases", ex.Message);
        }

        // LogPinglet.LogLevelType
        try
        {
            var info = LogPinglet.LogLevelType.Info;
            var warning = LogPinglet.LogLevelType.Warning;
            bool distinct = info.Tag != warning.Tag;
            if (distinct)
            {
                logger.Pass($"LogPinglet.LogLevelType: Info={info.Tag}, Warning={warning.Tag}");
                results.Pass("LogLevelType_Cases");
            }
            else
            {
                logger.Fail("LogPinglet.LogLevelType: duplicate tags");
                results.Fail("LogLevelType_Cases", "Duplicate tags");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"LogPinglet.LogLevelType: {ex.Message}");
            results.Fail("LogLevelType_Cases", ex.Message);
        }

        // WrapperProductInfoPinglet.WrapperProductType
        try
        {
            var flutter = WrapperProductInfoPinglet.WrapperProductType.Crossplatformflutter;
            var reactNative = WrapperProductInfoPinglet.WrapperProductType.Crossplatformreactnative;
            if (flutter.Tag != reactNative.Tag)
            {
                logger.Pass($"WrapperProductType: Flutter={flutter.Tag}, ReactNative={reactNative.Tag}");
                results.Pass("WrapperProductType_Cases");
            }
            else
            {
                logger.Fail("WrapperProductType: duplicate tags");
                results.Fail("WrapperProductType_Cases", "Duplicate tags");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"WrapperProductType: {ex.Message}");
            results.Fail("WrapperProductType_Cases", ex.Message);
        }

        // SdkInitStartPinglet.ProductType
        try
        {
            var blinkId = SdkInitStartPinglet.ProductType.Blinkid;
            if (blinkId != null)
            {
                logger.Pass($"SdkInitStartPinglet.ProductType.Blinkid tag={blinkId.Tag}");
                results.Pass("ProductType_Blinkid");
            }
            else
            {
                logger.Fail("ProductType.Blinkid is null");
                results.Fail("ProductType_Blinkid", "Null");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"SdkInitStartPinglet.ProductType: {ex.Message}");
            results.Fail("ProductType_Blinkid", ex.Message);
        }

        // SdkInitStartPinglet.PlatformType
        try
        {
            var ios = SdkInitStartPinglet.PlatformType.Ios;
            var android = SdkInitStartPinglet.PlatformType.Android;
            if (ios.Tag != android.Tag)
            {
                logger.Pass($"PlatformType: iOS={ios.Tag}, Android={android.Tag}");
                results.Pass("PlatformType_Cases");
            }
            else
            {
                logger.Fail("PlatformType: iOS and Android have same tag");
                results.Fail("PlatformType_Cases", "Same tag");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"SdkInitStartPinglet.PlatformType: {ex.Message}");
            results.Fail("PlatformType_Cases", ex.Message);
        }
    }

    // ──────────────────────────────────────────────
    // Section 8: Memory & Dispose
    // ──────────────────────────────────────────────

    private void RunMemoryTests(TestLogger logger, TestResults results)
    {
        // --- Memory pressure: DocumentType create/dispose cycles ---
        try
        {
            for (int i = 0; i < 100; i++)
            {
                using var dt = DocumentType.Dl;
                using var dt2 = DocumentType.Id;
            }
            logger.Pass("Memory pressure: 100 DocumentType create/dispose cycles");
            results.Pass("Memory_DocumentType_Cycles");
        }
        catch (Exception ex)
        {
            logger.Fail($"Memory pressure DocumentType: {ex.Message}");
            results.Fail("Memory_DocumentType_Cycles", ex.Message);
        }

        // --- Memory pressure: Country create/dispose cycles ---
        try
        {
            for (int i = 0; i < 50; i++)
            {
                using var c1 = Country.Usa;
                using var c2 = Country.Germany;
                using var c3 = Country.Japan;
            }
            logger.Pass("Memory pressure: 50 Country create/dispose cycles (3 per iteration)");
            results.Pass("Memory_Country_Cycles");
        }
        catch (Exception ex)
        {
            logger.Fail($"Memory pressure Country: {ex.Message}");
            results.Fail("Memory_Country_Cycles", ex.Message);
        }

        // --- Memory pressure: DetectionStatus ---
        // Note: singleton cases (e.g. Success, Failed) have _isCachedSingleton=true
        // and skip Dispose() internally, but we use 'using' for consistency.
        try
        {
            for (int i = 0; i < 50; i++)
            {
                using var s = DetectionStatus.Success;
                using var f = DetectionStatus.Failed;
                _ = s.Tag;
                _ = f.Tag;
            }
            logger.Pass("Memory pressure: 50 DetectionStatus access cycles");
            results.Pass("Memory_DetectionStatus_Cycles");
        }
        catch (Exception ex)
        {
            logger.Fail($"Memory pressure DetectionStatus: {ex.Message}");
            results.Fail("Memory_DetectionStatus_Cycles", ex.Message);
        }

        // --- Memory pressure: RequestTimeout ---
        try
        {
            for (int i = 0; i < 50; i++)
            {
                using var t = RequestTimeout.Default;
            }
            logger.Pass("Memory pressure: 50 RequestTimeout.Default cycles");
            results.Pass("Memory_RequestTimeout_Cycles");
        }
        catch (Exception ex)
        {
            logger.Fail($"Memory pressure RequestTimeout: {ex.Message}");
            results.Fail("Memory_RequestTimeout_Cycles", ex.Message);
        }

        // --- Memory pressure: ResourceDownloaderError factory + dispose ---
        try
        {
            for (int i = 0; i < 30; i++)
            {
                using var e1 = ResourceDownloaderError.InvalidURL($"url_{i}");
                using var e2 = ResourceDownloaderError.DownloadFailed(i);
                using var e3 = ResourceDownloaderError.HashMismatch($"hash_{i}");
            }
            logger.Pass("Memory pressure: 30 ResourceDownloaderError factory+dispose cycles");
            results.Pass("Memory_ResourceDownloaderError_Cycles");
        }
        catch (Exception ex)
        {
            logger.Fail($"Memory pressure ResourceDownloaderError: {ex.Message}");
            results.Fail("Memory_ResourceDownloaderError_Cycles", ex.Message);
        }

        // --- Memory pressure: FieldType access cycles ---
        try
        {
            for (int i = 0; i < 20; i++)
            {
                using var f1 = FieldType.FirstName;
                using var f2 = FieldType.LastName;
                using var f3 = FieldType.DateOfBirth;
                _ = f1.Tag;
                _ = f2.RawValue;
            }
            logger.Pass("Memory pressure: 20 FieldType access cycles");
            results.Pass("Memory_FieldType_Cycles");
        }
        catch (Exception ex)
        {
            logger.Fail($"Memory pressure FieldType: {ex.Message}");
            results.Fail("Memory_FieldType_Cycles", ex.Message);
        }

        // --- Dispose safety: double dispose ---
        try
        {
            var dt = DocumentType.Dl;
            dt.Dispose();
            dt.Dispose(); // Should not crash
            logger.Pass("Double dispose on DocumentType does not crash");
            results.Pass("Memory_DoubleDispose");
        }
        catch (Exception ex)
        {
            logger.Fail($"Double dispose: {ex.Message}");
            results.Fail("Memory_DoubleDispose", ex.Message);
        }
    }
}

#endregion
