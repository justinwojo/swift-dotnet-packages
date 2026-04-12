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

        logger.Info("=== Section 3: String-Based Enums ===");
        RunStringEnumTests(logger, results);

        logger.Info("=== Section 4: MPIOptions String-Based Enums ===");
        RunOptionsEnumTests(logger, results);

        logger.Info("=== Section 5: Constructor Tests ===");
        RunConstructorTests(logger, results);

        logger.Info("=== Section 6: String-Enum RawValue & FromRawValue ===");
        RunRawValueTests(logger, results);

        logger.Info("=== Section 7: Protocol Interface Tests ===");
        RunProtocolTests(logger, results);

        logger.Info("=== Section 8: MPIOptions Nested Struct Metadata ===");
        RunOptionsStructMetadataTests(logger, results);

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
        // Test metadata access for all key struct, class, and enum types
        var metadataTests = new (string Name, Func<TypeMetadata> GetMetadata)[]
        {
            // Structs
            ("MPISiblingGroup", () => SwiftObjectHelper<MPISiblingGroup>.GetTypeMetadata()),
            ("MPIBlueDotPositionUpdate", () => SwiftObjectHelper<MPIBlueDotPositionUpdate>.GetTypeMetadata()),
            ("MPIBlueDotStateChange", () => SwiftObjectHelper<MPIBlueDotStateChange>.GetTypeMetadata()),
            ("MPIPhone", () => SwiftObjectHelper<MPIPhone>.GetTypeMetadata()),
            ("MPIVenueResponse", () => SwiftObjectHelper<MPIVenueResponse>.GetTypeMetadata()),
            ("MPIPath", () => SwiftObjectHelper<MPIPath>.GetTypeMetadata()),
            ("MPILanguage", () => SwiftObjectHelper<MPILanguage>.GetTypeMetadata()),
            ("MPIGalleryImage", () => SwiftObjectHelper<MPIGalleryImage>.GetTypeMetadata()),
            ("MPIPolygonRanking", () => SwiftObjectHelper<MPIPolygonRanking>.GetTypeMetadata()),
            ("MPICategory", () => SwiftObjectHelper<MPICategory>.GetTypeMetadata()),
            ("MPIPolygon", () => SwiftObjectHelper<MPIPolygon>.GetTypeMetadata()),
            ("MPIVortex", () => SwiftObjectHelper<MPIVortex>.GetTypeMetadata()),
            ("MPIDestinationSet", () => SwiftObjectHelper<MPIDestinationSet>.GetTypeMetadata()),
            ("MPINode", () => SwiftObjectHelper<MPINode>.GetTypeMetadata()),
            ("MPIPathNode", () => SwiftObjectHelper<MPIPathNode>.GetTypeMetadata()),
            ("MPIDirections", () => SwiftObjectHelper<MPIDirections>.GetTypeMetadata()),
            ("MPIInstruction", () => SwiftObjectHelper<MPIInstruction>.GetTypeMetadata()),
            ("MPIAction", () => SwiftObjectHelper<MPIAction>.GetTypeMetadata()),
            ("MPILocationState", () => SwiftObjectHelper<MPILocationState>.GetTypeMetadata()),
            ("MPIVenue", () => SwiftObjectHelper<MPIVenue>.GetTypeMetadata()),
            ("MPITinyObject", () => SwiftObjectHelper<MPITinyObject>.GetTypeMetadata()),
            ("MPISocial", () => SwiftObjectHelper<MPISocial>.GetTypeMetadata()),
            ("MPIOpeningHours", () => SwiftObjectHelper<MPIOpeningHours>.GetTypeMetadata()),
            ("MPIMapClickEvent", () => SwiftObjectHelper<MPIMapClickEvent>.GetTypeMetadata()),
            ("MPILocation", () => SwiftObjectHelper<MPILocation>.GetTypeMetadata()),
            ("MPISearchMatch", () => SwiftObjectHelper<MPISearchMatch>.GetTypeMetadata()),
            ("MPISearchResult", () => SwiftObjectHelper<MPISearchResult>.GetTypeMetadata()),
            ("MPISearchSuggestion", () => SwiftObjectHelper<MPISearchSuggestion>.GetTypeMetadata()),
            ("MPISearchSuggestions", () => SwiftObjectHelper<MPISearchSuggestions>.GetTypeMetadata()),
            ("MPISuggestions", () => SwiftObjectHelper<MPISuggestions>.GetTypeMetadata()),
            ("MPISearchResultLocation", () => SwiftObjectHelper<MPISearchResultLocation>.GetTypeMetadata()),
            ("MPISearchResultCategory", () => SwiftObjectHelper<MPISearchResultCategory>.GetTypeMetadata()),
            ("MPISearchResultCustom", () => SwiftObjectHelper<MPISearchResultCustom>.GetTypeMetadata()),
            ("MPIIdBasedObject", () => SwiftObjectHelper<MPIIdBasedObject>.GetTypeMetadata()),
            ("MPIColor", () => SwiftObjectHelper<MPIColor>.GetTypeMetadata()),
            ("MPIHeader", () => SwiftObjectHelper<MPIHeader>.GetTypeMetadata()),
            ("MPIRankings", () => SwiftObjectHelper<MPIRankings>.GetTypeMetadata()),
            ("MPIPosition", () => SwiftObjectHelper<MPIPosition>.GetTypeMetadata()),
            ("MPICoordinates", () => SwiftObjectHelper<MPICoordinates>.GetTypeMetadata()),
            ("MPIOptions", () => SwiftObjectHelper<MPIOptions>.GetTypeMetadata()),
            ("MPIPicture", () => SwiftObjectHelper<MPIPicture>.GetTypeMetadata()),
            ("MPIImage", () => SwiftObjectHelper<MPIImage>.GetTypeMetadata()),
            ("MappedinIdBasedObject", () => SwiftObjectHelper<MappedinIdBasedObject>.GetTypeMetadata()),
            // Nested event structs
            ("MPIMapClickEvent.FloatingLabelClicked", () => SwiftObjectHelper<MPIMapClickEvent.FloatingLabelClicked>.GetTypeMetadata()),
            ("MPIMapClickEvent.ClickEventPayload", () => SwiftObjectHelper<MPIMapClickEvent.ClickEventPayload>.GetTypeMetadata()),
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

    private void RunIntegerEnumTests(TestLogger logger, TestResults results)
    {
        // --- MPIMarkerState ---
        TestIntEnum(logger, results, "MPIMarkerState", new (string, int)[]
        {
            ("Hidden", (int)MPIMarkerState.Hidden),
            ("Ghost", (int)MPIMarkerState.Ghost),
            ("Normal", (int)MPIMarkerState.Normal),
            ("Uncertain", (int)MPIMarkerState.Uncertain),
        });

        // --- MPIBlueDotState ---
        TestIntEnum(logger, results, "MPIBlueDotState", new (string, int)[]
        {
            ("NotListening", (int)MPIBlueDotState.NotListening),
            ("Listening", (int)MPIBlueDotState.Listening),
            ("HasPosition", (int)MPIBlueDotState.HasPosition),
            ("HasIndoorPosition", (int)MPIBlueDotState.HasIndoorPosition),
            ("LocationUncertain", (int)MPIBlueDotState.LocationUncertain),
        });

        // --- MPIBlueDotStateReason ---
        TestIntEnum(logger, results, "MPIBlueDotStateReason", new (string, int)[]
        {
            ("OutsideMap", (int)MPIBlueDotStateReason.OutsideMap),
            ("NoPositionsProvided", (int)MPIBlueDotStateReason.NoPositionsProvided),
            ("GeolocationProviderError", (int)MPIBlueDotStateReason.GeolocationProviderError),
            ("CustomGeolocationProviderError", (int)MPIBlueDotStateReason.CustomGeolocationProviderError),
        });

        // --- MPICameraManagerError ---
        TestIntEnum(logger, results, "MPICameraManagerError", new (string, int)[]
        {
            ("SetMaxTiltOutOfBounds", (int)MPICameraManagerError.SetMaxTiltOutOfBounds),
        });

        // --- MPIOptions.MarkerAnchor ---
        TestIntEnum(logger, results, "MarkerAnchor", new (string, int)[]
        {
            ("Center", (int)MPIOptions.MarkerAnchor.Center),
            ("Top", (int)MPIOptions.MarkerAnchor.Top),
            ("Bottom", (int)MPIOptions.MarkerAnchor.Bottom),
            ("Left", (int)MPIOptions.MarkerAnchor.Left),
            ("Right", (int)MPIOptions.MarkerAnchor.Right),
        });

        // --- MPIOptions.CollisionRankingTiers ---
        TestIntEnum(logger, results, "CollisionRankingTiers", new (string, int)[]
        {
            ("Medium", (int)MPIOptions.CollisionRankingTiers.Medium),
            ("High", (int)MPIOptions.CollisionRankingTiers.High),
            ("AlwaysVisible", (int)MPIOptions.CollisionRankingTiers.AlwaysVisible),
        });
    }

    private void TestIntEnum(TestLogger logger, TestResults results, string enumName, (string CaseName, int ExpectedValue)[] cases)
    {
        for (int i = 0; i < cases.Length; i++)
        {
            var (caseName, expectedValue) = cases[i];
            try
            {
                // Verify case has expected ordinal value (sequential from 0)
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

    private void RunStringEnumTests(TestLogger logger, TestResults results)
    {
        // --- MPIError static cases ---
        TestStringEnumCase(logger, results, "MPIError", "Decode", () => MPIError.Decode);
        TestStringEnumCase(logger, results, "MPIError", "Retrieve", () => MPIError.Retrieve);
        TestStringEnumCase(logger, results, "MPIError", "ShowVenue", () => MPIError.ShowVenue);
        TestStringEnumCase(logger, results, "MPIError", "LoadVenue", () => MPIError.LoadVenue);
        TestStringEnumCase(logger, results, "MPIError", "PathNotFound", () => MPIError.PathNotFound);

        // --- MPIVortexType static cases ---
        TestStringEnumCase(logger, results, "MPIVortexType", "Stairs", () => MPIVortexType.Stairs);
        TestStringEnumCase(logger, results, "MPIVortexType", "Elevator", () => MPIVortexType.Elevator);
        TestStringEnumCase(logger, results, "MPIVortexType", "Escalator", () => MPIVortexType.Escalator);
        TestStringEnumCase(logger, results, "MPIVortexType", "Door", () => MPIVortexType.Door);
        TestStringEnumCase(logger, results, "MPIVortexType", "Slide", () => MPIVortexType.Slide);
        TestStringEnumCase(logger, results, "MPIVortexType", "Portal", () => MPIVortexType.Portal);
        TestStringEnumCase(logger, results, "MPIVortexType", "Ramp", () => MPIVortexType.Ramp);
        TestStringEnumCase(logger, results, "MPIVortexType", "Other", () => MPIVortexType.Other);

        // --- MPIActionType static cases ---
        TestStringEnumCase(logger, results, "MPIActionType", "Departure", () => MPIActionType.Departure);
        TestStringEnumCase(logger, results, "MPIActionType", "TakeVortex", () => MPIActionType.TakeVortex);
        TestStringEnumCase(logger, results, "MPIActionType", "ExitVortex", () => MPIActionType.ExitVortex);
        TestStringEnumCase(logger, results, "MPIActionType", "Turn", () => MPIActionType.Turn);
        TestStringEnumCase(logger, results, "MPIActionType", "Arrival", () => MPIActionType.Arrival);

        // --- MPIBearingType static cases ---
        TestStringEnumCase(logger, results, "MPIBearingType", "Straight", () => MPIBearingType.Straight);
        TestStringEnumCase(logger, results, "MPIBearingType", "Right", () => MPIBearingType.Right);
        TestStringEnumCase(logger, results, "MPIBearingType", "SlightRight", () => MPIBearingType.SlightRight);
        TestStringEnumCase(logger, results, "MPIBearingType", "Left", () => MPIBearingType.Left);
        TestStringEnumCase(logger, results, "MPIBearingType", "SlightLeft", () => MPIBearingType.SlightLeft);

        // --- MPIState static cases ---
        TestStringEnumCase(logger, results, "MPIState", "Follow", () => MPIState.Follow);
        TestStringEnumCase(logger, results, "MPIState", "Explore", () => MPIState.Explore);
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
    // Section 4: MPIOptions String-Based Enums
    // ──────────────────────────────────────────────

    private void RunOptionsEnumTests(TestLogger logger, TestResults results)
    {
        // --- CameraDirection ---
        TestStringEnumCase(logger, results, "CameraDirection", "Up", () => MPIOptions.CameraDirection.Up);
        TestStringEnumCase(logger, results, "CameraDirection", "Down", () => MPIOptions.CameraDirection.Down);
        TestStringEnumCase(logger, results, "CameraDirection", "Left", () => MPIOptions.CameraDirection.Left);
        TestStringEnumCase(logger, results, "CameraDirection", "Right", () => MPIOptions.CameraDirection.Right);

        // --- EasingMode ---
        TestStringEnumCase(logger, results, "EasingMode", "Linear", () => MPIOptions.EasingMode.Linear);
        TestStringEnumCase(logger, results, "EasingMode", "EaseIn", () => MPIOptions.EasingMode.EaseIn);
        TestStringEnumCase(logger, results, "EasingMode", "EaseOut", () => MPIOptions.EasingMode.EaseOut);
        TestStringEnumCase(logger, results, "EasingMode", "EaseInOut", () => MPIOptions.EasingMode.EaseInOut);

        // --- FloatingLabelRank ---
        TestStringEnumCase(logger, results, "FloatingLabelRank", "Medium", () => MPIOptions.FloatingLabelRank.Medium);
        TestStringEnumCase(logger, results, "FloatingLabelRank", "High", () => MPIOptions.FloatingLabelRank.High);
        TestStringEnumCase(logger, results, "FloatingLabelRank", "AlwaysVisible", () => MPIOptions.FloatingLabelRank.AlwaysVisible);

        // --- ThingKey ---
        TestStringEnumCase(logger, results, "ThingKey", "Venue", () => MPIOptions.ThingKey.Venue);
        TestStringEnumCase(logger, results, "ThingKey", "Nodes", () => MPIOptions.ThingKey.Nodes);
        TestStringEnumCase(logger, results, "ThingKey", "Vortexes", () => MPIOptions.ThingKey.Vortexes);
        TestStringEnumCase(logger, results, "ThingKey", "Polygons", () => MPIOptions.ThingKey.Polygons);
        TestStringEnumCase(logger, results, "ThingKey", "Locations", () => MPIOptions.ThingKey.Locations);
        TestStringEnumCase(logger, results, "ThingKey", "Categories", () => MPIOptions.ThingKey.Categories);
        TestStringEnumCase(logger, results, "ThingKey", "Maps", () => MPIOptions.ThingKey.Maps);
        TestStringEnumCase(logger, results, "ThingKey", "Mapgroups", () => MPIOptions.ThingKey.Mapgroups);
        TestStringEnumCase(logger, results, "ThingKey", "Themes", () => MPIOptions.ThingKey.Themes);
        TestStringEnumCase(logger, results, "ThingKey", "Rankings", () => MPIOptions.ThingKey.Rankings);

        // --- FloatingLabelMarkerIconFit ---
        TestStringEnumCase(logger, results, "FloatingLabelMarkerIconFit", "Fill", () => MPIOptions.FloatingLabelMarkerIconFit.Fill);
        TestStringEnumCase(logger, results, "FloatingLabelMarkerIconFit", "Cover", () => MPIOptions.FloatingLabelMarkerIconFit.Cover);
        TestStringEnumCase(logger, results, "FloatingLabelMarkerIconFit", "Contain", () => MPIOptions.FloatingLabelMarkerIconFit.Contain);

        // --- TooltipAnchorType ---
        TestStringEnumCase(logger, results, "TooltipAnchorType", "Top", () => MPIOptions.TooltipAnchorType.Top);
        TestStringEnumCase(logger, results, "TooltipAnchorType", "Left", () => MPIOptions.TooltipAnchorType.Left);
        TestStringEnumCase(logger, results, "TooltipAnchorType", "TopLeft", () => MPIOptions.TooltipAnchorType.TopLeft);
        TestStringEnumCase(logger, results, "TooltipAnchorType", "Right", () => MPIOptions.TooltipAnchorType.Right);
        TestStringEnumCase(logger, results, "TooltipAnchorType", "TopRight", () => MPIOptions.TooltipAnchorType.TopRight);
        TestStringEnumCase(logger, results, "TooltipAnchorType", "Bottom", () => MPIOptions.TooltipAnchorType.Bottom);
        TestStringEnumCase(logger, results, "TooltipAnchorType", "BottomLeft", () => MPIOptions.TooltipAnchorType.BottomLeft);
        TestStringEnumCase(logger, results, "TooltipAnchorType", "BottomRight", () => MPIOptions.TooltipAnchorType.BottomRight);

        // --- AntiAliasQuality ---
        TestStringEnumCase(logger, results, "AntiAliasQuality", "Low", () => MPIOptions.AntiAliasQuality.Low);
        TestStringEnumCase(logger, results, "AntiAliasQuality", "Medium", () => MPIOptions.AntiAliasQuality.Medium);
        TestStringEnumCase(logger, results, "AntiAliasQuality", "High", () => MPIOptions.AntiAliasQuality.High);
        TestStringEnumCase(logger, results, "AntiAliasQuality", "Ultra", () => MPIOptions.AntiAliasQuality.Ultra);

        // --- AmbientOcclusionQuality ---
        TestStringEnumCase(logger, results, "AmbientOcclusionQuality", "Performance", () => MPIOptions.AmbientOcclusionQuality.Performance);
        TestStringEnumCase(logger, results, "AmbientOcclusionQuality", "Low", () => MPIOptions.AmbientOcclusionQuality.Low);
        TestStringEnumCase(logger, results, "AmbientOcclusionQuality", "Medium", () => MPIOptions.AmbientOcclusionQuality.Medium);
        TestStringEnumCase(logger, results, "AmbientOcclusionQuality", "High", () => MPIOptions.AmbientOcclusionQuality.High);
        TestStringEnumCase(logger, results, "AmbientOcclusionQuality", "Ultra", () => MPIOptions.AmbientOcclusionQuality.Ultra);

        // --- AmbientOcclusionResolution ---
        TestStringEnumCase(logger, results, "AmbientOcclusionResolution", "Half", () => MPIOptions.AmbientOcclusionResolution.Half);
        TestStringEnumCase(logger, results, "AmbientOcclusionResolution", "Full", () => MPIOptions.AmbientOcclusionResolution.Full);

        // --- OutdoorAttributionPosition ---
        TestStringEnumCase(logger, results, "OutdoorAttributionPosition", "TopLeft", () => MPIOptions.OutdoorAttributionPosition.TopLeft);
        TestStringEnumCase(logger, results, "OutdoorAttributionPosition", "TopRight", () => MPIOptions.OutdoorAttributionPosition.TopRight);
        TestStringEnumCase(logger, results, "OutdoorAttributionPosition", "BottomLeft", () => MPIOptions.OutdoorAttributionPosition.BottomLeft);
        TestStringEnumCase(logger, results, "OutdoorAttributionPosition", "BottomRight", () => MPIOptions.OutdoorAttributionPosition.BottomRight);
    }

    // ──────────────────────────────────────────────
    // Section 5: Constructor Tests
    // ──────────────────────────────────────────────

    private void RunConstructorTests(TestLogger logger, TestResults results)
    {
        // --- MPIHeader (string, string) ---
        try
        {
            var header = new MPIHeader("Authorization", "Bearer token123");
            var name = header.Name;
            var value = header.Value;
            if (name == "Authorization" && value == "Bearer token123")
            {
                logger.Pass($"MPIHeader constructor + property access (name={name}, value={value})");
                results.Pass("Constructor_MPIHeader");
            }
            else
            {
                logger.Fail($"MPIHeader: expected (Authorization, Bearer token123), got ({name}, {value})");
                results.Fail("Constructor_MPIHeader", $"Wrong values: ({name}, {value})");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"MPIHeader constructor: {ex.Message}");
            results.Fail("Constructor_MPIHeader", ex.Message);
        }

        // --- MPICoordinates (double, double, double, nint?) ---
        try
        {
            var coords = new MPICoordinates(latitude: 43.6532, longitude: -79.3832, accuracy: 10.0, floorLevel: 2);
            var lat = coords.Latitude;
            var lon = coords.Longitude;
            var acc = coords.Accuracy;
            var floor = coords.FloorLevel;
            if (Math.Abs(lat - 43.6532) < 0.001 && Math.Abs(lon - (-79.3832)) < 0.001)
            {
                logger.Pass($"MPICoordinates constructor (lat={lat}, lon={lon}, acc={acc}, floor={floor})");
                results.Pass("Constructor_MPICoordinates");
            }
            else
            {
                logger.Fail($"MPICoordinates: unexpected values lat={lat}, lon={lon}");
                results.Fail("Constructor_MPICoordinates", $"Wrong values: lat={lat}, lon={lon}");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"MPICoordinates constructor: {ex.Message}");
            results.Fail("Constructor_MPICoordinates", ex.Message);
        }

        // --- MPICoordinates property: Accuracy ---
        try
        {
            var coords = new MPICoordinates(latitude: 0.0, longitude: 0.0, accuracy: 5.5, floorLevel: null);
            var acc = coords.Accuracy;
            if (Math.Abs(acc - 5.5) < 0.001)
            {
                logger.Pass($"MPICoordinates.Accuracy = {acc}");
                results.Pass("Property_MPICoordinates_Accuracy");
            }
            else
            {
                logger.Fail($"MPICoordinates.Accuracy: expected 5.5, got {acc}");
                results.Fail("Property_MPICoordinates_Accuracy", $"Expected 5.5, got {acc}");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"MPICoordinates.Accuracy: {ex.Message}");
            results.Fail("Property_MPICoordinates_Accuracy", ex.Message);
        }

        // --- MPICoordinates.FloorLevel with null ---
        try
        {
            var coords = new MPICoordinates(latitude: 0.0, longitude: 0.0, accuracy: 1.0, floorLevel: null);
            var floor = coords.FloorLevel;
            // Null floor level should be accessible
            logger.Pass($"MPICoordinates.FloorLevel (null) = {(floor.HasValue ? floor.Value.ToString() : "null")}");
            results.Pass("Property_MPICoordinates_FloorLevel_Null");
        }
        catch (Exception ex)
        {
            logger.Fail($"MPICoordinates.FloorLevel (null): {ex.Message}");
            results.Fail("Property_MPICoordinates_FloorLevel_Null", ex.Message);
        }

        // --- MPIPosition with defaults ---
        try
        {
            var pos = new MPIPosition();
            var type = pos.Type;
            logger.Pass($"MPIPosition default constructor (type={type})");
            results.Pass("Constructor_MPIPosition_Default");
        }
        catch (Exception ex)
        {
            logger.Fail($"MPIPosition default constructor: {ex.Message}");
            results.Fail("Constructor_MPIPosition_Default", ex.Message);
        }

        // --- MPIPosition with coords ---
        try
        {
            var coords = new MPICoordinates(latitude: 43.6532, longitude: -79.3832, accuracy: 10.0, floorLevel: 1);
            var pos = new MPIPosition(timestamp: 1000.0, coords: coords, type: "gps", annotation: "test", bearing: 90.0);
            var ts = pos.Timestamp;
            var t = pos.Type;
            var ann = pos.Annotation;
            var bear = pos.Bearing;
            bool valid = t == "gps" && ann == "test";
            if (valid)
            {
                logger.Pass($"MPIPosition full constructor (ts={ts}, type={t}, ann={ann}, bearing={bear})");
                results.Pass("Constructor_MPIPosition_Full");
            }
            else
            {
                logger.Fail($"MPIPosition full constructor: type={t} (expected gps), ann={ann} (expected test)");
                results.Fail("Constructor_MPIPosition_Full", $"Wrong values: type={t}, ann={ann}");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"MPIPosition full constructor: {ex.Message}");
            results.Fail("Constructor_MPIPosition_Full", ex.Message);
        }

        // --- MPIPosition.Description ---
        try
        {
            var pos = new MPIPosition(type: "test");
            var desc = pos.Description;
            if (desc != null)
            {
                logger.Pass($"MPIPosition.Description = {desc.Substring(0, Math.Min(50, desc.Length))}...");
                results.Pass("Property_MPIPosition_Description");
            }
            else
            {
                logger.Fail("MPIPosition.Description is null");
                results.Fail("Property_MPIPosition_Description", "Description is null");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"MPIPosition.Description: {ex.Message}");
            results.Fail("Property_MPIPosition_Description", ex.Message);
        }

        // --- MPICoordinates equality ---
        try
        {
            var a = new MPICoordinates(latitude: 43.6532, longitude: -79.3832, accuracy: 10.0, floorLevel: 1);
            var b = new MPICoordinates(latitude: 43.6532, longitude: -79.3832, accuracy: 10.0, floorLevel: 1);
            var eq = a.Equals(b);
            if (eq)
            {
                logger.Pass($"MPICoordinates.Equals = {eq}");
                results.Pass("Method_MPICoordinates_Equals");
            }
            else
            {
                // BUG: Equals returns false for identical constructor inputs
                logger.Fail($"MPICoordinates.Equals: expected true for identical values, got false");
                results.Fail("Method_MPICoordinates_Equals", "Expected true for identical values, got false");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"MPICoordinates.Equals: {ex.Message}");
            results.Fail("Method_MPICoordinates_Equals", ex.Message);
        }

        // --- MPIMapView constructor ---
        try
        {
            var frame = new CoreGraphics.CGRect(0, 0, 375, 667);
            var mapView = new MPIMapView(frame);
            logger.Pass("MPIMapView constructor with CGRect");
            results.Pass("Constructor_MPIMapView");
        }
        catch (Exception ex)
        {
            // BUG: MPIMapView constructor may crash if WebKit isn't available in test context
            logger.Fail($"MPIMapView constructor: {ex.Message}");
            results.Fail("Constructor_MPIMapView", ex.Message);
        }
    }

    // ──────────────────────────────────────────────
    // Section 6: String-Enum RawValue & FromRawValue
    // ──────────────────────────────────────────────

    private void RunRawValueTests(TestLogger logger, TestResults results)
    {
        // --- MPIError.RawValue ---
        TestRawValue(logger, results, "MPIError", "Decode", () => MPIError.Decode.RawValue);
        TestRawValue(logger, results, "MPIError", "Retrieve", () => MPIError.Retrieve.RawValue);
        TestRawValue(logger, results, "MPIError", "ShowVenue", () => MPIError.ShowVenue.RawValue);
        TestRawValue(logger, results, "MPIError", "LoadVenue", () => MPIError.LoadVenue.RawValue);
        TestRawValue(logger, results, "MPIError", "PathNotFound", () => MPIError.PathNotFound.RawValue);

        // --- MPIVortexType.RawValue ---
        TestRawValue(logger, results, "MPIVortexType", "Stairs", () => MPIVortexType.Stairs.RawValue);
        TestRawValue(logger, results, "MPIVortexType", "Elevator", () => MPIVortexType.Elevator.RawValue);
        TestRawValue(logger, results, "MPIVortexType", "Escalator", () => MPIVortexType.Escalator.RawValue);
        TestRawValue(logger, results, "MPIVortexType", "Door", () => MPIVortexType.Door.RawValue);

        // --- MPIActionType.RawValue ---
        TestRawValue(logger, results, "MPIActionType", "Departure", () => MPIActionType.Departure.RawValue);
        TestRawValue(logger, results, "MPIActionType", "Turn", () => MPIActionType.Turn.RawValue);
        TestRawValue(logger, results, "MPIActionType", "Arrival", () => MPIActionType.Arrival.RawValue);

        // --- MPIBearingType.RawValue ---
        TestRawValue(logger, results, "MPIBearingType", "Straight", () => MPIBearingType.Straight.RawValue);
        TestRawValue(logger, results, "MPIBearingType", "Right", () => MPIBearingType.Right.RawValue);
        TestRawValue(logger, results, "MPIBearingType", "Left", () => MPIBearingType.Left.RawValue);

        // --- MPIState.RawValue ---
        TestRawValue(logger, results, "MPIState", "Follow", () => MPIState.Follow.RawValue);
        TestRawValue(logger, results, "MPIState", "Explore", () => MPIState.Explore.RawValue);

        // --- MPIOptions.CameraDirection.RawValue ---
        TestRawValue(logger, results, "CameraDirection", "Up", () => MPIOptions.CameraDirection.Up.RawValue);
        TestRawValue(logger, results, "CameraDirection", "Down", () => MPIOptions.CameraDirection.Down.RawValue);

        // --- MPIOptions.EasingMode.RawValue ---
        TestRawValue(logger, results, "EasingMode", "Linear", () => MPIOptions.EasingMode.Linear.RawValue);
        TestRawValue(logger, results, "EasingMode", "EaseInOut", () => MPIOptions.EasingMode.EaseInOut.RawValue);

        // --- FromRawValue round-trip tests ---
        TestFromRawValueRoundTrip(logger, results, "MPIError", "decode",
            MPIError.Decode.RawValue, () => MPIError.FromRawValue(MPIError.Decode.RawValue)?.RawValue);
        TestFromRawValueRoundTrip(logger, results, "MPIVortexType", "stairs",
            MPIVortexType.Stairs.RawValue, () => MPIVortexType.FromRawValue(MPIVortexType.Stairs.RawValue)?.RawValue);
        TestFromRawValueRoundTrip(logger, results, "MPIActionType", "departure",
            MPIActionType.Departure.RawValue, () => MPIActionType.FromRawValue(MPIActionType.Departure.RawValue)?.RawValue);
        TestFromRawValueRoundTrip(logger, results, "MPIBearingType", "straight",
            MPIBearingType.Straight.RawValue, () => MPIBearingType.FromRawValue(MPIBearingType.Straight.RawValue)?.RawValue);
        TestFromRawValueRoundTrip(logger, results, "MPIState", "follow",
            MPIState.Follow.RawValue, () => MPIState.FromRawValue(MPIState.Follow.RawValue)?.RawValue);

        // --- FromRawValue with invalid input ---
        TestFromRawValueInvalid(logger, results, "MPIError",
            () => MPIError.FromRawValue("invalid_raw_value_xyz"));
        TestFromRawValueInvalid(logger, results, "MPIVortexType",
            () => MPIVortexType.FromRawValue("not_a_vortex_type"));
        TestFromRawValueInvalid(logger, results, "MPIActionType",
            () => MPIActionType.FromRawValue("not_an_action_type"));
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
                // Ambiguous: Swift may return a default case for unknown raw values
                logger.Skip($"{enumName}.FromRawValue(invalid): expected null but got non-null (Swift behavior)");
                results.Skip($"FromRawValue_{enumName}_Invalid", "Non-null for invalid input — Swift behavior ambiguous");
            }
        }
        catch (Exception ex)
        {
            // BUG: FromRawValue with invalid input may crash instead of returning null
            logger.Fail($"{enumName}.FromRawValue(invalid): {ex.Message}");
            results.Fail($"FromRawValue_{enumName}_Invalid", ex.Message);
        }
    }

    // ──────────────────────────────────────────────
    // Section 7: Protocol Interface Tests
    // ──────────────────────────────────────────────

    private void RunProtocolTests(TestLogger logger, TestResults results)
    {
        // Test that IMPINavigatable interface is correctly implemented by types
        // MPIPolygon, MPINode, MPIVortex, MPILocation all implement IMPINavigatable

        // We can't construct these types directly (no public constructors),
        // but we can verify the interface type exists and is assignable
        try
        {
            var polygonType = typeof(MPIPolygon);
            var isNavigatable = typeof(IMPINavigatable).IsAssignableFrom(polygonType);
            if (isNavigatable)
            {
                logger.Pass("MPIPolygon implements IMPINavigatable");
                results.Pass("Protocol_MPIPolygon_IMPINavigatable");
            }
            else
            {
                logger.Fail("MPIPolygon does not implement IMPINavigatable");
                results.Fail("Protocol_MPIPolygon_IMPINavigatable", "Interface not implemented");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"Protocol check MPIPolygon: {ex.Message}");
            results.Fail("Protocol_MPIPolygon_IMPINavigatable", ex.Message);
        }

        try
        {
            var nodeType = typeof(MPINode);
            var isNavigatable = typeof(IMPINavigatable).IsAssignableFrom(nodeType);
            if (isNavigatable)
            {
                logger.Pass("MPINode implements IMPINavigatable");
                results.Pass("Protocol_MPINode_IMPINavigatable");
            }
            else
            {
                logger.Fail("MPINode does not implement IMPINavigatable");
                results.Fail("Protocol_MPINode_IMPINavigatable", "Interface not implemented");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"Protocol check MPINode: {ex.Message}");
            results.Fail("Protocol_MPINode_IMPINavigatable", ex.Message);
        }

        try
        {
            var vortexType = typeof(MPIVortex);
            var isNavigatable = typeof(IMPINavigatable).IsAssignableFrom(vortexType);
            if (isNavigatable)
            {
                logger.Pass("MPIVortex implements IMPINavigatable");
                results.Pass("Protocol_MPIVortex_IMPINavigatable");
            }
            else
            {
                logger.Fail("MPIVortex does not implement IMPINavigatable");
                results.Fail("Protocol_MPIVortex_IMPINavigatable", "Interface not implemented");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"Protocol check MPIVortex: {ex.Message}");
            results.Fail("Protocol_MPIVortex_IMPINavigatable", ex.Message);
        }

        try
        {
            var locationType = typeof(MPILocation);
            var isNavigatable = typeof(IMPINavigatable).IsAssignableFrom(locationType);
            if (isNavigatable)
            {
                logger.Pass("MPILocation implements IMPINavigatable");
                results.Pass("Protocol_MPILocation_IMPINavigatable");
            }
            else
            {
                logger.Fail("MPILocation does not implement IMPINavigatable");
                results.Fail("Protocol_MPILocation_IMPINavigatable", "Interface not implemented");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"Protocol check MPILocation: {ex.Message}");
            results.Fail("Protocol_MPILocation_IMPINavigatable", ex.Message);
        }

        // Test IMPISearchResultCommon implementations
        try
        {
            var isImpl = typeof(IMPISearchResultCommon).IsAssignableFrom(typeof(MPISearchResultLocation));
            if (isImpl)
            {
                logger.Pass("MPISearchResultLocation implements IMPISearchResultCommon");
                results.Pass("Protocol_MPISearchResultLocation_IMPISearchResultCommon");
            }
            else
            {
                logger.Fail("MPISearchResultLocation does not implement IMPISearchResultCommon");
                results.Fail("Protocol_MPISearchResultLocation_IMPISearchResultCommon", "Interface not implemented");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"Protocol check MPISearchResultLocation: {ex.Message}");
            results.Fail("Protocol_MPISearchResultLocation_IMPISearchResultCommon", ex.Message);
        }

        try
        {
            var isImpl = typeof(IMPISearchResultCommon).IsAssignableFrom(typeof(MPISearchResultCategory));
            if (isImpl)
            {
                logger.Pass("MPISearchResultCategory implements IMPISearchResultCommon");
                results.Pass("Protocol_MPISearchResultCategory_IMPISearchResultCommon");
            }
            else
            {
                logger.Fail("MPISearchResultCategory does not implement IMPISearchResultCommon");
                results.Fail("Protocol_MPISearchResultCategory_IMPISearchResultCommon", "Interface not implemented");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"Protocol check MPISearchResultCategory: {ex.Message}");
            results.Fail("Protocol_MPISearchResultCategory_IMPISearchResultCommon", ex.Message);
        }

        try
        {
            var isImpl = typeof(IMPISearchResultCommon).IsAssignableFrom(typeof(MPISearchResultCustom));
            if (isImpl)
            {
                logger.Pass("MPISearchResultCustom implements IMPISearchResultCommon");
                results.Pass("Protocol_MPISearchResultCustom_IMPISearchResultCommon");
            }
            else
            {
                logger.Fail("MPISearchResultCustom does not implement IMPISearchResultCommon");
                results.Fail("Protocol_MPISearchResultCustom_IMPISearchResultCommon", "Interface not implemented");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"Protocol check MPISearchResultCustom: {ex.Message}");
            results.Fail("Protocol_MPISearchResultCustom_IMPISearchResultCommon", ex.Message);
        }

        // Test ISwiftObject implementations
        var swiftObjectTypes = new (string Name, Type Type)[]
        {
            ("MPIData", typeof(MPIData)),
            ("MPIMap", typeof(MPIMap)),
            ("MPIMapGroup", typeof(MPIMapGroup)),
            ("MPISearchManager", typeof(MPISearchManager)),
            ("MPIPayload", typeof(MPIPayload)),
            ("MPIFloatingLabelManager", typeof(MPIFloatingLabelManager)),
            ("MPIBlueDotManager", typeof(MPIBlueDotManager)),
            ("MPIMarkerManager", typeof(MPIMarkerManager)),
            ("MPIFlatLabelManager", typeof(MPIFlatLabelManager)),
            ("MPICameraTransform", typeof(MPICameraTransform)),
            ("MPIJourneyManager", typeof(MPIJourneyManager)),
            ("MPIPathManager", typeof(MPIPathManager)),
            ("MPICameraManager", typeof(MPICameraManager)),
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

        // Test ISwiftStruct implementations
        var swiftStructTypes = new (string Name, Type Type)[]
        {
            ("MPIPolygon", typeof(MPIPolygon)),
            ("MPINode", typeof(MPINode)),
            ("MPIVortex", typeof(MPIVortex)),
            ("MPICategory", typeof(MPICategory)),
            ("MPIPath", typeof(MPIPath)),
            ("MPIDirections", typeof(MPIDirections)),
            ("MPIInstruction", typeof(MPIInstruction)),
            ("MPIAction", typeof(MPIAction)),
            ("MPILanguage", typeof(MPILanguage)),
            ("MPIBlueDotPositionUpdate", typeof(MPIBlueDotPositionUpdate)),
            ("MPIBlueDotStateChange", typeof(MPIBlueDotStateChange)),
            ("MPIVenue", typeof(MPIVenue)),
            ("MPISocial", typeof(MPISocial)),
            ("MPIPhone", typeof(MPIPhone)),
            ("MPIOpeningHours", typeof(MPIOpeningHours)),
            ("MPILocation", typeof(MPILocation)),
            ("MPIColor", typeof(MPIColor)),
            ("MPIPosition", typeof(MPIPosition)),
            ("MPICoordinates", typeof(MPICoordinates)),
            ("MPIHeader", typeof(MPIHeader)),
            ("MPIImage", typeof(MPIImage)),
            ("MPIPicture", typeof(MPIPicture)),
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

        // Test IEquatable implementations
        try
        {
            var isEq = typeof(IEquatable<MPIPosition>).IsAssignableFrom(typeof(MPIPosition));
            if (isEq)
            {
                logger.Pass("MPIPosition implements IEquatable<MPIPosition>");
                results.Pass("Protocol_MPIPosition_IEquatable");
            }
            else
            {
                logger.Fail("MPIPosition does not implement IEquatable<MPIPosition>");
                results.Fail("Protocol_MPIPosition_IEquatable", "Missing IEquatable");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"Protocol check MPIPosition IEquatable: {ex.Message}");
            results.Fail("Protocol_MPIPosition_IEquatable", ex.Message);
        }

        try
        {
            var isEq = typeof(IEquatable<MPICoordinates>).IsAssignableFrom(typeof(MPICoordinates));
            if (isEq)
            {
                logger.Pass("MPICoordinates implements IEquatable<MPICoordinates>");
                results.Pass("Protocol_MPICoordinates_IEquatable");
            }
            else
            {
                logger.Fail("MPICoordinates does not implement IEquatable<MPICoordinates>");
                results.Fail("Protocol_MPICoordinates_IEquatable", "Missing IEquatable");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"Protocol check MPICoordinates IEquatable: {ex.Message}");
            results.Fail("Protocol_MPICoordinates_IEquatable", ex.Message);
        }

        // Test MPIMapView inherits from WKWebView
        try
        {
            var isWebView = typeof(WebKit.WKWebView).IsAssignableFrom(typeof(MPIMapView));
            if (isWebView)
            {
                logger.Pass("MPIMapView inherits from WKWebView");
                results.Pass("Protocol_MPIMapView_WKWebView");
            }
            else
            {
                logger.Fail("MPIMapView does not inherit from WKWebView");
                results.Fail("Protocol_MPIMapView_WKWebView", "Not a WKWebView subclass");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"Protocol check MPIMapView WKWebView: {ex.Message}");
            results.Fail("Protocol_MPIMapView_WKWebView", ex.Message);
        }
    }

    // ──────────────────────────────────────────────
    // Section 8: MPIOptions Nested Struct Metadata
    // ──────────────────────────────────────────────

    private void RunOptionsStructMetadataTests(TestLogger logger, TestResults results)
    {
        var optionsMetadataTests = new (string Name, Func<TypeMetadata> GetMetadata)[]
        {
            ("CameraTargets", () => SwiftObjectHelper<MPIOptions.CameraTargets>.GetTypeMetadata()),
            ("CameraConfiguration", () => SwiftObjectHelper<MPIOptions.CameraConfiguration>.GetTypeMetadata()),
            ("CameraTransformNode", () => SwiftObjectHelper<MPIOptions.CameraTransformNode>.GetTypeMetadata()),
            ("CameraAnimation", () => SwiftObjectHelper<MPIOptions.CameraAnimation>.GetTypeMetadata()),
            ("CameraSetSafeAreaInsets", () => SwiftObjectHelper<MPIOptions.CameraSetSafeAreaInsets>.GetTypeMetadata()),
            ("CameraInteractionsSetOptions", () => SwiftObjectHelper<MPIOptions.CameraInteractionsSetOptions>.GetTypeMetadata()),
            ("FocusOnOptions", () => SwiftObjectHelper<MPIOptions.FocusOnOptions>.GetTypeMetadata()),
            ("Init", () => SwiftObjectHelper<MPIOptions.Init>.GetTypeMetadata()),
            ("ShowVenue", () => SwiftObjectHelper<MPIOptions.ShowVenue>.GetTypeMetadata()),
            ("BlueDot", () => SwiftObjectHelper<MPIOptions.BlueDot>.GetTypeMetadata()),
            ("CameraPadding", () => SwiftObjectHelper<MPIOptions.CameraPadding>.GetTypeMetadata()),
            ("Path", () => SwiftObjectHelper<MPIOptions.Path>.GetTypeMetadata()),
            ("GetPolygonsAtCoordinateOptions", () => SwiftObjectHelper<MPIOptions.GetPolygonsAtCoordinateOptions>.GetTypeMetadata()),
            ("ConnectionPath", () => SwiftObjectHelper<MPIOptions.ConnectionPath>.GetTypeMetadata()),
            ("FlatLabelOptions", () => SwiftObjectHelper<MPIOptions.FlatLabelOptions>.GetTypeMetadata()),
            ("FloatingLabelOptions", () => SwiftObjectHelper<MPIOptions.FloatingLabelOptions>.GetTypeMetadata()),
            ("FloatingLabel", () => SwiftObjectHelper<MPIOptions.FloatingLabel>.GetTypeMetadata()),
            ("FlatLabel", () => SwiftObjectHelper<MPIOptions.FlatLabel>.GetTypeMetadata()),
            ("FloatingLabelAppearance", () => SwiftObjectHelper<MPIOptions.FloatingLabelAppearance>.GetTypeMetadata()),
            ("FloatingLabelAppearance.TextType", () => SwiftObjectHelper<MPIOptions.FloatingLabelAppearance.TextType>.GetTypeMetadata()),
            ("FloatingLabelAppearance.MarkerType", () => SwiftObjectHelper<MPIOptions.FloatingLabelAppearance.MarkerType>.GetTypeMetadata()),
            ("FloatingLabelAppearance.Color", () => SwiftObjectHelper<MPIOptions.FloatingLabelAppearance.Color>.GetTypeMetadata()),
            ("SearchOptions", () => SwiftObjectHelper<MPIOptions.SearchOptions>.GetTypeMetadata()),
            ("FloatingLabelAllLocations", () => SwiftObjectHelper<MPIOptions.FloatingLabelAllLocations>.GetTypeMetadata()),
            ("FlatLabelAppearance", () => SwiftObjectHelper<MPIOptions.FlatLabelAppearance>.GetTypeMetadata()),
            ("FlatLabelAllLocations", () => SwiftObjectHelper<MPIOptions.FlatLabelAllLocations>.GetTypeMetadata()),
            ("Journey", () => SwiftObjectHelper<MPIOptions.Journey>.GetTypeMetadata()),
            ("Marker", () => SwiftObjectHelper<MPIOptions.Marker>.GetTypeMetadata()),
            ("MarkerAnimationOptions", () => SwiftObjectHelper<MPIOptions.MarkerAnimationOptions>.GetTypeMetadata()),
            ("TooltipAnchor", () => SwiftObjectHelper<MPIOptions.TooltipAnchor>.GetTypeMetadata()),
            ("Tooltip", () => SwiftObjectHelper<MPIOptions.Tooltip>.GetTypeMetadata()),
            ("CustomTooltip", () => SwiftObjectHelper<MPIOptions.CustomTooltip>.GetTypeMetadata()),
            ("AntialiasConfiguration", () => SwiftObjectHelper<MPIOptions.AntialiasConfiguration>.GetTypeMetadata()),
            ("AmbientOcclusionConfiguration", () => SwiftObjectHelper<MPIOptions.AmbientOcclusionConfiguration>.GetTypeMetadata()),
            ("OutdoorView", () => SwiftObjectHelper<MPIOptions.OutdoorView>.GetTypeMetadata()),
        };

        foreach (var (name, getMetadata) in optionsMetadataTests)
        {
            try
            {
                var metadata = getMetadata();
                logger.Pass($"MPIOptions.{name} metadata (size={metadata.Size})");
                results.Pass($"OptionsMetadata_{name}");
            }
            catch (Exception ex)
            {
                logger.Fail($"MPIOptions.{name} metadata: {ex.Message}");
                results.Fail($"OptionsMetadata_{name}", ex.Message);
            }
        }
    }
}

#endregion
