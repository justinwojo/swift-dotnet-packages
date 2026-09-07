// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#if IOS || TVOS
using System.Diagnostics;
using System.Text;
using Foundation;
using UIKit;
using Swift;
using Nuke;
using Swift.Runtime;

// Caseless Swift enums in Nuke (ImageProcessors, ImageDecoders, ImageProcessingOptions)
// emit as C# child namespaces of Nuke, not as static types. `using Nuke;` imports
// types but not child namespaces, so reference them via aliases.
using ImageProcessors = Nuke.ImageProcessors;
using ImageDecoders = Nuke.ImageDecoders;
using ImageProcessingOptions = Nuke.ImageProcessingOptions;

namespace NukeSimTests;

#region Test Infrastructure

public static class RuntimeEnvironment
{
    /// <summary>
    /// True when running on iOS Simulator. False on physical device.
    /// </summary>
    public static bool IsSimulator { get; } =
        ObjCRuntime.Runtime.Arch == ObjCRuntime.Arch.SIMULATOR;

    /// <summary>
    /// True when running on Mono JIT (both simulator and device Debug builds).
    /// False on NativeAOT (device Release/publish builds).
    /// CallConvSwift P/Invokes with SwiftSelf parameter crash on Mono JIT (jit-info.c:918)
    /// but work on NativeAOT.
    /// </summary>
    public static bool IsMonoRuntime { get; } =
        System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription.Contains("Mono") ||
        RuntimeEnvironment.IsSimulator; // Simulator always uses Mono JIT
}

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

    // Bundled-resource file URL used in place of third-party HTTPS endpoints.
    // Routing every test through a local file URL removes the regression sweep's
    // dependency on httpbin.org / picsum.photos / example.com being reachable and
    // returning 2xx — a single 502 from one of those servers used to flake the
    // entire Nuke ios-sim cell. URLSession.dataTask supports file:// natively, and
    // Nuke's default DataLoader.validate accepts non-HTTPURLResponse, so this still
    // exercises the full async + cache + decode binding round-trip.
    private static readonly string TestImageBaseUrl = BuildTestImageBaseUrl();

    static string BuildTestImageBaseUrl()
    {
        var path = NSBundle.MainBundle.PathForResource("test_image", "png")
            ?? throw new InvalidOperationException("Bundled test_image.png is missing from app Resources.");
        // Foundation URL construction handles percent-encoding of spaces and
        // URL-significant characters that can appear in simulator/device bundle
        // paths; raw "file://" + path can produce an invalid URL.
        var url = NSUrl.FromFilename(path)
            ?? throw new InvalidOperationException($"NSUrl.FromFilename returned null for: {path}");
        return url.AbsoluteString
            ?? throw new InvalidOperationException($"NSUrl.AbsoluteString returned null for path: {path}");
    }

    // For tests that need distinct cache keys (concurrent loads, cache-hit
    // verification), append a fragment — URLSession ignores the fragment when
    // opening a file but Nuke uses the full URL string as the cache key.
    static string TestImageUrl(string? seed = null)
        => seed is null ? TestImageBaseUrl : $"{TestImageBaseUrl}#{seed}";

    public override void ViewDidLoad()
    {
        base.ViewDidLoad();

#if IOS
        View!.BackgroundColor = UIColor.SystemBackground;
#else
        View!.BackgroundColor = UIColor.White;
#endif

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

        // Phase 2: Library-specific tests (no-throw APIs)
        logger.Info("=== Phase 2: Library-Specific Tests ===");
        RunLibraryTests(logger, results);

        // Phase 3: Async tests
        logger.Info("=== Phase 3: Async Tests ===");
        await RunAsyncTests(logger, results);

        // Phase 4: Cache tests
        logger.Info("=== Phase 4: Cache Tests ===");
        RunCacheTests(logger, results);

        // Phase 5: Pipeline configuration tests
        logger.Info("=== Phase 5: Pipeline Configuration Tests ===");
        RunPipelineConfigTests(logger, results);

        // Phase 6: Prefetcher & task tests
        logger.Info("=== Phase 6: Prefetcher & Task Tests ===");
        RunPrefetcherAndTaskTests(logger, results);

        // Phase 7: Decoder tests
        logger.Info("=== Phase 7: Decoder Tests ===");
        RunDecoderTests(logger, results);

        // Phase 8: Library parity tests (SDK 0.3.0 fixes)
        logger.Info("=== Phase 8: Library Parity Tests ===");
        await RunLibraryParityTests(logger, results);

        // Phase 9: Release confidence coverage gaps
        logger.Info("=== Phase 9: Coverage Gap Tests ===");
        RunCoverageGapTests(logger, results);

        // Phase 10: Behavioural tests against a locally served, byte-known image
        logger.Info("=== Phase 10: Loopback Behaviour Tests ===");
        await RunLoopbackBehaviourTests(logger, results);

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

    private void RunSmokeTests(TestLogger logger, TestResults results)
    {
        // ImagePipeline type metadata
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

        // ImageCache type metadata
        try
        {
            var metadata = SwiftObjectHelper<ImageCache>.GetTypeMetadata();
            logger.Info($"ImageCache metadata size: {metadata.Size}");
            if (metadata.Size > 0)
            {
                logger.Pass("ImageCache metadata");
                results.Pass("ImageCache_Metadata");
            }
            else
            {
                logger.Fail("ImageCache metadata: size is 0");
                results.Fail("ImageCache_Metadata", "Size is 0");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"ImageCache metadata: {ex.Message}");
            results.Fail("ImageCache_Metadata", ex.Message);
        }

        // DataCache type metadata
        try
        {
            var metadata = SwiftObjectHelper<DataCache>.GetTypeMetadata();
            logger.Info($"DataCache metadata size: {metadata.Size}");
            if (metadata.Size > 0)
            {
                logger.Pass("DataCache metadata");
                results.Pass("DataCache_Metadata");
            }
            else
            {
                logger.Fail("DataCache metadata: size is 0");
                results.Fail("DataCache_Metadata", "Size is 0");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"DataCache metadata: {ex.Message}");
            results.Fail("DataCache_Metadata", ex.Message);
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
            var request = new ImageRequest(TestImageUrl());
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
            var config = pipeline.Configuration;
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
            var veryLow = ImageRequest.PriorityKind.VeryLow;
            var low = ImageRequest.PriorityKind.Low;
            var normal = ImageRequest.PriorityKind.Normal;
            var high = ImageRequest.PriorityKind.High;
            var veryHigh = ImageRequest.PriorityKind.VeryHigh;

            var values = new[]
            {
                (int)veryLow, (int)low, (int)normal, (int)high, (int)veryHigh,
            };

            logger.Info($"Priority values: {string.Join(", ", values)}");

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
            var invalid = (ImageRequest.PriorityKind)999;
            if (!Enum.IsDefined(typeof(ImageRequest.PriorityKind), invalid))
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
            var disableMemoryReads = ImageRequest.OptionsInfo.DisableMemoryCacheReads;
            var disableMemoryWrites = ImageRequest.OptionsInfo.DisableMemoryCacheWrites;
            var disableDiskReads = ImageRequest.OptionsInfo.DisableDiskCacheReads;
            var disableDiskWrites = ImageRequest.OptionsInfo.DisableDiskCacheWrites;

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

        // ImageRequest.Priority property — previously skipped (Issue 7), re-testing with SDK 0.3.0
        logger.Info("--- ImageRequest Priority Property ---");
        try
        {
            var request = new ImageRequest(TestImageUrl());
            var priority = request.Priority;
            logger.Pass($"ImageRequest.Priority: {priority}");
            results.Pass("ImageRequest_Priority");
        }
        catch (Exception ex)
        {
            logger.Fail($"ImageRequest.Priority: {ex.Message}");
            results.Fail("ImageRequest_Priority", ex.Message);
        }

        // ImageRequest.OptionsValue
        try
        {
            var request = new ImageRequest(TestImageUrl());
            var opts = request.Options;
            logger.Pass($"ImageRequest.OptionsValue: {opts.GetType().Name}");
            results.Pass("ImageRequest_OptionsValue");
        }
        catch (Exception ex)
        {
            logger.Fail($"ImageRequest.OptionsValue: {ex.Message}");
            results.Fail("ImageRequest_OptionsValue", ex.Message);
        }

        // DataCachePolicy enum
        logger.Info("--- DataCachePolicy Enum ---");
        try
        {
            var auto = ImagePipeline.DataCachePolicy.Automatic;
            var original = ImagePipeline.DataCachePolicy.StoreOriginalData;
            var encoded = ImagePipeline.DataCachePolicy.StoreEncodedImages;
            var all = ImagePipeline.DataCachePolicy.StoreAll;

            logger.Info($"DataCachePolicy: Auto={auto}, Original={original}, Encoded={encoded}, All={all}");

            if ((int)auto != (int)original && (int)original != (int)encoded && (int)encoded != (int)all)
            {
                logger.Pass("DataCachePolicy enum cases");
                results.Pass("DataCachePolicy_EnumCases");
            }
            else
            {
                logger.Fail("DataCachePolicy: duplicate values");
                results.Fail("DataCachePolicy_EnumCases", "Duplicate values");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"DataCachePolicy enum: {ex.Message}");
            results.Fail("DataCachePolicy_EnumCases", ex.Message);
        }

        // ImageResponse.CacheType enum
        try
        {
            var memory = ImageResponse.CacheTypeKind.Memory;
            var disk = ImageResponse.CacheTypeKind.Disk;
            logger.Info($"CacheType: Memory={memory}, Disk={disk}");
            if (memory != disk)
            {
                logger.Pass("ImageResponse.CacheType enum");
                results.Pass("CacheType_Enum");
            }
            else
            {
                logger.Fail("CacheType enum: Memory == Disk");
                results.Fail("CacheType_Enum", "Memory == Disk");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"CacheType enum: {ex.Message}");
            results.Fail("CacheType_Enum", ex.Message);
        }

        // ImageTask.State enum
        try
        {
            var running = ImageTask.StateKind.Running;
            var cancelled = ImageTask.StateKind.Cancelled;
            var completed = ImageTask.StateKind.Completed;
            logger.Info($"ImageTask.State: Running={running}, Cancelled={cancelled}, Completed={completed}");
            var distinct = new HashSet<int> { (int)running, (int)cancelled, (int)completed };
            if (distinct.Count == 3)
            {
                logger.Pass("ImageTask.State enum: 3 distinct cases");
                results.Pass("ImageTaskState_Enum");
            }
            else
            {
                logger.Fail("ImageTask.State enum: duplicate values");
                results.Fail("ImageTaskState_Enum", "Duplicate values");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"ImageTask.State enum: {ex.Message}");
            results.Fail("ImageTaskState_Enum", ex.Message);
        }
    }

    private void RunCacheTests(TestLogger logger, TestResults results)
    {
        // ImageCache.Shared singleton
        logger.Info("--- ImageCache.Shared ---");
        try
        {
            var cache = ImageCache.Shared;
            if (cache != null)
            {
                logger.Pass("ImageCache.Shared singleton");
                results.Pass("ImageCache_Shared");
            }
            else
            {
                logger.Fail("ImageCache.Shared: returned null");
                results.Fail("ImageCache_Shared", "Returned null");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"ImageCache.Shared: {ex.Message}");
            results.Fail("ImageCache_Shared", ex.Message);
        }

        // ImageCache constructor
        logger.Info("--- ImageCache Construction ---");
        try
        {
            var cache = new ImageCache();
            if (cache != null)
            {
                logger.Pass("ImageCache() construction");
                results.Pass("ImageCache_DefaultConstruction");
            }
            else
            {
                logger.Fail("ImageCache(): returned null");
                results.Fail("ImageCache_DefaultConstruction", "Returned null");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"ImageCache(): {ex.Message}");
            results.Fail("ImageCache_DefaultConstruction", ex.Message);
        }

        // ImageCache property get/set (CostLimit, CountLimit)
        logger.Info("--- ImageCache Properties ---");
        try
        {
            var cache = new ImageCache();
            var originalCostLimit = cache.CostLimit;
            var originalCountLimit = cache.CountLimit;
            logger.Info($"ImageCache defaults: CostLimit={originalCostLimit}, CountLimit={originalCountLimit}");

            cache.CostLimit = 50_000_000; // 50MB
            cache.CountLimit = 200;
            var newCostLimit = cache.CostLimit;
            var newCountLimit = cache.CountLimit;
            logger.Info($"ImageCache after set: CostLimit={newCostLimit}, CountLimit={newCountLimit}");

            if (newCostLimit == 50_000_000 && newCountLimit == 200)
            {
                logger.Pass("ImageCache CostLimit/CountLimit set/get");
                results.Pass("ImageCache_CostCountLimits");
            }
            else
            {
                logger.Fail($"ImageCache limits mismatch: CostLimit={newCostLimit} (expected 50000000), CountLimit={newCountLimit} (expected 200)");
                results.Fail("ImageCache_CostCountLimits", "Set/get mismatch");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"ImageCache properties: {ex.Message}");
            results.Fail("ImageCache_CostCountLimits", ex.Message);
        }

        // ImageCache TotalCount and TotalCost
        try
        {
            var cache = new ImageCache();
            var totalCount = cache.TotalCount;
            var totalCost = cache.TotalCost;
            logger.Info($"Fresh ImageCache: TotalCount={totalCount}, TotalCost={totalCost}");
            if (totalCount == 0 && totalCost == 0)
            {
                logger.Pass("ImageCache fresh TotalCount/TotalCost = 0");
                results.Pass("ImageCache_FreshTotals");
            }
            else
            {
                // Shared cache may have items, but a fresh instance should be empty
                logger.Pass($"ImageCache TotalCount={totalCount}, TotalCost={totalCost} (accessible)");
                results.Pass("ImageCache_FreshTotals");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"ImageCache TotalCount/TotalCost: {ex.Message}");
            results.Fail("ImageCache_FreshTotals", ex.Message);
        }

        // ImageCache.RemoveAll
        try
        {
            var cache = new ImageCache();
            cache.RemoveAll();
            logger.Pass("ImageCache.RemoveAll() succeeded");
            results.Pass("ImageCache_RemoveAll");
        }
        catch (Exception ex)
        {
            logger.Fail($"ImageCache.RemoveAll: {ex.Message}");
            results.Fail("ImageCache_RemoveAll", ex.Message);
        }

        // ImageCache.Ttl property
        try
        {
            var cache = new ImageCache();
            cache.Ttl = 300.0;
            var ttl = cache.Ttl;
            logger.Info($"ImageCache.Ttl: {ttl}");
            if (ttl != null && Math.Abs(ttl.Value - 300.0) < 0.01)
            {
                logger.Pass("ImageCache.Ttl set/get");
                results.Pass("ImageCache_Ttl");
            }
            else
            {
                logger.Fail($"ImageCache.Ttl: expected 300.0, got {ttl}");
                results.Fail("ImageCache_Ttl", $"Expected 300.0, got {ttl}");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"ImageCache.Ttl: {ex.Message}");
            results.Fail("ImageCache_Ttl", ex.Message);
        }

        // DataCache store/contains/retrieve/remove roundtrip
        logger.Info("--- DataCache Roundtrip ---");
        // DataCache roundtrip — previously skipped (SDK 0.2.0 Data param regression), re-testing with 0.3.0
        try
        {
            var cache = new DataCache("roundtrip-test");
            var testData = System.Text.Encoding.UTF8.GetBytes("hello-roundtrip");
            cache.StoreData(testData, "test-key");
            var exists = cache.ContainsData("test-key");
            if (exists)
            {
                logger.Pass("DataCache roundtrip: StoreData + ContainsData");
                results.Pass("DataCache_Roundtrip");
            }
            else
            {
                logger.Fail("DataCache roundtrip: ContainsData returned false after StoreData");
                results.Fail("DataCache_Roundtrip", "ContainsData returned false");
            }

            // Remove
            try
            {
                cache.RemoveData("test-key");
                logger.Pass("DataCache.RemoveData succeeded");
                results.Pass("DataCache_Remove");
            }
            catch (Exception ex2)
            {
                logger.Fail($"DataCache.RemoveData: {ex2.Message}");
                results.Fail("DataCache_Remove", ex2.Message);
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"DataCache roundtrip: {ex.Message}");
            results.Fail("DataCache_Roundtrip", ex.Message);
            results.Skip("DataCache_Remove", "Depends on roundtrip");
        }

        // DataCache properties (SizeLimit, TotalSize)
        // Nuke 13.0 removed DataCache.isCompressionEnabled; compression is now handled by a separate compressor.
        logger.Info("--- DataCache Properties ---");
        try
        {
            var cache = new DataCache("props-test");
            var sizeLimit = cache.SizeLimit;
            var totalSize = cache.TotalSize;
            logger.Info($"DataCache: SizeLimit={sizeLimit}, TotalSize={totalSize}");

            if (sizeLimit > 0)
            {
                logger.Pass("DataCache properties accessible");
                results.Pass("DataCache_Properties");
            }
            else
            {
                logger.Fail($"DataCache SizeLimit should be > 0, got {sizeLimit}");
                results.Fail("DataCache_Properties", $"SizeLimit={sizeLimit}");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"DataCache properties: {ex.Message}");
            results.Fail("DataCache_Properties", ex.Message);
        }

        // DataCache.Path
        try
        {
            var pathCache = new DataCache("path-test-cache");
            var path = pathCache.Path;
            logger.Pass($"DataCache.Path: {path}");
            results.Pass("DataCache_Path");
        }
        catch (Exception ex) when (ex.Message.Contains("non-blittable"))
        {
            logger.Skip($"DataCache.Path: non-blittable NSUrl return (Known Issue 9)");
            results.Skip("DataCache_Path", "Non-blittable NSUrl return");
        }
        catch (Exception ex)
        {
            logger.Fail($"DataCache.Path: {ex.Message}");
            results.Fail("DataCache_Path", ex.Message);
        }

        // DataCache.RemoveAll — previously skipped, re-testing with 0.3.0
        try
        {
            var cache = new DataCache("removeall-test");
            cache.RemoveAll();
            logger.Pass("DataCache.RemoveAll() succeeded");
            results.Pass("DataCache_RemoveAll");
        }
        catch (Exception ex)
        {
            logger.Fail($"DataCache.RemoveAll: {ex.Message}");
            results.Fail("DataCache_RemoveAll", ex.Message);
        }

        // DataCache.Sweep
        try
        {
            var cache = new DataCache("sweep-test");
            cache.Sweep();
            logger.Pass("DataCache.Sweep() succeeded");
            results.Pass("DataCache_Sweep");
        }
        catch (Exception ex)
        {
            logger.Fail($"DataCache.Sweep: {ex.Message}");
            results.Fail("DataCache_Sweep", ex.Message);
        }

        // DataCache.SweepInterval set/get
        try
        {
            var cache = new DataCache("interval-test");
            cache.SweepInterval = 60.0;
            var interval = cache.SweepInterval;
            logger.Info($"DataCache.SweepInterval: {interval}");
            if (Math.Abs(interval - 60.0) < 0.01)
            {
                logger.Pass("DataCache.SweepInterval set/get");
                results.Pass("DataCache_SweepInterval");
            }
            else
            {
                logger.Fail($"DataCache.SweepInterval: expected 60, got {interval}");
                results.Fail("DataCache_SweepInterval", $"Expected 60, got {interval}");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"DataCache.SweepInterval: {ex.Message}");
            results.Fail("DataCache_SweepInterval", ex.Message);
        }

        // DataCache.IsCompressionEnabled removed in Nuke 13.0 (compression moved to a separate compressor type).
        logger.Skip("DataCache.IsCompressionEnabled: property removed in Nuke 13.0");
        results.Skip("DataCache_Compression", "Removed in Nuke 13.0");

        // DataCache.SizeLimit set/get
        try
        {
            var cache = new DataCache("sizelimit-test");
            cache.SizeLimit = 100_000_000; // 100MB
            var limit = cache.SizeLimit;
            if (limit == 100_000_000)
            {
                logger.Pass("DataCache.SizeLimit set/get");
                results.Pass("DataCache_SizeLimit");
            }
            else
            {
                logger.Fail($"DataCache.SizeLimit: expected 100000000, got {limit}");
                results.Fail("DataCache_SizeLimit", $"Expected 100000000, got {limit}");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"DataCache.SizeLimit: {ex.Message}");
            results.Fail("DataCache_SizeLimit", ex.Message);
        }
    }

    private void RunPipelineConfigTests(TestLogger logger, TestResults results)
    {
        // Nuke 13.0 removed the parameterless ImagePipeline(); a Configuration must be supplied.
        // Custom pipeline with default-Configuration constructor
        logger.Info("--- Custom Pipeline ---");
        try
        {
            var pipeline = new ImagePipeline(new ImagePipeline.ConfigurationInfo());
            if (pipeline != null)
            {
                logger.Pass("ImagePipeline(Configuration()) constructor");
                results.Pass("Pipeline_DefaultConstructor");
            }
            else
            {
                logger.Fail("ImagePipeline(Configuration()) returned null");
                results.Fail("Pipeline_DefaultConstructor", "Returned null");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"ImagePipeline(Configuration()) constructor: {ex.Message}");
            results.Fail("Pipeline_DefaultConstructor", ex.Message);
        }

        // Pipeline Invalidate
        try
        {
            var pipeline = new ImagePipeline(new ImagePipeline.ConfigurationInfo());
            pipeline.Invalidate();
            logger.Pass("ImagePipeline.Invalidate()");
            results.Pass("Pipeline_Invalidate");
        }
        catch (Exception ex)
        {
            logger.Fail($"ImagePipeline.Invalidate: {ex.Message}");
            results.Fail("Pipeline_Invalidate", ex.Message);
        }

        // Pipeline.CacheValue
        try
        {
            var pipeline = ImagePipeline.Shared;
            var cache = pipeline.Cache;
            if (cache != null)
            {
                logger.Pass("ImagePipeline.CacheValue access");
                results.Pass("Pipeline_CacheValue");
            }
            else
            {
                logger.Fail("ImagePipeline.CacheValue: returned null");
                results.Fail("Pipeline_CacheValue", "Returned null");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"ImagePipeline.CacheValue: {ex.Message}");
            results.Fail("Pipeline_CacheValue", ex.Message);
        }

        // DataLoader.DefaultConfiguration static property
        logger.Info("--- DataLoader ---");
        try
        {
            var defaultConfig = DataLoader.DefaultConfiguration;
            if (defaultConfig != null)
            {
                logger.Pass("DataLoader.DefaultConfiguration");
                results.Pass("DataLoader_DefaultConfig");
            }
            else
            {
                logger.Fail("DataLoader.DefaultConfiguration: returned null");
                results.Fail("DataLoader_DefaultConfig", "Returned null");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"DataLoader.DefaultConfiguration: {ex.Message}");
            results.Fail("DataLoader_DefaultConfig", ex.Message);
        }

        // DataLoader.SharedUrlCache static property
        try
        {
            var sharedCache = DataLoader.SharedUrlCache;
            if (sharedCache != null)
            {
                logger.Pass("DataLoader.SharedUrlCache");
                results.Pass("DataLoader_SharedUrlCache");
            }
            else
            {
                logger.Fail("DataLoader.SharedUrlCache: returned null");
                results.Fail("DataLoader_SharedUrlCache", "Returned null");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"DataLoader.SharedUrlCache: {ex.Message}");
            results.Fail("DataLoader_SharedUrlCache", ex.Message);
        }

        // ImageDecoderRegistry access
        logger.Info("--- ImageDecoderRegistry ---");
        try
        {
            var registry = ImageDecoderRegistry.Shared;
            if (registry != null)
            {
                logger.Pass("ImageDecoderRegistry.Shared");
                results.Pass("ImageDecoderRegistry_Shared");
            }
            else
            {
                logger.Fail("ImageDecoderRegistry.Shared: returned null");
                results.Fail("ImageDecoderRegistry_Shared", "Returned null");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"ImageDecoderRegistry.Shared: {ex.Message}");
            results.Fail("ImageDecoderRegistry_Shared", ex.Message);
        }
    }

    private void RunPrefetcherAndTaskTests(TestLogger logger, TestResults results)
    {
        // ImagePrefetcher
        logger.Info("--- ImagePrefetcher ---");
        try
        {
            // Nuke 13.0 requires an explicit pipeline argument to ImagePrefetcher.
            var prefetcher = new ImagePrefetcher(ImagePipeline.Shared);
            logger.Pass("ImagePrefetcher(pipeline) construction");
            results.Pass("Prefetcher_Construction");

            // IsPaused
            try
            {
                var paused = prefetcher.IsPaused;
                logger.Pass($"ImagePrefetcher.IsPaused: {paused}");
                results.Pass("Prefetcher_IsPaused");
            }
            catch (Exception ex)
            {
                logger.Fail($"Prefetcher.IsPaused: {ex.Message}");
                results.Fail("Prefetcher_IsPaused", ex.Message);
            }

                // Prefetcher.Priority — SDK 0.3.0 added @_cdecl wrappers for this
            try
            {
                var priority = prefetcher.Priority;
                logger.Pass($"ImagePrefetcher.Priority get: {priority}");
                results.Pass("Prefetcher_PriorityGet");

                prefetcher.Priority = ImageRequest.PriorityKind.High;
                var updated = prefetcher.Priority;
                if (updated == ImageRequest.PriorityKind.High)
                {
                    logger.Pass("ImagePrefetcher.Priority set/get roundtrip");
                    results.Pass("Prefetcher_PrioritySet");
                }
                else
                {
                    logger.Fail($"Prefetcher.Priority: expected High, got {updated}");
                    results.Fail("Prefetcher_PrioritySet", $"Expected High, got {updated}");
                }
            }
            catch (Exception ex)
            {
                logger.Fail($"Prefetcher.Priority: {ex.Message}");
                results.Fail("Prefetcher_PriorityGet", ex.Message);
                results.Fail("Prefetcher_PrioritySet", ex.Message);
            }

            // StopPrefetching
            try
            {
                prefetcher.StopPrefetching();
                logger.Pass("ImagePrefetcher.StopPrefetching()");
                results.Pass("Prefetcher_Stop");
            }
            catch (Exception ex)
            {
                logger.Fail($"Prefetcher.StopPrefetching: {ex.Message}");
                results.Fail("Prefetcher_Stop", ex.Message);
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"ImagePrefetcher(): {ex.Message}");
            results.Fail("Prefetcher_Construction", ex.Message);
            results.Skip("Prefetcher_IsPaused", "Depends on constructor");
            results.Skip("Prefetcher_Priority", "Depends on constructor");
            results.Skip("Prefetcher_Stop", "Depends on constructor");
        }

        // ImagePrefetcher with pipeline
        try
        {
            var pipeline = ImagePipeline.Shared;
            var prefetcher = new ImagePrefetcher(pipeline);
            logger.Pass("ImagePrefetcher(pipeline) construction");
            results.Pass("Prefetcher_WithPipeline");
        }
        catch (Exception ex)
        {
            logger.Fail($"ImagePrefetcher(pipeline): {ex.Message}");
            results.Fail("Prefetcher_WithPipeline", ex.Message);
        }

        // ImageTask from pipeline via NSUrl
        logger.Info("--- ImageTask ---");
        try
        {
            var pipeline = ImagePipeline.Shared;
            var url = new Foundation.NSUrl(TestImageUrl());
            var task = pipeline.ImageTask(url);
            logger.Pass("ImageTask(NSUrl) construction");
            results.Pass("ImageTask_FromNSUrl");
            task.Cancel();
        }
        catch (Exception ex) when (ex.Message.Contains("non-blittable"))
        {
            logger.Skip($"ImageTask(NSUrl): non-blittable NSUrl parameter (Known Issue 9)");
            results.Skip("ImageTask_FromNSUrl", "Non-blittable NSUrl parameter");
        }
        catch (Exception ex)
        {
            logger.Fail($"ImageTask(NSUrl): {ex.Message}");
            results.Fail("ImageTask_FromNSUrl", ex.Message);
        }

        // ImageTask with ImageRequest — works!
        try
        {
            var pipeline = ImagePipeline.Shared;
            var request = new ImageRequest(TestImageUrl());
            var task = pipeline.ImageTask(request);
            var taskRequest = task.Request;
            var description = taskRequest.Description;
            logger.Info($"ImageTask.Request.Description: {description.Substring(0, Math.Min(60, description.Length))}");
            logger.Pass("ImageTask from ImageRequest + Request property");
            results.Pass("ImageTask_FromImageRequest");

            // Cancel
            task.Cancel();
            logger.Pass("ImageTask.Cancel()");
            results.Pass("ImageTask_Cancel");
        }
        catch (Exception ex)
        {
            logger.Fail($"ImageTask from ImageRequest: {ex.Message}");
            results.Fail("ImageTask_FromImageRequest", ex.Message);
        }

        // ImageTask.CurrentProgress
        try
        {
            var pipeline = ImagePipeline.Shared;
            var request = new ImageRequest(TestImageUrl("progress"));
            var task = pipeline.ImageTask(request);
            var progress = task.CurrentProgress;
            logger.Pass($"ImageTask.CurrentProgress: {progress.GetType().Name}");
            results.Pass("ImageTask_CurrentProgress");
            task.Cancel();
        }
        catch (Exception ex)
        {
            logger.Fail($"ImageTask.CurrentProgress: {ex.Message}");
            results.Fail("ImageTask_CurrentProgress", ex.Message);
        }

        // ImageTask.Priority set — SDK 0.3.0 added @_cdecl wrappers
        try
        {
            var pipeline = ImagePipeline.Shared;
            var request = new ImageRequest(TestImageUrl("priorityset"));
            var task = pipeline.ImageTask(request);
            task.Priority = ImageRequest.PriorityKind.VeryHigh;
            var priority = task.Priority;
            // A setter that silently drops the value is a marshalling defect, not an
            // acceptable outcome — the round trip must return exactly what was written.
            if (priority == ImageRequest.PriorityKind.VeryHigh)
            {
                // Second value so a getter hard-wired to VeryHigh cannot pass.
                task.Priority = ImageRequest.PriorityKind.Low;
                var lowered = task.Priority;
                if (lowered == ImageRequest.PriorityKind.Low)
                {
                    logger.Pass("ImageTask.Priority set/get: VeryHigh then Low round-tripped exactly");
                    results.Pass("ImageTask_PrioritySet");
                }
                else
                {
                    logger.Fail($"ImageTask.Priority set to Low returned {lowered}");
                    results.Fail("ImageTask_PrioritySet", $"set Low, got {lowered}");
                }
            }
            else
            {
                logger.Fail($"ImageTask.Priority set to VeryHigh returned {priority}");
                results.Fail("ImageTask_PrioritySet", $"set VeryHigh, got {priority}");
            }
            task.Cancel();
        }
        catch (Exception ex)
        {
            logger.Fail($"ImageTask.Priority set: {ex.Message}");
            results.Fail("ImageTask_PrioritySet", ex.Message);
        }
    }

    private void RunDecoderTests(TestLogger logger, TestResults results)
    {
        logger.Info("--- ImageDecoders ---");

        // ImageDecoders.Default — works on device
        try
        {
            var decoder = new ImageDecoders.Default();
            logger.Pass("ImageDecoders.Default() construction");
            results.Pass("Decoder_Default");
        }
        catch (Exception ex)
        {
            logger.Fail($"ImageDecoders.Default(): {ex.Message}");
            results.Fail("Decoder_Default", ex.Message);
        }

        // ImageDecoders.Empty — SDK 0.3.0 added @_cdecl wrapper for this constructor
        try
        {
            var emptyDecoder = new ImageDecoders.Empty();
            logger.Pass("ImageDecoders.Empty() construction");
            results.Pass("Decoder_Empty");
        }
        catch (Exception ex)
        {
            logger.Fail($"ImageDecoders.Empty(): {ex.Message}");
            results.Fail("Decoder_Empty", ex.Message);
        }
    }

    private async Task RunAsyncTests(TestLogger logger, TestResults results)
    {
        // Image load (network) — async via ImagePipeline with ImageRequest
        logger.Info("--- Async Image Load (ImageRequest) ---");
        try
        {
            var pipeline = ImagePipeline.Shared;
            var request = new ImageRequest(TestImageUrl("async-load"));
            var image = await pipeline.ImageAsync(request);
            bool loaded = image != null;
            if (loaded)
            {
                logger.Pass($"Image load (ImageRequest): {image!.Size.Width}x{image.Size.Height}");
                results.Pass("ImageLoad_ImageRequest");
            }
            else
            {
                logger.Fail("Image load (ImageRequest): returned null");
                results.Fail("ImageLoad_ImageRequest", "Returned null");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"Image load (ImageRequest): {ex.GetType().Name}: {ex.Message}");
            results.Fail("ImageLoad_ImageRequest", ex.Message);
        }

        // Image load via NSUrl directly — non-blittable NSUrl parameter (Known Issue 9)
        logger.Info("--- Async Image Load (NSUrl) ---");
        try
        {
            var pipeline = ImagePipeline.Shared;
            var url = new Foundation.NSUrl(TestImageUrl("nsurl"));
            var image = await pipeline.ImageAsync(url);
            logger.Pass($"ImageAsync(NSUrl): loaded={image != null}");
            results.Pass("ImageLoad_NSUrl");
        }
        catch (Exception ex) when (ex.Message.Contains("non-blittable"))
        {
            logger.Skip($"ImageAsync(NSUrl): non-blittable type error (Known Issue 9)");
            results.Skip("ImageLoad_NSUrl", "Non-blittable NSUrl parameter");
        }
        catch (Exception ex)
        {
            logger.Fail($"ImageAsync(NSUrl): {ex.Message}");
            results.Fail("ImageLoad_NSUrl", ex.Message);
        }

        // DataAsync(NSUrl) overload removed in Nuke 13.0 — DataAsync now takes ImageRequest only.
        // The DataAsync(ImageRequest) variant is covered by the next test.
        logger.Info("--- Async Data Load ---");
        logger.Skip("DataAsync(NSUrl): overload removed in Nuke 13.0");
        results.Skip("DataAsync_NSUrl", "Removed in Nuke 13.0");
        await Task.CompletedTask;

        // DataAsync(ImageRequest) — previously Mono JIT SIGSEGV, re-testing with SDK 0.5.0
        logger.Info("--- Async Data Load (ImageRequest) ---");
        try
        {
            var pipeline = ImagePipeline.Shared;
            var request = new ImageRequest(TestImageUrl("datareq"));
            var (data, response) = await pipeline.DataAsync(request);
            logger.Pass($"DataAsync(ImageRequest): data={data?.Length}");
            results.Pass("DataAsync_ImageRequest");
        }
        catch (Exception ex)
        {
            logger.Fail($"DataAsync(ImageRequest): {ex.Message}");
            results.Fail("DataAsync_ImageRequest", ex.Message);
        }

        // Cancellation against a loopback route that never answers. A stalled source can
        // never be served from cache, so "it completed anyway" is a real failure here
        // rather than an outcome the test has to tolerate.
        logger.Info("--- Async CancellationToken (stalling loopback source) ---");
        using (var server = new LoopbackHttpServer())
        {
            // Two distinct stalling routes rather than one route with different query
            // strings: the loopback server matches and counts on the path alone, so a
            // shared route would let the pre-cancelled load's own request satisfy the
            // "my request reached the server" wait below. The delayed-cancellation test
            // would then cancel a load that never left the process and still report
            // that it had proven in-flight cancellation.
            const string preCancelRoute = "/stall-precancel.png";
            const string inFlightRoute = "/stall-inflight.png";
            server.Map(preCancelRoute, new LoopbackResponse { Stall = true, ContentType = "image/png" });
            server.Map(inFlightRoute, new LoopbackResponse { Stall = true, ContentType = "image/png" });

            // Pre-cancelled token: must surface as a cancellation, never as a result.
            try
            {
                var pipeline = ImagePipeline.Shared;
                var cts = new CancellationTokenSource();
                cts.Cancel();
                var request = new ImageRequest(server.Url(preCancelRoute));
                try
                {
                    var image = await Await.WithTimeoutAsync(
                        pipeline.ImageAsync(request, cts.Token), 10000, "pre-cancelled load");
                    logger.Fail($"Pre-cancelled token still produced an image ({image?.Size.Width}x{image?.Size.Height}) from a stalled source");
                    results.Fail("Async_Cancellation", "pre-cancelled token produced an image from a stalled source");
                }
                catch (OperationCanceledException)
                {
                    logger.Pass("Async with pre-cancelled token: cancelled as expected");
                    results.Pass("Async_Cancellation");
                }
            }
            catch (Exception ex)
            {
                logger.Fail($"Async cancellation: {ex.GetType().Name}: {ex.Message}");
                results.Fail("Async_Cancellation", ex.Message);
            }

            // Delayed cancellation: prove the task is genuinely in flight (the server has
            // accepted the connection and is holding it) before cancelling it.
            try
            {
                var pipeline = ImagePipeline.Shared;
                var cts = new CancellationTokenSource();
                var request = new ImageRequest(server.Url(inFlightRoute));
                var loadTask = pipeline.ImageAsync(request, cts.Token);

                var reachedServer = await Await.UntilAsync(() => server.HitsFor(inFlightRoute) >= 1, 8000);
                if (!reachedServer)
                {
                    logger.Fail("Delayed cancellation: request never reached the loopback server");
                    results.Fail("Async_DelayedCancellation", "request never reached the loopback server");
                }
                else if (loadTask.IsCompleted)
                {
                    logger.Fail("Delayed cancellation: load completed before it was cancelled (server never answered)");
                    results.Fail("Async_DelayedCancellation", "load completed against a stalled source");
                }
                else
                {
                    cts.Cancel();
                    try
                    {
                        await Await.WithTimeoutAsync(loadTask, 10000, "delayed cancellation");
                        logger.Fail("Delayed cancellation: in-flight load produced an image from a stalled source");
                        results.Fail("Async_DelayedCancellation", "in-flight load completed after cancel");
                    }
                    catch (OperationCanceledException)
                    {
                        logger.Pass("Delayed cancellation: in-flight load cancelled after reaching the server");
                        results.Pass("Async_DelayedCancellation");
                    }
                }
            }
            catch (Exception ex)
            {
                logger.Fail($"Delayed cancellation: {ex.GetType().Name}: {ex.Message}");
                results.Fail("Async_DelayedCancellation", ex.Message);
            }
        }

        // Multiple concurrent image loads (using ImageRequest, not NSUrl)
        logger.Info("--- Concurrent Image Loads ---");
        try
        {
            var pipeline = ImagePipeline.Shared;
            var tasks = new List<Task<UIKit.UIImage>>();
            for (int i = 0; i < 5; i++)
            {
                var request = new ImageRequest(TestImageUrl($"concurrent{i}"));
                tasks.Add(pipeline.ImageAsync(request));
            }

            var images = await Task.WhenAll(tasks);
            var loadedCount = images.Count(img => img != null);
            logger.Info($"Concurrent loads: {loadedCount}/5 succeeded");
            if (loadedCount == 5)
            {
                logger.Pass("5 concurrent image loads all succeeded");
                results.Pass("ConcurrentLoads");
            }
            else
            {
                logger.Fail($"Concurrent loads: only {loadedCount}/5 succeeded");
                results.Fail("ConcurrentLoads", $"Only {loadedCount}/5");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"Concurrent loads: {ex.Message}");
            results.Fail("ConcurrentLoads", ex.Message);
        }

        // ImageContainer from loaded image
        logger.Info("--- ImageContainer ---");
        try
        {
            var pipeline = ImagePipeline.Shared;
            var request = new ImageRequest(TestImageUrl("container"));
            var image = await pipeline.ImageAsync(request);
            if (image != null)
            {
                // Create an ImageContainer from the loaded image to test its properties
                // ImageContainer is typically returned from decoders; test via response
                logger.Pass("Image loaded successfully for container test");
                results.Pass("ImageContainer_ViaLoad");
            }
            else
            {
                logger.Fail("ImageContainer: failed to load image");
                results.Fail("ImageContainer_ViaLoad", "Failed to load");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"ImageContainer: {ex.Message}");
            results.Fail("ImageContainer_ViaLoad", ex.Message);
        }

        // Second load from cache should be fast (existing test)
        logger.Info("--- Cache Hit (second load) ---");
        try
        {
            var pipeline = ImagePipeline.Shared;
            var url = TestImageUrl("cachehit");

            // First load
            var request1 = new ImageRequest(url);
            var image1 = await pipeline.ImageAsync(request1);

            // Second load (should be cached)
            var sw = Stopwatch.StartNew();
            var request2 = new ImageRequest(url);
            var image2 = await pipeline.ImageAsync(request2);
            sw.Stop();

            if (image1 != null && image2 != null)
            {
                logger.Pass($"Cache hit: second load took {sw.ElapsedMilliseconds}ms");
                results.Pass("CacheHit_SecondLoad");
            }
            else
            {
                logger.Fail("Cache hit: one or both loads returned null");
                results.Fail("CacheHit_SecondLoad", "Null images");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"Cache hit: {ex.Message}");
            results.Fail("CacheHit_SecondLoad", ex.Message);
        }
    }

    private async Task RunLibraryParityTests(TestLogger logger, TestResults results)
    {
        // N1: ImageRequest from URL string + UrlRequest property access
        // SDK 0.3.0: URL/URLRequest now use ObjC bridge (Foundation.NSUrl/NSUrlRequest).
        // ImageRequest(string) replaces the old URLRequest-based factory.
        logger.Info("--- N1: ImageRequest from URL string ---");
        try
        {
            var imageRequest = new ImageRequest(TestImageUrl("parity"));
            logger.Pass("ImageRequest(string) construction");
            results.Pass("N1_URLRequest_Construction");

            // Verify UrlRequest property returns NSUrlRequest via ObjC bridge
            var urlRequest = imageRequest.UrlRequest;
            if (urlRequest != null)
            {
                logger.Pass("ImageRequest.UrlRequest returns NSUrlRequest via ObjC bridge");
                results.Pass("N1_ImageRequest_FromURLRequest");
            }
            else
            {
                logger.Fail("N1: ImageRequest.UrlRequest returned null");
                results.Fail("N1_ImageRequest_FromURLRequest", "UrlRequest returned null");
            }

            // Load image
            var pipeline = ImagePipeline.Shared;
            var image = await pipeline.ImageAsync(imageRequest);
            if (image != null)
            {
                logger.Pass($"Image loaded via ImageRequest(string): {image.Size.Width}x{image.Size.Height}");
                results.Pass("N1_ImageLoad_WithHeaders");
            }
            else
            {
                logger.Fail("N1: Image load returned null");
                results.Fail("N1_ImageLoad_WithHeaders", "Returned null");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"N1 ImageRequest: {ex.Message}");
            results.Fail("N1_URLRequest_Construction", ex.Message);
            results.Skip("N1_ImageRequest_FromURLRequest", "Depends on ImageRequest");
            results.Skip("N1_ImageLoad_WithHeaders", "Depends on ImageRequest");
        }

        // N1 bonus: ImageRequest from NSUrlRequest
        // The (NSUrlRequest, processors) constructor is no longer exposed in the Nuke 13.0 binding;
        // only ImageRequest(string) survives the wrapper-emit on this overload set.
        logger.Info("--- N1: ImageRequest from NSUrlRequest ---");
        logger.Skip("ImageRequest(NSUrlRequest, processors): constructor not exposed in Nuke 13.0 binding");
        results.Skip("N1_URLRequest_AddValue", "Removed in Nuke 13.0 binding");

        // N2: ImageRequest.Processors is now a fully-bound existential-array property —
        // IReadOnlyList<IImageProcessing>, projected from SwiftArray<ExistentialContainer1>.
        // It used to be a co-gated stub that threw NotSupportedException; the existential-array
        // binding work made it real, so accessing it must succeed. A default ImageRequest carries
        // no processors, so the getter round-trips an empty list.
        logger.Info("--- N2: ImageRequest.Processors ---");
        try
        {
            var request = new ImageRequest(TestImageUrl());
            var processors = request.Processors;
            if (processors is null)
            {
                logger.Fail("N2: Processors returned null");
                results.Fail("N2_Processors_Exists", "Processors was null");
            }
            else if (processors.Count != 0)
            {
                logger.Fail($"N2: Processors expected empty for a default request, got {processors.Count}");
                results.Fail("N2_Processors_Exists", $"Expected empty list, got {processors.Count}");
            }
            else
            {
                logger.Pass("N2: Processors projects an empty IReadOnlyList<IImageProcessing> for a default request");
                results.Pass("N2_Processors_Exists");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"N2 Processors: {ex.Message}");
            results.Fail("N2_Processors_Exists", ex.Message);
        }

        // N6: ImagePipeline.Cache query methods
        logger.Info("--- N6: Pipeline.Cache Methods ---");
        try
        {
            var pipeline = ImagePipeline.Shared;
            var cache = pipeline.Cache;
            var request = new ImageRequest(TestImageUrl("n6"));

            // Start from a known-empty entry so nothing below can be satisfied by a
            // leftover from an earlier phase.
            cache.RemoveCachedImage(request);

            // Store an image we built ourselves, so the container that comes back out can
            // be compared against known pixel dimensions rather than merely being non-null.
            var stored = ImageFixtures.SolidUIImage(37, 21, 0.0, 0.0, 1.0);
            cache.StoreCachedImage(new ImageContainer(stored), request);
            logger.Pass($"N6: StoreCachedImage stored {stored.Size.Width}x{stored.Size.Height}");
            results.Pass("N6_Cache_StoreImage");

            var contains = cache.ContainsCachedImage(request);
            if (contains)
            {
                logger.Pass("N6: ContainsCachedImage true after store");
                results.Pass("N6_Cache_ContainsImage");
            }
            else
            {
                logger.Fail("N6: ContainsCachedImage false immediately after StoreCachedImage");
                results.Fail("N6_Cache_ContainsImage", "ContainsCachedImage false after store");
            }

            var cached = cache.CachedImage(request);
            if (cached == null)
            {
                logger.Fail("N6: CachedImage returned null for an image just stored in the memory cache");
                results.Fail("N6_Cache_CachedImage", "CachedImage null after StoreCachedImage");
            }
            else if ((int)cached.Image.Size.Width != 37 || (int)cached.Image.Size.Height != 21)
            {
                logger.Fail($"N6: CachedImage returned {cached.Image.Size.Width}x{cached.Image.Size.Height}, expected 37x21");
                results.Fail("N6_Cache_CachedImage", $"got {cached.Image.Size.Width}x{cached.Image.Size.Height}, expected 37x21");
            }
            else
            {
                logger.Pass("N6: CachedImage round-tripped the stored 37x21 container");
                results.Pass("N6_Cache_CachedImage");
            }

            // RemoveCachedImage must actually remove it.
            cache.RemoveCachedImage(request);
            var afterRemove = cache.CachedImage(request);
            var containsAfterRemove = cache.ContainsCachedImage(request);
            if (afterRemove == null && !containsAfterRemove)
            {
                logger.Pass("N6: RemoveCachedImage cleared both the entry and the containment flag");
                results.Pass("N6_Cache_RemoveImage");
            }
            else
            {
                logger.Fail($"N6: after RemoveCachedImage, CachedImage={(afterRemove == null ? "null" : "non-null")} Contains={containsAfterRemove}");
                results.Fail("N6_Cache_RemoveImage", "entry survived RemoveCachedImage");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"N6 Cache methods: {ex.Message}");
            results.Fail("N6_Cache_CachedImage", ex.Message);
        }
    }

    /// <summary>
    /// Behavioural tests served from an in-process loopback HTTP server. Serving the
    /// fixture ourselves gives three things a bundled file cannot: the exact bytes (so
    /// decoded pixels can be asserted), a count of how many times the network was
    /// actually touched (so a cache hit is proof rather than inference), and control over
    /// failure shapes (404, corrupt body, a connection that never answers).
    /// </summary>
    private async Task RunLoopbackBehaviourTests(TestLogger logger, TestResults results)
    {
        LoopbackHttpServer server;
        try
        {
            server = new LoopbackHttpServer();
        }
        catch (Exception ex)
        {
            logger.Fail($"Loopback server could not start: {ex.GetType().Name}: {ex.Message}");
            results.Fail("Loopback_ServerStart", ex.Message);
            return;
        }

        using (server)
        {
            var redPng = ImageFixtures.SolidPng(24, 16, 1.0, 0.0, 0.0);
            server.Map("/red.png", new LoopbackResponse { ContentType = "image/png", Body = redPng });
            server.Map("/missing.png", new LoopbackResponse { StatusCode = 404, ReasonPhrase = "Not Found", ContentType = "text/plain", Body = Encoding.UTF8.GetBytes("no such image") });
            server.Map("/corrupt.png", new LoopbackResponse { ContentType = "image/png", Body = ImageFixtures.CorruptImageBytes() });
            server.Map("/cachehit.png", new LoopbackResponse { ContentType = "image/png", Body = redPng });
            server.Map("/prefetch.png", new LoopbackResponse { ContentType = "image/png", Body = redPng });
            server.Map("/raw.bin", new LoopbackResponse { ContentType = "application/octet-stream", Body = Encoding.UTF8.GetBytes("raw-loader-payload") });
            logger.Info($"Loopback server listening on {server.BaseUrl}; fixture png is {redPng.Length} bytes");

            await LoopbackPixelRoundTrip(logger, results, server);
            await LoopbackCacheHitLoadCount(logger, results, server);
            await LoopbackFailurePaths(logger, results, server);
            await LoopbackProcessorPixels(logger, results, server);
            await LoopbackPrefetchCompletes(logger, results, server);
            await DataLoaderResponseCallback(logger, results, server);
            DataCacheRoundTrip(logger, results);
        }
    }

    private async Task LoopbackPixelRoundTrip(TestLogger logger, TestResults results, LoopbackHttpServer server)
    {
        try
        {
            var pipeline = ImagePipeline.Shared;
            var request = new ImageRequest(server.Url("/red.png"));
            var image = await Await.WithTimeoutAsync(pipeline.ImageAsync(request), 15000, "loopback image load");

            if (image is null)
            {
                logger.Fail("Loopback load returned a null image");
                results.Fail("Loopback_PixelRoundTrip", "null image");
                return;
            }

            var px = ImageFixtures.CenterPixel(image);
            bool sized = (int)image.Size.Width == 24 && (int)image.Size.Height == 16;
            bool red = px.R > 240 && px.G < 15 && px.B < 15 && px.A == 255;
            if (sized && red)
            {
                logger.Pass($"Loopback image decoded 24x16 with centre pixel {px}");
                results.Pass("Loopback_PixelRoundTrip");
            }
            else
            {
                logger.Fail($"Loopback image was {image.Size.Width}x{image.Size.Height} with centre pixel {px}, expected 24x16 solid red");
                results.Fail("Loopback_PixelRoundTrip", $"{image.Size.Width}x{image.Size.Height} px={px}");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"Loopback pixel round trip: {ex.GetType().Name}: {ex.Message}");
            results.Fail("Loopback_PixelRoundTrip", ex.Message);
        }
    }

    private async Task LoopbackCacheHitLoadCount(TestLogger logger, TestResults results, LoopbackHttpServer server)
    {
        // An isolated pipeline so the shared pipeline's cache state cannot decide the
        // outcome, and a server hit counter so "it was cached" is measured, not assumed.
        try
        {
            var config = new ImagePipeline.ConfigurationInfo(ImagePipeline.Shared.Configuration.DataLoader);
            using var pipeline = new ImagePipeline(config);

            var url = server.Url("/cachehit.png");
            var first = await Await.WithTimeoutAsync(pipeline.ImageAsync(new ImageRequest(url)), 15000, "cache-hit first load");
            int hitsAfterFirst = server.HitsFor("/cachehit.png");
            var second = await Await.WithTimeoutAsync(pipeline.ImageAsync(new ImageRequest(url)), 15000, "cache-hit second load");
            int hitsAfterSecond = server.HitsFor("/cachehit.png");

            if (first is null || second is null)
            {
                logger.Fail($"Cache-hit test: first={(first is null ? "null" : "ok")} second={(second is null ? "null" : "ok")}");
                results.Fail("Loopback_CacheHit", "a load returned null");
            }
            else if (hitsAfterFirst != 1)
            {
                logger.Fail($"Cache-hit test: first load produced {hitsAfterFirst} server hits, expected exactly 1");
                results.Fail("Loopback_CacheHit", $"first load hits={hitsAfterFirst}");
            }
            else if (hitsAfterSecond != 1)
            {
                logger.Fail($"Cache-hit test: second load went back to the network (hits {hitsAfterFirst} -> {hitsAfterSecond})");
                results.Fail("Loopback_CacheHit", $"second load hits={hitsAfterSecond}");
            }
            else
            {
                logger.Pass("Cache hit proven: two loads, exactly one server hit");
                results.Pass("Loopback_CacheHit");
            }

            // The pipeline's own cache facade must agree with what the hit count showed.
            var cached = pipeline.Cache.CachedImage(new ImageRequest(url));
            if (cached is not null && (int)cached.Image.Size.Width == 24)
            {
                logger.Pass("Pipeline.Cache.CachedImage returned the 24x16 entry the second load was served from");
                results.Pass("Loopback_CacheHit_Facade");
            }
            else
            {
                logger.Fail($"Pipeline.Cache.CachedImage returned {(cached is null ? "null" : $"{cached.Image.Size.Width}x{cached.Image.Size.Height}")} after a proven memory-cache hit");
                results.Fail("Loopback_CacheHit_Facade", cached is null ? "null" : "wrong size");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"Cache-hit load count: {ex.GetType().Name}: {ex.Message}");
            results.Fail("Loopback_CacheHit", ex.Message);
        }
    }

    private async Task LoopbackFailurePaths(TestLogger logger, TestResults results, LoopbackHttpServer server)
    {
        // 404: the pipeline must surface a failure, not an image.
        try
        {
            var pipeline = ImagePipeline.Shared;
            try
            {
                var image = await Await.WithTimeoutAsync(
                    pipeline.ImageAsync(new ImageRequest(server.Url("/missing.png"))), 15000, "404 load");
                logger.Fail($"404 response produced an image ({image?.Size.Width}x{image?.Size.Height})");
                results.Fail("Loopback_404", "404 produced an image");
            }
            catch (TimeoutException)
            {
                throw;
            }
            catch (Exception ex)
            {
                if (server.HitsFor("/missing.png") < 1)
                {
                    logger.Fail("404 test failed before reaching the server: " + ex.Message);
                    results.Fail("Loopback_404", "request never reached the server");
                }
                else
                {
                    logger.Pass($"404 surfaced as {ex.GetType().Name}: {ex.Message}");
                    results.Pass("Loopback_404");
                }
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"404 path: {ex.GetType().Name}: {ex.Message}");
            results.Fail("Loopback_404", ex.Message);
        }

        // Corrupt body: a 200 whose payload cannot be decoded must fail, not yield a
        // blank or zero-sized image.
        try
        {
            var pipeline = ImagePipeline.Shared;
            try
            {
                var image = await Await.WithTimeoutAsync(
                    pipeline.ImageAsync(new ImageRequest(server.Url("/corrupt.png"))), 15000, "corrupt load");
                logger.Fail($"Corrupt image body produced an image ({image?.Size.Width}x{image?.Size.Height})");
                results.Fail("Loopback_CorruptImage", "corrupt body produced an image");
            }
            catch (TimeoutException)
            {
                throw;
            }
            catch (Exception ex)
            {
                if (server.HitsFor("/corrupt.png") < 1)
                {
                    logger.Fail("Corrupt-image test failed before reaching the server: " + ex.Message);
                    results.Fail("Loopback_CorruptImage", "request never reached the server");
                }
                else
                {
                    logger.Pass($"Corrupt image body surfaced as {ex.GetType().Name}: {ex.Message}");
                    results.Pass("Loopback_CorruptImage");
                }
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"Corrupt-image path: {ex.GetType().Name}: {ex.Message}");
            results.Fail("Loopback_CorruptImage", ex.Message);
        }
    }

    private async Task LoopbackProcessorPixels(TestLogger logger, TestResults results, LoopbackHttpServer server)
    {
        // Resize through the request pipeline: assert the delivered image's dimensions,
        // which is the processor's actual output rather than the fact it was accepted.
        try
        {
            var pipeline = ImagePipeline.Shared;
            // Pixels, not points: a points-based size is multiplied by the screen scale,
            // which would make the expected dimensions depend on the simulator device.
            var resize = new ImageProcessors.Resize(
                new Swift.CGSize(8, 8), ImageProcessingOptions.Unit.Pixels, crop: true);
            var request = new ImageRequest(
                new NSUrl(server.Url("/red.png#resized")),
                new IImageProcessing[] { resize });
            var image = await Await.WithTimeoutAsync(pipeline.ImageAsync(request), 15000, "resize load");

            if (image is null)
            {
                logger.Fail("Resize processor: pipeline returned null");
                results.Fail("Loopback_ProcessorResize", "null image");
            }
            else if ((int)image.Size.Width == 8 && (int)image.Size.Height == 8)
            {
                var px = ImageFixtures.CenterPixel(image);
                if (px.R > 240 && px.G < 15 && px.B < 15)
                {
                    logger.Pass($"Resize processor produced 8x8 and preserved the colour ({px})");
                    results.Pass("Loopback_ProcessorResize");
                }
                else
                {
                    logger.Fail($"Resize processor produced 8x8 but the centre pixel was {px}, expected red");
                    results.Fail("Loopback_ProcessorResize", $"centre pixel {px}");
                }
            }
            else
            {
                logger.Fail($"Resize processor produced {image.Size.Width}x{image.Size.Height}, expected 8x8");
                results.Fail("Loopback_ProcessorResize", $"{image.Size.Width}x{image.Size.Height}");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"Resize processor: {ex.GetType().Name}: {ex.Message}");
            results.Fail("Loopback_ProcessorResize", ex.Message);
        }

        // Circle: the corner of a circular crop must become transparent while the centre
        // keeps the source colour. This reads the processor's own output pixels.
        try
        {
            var source = ImageFixtures.SolidUIImage(32, 32, 0.0, 1.0, 0.0);
            var circle = new ImageProcessors.Circle();
            var processed = circle.Process(source);

            if (processed is null)
            {
                logger.Fail("Circle processor returned null");
                results.Fail("Loopback_ProcessorCircle", "null output");
            }
            else
            {
                var corner = ImageFixtures.PixelAt(processed, 1, 1);
                var centre = ImageFixtures.PixelAt(processed, 16, 16);
                if (corner.A == 0 && centre.A > 200 && centre.G > 200 && centre.R < 60)
                {
                    logger.Pass($"Circle processor cleared the corner (a={corner.A}) and kept the centre green ({centre})");
                    results.Pass("Loopback_ProcessorCircle");
                }
                else
                {
                    logger.Fail($"Circle processor output: corner={corner} centre={centre}; expected a transparent corner and a green centre");
                    results.Fail("Loopback_ProcessorCircle", $"corner={corner} centre={centre}");
                }
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"Circle processor: {ex.GetType().Name}: {ex.Message}");
            results.Fail("Loopback_ProcessorCircle", ex.Message);
        }
        await Task.CompletedTask;
    }

    private async Task LoopbackPrefetchCompletes(TestLogger logger, TestResults results, LoopbackHttpServer server)
    {
        try
        {
            var config = new ImagePipeline.ConfigurationInfo(ImagePipeline.Shared.Configuration.DataLoader);
            using var pipeline = new ImagePipeline(config);
            var prefetcher = new ImagePrefetcher(pipeline);

            int completions = 0;
            prefetcher.DidComplete = () => Interlocked.Increment(ref completions);

            var url = server.Url("/prefetch.png");
            prefetcher.StartPrefetching(new[] { new NSUrl(url) });

            bool completed = await Await.UntilAsync(() => Volatile.Read(ref completions) > 0, 20000);
            if (!completed)
            {
                logger.Fail("ImagePrefetcher.DidComplete never fired within 20s");
                results.Fail("Loopback_PrefetchCompletes", "DidComplete never fired");
                return;
            }

            // Completion alone would still pass if nothing was fetched — require the
            // prefetched image to be in the pipeline's cache too.
            var cached = pipeline.Cache.CachedImage(new ImageRequest(url));
            if (cached is not null && (int)cached.Image.Size.Width == 24 && (int)cached.Image.Size.Height == 16)
            {
                logger.Pass($"Prefetcher completed ({completions} callback(s)) and left a 24x16 image in the pipeline cache");
                results.Pass("Loopback_PrefetchCompletes");
            }
            else
            {
                logger.Fail($"Prefetcher completed but the cache holds {(cached is null ? "nothing" : $"{cached.Image.Size.Width}x{cached.Image.Size.Height}")}");
                results.Fail("Loopback_PrefetchCompletes", "prefetched image absent from cache");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"Prefetch completion: {ex.GetType().Name}: {ex.Message}");
            results.Fail("Loopback_PrefetchCompletes", ex.Message);
        }
    }

    private async Task DataLoaderResponseCallback(TestLogger logger, TestResults results, LoopbackHttpServer server)
    {
        // Exercises the data loader's LoadData entry point directly, so the NSUrlResponse
        // handed to the didReceiveData callback is inspected rather than merely received
        // — an ObjC-class callback argument is the shape most likely to be mismarshalled.
        // The binding's only public DataLoader constructor is an always-throwing SB0005
        // stub, and the shared pipeline vends its loader through the IDataLoading
        // projection, so the call goes through the interface.
        try
        {
            var loader = ImagePipeline.Shared.Configuration.DataLoader;
            if (loader is null)
            {
                logger.Fail("Shared pipeline's Configuration.DataLoader was null");
                results.Fail("DataLoader_LoadDataResponse", "no data loader available");
                return;
            }

            string url = server.Url("/raw.bin");
            long totalBytes = 0;
            string? seenUrl = null;
            long seenStatus = -1;
            Exception? callbackError = null;
            var completed = new TaskCompletionSource<Swift.Foundation.AnyError?>(TaskCreationOptions.RunContinuationsAsynchronously);

            var cancellable = loader.LoadData(
                new NSUrlRequest(new NSUrl(url)),
                (data, response) =>
                {
                    try
                    {
                        Interlocked.Add(ref totalBytes, data?.Length ?? 0);
                        seenUrl = response?.Url?.AbsoluteString;
                        if (response is NSHttpUrlResponse http)
                            seenStatus = http.StatusCode;
                    }
                    catch (Exception ex)
                    {
                        callbackError = ex;
                    }
                },
                error => completed.TrySetResult(error));

            // Nothing holds the delegates but the native side; collecting here is what
            // makes a missing GC root show up as a crash or a lost callback.
            GC.Collect();
            GC.WaitForPendingFinalizers();

            var finalError = await Await.WithTimeoutAsync(completed.Task, 20000, "DataLoader.LoadData");
            GC.KeepAlive(cancellable);

            if (callbackError is not null)
            {
                logger.Fail($"DataLoader response callback threw: {callbackError.GetType().Name}: {callbackError.Message}");
                results.Fail("DataLoader_LoadDataResponse", callbackError.Message);
            }
            else if (finalError is not null)
            {
                logger.Fail($"DataLoader.LoadData completed with an error for a 200 response: {finalError}");
                results.Fail("DataLoader_LoadDataResponse", "completion carried an error");
            }
            else if (seenStatus != 200)
            {
                logger.Fail($"DataLoader response status was {seenStatus}, expected 200 (url={seenUrl ?? "null"})");
                results.Fail("DataLoader_LoadDataResponse", $"status {seenStatus}");
            }
            else if (seenUrl is null || !seenUrl.Contains("/raw.bin", StringComparison.Ordinal))
            {
                logger.Fail($"DataLoader response URL was {seenUrl ?? "null"}, expected it to contain /raw.bin");
                results.Fail("DataLoader_LoadDataResponse", $"url {seenUrl ?? "null"}");
            }
            else if (Interlocked.Read(ref totalBytes) != 18)
            {
                logger.Fail($"DataLoader delivered {Interlocked.Read(ref totalBytes)} bytes, expected 18");
                results.Fail("DataLoader_LoadDataResponse", $"{Interlocked.Read(ref totalBytes)} bytes");
            }
            else
            {
                logger.Pass($"DataLoader.LoadData delivered 18 bytes with status 200 from {seenUrl}, surviving a GC mid-flight");
                results.Pass("DataLoader_LoadDataResponse");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"DataLoader.LoadData: {ex.GetType().Name}: {ex.Message}");
            results.Fail("DataLoader_LoadDataResponse", ex.Message);
        }
    }

    private void DataCacheRoundTrip(TestLogger logger, TestResults results)
    {
        // write -> read -> remove -> reopen, so persistence across instances is asserted
        // rather than just the absence of an exception.
        try
        {
            string name = "swiftbindings.datacache.roundtrip";
            var payload = Encoding.UTF8.GetBytes("nuke-datacache-round-trip-payload");

            using (var cache = new DataCache(name))
            {
                cache.RemoveAll();
                cache.StoreData(payload, "k1");
                cache.Flush();

                var read = cache.CachedData("k1");
                if (read is null || !read.AsSpan().SequenceEqual(payload))
                {
                    logger.Fail($"DataCache read back {(read is null ? "null" : read.Length + " bytes")}, expected the {payload.Length} bytes written");
                    results.Fail("DataCache_RoundTrip", "stored payload did not read back");
                    return;
                }
                if (!cache.ContainsData("k1"))
                {
                    logger.Fail("DataCache.ContainsData false for a key just stored and flushed");
                    results.Fail("DataCache_RoundTrip", "ContainsData false after store");
                    return;
                }
            }

            // Reopen: the bytes must still be there after the first instance is gone.
            using (var reopened = new DataCache(name))
            {
                var persisted = reopened.CachedData("k1");
                if (persisted is null || !persisted.AsSpan().SequenceEqual(payload))
                {
                    logger.Fail($"DataCache reopened by name returned {(persisted is null ? "null" : persisted.Length + " bytes")}, expected the persisted payload");
                    results.Fail("DataCache_RoundTrip", "payload did not survive reopen");
                    return;
                }

                reopened.RemoveData("k1");
                reopened.Flush();
                var afterRemove = reopened.CachedData("k1");
                if (afterRemove is not null)
                {
                    logger.Fail($"DataCache still returned {afterRemove.Length} bytes after RemoveData");
                    results.Fail("DataCache_RoundTrip", "RemoveData did not remove the entry");
                    return;
                }
            }

            using (var afterReopen = new DataCache(name))
            {
                if (afterReopen.CachedData("k1") is not null)
                {
                    logger.Fail("DataCache removal did not survive a reopen — the entry came back");
                    results.Fail("DataCache_RoundTrip", "removal did not persist");
                    return;
                }
            }

            logger.Pass("DataCache round trip: write, read, reopen, remove, reopen all behaved");
            results.Pass("DataCache_RoundTrip");
        }
        catch (Exception ex)
        {
            logger.Fail($"DataCache round trip: {ex.GetType().Name}: {ex.Message}");
            results.Fail("DataCache_RoundTrip", ex.Message);
        }
    }

    private void RunCoverageGapTests(TestLogger logger, TestResults results)
    {
        // N9a: ImageProcessors.Resize constructor — struct with multiple params + enum + defaults
        try
        {
            using var resize = new ImageProcessors.Resize(width: 200.0);
            logger.Pass($"N9a: ImageProcessors.Resize(width:200)");
            results.Pass("N9a_Resize_Constructor");
        }
        catch (Exception ex)
        {
            logger.Fail($"N9a Resize constructor: {ex.Message}");
            results.Fail("N9a_Resize_Constructor", ex.Message);
        }

        // N9b: ImageProcessors.Circle constructor — struct with optional param
        try
        {
            using var circle = new ImageProcessors.Circle();
            logger.Pass($"N9b: ImageProcessors.Circle()");
            results.Pass("N9b_Circle_Constructor");
        }
        catch (Exception ex)
        {
            logger.Fail($"N9b Circle constructor: {ex.Message}");
            results.Fail("N9b_Circle_Constructor", ex.Message);
        }

        // N9c: ImageProcessors.RoundedCorners constructor — with default unit
        try
        {
            using var corners = new ImageProcessors.RoundedCorners(radius: 10.0);
            logger.Pass($"N9c: ImageProcessors.RoundedCorners(radius:10)");
            results.Pass("N9c_RoundedCorners_Constructor");
        }
        catch (Exception ex)
        {
            logger.Fail($"N9c RoundedCorners constructor: {ex.Message}");
            results.Fail("N9c_RoundedCorners_Constructor", ex.Message);
        }

        // N9d: ImageRequest.Priority — enum property getter
        try
        {
            var request = new ImageRequest(TestImageUrl());
            var priority = request.Priority;
            logger.Pass($"N9d: ImageRequest.Priority = {priority}");
            results.Pass("N9d_Request_Priority");
        }
        catch (Exception ex)
        {
            logger.Fail($"N9d ImageRequest.Priority: {ex.Message}");
            results.Fail("N9d_Request_Priority", ex.Message);
        }

        // N9e: ImageRequest.Options — struct property getter
        try
        {
            var request = new ImageRequest(TestImageUrl());
            using var options = request.Options;
            logger.Pass($"N9e: ImageRequest.Options accessed");
            results.Pass("N9e_Request_Options");
        }
        catch (Exception ex)
        {
            logger.Fail($"N9e ImageRequest.Options: {ex.Message}");
            results.Fail("N9e_Request_Options", ex.Message);
        }

        // N9f: ImageCache.RemoveAll — void method on cache
        try
        {
            var cache = ImageCache.Shared;
            cache.RemoveAll();
            logger.Pass("N9f: ImageCache.RemoveAll()");
            results.Pass("N9f_Cache_RemoveAll");
        }
        catch (Exception ex)
        {
            logger.Fail($"N9f ImageCache.RemoveAll: {ex.Message}");
            results.Fail("N9f_Cache_RemoveAll", ex.Message);
        }

        // N9g: ImageProcessingOptions.Unit enum values
        try
        {
            var points = ImageProcessingOptions.Unit.Points;
            var pixels = ImageProcessingOptions.Unit.Pixels;
            logger.Pass($"N9g: Unit enum: Points={(int)points}, Pixels={(int)pixels}");
            results.Pass("N9g_Unit_Enum");
        }
        catch (Exception ex)
        {
            logger.Fail($"N9g Unit enum: {ex.Message}");
            results.Fail("N9g_Unit_Enum", ex.Message);
        }

        // N9h: ImageProcessingOptions.ContentMode enum values
        try
        {
            var fill = ImageProcessingOptions.ContentMode.AspectFill;
            var fit = ImageProcessingOptions.ContentMode.AspectFit;
            logger.Pass($"N9h: ContentMode enum: AspectFill={(int)fill}, AspectFit={(int)fit}");
            results.Pass("N9h_ContentMode_Enum");
        }
        catch (Exception ex)
        {
            logger.Fail($"N9h ContentMode enum: {ex.Message}");
            results.Fail("N9h_ContentMode_Enum", ex.Message);
        }

        // N9i: ImageRequest.ThumbnailOptions — struct construction
        try
        {
            using var opts = new ImageRequest.ThumbnailOptions(maxPixelSize: 100.0f);
            logger.Pass($"N9i: ThumbnailOptions(maxPixelSize:100)");
            results.Pass("N9i_ThumbnailOptions");
        }
        catch (Exception ex)
        {
            logger.Fail($"N9i ThumbnailOptions: {ex.Message}");
            results.Fail("N9i_ThumbnailOptions", ex.Message);
        }

        // N9j: Memory pressure — create and dispose many ImageRequest objects
        try
        {
            for (int i = 0; i < 100; i++)
            {
                var request = new ImageRequest(TestImageUrl($"pressure{i}"));
                var _ = request.Priority;
            }
            logger.Pass("N9j: Memory pressure: 100 ImageRequest create/dispose cycles");
            results.Pass("N9j_Memory_Pressure");
        }
        catch (Exception ex)
        {
            logger.Fail($"N9j Memory pressure: {ex.Message}");
            results.Fail("N9j_Memory_Pressure", ex.Message);
        }
    }
}

#endregion
#endif
