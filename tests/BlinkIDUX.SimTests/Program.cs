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
using BUXUIEvent = BlinkIDUX.UIEvent;

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

        logger.Info("=== Phase 1: BlinkID Cross-Module Smoke Tests ===");
        RunBlinkIDCrossModuleTests(logger, results);

        logger.Info("=== Phase 2: BlinkIDUX Type Metadata ===");
        RunBlinkIDUXMetadataTests(logger, results);

        logger.Info("=== Phase 3: C# Enum Types ===");
        RunCSharpEnumTests(logger, results);

        logger.Info("=== Phase 4: CameraStatus Enum (6 cases) ===");
        RunCameraStatusTests(logger, results);

        logger.Info("=== Phase 5: BlinkIDScanningAlertType Enum (2 cases) ===");
        RunBlinkIDScanningAlertTypeTests(logger, results);

        logger.Info("=== Phase 6: DocumentSide Enum (5 cases) ===");
        RunDocumentSideTests(logger, results);

        logger.Info("=== Phase 7: ReticleState Enum (9 cases) ===");
        RunReticleStateTests(logger, results);

        logger.Info("=== Phase 8: UIEvent Enum (15 cases) ===");
        RunUIEventTests(logger, results);

        logger.Info("=== Phase 9: MicroblinkColor Enum (7 cases) ===");
        RunMicroblinkColorTests(logger, results);

        logger.Info("=== Phase 10: CaptureMode Enum ===");
        RunCaptureModeTests(logger, results);

        logger.Info("=== Phase 11: ScanningResult<T,U> Generic Enum ===");
        RunScanningResultTests(logger, results);

        logger.Info("=== Phase 12: Class Constructors (wrapper-dependent) ===");
        RunClassConstructorTests(logger, results);

        logger.Info("=== Phase 13: Struct Constructors (wrapper-dependent) ===");
        RunStructConstructorTests(logger, results);

        logger.Info("=== Phase 14: Property Access (wrapper-dependent) ===");
        RunPropertyAccessTests(logger, results);

        logger.Info("=== Phase 15: Protocol Interfaces ===");
        RunProtocolInterfaceTests(logger, results);

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

    // ==========================================
    // Phase 1: BlinkID Cross-Module Smoke Tests
    // ==========================================

    private void RunBlinkIDCrossModuleTests(TestLogger logger, TestResults results)
    {
        // Verify the BlinkID dependency module loads correctly from BlinkIDUX context
        foreach (var (typeName, getMetadata) in new (string, Func<TypeMetadata>)[]
        {
            ("BlinkID.RequestTimeout", () => SwiftObjectHelper<RequestTimeout>.GetTypeMetadata()),
            ("BlinkID.DetectionStatus", () => SwiftObjectHelper<DetectionStatus>.GetTypeMetadata()),
            ("BlinkID.DocumentType", () => SwiftObjectHelper<DocumentType>.GetTypeMetadata()),
            ("BlinkID.ProcessingStatus", () => SwiftObjectHelper<ProcessingStatus>.GetTypeMetadata()),
            ("BlinkID.RecognitionMode", () => SwiftObjectHelper<RecognitionMode>.GetTypeMetadata()),
        })
        {
            try
            {
                var metadata = getMetadata();
                if (metadata.Size > 0)
                {
                    logger.Pass($"{typeName} cross-module metadata (size={metadata.Size})");
                    results.Pass($"CrossModule_{typeName}_Metadata");
                }
                else
                {
                    logger.Fail($"{typeName} cross-module metadata: size is 0");
                    results.Fail($"CrossModule_{typeName}_Metadata", "Size is 0");
                }
            }
            catch (Exception ex)
            {
                logger.Fail($"{typeName} cross-module metadata: {ex.Message}");
                results.Fail($"CrossModule_{typeName}_Metadata", ex.Message);
            }
        }

        // Cross-module enum case construction
        try
        {
            var success = DetectionStatus.Success;
            var failed = DetectionStatus.Failed;
            if (success.Tag != failed.Tag)
            {
                logger.Pass("BlinkID DetectionStatus cross-module case construction");
                results.Pass("CrossModule_DetectionStatus_Cases");
            }
            else
            {
                logger.Fail("BlinkID DetectionStatus cases: same tag");
                results.Fail("CrossModule_DetectionStatus_Cases", "Same tag");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"BlinkID DetectionStatus cases: {ex.Message}");
            results.Fail("CrossModule_DetectionStatus_Cases", ex.Message);
        }
    }

    // ==========================================
    // Phase 2: BlinkIDUX Type Metadata
    // ==========================================

    private void RunBlinkIDUXMetadataTests(TestLogger logger, TestResults results)
    {
        // Test type metadata for all BlinkIDUX types (uses fallback to native lib, no wrapper needed)
        foreach (var (typeName, getMetadata) in new (string, Func<TypeMetadata>)[]
        {
            ("BlinkIDEventStream", () => SwiftObjectHelper<BlinkIDEventStream>.GetTypeMetadata()),
            ("BlinkIDAnalyzer", () => SwiftObjectHelper<BlinkIDAnalyzer>.GetTypeMetadata()),
            ("BlinkIDResultState", () => SwiftObjectHelper<BlinkIDResultState>.GetTypeMetadata()),
            ("BlinkIDScanningAlertType", () => SwiftObjectHelper<BlinkIDScanningAlertType>.GetTypeMetadata()),
            ("BlinkIDUXModel", () => SwiftObjectHelper<BlinkIDUXModel>.GetTypeMetadata()),
            ("BlinkIDTheme", () => SwiftObjectHelper<BlinkIDTheme>.GetTypeMetadata()),
            ("DocumentSide", () => SwiftObjectHelper<DocumentSide>.GetTypeMetadata()),
            ("Camera", () => SwiftObjectHelper<Camera>.GetTypeMetadata()),
            ("SampleBuffer", () => SwiftObjectHelper<SampleBuffer>.GetTypeMetadata()),
            ("CaptureService", () => SwiftObjectHelper<CaptureService>.GetTypeMetadata()),
            ("CameraStatus", () => SwiftObjectHelper<CameraStatus>.GetTypeMetadata()),
            // BUG: CaptureMode metadata returns size=0 — generator may not be emitting correct metadata accessor for this enum type
            ("CaptureMode", () => SwiftObjectHelper<CaptureMode>.GetTypeMetadata()),
            ("NetworkMonitor", () => SwiftObjectHelper<NetworkMonitor>.GetTypeMetadata()),
            ("ScanningUXSettings", () => SwiftObjectHelper<ScanningUXSettings>.GetTypeMetadata()),
            ("MicroblinkColor", () => SwiftObjectHelper<MicroblinkColor>.GetTypeMetadata()),
            ("ReticleState", () => SwiftObjectHelper<ReticleState>.GetTypeMetadata()),
            ("UIEvent", () => SwiftObjectHelper<BUXUIEvent>.GetTypeMetadata()),
        })
        {
            try
            {
                var metadata = getMetadata();
                if (metadata.Size > 0)
                {
                    logger.Pass($"{typeName} metadata (size={metadata.Size})");
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

    // ==========================================
    // Phase 3: C# Enum Types
    // ==========================================

    private void RunCSharpEnumTests(TestLogger logger, TestResults results)
    {
        // PassportOrientation — plain C# enum (no P/Invoke needed)
        try
        {
            var none = PassportOrientation.None;
            var left90 = PassportOrientation.Left90;
            var right90 = PassportOrientation.Right90;
            if ((int)none == 0 && (int)left90 == 1 && (int)right90 == 2)
            {
                logger.Pass("PassportOrientation raw values correct");
                results.Pass("PassportOrientation_RawValues");
            }
            else
            {
                logger.Fail($"PassportOrientation raw values: None={none}, Left90={left90}, Right90={right90}");
                results.Fail("PassportOrientation_RawValues", "Unexpected raw values");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"PassportOrientation: {ex.Message}");
            results.Fail("PassportOrientation_RawValues", ex.Message);
        }

        // PassportOrientation — all 3 values are distinct
        try
        {
            var values = new[] { PassportOrientation.None, PassportOrientation.Left90, PassportOrientation.Right90 };
            var distinct = new HashSet<int>(values.Select(v => (int)v));
            if (distinct.Count == 3)
            {
                logger.Pass("PassportOrientation all 3 values distinct");
                results.Pass("PassportOrientation_Distinct");
            }
            else
            {
                logger.Fail("PassportOrientation: not all values distinct");
                results.Fail("PassportOrientation_Distinct", $"Only {distinct.Count} distinct values");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"PassportOrientation distinct: {ex.Message}");
            results.Fail("PassportOrientation_Distinct", ex.Message);
        }

        // CaptureActivity — plain C# enum
        try
        {
            var idle = CaptureActivity.Idle;
            if ((int)idle == 0)
            {
                logger.Pass("CaptureActivity.Idle raw value = 0");
                results.Pass("CaptureActivity_Idle");
            }
            else
            {
                logger.Fail($"CaptureActivity.Idle raw value = {(int)idle}");
                results.Fail("CaptureActivity_Idle", $"Unexpected raw value: {(int)idle}");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"CaptureActivity: {ex.Message}");
            results.Fail("CaptureActivity_Idle", ex.Message);
        }

        // Camera.CameraPosition — nested C# enum
        // Note: Swift Camera.Position uses back=0, front=1 (not the intuitive front=0, back=1)
        try
        {
            var front = Camera.CameraPosition.Front;
            var back = Camera.CameraPosition.Back;
            if ((int)back == 0 && (int)front == 1)
            {
                logger.Pass($"Camera.CameraPosition raw values: Back=0, Front=1 (Swift ordering)");
                results.Pass("CameraPosition_RawValues");
            }
            else
            {
                logger.Fail($"Camera.CameraPosition: Front={(int)front}, Back={(int)back}");
                results.Fail("CameraPosition_RawValues", $"Unexpected: Front={(int)front}, Back={(int)back}");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"Camera.CameraPosition: {ex.Message}");
            results.Fail("CameraPosition_RawValues", ex.Message);
        }

        // Camera.CameraPosition — distinct values
        try
        {
            if (Camera.CameraPosition.Front != Camera.CameraPosition.Back)
            {
                logger.Pass("Camera.CameraPosition Front != Back");
                results.Pass("CameraPosition_Distinct");
            }
            else
            {
                logger.Fail("Camera.CameraPosition Front == Back");
                results.Fail("CameraPosition_Distinct", "Front == Back");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"Camera.CameraPosition distinct: {ex.Message}");
            results.Fail("CameraPosition_Distinct", ex.Message);
        }
    }

    // ==========================================
    // Phase 4: CameraStatus Enum (6 cases)
    // ==========================================

    private void RunCameraStatusTests(TestLogger logger, TestResults results)
    {
        // Test all 6 cases exist and have unique tags
        var cases = new (string name, Func<CameraStatus> factory, CameraStatus.CaseTag expectedTag)[]
        {
            ("Unknown", () => CameraStatus.Unknown, CameraStatus.CaseTag.Unknown),
            ("Unauthorized", () => CameraStatus.Unauthorized, CameraStatus.CaseTag.Unauthorized),
            ("Failed", () => CameraStatus.Failed, CameraStatus.CaseTag.Failed),
            ("Running", () => CameraStatus.Running, CameraStatus.CaseTag.Running),
            ("Interrupted", () => CameraStatus.Interrupted, CameraStatus.CaseTag.Interrupted),
            ("Stopped", () => CameraStatus.Stopped, CameraStatus.CaseTag.Stopped),
        };

        var tags = new HashSet<uint>();
        foreach (var (name, factory, expectedTag) in cases)
        {
            try
            {
                var instance = factory();
                var tag = instance.Tag;
                if (tag == expectedTag)
                {
                    logger.Pass($"CameraStatus.{name} tag={tag}");
                    results.Pass($"CameraStatus_{name}_Tag");
                    tags.Add((uint)tag);
                }
                else
                {
                    logger.Fail($"CameraStatus.{name}: expected tag {expectedTag}, got {tag}");
                    results.Fail($"CameraStatus_{name}_Tag", $"Expected {expectedTag}, got {tag}");
                }
            }
            catch (Exception ex)
            {
                logger.Fail($"CameraStatus.{name}: {ex.Message}");
                results.Fail($"CameraStatus_{name}_Tag", ex.Message);
            }
        }

        // Verify all tags are unique
        try
        {
            if (tags.Count == cases.Length)
            {
                logger.Pass($"CameraStatus: all {cases.Length} tags unique");
                results.Pass("CameraStatus_AllTagsUnique");
            }
            else
            {
                logger.Fail($"CameraStatus: only {tags.Count}/{cases.Length} unique tags");
                results.Fail("CameraStatus_AllTagsUnique", $"Only {tags.Count} unique tags");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"CameraStatus uniqueness: {ex.Message}");
            results.Fail("CameraStatus_AllTagsUnique", ex.Message);
        }

        // Verify CaseTag enum has correct raw values
        try
        {
            if ((uint)CameraStatus.CaseTag.Unknown == 0 &&
                (uint)CameraStatus.CaseTag.Unauthorized == 1 &&
                (uint)CameraStatus.CaseTag.Failed == 2 &&
                (uint)CameraStatus.CaseTag.Running == 3 &&
                (uint)CameraStatus.CaseTag.Interrupted == 4 &&
                (uint)CameraStatus.CaseTag.Stopped == 5)
            {
                logger.Pass("CameraStatus CaseTag raw values match Swift ordering");
                results.Pass("CameraStatus_CaseTag_RawValues");
            }
            else
            {
                logger.Fail("CameraStatus CaseTag raw values don't match expected");
                results.Fail("CameraStatus_CaseTag_RawValues", "Unexpected raw values");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"CameraStatus CaseTag: {ex.Message}");
            results.Fail("CameraStatus_CaseTag_RawValues", ex.Message);
        }
    }

    // ==========================================
    // Phase 5: BlinkIDScanningAlertType Enum (2 cases)
    // ==========================================

    private void RunBlinkIDScanningAlertTypeTests(TestLogger logger, TestResults results)
    {
        // Test both cases
        try
        {
            var timeout = BlinkIDScanningAlertType.Timeout;
            if (timeout.Tag == BlinkIDScanningAlertType.CaseTag.Timeout)
            {
                logger.Pass("BlinkIDScanningAlertType.Timeout tag correct");
                results.Pass("BlinkIDScanningAlertType_Timeout_Tag");
            }
            else
            {
                logger.Fail($"BlinkIDScanningAlertType.Timeout: expected Timeout, got {timeout.Tag}");
                results.Fail("BlinkIDScanningAlertType_Timeout_Tag", $"Got {timeout.Tag}");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"BlinkIDScanningAlertType.Timeout: {ex.Message}");
            results.Fail("BlinkIDScanningAlertType_Timeout_Tag", ex.Message);
        }

        try
        {
            var disallowed = BlinkIDScanningAlertType.DisallowedClass;
            if (disallowed.Tag == BlinkIDScanningAlertType.CaseTag.DisallowedClass)
            {
                logger.Pass("BlinkIDScanningAlertType.DisallowedClass tag correct");
                results.Pass("BlinkIDScanningAlertType_DisallowedClass_Tag");
            }
            else
            {
                logger.Fail($"BlinkIDScanningAlertType.DisallowedClass: got {disallowed.Tag}");
                results.Fail("BlinkIDScanningAlertType_DisallowedClass_Tag", $"Got {disallowed.Tag}");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"BlinkIDScanningAlertType.DisallowedClass: {ex.Message}");
            results.Fail("BlinkIDScanningAlertType_DisallowedClass_Tag", ex.Message);
        }

        // Tags are distinct
        try
        {
            var t = BlinkIDScanningAlertType.Timeout;
            var d = BlinkIDScanningAlertType.DisallowedClass;
            if (t.Tag != d.Tag)
            {
                logger.Pass("BlinkIDScanningAlertType tags are distinct");
                results.Pass("BlinkIDScanningAlertType_TagsDistinct");
            }
            else
            {
                logger.Fail("BlinkIDScanningAlertType: tags are same");
                results.Fail("BlinkIDScanningAlertType_TagsDistinct", "Same tag");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"BlinkIDScanningAlertType distinct: {ex.Message}");
            results.Fail("BlinkIDScanningAlertType_TagsDistinct", ex.Message);
        }

        // CaseTag raw values
        try
        {
            if ((uint)BlinkIDScanningAlertType.CaseTag.Timeout == 0 &&
                (uint)BlinkIDScanningAlertType.CaseTag.DisallowedClass == 1)
            {
                logger.Pass("BlinkIDScanningAlertType CaseTag raw values correct");
                results.Pass("BlinkIDScanningAlertType_CaseTag_RawValues");
            }
            else
            {
                logger.Fail("BlinkIDScanningAlertType CaseTag raw values wrong");
                results.Fail("BlinkIDScanningAlertType_CaseTag_RawValues", "Wrong values");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"BlinkIDScanningAlertType CaseTag: {ex.Message}");
            results.Fail("BlinkIDScanningAlertType_CaseTag_RawValues", ex.Message);
        }

        // Title property — requires wrapper xcframework
        // BUG: BlinkIDUXSwiftBindings wrapper compilation fails (SWIFTBIND051), so all
        // wrapper-dependent P/Invokes throw DllNotFoundException at runtime.
        try
        {
            var timeout = BlinkIDScanningAlertType.Timeout;
            var title = timeout.Title;
            logger.Pass($"BlinkIDScanningAlertType.Timeout.Title = '{title}'");
            results.Pass("BlinkIDScanningAlertType_Timeout_Title");
        }
        catch (DllNotFoundException)
        {
            // BUG: Wrapper xcframework not compiled — DllNotFoundException for BlinkIDUXSwiftBindings
            logger.Fail("BlinkIDScanningAlertType.Timeout.Title: DllNotFoundException (wrapper not compiled)");
            results.Fail("BlinkIDScanningAlertType_Timeout_Title",
                "DllNotFoundException — BlinkIDUXSwiftBindings wrapper compilation failed (SWIFTBIND051)");
        }
        catch (Exception ex)
        {
            logger.Fail($"BlinkIDScanningAlertType.Timeout.Title: {ex.Message}");
            results.Fail("BlinkIDScanningAlertType_Timeout_Title", ex.Message);
        }

        // Description property — same wrapper dependency
        try
        {
            var timeout = BlinkIDScanningAlertType.Timeout;
            var desc = timeout.Description;
            logger.Pass($"BlinkIDScanningAlertType.Timeout.Description = '{desc}'");
            results.Pass("BlinkIDScanningAlertType_Timeout_Description");
        }
        catch (DllNotFoundException)
        {
            // BUG: Wrapper xcframework not compiled
            logger.Fail("BlinkIDScanningAlertType.Timeout.Description: DllNotFoundException (wrapper not compiled)");
            results.Fail("BlinkIDScanningAlertType_Timeout_Description",
                "DllNotFoundException — BlinkIDUXSwiftBindings wrapper compilation failed (SWIFTBIND051)");
        }
        catch (Exception ex)
        {
            logger.Fail($"BlinkIDScanningAlertType.Timeout.Description: {ex.Message}");
            results.Fail("BlinkIDScanningAlertType_Timeout_Description", ex.Message);
        }
    }

    // ==========================================
    // Phase 6: DocumentSide Enum (5 cases)
    // ==========================================

    private void RunDocumentSideTests(TestLogger logger, TestResults results)
    {
        // No-payload cases (use DestructiveInjectEnumTag — no wrapper needed)
        var noPayloadCases = new (string name, Func<DocumentSide> factory, DocumentSide.CaseTag expectedTag)[]
        {
            ("Front", () => DocumentSide.Front, DocumentSide.CaseTag.Front),
            ("Back", () => DocumentSide.Back, DocumentSide.CaseTag.Back),
            ("Barcode", () => DocumentSide.Barcode, DocumentSide.CaseTag.Barcode),
            ("PassportBarcode", () => DocumentSide.PassportBarcode, DocumentSide.CaseTag.PassportBarcode),
        };

        var tags = new HashSet<uint>();
        foreach (var (name, factory, expectedTag) in noPayloadCases)
        {
            try
            {
                var instance = factory();
                var tag = instance.Tag;
                if (tag == expectedTag)
                {
                    logger.Pass($"DocumentSide.{name} tag={tag}");
                    results.Pass($"DocumentSide_{name}_Tag");
                    tags.Add((uint)tag);
                }
                else
                {
                    logger.Fail($"DocumentSide.{name}: expected {expectedTag}, got {tag}");
                    results.Fail($"DocumentSide_{name}_Tag", $"Expected {expectedTag}, got {tag}");
                }
            }
            catch (Exception ex)
            {
                logger.Fail($"DocumentSide.{name}: {ex.Message}");
                results.Fail($"DocumentSide_{name}_Tag", ex.Message);
            }
        }

        // Passport (payload case) — requires wrapper
        // BUG: BlinkIDUXSwiftBindings wrapper not compiled
        try
        {
            var passport = DocumentSide.Passport(PassportOrientation.None);
            if (passport.Tag == DocumentSide.CaseTag.Passport)
            {
                logger.Pass("DocumentSide.Passport(None) tag correct");
                results.Pass("DocumentSide_Passport_Tag");
            }
            else
            {
                logger.Fail($"DocumentSide.Passport(None): got tag {passport.Tag}");
                results.Fail("DocumentSide_Passport_Tag", $"Got {passport.Tag}");
            }
        }
        catch (DllNotFoundException)
        {
            // BUG: Wrapper not compiled
            logger.Fail("DocumentSide.Passport: DllNotFoundException (wrapper not compiled)");
            results.Fail("DocumentSide_Passport_Tag",
                "DllNotFoundException — BlinkIDUXSwiftBindings wrapper compilation failed (SWIFTBIND051)");
        }
        catch (Exception ex)
        {
            logger.Fail($"DocumentSide.Passport: {ex.Message}");
            results.Fail("DocumentSide_Passport_Tag", ex.Message);
        }

        // TryGetPassport on Passport case — wrapper-dependent
        try
        {
            var passport = DocumentSide.Passport(PassportOrientation.Left90);
            if (passport.TryGetPassport(out var orientation) && orientation == PassportOrientation.Left90)
            {
                logger.Pass("DocumentSide.Passport TryGetPassport extracted Left90");
                results.Pass("DocumentSide_Passport_TryGet");
            }
            else
            {
                logger.Fail("DocumentSide.Passport TryGetPassport failed");
                results.Fail("DocumentSide_Passport_TryGet", "TryGetPassport returned false or wrong value");
            }
        }
        catch (DllNotFoundException)
        {
            // BUG: Wrapper not compiled
            logger.Fail("DocumentSide.Passport TryGet: DllNotFoundException (wrapper not compiled)");
            results.Fail("DocumentSide_Passport_TryGet",
                "DllNotFoundException — BlinkIDUXSwiftBindings wrapper compilation failed (SWIFTBIND051)");
        }
        catch (Exception ex)
        {
            logger.Fail($"DocumentSide.Passport TryGet: {ex.Message}");
            results.Fail("DocumentSide_Passport_TryGet", ex.Message);
        }

        // TryGetPassport on non-Passport case should return false
        try
        {
            var front = DocumentSide.Front;
            if (!front.TryGetPassport(out _))
            {
                logger.Pass("DocumentSide.Front.TryGetPassport correctly returns false");
                results.Pass("DocumentSide_Front_TryGetPassport_False");
            }
            else
            {
                logger.Fail("DocumentSide.Front.TryGetPassport should return false");
                results.Fail("DocumentSide_Front_TryGetPassport_False", "Returned true for non-Passport case");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"DocumentSide.Front.TryGetPassport: {ex.Message}");
            results.Fail("DocumentSide_Front_TryGetPassport_False", ex.Message);
        }

        // All no-payload tags unique
        try
        {
            if (tags.Count == noPayloadCases.Length)
            {
                logger.Pass($"DocumentSide: all {noPayloadCases.Length} no-payload tags unique");
                results.Pass("DocumentSide_NoPayload_TagsUnique");
            }
            else
            {
                logger.Fail($"DocumentSide: only {tags.Count}/{noPayloadCases.Length} unique");
                results.Fail("DocumentSide_NoPayload_TagsUnique", "Duplicate tags");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"DocumentSide uniqueness: {ex.Message}");
            results.Fail("DocumentSide_NoPayload_TagsUnique", ex.Message);
        }

        // CaseTag raw values
        try
        {
            if ((uint)DocumentSide.CaseTag.Passport == 0 &&
                (uint)DocumentSide.CaseTag.Front == 1 &&
                (uint)DocumentSide.CaseTag.Back == 2 &&
                (uint)DocumentSide.CaseTag.Barcode == 3 &&
                (uint)DocumentSide.CaseTag.PassportBarcode == 4)
            {
                logger.Pass("DocumentSide CaseTag raw values correct");
                results.Pass("DocumentSide_CaseTag_RawValues");
            }
            else
            {
                logger.Fail("DocumentSide CaseTag raw values wrong");
                results.Fail("DocumentSide_CaseTag_RawValues", "Wrong values");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"DocumentSide CaseTag: {ex.Message}");
            results.Fail("DocumentSide_CaseTag_RawValues", ex.Message);
        }
    }

    // ==========================================
    // Phase 7: ReticleState Enum (9 cases)
    // ==========================================

    private void RunReticleStateTests(TestLogger logger, TestResults results)
    {
        // No-payload cases (use DestructiveInjectEnumTag — no wrapper needed)
        var noPayloadCases = new (string name, Func<ReticleState> factory, ReticleState.CaseTag expectedTag)[]
        {
            ("Front", () => ReticleState.Front, ReticleState.CaseTag.Front),
            ("Back", () => ReticleState.Back, ReticleState.CaseTag.Back),
            ("Barcode", () => ReticleState.Barcode, ReticleState.CaseTag.Barcode),
            ("Detecting", () => ReticleState.Detecting, ReticleState.CaseTag.Detecting),
            ("Flip", () => ReticleState.Flip, ReticleState.CaseTag.Flip),
            ("Inactive", () => ReticleState.Inactive, ReticleState.CaseTag.Inactive),
        };

        var tags = new HashSet<uint>();
        foreach (var (name, factory, expectedTag) in noPayloadCases)
        {
            try
            {
                var instance = factory();
                var tag = instance.Tag;
                if (tag == expectedTag)
                {
                    logger.Pass($"ReticleState.{name} tag={tag}");
                    results.Pass($"ReticleState_{name}_Tag");
                    tags.Add((uint)tag);
                }
                else
                {
                    logger.Fail($"ReticleState.{name}: expected {expectedTag}, got {tag}");
                    results.Fail($"ReticleState_{name}_Tag", $"Expected {expectedTag}, got {tag}");
                }
            }
            catch (Exception ex)
            {
                logger.Fail($"ReticleState.{name}: {ex.Message}");
                results.Fail($"ReticleState_{name}_Tag", ex.Message);
            }
        }

        // All no-payload tags unique
        try
        {
            if (tags.Count == noPayloadCases.Length)
            {
                logger.Pass($"ReticleState: all {noPayloadCases.Length} no-payload tags unique");
                results.Pass("ReticleState_NoPayload_TagsUnique");
            }
            else
            {
                logger.Fail($"ReticleState: only {tags.Count}/{noPayloadCases.Length} unique");
                results.Fail("ReticleState_NoPayload_TagsUnique", "Duplicate tags");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"ReticleState uniqueness: {ex.Message}");
            results.Fail("ReticleState_NoPayload_TagsUnique", ex.Message);
        }

        // Payload case: Error(String) — requires wrapper
        // BUG: BlinkIDUXSwiftBindings wrapper not compiled
        try
        {
            var error = ReticleState.Error("Test error message");
            if (error.Tag == ReticleState.CaseTag.Error)
            {
                logger.Pass("ReticleState.Error() tag correct");
                results.Pass("ReticleState_Error_Tag");
            }
            else
            {
                logger.Fail($"ReticleState.Error(): got tag {error.Tag}");
                results.Fail("ReticleState_Error_Tag", $"Got {error.Tag}");
            }
        }
        catch (DllNotFoundException)
        {
            logger.Fail("ReticleState.Error: DllNotFoundException (wrapper not compiled)");
            results.Fail("ReticleState_Error_Tag",
                "DllNotFoundException — BlinkIDUXSwiftBindings wrapper compilation failed (SWIFTBIND051)");
        }
        catch (Exception ex)
        {
            logger.Fail($"ReticleState.Error: {ex.Message}");
            results.Fail("ReticleState_Error_Tag", ex.Message);
        }

        // Payload case: Passport(String) — requires wrapper
        try
        {
            var passport = ReticleState.Passport("Show passport");
            if (passport.Tag == ReticleState.CaseTag.Passport)
            {
                logger.Pass("ReticleState.Passport() tag correct");
                results.Pass("ReticleState_Passport_Tag");
            }
            else
            {
                logger.Fail($"ReticleState.Passport(): got tag {passport.Tag}");
                results.Fail("ReticleState_Passport_Tag", $"Got {passport.Tag}");
            }
        }
        catch (DllNotFoundException)
        {
            logger.Fail("ReticleState.Passport: DllNotFoundException (wrapper not compiled)");
            results.Fail("ReticleState_Passport_Tag",
                "DllNotFoundException — BlinkIDUXSwiftBindings wrapper compilation failed (SWIFTBIND051)");
        }
        catch (Exception ex)
        {
            logger.Fail($"ReticleState.Passport: {ex.Message}");
            results.Fail("ReticleState_Passport_Tag", ex.Message);
        }

        // Payload case: InactiveWithMessage(String) — requires wrapper
        try
        {
            var inactive = ReticleState.InactiveWithMessage("Hold still");
            if (inactive.Tag == ReticleState.CaseTag.InactiveWithMessage)
            {
                logger.Pass("ReticleState.InactiveWithMessage() tag correct");
                results.Pass("ReticleState_InactiveWithMessage_Tag");
            }
            else
            {
                logger.Fail($"ReticleState.InactiveWithMessage(): got tag {inactive.Tag}");
                results.Fail("ReticleState_InactiveWithMessage_Tag", $"Got {inactive.Tag}");
            }
        }
        catch (DllNotFoundException)
        {
            logger.Fail("ReticleState.InactiveWithMessage: DllNotFoundException (wrapper not compiled)");
            results.Fail("ReticleState_InactiveWithMessage_Tag",
                "DllNotFoundException — BlinkIDUXSwiftBindings wrapper compilation failed (SWIFTBIND051)");
        }
        catch (Exception ex)
        {
            logger.Fail($"ReticleState.InactiveWithMessage: {ex.Message}");
            results.Fail("ReticleState_InactiveWithMessage_Tag", ex.Message);
        }

        // CaseTag raw values
        try
        {
            if ((uint)ReticleState.CaseTag.Error == 0 &&
                (uint)ReticleState.CaseTag.Passport == 1 &&
                (uint)ReticleState.CaseTag.InactiveWithMessage == 2 &&
                (uint)ReticleState.CaseTag.Front == 3 &&
                (uint)ReticleState.CaseTag.Back == 4 &&
                (uint)ReticleState.CaseTag.Barcode == 5 &&
                (uint)ReticleState.CaseTag.Detecting == 6 &&
                (uint)ReticleState.CaseTag.Flip == 7 &&
                (uint)ReticleState.CaseTag.Inactive == 8)
            {
                logger.Pass("ReticleState CaseTag raw values correct");
                results.Pass("ReticleState_CaseTag_RawValues");
            }
            else
            {
                logger.Fail("ReticleState CaseTag raw values wrong");
                results.Fail("ReticleState_CaseTag_RawValues", "Wrong values");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"ReticleState CaseTag: {ex.Message}");
            results.Fail("ReticleState_CaseTag_RawValues", ex.Message);
        }
    }

    // ==========================================
    // Phase 8: UIEvent Enum (15 cases)
    // ==========================================

    private void RunUIEventTests(TestLogger logger, TestResults results)
    {
        // No-payload cases (13 cases — use DestructiveInjectEnumTag, no wrapper needed)
        var noPayloadCases = new (string name, Func<BUXUIEvent> factory, BUXUIEvent.CaseTag expectedTag)[]
        {
            ("WrongSide", () => BUXUIEvent.WrongSide, BUXUIEvent.CaseTag.WrongSide),
            ("Blur", () => BUXUIEvent.Blur, BUXUIEvent.CaseTag.Blur),
            ("Glare", () => BUXUIEvent.Glare, BUXUIEvent.CaseTag.Glare),
            ("Occlusion", () => BUXUIEvent.Occlusion, BUXUIEvent.CaseTag.Occlusion),
            ("Tilt", () => BUXUIEvent.Tilt, BUXUIEvent.CaseTag.Tilt),
            ("TooClose", () => BUXUIEvent.TooClose, BUXUIEvent.CaseTag.TooClose),
            ("TooFar", () => BUXUIEvent.TooFar, BUXUIEvent.CaseTag.TooFar),
            ("TooCloseToEdge", () => BUXUIEvent.TooCloseToEdge, BUXUIEvent.CaseTag.TooCloseToEdge),
            ("NotFullyVisible", () => BUXUIEvent.NotFullyVisible, BUXUIEvent.CaseTag.NotFullyVisible),
            ("TooDark", () => BUXUIEvent.TooDark, BUXUIEvent.CaseTag.TooDark),
            ("TooBright", () => BUXUIEvent.TooBright, BUXUIEvent.CaseTag.TooBright),
            ("FacePhotoNotFullyVisible", () => BUXUIEvent.FacePhotoNotFullyVisible, BUXUIEvent.CaseTag.FacePhotoNotFullyVisible),
            ("WrongSidePassportWithBarcode", () => BUXUIEvent.WrongSidePassportWithBarcode, BUXUIEvent.CaseTag.WrongSidePassportWithBarcode),
        };

        var tags = new HashSet<uint>();
        foreach (var (name, factory, expectedTag) in noPayloadCases)
        {
            try
            {
                var instance = factory();
                var tag = instance.Tag;
                if (tag == expectedTag)
                {
                    logger.Pass($"UIEvent.{name} tag={tag}");
                    results.Pass($"UIEvent_{name}_Tag");
                    tags.Add((uint)tag);
                }
                else
                {
                    logger.Fail($"UIEvent.{name}: expected {expectedTag}, got {tag}");
                    results.Fail($"UIEvent_{name}_Tag", $"Expected {expectedTag}, got {tag}");
                }
            }
            catch (Exception ex)
            {
                logger.Fail($"UIEvent.{name}: {ex.Message}");
                results.Fail($"UIEvent_{name}_Tag", ex.Message);
            }
        }

        // All no-payload tags unique
        try
        {
            if (tags.Count == noPayloadCases.Length)
            {
                logger.Pass($"UIEvent: all {noPayloadCases.Length} no-payload tags unique");
                results.Pass("UIEvent_NoPayload_TagsUnique");
            }
            else
            {
                logger.Fail($"UIEvent: only {tags.Count}/{noPayloadCases.Length} unique tags");
                results.Fail("UIEvent_NoPayload_TagsUnique", $"Only {tags.Count} unique");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"UIEvent uniqueness: {ex.Message}");
            results.Fail("UIEvent_NoPayload_TagsUnique", ex.Message);
        }

        // Payload case: RequestDocumentSide(DocumentSide) — requires wrapper
        // BUG: BlinkIDUXSwiftBindings wrapper not compiled
        try
        {
            var front = DocumentSide.Front;
            var evt = BUXUIEvent.RequestDocumentSide(front);
            if (evt.Tag == BUXUIEvent.CaseTag.RequestDocumentSide)
            {
                logger.Pass("UIEvent.RequestDocumentSide(Front) tag correct");
                results.Pass("UIEvent_RequestDocumentSide_Tag");
            }
            else
            {
                logger.Fail($"UIEvent.RequestDocumentSide: got {evt.Tag}");
                results.Fail("UIEvent_RequestDocumentSide_Tag", $"Got {evt.Tag}");
            }
        }
        catch (DllNotFoundException)
        {
            logger.Fail("UIEvent.RequestDocumentSide: DllNotFoundException (wrapper not compiled)");
            results.Fail("UIEvent_RequestDocumentSide_Tag",
                "DllNotFoundException — BlinkIDUXSwiftBindings wrapper compilation failed (SWIFTBIND051)");
        }
        catch (Exception ex)
        {
            logger.Fail($"UIEvent.RequestDocumentSide: {ex.Message}");
            results.Fail("UIEvent_RequestDocumentSide_Tag", ex.Message);
        }

        // Payload case: WrongSidePassport(PassportOrientation) — requires wrapper
        try
        {
            var evt = BUXUIEvent.WrongSidePassport(PassportOrientation.Left90);
            if (evt.Tag == BUXUIEvent.CaseTag.WrongSidePassport)
            {
                logger.Pass("UIEvent.WrongSidePassport(Left90) tag correct");
                results.Pass("UIEvent_WrongSidePassport_Tag");
            }
            else
            {
                logger.Fail($"UIEvent.WrongSidePassport: got {evt.Tag}");
                results.Fail("UIEvent_WrongSidePassport_Tag", $"Got {evt.Tag}");
            }
        }
        catch (DllNotFoundException)
        {
            logger.Fail("UIEvent.WrongSidePassport: DllNotFoundException (wrapper not compiled)");
            results.Fail("UIEvent_WrongSidePassport_Tag",
                "DllNotFoundException — BlinkIDUXSwiftBindings wrapper compilation failed (SWIFTBIND051)");
        }
        catch (Exception ex)
        {
            logger.Fail($"UIEvent.WrongSidePassport: {ex.Message}");
            results.Fail("UIEvent_WrongSidePassport_Tag", ex.Message);
        }

        // CaseTag raw values (Swift ordering: payload cases first)
        try
        {
            if ((uint)BUXUIEvent.CaseTag.RequestDocumentSide == 0 &&
                (uint)BUXUIEvent.CaseTag.WrongSidePassport == 1 &&
                (uint)BUXUIEvent.CaseTag.WrongSide == 2 &&
                (uint)BUXUIEvent.CaseTag.Blur == 3 &&
                (uint)BUXUIEvent.CaseTag.Glare == 4 &&
                (uint)BUXUIEvent.CaseTag.Occlusion == 5 &&
                (uint)BUXUIEvent.CaseTag.Tilt == 6 &&
                (uint)BUXUIEvent.CaseTag.TooClose == 7 &&
                (uint)BUXUIEvent.CaseTag.TooFar == 8 &&
                (uint)BUXUIEvent.CaseTag.TooCloseToEdge == 9 &&
                (uint)BUXUIEvent.CaseTag.NotFullyVisible == 10 &&
                (uint)BUXUIEvent.CaseTag.TooDark == 11 &&
                (uint)BUXUIEvent.CaseTag.TooBright == 12 &&
                (uint)BUXUIEvent.CaseTag.FacePhotoNotFullyVisible == 13 &&
                (uint)BUXUIEvent.CaseTag.WrongSidePassportWithBarcode == 14)
            {
                logger.Pass("UIEvent CaseTag raw values correct (all 15)");
                results.Pass("UIEvent_CaseTag_RawValues");
            }
            else
            {
                logger.Fail("UIEvent CaseTag raw values wrong");
                results.Fail("UIEvent_CaseTag_RawValues", "Wrong values");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"UIEvent CaseTag: {ex.Message}");
            results.Fail("UIEvent_CaseTag_RawValues", ex.Message);
        }
    }

    // ==========================================
    // Phase 9: MicroblinkColor Enum (7 cases)
    // ==========================================

    private void RunMicroblinkColorTests(TestLogger logger, TestResults results)
    {
        // MicroblinkColor uses CaseByIndex P/Invoke from BlinkIDUXSwiftBindings
        // BUG: Wrapper xcframework not compiled, so all CaseByIndex calls throw DllNotFoundException
        var cases = new (string name, Func<MicroblinkColor> factory, MicroblinkColor.CaseTag expectedTag)[]
        {
            ("Secondary", () => MicroblinkColor.Secondary, MicroblinkColor.CaseTag.Secondary),
            ("Primary", () => MicroblinkColor.Primary, MicroblinkColor.CaseTag.Primary),
            ("TooltipBackground", () => MicroblinkColor.TooltipBackground, MicroblinkColor.CaseTag.TooltipBackground),
            ("HelpBackground", () => MicroblinkColor.HelpBackground, MicroblinkColor.CaseTag.HelpBackground),
            ("Background", () => MicroblinkColor.Background, MicroblinkColor.CaseTag.Background),
            ("NeedHelpTooltipBackground", () => MicroblinkColor.NeedHelpTooltipBackground, MicroblinkColor.CaseTag.NeedHelpTooltipBackground),
            ("ToastBackgroundColor", () => MicroblinkColor.ToastBackgroundColor, MicroblinkColor.CaseTag.ToastBackgroundColor),
        };

        foreach (var (name, factory, expectedTag) in cases)
        {
            try
            {
                var instance = factory();
                var tag = instance.Tag;
                if (tag == expectedTag)
                {
                    logger.Pass($"MicroblinkColor.{name} tag={tag}");
                    results.Pass($"MicroblinkColor_{name}_Tag");
                }
                else
                {
                    logger.Fail($"MicroblinkColor.{name}: expected {expectedTag}, got {tag}");
                    results.Fail($"MicroblinkColor_{name}_Tag", $"Expected {expectedTag}, got {tag}");
                }
            }
            catch (DllNotFoundException)
            {
                // BUG: CaseByIndex requires wrapper xcframework which failed to compile
                logger.Fail($"MicroblinkColor.{name}: DllNotFoundException (wrapper not compiled)");
                results.Fail($"MicroblinkColor_{name}_Tag",
                    "DllNotFoundException — BlinkIDUXSwiftBindings CaseByIndex requires wrapper (SWIFTBIND051)");
            }
            catch (Exception ex)
            {
                logger.Fail($"MicroblinkColor.{name}: {ex.Message}");
                results.Fail($"MicroblinkColor_{name}_Tag", ex.Message);
            }
        }

        // CaseTag raw values (no P/Invoke needed)
        try
        {
            if ((uint)MicroblinkColor.CaseTag.Secondary == 0 &&
                (uint)MicroblinkColor.CaseTag.Primary == 1 &&
                (uint)MicroblinkColor.CaseTag.TooltipBackground == 2 &&
                (uint)MicroblinkColor.CaseTag.HelpBackground == 3 &&
                (uint)MicroblinkColor.CaseTag.Background == 4 &&
                (uint)MicroblinkColor.CaseTag.NeedHelpTooltipBackground == 5 &&
                (uint)MicroblinkColor.CaseTag.ToastBackgroundColor == 6)
            {
                logger.Pass("MicroblinkColor CaseTag raw values correct (all 7)");
                results.Pass("MicroblinkColor_CaseTag_RawValues");
            }
            else
            {
                logger.Fail("MicroblinkColor CaseTag raw values wrong");
                results.Fail("MicroblinkColor_CaseTag_RawValues", "Wrong values");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"MicroblinkColor CaseTag: {ex.Message}");
            results.Fail("MicroblinkColor_CaseTag_RawValues", ex.Message);
        }

        // RawValue — requires wrapper
        try
        {
            var secondary = MicroblinkColor.Secondary;
            var rawValue = secondary.RawValue;
            logger.Pass($"MicroblinkColor.Secondary.RawValue = '{rawValue}'");
            results.Pass("MicroblinkColor_Secondary_RawValue");
        }
        catch (DllNotFoundException)
        {
            logger.Fail("MicroblinkColor.Secondary.RawValue: DllNotFoundException (wrapper not compiled)");
            results.Fail("MicroblinkColor_Secondary_RawValue",
                "DllNotFoundException — wrapper required for RawValue getter (SWIFTBIND051)");
        }
        catch (Exception ex)
        {
            logger.Fail($"MicroblinkColor.RawValue: {ex.Message}");
            results.Fail("MicroblinkColor_Secondary_RawValue", ex.Message);
        }

        // FromRawValue — requires wrapper
        try
        {
            var color = MicroblinkColor.FromRawValue("secondary");
            if (color != null)
            {
                logger.Pass("MicroblinkColor.FromRawValue('secondary') returned non-null");
                results.Pass("MicroblinkColor_FromRawValue");
            }
            else
            {
                logger.Fail("MicroblinkColor.FromRawValue('secondary') returned null");
                results.Fail("MicroblinkColor_FromRawValue", "Returned null");
            }
        }
        catch (DllNotFoundException)
        {
            logger.Fail("MicroblinkColor.FromRawValue: DllNotFoundException (wrapper not compiled)");
            results.Fail("MicroblinkColor_FromRawValue",
                "DllNotFoundException — wrapper required for FromRawValue (SWIFTBIND051)");
        }
        catch (Exception ex)
        {
            logger.Fail($"MicroblinkColor.FromRawValue: {ex.Message}");
            results.Fail("MicroblinkColor_FromRawValue", ex.Message);
        }
    }

    // ==========================================
    // Phase 10: CaptureMode Enum
    // ==========================================

    private void RunCaptureModeTests(TestLogger logger, TestResults results)
    {
        // CaptureMode.Video — uses CaseByIndex P/Invoke from wrapper
        // BUG: Wrapper not compiled
        try
        {
            var video = CaptureMode.Video;
            if (video.Tag == CaptureMode.CaseTag.Video)
            {
                logger.Pass("CaptureMode.Video tag correct");
                results.Pass("CaptureMode_Video_Tag");
            }
            else
            {
                logger.Fail($"CaptureMode.Video: got tag {video.Tag}");
                results.Fail("CaptureMode_Video_Tag", $"Got {video.Tag}");
            }
        }
        catch (DllNotFoundException)
        {
            logger.Fail("CaptureMode.Video: DllNotFoundException (wrapper not compiled)");
            results.Fail("CaptureMode_Video_Tag",
                "DllNotFoundException — BlinkIDUXSwiftBindings CaseByIndex requires wrapper (SWIFTBIND051)");
        }
        catch (Exception ex)
        {
            logger.Fail($"CaptureMode.Video: {ex.Message}");
            results.Fail("CaptureMode_Video_Tag", ex.Message);
        }

        // CaseTag raw value (no P/Invoke)
        try
        {
            if ((uint)CaptureMode.CaseTag.Video == 0)
            {
                logger.Pass("CaptureMode.CaseTag.Video == 0");
                results.Pass("CaptureMode_CaseTag_RawValue");
            }
            else
            {
                logger.Fail($"CaptureMode.CaseTag.Video = {(uint)CaptureMode.CaseTag.Video}");
                results.Fail("CaptureMode_CaseTag_RawValue", "Wrong value");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"CaptureMode CaseTag: {ex.Message}");
            results.Fail("CaptureMode_CaseTag_RawValue", ex.Message);
        }

        // RawValue — requires wrapper
        try
        {
            var video = CaptureMode.Video;
            var raw = video.RawValue;
            logger.Pass($"CaptureMode.Video.RawValue = '{raw}'");
            results.Pass("CaptureMode_Video_RawValue");
        }
        catch (DllNotFoundException)
        {
            logger.Fail("CaptureMode.Video.RawValue: DllNotFoundException (wrapper not compiled)");
            results.Fail("CaptureMode_Video_RawValue",
                "DllNotFoundException — wrapper required (SWIFTBIND051)");
        }
        catch (Exception ex)
        {
            logger.Fail($"CaptureMode.RawValue: {ex.Message}");
            results.Fail("CaptureMode_Video_RawValue", ex.Message);
        }

        // AllCases — requires wrapper
        try
        {
            var allCases = CaptureMode.AllCases;
            if (allCases.Count > 0)
            {
                logger.Pass($"CaptureMode.AllCases count = {allCases.Count}");
                results.Pass("CaptureMode_AllCases");
            }
            else
            {
                logger.Fail("CaptureMode.AllCases is empty");
                results.Fail("CaptureMode_AllCases", "Empty");
            }
        }
        catch (DllNotFoundException)
        {
            logger.Fail("CaptureMode.AllCases: DllNotFoundException (wrapper not compiled)");
            results.Fail("CaptureMode_AllCases",
                "DllNotFoundException — wrapper required (SWIFTBIND051)");
        }
        catch (Exception ex)
        {
            logger.Fail($"CaptureMode.AllCases: {ex.Message}");
            results.Fail("CaptureMode_AllCases", ex.Message);
        }

        // FromRawValue — requires wrapper
        try
        {
            var mode = CaptureMode.FromRawValue("video");
            if (mode != null)
            {
                logger.Pass("CaptureMode.FromRawValue('video') returned non-null");
                results.Pass("CaptureMode_FromRawValue");
            }
            else
            {
                logger.Fail("CaptureMode.FromRawValue('video') returned null");
                results.Fail("CaptureMode_FromRawValue", "Returned null");
            }
        }
        catch (DllNotFoundException)
        {
            logger.Fail("CaptureMode.FromRawValue: DllNotFoundException (wrapper not compiled)");
            results.Fail("CaptureMode_FromRawValue",
                "DllNotFoundException — wrapper required (SWIFTBIND051)");
        }
        catch (Exception ex)
        {
            logger.Fail($"CaptureMode.FromRawValue: {ex.Message}");
            results.Fail("CaptureMode_FromRawValue", ex.Message);
        }

        // FromRawValue with invalid string should return null — requires wrapper
        try
        {
            var mode = CaptureMode.FromRawValue("nonexistent");
            if (mode == null)
            {
                logger.Pass("CaptureMode.FromRawValue('nonexistent') correctly returned null");
                results.Pass("CaptureMode_FromRawValue_Invalid");
            }
            else
            {
                logger.Fail("CaptureMode.FromRawValue('nonexistent') should return null");
                results.Fail("CaptureMode_FromRawValue_Invalid", "Returned non-null for invalid value");
            }
        }
        catch (DllNotFoundException)
        {
            logger.Fail("CaptureMode.FromRawValue(invalid): DllNotFoundException (wrapper not compiled)");
            results.Fail("CaptureMode_FromRawValue_Invalid",
                "DllNotFoundException — wrapper required (SWIFTBIND051)");
        }
        catch (Exception ex)
        {
            logger.Fail($"CaptureMode.FromRawValue(invalid): {ex.Message}");
            results.Fail("CaptureMode_FromRawValue_Invalid", ex.Message);
        }
    }

    // ==========================================
    // Phase 11: ScanningResult<T,U> Generic Enum
    // ==========================================

    private void RunScanningResultTests(TestLogger logger, TestResults results)
    {
        // ScanningResult is generic: ScanningResult<T, U> where T, U : ISwiftObject
        // Test no-payload cases (Cancelled, Ended) — these use DestructiveInjectEnumTag
        // We need concrete type args. Use BlinkIDResultState and UIEvent as T and U.

        // Cancelled case
        try
        {
            var cancelled = ScanningResult<BlinkIDResultState, BUXUIEvent>.Cancelled;
            if (cancelled.Tag == ScanningResult<BlinkIDResultState, BUXUIEvent>.CaseTag.Cancelled)
            {
                logger.Pass("ScanningResult<..>.Cancelled tag correct");
                results.Pass("ScanningResult_Cancelled_Tag");
            }
            else
            {
                logger.Fail($"ScanningResult.Cancelled: got tag {cancelled.Tag}");
                results.Fail("ScanningResult_Cancelled_Tag", $"Got {cancelled.Tag}");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"ScanningResult.Cancelled: {ex.Message}");
            results.Fail("ScanningResult_Cancelled_Tag", ex.Message);
        }

        // Ended case
        try
        {
            var ended = ScanningResult<BlinkIDResultState, BUXUIEvent>.Ended;
            if (ended.Tag == ScanningResult<BlinkIDResultState, BUXUIEvent>.CaseTag.Ended)
            {
                logger.Pass("ScanningResult<..>.Ended tag correct");
                results.Pass("ScanningResult_Ended_Tag");
            }
            else
            {
                logger.Fail($"ScanningResult.Ended: got tag {ended.Tag}");
                results.Fail("ScanningResult_Ended_Tag", $"Got {ended.Tag}");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"ScanningResult.Ended: {ex.Message}");
            results.Fail("ScanningResult_Ended_Tag", ex.Message);
        }

        // Cancelled != Ended
        try
        {
            var cancelled = ScanningResult<BlinkIDResultState, BUXUIEvent>.Cancelled;
            var ended = ScanningResult<BlinkIDResultState, BUXUIEvent>.Ended;
            if (cancelled.Tag != ended.Tag)
            {
                logger.Pass("ScanningResult Cancelled != Ended");
                results.Pass("ScanningResult_CancelledNotEnded");
            }
            else
            {
                logger.Fail("ScanningResult Cancelled == Ended");
                results.Fail("ScanningResult_CancelledNotEnded", "Same tag");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"ScanningResult Cancelled/Ended: {ex.Message}");
            results.Fail("ScanningResult_CancelledNotEnded", ex.Message);
        }

        // CaseTag raw values
        try
        {
            if ((uint)ScanningResult<BlinkIDResultState, BUXUIEvent>.CaseTag.Completed == 0 &&
                (uint)ScanningResult<BlinkIDResultState, BUXUIEvent>.CaseTag.Interrupted == 1 &&
                (uint)ScanningResult<BlinkIDResultState, BUXUIEvent>.CaseTag.Cancelled == 2 &&
                (uint)ScanningResult<BlinkIDResultState, BUXUIEvent>.CaseTag.Ended == 3)
            {
                logger.Pass("ScanningResult CaseTag raw values correct");
                results.Pass("ScanningResult_CaseTag_RawValues");
            }
            else
            {
                logger.Fail("ScanningResult CaseTag raw values wrong");
                results.Fail("ScanningResult_CaseTag_RawValues", "Wrong values");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"ScanningResult CaseTag: {ex.Message}");
            results.Fail("ScanningResult_CaseTag_RawValues", ex.Message);
        }

        // TryGetCompleted on Cancelled case should return false
        try
        {
            var cancelled = ScanningResult<BlinkIDResultState, BUXUIEvent>.Cancelled;
            if (!cancelled.TryGetCompleted(out _))
            {
                logger.Pass("ScanningResult.Cancelled.TryGetCompleted correctly returns false");
                results.Pass("ScanningResult_Cancelled_TryGetCompleted_False");
            }
            else
            {
                logger.Fail("ScanningResult.Cancelled.TryGetCompleted should return false");
                results.Fail("ScanningResult_Cancelled_TryGetCompleted_False", "Returned true");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"ScanningResult TryGetCompleted: {ex.Message}");
            results.Fail("ScanningResult_Cancelled_TryGetCompleted_False", ex.Message);
        }

        // TryGetInterrupted on Cancelled case should return false
        try
        {
            var cancelled = ScanningResult<BlinkIDResultState, BUXUIEvent>.Cancelled;
            if (!cancelled.TryGetInterrupted(out _))
            {
                logger.Pass("ScanningResult.Cancelled.TryGetInterrupted correctly returns false");
                results.Pass("ScanningResult_Cancelled_TryGetInterrupted_False");
            }
            else
            {
                logger.Fail("ScanningResult.Cancelled.TryGetInterrupted should return false");
                results.Fail("ScanningResult_Cancelled_TryGetInterrupted_False", "Returned true");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"ScanningResult TryGetInterrupted: {ex.Message}");
            results.Fail("ScanningResult_Cancelled_TryGetInterrupted_False", ex.Message);
        }

        // Generic metadata resolution
        try
        {
            var metadata = SwiftObjectHelper<ScanningResult<BlinkIDResultState, BUXUIEvent>>.GetTypeMetadata();
            if (metadata.Size > 0)
            {
                logger.Pass($"ScanningResult<BlinkIDResultState, BUXUIEvent> metadata (size={metadata.Size})");
                results.Pass("ScanningResult_GenericMetadata");
            }
            else
            {
                logger.Fail("ScanningResult generic metadata: size is 0");
                results.Fail("ScanningResult_GenericMetadata", "Size is 0");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"ScanningResult generic metadata: {ex.Message}");
            results.Fail("ScanningResult_GenericMetadata", ex.Message);
        }
    }

    // ==========================================
    // Phase 12: Class Constructors (wrapper-dependent)
    // ==========================================

    private void RunClassConstructorTests(TestLogger logger, TestResults results)
    {
        // BlinkIDEventStream constructor — requires wrapper
        // BUG: BlinkIDUXSwiftBindings wrapper not compiled
        try
        {
            using var stream = new BlinkIDEventStream();
            logger.Pass("BlinkIDEventStream() constructor succeeded");
            results.Pass("BlinkIDEventStream_Constructor");
        }
        catch (DllNotFoundException)
        {
            logger.Fail("BlinkIDEventStream(): DllNotFoundException (wrapper not compiled)");
            results.Fail("BlinkIDEventStream_Constructor",
                "DllNotFoundException — wrapper required for constructor (SWIFTBIND051)");
        }
        catch (Exception ex)
        {
            logger.Fail($"BlinkIDEventStream(): {ex.Message}");
            results.Fail("BlinkIDEventStream_Constructor", ex.Message);
        }

        // NetworkMonitor constructor — requires wrapper
        try
        {
            using var monitor = new NetworkMonitor();
            logger.Pass("NetworkMonitor() constructor succeeded");
            results.Pass("NetworkMonitor_Constructor");
        }
        catch (DllNotFoundException)
        {
            logger.Fail("NetworkMonitor(): DllNotFoundException (wrapper not compiled)");
            results.Fail("NetworkMonitor_Constructor",
                "DllNotFoundException — wrapper required for constructor (SWIFTBIND051)");
        }
        catch (Exception ex)
        {
            logger.Fail($"NetworkMonitor(): {ex.Message}");
            results.Fail("NetworkMonitor_Constructor", ex.Message);
        }

        // BlinkIDTheme.Shared — requires wrapper AND resource bundle
        // BUG: Even if wrapper compiled, accessing Shared triggers resource_bundle_accessor.swift
        // which fatally crashes when the BlinkIDUX_BlinkIDUX bundle isn't in the app container.
        try
        {
            var theme = BlinkIDTheme.Shared;
            logger.Pass("BlinkIDTheme.Shared singleton retrieved");
            results.Pass("BlinkIDTheme_Shared");
        }
        catch (DllNotFoundException)
        {
            // BUG: Wrapper not compiled
            logger.Fail("BlinkIDTheme.Shared: DllNotFoundException (wrapper not compiled)");
            results.Fail("BlinkIDTheme_Shared",
                "DllNotFoundException — wrapper required for Shared singleton (SWIFTBIND051)");
        }
        catch (Exception ex)
        {
            // May be a fatal crash from resource_bundle_accessor
            logger.Fail($"BlinkIDTheme.Shared: {ex.Message}");
            results.Fail("BlinkIDTheme_Shared", ex.Message);
        }
    }

    // ==========================================
    // Phase 13: Struct Constructors (wrapper-dependent)
    // ==========================================

    private void RunStructConstructorTests(TestLogger logger, TestResults results)
    {
        // ScanningUXSettings constructor with default params — requires wrapper
        // BUG: BlinkIDUXSwiftBindings wrapper not compiled
        try
        {
            using var settings = new ScanningUXSettings();
            logger.Pass("ScanningUXSettings() default constructor succeeded");
            results.Pass("ScanningUXSettings_DefaultConstructor");
        }
        catch (DllNotFoundException)
        {
            logger.Fail("ScanningUXSettings(): DllNotFoundException (wrapper not compiled)");
            results.Fail("ScanningUXSettings_DefaultConstructor",
                "DllNotFoundException — wrapper required for constructor (SWIFTBIND051)");
        }
        catch (Exception ex)
        {
            logger.Fail($"ScanningUXSettings(): {ex.Message}");
            results.Fail("ScanningUXSettings_DefaultConstructor", ex.Message);
        }

        // ScanningUXSettings with explicit params — requires wrapper
        try
        {
            using var settings = new ScanningUXSettings(
                showIntroductionAlert: false,
                showHelpButton: true,
                preferredCameraPosition: Camera.CameraPosition.Front,
                allowHapticFeedback: false);
            logger.Pass("ScanningUXSettings(params) constructor succeeded");
            results.Pass("ScanningUXSettings_ParamConstructor");
        }
        catch (DllNotFoundException)
        {
            logger.Fail("ScanningUXSettings(params): DllNotFoundException (wrapper not compiled)");
            results.Fail("ScanningUXSettings_ParamConstructor",
                "DllNotFoundException — wrapper required for constructor (SWIFTBIND051)");
        }
        catch (Exception ex)
        {
            logger.Fail($"ScanningUXSettings(params): {ex.Message}");
            results.Fail("ScanningUXSettings_ParamConstructor", ex.Message);
        }

        // ScanningUXSettings with non-default camera position — requires wrapper
        try
        {
            using var settings = new ScanningUXSettings(
                preferredCameraPosition: Camera.CameraPosition.Back);
            logger.Pass("ScanningUXSettings(cameraPosition:Back) succeeded");
            results.Pass("ScanningUXSettings_BackCamera");
        }
        catch (DllNotFoundException)
        {
            logger.Fail("ScanningUXSettings(Back): DllNotFoundException (wrapper not compiled)");
            results.Fail("ScanningUXSettings_BackCamera",
                "DllNotFoundException — wrapper required (SWIFTBIND051)");
        }
        catch (Exception ex)
        {
            logger.Fail($"ScanningUXSettings(Back): {ex.Message}");
            results.Fail("ScanningUXSettings_BackCamera", ex.Message);
        }
    }

    // ==========================================
    // Phase 14: Property Access (wrapper-dependent)
    // ==========================================

    private void RunPropertyAccessTests(TestLogger logger, TestResults results)
    {
        // BlinkIDResultState.ScanningResult — cross-module property (BlinkID.BlinkIDScanningResult?)
        // This tests the cross-module type reference: BlinkIDUX type returning BlinkID type
        // Requires wrapper for the property getter P/Invoke
        // BUG: Wrapper not compiled
        // NOTE: Can't construct BlinkIDResultState directly (no public constructor),
        // so we skip this test as it would require an instance from elsewhere
        logger.Skip("BlinkIDResultState.ScanningResult: requires instance (no public constructor) and wrapper");
        results.Skip("BlinkIDResultState_ScanningResult",
            "No public constructor and wrapper not compiled (SWIFTBIND051)");

        // NetworkMonitor.IsConnected — requires wrapper for constructor + getter
        try
        {
            var monitor = new NetworkMonitor();
            var connected = monitor.IsConnected;
            logger.Pass($"NetworkMonitor.IsConnected = {connected}");
            results.Pass("NetworkMonitor_IsConnected");
            monitor.Dispose();
        }
        catch (DllNotFoundException)
        {
            logger.Fail("NetworkMonitor.IsConnected: DllNotFoundException (wrapper not compiled)");
            results.Fail("NetworkMonitor_IsConnected",
                "DllNotFoundException — wrapper required (SWIFTBIND051)");
        }
        catch (Exception ex)
        {
            logger.Fail($"NetworkMonitor.IsConnected: {ex.Message}");
            results.Fail("NetworkMonitor_IsConnected", ex.Message);
        }

        // NetworkMonitor.IsOffline — requires wrapper
        try
        {
            var monitor = new NetworkMonitor();
            var offline = monitor.IsOffline;
            logger.Pass($"NetworkMonitor.IsOffline = {offline}");
            results.Pass("NetworkMonitor_IsOffline");
            monitor.Dispose();
        }
        catch (DllNotFoundException)
        {
            logger.Fail("NetworkMonitor.IsOffline: DllNotFoundException (wrapper not compiled)");
            results.Fail("NetworkMonitor_IsOffline",
                "DllNotFoundException — wrapper required (SWIFTBIND051)");
        }
        catch (Exception ex)
        {
            logger.Fail($"NetworkMonitor.IsOffline: {ex.Message}");
            results.Fail("NetworkMonitor_IsOffline", ex.Message);
        }

        // BlinkIDScanningAlertType implements IAlertTypeProtocol
        try
        {
            var timeout = BlinkIDScanningAlertType.Timeout;
            if (timeout is IAlertTypeProtocol proto)
            {
                logger.Pass("BlinkIDScanningAlertType implements IAlertTypeProtocol");
                results.Pass("BlinkIDScanningAlertType_IAlertTypeProtocol");
            }
            else
            {
                logger.Fail("BlinkIDScanningAlertType does not implement IAlertTypeProtocol");
                results.Fail("BlinkIDScanningAlertType_IAlertTypeProtocol", "Cast failed");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"BlinkIDScanningAlertType protocol check: {ex.Message}");
            results.Fail("BlinkIDScanningAlertType_IAlertTypeProtocol", ex.Message);
        }

        // BlinkIDTheme implements IUXThemeProtocol (check via typeof, not instance)
        try
        {
            var implements = typeof(IUXThemeProtocol).IsAssignableFrom(typeof(BlinkIDTheme));
            if (implements)
            {
                logger.Pass("BlinkIDTheme implements IUXThemeProtocol");
                results.Pass("BlinkIDTheme_IUXThemeProtocol");
            }
            else
            {
                logger.Fail("BlinkIDTheme does not implement IUXThemeProtocol");
                results.Fail("BlinkIDTheme_IUXThemeProtocol", "Not assignable");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"BlinkIDTheme protocol check: {ex.Message}");
            results.Fail("BlinkIDTheme_IUXThemeProtocol", ex.Message);
        }

        // BlinkIDScanningAlertType.ToString() — overridden to call Description
        // Requires wrapper for Description property
        try
        {
            var timeout = BlinkIDScanningAlertType.Timeout;
            var str = timeout.ToString();
            if (str != null && str.Length > 0)
            {
                logger.Pass($"BlinkIDScanningAlertType.Timeout.ToString() = '{str}'");
                results.Pass("BlinkIDScanningAlertType_ToString");
            }
            else
            {
                logger.Fail("BlinkIDScanningAlertType.Timeout.ToString() is null or empty");
                results.Fail("BlinkIDScanningAlertType_ToString", "Null or empty");
            }
        }
        catch (DllNotFoundException)
        {
            logger.Fail("BlinkIDScanningAlertType.ToString: DllNotFoundException (wrapper not compiled)");
            results.Fail("BlinkIDScanningAlertType_ToString",
                "DllNotFoundException — ToString calls Description which requires wrapper (SWIFTBIND051)");
        }
        catch (Exception ex)
        {
            logger.Fail($"BlinkIDScanningAlertType.ToString: {ex.Message}");
            results.Fail("BlinkIDScanningAlertType_ToString", ex.Message);
        }
    }

    // ==========================================
    // Phase 15: Protocol Interfaces
    // ==========================================

    private void RunProtocolInterfaceTests(TestLogger logger, TestResults results)
    {
        // Verify all protocol interfaces exist as C# types
        var interfaces = new (string name, Type type)[]
        {
            ("IBlinkIDClassFilter", typeof(IBlinkIDClassFilter)),
            ("ICameraModel", typeof(ICameraModel)),
            ("IPreviewSource", typeof(IPreviewSource)),
            ("IPreviewTarget", typeof(IPreviewTarget)),
            ("IAlertTypeProtocol", typeof(IAlertTypeProtocol)),
            ("IUXThemeProtocol", typeof(IUXThemeProtocol)),
        };

        foreach (var (name, type) in interfaces)
        {
            try
            {
                if (type.IsInterface)
                {
                    logger.Pass($"{name} is a valid C# interface");
                    results.Pass($"Protocol_{name}_Exists");
                }
                else
                {
                    logger.Fail($"{name} is not an interface");
                    results.Fail($"Protocol_{name}_Exists", "Not an interface");
                }
            }
            catch (Exception ex)
            {
                logger.Fail($"{name}: {ex.Message}");
                results.Fail($"Protocol_{name}_Exists", ex.Message);
            }
        }

        // Verify protocol proxies exist
        var proxies = new (string name, Type type, Type protocol)[]
        {
            ("BlinkIDClassFilterProxy", typeof(BlinkIDUX.SwiftInterop.BlinkIDClassFilterProxy), typeof(IBlinkIDClassFilter)),
            ("CameraModelProxy", typeof(BlinkIDUX.SwiftInterop.CameraModelProxy), typeof(ICameraModel)),
            ("PreviewSourceProxy", typeof(BlinkIDUX.SwiftInterop.PreviewSourceProxy), typeof(IPreviewSource)),
            ("PreviewTargetProxy", typeof(BlinkIDUX.SwiftInterop.PreviewTargetProxy), typeof(IPreviewTarget)),
            ("AlertTypeProtocolProxy", typeof(BlinkIDUX.SwiftInterop.AlertTypeProtocolProxy), typeof(IAlertTypeProtocol)),
            ("UXThemeProtocolProxy", typeof(BlinkIDUX.SwiftInterop.UXThemeProtocolProxy), typeof(IUXThemeProtocol)),
        };

        foreach (var (name, proxyType, protocolType) in proxies)
        {
            try
            {
                if (protocolType.IsAssignableFrom(proxyType))
                {
                    logger.Pass($"{name} implements {protocolType.Name}");
                    results.Pass($"Proxy_{name}_ImplementsProtocol");
                }
                else
                {
                    logger.Fail($"{name} does not implement {protocolType.Name}");
                    results.Fail($"Proxy_{name}_ImplementsProtocol", "Interface not implemented");
                }
            }
            catch (Exception ex)
            {
                logger.Fail($"{name}: {ex.Message}");
                results.Fail($"Proxy_{name}_ImplementsProtocol", ex.Message);
            }
        }

        // Verify ISwiftObject is implemented by key types
        var swiftObjectTypes = new (string name, Type type)[]
        {
            ("BlinkIDEventStream", typeof(BlinkIDEventStream)),
            ("BlinkIDAnalyzer", typeof(BlinkIDAnalyzer)),
            ("BlinkIDResultState", typeof(BlinkIDResultState)),
            ("BlinkIDScanningAlertType", typeof(BlinkIDScanningAlertType)),
            ("BlinkIDUXModel", typeof(BlinkIDUXModel)),
            ("BlinkIDTheme", typeof(BlinkIDTheme)),
            ("DocumentSide", typeof(DocumentSide)),
            ("Camera", typeof(Camera)),
            ("CameraStatus", typeof(CameraStatus)),
            ("CaptureMode", typeof(CaptureMode)),
            ("NetworkMonitor", typeof(NetworkMonitor)),
            ("ScanningUXSettings", typeof(ScanningUXSettings)),
            ("MicroblinkColor", typeof(MicroblinkColor)),
            ("ReticleState", typeof(ReticleState)),
            ("UIEvent", typeof(BUXUIEvent)),
        };

        foreach (var (name, type) in swiftObjectTypes)
        {
            try
            {
                if (typeof(ISwiftObject).IsAssignableFrom(type))
                {
                    logger.Pass($"{name} implements ISwiftObject");
                    results.Pass($"ISwiftObject_{name}");
                }
                else
                {
                    logger.Fail($"{name} does not implement ISwiftObject");
                    results.Fail($"ISwiftObject_{name}", "Not ISwiftObject");
                }
            }
            catch (Exception ex)
            {
                logger.Fail($"{name} ISwiftObject check: {ex.Message}");
                results.Fail($"ISwiftObject_{name}", ex.Message);
            }
        }

        // IDisposable on all bound types
        foreach (var (name, type) in swiftObjectTypes)
        {
            try
            {
                if (typeof(IDisposable).IsAssignableFrom(type))
                {
                    logger.Pass($"{name} implements IDisposable");
                    results.Pass($"IDisposable_{name}");
                }
                else
                {
                    logger.Fail($"{name} does not implement IDisposable");
                    results.Fail($"IDisposable_{name}", "Not IDisposable");
                }
            }
            catch (Exception ex)
            {
                logger.Fail($"{name} IDisposable check: {ex.Message}");
                results.Fail($"IDisposable_{name}", ex.Message);
            }
        }
    }
}

#endregion
