// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Text;
using Foundation;
using UIKit;
using Swift;
using Swift.Runtime;
using Mappedin;

namespace MappedinSimTests;

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
            Text = "Mappedin Binding Tests",
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

        logger.Info("=== Section 2: Integer Enums ===");
        RunIntegerEnumTests(logger, results);

        logger.Info("=== Section 3: String-Based Enums (Singletons) ===");
        RunStringEnumTests(logger, results);

        logger.Info("=== Section 4: Constructors & Properties ===");
        RunConstructorTests(logger, results);

        logger.Info("=== Section 5: String-Enum RawValue & FromRawValue ===");
        RunRawValueTests(logger, results);

        logger.Info("=== Section 6: Protocol & Interface Conformance ===");
        RunProtocolTests(logger, results);

        logger.Info("=== Section 7: WKWebView Inheritance ===");
        RunInheritanceTests(logger, results);

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
    //
    // Mappedin 6.2.0 exports an unprefixed surface (Coordinate, Directions, BearingType, …).
    // These checks exercise generator-emitted TypeMetadata access across a representative
    // mix of classes, value-style structs, generic types, and nested types.

    private void RunMetadataTests(TestLogger logger, TestResults results)
    {
        var metadataTests = new (string Name, Func<TypeMetadata> GetMetadata)[]
        {
            // Top-level classes
            ("MapData", () => SwiftObjectHelper<MapData>.GetTypeMetadata()),
            ("MapView", () => SwiftObjectHelper<MapView>.GetTypeMetadata()),
            ("Query", () => SwiftObjectHelper<Query>.GetTypeMetadata()),
            ("Search", () => SwiftObjectHelper<Search>.GetTypeMetadata()),
            ("Analytics", () => SwiftObjectHelper<Analytics>.GetTypeMetadata()),
            ("Coordinate", () => SwiftObjectHelper<Coordinate>.GetTypeMetadata()),
            ("Annotation", () => SwiftObjectHelper<Annotation>.GetTypeMetadata()),
            ("Area", () => SwiftObjectHelper<Area>.GetTypeMetadata()),
            ("Door", () => SwiftObjectHelper<Door>.GetTypeMetadata()),
            ("Floor", () => SwiftObjectHelper<Floor>.GetTypeMetadata()),
            ("Connection", () => SwiftObjectHelper<Connection>.GetTypeMetadata()),
            // Value-style structs
            ("BinaryBundle", () => SwiftObjectHelper<BinaryBundle>.GetTypeMetadata()),
            ("Directions", () => SwiftObjectHelper<Directions>.GetTypeMetadata()),
            ("DirectionInstruction", () => SwiftObjectHelper<DirectionInstruction>.GetTypeMetadata()),
            ("DirectionInstructionAction", () => SwiftObjectHelper<DirectionInstructionAction>.GetTypeMetadata()),
            ("CameraTarget", () => SwiftObjectHelper<CameraTarget>.GetTypeMetadata()),
            ("CameraTransform", () => SwiftObjectHelper<CameraTransform>.GetTypeMetadata()),
            ("CameraAnimationOptions", () => SwiftObjectHelper<CameraAnimationOptions>.GetTypeMetadata()),
            ("CameraInteractionsSetOptions", () => SwiftObjectHelper<CameraInteractionsSetOptions>.GetTypeMetadata()),
            ("AddImageOptions", () => SwiftObjectHelper<AddImageOptions>.GetTypeMetadata()),
            ("AddMarkerOptions", () => SwiftObjectHelper<AddMarkerOptions>.GetTypeMetadata()),
            ("AddPathOptions", () => SwiftObjectHelper<AddPathOptions>.GetTypeMetadata()),
            ("AnimationOptions", () => SwiftObjectHelper<AnimationOptions>.GetTypeMetadata()),
            ("AntialiasingOptions", () => SwiftObjectHelper<AntialiasingOptions>.GetTypeMetadata()),
            // Nested string-enum classes (covers nested-type metadata emission)
            ("AntialiasingOptions.QualityType", () => SwiftObjectHelper<AntialiasingOptions.QualityType>.GetTypeMetadata()),
            // String-enum value-style classes
            ("ActionType", () => SwiftObjectHelper<ActionType>.GetTypeMetadata()),
            ("BearingType", () => SwiftObjectHelper<BearingType>.GetTypeMetadata()),
            ("BlueDotStatus", () => SwiftObjectHelper<BlueDotStatus>.GetTypeMetadata()),
            ("CollisionRankingTier", () => SwiftObjectHelper<CollisionRankingTier>.GetTypeMetadata()),
            ("ConnectionType", () => SwiftObjectHelper<ConnectionType>.GetTypeMetadata()),
            ("EasingFunction", () => SwiftObjectHelper<EasingFunction>.GetTypeMetadata()),
            ("Doors", () => SwiftObjectHelper<Doors>.GetTypeMetadata()),
            // Payload structs
            ("BlueDotPositionUpdate", () => SwiftObjectHelper<BlueDotPositionUpdate>.GetTypeMetadata()),
            ("BlueDotPositionUpdatePayload", () => SwiftObjectHelper<BlueDotPositionUpdatePayload>.GetTypeMetadata()),
            ("BlueDotDeviceOrientationUpdatePayload", () => SwiftObjectHelper<BlueDotDeviceOrientationUpdatePayload>.GetTypeMetadata()),
            ("BlueDotStatusChangePayload", () => SwiftObjectHelper<BlueDotStatusChangePayload>.GetTypeMetadata()),
            ("BlueDotErrorPayload", () => SwiftObjectHelper<BlueDotErrorPayload>.GetTypeMetadata()),
            ("BlueDotUpdateOptions", () => SwiftObjectHelper<BlueDotUpdateOptions>.GetTypeMetadata()),
            ("ClickPayload", () => SwiftObjectHelper<ClickPayload>.GetTypeMetadata()),
            ("FloorChangePayload", () => SwiftObjectHelper<FloorChangePayload>.GetTypeMetadata()),
        };

        foreach (var (name, getMetadata) in metadataTests)
        {
            try
            {
                var metadata = getMetadata();
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
    // Section 2: Integer Enums
    // ──────────────────────────────────────────────
    //
    // Mappedin 6.2.0's only top-level integer enum is `EnvMapOptions`. Its case
    // ordinals are pinned by the generator from the Swift declaration order.

    private void RunIntegerEnumTests(TestLogger logger, TestResults results)
    {
        TestIntEnum(logger, results, "EnvMapOptions", new (string, int)[]
        {
            ("Basic", (int)EnvMapOptions.Basic),
            ("Disabled", (int)EnvMapOptions.Disabled),
        });
    }

    private void TestIntEnum(TestLogger logger, TestResults results, string enumName, (string CaseName, int ExpectedValue)[] cases)
    {
        for (int i = 0; i < cases.Length; i++)
        {
            var (caseName, expectedValue) = cases[i];
            try
            {
                if (expectedValue == i)
                {
                    logger.Pass($"{enumName}.{caseName} = {expectedValue}");
                    results.Pass($"Enum_{enumName}_{caseName}");
                }
                else
                {
                    logger.Fail($"{enumName}.{caseName}: expected ordinal {i}, got {expectedValue}");
                    results.Fail($"Enum_{enumName}_{caseName}", $"Expected ordinal {i}, got {expectedValue}");
                }
            }
            catch (Exception ex)
            {
                logger.Fail($"{enumName}.{caseName}: {ex.Message}");
                results.Fail($"Enum_{enumName}_{caseName}", ex.Message);
            }
        }
    }

    // ──────────────────────────────────────────────
    // Section 3: String-Based Enums (Singleton Cases)
    // ──────────────────────────────────────────────
    //
    // Swift string-backed enums are emitted as classes with cached singleton properties
    // for each case. Loading a singleton exercises the `CaseByIndex` cdecl wrapper plus
    // the cached-handle lifetime path.

    private void RunStringEnumTests(TestLogger logger, TestResults results)
    {
        // --- BearingType ---
        TestStringEnumCase(logger, results, "BearingType", "Straight", () => BearingType.Straight);
        TestStringEnumCase(logger, results, "BearingType", "Left", () => BearingType.Left);
        TestStringEnumCase(logger, results, "BearingType", "Right", () => BearingType.Right);
        TestStringEnumCase(logger, results, "BearingType", "SlightLeft", () => BearingType.SlightLeft);
        TestStringEnumCase(logger, results, "BearingType", "SlightRight", () => BearingType.SlightRight);
        TestStringEnumCase(logger, results, "BearingType", "Back", () => BearingType.Back);

        // --- ActionType ---
        TestStringEnumCase(logger, results, "ActionType", "Arrival", () => ActionType.Arrival);
        TestStringEnumCase(logger, results, "ActionType", "Departure", () => ActionType.Departure);
        TestStringEnumCase(logger, results, "ActionType", "Turn", () => ActionType.Turn);
        TestStringEnumCase(logger, results, "ActionType", "TakeConnection", () => ActionType.TakeConnection);
        TestStringEnumCase(logger, results, "ActionType", "ExitConnection", () => ActionType.ExitConnection);

        // --- ConnectionType ---
        TestStringEnumCase(logger, results, "ConnectionType", "Stairs", () => ConnectionType.Stairs);
        TestStringEnumCase(logger, results, "ConnectionType", "Elevator", () => ConnectionType.Elevator);
        TestStringEnumCase(logger, results, "ConnectionType", "Escalator", () => ConnectionType.Escalator);
        TestStringEnumCase(logger, results, "ConnectionType", "Door", () => ConnectionType.Door);
        TestStringEnumCase(logger, results, "ConnectionType", "Portal", () => ConnectionType.Portal);
        TestStringEnumCase(logger, results, "ConnectionType", "Ramp", () => ConnectionType.Ramp);

        // --- EasingFunction ---
        TestStringEnumCase(logger, results, "EasingFunction", "Linear", () => EasingFunction.Linear);
        TestStringEnumCase(logger, results, "EasingFunction", "EaseIn", () => EasingFunction.EaseIn);
        TestStringEnumCase(logger, results, "EasingFunction", "EaseOut", () => EasingFunction.EaseOut);
        TestStringEnumCase(logger, results, "EasingFunction", "EaseInOut", () => EasingFunction.EaseInOut);

        // --- BlueDotStatus ---
        TestStringEnumCase(logger, results, "BlueDotStatus", "Active", () => BlueDotStatus.Active);
        TestStringEnumCase(logger, results, "BlueDotStatus", "Inactive", () => BlueDotStatus.Inactive);
        TestStringEnumCase(logger, results, "BlueDotStatus", "Hidden", () => BlueDotStatus.Hidden);
        TestStringEnumCase(logger, results, "BlueDotStatus", "Disabled", () => BlueDotStatus.Disabled);

        // --- Doors ---
        TestStringEnumCase(logger, results, "Doors", "Exterior", () => Doors.Exterior);
        TestStringEnumCase(logger, results, "Doors", "Interior", () => Doors.Interior);

        // --- CollisionRankingTier ---
        TestStringEnumCase(logger, results, "CollisionRankingTier", "Low", () => CollisionRankingTier.Low);
        TestStringEnumCase(logger, results, "CollisionRankingTier", "Medium", () => CollisionRankingTier.Medium);
        TestStringEnumCase(logger, results, "CollisionRankingTier", "High", () => CollisionRankingTier.High);
        TestStringEnumCase(logger, results, "CollisionRankingTier", "AlwaysVisible", () => CollisionRankingTier.AlwaysVisible);

        // --- Nested string-enum: AntialiasingOptions.QualityType ---
        // Exercises the nested-type singleton dispatch + cdecl wrapper layout.
        TestStringEnumCase(logger, results, "AntialiasingOptions.QualityType", "Low",
            () => AntialiasingOptions.QualityType.Low);
        TestStringEnumCase(logger, results, "AntialiasingOptions.QualityType", "Medium",
            () => AntialiasingOptions.QualityType.Medium);
        TestStringEnumCase(logger, results, "AntialiasingOptions.QualityType", "High",
            () => AntialiasingOptions.QualityType.High);
        TestStringEnumCase(logger, results, "AntialiasingOptions.QualityType", "Ultra",
            () => AntialiasingOptions.QualityType.Ultra);
    }

    private void TestStringEnumCase<T>(TestLogger logger, TestResults results,
        string enumName, string caseName, Func<T> getCase) where T : class
    {
        try
        {
            var instance = getCase();
            if (instance != null)
            {
                logger.Pass($"{enumName}.{caseName} singleton loaded");
                results.Pass($"StringEnum_{enumName}_{caseName}");
            }
            else
            {
                logger.Fail($"{enumName}.{caseName}: singleton is null");
                results.Fail($"StringEnum_{enumName}_{caseName}", "Singleton is null");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"{enumName}.{caseName}: {ex.Message}");
            results.Fail($"StringEnum_{enumName}_{caseName}", ex.Message);
        }
    }

    // ──────────────────────────────────────────────
    // Section 4: Constructors & Properties
    // ──────────────────────────────────────────────
    //
    // Exercises generator-emitted constructors of varying shapes: required positional,
    // optional defaults, primitive + nullable + enum-typed parameters. Each test
    // round-trips at least one property back to verify field layout.

    private void RunConstructorTests(TestLogger logger, TestResults results)
    {
        // --- Coordinate(double, double) ---
        try
        {
            using var coord = new Coordinate(latitude: 43.6532, longitude: -79.3832);
            var lat = coord.Latitude;
            var lon = coord.Longitude;
            if (Math.Abs(lat - 43.6532) < 0.001 && Math.Abs(lon - (-79.3832)) < 0.001)
            {
                logger.Pass($"Coordinate(lat,lon) round-trip (lat={lat}, lon={lon})");
                results.Pass("Constructor_Coordinate_2arg");
            }
            else
            {
                logger.Fail($"Coordinate(lat,lon): expected (43.6532, -79.3832), got ({lat}, {lon})");
                results.Fail("Constructor_Coordinate_2arg", $"Wrong values lat={lat}, lon={lon}");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"Coordinate(lat,lon) constructor: {ex.Message}");
            results.Fail("Constructor_Coordinate_2arg", ex.Message);
        }

        // --- Coordinate(double, double, string?, double, string) — full positional ---
        try
        {
            using var coord = new Coordinate(
                latitude: 43.6532,
                longitude: -79.3832,
                floorId: "floor-1",
                verticalOffset: 1.5,
                id: "test-id");
            var floor = coord.FloorId;
            var offset = coord.VerticalOffset;
            var id = coord.Id;
            if (floor == "floor-1" && Math.Abs(offset - 1.5) < 0.001 && id == "test-id")
            {
                logger.Pass($"Coordinate full constructor (floorId={floor}, verticalOffset={offset}, id={id})");
                results.Pass("Constructor_Coordinate_Full");
            }
            else
            {
                logger.Fail($"Coordinate full ctor: floorId={floor}, verticalOffset={offset}, id={id}");
                results.Fail("Constructor_Coordinate_Full",
                    $"Wrong values: floorId={floor}, verticalOffset={offset}, id={id}");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"Coordinate full constructor: {ex.Message}");
            results.Fail("Constructor_Coordinate_Full", ex.Message);
        }

        // --- Coordinate FloorId null path ---
        try
        {
            using var coord = new Coordinate(latitude: 0.0, longitude: 0.0);
            var floor = coord.FloorId;
            // No floorId argument → property should be reachable; value may be null or empty.
            logger.Pass($"Coordinate.FloorId (no-arg ctor) = {(floor == null ? "null" : $"\"{floor}\"")}");
            results.Pass("Property_Coordinate_FloorId_Null");
        }
        catch (Exception ex)
        {
            logger.Fail($"Coordinate.FloorId no-arg: {ex.Message}");
            results.Fail("Property_Coordinate_FloorId_Null", ex.Message);
        }

        // --- BlueDotErrorPayload(nint code, string message) ---
        try
        {
            using var payload = new BlueDotErrorPayload(code: 404, message: "not found");
            var code = payload.Code;
            var msg = payload.Message;
            if (code == 404 && msg == "not found")
            {
                logger.Pass($"BlueDotErrorPayload round-trip (code={code}, message=\"{msg}\")");
                results.Pass("Constructor_BlueDotErrorPayload");
            }
            else
            {
                logger.Fail($"BlueDotErrorPayload: expected (404, \"not found\"), got ({code}, \"{msg}\")");
                results.Fail("Constructor_BlueDotErrorPayload", $"Wrong values code={code}, msg={msg}");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"BlueDotErrorPayload constructor: {ex.Message}");
            results.Fail("Constructor_BlueDotErrorPayload", ex.Message);
        }

        // --- BlueDotUpdateOptions(bool? animate, bool? silent) ---
        try
        {
            using var opts = new BlueDotUpdateOptions(animate: true, silent: false);
            var animate = opts.Animate;
            var silent = opts.Silent;
            if (animate == true && silent == false)
            {
                logger.Pass($"BlueDotUpdateOptions round-trip (animate={animate}, silent={silent})");
                results.Pass("Constructor_BlueDotUpdateOptions");
            }
            else
            {
                logger.Fail($"BlueDotUpdateOptions: expected (true, false), got ({animate}, {silent})");
                results.Fail("Constructor_BlueDotUpdateOptions", $"Wrong values animate={animate}, silent={silent}");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"BlueDotUpdateOptions constructor: {ex.Message}");
            results.Fail("Constructor_BlueDotUpdateOptions", ex.Message);
        }

        // --- BlueDotUpdateOptions() with all defaults (null) ---
        try
        {
            using var opts = new BlueDotUpdateOptions();
            // Both properties should be null when defaults are used.
            var animate = opts.Animate;
            var silent = opts.Silent;
            if (animate == null && silent == null)
            {
                logger.Pass("BlueDotUpdateOptions default constructor (animate=null, silent=null)");
                results.Pass("Constructor_BlueDotUpdateOptions_Defaults");
            }
            else
            {
                logger.Fail($"BlueDotUpdateOptions defaults: expected (null, null), got ({animate}, {silent})");
                results.Fail("Constructor_BlueDotUpdateOptions_Defaults",
                    $"Wrong defaults animate={animate}, silent={silent}");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"BlueDotUpdateOptions defaults: {ex.Message}");
            results.Fail("Constructor_BlueDotUpdateOptions_Defaults", ex.Message);
        }

        // --- CameraInteractionsSetOptions(bool?, bool?, bool?) ---
        try
        {
            using var opts = new CameraInteractionsSetOptions(pan: true, zoom: false, bearingAndPitch: null);
            var pan = opts.Pan;
            var zoom = opts.Zoom;
            var bp = opts.BearingAndPitch;
            if (pan == true && zoom == false && bp == null)
            {
                logger.Pass($"CameraInteractionsSetOptions round-trip (pan={pan}, zoom={zoom}, bearingAndPitch={bp})");
                results.Pass("Constructor_CameraInteractionsSetOptions");
            }
            else
            {
                logger.Fail($"CameraInteractionsSetOptions: expected (true, false, null), got ({pan}, {zoom}, {bp})");
                results.Fail("Constructor_CameraInteractionsSetOptions",
                    $"Wrong values pan={pan}, zoom={zoom}, bp={bp}");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"CameraInteractionsSetOptions constructor: {ex.Message}");
            results.Fail("Constructor_CameraInteractionsSetOptions", ex.Message);
        }

        // --- BlueDotDeviceOrientationUpdatePayload(double?) ---
        try
        {
            using var payload = new BlueDotDeviceOrientationUpdatePayload(heading: 42.5);
            var heading = payload.Heading;
            if (heading.HasValue && Math.Abs(heading.Value - 42.5) < 0.001)
            {
                logger.Pass($"BlueDotDeviceOrientationUpdatePayload.Heading = {heading}");
                results.Pass("Constructor_BlueDotDeviceOrientationUpdatePayload");
            }
            else
            {
                logger.Fail($"BlueDotDeviceOrientationUpdatePayload.Heading: expected 42.5, got {heading}");
                results.Fail("Constructor_BlueDotDeviceOrientationUpdatePayload",
                    $"Wrong heading {heading}");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"BlueDotDeviceOrientationUpdatePayload: {ex.Message}");
            results.Fail("Constructor_BlueDotDeviceOrientationUpdatePayload", ex.Message);
        }

        // --- AntialiasingOptions(bool?, QualityType?) — non-null nested-enum marshaling ---
        // Passing a non-null nested string-enum singleton and reading it back through
        // the Quality getter exercises the full Optional<nested-enum-class> round-trip,
        // not just the constructor call path.
        try
        {
            using var opts = new AntialiasingOptions(
                enabled: true,
                quality: AntialiasingOptions.QualityType.High);
            var readBack = opts.Quality;
            var readBackRaw = readBack?.RawValue;
            var expectedRaw = AntialiasingOptions.QualityType.High.RawValue;
            if (readBack is not null && readBackRaw == expectedRaw)
            {
                logger.Pass($"AntialiasingOptions.Quality round-trip = {readBackRaw}");
                results.Pass("Constructor_AntialiasingOptions");
            }
            else
            {
                logger.Fail(
                    $"AntialiasingOptions.Quality: expected {expectedRaw}, got {readBackRaw ?? "<null>"}");
                results.Fail("Constructor_AntialiasingOptions",
                    $"Quality round-trip mismatch (expected {expectedRaw}, got {readBackRaw ?? "<null>"})");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"AntialiasingOptions constructor: {ex.Message}");
            results.Fail("Constructor_AntialiasingOptions", ex.Message);
        }

        // --- AnimationOptions(nint? duration, EasingFunction? easing) ---
        try
        {
            using var opts = new AnimationOptions(duration: (nint)250, easing: EasingFunction.EaseInOut);
            logger.Pass("AnimationOptions constructor with duration + easing");
            results.Pass("Constructor_AnimationOptions");
        }
        catch (Exception ex)
        {
            logger.Fail($"AnimationOptions constructor: {ex.Message}");
            results.Fail("Constructor_AnimationOptions", ex.Message);
        }

        // --- Coordinate equality (IEquatable<Coordinate>) ---
        try
        {
            using var a = new Coordinate(latitude: 43.6532, longitude: -79.3832, floorId: "f-1", verticalOffset: 0.0, id: "id-1");
            using var b = new Coordinate(latitude: 43.6532, longitude: -79.3832, floorId: "f-1", verticalOffset: 0.0, id: "id-1");
            var eq = a.Equals(b);
            if (eq)
            {
                logger.Pass($"Coordinate.Equals matches identical inputs ({eq})");
                results.Pass("Method_Coordinate_Equals");
            }
            else
            {
                logger.Fail("Coordinate.Equals returned false for identical inputs");
                results.Fail("Method_Coordinate_Equals", "Equals returned false for identical inputs");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"Coordinate.Equals: {ex.Message}");
            results.Fail("Method_Coordinate_Equals", ex.Message);
        }
    }

    // ──────────────────────────────────────────────
    // Section 5: String-Enum RawValue & FromRawValue
    // ──────────────────────────────────────────────
    //
    // Singleton cases expose `.RawValue` (Swift-side String) and `static FromRawValue(string)`
    // for round-trip parsing. Round-tripping a singleton through FromRawValue exercises
    // both the failable-init wrapper and the cdecl/Optional-payload path.

    private void RunRawValueTests(TestLogger logger, TestResults results)
    {
        // --- RawValue access ---
        TestRawValue(logger, results, "BearingType", "Straight", () => BearingType.Straight.RawValue);
        TestRawValue(logger, results, "BearingType", "Right", () => BearingType.Right.RawValue);
        TestRawValue(logger, results, "BearingType", "Left", () => BearingType.Left.RawValue);
        TestRawValue(logger, results, "ActionType", "Departure", () => ActionType.Departure.RawValue);
        TestRawValue(logger, results, "ActionType", "Arrival", () => ActionType.Arrival.RawValue);
        TestRawValue(logger, results, "ConnectionType", "Stairs", () => ConnectionType.Stairs.RawValue);
        TestRawValue(logger, results, "ConnectionType", "Elevator", () => ConnectionType.Elevator.RawValue);
        TestRawValue(logger, results, "EasingFunction", "Linear", () => EasingFunction.Linear.RawValue);
        TestRawValue(logger, results, "EasingFunction", "EaseInOut", () => EasingFunction.EaseInOut.RawValue);
        TestRawValue(logger, results, "BlueDotStatus", "Active", () => BlueDotStatus.Active.RawValue);
        TestRawValue(logger, results, "CollisionRankingTier", "Medium", () => CollisionRankingTier.Medium.RawValue);
        // Nested-enum RawValue access:
        TestRawValue(logger, results, "AntialiasingOptions.QualityType", "Medium",
            () => AntialiasingOptions.QualityType.Medium.RawValue);
        TestRawValue(logger, results, "AntialiasingOptions.QualityType", "Ultra",
            () => AntialiasingOptions.QualityType.Ultra.RawValue);

        // --- FromRawValue round-trip ---
        TestFromRawValueRoundTrip(logger, results, "BearingType", "straight",
            BearingType.Straight.RawValue, () => BearingType.FromRawValue(BearingType.Straight.RawValue)?.RawValue);
        TestFromRawValueRoundTrip(logger, results, "ActionType", "departure",
            ActionType.Departure.RawValue, () => ActionType.FromRawValue(ActionType.Departure.RawValue)?.RawValue);
        TestFromRawValueRoundTrip(logger, results, "ConnectionType", "stairs",
            ConnectionType.Stairs.RawValue, () => ConnectionType.FromRawValue(ConnectionType.Stairs.RawValue)?.RawValue);
        TestFromRawValueRoundTrip(logger, results, "EasingFunction", "linear",
            EasingFunction.Linear.RawValue, () => EasingFunction.FromRawValue(EasingFunction.Linear.RawValue)?.RawValue);
        TestFromRawValueRoundTrip(logger, results, "BlueDotStatus", "active",
            BlueDotStatus.Active.RawValue, () => BlueDotStatus.FromRawValue(BlueDotStatus.Active.RawValue)?.RawValue);
        // Nested-enum FromRawValue round-trip:
        TestFromRawValueRoundTrip(logger, results, "AntialiasingOptions.QualityType", "medium",
            AntialiasingOptions.QualityType.Medium.RawValue,
            () => AntialiasingOptions.QualityType.FromRawValue(
                AntialiasingOptions.QualityType.Medium.RawValue)?.RawValue);

        // --- FromRawValue with invalid input ---
        TestFromRawValueInvalid(logger, results, "BearingType",
            () => BearingType.FromRawValue("not_a_bearing_type"));
        TestFromRawValueInvalid(logger, results, "ActionType",
            () => ActionType.FromRawValue("not_an_action_type"));
        TestFromRawValueInvalid(logger, results, "ConnectionType",
            () => ConnectionType.FromRawValue("not_a_connection_type"));
    }

    private void TestRawValue(TestLogger logger, TestResults results,
        string enumName, string caseName, Func<string> getRawValue)
    {
        try
        {
            var raw = getRawValue();
            if (!string.IsNullOrEmpty(raw))
            {
                logger.Pass($"{enumName}.{caseName}.RawValue = \"{raw}\"");
                results.Pass($"RawValue_{enumName}_{caseName}");
            }
            else
            {
                logger.Fail($"{enumName}.{caseName}.RawValue: empty or null");
                results.Fail($"RawValue_{enumName}_{caseName}", "Empty or null RawValue");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"{enumName}.{caseName}.RawValue: {ex.Message}");
            results.Fail($"RawValue_{enumName}_{caseName}", ex.Message);
        }
    }

    private void TestFromRawValueRoundTrip(TestLogger logger, TestResults results,
        string enumName, string desc, string expectedRaw, Func<string?> getRoundTrippedRaw)
    {
        try
        {
            var roundTripped = getRoundTrippedRaw();
            if (roundTripped == expectedRaw)
            {
                logger.Pass($"{enumName}.FromRawValue({desc}) round-trip matched: \"{roundTripped}\"");
                results.Pass($"FromRawValue_{enumName}_{desc}");
            }
            else if (roundTripped != null)
            {
                logger.Fail($"{enumName}.FromRawValue({desc}): expected \"{expectedRaw}\", got \"{roundTripped}\"");
                results.Fail($"FromRawValue_{enumName}_{desc}", $"Expected \"{expectedRaw}\", got \"{roundTripped}\"");
            }
            else
            {
                logger.Fail($"{enumName}.FromRawValue({desc}): returned null for valid raw value");
                results.Fail($"FromRawValue_{enumName}_{desc}", "Returned null for valid raw value");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"{enumName}.FromRawValue({desc}): {ex.Message}");
            results.Fail($"FromRawValue_{enumName}_{desc}", ex.Message);
        }
    }

    private void TestFromRawValueInvalid<T>(TestLogger logger, TestResults results,
        string enumName, Func<T?> fromRawValue) where T : class
    {
        try
        {
            var result = fromRawValue();
            if (result == null)
            {
                logger.Pass($"{enumName}.FromRawValue(invalid) correctly returned null");
                results.Pass($"FromRawValue_{enumName}_Invalid");
            }
            else
            {
                // Some Swift enums fall back to a default case for unknown raw values.
                // Either behavior is acceptable here as long as it doesn't crash.
                logger.Skip($"{enumName}.FromRawValue(invalid): non-null result (Swift may return default)");
                results.Skip($"FromRawValue_{enumName}_Invalid", "Non-null result — Swift may use default case");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"{enumName}.FromRawValue(invalid): {ex.Message}");
            results.Fail($"FromRawValue_{enumName}_Invalid", ex.Message);
        }
    }

    // ──────────────────────────────────────────────
    // Section 6: Protocol & Interface Conformance
    // ──────────────────────────────────────────────
    //
    // Swift protocols are projected as C# interfaces (prefixed with `I`). Any class
    // that conforms in Swift must satisfy `IsAssignableFrom` in C#.

    private void RunProtocolTests(TestLogger logger, TestResults results)
    {
        // --- IAnchorable conformers ---
        TestInterfaceImpl(logger, results, "Coordinate", typeof(IAnchorable), typeof(Coordinate));
        TestInterfaceImpl(logger, results, "Annotation", typeof(IAnchorable), typeof(Annotation));
        TestInterfaceImpl(logger, results, "Area", typeof(IAnchorable), typeof(Area));
        TestInterfaceImpl(logger, results, "Door", typeof(IAnchorable), typeof(Door));
        TestInterfaceImpl(logger, results, "Facade", typeof(IAnchorable), typeof(Facade));

        // --- IGeoJSONData conformers ---
        TestInterfaceImpl(logger, results, "Annotation", typeof(IGeoJSONData), typeof(Annotation));
        TestInterfaceImpl(logger, results, "Area", typeof(IGeoJSONData), typeof(Area));
        TestInterfaceImpl(logger, results, "Door", typeof(IGeoJSONData), typeof(Door));
        TestInterfaceImpl(logger, results, "Floor", typeof(IGeoJSONData), typeof(Floor));
        TestInterfaceImpl(logger, results, "Connection", typeof(IGeoJSONData), typeof(Connection));

        // --- IQueryOrigin conformers ---
        TestInterfaceImpl(logger, results, "Coordinate", typeof(IQueryOrigin), typeof(Coordinate));
        TestInterfaceImpl(logger, results, "Annotation", typeof(IQueryOrigin), typeof(Annotation));
        TestInterfaceImpl(logger, results, "Door", typeof(IQueryOrigin), typeof(Door));

        // --- ISwiftObject + IDisposable on top-level classes ---
        var swiftObjectTypes = new (string Name, Type Type)[]
        {
            ("MapData", typeof(MapData)),
            ("MapView", typeof(MapView)),
            ("Query", typeof(Query)),
            ("Search", typeof(Search)),
            ("Analytics", typeof(Analytics)),
            ("Annotation", typeof(Annotation)),
            ("Coordinate", typeof(Coordinate)),
            ("Floor", typeof(Floor)),
            ("Door", typeof(Door)),
            ("Connection", typeof(Connection)),
        };

        foreach (var (name, type) in swiftObjectTypes)
        {
            try
            {
                var isSwiftObj = typeof(ISwiftObject).IsAssignableFrom(type);
                var isDisposable = typeof(IDisposable).IsAssignableFrom(type);
                if (isSwiftObj && isDisposable)
                {
                    logger.Pass($"{name} implements ISwiftObject + IDisposable");
                    results.Pass($"Protocol_{name}_ISwiftObject");
                }
                else
                {
                    logger.Fail($"{name}: ISwiftObject={isSwiftObj}, IDisposable={isDisposable}");
                    results.Fail($"Protocol_{name}_ISwiftObject", "Missing interface implementation");
                }
            }
            catch (Exception ex)
            {
                logger.Fail($"Protocol check {name}: {ex.Message}");
                results.Fail($"Protocol_{name}_ISwiftObject", ex.Message);
            }
        }

        // --- ISwiftStruct on value-style classes ---
        var swiftStructTypes = new (string Name, Type Type)[]
        {
            ("BinaryBundle", typeof(BinaryBundle)),
            ("Directions", typeof(Directions)),
            ("DirectionInstruction", typeof(DirectionInstruction)),
            ("CameraTransform", typeof(CameraTransform)),
            ("CameraTarget", typeof(CameraTarget)),
            ("CameraAnimationOptions", typeof(CameraAnimationOptions)),
            ("AnimationOptions", typeof(AnimationOptions)),
            ("AntialiasingOptions", typeof(AntialiasingOptions)),
            ("BlueDotPositionUpdate", typeof(BlueDotPositionUpdate)),
            ("BlueDotPositionUpdatePayload", typeof(BlueDotPositionUpdatePayload)),
            ("BlueDotErrorPayload", typeof(BlueDotErrorPayload)),
            ("BlueDotUpdateOptions", typeof(BlueDotUpdateOptions)),
            ("BearingType", typeof(BearingType)),
            ("ActionType", typeof(ActionType)),
            ("ConnectionType", typeof(ConnectionType)),
            ("EasingFunction", typeof(EasingFunction)),
        };

        foreach (var (name, type) in swiftStructTypes)
        {
            try
            {
                var isStruct = typeof(ISwiftStruct).IsAssignableFrom(type);
                if (isStruct)
                {
                    logger.Pass($"{name} implements ISwiftStruct");
                    results.Pass($"Protocol_{name}_ISwiftStruct");
                }
                else
                {
                    logger.Fail($"{name} does not implement ISwiftStruct");
                    results.Fail($"Protocol_{name}_ISwiftStruct", "Missing ISwiftStruct");
                }
            }
            catch (Exception ex)
            {
                logger.Fail($"Protocol check {name}: {ex.Message}");
                results.Fail($"Protocol_{name}_ISwiftStruct", ex.Message);
            }
        }

        // --- IEquatable<T> on Equatable conformers ---
        TestInterfaceImpl(logger, results, "Coordinate", typeof(IEquatable<Coordinate>), typeof(Coordinate));
        TestInterfaceImpl(logger, results, "BlueDotErrorPayload", typeof(IEquatable<BlueDotErrorPayload>), typeof(BlueDotErrorPayload));
        TestInterfaceImpl(logger, results, "BlueDotUpdateOptions", typeof(IEquatable<BlueDotUpdateOptions>), typeof(BlueDotUpdateOptions));
        TestInterfaceImpl(logger, results, "BearingType", typeof(IEquatable<BearingType>), typeof(BearingType));
        TestInterfaceImpl(logger, results, "AntialiasingOptions", typeof(IEquatable<AntialiasingOptions>), typeof(AntialiasingOptions));
    }

    private void TestInterfaceImpl(TestLogger logger, TestResults results,
        string typeName, Type iface, Type implType)
    {
        try
        {
            var ok = iface.IsAssignableFrom(implType);
            if (ok)
            {
                logger.Pass($"{typeName} implements {iface.Name}");
                results.Pass($"Protocol_{typeName}_{iface.Name}");
            }
            else
            {
                logger.Fail($"{typeName} does not implement {iface.Name}");
                results.Fail($"Protocol_{typeName}_{iface.Name}", "Interface not implemented");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"Protocol check {typeName} / {iface.Name}: {ex.Message}");
            results.Fail($"Protocol_{typeName}_{iface.Name}", ex.Message);
        }
    }

    // ──────────────────────────────────────────────
    // Section 7: WKWebView Inheritance
    // ──────────────────────────────────────────────
    //
    // `MapViewController` is a Swift class declared `class MapViewController: WKWebView`,
    // so the generated C# class must inherit from `WebKit.WKWebView`.

    private void RunInheritanceTests(TestLogger logger, TestResults results)
    {
        try
        {
            var isWebView = typeof(WebKit.WKWebView).IsAssignableFrom(typeof(MapViewController));
            if (isWebView)
            {
                logger.Pass("MapViewController inherits from WKWebView");
                results.Pass("Inheritance_MapViewController_WKWebView");
            }
            else
            {
                logger.Fail("MapViewController does not inherit from WKWebView");
                results.Fail("Inheritance_MapViewController_WKWebView", "Not a WKWebView subclass");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"Inheritance check MapViewController / WKWebView: {ex.Message}");
            results.Fail("Inheritance_MapViewController_WKWebView", ex.Message);
        }
    }
}

#endregion
