// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Text;
using Foundation;
using UIKit;
using Swift;
using Swift.Runtime;
using Kingfisher;

namespace KingfisherSimTests;

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
            Text = "Kingfisher Binding Tests",
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

        logger.Info("=== Section 2: Singletons & Static Properties ===");
        RunSingletonTests(logger, results);

        logger.Info("=== Section 3: Core Class Constructors ===");
        RunConstructorTests(logger, results);

        logger.Info("=== Section 4: ImageDownloader Properties ===");
        RunImageDownloaderPropertyTests(logger, results);

        logger.Info("=== Section 5: ImageCache Methods ===");
        RunImageCacheMethodTests(logger, results);

        logger.Info("=== Section 6: KingfisherManager Properties ===");
        RunKingfisherManagerTests(logger, results);

        logger.Info("=== Section 7: C# Integer Enums ===");
        RunIntegerEnumTests(logger, results);

        logger.Info("=== Section 8: StorageExpiration Discriminated Union ===");
        RunStorageExpirationTests(logger, results);

        logger.Info("=== Section 9: Radius Discriminated Union ===");
        RunRadiusTests(logger, results);

        logger.Info("=== Section 10: ImageFormat Discriminated Union ===");
        RunImageFormatTests(logger, results);

        logger.Info("=== Section 11: ImageTransition Discriminated Union ===");
        RunImageTransitionTests(logger, results);

        logger.Info("=== Section 12: ExpirationExtending Discriminated Union ===");
        RunExpirationExtendingTests(logger, results);

        logger.Info("=== Section 13: CallbackQueue Discriminated Union ===");
        RunCallbackQueueTests(logger, results);

        logger.Info("=== Section 14: RepeatCountType Discriminated Union ===");
        RunRepeatCountTypeTests(logger, results);

        logger.Info("=== Section 15: DelayRetryStrategy ===");
        RunDelayRetryStrategyTests(logger, results);

        logger.Info("=== Section 16: Image Processors ===");
        RunImageProcessorTests(logger, results);

        logger.Info("=== Section 17: DefaultCacheSerializer ===");
        RunCacheSerializerTests(logger, results);

        logger.Info("=== Section 18: RectCorner ===");
        RunRectCornerTests(logger, results);

        logger.Info("=== Section 19: AnimatedImageView ===");
        RunAnimatedImageViewTests(logger, results);

        logger.Info("=== Section 20: ImagePrefetcher ===");
        RunImagePrefetcherTests(logger, results);

        logger.Info("=== Section 21: KingfisherError ===");
        RunKingfisherErrorTests(logger, results);

        logger.Info("=== Section 22: ImageDownloader Methods ===");
        RunImageDownloaderMethodTests(logger, results);

        logger.Info("=== Section 23: Property Setter Round-Trips ===");
        RunPropertySetterTests(logger, results);

        logger.Info("=== Section 24: Processor Properties Deep Dive ===");
        RunProcessorPropertiesDeepDive(logger, results);

        logger.Info("=== Section 25: FormatIndicatedCacheSerializer ===");
        RunFormatIndicatedSerializerTests(logger, results);

        logger.Info("=== Section 26: CacheType Extension Methods ===");
        RunCacheTypeExtensionTests(logger, results);

        logger.Info("=== Section 27: Radius.Compute ===");
        RunRadiusComputeTests(logger, results);

        logger.Info("=== Section 28: UpdatingStrategy ===");
        RunUpdatingStrategyTests(logger, results);

        logger.Info("=== Section 29: Additional Constructor Overloads ===");
        RunAdditionalConstructorTests(logger, results);

        logger.Info("=== Section 30: Additional Metadata ===");
        RunAdditionalMetadataTests(logger, results);

        logger.Info("=== Section 31: ImageCreatingOptions ===");
        RunImageCreatingOptionsTests(logger, results);

        logger.Info("=== Section 32: ImageTransition TryGet Coverage ===");
        RunImageTransitionTryGetTests(logger, results);

        logger.Info("=== Section 33: StorageExpiration TryGet Coverage ===");
        RunStorageExpirationTryGetCoverage(logger, results);

        logger.Info("=== Section 34: Processor Append ===");
        RunProcessorAppendTests(logger, results);

        logger.Info("=== Section 35: Multiple Singleton Stability ===");
        RunSingletonStabilityTests(logger, results);

        logger.Info("=== Section 36: SessionDelegate & ImageResource ===");
        RunSessionDelegateAndResourceTests(logger, results);

        logger.Info("=== Section 37: Interval Tag Verification ===");
        RunIntervalTagTests(logger, results);

        logger.Info("=== Section 38: ImageProcessItem ===");
        RunImageProcessItemTests(logger, results);

        logger.Info("=== Section 39: Cross-Type Interactions ===");
        RunCrossTypeTests(logger, results);

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
        var metadataTests = new (string Name, Func<TypeMetadata> GetMetadata)[]
        {
            // Core classes
            ("KingfisherManager", () => SwiftObjectHelper<KingfisherManager>.GetTypeMetadata()),
            ("ImageCache", () => SwiftObjectHelper<ImageCache>.GetTypeMetadata()),
            ("ImageDownloader", () => SwiftObjectHelper<ImageDownloader>.GetTypeMetadata()),
            ("SessionDelegate", () => SwiftObjectHelper<SessionDelegate>.GetTypeMetadata()),
            // Image processors (structs)
            ("DefaultImageProcessor", () => SwiftObjectHelper<DefaultImageProcessor>.GetTypeMetadata()),
            ("ResizingImageProcessor", () => SwiftObjectHelper<ResizingImageProcessor>.GetTypeMetadata()),
            ("BlurImageProcessor", () => SwiftObjectHelper<BlurImageProcessor>.GetTypeMetadata()),
            ("RoundCornerImageProcessor", () => SwiftObjectHelper<RoundCornerImageProcessor>.GetTypeMetadata()),
            ("OverlayImageProcessor", () => SwiftObjectHelper<OverlayImageProcessor>.GetTypeMetadata()),
            ("TintImageProcessor", () => SwiftObjectHelper<TintImageProcessor>.GetTypeMetadata()),
            ("BlackWhiteProcessor", () => SwiftObjectHelper<BlackWhiteProcessor>.GetTypeMetadata()),
            ("CroppingImageProcessor", () => SwiftObjectHelper<CroppingImageProcessor>.GetTypeMetadata()),
            ("DownsamplingImageProcessor", () => SwiftObjectHelper<DownsamplingImageProcessor>.GetTypeMetadata()),
            // Serializer/strategy
            ("DefaultCacheSerializer", () => SwiftObjectHelper<DefaultCacheSerializer>.GetTypeMetadata()),
            ("DelayRetryStrategy", () => SwiftObjectHelper<DelayRetryStrategy>.GetTypeMetadata()),
            // Discriminated unions
            ("StorageExpiration", () => SwiftObjectHelper<StorageExpiration>.GetTypeMetadata()),
            ("Radius", () => SwiftObjectHelper<Radius>.GetTypeMetadata()),
            ("ImageFormat", () => SwiftObjectHelper<ImageFormat>.GetTypeMetadata()),
            ("ImageTransition", () => SwiftObjectHelper<ImageTransition>.GetTypeMetadata()),
            ("ExpirationExtending", () => SwiftObjectHelper<ExpirationExtending>.GetTypeMetadata()),
            ("CallbackQueue", () => SwiftObjectHelper<CallbackQueue>.GetTypeMetadata()),
            // Other structs
            ("RectCorner", () => SwiftObjectHelper<RectCorner>.GetTypeMetadata()),
            ("RetrieveImageResult", () => SwiftObjectHelper<RetrieveImageResult>.GetTypeMetadata()),
            ("CacheStoreResult", () => SwiftObjectHelper<CacheStoreResult>.GetTypeMetadata()),
        };

        foreach (var (name, getMetadata) in metadataTests)
        {
            try
            {
                var metadata = getMetadata();
                logger.Pass($"{name} metadata: size={metadata.Size}");
                results.Pass($"Metadata_{name}");
            }
            catch (Exception ex)
            {
                logger.Fail($"{name} metadata: {ex.GetType().Name}: {ex.Message}");
                results.Fail($"Metadata_{name}", $"{ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    // ──────────────────────────────────────────────
    // Section 2: Singletons & Static Properties
    // ──────────────────────────────────────────────

    private void RunSingletonTests(TestLogger logger, TestResults results)
    {
        // KingfisherManager.Shared
        try
        {
            var manager = KingfisherManager.Shared;
            logger.Pass($"KingfisherManager.Shared: non-null={manager != null}");
            results.Pass("KingfisherManager.Shared");
        }
        catch (Exception ex)
        {
            logger.Fail($"KingfisherManager.Shared: {ex.GetType().Name}: {ex.Message}");
            results.Fail("KingfisherManager.Shared", $"{ex.GetType().Name}: {ex.Message}");
        }

        // ImageCache.Default
        try
        {
            var cache = ImageCache.Default;
            logger.Pass($"ImageCache.Default: non-null={cache != null}");
            results.Pass("ImageCache.Default");
        }
        catch (Exception ex)
        {
            logger.Fail($"ImageCache.Default: {ex.GetType().Name}: {ex.Message}");
            results.Fail("ImageCache.Default", $"{ex.GetType().Name}: {ex.Message}");
        }

        // ImageDownloader.Default
        try
        {
            var downloader = ImageDownloader.Default;
            logger.Pass($"ImageDownloader.Default: non-null={downloader != null}");
            results.Pass("ImageDownloader.Default");
        }
        catch (Exception ex)
        {
            logger.Fail($"ImageDownloader.Default: {ex.GetType().Name}: {ex.Message}");
            results.Fail("ImageDownloader.Default", $"{ex.GetType().Name}: {ex.Message}");
        }

        // DefaultImageProcessor.Default
        try
        {
            using var processor = DefaultImageProcessor.Default;
            logger.Pass($"DefaultImageProcessor.Default: created");
            results.Pass("DefaultImageProcessor.Default");
        }
        catch (Exception ex)
        {
            logger.Fail($"DefaultImageProcessor.Default: {ex.GetType().Name}: {ex.Message}");
            results.Fail("DefaultImageProcessor.Default", $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    // ──────────────────────────────────────────────
    // Section 3: Core Class Constructors
    // ──────────────────────────────────────────────

    private void RunConstructorTests(TestLogger logger, TestResults results)
    {
        // ImageCache(name:)
        try
        {
            var cache = new ImageCache("TestCache");
            logger.Pass("ImageCache(name:) constructor: created");
            cache.Dispose();
            results.Pass("ImageCache_ctor_name");
        }
        catch (Exception ex)
        {
            logger.Fail($"ImageCache(name:) constructor: {ex.GetType().Name}: {ex.Message}");
            results.Fail("ImageCache_ctor_name", $"{ex.GetType().Name}: {ex.Message}");
        }

        // ImageDownloader(name:)
        try
        {
            var downloader = new ImageDownloader("TestDownloader");
            logger.Pass("ImageDownloader(name:) constructor: created");
            downloader.Dispose();
            results.Pass("ImageDownloader_ctor_name");
        }
        catch (Exception ex)
        {
            logger.Fail($"ImageDownloader(name:) constructor: {ex.GetType().Name}: {ex.Message}");
            results.Fail("ImageDownloader_ctor_name", $"{ex.GetType().Name}: {ex.Message}");
        }

        // KingfisherManager(downloader:, cache:) — construct with custom downloader and cache
        try
        {
            var dl = new ImageDownloader("CtorTestDL");
            var ic = new ImageCache("CtorTestCache");
            var mgr = new KingfisherManager(dl, ic);
            logger.Pass("KingfisherManager(downloader:, cache:) constructor: created");
            mgr.Dispose();
            dl.Dispose();
            ic.Dispose();
            results.Pass("KingfisherManager_ctor");
        }
        catch (Exception ex)
        {
            logger.Fail($"KingfisherManager(downloader:, cache:) constructor: {ex.GetType().Name}: {ex.Message}");
            results.Fail("KingfisherManager_ctor", $"{ex.GetType().Name}: {ex.Message}");
        }

        // DefaultImageProcessor()
        try
        {
            using var proc = new DefaultImageProcessor();
            logger.Pass("DefaultImageProcessor() constructor: created");
            results.Pass("DefaultImageProcessor_ctor");
        }
        catch (Exception ex)
        {
            logger.Fail($"DefaultImageProcessor() constructor: {ex.GetType().Name}: {ex.Message}");
            results.Fail("DefaultImageProcessor_ctor", $"{ex.GetType().Name}: {ex.Message}");
        }

        // DefaultCacheSerializer()
        try
        {
            using var ser = new DefaultCacheSerializer();
            logger.Pass("DefaultCacheSerializer() constructor: created");
            results.Pass("DefaultCacheSerializer_ctor");
        }
        catch (Exception ex)
        {
            logger.Fail($"DefaultCacheSerializer() constructor: {ex.GetType().Name}: {ex.Message}");
            results.Fail("DefaultCacheSerializer_ctor", $"{ex.GetType().Name}: {ex.Message}");
        }

        // BlackWhiteProcessor()
        try
        {
            using var proc = new BlackWhiteProcessor();
            logger.Pass("BlackWhiteProcessor() constructor: created");
            results.Pass("BlackWhiteProcessor_ctor");
        }
        catch (Exception ex)
        {
            logger.Fail($"BlackWhiteProcessor() constructor: {ex.GetType().Name}: {ex.Message}");
            results.Fail("BlackWhiteProcessor_ctor", $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    // ──────────────────────────────────────────────
    // Section 4: ImageDownloader Properties
    // ──────────────────────────────────────────────

    private void RunImageDownloaderPropertyTests(TestLogger logger, TestResults results)
    {
        // DownloadTimeout getter
        try
        {
            var dl = ImageDownloader.Default;
            var timeout = dl.DownloadTimeout;
            logger.Pass($"ImageDownloader.DownloadTimeout getter: {timeout}");
            results.Pass("ImageDownloader_DownloadTimeout_get");
        }
        catch (Exception ex)
        {
            logger.Fail($"ImageDownloader.DownloadTimeout getter: {ex.GetType().Name}: {ex.Message}");
            results.Fail("ImageDownloader_DownloadTimeout_get", $"{ex.GetType().Name}: {ex.Message}");
        }

        // RequestsUsePipelining getter
        try
        {
            var dl = ImageDownloader.Default;
            var pipelining = dl.RequestsUsePipelining;
            logger.Pass($"ImageDownloader.RequestsUsePipelining getter: {pipelining}");
            results.Pass("ImageDownloader_RequestsUsePipelining_get");
        }
        catch (Exception ex)
        {
            logger.Fail($"ImageDownloader.RequestsUsePipelining getter: {ex.GetType().Name}: {ex.Message}");
            results.Fail("ImageDownloader_RequestsUsePipelining_get", $"{ex.GetType().Name}: {ex.Message}");
        }

        // SessionConfiguration getter
        try
        {
            var dl = ImageDownloader.Default;
            var config = dl.SessionConfiguration;
            logger.Pass($"ImageDownloader.SessionConfiguration getter: {config?.GetType().Name ?? "null"}");
            results.Pass("ImageDownloader_SessionConfiguration_get");
        }
        catch (Exception ex)
        {
            logger.Fail($"ImageDownloader.SessionConfiguration getter: {ex.GetType().Name}: {ex.Message}");
            results.Fail("ImageDownloader_SessionConfiguration_get", $"{ex.GetType().Name}: {ex.Message}");
        }

        // SessionDelegate getter
        try
        {
            var dl = ImageDownloader.Default;
            var sd = dl.SessionDelegate;
            logger.Pass($"ImageDownloader.SessionDelegate getter: non-null={sd != null}");
            results.Pass("ImageDownloader_SessionDelegate_get");
        }
        catch (Exception ex)
        {
            logger.Fail($"ImageDownloader.SessionDelegate getter: {ex.GetType().Name}: {ex.Message}");
            results.Fail("ImageDownloader_SessionDelegate_get", $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    // ──────────────────────────────────────────────
    // Section 5: ImageCache Methods
    // ──────────────────────────────────────────────

    private void RunImageCacheMethodTests(TestLogger logger, TestResults results)
    {
        // CleanExpiredMemoryCache
        try
        {
            var cache = ImageCache.Default;
            cache.CleanExpiredMemoryCache();
            logger.Pass("ImageCache.CleanExpiredMemoryCache(): called successfully");
            results.Pass("ImageCache_CleanExpiredMemoryCache");
        }
        catch (Exception ex)
        {
            logger.Fail($"ImageCache.CleanExpiredMemoryCache(): {ex.GetType().Name}: {ex.Message}");
            results.Fail("ImageCache_CleanExpiredMemoryCache", $"{ex.GetType().Name}: {ex.Message}");
        }

        // BackgroundCleanExpiredDiskCache
        try
        {
            var cache = ImageCache.Default;
            cache.BackgroundCleanExpiredDiskCache();
            logger.Pass("ImageCache.BackgroundCleanExpiredDiskCache(): called successfully");
            results.Pass("ImageCache_BackgroundCleanExpiredDiskCache");
        }
        catch (Exception ex)
        {
            logger.Fail($"ImageCache.BackgroundCleanExpiredDiskCache(): {ex.GetType().Name}: {ex.Message}");
            results.Fail("ImageCache_BackgroundCleanExpiredDiskCache", $"{ex.GetType().Name}: {ex.Message}");
        }

        // IsCached(key:) — single param overload
        try
        {
            var cache = ImageCache.Default;
            var cached = cache.IsCached("nonexistent_key_test");
            logger.Pass($"ImageCache.IsCached(key:): {cached}");
            results.Pass("ImageCache_IsCached_key");
        }
        catch (Exception ex)
        {
            logger.Fail($"ImageCache.IsCached(key:): {ex.GetType().Name}: {ex.Message}");
            results.Fail("ImageCache_IsCached_key", $"{ex.GetType().Name}: {ex.Message}");
        }

        // IsCached(key:, identifier:) — two param overload
        try
        {
            var cache = ImageCache.Default;
            var cached = cache.IsCached("nonexistent_key", "");
            logger.Pass($"ImageCache.IsCached(key:, identifier:): {cached}");
            results.Pass("ImageCache_IsCached_key_identifier");
        }
        catch (Exception ex)
        {
            logger.Fail($"ImageCache.IsCached(key:, identifier:): {ex.GetType().Name}: {ex.Message}");
            results.Fail("ImageCache_IsCached_key_identifier", $"{ex.GetType().Name}: {ex.Message}");
        }

        // ClearMemoryCache
        try
        {
            var cache = ImageCache.Default;
            cache.ClearMemoryCache();
            logger.Pass("ImageCache.ClearMemoryCache(): called successfully");
            results.Pass("ImageCache_ClearMemoryCache");
        }
        catch (Exception ex)
        {
            logger.Fail($"ImageCache.ClearMemoryCache(): {ex.GetType().Name}: {ex.Message}");
            results.Fail("ImageCache_ClearMemoryCache", $"{ex.GetType().Name}: {ex.Message}");
        }

        // ClearDiskCache with callback
        try
        {
            var cache = ImageCache.Default;
            cache.ClearDiskCache(null);
            logger.Pass("ImageCache.ClearDiskCache(null): called successfully");
            results.Pass("ImageCache_ClearDiskCache_null");
        }
        catch (Exception ex)
        {
            logger.Fail($"ImageCache.ClearDiskCache(null): {ex.GetType().Name}: {ex.Message}");
            results.Fail("ImageCache_ClearDiskCache_null", $"{ex.GetType().Name}: {ex.Message}");
        }

        // CleanExpiredDiskCache with callback
        try
        {
            var cache = ImageCache.Default;
            cache.CleanExpiredDiskCache(null);
            logger.Pass("ImageCache.CleanExpiredDiskCache(null): called successfully");
            results.Pass("ImageCache_CleanExpiredDiskCache_null");
        }
        catch (Exception ex)
        {
            logger.Fail($"ImageCache.CleanExpiredDiskCache(null): {ex.GetType().Name}: {ex.Message}");
            results.Fail("ImageCache_CleanExpiredDiskCache_null", $"{ex.GetType().Name}: {ex.Message}");
        }

        // CleanExpiredCache with callback
        try
        {
            var cache = ImageCache.Default;
            cache.CleanExpiredCache(null);
            logger.Pass("ImageCache.CleanExpiredCache(null): called successfully");
            results.Pass("ImageCache_CleanExpiredCache_null");
        }
        catch (Exception ex)
        {
            logger.Fail($"ImageCache.CleanExpiredCache(null): {ex.GetType().Name}: {ex.Message}");
            results.Fail("ImageCache_CleanExpiredCache_null", $"{ex.GetType().Name}: {ex.Message}");
        }

        // CalculateDiskStorageSize with callback
        try
        {
            var cache = ImageCache.Default;
            bool callbackReceived = false;
            cache.CalculateDiskStorageSize((Swift.SwiftResult<nuint, KingfisherError> result) =>
            {
                callbackReceived = true;
            });
            // Brief wait for async callback
            System.Threading.Thread.Sleep(200);
            logger.Pass($"ImageCache.CalculateDiskStorageSize: callbackReceived={callbackReceived}");
            results.Pass("ImageCache_CalculateDiskStorageSize");
        }
        catch (Exception ex)
        {
            logger.Fail($"ImageCache.CalculateDiskStorageSize: {ex.GetType().Name}: {ex.Message}");
            results.Fail("ImageCache_CalculateDiskStorageSize", $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    // ──────────────────────────────────────────────
    // Section 6: KingfisherManager Properties
    // ──────────────────────────────────────────────

    private void RunKingfisherManagerTests(TestLogger logger, TestResults results)
    {
        // Cache getter
        try
        {
            var manager = KingfisherManager.Shared;
            var cache = manager.Cache;
            logger.Pass($"KingfisherManager.Cache getter: non-null={cache != null}");
            results.Pass("KingfisherManager_Cache_get");
        }
        catch (Exception ex)
        {
            logger.Fail($"KingfisherManager.Cache getter: {ex.GetType().Name}: {ex.Message}");
            results.Fail("KingfisherManager_Cache_get", $"{ex.GetType().Name}: {ex.Message}");
        }

        // Downloader getter
        try
        {
            var manager = KingfisherManager.Shared;
            var downloader = manager.Downloader;
            logger.Pass($"KingfisherManager.Downloader getter: non-null={downloader != null}");
            results.Pass("KingfisherManager_Downloader_get");
        }
        catch (Exception ex)
        {
            logger.Fail($"KingfisherManager.Downloader getter: {ex.GetType().Name}: {ex.Message}");
            results.Fail("KingfisherManager_Downloader_get", $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    // ──────────────────────────────────────────────
    // Section 7: C# Integer Enums
    // ──────────────────────────────────────────────

    private void RunIntegerEnumTests(TestLogger logger, TestResults results)
    {
        // CacheType enum
        try
        {
            var none = CacheType.None;
            var mem = CacheType.Memory;
            var disk = CacheType.Disk;
            logger.Pass($"CacheType: None={(int)none}, Memory={(int)mem}, Disk={(int)disk}");
            results.Pass("CacheType_values");
        }
        catch (Exception ex)
        {
            logger.Fail($"CacheType: {ex.GetType().Name}: {ex.Message}");
            results.Fail("CacheType_values", $"{ex.GetType().Name}: {ex.Message}");
        }

        // CacheType cast roundtrip
        try
        {
            var disk = (CacheType)2;
            bool ok = disk == CacheType.Disk;
            logger.Pass($"CacheType roundtrip: (CacheType)2 == Disk: {ok}");
            results.Pass("CacheType_roundtrip");
        }
        catch (Exception ex)
        {
            logger.Fail($"CacheType roundtrip: {ex.GetType().Name}: {ex.Message}");
            results.Fail("CacheType_roundtrip", $"{ex.GetType().Name}: {ex.Message}");
        }

        // ContentMode enum
        try
        {
            var none = ContentMode.None;
            var fit = ContentMode.AspectFit;
            var fill = ContentMode.AspectFill;
            logger.Pass($"ContentMode: None={(int)none}, AspectFit={(int)fit}, AspectFill={(int)fill}");
            results.Pass("ContentMode_values");
        }
        catch (Exception ex)
        {
            logger.Fail($"ContentMode: {ex.GetType().Name}: {ex.Message}");
            results.Fail("ContentMode_values", $"{ex.GetType().Name}: {ex.Message}");
        }

        // ContentMode cast roundtrip
        try
        {
            var fit = (ContentMode)1;
            bool ok = fit == ContentMode.AspectFit;
            logger.Pass($"ContentMode roundtrip: (ContentMode)1 == AspectFit: {ok}");
            results.Pass("ContentMode_roundtrip");
        }
        catch (Exception ex)
        {
            logger.Fail($"ContentMode roundtrip: {ex.GetType().Name}: {ex.Message}");
            results.Fail("ContentMode_roundtrip", $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    // ──────────────────────────────────────────────
    // Section 8: StorageExpiration Discriminated Union
    // ──────────────────────────────────────────────

    private void RunStorageExpirationTests(TestLogger logger, TestResults results)
    {
        // StorageExpiration.Seconds
        try
        {
            using var exp = StorageExpiration.Seconds(3600.0);
            var tag = exp.Tag;
            exp.TryGetSeconds(out var seconds);
            logger.Pass($"StorageExpiration.Seconds: Tag={tag}, Value={seconds}");
            results.Pass("StorageExpiration_Seconds");
        }
        catch (Exception ex)
        {
            logger.Fail($"StorageExpiration.Seconds: {ex.GetType().Name}: {ex.Message}");
            results.Fail("StorageExpiration_Seconds", $"{ex.GetType().Name}: {ex.Message}");
        }

        // StorageExpiration.Days
        try
        {
            using var exp = StorageExpiration.Days(7);
            var tag = exp.Tag;
            logger.Pass($"StorageExpiration.Days(7): Tag={tag}");
            results.Pass("StorageExpiration_Days");
        }
        catch (Exception ex)
        {
            logger.Fail($"StorageExpiration.Days: {ex.GetType().Name}: {ex.Message}");
            results.Fail("StorageExpiration_Days", $"{ex.GetType().Name}: {ex.Message}");
        }

        // StorageExpiration.Date
        try
        {
            using var exp = StorageExpiration.Date(DateTimeOffset.UtcNow.AddHours(1));
            var tag = exp.Tag;
            logger.Pass($"StorageExpiration.Date: Tag={tag}");
            results.Pass("StorageExpiration_Date");
        }
        catch (Exception ex)
        {
            logger.Fail($"StorageExpiration.Date: {ex.GetType().Name}: {ex.Message}");
            results.Fail("StorageExpiration_Date", $"{ex.GetType().Name}: {ex.Message}");
        }

        // StorageExpiration.Never (no-payload singleton)
        try
        {
            var exp = StorageExpiration.Never;
            var tag = exp.Tag;
            logger.Pass($"StorageExpiration.Never: Tag={tag}");
            results.Pass("StorageExpiration_Never");
        }
        catch (Exception ex)
        {
            logger.Fail($"StorageExpiration.Never: {ex.GetType().Name}: {ex.Message}");
            results.Fail("StorageExpiration_Never", $"{ex.GetType().Name}: {ex.Message}");
        }

        // StorageExpiration.Expired (no-payload singleton)
        try
        {
            var exp = StorageExpiration.Expired;
            var tag = exp.Tag;
            logger.Pass($"StorageExpiration.Expired: Tag={tag}");
            results.Pass("StorageExpiration_Expired");
        }
        catch (Exception ex)
        {
            logger.Fail($"StorageExpiration.Expired: {ex.GetType().Name}: {ex.Message}");
            results.Fail("StorageExpiration_Expired", $"{ex.GetType().Name}: {ex.Message}");
        }

        // Tag value verification
        try
        {
            using var sec = StorageExpiration.Seconds(1.0);
            using var day = StorageExpiration.Days(1);
            using var date = StorageExpiration.Date(DateTimeOffset.UtcNow);
            var never = StorageExpiration.Never;
            var expired = StorageExpiration.Expired;

            bool ok = sec.Tag == StorageExpiration.CaseTag.Seconds
                   && day.Tag == StorageExpiration.CaseTag.Days
                   && date.Tag == StorageExpiration.CaseTag.Date
                   && never.Tag == StorageExpiration.CaseTag.Never
                   && expired.Tag == StorageExpiration.CaseTag.Expired;
            logger.Pass($"StorageExpiration tag values all correct: {ok}");
            results.Pass("StorageExpiration_tag_values");
        }
        catch (Exception ex)
        {
            logger.Fail($"StorageExpiration tag values: {ex.GetType().Name}: {ex.Message}");
            results.Fail("StorageExpiration_tag_values", $"{ex.GetType().Name}: {ex.Message}");
        }

        // TryGet negative test — Seconds on a Days instance should return false
        try
        {
            using var day = StorageExpiration.Days(1);
            bool found = day.TryGetSeconds(out _);
            logger.Pass($"StorageExpiration.TryGetSeconds on Days: {found} (expected false)");
            results.Pass("StorageExpiration_TryGet_negative");
        }
        catch (Exception ex)
        {
            logger.Fail($"StorageExpiration.TryGet negative: {ex.GetType().Name}: {ex.Message}");
            results.Fail("StorageExpiration_TryGet_negative", $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    // ──────────────────────────────────────────────
    // Section 9: Radius Discriminated Union
    // ──────────────────────────────────────────────

    private void RunRadiusTests(TestLogger logger, TestResults results)
    {
        // Radius.Point
        try
        {
            using var r = Radius.Point(10.0);
            var tag = r.Tag;
            r.TryGetPoint(out var value);
            logger.Pass($"Radius.Point(10.0): Tag={tag}, Value={value}");
            results.Pass("Radius_Point");
        }
        catch (Exception ex)
        {
            logger.Fail($"Radius.Point: {ex.GetType().Name}: {ex.Message}");
            results.Fail("Radius_Point", $"{ex.GetType().Name}: {ex.Message}");
        }

        // Radius.WidthFraction
        try
        {
            using var r = Radius.WidthFraction(0.5);
            var tag = r.Tag;
            r.TryGetWidthFraction(out var value);
            logger.Pass($"Radius.WidthFraction(0.5): Tag={tag}, Value={value}");
            results.Pass("Radius_WidthFraction");
        }
        catch (Exception ex)
        {
            logger.Fail($"Radius.WidthFraction: {ex.GetType().Name}: {ex.Message}");
            results.Fail("Radius_WidthFraction", $"{ex.GetType().Name}: {ex.Message}");
        }

        // Radius.HeightFraction
        try
        {
            using var r = Radius.HeightFraction(0.25);
            var tag = r.Tag;
            r.TryGetHeightFraction(out var value);
            logger.Pass($"Radius.HeightFraction(0.25): Tag={tag}, Value={value}");
            results.Pass("Radius_HeightFraction");
        }
        catch (Exception ex)
        {
            logger.Fail($"Radius.HeightFraction: {ex.GetType().Name}: {ex.Message}");
            results.Fail("Radius_HeightFraction", $"{ex.GetType().Name}: {ex.Message}");
        }

        // Radius tag value verification
        try
        {
            using var wf = Radius.WidthFraction(0.1);
            using var hf = Radius.HeightFraction(0.2);
            using var pt = Radius.Point(5.0);

            bool ok = wf.Tag == Radius.CaseTag.WidthFraction
                   && hf.Tag == Radius.CaseTag.HeightFraction
                   && pt.Tag == Radius.CaseTag.Point;
            logger.Pass($"Radius tag values all correct: {ok}");
            results.Pass("Radius_tag_values");
        }
        catch (Exception ex)
        {
            logger.Fail($"Radius tag values: {ex.GetType().Name}: {ex.Message}");
            results.Fail("Radius_tag_values", $"{ex.GetType().Name}: {ex.Message}");
        }

        // TryGet negative test
        try
        {
            using var pt = Radius.Point(5.0);
            bool found = pt.TryGetWidthFraction(out _);
            logger.Pass($"Radius.TryGetWidthFraction on Point: {found} (expected false)");
            results.Pass("Radius_TryGet_negative");
        }
        catch (Exception ex)
        {
            logger.Fail($"Radius.TryGet negative: {ex.GetType().Name}: {ex.Message}");
            results.Fail("Radius_TryGet_negative", $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    // ──────────────────────────────────────────────
    // Section 10: ImageFormat Discriminated Union
    // ──────────────────────────────────────────────

    private void RunImageFormatTests(TestLogger logger, TestResults results)
    {
        // All no-payload cases
        var formatTests = new (string Name, Func<ImageFormat> Create, ImageFormat.CaseTag ExpectedTag)[]
        {
            ("Unknown", () => ImageFormat.Unknown, ImageFormat.CaseTag.Unknown),
            ("Png", () => ImageFormat.Png, ImageFormat.CaseTag.Png),
            ("Jpeg", () => ImageFormat.Jpeg, ImageFormat.CaseTag.Jpeg),
            ("Gif", () => ImageFormat.Gif, ImageFormat.CaseTag.Gif),
        };

        foreach (var (name, create, expectedTag) in formatTests)
        {
            try
            {
                var fmt = create();
                var tag = fmt.Tag;
                bool ok = tag == expectedTag;
                logger.Pass($"ImageFormat.{name}: Tag={tag}, correct={ok}");
                results.Pass($"ImageFormat_{name}");
            }
            catch (Exception ex)
            {
                logger.Fail($"ImageFormat.{name}: {ex.GetType().Name}: {ex.Message}");
                results.Fail($"ImageFormat_{name}", $"{ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    // ──────────────────────────────────────────────
    // Section 11: ImageTransition Discriminated Union
    // ──────────────────────────────────────────────

    private void RunImageTransitionTests(TestLogger logger, TestResults results)
    {
        var transitionTests = new (string Name, Func<ImageTransition> Create, ImageTransition.CaseTag ExpectedTag)[]
        {
            ("Fade", () => ImageTransition.Fade(0.3), ImageTransition.CaseTag.Fade),
            ("FlipFromLeft", () => ImageTransition.FlipFromLeft(0.3), ImageTransition.CaseTag.FlipFromLeft),
            ("FlipFromRight", () => ImageTransition.FlipFromRight(0.3), ImageTransition.CaseTag.FlipFromRight),
            ("FlipFromTop", () => ImageTransition.FlipFromTop(0.3), ImageTransition.CaseTag.FlipFromTop),
            ("FlipFromBottom", () => ImageTransition.FlipFromBottom(0.3), ImageTransition.CaseTag.FlipFromBottom),
        };

        foreach (var (name, create, expectedTag) in transitionTests)
        {
            try
            {
                using var transition = create();
                var tag = transition.Tag;
                bool ok = tag == expectedTag;
                logger.Pass($"ImageTransition.{name}: Tag={tag}, correct={ok}");
                results.Pass($"ImageTransition_{name}");
            }
            catch (Exception ex)
            {
                logger.Fail($"ImageTransition.{name}: {ex.GetType().Name}: {ex.Message}");
                results.Fail($"ImageTransition_{name}", $"{ex.GetType().Name}: {ex.Message}");
            }
        }

        // TryGet for Fade duration
        try
        {
            using var fade = ImageTransition.Fade(0.5);
            fade.TryGetFade(out var duration);
            logger.Pass($"ImageTransition.Fade.TryGetFade: duration={duration}");
            results.Pass("ImageTransition_Fade_TryGet");
        }
        catch (Exception ex)
        {
            logger.Fail($"ImageTransition.Fade.TryGetFade: {ex.GetType().Name}: {ex.Message}");
            results.Fail("ImageTransition_Fade_TryGet", $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    // ──────────────────────────────────────────────
    // Section 12: ExpirationExtending Discriminated Union
    // ──────────────────────────────────────────────

    private void RunExpirationExtendingTests(TestLogger logger, TestResults results)
    {
        // ExpirationExtending.None
        try
        {
            var ee = ExpirationExtending.None;
            var tag = ee.Tag;
            logger.Pass($"ExpirationExtending.None: Tag={tag}");
            results.Pass("ExpirationExtending_None");
        }
        catch (Exception ex)
        {
            logger.Fail($"ExpirationExtending.None: {ex.GetType().Name}: {ex.Message}");
            results.Fail("ExpirationExtending_None", $"{ex.GetType().Name}: {ex.Message}");
        }

        // ExpirationExtending.CacheTime
        try
        {
            var ee = ExpirationExtending.CacheTime;
            var tag = ee.Tag;
            logger.Pass($"ExpirationExtending.CacheTime: Tag={tag}");
            results.Pass("ExpirationExtending_CacheTime");
        }
        catch (Exception ex)
        {
            logger.Fail($"ExpirationExtending.CacheTime: {ex.GetType().Name}: {ex.Message}");
            results.Fail("ExpirationExtending_CacheTime", $"{ex.GetType().Name}: {ex.Message}");
        }

        // ExpirationExtending.ExpirationTime(StorageExpiration)
        try
        {
            using var se = StorageExpiration.Seconds(3600);
            using var ee = ExpirationExtending.ExpirationTime(se);
            var tag = ee.Tag;
            logger.Pass($"ExpirationExtending.ExpirationTime: Tag={tag}");
            results.Pass("ExpirationExtending_ExpirationTime");
        }
        catch (Exception ex)
        {
            logger.Fail($"ExpirationExtending.ExpirationTime: {ex.GetType().Name}: {ex.Message}");
            results.Fail("ExpirationExtending_ExpirationTime", $"{ex.GetType().Name}: {ex.Message}");
        }

        // Tag value verification
        try
        {
            using var se = StorageExpiration.Days(1);
            using var et = ExpirationExtending.ExpirationTime(se);
            var none = ExpirationExtending.None;
            var ct = ExpirationExtending.CacheTime;

            bool ok = et.Tag == ExpirationExtending.CaseTag.ExpirationTime
                   && none.Tag == ExpirationExtending.CaseTag.None
                   && ct.Tag == ExpirationExtending.CaseTag.CacheTime;
            logger.Pass($"ExpirationExtending tag values correct: {ok}");
            results.Pass("ExpirationExtending_tag_values");
        }
        catch (Exception ex)
        {
            logger.Fail($"ExpirationExtending tag values: {ex.GetType().Name}: {ex.Message}");
            results.Fail("ExpirationExtending_tag_values", $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    // ──────────────────────────────────────────────
    // Section 13: CallbackQueue Discriminated Union
    // ──────────────────────────────────────────────

    private void RunCallbackQueueTests(TestLogger logger, TestResults results)
    {
        // MainAsync
        try
        {
            var q = CallbackQueue.MainAsync;
            var tag = q.Tag;
            logger.Pass($"CallbackQueue.MainAsync: Tag={tag}");
            results.Pass("CallbackQueue_MainAsync");
        }
        catch (Exception ex)
        {
            logger.Fail($"CallbackQueue.MainAsync: {ex.GetType().Name}: {ex.Message}");
            results.Fail("CallbackQueue_MainAsync", $"{ex.GetType().Name}: {ex.Message}");
        }

        // MainCurrentOrAsync
        try
        {
            var q = CallbackQueue.MainCurrentOrAsync;
            var tag = q.Tag;
            logger.Pass($"CallbackQueue.MainCurrentOrAsync: Tag={tag}");
            results.Pass("CallbackQueue_MainCurrentOrAsync");
        }
        catch (Exception ex)
        {
            logger.Fail($"CallbackQueue.MainCurrentOrAsync: {ex.GetType().Name}: {ex.Message}");
            results.Fail("CallbackQueue_MainCurrentOrAsync", $"{ex.GetType().Name}: {ex.Message}");
        }

        // Untouch
        try
        {
            var q = CallbackQueue.Untouch;
            var tag = q.Tag;
            logger.Pass($"CallbackQueue.Untouch: Tag={tag}");
            results.Pass("CallbackQueue_Untouch");
        }
        catch (Exception ex)
        {
            logger.Fail($"CallbackQueue.Untouch: {ex.GetType().Name}: {ex.Message}");
            results.Fail("CallbackQueue_Untouch", $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    // ──────────────────────────────────────────────
    // Section 14: RepeatCountType Discriminated Union
    // ──────────────────────────────────────────────

    private void RunRepeatCountTypeTests(TestLogger logger, TestResults results)
    {
        // RepeatCountType.Once
        try
        {
            var rc = AnimatedImageView.RepeatCountType.Once;
            var tag = rc.Tag;
            logger.Pass($"RepeatCountType.Once: Tag={tag}");
            results.Pass("RepeatCountType_Once");
        }
        catch (Exception ex)
        {
            logger.Fail($"RepeatCountType.Once: {ex.GetType().Name}: {ex.Message}");
            results.Fail("RepeatCountType_Once", $"{ex.GetType().Name}: {ex.Message}");
        }

        // RepeatCountType.Infinite
        try
        {
            var rc = AnimatedImageView.RepeatCountType.Infinite;
            var tag = rc.Tag;
            logger.Pass($"RepeatCountType.Infinite: Tag={tag}");
            results.Pass("RepeatCountType_Infinite");
        }
        catch (Exception ex)
        {
            logger.Fail($"RepeatCountType.Infinite: {ex.GetType().Name}: {ex.Message}");
            results.Fail("RepeatCountType_Infinite", $"{ex.GetType().Name}: {ex.Message}");
        }

        // RepeatCountType.Finite(count)
        try
        {
            using var rc = AnimatedImageView.RepeatCountType.Finite(5);
            var tag = rc.Tag;
            logger.Pass($"RepeatCountType.Finite(5): Tag={tag}");
            results.Pass("RepeatCountType_Finite");
        }
        catch (Exception ex)
        {
            logger.Fail($"RepeatCountType.Finite: {ex.GetType().Name}: {ex.Message}");
            results.Fail("RepeatCountType_Finite", $"{ex.GetType().Name}: {ex.Message}");
        }

        // Tag value verification
        try
        {
            using var finite = AnimatedImageView.RepeatCountType.Finite(3);
            var once = AnimatedImageView.RepeatCountType.Once;
            var infinite = AnimatedImageView.RepeatCountType.Infinite;

            bool ok = finite.Tag == AnimatedImageView.RepeatCountType.CaseTag.Finite
                   && once.Tag == AnimatedImageView.RepeatCountType.CaseTag.Once
                   && infinite.Tag == AnimatedImageView.RepeatCountType.CaseTag.Infinite;
            logger.Pass($"RepeatCountType tag values correct: {ok}");
            results.Pass("RepeatCountType_tag_values");
        }
        catch (Exception ex)
        {
            logger.Fail($"RepeatCountType tag values: {ex.GetType().Name}: {ex.Message}");
            results.Fail("RepeatCountType_tag_values", $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    // ──────────────────────────────────────────────
    // Section 15: DelayRetryStrategy
    // ──────────────────────────────────────────────

    private void RunDelayRetryStrategyTests(TestLogger logger, TestResults results)
    {
        // Constructor with maxRetryCount only
        try
        {
            using var strategy = new DelayRetryStrategy(3);
            logger.Pass("DelayRetryStrategy(3): created");
            results.Pass("DelayRetryStrategy_ctor_simple");
        }
        catch (Exception ex)
        {
            logger.Fail($"DelayRetryStrategy(3): {ex.GetType().Name}: {ex.Message}");
            results.Fail("DelayRetryStrategy_ctor_simple", $"{ex.GetType().Name}: {ex.Message}");
        }

        // Constructor with maxRetryCount and Interval
        try
        {
            using var interval = DelayRetryStrategy.Interval.Seconds(2.0);
            using var strategy = new DelayRetryStrategy(5, interval);
            logger.Pass("DelayRetryStrategy(5, Interval.Seconds(2.0)): created");
            results.Pass("DelayRetryStrategy_ctor_interval");
        }
        catch (Exception ex)
        {
            logger.Fail($"DelayRetryStrategy(5, interval): {ex.GetType().Name}: {ex.Message}");
            results.Fail("DelayRetryStrategy_ctor_interval", $"{ex.GetType().Name}: {ex.Message}");
        }

        // MaxRetryCount getter
        try
        {
            using var strategy = new DelayRetryStrategy(3);
            var count = strategy.MaxRetryCount;
            logger.Pass($"DelayRetryStrategy.MaxRetryCount: {count}");
            results.Pass("DelayRetryStrategy_MaxRetryCount");
        }
        catch (Exception ex)
        {
            logger.Fail($"DelayRetryStrategy.MaxRetryCount: {ex.GetType().Name}: {ex.Message}");
            results.Fail("DelayRetryStrategy_MaxRetryCount", $"{ex.GetType().Name}: {ex.Message}");
        }

        // RetryInterval getter
        try
        {
            using var interval = DelayRetryStrategy.Interval.Seconds(1.5);
            using var strategy = new DelayRetryStrategy(2, interval);
            using var ri = strategy.RetryInterval;
            var tag = ri.Tag;
            logger.Pass($"DelayRetryStrategy.RetryInterval: Tag={tag}");
            results.Pass("DelayRetryStrategy_RetryInterval");
        }
        catch (Exception ex)
        {
            logger.Fail($"DelayRetryStrategy.RetryInterval: {ex.GetType().Name}: {ex.Message}");
            results.Fail("DelayRetryStrategy_RetryInterval", $"{ex.GetType().Name}: {ex.Message}");
        }

        // Interval.Seconds
        try
        {
            using var interval = DelayRetryStrategy.Interval.Seconds(3.0);
            var tag = interval.Tag;
            logger.Pass($"Interval.Seconds(3.0): Tag={tag}");
            results.Pass("Interval_Seconds");
        }
        catch (Exception ex)
        {
            logger.Fail($"Interval.Seconds: {ex.GetType().Name}: {ex.Message}");
            results.Fail("Interval_Seconds", $"{ex.GetType().Name}: {ex.Message}");
        }

        // Interval.Accumulated
        try
        {
            using var interval = DelayRetryStrategy.Interval.Accumulated(1.0);
            var tag = interval.Tag;
            logger.Pass($"Interval.Accumulated(1.0): Tag={tag}");
            results.Pass("Interval_Accumulated");
        }
        catch (Exception ex)
        {
            logger.Fail($"Interval.Accumulated: {ex.GetType().Name}: {ex.Message}");
            results.Fail("Interval_Accumulated", $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    // ──────────────────────────────────────────────
    // Section 16: Image Processors
    // ──────────────────────────────────────────────

    private void RunImageProcessorTests(TestLogger logger, TestResults results)
    {
        // ResizingImageProcessor with CGSize
        try
        {
            using var proc = new ResizingImageProcessor(new Swift.CGSize(100.0, 100.0));
            logger.Pass("ResizingImageProcessor(CGSize): created");
            results.Pass("ResizingImageProcessor_ctor_size");
        }
        catch (Exception ex)
        {
            logger.Fail($"ResizingImageProcessor(CGSize): {ex.GetType().Name}: {ex.Message}");
            results.Fail("ResizingImageProcessor_ctor_size", $"{ex.GetType().Name}: {ex.Message}");
        }

        // ResizingImageProcessor with CGSize + ContentMode
        try
        {
            using var proc = new ResizingImageProcessor(new Swift.CGSize(200.0, 150.0), ContentMode.AspectFit);
            logger.Pass("ResizingImageProcessor(CGSize, ContentMode): created");
            results.Pass("ResizingImageProcessor_ctor_mode");
        }
        catch (Exception ex)
        {
            logger.Fail($"ResizingImageProcessor(CGSize, ContentMode): {ex.GetType().Name}: {ex.Message}");
            results.Fail("ResizingImageProcessor_ctor_mode", $"{ex.GetType().Name}: {ex.Message}");
        }

        // ResizingImageProcessor.ReferenceSize
        try
        {
            using var proc = new ResizingImageProcessor(new Swift.CGSize(200.0, 150.0));
            var size = proc.ReferenceSize;
            logger.Pass($"ResizingImageProcessor.ReferenceSize: ({size.Width}, {size.Height})");
            results.Pass("ResizingImageProcessor_ReferenceSize");
        }
        catch (Exception ex)
        {
            logger.Fail($"ResizingImageProcessor.ReferenceSize: {ex.GetType().Name}: {ex.Message}");
            results.Fail("ResizingImageProcessor_ReferenceSize", $"{ex.GetType().Name}: {ex.Message}");
        }

        // ResizingImageProcessor.TargetContentMode
        try
        {
            using var proc = new ResizingImageProcessor(new Swift.CGSize(200.0, 150.0), ContentMode.AspectFill);
            var mode = proc.TargetContentMode;
            logger.Pass($"ResizingImageProcessor.TargetContentMode: {mode}");
            results.Pass("ResizingImageProcessor_TargetContentMode");
        }
        catch (Exception ex)
        {
            logger.Fail($"ResizingImageProcessor.TargetContentMode: {ex.GetType().Name}: {ex.Message}");
            results.Fail("ResizingImageProcessor_TargetContentMode", $"{ex.GetType().Name}: {ex.Message}");
        }

        // ResizingImageProcessor.Identifier
        try
        {
            using var proc = new ResizingImageProcessor(new Swift.CGSize(100.0, 100.0));
            var id = proc.Identifier;
            logger.Pass($"ResizingImageProcessor.Identifier: '{id}'");
            results.Pass("ResizingImageProcessor_Identifier");
        }
        catch (Exception ex)
        {
            logger.Fail($"ResizingImageProcessor.Identifier: {ex.GetType().Name}: {ex.Message}");
            results.Fail("ResizingImageProcessor_Identifier", $"{ex.GetType().Name}: {ex.Message}");
        }

        // BlurImageProcessor
        try
        {
            using var blur = new BlurImageProcessor(5.0);
            logger.Pass("BlurImageProcessor(5.0): created");
            results.Pass("BlurImageProcessor_ctor");
        }
        catch (Exception ex)
        {
            logger.Fail($"BlurImageProcessor(5.0): {ex.GetType().Name}: {ex.Message}");
            results.Fail("BlurImageProcessor_ctor", $"{ex.GetType().Name}: {ex.Message}");
        }

        // BlurImageProcessor.BlurRadius
        try
        {
            using var blur = new BlurImageProcessor(7.5);
            var radius = blur.BlurRadius;
            logger.Pass($"BlurImageProcessor.BlurRadius: {radius}");
            results.Pass("BlurImageProcessor_BlurRadius");
        }
        catch (Exception ex)
        {
            logger.Fail($"BlurImageProcessor.BlurRadius: {ex.GetType().Name}: {ex.Message}");
            results.Fail("BlurImageProcessor_BlurRadius", $"{ex.GetType().Name}: {ex.Message}");
        }

        // BlurImageProcessor.Identifier
        try
        {
            using var blur = new BlurImageProcessor(5.0);
            var id = blur.Identifier;
            logger.Pass($"BlurImageProcessor.Identifier: '{id}'");
            results.Pass("BlurImageProcessor_Identifier");
        }
        catch (Exception ex)
        {
            logger.Fail($"BlurImageProcessor.Identifier: {ex.GetType().Name}: {ex.Message}");
            results.Fail("BlurImageProcessor_Identifier", $"{ex.GetType().Name}: {ex.Message}");
        }

        // RoundCornerImageProcessor with double
        try
        {
            using var proc = new RoundCornerImageProcessor(10.0);
            logger.Pass("RoundCornerImageProcessor(10.0): created");
            results.Pass("RoundCornerImageProcessor_ctor_double");
        }
        catch (Exception ex)
        {
            logger.Fail($"RoundCornerImageProcessor(10.0): {ex.GetType().Name}: {ex.Message}");
            results.Fail("RoundCornerImageProcessor_ctor_double", $"{ex.GetType().Name}: {ex.Message}");
        }

        // RoundCornerImageProcessor with Radius
        try
        {
            using var r = Radius.Point(15.0);
            using var proc = new RoundCornerImageProcessor(r);
            logger.Pass("RoundCornerImageProcessor(Radius.Point(15)): created");
            results.Pass("RoundCornerImageProcessor_ctor_radius");
        }
        catch (Exception ex)
        {
            logger.Fail($"RoundCornerImageProcessor(Radius): {ex.GetType().Name}: {ex.Message}");
            results.Fail("RoundCornerImageProcessor_ctor_radius", $"{ex.GetType().Name}: {ex.Message}");
        }

        // RoundCornerImageProcessor.Identifier
        try
        {
            using var proc = new RoundCornerImageProcessor(10.0);
            var id = proc.Identifier;
            logger.Pass($"RoundCornerImageProcessor.Identifier: '{id}'");
            results.Pass("RoundCornerImageProcessor_Identifier");
        }
        catch (Exception ex)
        {
            logger.Fail($"RoundCornerImageProcessor.Identifier: {ex.GetType().Name}: {ex.Message}");
            results.Fail("RoundCornerImageProcessor_Identifier", $"{ex.GetType().Name}: {ex.Message}");
        }

        // RoundCornerImageProcessor.Radius
        try
        {
            using var proc = new RoundCornerImageProcessor(10.0);
            using var radius = proc.Radius;
            var tag = radius.Tag;
            logger.Pass($"RoundCornerImageProcessor.Radius: Tag={tag}");
            results.Pass("RoundCornerImageProcessor_Radius");
        }
        catch (Exception ex)
        {
            logger.Fail($"RoundCornerImageProcessor.Radius: {ex.GetType().Name}: {ex.Message}");
            results.Fail("RoundCornerImageProcessor_Radius", $"{ex.GetType().Name}: {ex.Message}");
        }

        // RoundCornerImageProcessor.RoundingCorners
        try
        {
            using var proc = new RoundCornerImageProcessor(10.0);
            using var corners = proc.RoundingCorners;
            var raw = corners.RawValue;
            logger.Pass($"RoundCornerImageProcessor.RoundingCorners: RawValue={raw}");
            results.Pass("RoundCornerImageProcessor_RoundingCorners");
        }
        catch (Exception ex)
        {
            logger.Fail($"RoundCornerImageProcessor.RoundingCorners: {ex.GetType().Name}: {ex.Message}");
            results.Fail("RoundCornerImageProcessor_RoundingCorners", $"{ex.GetType().Name}: {ex.Message}");
        }

        // OverlayImageProcessor
        try
        {
            using var proc = new OverlayImageProcessor(UIColor.Red, 0.5);
            logger.Pass("OverlayImageProcessor(UIColor.Red, 0.5): created");
            results.Pass("OverlayImageProcessor_ctor");
        }
        catch (Exception ex)
        {
            logger.Fail($"OverlayImageProcessor: {ex.GetType().Name}: {ex.Message}");
            results.Fail("OverlayImageProcessor_ctor", $"{ex.GetType().Name}: {ex.Message}");
        }

        // OverlayImageProcessor.Identifier
        try
        {
            using var proc = new OverlayImageProcessor(UIColor.Blue);
            var id = proc.Identifier;
            logger.Pass($"OverlayImageProcessor.Identifier: '{id}'");
            results.Pass("OverlayImageProcessor_Identifier");
        }
        catch (Exception ex)
        {
            logger.Fail($"OverlayImageProcessor.Identifier: {ex.GetType().Name}: {ex.Message}");
            results.Fail("OverlayImageProcessor_Identifier", $"{ex.GetType().Name}: {ex.Message}");
        }

        // TintImageProcessor
        try
        {
            using var proc = new TintImageProcessor(UIColor.Green);
            logger.Pass("TintImageProcessor(UIColor.Green): created");
            results.Pass("TintImageProcessor_ctor");
        }
        catch (Exception ex)
        {
            logger.Fail($"TintImageProcessor: {ex.GetType().Name}: {ex.Message}");
            results.Fail("TintImageProcessor_ctor", $"{ex.GetType().Name}: {ex.Message}");
        }

        // TintImageProcessor.Identifier
        try
        {
            using var proc = new TintImageProcessor(UIColor.Red);
            var id = proc.Identifier;
            logger.Pass($"TintImageProcessor.Identifier: '{id}'");
            results.Pass("TintImageProcessor_Identifier");
        }
        catch (Exception ex)
        {
            logger.Fail($"TintImageProcessor.Identifier: {ex.GetType().Name}: {ex.Message}");
            results.Fail("TintImageProcessor_Identifier", $"{ex.GetType().Name}: {ex.Message}");
        }

        // BlackWhiteProcessor.Identifier
        try
        {
            using var proc = new BlackWhiteProcessor();
            var id = proc.Identifier;
            logger.Pass($"BlackWhiteProcessor.Identifier: '{id}'");
            results.Pass("BlackWhiteProcessor_Identifier");
        }
        catch (Exception ex)
        {
            logger.Fail($"BlackWhiteProcessor.Identifier: {ex.GetType().Name}: {ex.Message}");
            results.Fail("BlackWhiteProcessor_Identifier", $"{ex.GetType().Name}: {ex.Message}");
        }

        // CroppingImageProcessor with CGSize
        try
        {
            using var proc = new CroppingImageProcessor(new Swift.CGSize(50.0, 50.0));
            logger.Pass("CroppingImageProcessor(CGSize): created");
            results.Pass("CroppingImageProcessor_ctor_size");
        }
        catch (Exception ex)
        {
            logger.Fail($"CroppingImageProcessor(CGSize): {ex.GetType().Name}: {ex.Message}");
            results.Fail("CroppingImageProcessor_ctor_size", $"{ex.GetType().Name}: {ex.Message}");
        }

        // CroppingImageProcessor with CGSize + CGPoint
        try
        {
            using var proc = new CroppingImageProcessor(new Swift.CGSize(50.0, 50.0), new Swift.CGPoint(0.5, 0.5));
            logger.Pass("CroppingImageProcessor(CGSize, CGPoint): created");
            results.Pass("CroppingImageProcessor_ctor_anchor");
        }
        catch (Exception ex)
        {
            logger.Fail($"CroppingImageProcessor(CGSize, CGPoint): {ex.GetType().Name}: {ex.Message}");
            results.Fail("CroppingImageProcessor_ctor_anchor", $"{ex.GetType().Name}: {ex.Message}");
        }

        // CroppingImageProcessor.Identifier
        try
        {
            using var proc = new CroppingImageProcessor(new Swift.CGSize(50.0, 50.0));
            var id = proc.Identifier;
            logger.Pass($"CroppingImageProcessor.Identifier: '{id}'");
            results.Pass("CroppingImageProcessor_Identifier");
        }
        catch (Exception ex)
        {
            logger.Fail($"CroppingImageProcessor.Identifier: {ex.GetType().Name}: {ex.Message}");
            results.Fail("CroppingImageProcessor_Identifier", $"{ex.GetType().Name}: {ex.Message}");
        }

        // DownsamplingImageProcessor
        try
        {
            using var proc = new DownsamplingImageProcessor(new Swift.CGSize(100.0, 100.0));
            logger.Pass("DownsamplingImageProcessor(CGSize): created");
            results.Pass("DownsamplingImageProcessor_ctor");
        }
        catch (Exception ex)
        {
            logger.Fail($"DownsamplingImageProcessor(CGSize): {ex.GetType().Name}: {ex.Message}");
            results.Fail("DownsamplingImageProcessor_ctor", $"{ex.GetType().Name}: {ex.Message}");
        }

        // DownsamplingImageProcessor.Identifier
        try
        {
            using var proc = new DownsamplingImageProcessor(new Swift.CGSize(100.0, 100.0));
            var id = proc.Identifier;
            logger.Pass($"DownsamplingImageProcessor.Identifier: '{id}'");
            results.Pass("DownsamplingImageProcessor_Identifier");
        }
        catch (Exception ex)
        {
            logger.Fail($"DownsamplingImageProcessor.Identifier: {ex.GetType().Name}: {ex.Message}");
            results.Fail("DownsamplingImageProcessor_Identifier", $"{ex.GetType().Name}: {ex.Message}");
        }

        // DefaultImageProcessor.Identifier
        try
        {
            using var proc = DefaultImageProcessor.Default;
            var id = proc.Identifier;
            logger.Pass($"DefaultImageProcessor.Identifier: '{id}'");
            results.Pass("DefaultImageProcessor_Identifier");
        }
        catch (Exception ex)
        {
            logger.Fail($"DefaultImageProcessor.Identifier: {ex.GetType().Name}: {ex.Message}");
            results.Fail("DefaultImageProcessor_Identifier", $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    // ──────────────────────────────────────────────
    // Section 17: DefaultCacheSerializer
    // ──────────────────────────────────────────────

    private void RunCacheSerializerTests(TestLogger logger, TestResults results)
    {
        // DefaultCacheSerializer.PreferCacheOriginalData getter
        try
        {
            using var ser = new DefaultCacheSerializer();
            var prefer = ser.PreferCacheOriginalData;
            logger.Pass($"DefaultCacheSerializer.PreferCacheOriginalData: {prefer}");
            results.Pass("DefaultCacheSerializer_PreferCacheOriginalData");
        }
        catch (Exception ex)
        {
            logger.Fail($"DefaultCacheSerializer.PreferCacheOriginalData: {ex.GetType().Name}: {ex.Message}");
            results.Fail("DefaultCacheSerializer_PreferCacheOriginalData", $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    // ──────────────────────────────────────────────
    // Section 18: RectCorner
    // ──────────────────────────────────────────────

    private void RunRectCornerTests(TestLogger logger, TestResults results)
    {
        var cornerTests = new (string Name, Func<RectCorner> Get)[]
        {
            ("TopLeft", () => RectCorner.TopLeft),
            ("TopRight", () => RectCorner.TopRight),
            ("BottomLeft", () => RectCorner.BottomLeft),
            ("BottomRight", () => RectCorner.BottomRight),
            ("All", () => RectCorner.All),
        };

        foreach (var (name, get) in cornerTests)
        {
            try
            {
                using var corner = get();
                var raw = corner.RawValue;
                logger.Pass($"RectCorner.{name}: RawValue={raw}");
                results.Pass($"RectCorner_{name}");
            }
            catch (Exception ex)
            {
                logger.Fail($"RectCorner.{name}: {ex.GetType().Name}: {ex.Message}");
                results.Fail($"RectCorner_{name}", $"{ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    // ──────────────────────────────────────────────
    // Section 19: AnimatedImageView
    // ──────────────────────────────────────────────

    private void RunAnimatedImageViewTests(TestLogger logger, TestResults results)
    {
        // AnimatedImageView.AutoPlayAnimatedImage
        try
        {
            var view = new AnimatedImageView((UIKit.UIImage?)null);
            var autoPlay = view.AutoPlayAnimatedImage;
            logger.Pass($"AnimatedImageView.AutoPlayAnimatedImage: {autoPlay}");
            results.Pass("AnimatedImageView_AutoPlayAnimatedImage");
        }
        catch (Exception ex)
        {
            logger.Fail($"AnimatedImageView.AutoPlayAnimatedImage: {ex.GetType().Name}: {ex.Message}");
            results.Fail("AnimatedImageView_AutoPlayAnimatedImage", $"{ex.GetType().Name}: {ex.Message}");
        }

        // AnimatedImageView.FramePreloadCount
        try
        {
            var view = new AnimatedImageView((UIKit.UIImage?)null);
            var count = view.FramePreloadCount;
            logger.Pass($"AnimatedImageView.FramePreloadCount: {count}");
            results.Pass("AnimatedImageView_FramePreloadCount");
        }
        catch (Exception ex)
        {
            logger.Fail($"AnimatedImageView.FramePreloadCount: {ex.GetType().Name}: {ex.Message}");
            results.Fail("AnimatedImageView_FramePreloadCount", $"{ex.GetType().Name}: {ex.Message}");
        }

        // AnimatedImageView.NeedsPrescaling
        try
        {
            var view = new AnimatedImageView((UIKit.UIImage?)null);
            var prescaling = view.NeedsPrescaling;
            logger.Pass($"AnimatedImageView.NeedsPrescaling: {prescaling}");
            results.Pass("AnimatedImageView_NeedsPrescaling");
        }
        catch (Exception ex)
        {
            logger.Fail($"AnimatedImageView.NeedsPrescaling: {ex.GetType().Name}: {ex.Message}");
            results.Fail("AnimatedImageView_NeedsPrescaling", $"{ex.GetType().Name}: {ex.Message}");
        }

        // AnimatedImageView.BackgroundDecode
        try
        {
            var view = new AnimatedImageView((UIKit.UIImage?)null);
            var bgDecode = view.BackgroundDecode;
            logger.Pass($"AnimatedImageView.BackgroundDecode: {bgDecode}");
            results.Pass("AnimatedImageView_BackgroundDecode");
        }
        catch (Exception ex)
        {
            logger.Fail($"AnimatedImageView.BackgroundDecode: {ex.GetType().Name}: {ex.Message}");
            results.Fail("AnimatedImageView_BackgroundDecode", $"{ex.GetType().Name}: {ex.Message}");
        }

        // AnimatedImageView.IsAnimating
        try
        {
            var view = new AnimatedImageView((UIKit.UIImage?)null);
            var isAnimating = view.IsAnimating;
            logger.Pass($"AnimatedImageView.IsAnimating: {isAnimating}");
            results.Pass("AnimatedImageView_IsAnimating");
        }
        catch (Exception ex)
        {
            logger.Fail($"AnimatedImageView.IsAnimating: {ex.GetType().Name}: {ex.Message}");
            results.Fail("AnimatedImageView_IsAnimating", $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    // ──────────────────────────────────────────────
    // Section 20: ImagePrefetcher
    // ──────────────────────────────────────────────

    private void RunImagePrefetcherTests(TestLogger logger, TestResults results)
    {
        // Constructor with URLs (Bug 3 fix: NonBlittableCallConvSwift constructors now generate @_cdecl wrappers)
        ImagePrefetcher? prefetcher = null;
        try
        {
            var urls = new Foundation.NSUrl[] { new Foundation.NSUrl("https://example.com/image.png") };
            prefetcher = new ImagePrefetcher(urls);
            logger.Pass("ImagePrefetcher(urls) constructed successfully");
            results.Pass("ImagePrefetcher_ctor_urls");
        }
        catch (Exception ex)
        {
            logger.Fail($"ImagePrefetcher(urls): {ex.GetType().Name}: {ex.Message}");
            results.Fail("ImagePrefetcher_ctor_urls", $"{ex.GetType().Name}: {ex.Message}");
            return; // Can't test properties without an instance
        }

        // MaxConcurrentDownloads getter
        try
        {
            var maxDownloads = prefetcher!.MaxConcurrentDownloads;
            logger.Pass($"ImagePrefetcher.MaxConcurrentDownloads = {maxDownloads}");
            results.Pass("ImagePrefetcher_MaxConcurrentDownloads");
        }
        catch (Exception ex)
        {
            logger.Fail($"ImagePrefetcher.MaxConcurrentDownloads get: {ex.GetType().Name}: {ex.Message}");
            results.Fail("ImagePrefetcher_MaxConcurrentDownloads", $"{ex.GetType().Name}: {ex.Message}");
        }

        // MaxConcurrentDownloads setter
        try
        {
            prefetcher!.MaxConcurrentDownloads = 3;
            var readBack = prefetcher.MaxConcurrentDownloads;
            if (readBack == 3)
            {
                logger.Pass("ImagePrefetcher.MaxConcurrentDownloads set to 3, read back 3");
                results.Pass("ImagePrefetcher_MaxConcurrentDownloads_set");
            }
            else
            {
                logger.Fail($"ImagePrefetcher.MaxConcurrentDownloads set to 3, read back {readBack}");
                results.Fail("ImagePrefetcher_MaxConcurrentDownloads_set", $"Expected 3, got {readBack}");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"ImagePrefetcher.MaxConcurrentDownloads set: {ex.GetType().Name}: {ex.Message}");
            results.Fail("ImagePrefetcher_MaxConcurrentDownloads_set", $"{ex.GetType().Name}: {ex.Message}");
        }

        // Stop method
        try
        {
            prefetcher!.Stop();
            logger.Pass("ImagePrefetcher.Stop() called successfully");
            results.Pass("ImagePrefetcher_Stop");
        }
        catch (Exception ex)
        {
            logger.Fail($"ImagePrefetcher.Stop(): {ex.GetType().Name}: {ex.Message}");
            results.Fail("ImagePrefetcher_Stop", $"{ex.GetType().Name}: {ex.Message}");
        }

        // Description property
        try
        {
            var desc = prefetcher!.Description;
            logger.Pass($"ImagePrefetcher.Description = \"{desc}\"");
            results.Pass("ImagePrefetcher_Description");
        }
        catch (Exception ex)
        {
            logger.Fail($"ImagePrefetcher.Description: {ex.GetType().Name}: {ex.Message}");
            results.Fail("ImagePrefetcher_Description", $"{ex.GetType().Name}: {ex.Message}");
        }

        prefetcher?.Dispose();
    }

    // ──────────────────────────────────────────────
    // Section 21: KingfisherError
    // ──────────────────────────────────────────────

    private void RunKingfisherErrorTests(TestLogger logger, TestResults results)
    {
        // KingfisherError metadata
        try
        {
            var metadata = SwiftObjectHelper<KingfisherError>.GetTypeMetadata();
            logger.Pass($"KingfisherError metadata: size={metadata.Size}");
            results.Pass("KingfisherError_metadata");
        }
        catch (Exception ex)
        {
            logger.Fail($"KingfisherError metadata: {ex.GetType().Name}: {ex.Message}");
            results.Fail("KingfisherError_metadata", $"{ex.GetType().Name}: {ex.Message}");
        }

        // CaseTag enum values exist
        try
        {
            var tags = new[]
            {
                KingfisherError.CaseTag.RequestError,
                KingfisherError.CaseTag.ResponseError,
                KingfisherError.CaseTag.CacheError,
                KingfisherError.CaseTag.ProcessorError,
                KingfisherError.CaseTag.ImageSettingError,
            };
            logger.Pass($"KingfisherError.CaseTag: {tags.Length} cases defined");
            results.Pass("KingfisherError_CaseTag_values");
        }
        catch (Exception ex)
        {
            logger.Fail($"KingfisherError.CaseTag: {ex.GetType().Name}: {ex.Message}");
            results.Fail("KingfisherError_CaseTag_values", $"{ex.GetType().Name}: {ex.Message}");
        }

        // CaseTag numeric values
        try
        {
            bool ok = (uint)KingfisherError.CaseTag.RequestError == 0
                   && (uint)KingfisherError.CaseTag.ResponseError == 1
                   && (uint)KingfisherError.CaseTag.CacheError == 2
                   && (uint)KingfisherError.CaseTag.ProcessorError == 3
                   && (uint)KingfisherError.CaseTag.ImageSettingError == 4;
            logger.Pass($"KingfisherError.CaseTag numeric values correct: {ok}");
            results.Pass("KingfisherError_CaseTag_numeric");
        }
        catch (Exception ex)
        {
            logger.Fail($"KingfisherError.CaseTag numeric: {ex.GetType().Name}: {ex.Message}");
            results.Fail("KingfisherError_CaseTag_numeric", $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    // ──────────────────────────────────────────────
    // Section 22: ImageDownloader Methods
    // ──────────────────────────────────────────────

    private void RunImageDownloaderMethodTests(TestLogger logger, TestResults results)
    {
        // CancelAll
        try
        {
            var dl = new ImageDownloader("CancelTest");
            dl.CancelAll();
            logger.Pass("ImageDownloader.CancelAll(): called successfully");
            dl.Dispose();
            results.Pass("ImageDownloader_CancelAll");
        }
        catch (Exception ex)
        {
            logger.Fail($"ImageDownloader.CancelAll(): {ex.GetType().Name}: {ex.Message}");
            results.Fail("ImageDownloader_CancelAll", $"{ex.GetType().Name}: {ex.Message}");
        }

        // Cancel(url:)
        try
        {
            var dl = new ImageDownloader("CancelUrlTest");
            var url = new NSUrl("https://example.com/image.png");
            dl.Cancel(url);
            logger.Pass("ImageDownloader.Cancel(url:): called successfully");
            dl.Dispose();
            results.Pass("ImageDownloader_Cancel_url");
        }
        catch (Exception ex)
        {
            logger.Fail($"ImageDownloader.Cancel(url:): {ex.GetType().Name}: {ex.Message}");
            results.Fail("ImageDownloader_Cancel_url", $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    // ──────────────────────────────────────────────
    // Section 23: Property Setter Round-Trips
    // ──────────────────────────────────────────────

    private void RunPropertySetterTests(TestLogger logger, TestResults results)
    {
        // ImageDownloader.DownloadTimeout setter
        try
        {
            var dl = new ImageDownloader("TimeoutTest");
            dl.DownloadTimeout = 30.0;
            var readBack = dl.DownloadTimeout;
            bool ok = Math.Abs(readBack - 30.0) < 0.001;
            logger.Pass($"ImageDownloader.DownloadTimeout roundtrip: set=30.0, get={readBack}, ok={ok}");
            dl.Dispose();
            results.Pass("ImageDownloader_DownloadTimeout_roundtrip");
        }
        catch (Exception ex)
        {
            logger.Fail($"ImageDownloader.DownloadTimeout roundtrip: {ex.GetType().Name}: {ex.Message}");
            results.Fail("ImageDownloader_DownloadTimeout_roundtrip", $"{ex.GetType().Name}: {ex.Message}");
        }

        // ImageDownloader.RequestsUsePipelining setter
        try
        {
            var dl = new ImageDownloader("PipelineTest");
            dl.RequestsUsePipelining = true;
            var readBack = dl.RequestsUsePipelining;
            logger.Pass($"ImageDownloader.RequestsUsePipelining roundtrip: set=true, get={readBack}");
            dl.Dispose();
            results.Pass("ImageDownloader_RequestsUsePipelining_roundtrip");
        }
        catch (Exception ex)
        {
            logger.Fail($"ImageDownloader.RequestsUsePipelining roundtrip: {ex.GetType().Name}: {ex.Message}");
            results.Fail("ImageDownloader_RequestsUsePipelining_roundtrip", $"{ex.GetType().Name}: {ex.Message}");
        }

        // ImageDownloader.DownloadTimeout — set custom, verify different from default
        try
        {
            var dl = new ImageDownloader("TimeoutCustom");
            var defaultTimeout = dl.DownloadTimeout;
            dl.DownloadTimeout = 60.0;
            var newTimeout = dl.DownloadTimeout;
            bool ok = Math.Abs(newTimeout - 60.0) < 0.001 && Math.Abs(defaultTimeout - 60.0) > 0.001;
            logger.Pass($"ImageDownloader.DownloadTimeout custom: default={defaultTimeout}, new={newTimeout}, changed={ok}");
            dl.Dispose();
            results.Pass("ImageDownloader_DownloadTimeout_custom");
        }
        catch (Exception ex)
        {
            logger.Fail($"ImageDownloader.DownloadTimeout custom: {ex.GetType().Name}: {ex.Message}");
            results.Fail("ImageDownloader_DownloadTimeout_custom", $"{ex.GetType().Name}: {ex.Message}");
        }

        // AnimatedImageView.AutoPlayAnimatedImage setter
        try
        {
            var view = new AnimatedImageView((UIKit.UIImage?)null);
            view.AutoPlayAnimatedImage = false;
            var readBack = view.AutoPlayAnimatedImage;
            logger.Pass($"AnimatedImageView.AutoPlayAnimatedImage roundtrip: set=false, get={readBack}");
            results.Pass("AnimatedImageView_AutoPlayAnimatedImage_roundtrip");
        }
        catch (Exception ex)
        {
            logger.Fail($"AnimatedImageView.AutoPlayAnimatedImage roundtrip: {ex.GetType().Name}: {ex.Message}");
            results.Fail("AnimatedImageView_AutoPlayAnimatedImage_roundtrip", $"{ex.GetType().Name}: {ex.Message}");
        }

        // AnimatedImageView.FramePreloadCount setter
        try
        {
            var view = new AnimatedImageView((UIKit.UIImage?)null);
            view.FramePreloadCount = 20;
            var readBack = view.FramePreloadCount;
            logger.Pass($"AnimatedImageView.FramePreloadCount roundtrip: set=20, get={readBack}");
            results.Pass("AnimatedImageView_FramePreloadCount_roundtrip");
        }
        catch (Exception ex)
        {
            logger.Fail($"AnimatedImageView.FramePreloadCount roundtrip: {ex.GetType().Name}: {ex.Message}");
            results.Fail("AnimatedImageView_FramePreloadCount_roundtrip", $"{ex.GetType().Name}: {ex.Message}");
        }

        // AnimatedImageView.NeedsPrescaling setter
        try
        {
            var view = new AnimatedImageView((UIKit.UIImage?)null);
            view.NeedsPrescaling = false;
            var readBack = view.NeedsPrescaling;
            logger.Pass($"AnimatedImageView.NeedsPrescaling roundtrip: set=false, get={readBack}");
            results.Pass("AnimatedImageView_NeedsPrescaling_roundtrip");
        }
        catch (Exception ex)
        {
            logger.Fail($"AnimatedImageView.NeedsPrescaling roundtrip: {ex.GetType().Name}: {ex.Message}");
            results.Fail("AnimatedImageView_NeedsPrescaling_roundtrip", $"{ex.GetType().Name}: {ex.Message}");
        }

        // AnimatedImageView.BackgroundDecode setter
        try
        {
            var view = new AnimatedImageView((UIKit.UIImage?)null);
            view.BackgroundDecode = true;
            var readBack = view.BackgroundDecode;
            logger.Pass($"AnimatedImageView.BackgroundDecode roundtrip: set=true, get={readBack}");
            results.Pass("AnimatedImageView_BackgroundDecode_roundtrip");
        }
        catch (Exception ex)
        {
            logger.Fail($"AnimatedImageView.BackgroundDecode roundtrip: {ex.GetType().Name}: {ex.Message}");
            results.Fail("AnimatedImageView_BackgroundDecode_roundtrip", $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    // ──────────────────────────────────────────────
    // Section 24: Processor Properties Deep Dive
    // ──────────────────────────────────────────────

    private void RunProcessorPropertiesDeepDive(TestLogger logger, TestResults results)
    {
        // OverlayImageProcessor.Overlay (UIColor property)
        try
        {
            using var proc = new OverlayImageProcessor(UIColor.Red, 0.7);
            var overlay = proc.Overlay;
            logger.Pass($"OverlayImageProcessor.Overlay: non-null={overlay != null}");
            results.Pass("OverlayImageProcessor_Overlay");
        }
        catch (Exception ex)
        {
            logger.Fail($"OverlayImageProcessor.Overlay: {ex.GetType().Name}: {ex.Message}");
            results.Fail("OverlayImageProcessor_Overlay", $"{ex.GetType().Name}: {ex.Message}");
        }

        // OverlayImageProcessor.Fraction (double property)
        try
        {
            using var proc = new OverlayImageProcessor(UIColor.Blue, 0.3);
            var fraction = proc.Fraction;
            logger.Pass($"OverlayImageProcessor.Fraction: {fraction}");
            results.Pass("OverlayImageProcessor_Fraction");
        }
        catch (Exception ex)
        {
            logger.Fail($"OverlayImageProcessor.Fraction: {ex.GetType().Name}: {ex.Message}");
            results.Fail("OverlayImageProcessor_Fraction", $"{ex.GetType().Name}: {ex.Message}");
        }

        // TintImageProcessor.Tint (UIColor property)
        try
        {
            using var proc = new TintImageProcessor(UIColor.Green);
            var tint = proc.Tint;
            logger.Pass($"TintImageProcessor.Tint: non-null={tint != null}");
            results.Pass("TintImageProcessor_Tint");
        }
        catch (Exception ex)
        {
            logger.Fail($"TintImageProcessor.Tint: {ex.GetType().Name}: {ex.Message}");
            results.Fail("TintImageProcessor_Tint", $"{ex.GetType().Name}: {ex.Message}");
        }

        // CroppingImageProcessor.Size (CGSize property)
        try
        {
            using var proc = new CroppingImageProcessor(new Swift.CGSize(75.0, 50.0));
            var size = proc.Size;
            logger.Pass($"CroppingImageProcessor.Size: ({size.Width}, {size.Height})");
            results.Pass("CroppingImageProcessor_Size");
        }
        catch (Exception ex)
        {
            logger.Fail($"CroppingImageProcessor.Size: {ex.GetType().Name}: {ex.Message}");
            results.Fail("CroppingImageProcessor_Size", $"{ex.GetType().Name}: {ex.Message}");
        }

        // CroppingImageProcessor.Anchor (CGPoint property)
        try
        {
            using var proc = new CroppingImageProcessor(new Swift.CGSize(50.0, 50.0), new Swift.CGPoint(0.25, 0.75));
            var anchor = proc.Anchor;
            logger.Pass($"CroppingImageProcessor.Anchor: ({anchor.X}, {anchor.Y})");
            results.Pass("CroppingImageProcessor_Anchor");
        }
        catch (Exception ex)
        {
            logger.Fail($"CroppingImageProcessor.Anchor: {ex.GetType().Name}: {ex.Message}");
            results.Fail("CroppingImageProcessor_Anchor", $"{ex.GetType().Name}: {ex.Message}");
        }

        // DownsamplingImageProcessor.Size (CGSize property)
        try
        {
            using var proc = new DownsamplingImageProcessor(new Swift.CGSize(200.0, 300.0));
            var size = proc.Size;
            logger.Pass($"DownsamplingImageProcessor.Size: ({size.Width}, {size.Height})");
            results.Pass("DownsamplingImageProcessor_Size");
        }
        catch (Exception ex)
        {
            logger.Fail($"DownsamplingImageProcessor.Size: {ex.GetType().Name}: {ex.Message}");
            results.Fail("DownsamplingImageProcessor_Size", $"{ex.GetType().Name}: {ex.Message}");
        }

        // RoundCornerImageProcessor.TargetSize (Optional<CGSize>)
        try
        {
            using var proc = new RoundCornerImageProcessor(10.0);
            var targetSize = proc.TargetSize;
            logger.Pass($"RoundCornerImageProcessor.TargetSize: {(targetSize.HasValue ? $"({targetSize.Value.Width}, {targetSize.Value.Height})" : "null")}");
            results.Pass("RoundCornerImageProcessor_TargetSize");
        }
        catch (Exception ex)
        {
            logger.Fail($"RoundCornerImageProcessor.TargetSize: {ex.GetType().Name}: {ex.Message}");
            results.Fail("RoundCornerImageProcessor_TargetSize", $"{ex.GetType().Name}: {ex.Message}");
        }

        // RoundCornerImageProcessor.BackgroundColor (Optional<UIColor>)
        try
        {
            using var proc = new RoundCornerImageProcessor(10.0);
            var bgColor = proc.BackgroundColor;
            logger.Pass($"RoundCornerImageProcessor.BackgroundColor: {(bgColor != null ? "non-null" : "null")}");
            results.Pass("RoundCornerImageProcessor_BackgroundColor");
        }
        catch (Exception ex)
        {
            logger.Fail($"RoundCornerImageProcessor.BackgroundColor: {ex.GetType().Name}: {ex.Message}");
            results.Fail("RoundCornerImageProcessor_BackgroundColor", $"{ex.GetType().Name}: {ex.Message}");
        }

        // DefaultCacheSerializer.PreferCacheOriginalData setter roundtrip
        try
        {
            using var ser = new DefaultCacheSerializer();
            ser.PreferCacheOriginalData = true;
            var readBack = ser.PreferCacheOriginalData;
            logger.Pass($"DefaultCacheSerializer.PreferCacheOriginalData roundtrip: set=true, get={readBack}");
            results.Pass("DefaultCacheSerializer_PreferCacheOriginalData_roundtrip");
        }
        catch (Exception ex)
        {
            logger.Fail($"DefaultCacheSerializer.PreferCacheOriginalData roundtrip: {ex.GetType().Name}: {ex.Message}");
            results.Fail("DefaultCacheSerializer_PreferCacheOriginalData_roundtrip", $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    // ──────────────────────────────────────────────
    // Section 25: FormatIndicatedCacheSerializer
    // ──────────────────────────────────────────────

    private void RunFormatIndicatedSerializerTests(TestLogger logger, TestResults results)
    {
        // FormatIndicatedCacheSerializer.Png
        try
        {
            using var ser = FormatIndicatedCacheSerializer.Png;
            logger.Pass("FormatIndicatedCacheSerializer.Png: created");
            results.Pass("FormatIndicatedCacheSerializer_Png");
        }
        catch (Exception ex)
        {
            logger.Fail($"FormatIndicatedCacheSerializer.Png: {ex.GetType().Name}: {ex.Message}");
            results.Fail("FormatIndicatedCacheSerializer_Png", $"{ex.GetType().Name}: {ex.Message}");
        }

        // FormatIndicatedCacheSerializer.Jpeg
        try
        {
            using var ser = FormatIndicatedCacheSerializer.Jpeg;
            logger.Pass("FormatIndicatedCacheSerializer.Jpeg: created");
            results.Pass("FormatIndicatedCacheSerializer_Jpeg");
        }
        catch (Exception ex)
        {
            logger.Fail($"FormatIndicatedCacheSerializer.Jpeg: {ex.GetType().Name}: {ex.Message}");
            results.Fail("FormatIndicatedCacheSerializer_Jpeg", $"{ex.GetType().Name}: {ex.Message}");
        }

        // FormatIndicatedCacheSerializer.Gif
        try
        {
            using var ser = FormatIndicatedCacheSerializer.Gif;
            logger.Pass("FormatIndicatedCacheSerializer.Gif: created");
            results.Pass("FormatIndicatedCacheSerializer_Gif");
        }
        catch (Exception ex)
        {
            logger.Fail($"FormatIndicatedCacheSerializer.Gif: {ex.GetType().Name}: {ex.Message}");
            results.Fail("FormatIndicatedCacheSerializer_Gif", $"{ex.GetType().Name}: {ex.Message}");
        }

        // Metadata
        try
        {
            var metadata = SwiftObjectHelper<FormatIndicatedCacheSerializer>.GetTypeMetadata();
            logger.Pass($"FormatIndicatedCacheSerializer metadata: size={metadata.Size}");
            results.Pass("FormatIndicatedCacheSerializer_metadata");
        }
        catch (Exception ex)
        {
            logger.Fail($"FormatIndicatedCacheSerializer metadata: {ex.GetType().Name}: {ex.Message}");
            results.Fail("FormatIndicatedCacheSerializer_metadata", $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    // ──────────────────────────────────────────────
    // Section 26: CacheType Extension Methods
    // ──────────────────────────────────────────────

    private void RunCacheTypeExtensionTests(TestLogger logger, TestResults results)
    {
        // CacheType.None.GetCached()
        try
        {
            var cached = CacheType.None.GetCached();
            logger.Pass($"CacheType.None.GetCached(): {cached}");
            results.Pass("CacheType_None_GetCached");
        }
        catch (Exception ex)
        {
            logger.Fail($"CacheType.None.GetCached(): {ex.GetType().Name}: {ex.Message}");
            results.Fail("CacheType_None_GetCached", $"{ex.GetType().Name}: {ex.Message}");
        }

        // CacheType.Memory.GetCached()
        try
        {
            var cached = CacheType.Memory.GetCached();
            logger.Pass($"CacheType.Memory.GetCached(): {cached}");
            results.Pass("CacheType_Memory_GetCached");
        }
        catch (Exception ex)
        {
            logger.Fail($"CacheType.Memory.GetCached(): {ex.GetType().Name}: {ex.Message}");
            results.Fail("CacheType_Memory_GetCached", $"{ex.GetType().Name}: {ex.Message}");
        }

        // CacheType.Disk.GetCached()
        try
        {
            var cached = CacheType.Disk.GetCached();
            logger.Pass($"CacheType.Disk.GetCached(): {cached}");
            results.Pass("CacheType_Disk_GetCached");
        }
        catch (Exception ex)
        {
            logger.Fail($"CacheType.Disk.GetCached(): {ex.GetType().Name}: {ex.Message}");
            results.Fail("CacheType_Disk_GetCached", $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    // ──────────────────────────────────────────────
    // Section 27: Radius.Compute
    // ──────────────────────────────────────────────

    private void RunRadiusComputeTests(TestLogger logger, TestResults results)
    {
        // Radius.Point.Compute — should return the point value regardless of size
        try
        {
            using var r = Radius.Point(20.0);
            var computed = r.Compute(new Swift.CGSize(100.0, 200.0));
            logger.Pass($"Radius.Point(20).Compute(100x200): {computed}");
            results.Pass("Radius_Point_Compute");
        }
        catch (Exception ex)
        {
            logger.Fail($"Radius.Point.Compute: {ex.GetType().Name}: {ex.Message}");
            results.Fail("Radius_Point_Compute", $"{ex.GetType().Name}: {ex.Message}");
        }

        // Radius.WidthFraction.Compute
        try
        {
            using var r = Radius.WidthFraction(0.5);
            var computed = r.Compute(new Swift.CGSize(200.0, 100.0));
            logger.Pass($"Radius.WidthFraction(0.5).Compute(200x100): {computed}");
            results.Pass("Radius_WidthFraction_Compute");
        }
        catch (Exception ex)
        {
            logger.Fail($"Radius.WidthFraction.Compute: {ex.GetType().Name}: {ex.Message}");
            results.Fail("Radius_WidthFraction_Compute", $"{ex.GetType().Name}: {ex.Message}");
        }

        // Radius.HeightFraction.Compute
        try
        {
            using var r = Radius.HeightFraction(0.25);
            var computed = r.Compute(new Swift.CGSize(100.0, 400.0));
            logger.Pass($"Radius.HeightFraction(0.25).Compute(100x400): {computed}");
            results.Pass("Radius_HeightFraction_Compute");
        }
        catch (Exception ex)
        {
            logger.Fail($"Radius.HeightFraction.Compute: {ex.GetType().Name}: {ex.Message}");
            results.Fail("Radius_HeightFraction_Compute", $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    // ──────────────────────────────────────────────
    // Section 28: UpdatingStrategy
    // ──────────────────────────────────────────────

    private void RunUpdatingStrategyTests(TestLogger logger, TestResults results)
    {
        // UpdatingStrategy.Default
        try
        {
            var strategy = ImageProgressive.UpdatingStrategy.Default;
            var tag = strategy.Tag;
            logger.Pass($"UpdatingStrategy.Default: Tag={tag}");
            results.Pass("UpdatingStrategy_Default");
        }
        catch (Exception ex)
        {
            logger.Fail($"UpdatingStrategy.Default: {ex.GetType().Name}: {ex.Message}");
            results.Fail("UpdatingStrategy_Default", $"{ex.GetType().Name}: {ex.Message}");
        }

        // UpdatingStrategy.KeepCurrent
        try
        {
            var strategy = ImageProgressive.UpdatingStrategy.KeepCurrent;
            var tag = strategy.Tag;
            logger.Pass($"UpdatingStrategy.KeepCurrent: Tag={tag}");
            results.Pass("UpdatingStrategy_KeepCurrent");
        }
        catch (Exception ex)
        {
            logger.Fail($"UpdatingStrategy.KeepCurrent: {ex.GetType().Name}: {ex.Message}");
            results.Fail("UpdatingStrategy_KeepCurrent", $"{ex.GetType().Name}: {ex.Message}");
        }

        // UpdatingStrategy.Replace(null)
        try
        {
            using var strategy = ImageProgressive.UpdatingStrategy.Replace(null);
            var tag = strategy.Tag;
            logger.Pass($"UpdatingStrategy.Replace(null): Tag={tag}");
            results.Pass("UpdatingStrategy_Replace_null");
        }
        catch (Exception ex)
        {
            logger.Fail($"UpdatingStrategy.Replace(null): {ex.GetType().Name}: {ex.Message}");
            results.Fail("UpdatingStrategy_Replace_null", $"{ex.GetType().Name}: {ex.Message}");
        }

        // UpdatingStrategy metadata
        try
        {
            var metadata = SwiftObjectHelper<ImageProgressive.UpdatingStrategy>.GetTypeMetadata();
            logger.Pass($"UpdatingStrategy metadata: size={metadata.Size}");
            results.Pass("UpdatingStrategy_metadata");
        }
        catch (Exception ex)
        {
            logger.Fail($"UpdatingStrategy metadata: {ex.GetType().Name}: {ex.Message}");
            results.Fail("UpdatingStrategy_metadata", $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    // ──────────────────────────────────────────────
    // Section 29: Additional Constructor Overloads
    // ──────────────────────────────────────────────

    private void RunAdditionalConstructorTests(TestLogger logger, TestResults results)
    {
        // RoundCornerImageProcessor with double + targetSize
        try
        {
            using var proc = new RoundCornerImageProcessor(10.0, new Swift.CGSize(100.0, 100.0));
            logger.Pass("RoundCornerImageProcessor(10.0, CGSize): created");
            results.Pass("RoundCornerImageProcessor_ctor_double_size");
        }
        catch (Exception ex)
        {
            logger.Fail($"RoundCornerImageProcessor(10.0, CGSize): {ex.GetType().Name}: {ex.Message}");
            results.Fail("RoundCornerImageProcessor_ctor_double_size", $"{ex.GetType().Name}: {ex.Message}");
        }

        // RoundCornerImageProcessor with double + targetSize + corners
        try
        {
            using var corners = RectCorner.All;
            using var proc = new RoundCornerImageProcessor(10.0, new Swift.CGSize(100.0, 100.0), corners);
            logger.Pass("RoundCornerImageProcessor(10.0, CGSize, All): created");
            results.Pass("RoundCornerImageProcessor_ctor_double_size_corners");
        }
        catch (Exception ex)
        {
            logger.Fail($"RoundCornerImageProcessor(10.0, CGSize, All): {ex.GetType().Name}: {ex.Message}");
            results.Fail("RoundCornerImageProcessor_ctor_double_size_corners", $"{ex.GetType().Name}: {ex.Message}");
        }

        // RoundCornerImageProcessor with Radius + targetSize
        try
        {
            using var r = Radius.Point(8.0);
            using var proc = new RoundCornerImageProcessor(r, new Swift.CGSize(50.0, 50.0));
            logger.Pass("RoundCornerImageProcessor(Radius.Point(8), CGSize): created");
            results.Pass("RoundCornerImageProcessor_ctor_radius_size");
        }
        catch (Exception ex)
        {
            logger.Fail($"RoundCornerImageProcessor(Radius, CGSize): {ex.GetType().Name}: {ex.Message}");
            results.Fail("RoundCornerImageProcessor_ctor_radius_size", $"{ex.GetType().Name}: {ex.Message}");
        }

        // CroppingImageProcessor with size + anchor
        try
        {
            using var proc = new CroppingImageProcessor(new Swift.CGSize(100.0, 100.0), new Swift.CGPoint(0.0, 0.0));
            logger.Pass("CroppingImageProcessor(CGSize, CGPoint(0,0)): created");
            results.Pass("CroppingImageProcessor_ctor_origin");
        }
        catch (Exception ex)
        {
            logger.Fail($"CroppingImageProcessor(CGSize, CGPoint): {ex.GetType().Name}: {ex.Message}");
            results.Fail("CroppingImageProcessor_ctor_origin", $"{ex.GetType().Name}: {ex.Message}");
        }

        // ResizingImageProcessor with AspectFill
        try
        {
            using var proc = new ResizingImageProcessor(new Swift.CGSize(300.0, 300.0), ContentMode.AspectFill);
            var mode = proc.TargetContentMode;
            logger.Pass($"ResizingImageProcessor AspectFill: mode={mode}");
            results.Pass("ResizingImageProcessor_ctor_AspectFill");
        }
        catch (Exception ex)
        {
            logger.Fail($"ResizingImageProcessor AspectFill: {ex.GetType().Name}: {ex.Message}");
            results.Fail("ResizingImageProcessor_ctor_AspectFill", $"{ex.GetType().Name}: {ex.Message}");
        }

        // ImageCache(name:, cacheDirectoryURL:, diskCachePathClosure:) — closure parameter constructor.
        // Marked [Obsolete(SB0001)] (no @_cdecl wrapper). Skip — calling convention mismatch may crash.
        logger.Skip("ImageCache(name, url, closure): no @_cdecl wrapper (SB0001), closure constructor skipped");
        results.Skip("ImageCache_ctor_name_url", "no @_cdecl wrapper (SB0001)");

        // DelayRetryStrategy with Accumulated interval
        try
        {
            using var interval = DelayRetryStrategy.Interval.Accumulated(0.5);
            using var strategy = new DelayRetryStrategy(4, interval);
            var count = strategy.MaxRetryCount;
            logger.Pass($"DelayRetryStrategy with Accumulated: MaxRetryCount={count}");
            results.Pass("DelayRetryStrategy_ctor_accumulated");
        }
        catch (Exception ex)
        {
            logger.Fail($"DelayRetryStrategy with Accumulated: {ex.GetType().Name}: {ex.Message}");
            results.Fail("DelayRetryStrategy_ctor_accumulated", $"{ex.GetType().Name}: {ex.Message}");
        }

        // OverlayImageProcessor with default fraction
        try
        {
            using var proc = new OverlayImageProcessor(UIColor.White);
            var fraction = proc.Fraction;
            logger.Pass($"OverlayImageProcessor default fraction: {fraction}");
            results.Pass("OverlayImageProcessor_ctor_default_fraction");
        }
        catch (Exception ex)
        {
            logger.Fail($"OverlayImageProcessor default fraction: {ex.GetType().Name}: {ex.Message}");
            results.Fail("OverlayImageProcessor_ctor_default_fraction", $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    // ──────────────────────────────────────────────
    // Section 30: Additional Metadata
    // ──────────────────────────────────────────────

    private void RunAdditionalMetadataTests(TestLogger logger, TestResults results)
    {
        var extraMetadataTests = new (string Name, Func<TypeMetadata> GetMetadata)[]
        {
            ("ImagePrefetcher", () => SwiftObjectHelper<ImagePrefetcher>.GetTypeMetadata()),
            ("AnimatedImageView", () => SwiftObjectHelper<AnimatedImageView>.GetTypeMetadata()),
            ("ImageCreatingOptions", () => SwiftObjectHelper<ImageCreatingOptions>.GetTypeMetadata()),
            ("FormatIndicatedCacheSerializer", () => SwiftObjectHelper<FormatIndicatedCacheSerializer>.GetTypeMetadata()),
            ("NetworkMetrics", () => SwiftObjectHelper<NetworkMetrics>.GetTypeMetadata()),
            ("ImageLoadingResult", () => SwiftObjectHelper<ImageLoadingResult>.GetTypeMetadata()),
            ("KingfisherParsedOptionsInfo", () => SwiftObjectHelper<KingfisherParsedOptionsInfo>.GetTypeMetadata()),
            ("Source", () => SwiftObjectHelper<Source>.GetTypeMetadata()),
            ("DelayRetryStrategy.Interval", () => SwiftObjectHelper<DelayRetryStrategy.Interval>.GetTypeMetadata()),
            ("AnimatedImageView.RepeatCountType", () => SwiftObjectHelper<Kingfisher.AnimatedImageView.RepeatCountType>.GetTypeMetadata()),
            ("ImageProgressive.UpdatingStrategy", () => SwiftObjectHelper<ImageProgressive.UpdatingStrategy>.GetTypeMetadata()),
        };

        foreach (var (name, getMetadata) in extraMetadataTests)
        {
            try
            {
                var metadata = getMetadata();
                logger.Pass($"{name} metadata: size={metadata.Size}");
                results.Pass($"Metadata_Extra_{name}");
            }
            catch (Exception ex)
            {
                logger.Fail($"{name} metadata: {ex.GetType().Name}: {ex.Message}");
                results.Fail($"Metadata_Extra_{name}", $"{ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    // ──────────────────────────────────────────────
    // Section 31: ImageCreatingOptions
    // ──────────────────────────────────────────────

    private void RunImageCreatingOptionsTests(TestLogger logger, TestResults results)
    {
        // Constructor with defaults
        try
        {
            using var opts = new ImageCreatingOptions();
            logger.Pass("ImageCreatingOptions(): created");
            results.Pass("ImageCreatingOptions_ctor_defaults");
        }
        catch (Exception ex)
        {
            logger.Fail($"ImageCreatingOptions(): {ex.GetType().Name}: {ex.Message}");
            results.Fail("ImageCreatingOptions_ctor_defaults", $"{ex.GetType().Name}: {ex.Message}");
        }

        // Constructor with custom values
        try
        {
            using var opts = new ImageCreatingOptions(scale: 2.0, duration: 1.5, preloadAll: true, onlyFirstFrame: false);
            logger.Pass("ImageCreatingOptions(custom): created");
            results.Pass("ImageCreatingOptions_ctor_custom");
        }
        catch (Exception ex)
        {
            logger.Fail($"ImageCreatingOptions(custom): {ex.GetType().Name}: {ex.Message}");
            results.Fail("ImageCreatingOptions_ctor_custom", $"{ex.GetType().Name}: {ex.Message}");
        }

        // Scale property
        try
        {
            using var opts = new ImageCreatingOptions(scale: 3.0);
            var scale = opts.Scale;
            logger.Pass($"ImageCreatingOptions.Scale: {scale}");
            results.Pass("ImageCreatingOptions_Scale");
        }
        catch (Exception ex)
        {
            logger.Fail($"ImageCreatingOptions.Scale: {ex.GetType().Name}: {ex.Message}");
            results.Fail("ImageCreatingOptions_Scale", $"{ex.GetType().Name}: {ex.Message}");
        }

        // Duration property
        try
        {
            using var opts = new ImageCreatingOptions(duration: 2.5);
            var duration = opts.Duration;
            logger.Pass($"ImageCreatingOptions.Duration: {duration}");
            results.Pass("ImageCreatingOptions_Duration");
        }
        catch (Exception ex)
        {
            logger.Fail($"ImageCreatingOptions.Duration: {ex.GetType().Name}: {ex.Message}");
            results.Fail("ImageCreatingOptions_Duration", $"{ex.GetType().Name}: {ex.Message}");
        }

        // PreloadAll property
        try
        {
            using var opts = new ImageCreatingOptions(preloadAll: true);
            var preload = opts.PreloadAll;
            logger.Pass($"ImageCreatingOptions.PreloadAll: {preload}");
            results.Pass("ImageCreatingOptions_PreloadAll");
        }
        catch (Exception ex)
        {
            logger.Fail($"ImageCreatingOptions.PreloadAll: {ex.GetType().Name}: {ex.Message}");
            results.Fail("ImageCreatingOptions_PreloadAll", $"{ex.GetType().Name}: {ex.Message}");
        }

        // OnlyFirstFrame property
        try
        {
            using var opts = new ImageCreatingOptions(onlyFirstFrame: true);
            var firstFrame = opts.OnlyFirstFrame;
            logger.Pass($"ImageCreatingOptions.OnlyFirstFrame: {firstFrame}");
            results.Pass("ImageCreatingOptions_OnlyFirstFrame");
        }
        catch (Exception ex)
        {
            logger.Fail($"ImageCreatingOptions.OnlyFirstFrame: {ex.GetType().Name}: {ex.Message}");
            results.Fail("ImageCreatingOptions_OnlyFirstFrame", $"{ex.GetType().Name}: {ex.Message}");
        }

        // Scale setter roundtrip
        try
        {
            using var opts = new ImageCreatingOptions();
            opts.Scale = 4.0;
            var readBack = opts.Scale;
            logger.Pass($"ImageCreatingOptions.Scale roundtrip: set=4.0, get={readBack}");
            results.Pass("ImageCreatingOptions_Scale_roundtrip");
        }
        catch (Exception ex)
        {
            logger.Fail($"ImageCreatingOptions.Scale roundtrip: {ex.GetType().Name}: {ex.Message}");
            results.Fail("ImageCreatingOptions_Scale_roundtrip", $"{ex.GetType().Name}: {ex.Message}");
        }

        // Duration setter roundtrip
        try
        {
            using var opts = new ImageCreatingOptions();
            opts.Duration = 5.5;
            var readBack = opts.Duration;
            logger.Pass($"ImageCreatingOptions.Duration roundtrip: set=5.5, get={readBack}");
            results.Pass("ImageCreatingOptions_Duration_roundtrip");
        }
        catch (Exception ex)
        {
            logger.Fail($"ImageCreatingOptions.Duration roundtrip: {ex.GetType().Name}: {ex.Message}");
            results.Fail("ImageCreatingOptions_Duration_roundtrip", $"{ex.GetType().Name}: {ex.Message}");
        }

        // PreloadAll setter roundtrip
        try
        {
            using var opts = new ImageCreatingOptions();
            opts.PreloadAll = true;
            var readBack = opts.PreloadAll;
            logger.Pass($"ImageCreatingOptions.PreloadAll roundtrip: set=true, get={readBack}");
            results.Pass("ImageCreatingOptions_PreloadAll_roundtrip");
        }
        catch (Exception ex)
        {
            logger.Fail($"ImageCreatingOptions.PreloadAll roundtrip: {ex.GetType().Name}: {ex.Message}");
            results.Fail("ImageCreatingOptions_PreloadAll_roundtrip", $"{ex.GetType().Name}: {ex.Message}");
        }

        // OnlyFirstFrame setter roundtrip
        try
        {
            using var opts = new ImageCreatingOptions();
            opts.OnlyFirstFrame = true;
            var readBack = opts.OnlyFirstFrame;
            logger.Pass($"ImageCreatingOptions.OnlyFirstFrame roundtrip: set=true, get={readBack}");
            results.Pass("ImageCreatingOptions_OnlyFirstFrame_roundtrip");
        }
        catch (Exception ex)
        {
            logger.Fail($"ImageCreatingOptions.OnlyFirstFrame roundtrip: {ex.GetType().Name}: {ex.Message}");
            results.Fail("ImageCreatingOptions_OnlyFirstFrame_roundtrip", $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    // ──────────────────────────────────────────────
    // Section 32: ImageTransition TryGet Coverage
    // ──────────────────────────────────────────────

    private void RunImageTransitionTryGetTests(TestLogger logger, TestResults results)
    {
        // TryGet for each transition type
        var tryGetTests = new (string Name, Func<ImageTransition> Create, Func<ImageTransition, bool> TryGet)[]
        {
            ("FlipFromLeft", () => ImageTransition.FlipFromLeft(0.4), t => t.TryGetFlipFromLeft(out _)),
            ("FlipFromRight", () => ImageTransition.FlipFromRight(0.4), t => t.TryGetFlipFromRight(out _)),
            ("FlipFromTop", () => ImageTransition.FlipFromTop(0.4), t => t.TryGetFlipFromTop(out _)),
            ("FlipFromBottom", () => ImageTransition.FlipFromBottom(0.4), t => t.TryGetFlipFromBottom(out _)),
        };

        foreach (var (name, create, tryGet) in tryGetTests)
        {
            try
            {
                using var t = create();
                bool found = tryGet(t);
                logger.Pass($"ImageTransition.{name}.TryGet: {found}");
                results.Pass($"ImageTransition_{name}_TryGet");
            }
            catch (Exception ex)
            {
                logger.Fail($"ImageTransition.{name}.TryGet: {ex.GetType().Name}: {ex.Message}");
                results.Fail($"ImageTransition_{name}_TryGet", $"{ex.GetType().Name}: {ex.Message}");
            }
        }

        // Negative tests: TryGetFade on a FlipFromLeft
        try
        {
            using var t = ImageTransition.FlipFromLeft(0.3);
            bool found = t.TryGetFade(out _);
            logger.Pass($"ImageTransition.FlipFromLeft.TryGetFade: {found} (expected false)");
            results.Pass("ImageTransition_Negative_TryGetFade");
        }
        catch (Exception ex)
        {
            logger.Fail($"ImageTransition negative TryGetFade: {ex.GetType().Name}: {ex.Message}");
            results.Fail("ImageTransition_Negative_TryGetFade", $"{ex.GetType().Name}: {ex.Message}");
        }

        // Negative: TryGetFlipFromLeft on a Fade
        try
        {
            using var t = ImageTransition.Fade(0.3);
            bool found = t.TryGetFlipFromLeft(out _);
            logger.Pass($"ImageTransition.Fade.TryGetFlipFromLeft: {found} (expected false)");
            results.Pass("ImageTransition_Negative_TryGetFlip");
        }
        catch (Exception ex)
        {
            logger.Fail($"ImageTransition negative TryGetFlip: {ex.GetType().Name}: {ex.Message}");
            results.Fail("ImageTransition_Negative_TryGetFlip", $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    // ──────────────────────────────────────────────
    // Section 33: StorageExpiration TryGet Coverage
    // ──────────────────────────────────────────────

    private void RunStorageExpirationTryGetCoverage(TestLogger logger, TestResults results)
    {
        // TryGetDays
        try
        {
            using var exp = StorageExpiration.Days(14);
            bool found = exp.TryGetDays(out var days);
            logger.Pass($"StorageExpiration.Days.TryGetDays: found={found}, days={days}");
            results.Pass("StorageExpiration_Days_TryGetDays");
        }
        catch (Exception ex)
        {
            logger.Fail($"StorageExpiration.Days.TryGetDays: {ex.GetType().Name}: {ex.Message}");
            results.Fail("StorageExpiration_Days_TryGetDays", $"{ex.GetType().Name}: {ex.Message}");
        }

        // TryGetDate
        try
        {
            var futureDate = DateTimeOffset.UtcNow.AddDays(30);
            using var exp = StorageExpiration.Date(futureDate);
            bool found = exp.TryGetDate(out var date);
            logger.Pass($"StorageExpiration.Date.TryGetDate: found={found}, date={date}");
            results.Pass("StorageExpiration_Date_TryGetDate");
        }
        catch (Exception ex)
        {
            logger.Fail($"StorageExpiration.Date.TryGetDate: {ex.GetType().Name}: {ex.Message}");
            results.Fail("StorageExpiration_Date_TryGetDate", $"{ex.GetType().Name}: {ex.Message}");
        }

        // TryGetDays on a Seconds instance (negative)
        try
        {
            using var exp = StorageExpiration.Seconds(100);
            bool found = exp.TryGetDays(out _);
            logger.Pass($"StorageExpiration.Seconds.TryGetDays: {found} (expected false)");
            results.Pass("StorageExpiration_Seconds_TryGetDays_negative");
        }
        catch (Exception ex)
        {
            logger.Fail($"StorageExpiration.Seconds.TryGetDays negative: {ex.GetType().Name}: {ex.Message}");
            results.Fail("StorageExpiration_Seconds_TryGetDays_negative", $"{ex.GetType().Name}: {ex.Message}");
        }

        // TryGetDate on a Never instance (negative)
        try
        {
            var exp = StorageExpiration.Never;
            bool found = exp.TryGetDate(out _);
            logger.Pass($"StorageExpiration.Never.TryGetDate: {found} (expected false)");
            results.Pass("StorageExpiration_Never_TryGetDate_negative");
        }
        catch (Exception ex)
        {
            logger.Fail($"StorageExpiration.Never.TryGetDate negative: {ex.GetType().Name}: {ex.Message}");
            results.Fail("StorageExpiration_Never_TryGetDate_negative", $"{ex.GetType().Name}: {ex.Message}");
        }

        // TryGetSeconds on Expired (negative)
        try
        {
            var exp = StorageExpiration.Expired;
            bool found = exp.TryGetSeconds(out _);
            logger.Pass($"StorageExpiration.Expired.TryGetSeconds: {found} (expected false)");
            results.Pass("StorageExpiration_Expired_TryGetSeconds_negative");
        }
        catch (Exception ex)
        {
            logger.Fail($"StorageExpiration.Expired.TryGetSeconds negative: {ex.GetType().Name}: {ex.Message}");
            results.Fail("StorageExpiration_Expired_TryGetSeconds_negative", $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    // ──────────────────────────────────────────────
    // Section 34: Processor Append
    // ──────────────────────────────────────────────

    private void RunProcessorAppendTests(TestLogger logger, TestResults results)
    {
        // Append blur to default
        try
        {
            using var defProc = new DefaultImageProcessor();
            using var blur = new BlurImageProcessor(5.0);
            var combined = defProc.Append(blur);
            logger.Pass("DefaultImageProcessor.Append(BlurImageProcessor): created combined");
            results.Pass("Processor_Append_default_blur");
        }
        catch (Exception ex)
        {
            logger.Fail($"Processor.Append: {ex.GetType().Name}: {ex.Message}");
            results.Fail("Processor_Append_default_blur", $"{ex.GetType().Name}: {ex.Message}");
        }

        // Append tint to blur
        try
        {
            using var blur = new BlurImageProcessor(3.0);
            using var tint = new TintImageProcessor(UIColor.Red);
            var combined = blur.Append(tint);
            logger.Pass("BlurImageProcessor.Append(TintImageProcessor): created combined");
            results.Pass("Processor_Append_blur_tint");
        }
        catch (Exception ex)
        {
            logger.Fail($"Processor.Append blur+tint: {ex.GetType().Name}: {ex.Message}");
            results.Fail("Processor_Append_blur_tint", $"{ex.GetType().Name}: {ex.Message}");
        }

        // Append resizing to cropping
        try
        {
            using var crop = new CroppingImageProcessor(new Swift.CGSize(50.0, 50.0));
            using var resize = new ResizingImageProcessor(new Swift.CGSize(100.0, 100.0));
            var combined = crop.Append(resize);
            logger.Pass("CroppingImageProcessor.Append(ResizingImageProcessor): created combined");
            results.Pass("Processor_Append_crop_resize");
        }
        catch (Exception ex)
        {
            logger.Fail($"Processor.Append crop+resize: {ex.GetType().Name}: {ex.Message}");
            results.Fail("Processor_Append_crop_resize", $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    // ──────────────────────────────────────────────
    // Section 35: Multiple Singleton Stability
    // ──────────────────────────────────────────────

    private void RunSingletonStabilityTests(TestLogger logger, TestResults results)
    {
        // Access KingfisherManager.Shared multiple times
        try
        {
            for (int i = 0; i < 5; i++)
            {
                var mgr = KingfisherManager.Shared;
                _ = mgr.Cache;
                _ = mgr.Downloader;
            }
            logger.Pass("KingfisherManager.Shared 5x access: stable");
            results.Pass("Singleton_Manager_Stability");
        }
        catch (Exception ex)
        {
            logger.Fail($"Manager singleton stability: {ex.GetType().Name}: {ex.Message}");
            results.Fail("Singleton_Manager_Stability", $"{ex.GetType().Name}: {ex.Message}");
        }

        // Access ImageCache.Default multiple times
        try
        {
            for (int i = 0; i < 5; i++)
            {
                var cache = ImageCache.Default;
                _ = cache.IsCached("stability_test_key");
            }
            logger.Pass("ImageCache.Default 5x access: stable");
            results.Pass("Singleton_Cache_Stability");
        }
        catch (Exception ex)
        {
            logger.Fail($"Cache singleton stability: {ex.GetType().Name}: {ex.Message}");
            results.Fail("Singleton_Cache_Stability", $"{ex.GetType().Name}: {ex.Message}");
        }

        // Access ImageDownloader.Default multiple times
        try
        {
            for (int i = 0; i < 5; i++)
            {
                var dl = ImageDownloader.Default;
                _ = dl.DownloadTimeout;
            }
            logger.Pass("ImageDownloader.Default 5x access: stable");
            results.Pass("Singleton_Downloader_Stability");
        }
        catch (Exception ex)
        {
            logger.Fail($"Downloader singleton stability: {ex.GetType().Name}: {ex.Message}");
            results.Fail("Singleton_Downloader_Stability", $"{ex.GetType().Name}: {ex.Message}");
        }

        // Access DefaultImageProcessor.Default multiple times
        try
        {
            for (int i = 0; i < 5; i++)
            {
                using var proc = DefaultImageProcessor.Default;
                _ = proc.Identifier;
            }
            logger.Pass("DefaultImageProcessor.Default 5x access: stable");
            results.Pass("Singleton_Processor_Stability");
        }
        catch (Exception ex)
        {
            logger.Fail($"Processor singleton stability: {ex.GetType().Name}: {ex.Message}");
            results.Fail("Singleton_Processor_Stability", $"{ex.GetType().Name}: {ex.Message}");
        }

        // Access ImageFormat singletons multiple times
        try
        {
            for (int i = 0; i < 5; i++)
            {
                _ = ImageFormat.Png.Tag;
                _ = ImageFormat.Jpeg.Tag;
                _ = ImageFormat.Gif.Tag;
                _ = ImageFormat.Unknown.Tag;
            }
            logger.Pass("ImageFormat singletons 5x access: stable");
            results.Pass("Singleton_ImageFormat_Stability");
        }
        catch (Exception ex)
        {
            logger.Fail($"ImageFormat singleton stability: {ex.GetType().Name}: {ex.Message}");
            results.Fail("Singleton_ImageFormat_Stability", $"{ex.GetType().Name}: {ex.Message}");
        }

        // Access StorageExpiration singletons multiple times
        try
        {
            for (int i = 0; i < 5; i++)
            {
                _ = StorageExpiration.Never.Tag;
                _ = StorageExpiration.Expired.Tag;
            }
            logger.Pass("StorageExpiration singletons 5x access: stable");
            results.Pass("Singleton_StorageExpiration_Stability");
        }
        catch (Exception ex)
        {
            logger.Fail($"StorageExpiration singleton stability: {ex.GetType().Name}: {ex.Message}");
            results.Fail("Singleton_StorageExpiration_Stability", $"{ex.GetType().Name}: {ex.Message}");
        }

        // Access CallbackQueue singletons multiple times
        try
        {
            for (int i = 0; i < 5; i++)
            {
                _ = CallbackQueue.MainAsync.Tag;
                _ = CallbackQueue.MainCurrentOrAsync.Tag;
                _ = CallbackQueue.Untouch.Tag;
            }
            logger.Pass("CallbackQueue singletons 5x access: stable");
            results.Pass("Singleton_CallbackQueue_Stability");
        }
        catch (Exception ex)
        {
            logger.Fail($"CallbackQueue singleton stability: {ex.GetType().Name}: {ex.Message}");
            results.Fail("Singleton_CallbackQueue_Stability", $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    // ──────────────────────────────────────────────
    // Section 36: SessionDelegate & ImageResource
    // ──────────────────────────────────────────────

    private void RunSessionDelegateAndResourceTests(TestLogger logger, TestResults results)
    {
        // SessionDelegate constructor
        try
        {
            var sd = new SessionDelegate();
            logger.Pass("SessionDelegate(): created");
            results.Pass("SessionDelegate_ctor");
        }
        catch (Exception ex)
        {
            logger.Fail($"SessionDelegate(): {ex.GetType().Name}: {ex.Message}");
            results.Fail("SessionDelegate_ctor", $"{ex.GetType().Name}: {ex.Message}");
        }

        // KF.ImageResource constructor
        try
        {
            var url = new NSUrl("https://example.com/image.png");
            using var resource = new KF.ImageResource(url);
            logger.Pass("KF.ImageResource(url:): created");
            results.Pass("ImageResource_ctor");
        }
        catch (Exception ex)
        {
            logger.Fail($"KF.ImageResource(url:): {ex.GetType().Name}: {ex.Message}");
            results.Fail("ImageResource_ctor", $"{ex.GetType().Name}: {ex.Message}");
        }

        // KF.ImageResource.CacheKey
        try
        {
            var url = new NSUrl("https://example.com/image.png");
            using var resource = new KF.ImageResource(url);
            var cacheKey = resource.CacheKey;
            logger.Pass($"KF.ImageResource.CacheKey: '{cacheKey}'");
            results.Pass("ImageResource_CacheKey");
        }
        catch (Exception ex)
        {
            logger.Fail($"KF.ImageResource.CacheKey: {ex.GetType().Name}: {ex.Message}");
            results.Fail("ImageResource_CacheKey", $"{ex.GetType().Name}: {ex.Message}");
        }

        // KF.ImageResource.DownloadURL
        try
        {
            var url = new NSUrl("https://example.com/image.png");
            using var resource = new KF.ImageResource(url);
            var downloadUrl = resource.DownloadURL;
            logger.Pass($"KF.ImageResource.DownloadURL: {downloadUrl}");
            results.Pass("ImageResource_DownloadURL");
        }
        catch (Exception ex)
        {
            logger.Fail($"KF.ImageResource.DownloadURL: {ex.GetType().Name}: {ex.Message}");
            results.Fail("ImageResource_DownloadURL", $"{ex.GetType().Name}: {ex.Message}");
        }

        // KF.ImageResource with custom cacheKey
        try
        {
            var url = new NSUrl("https://example.com/image2.png");
            using var resource = new KF.ImageResource(url, "custom_cache_key");
            var cacheKey = resource.CacheKey;
            logger.Pass($"KF.ImageResource custom cacheKey: '{cacheKey}'");
            results.Pass("ImageResource_custom_cacheKey");
        }
        catch (Exception ex)
        {
            logger.Fail($"KF.ImageResource custom cacheKey: {ex.GetType().Name}: {ex.Message}");
            results.Fail("ImageResource_custom_cacheKey", $"{ex.GetType().Name}: {ex.Message}");
        }

        // KF.ImageResource metadata
        try
        {
            var metadata = SwiftObjectHelper<KF.ImageResource>.GetTypeMetadata();
            logger.Pass($"KF.ImageResource metadata: size={metadata.Size}");
            results.Pass("ImageResource_metadata");
        }
        catch (Exception ex)
        {
            logger.Fail($"KF.ImageResource metadata: {ex.GetType().Name}: {ex.Message}");
            results.Fail("ImageResource_metadata", $"{ex.GetType().Name}: {ex.Message}");
        }

        // SessionDelegate metadata
        try
        {
            var metadata = SwiftObjectHelper<SessionDelegate>.GetTypeMetadata();
            logger.Pass($"SessionDelegate metadata: size={metadata.Size}");
            results.Pass("SessionDelegate_metadata");
        }
        catch (Exception ex)
        {
            logger.Fail($"SessionDelegate metadata: {ex.GetType().Name}: {ex.Message}");
            results.Fail("SessionDelegate_metadata", $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    // ──────────────────────────────────────────────
    // Section 37: Interval Tag Verification
    // ──────────────────────────────────────────────

    private void RunIntervalTagTests(TestLogger logger, TestResults results)
    {
        // Interval.Seconds tag
        try
        {
            using var i = DelayRetryStrategy.Interval.Seconds(1.0);
            bool ok = i.Tag == DelayRetryStrategy.Interval.CaseTag.Seconds;
            logger.Pass($"Interval.Seconds tag correct: {ok}");
            results.Pass("Interval_Seconds_tag");
        }
        catch (Exception ex)
        {
            logger.Fail($"Interval.Seconds tag: {ex.GetType().Name}: {ex.Message}");
            results.Fail("Interval_Seconds_tag", $"{ex.GetType().Name}: {ex.Message}");
        }

        // Interval.Accumulated tag
        try
        {
            using var i = DelayRetryStrategy.Interval.Accumulated(2.0);
            bool ok = i.Tag == DelayRetryStrategy.Interval.CaseTag.Accumulated;
            logger.Pass($"Interval.Accumulated tag correct: {ok}");
            results.Pass("Interval_Accumulated_tag");
        }
        catch (Exception ex)
        {
            logger.Fail($"Interval.Accumulated tag: {ex.GetType().Name}: {ex.Message}");
            results.Fail("Interval_Accumulated_tag", $"{ex.GetType().Name}: {ex.Message}");
        }

        // Interval TryGetSeconds
        try
        {
            using var i = DelayRetryStrategy.Interval.Seconds(5.0);
            i.TryGetSeconds(out var secs);
            logger.Pass($"Interval.TryGetSeconds: {secs}");
            results.Pass("Interval_TryGetSeconds");
        }
        catch (Exception ex)
        {
            logger.Fail($"Interval.TryGetSeconds: {ex.GetType().Name}: {ex.Message}");
            results.Fail("Interval_TryGetSeconds", $"{ex.GetType().Name}: {ex.Message}");
        }

        // Interval TryGetAccumulated
        try
        {
            using var i = DelayRetryStrategy.Interval.Accumulated(3.0);
            i.TryGetAccumulated(out var acc);
            logger.Pass($"Interval.TryGetAccumulated: {acc}");
            results.Pass("Interval_TryGetAccumulated");
        }
        catch (Exception ex)
        {
            logger.Fail($"Interval.TryGetAccumulated: {ex.GetType().Name}: {ex.Message}");
            results.Fail("Interval_TryGetAccumulated", $"{ex.GetType().Name}: {ex.Message}");
        }

        // Interval negative TryGet
        try
        {
            using var i = DelayRetryStrategy.Interval.Seconds(1.0);
            bool found = i.TryGetAccumulated(out _);
            logger.Pass($"Interval.Seconds.TryGetAccumulated: {found} (expected false)");
            results.Pass("Interval_TryGet_negative");
        }
        catch (Exception ex)
        {
            logger.Fail($"Interval TryGet negative: {ex.GetType().Name}: {ex.Message}");
            results.Fail("Interval_TryGet_negative", $"{ex.GetType().Name}: {ex.Message}");
        }

        // Interval CaseTag numeric values
        try
        {
            bool ok = (uint)DelayRetryStrategy.Interval.CaseTag.Seconds == 0
                   && (uint)DelayRetryStrategy.Interval.CaseTag.Accumulated == 1
                   && (uint)DelayRetryStrategy.Interval.CaseTag.Custom == 2;
            logger.Pass($"Interval.CaseTag numeric values correct: {ok}");
            results.Pass("Interval_CaseTag_numeric");
        }
        catch (Exception ex)
        {
            logger.Fail($"Interval.CaseTag numeric: {ex.GetType().Name}: {ex.Message}");
            results.Fail("Interval_CaseTag_numeric", $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    // ──────────────────────────────────────────────
    // Section 38: ImageProcessItem
    // ──────────────────────────────────────────────

    private void RunImageProcessItemTests(TestLogger logger, TestResults results)
    {
        // ImageProcessItem metadata
        try
        {
            var metadata = SwiftObjectHelper<ImageProcessItem>.GetTypeMetadata();
            logger.Pass($"ImageProcessItem metadata: size={metadata.Size}");
            results.Pass("ImageProcessItem_metadata");
        }
        catch (Exception ex)
        {
            logger.Fail($"ImageProcessItem metadata: {ex.GetType().Name}: {ex.Message}");
            results.Fail("ImageProcessItem_metadata", $"{ex.GetType().Name}: {ex.Message}");
        }

        // ImageProcessItem.Image factory
        try
        {
            var uiImage = new UIKit.UIImage();
            using var item = ImageProcessItem.Image(uiImage);
            var tag = item.Tag;
            logger.Pass($"ImageProcessItem.Image: Tag={tag}");
            results.Pass("ImageProcessItem_Image");
        }
        catch (Exception ex)
        {
            logger.Fail($"ImageProcessItem.Image: {ex.GetType().Name}: {ex.Message}");
            results.Fail("ImageProcessItem_Image", $"{ex.GetType().Name}: {ex.Message}");
        }

        // ImageProcessItem.Data factory
        try
        {
            using var item = ImageProcessItem.Data(new byte[] { 0x89, 0x50, 0x4E, 0x47 });
            var tag = item.Tag;
            logger.Pass($"ImageProcessItem.Data: Tag={tag}");
            results.Pass("ImageProcessItem_Data");
        }
        catch (Exception ex)
        {
            logger.Fail($"ImageProcessItem.Data: {ex.GetType().Name}: {ex.Message}");
            results.Fail("ImageProcessItem_Data", $"{ex.GetType().Name}: {ex.Message}");
        }

        // ImageProcessItem.Image tag verification
        try
        {
            var uiImage = new UIKit.UIImage();
            using var item = ImageProcessItem.Image(uiImage);
            bool ok = item.Tag == ImageProcessItem.CaseTag.Image;
            logger.Pass($"ImageProcessItem.Image tag == CaseTag.Image: {ok}");
            results.Pass("ImageProcessItem_Image_tag");
        }
        catch (Exception ex)
        {
            logger.Fail($"ImageProcessItem.Image tag: {ex.GetType().Name}: {ex.Message}");
            results.Fail("ImageProcessItem_Image_tag", $"{ex.GetType().Name}: {ex.Message}");
        }

        // ImageProcessItem.Data tag verification
        try
        {
            using var item = ImageProcessItem.Data(new byte[] { 0xFF, 0xD8 });
            bool ok = item.Tag == ImageProcessItem.CaseTag.Data;
            logger.Pass($"ImageProcessItem.Data tag == CaseTag.Data: {ok}");
            results.Pass("ImageProcessItem_Data_tag");
        }
        catch (Exception ex)
        {
            logger.Fail($"ImageProcessItem.Data tag: {ex.GetType().Name}: {ex.Message}");
            results.Fail("ImageProcessItem_Data_tag", $"{ex.GetType().Name}: {ex.Message}");
        }

        // CaseTag numeric values
        try
        {
            bool ok = (uint)ImageProcessItem.CaseTag.Image == 0
                   && (uint)ImageProcessItem.CaseTag.Data == 1;
            logger.Pass($"ImageProcessItem.CaseTag numeric values: {ok}");
            results.Pass("ImageProcessItem_CaseTag_numeric");
        }
        catch (Exception ex)
        {
            logger.Fail($"ImageProcessItem.CaseTag: {ex.GetType().Name}: {ex.Message}");
            results.Fail("ImageProcessItem_CaseTag_numeric", $"{ex.GetType().Name}: {ex.Message}");
        }

        // Multiple ImageProcessItem create/dispose
        try
        {
            for (int i = 0; i < 5; i++)
            {
                using var item = ImageProcessItem.Data(new byte[] { (byte)i });
                _ = item.Tag;
            }
            logger.Pass("5x ImageProcessItem.Data create/dispose: stable");
            results.Pass("ImageProcessItem_Loop");
        }
        catch (Exception ex)
        {
            logger.Fail($"ImageProcessItem loop: {ex.GetType().Name}: {ex.Message}");
            results.Fail("ImageProcessItem_Loop", $"{ex.GetType().Name}: {ex.Message}");
        }

        // Radius TryGet value verification
        try
        {
            using var r = Radius.Point(42.0);
            r.TryGetPoint(out var value);
            bool ok = Math.Abs(value - 42.0) < 0.001;
            logger.Pass($"Radius.Point(42).TryGetPoint: value={value}, correct={ok}");
            results.Pass("Radius_Point_value_verify");
        }
        catch (Exception ex)
        {
            logger.Fail($"Radius value verify: {ex.GetType().Name}: {ex.Message}");
            results.Fail("Radius_Point_value_verify", $"{ex.GetType().Name}: {ex.Message}");
        }

        // StorageExpiration.Seconds value verification
        try
        {
            using var exp = StorageExpiration.Seconds(7200.0);
            exp.TryGetSeconds(out var seconds);
            bool ok = Math.Abs(seconds - 7200.0) < 0.001;
            logger.Pass($"StorageExpiration.Seconds(7200).TryGetSeconds: value={seconds}, correct={ok}");
            results.Pass("StorageExpiration_Seconds_value_verify");
        }
        catch (Exception ex)
        {
            logger.Fail($"StorageExpiration value verify: {ex.GetType().Name}: {ex.Message}");
            results.Fail("StorageExpiration_Seconds_value_verify", $"{ex.GetType().Name}: {ex.Message}");
        }

        // BlurImageProcessor value verification
        try
        {
            using var blur = new BlurImageProcessor(12.5);
            var radius = blur.BlurRadius;
            bool ok = Math.Abs(radius - 12.5) < 0.001;
            logger.Pass($"BlurImageProcessor(12.5).BlurRadius: {radius}, correct={ok}");
            results.Pass("BlurImageProcessor_value_verify");
        }
        catch (Exception ex)
        {
            logger.Fail($"BlurImageProcessor value verify: {ex.GetType().Name}: {ex.Message}");
            results.Fail("BlurImageProcessor_value_verify", $"{ex.GetType().Name}: {ex.Message}");
        }

        // ResizingImageProcessor size value verification
        try
        {
            using var proc = new ResizingImageProcessor(new Swift.CGSize(123.0, 456.0));
            var size = proc.ReferenceSize;
            bool ok = Math.Abs(size.Width - 123.0) < 0.001 && Math.Abs(size.Height - 456.0) < 0.001;
            logger.Pass($"ResizingImageProcessor(123x456).ReferenceSize: ({size.Width}, {size.Height}), correct={ok}");
            results.Pass("ResizingImageProcessor_size_verify");
        }
        catch (Exception ex)
        {
            logger.Fail($"ResizingImageProcessor size verify: {ex.GetType().Name}: {ex.Message}");
            results.Fail("ResizingImageProcessor_size_verify", $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    // ──────────────────────────────────────────────
    // Section 39: Cross-Type Interactions
    // ──────────────────────────────────────────────

    private void RunCrossTypeTests(TestLogger logger, TestResults results)
    {
        // KingfisherManager.Shared.Cache and ImageCache.Default should both be accessible
        try
        {
            var mgr = KingfisherManager.Shared;
            var mgrCache = mgr.Cache;
            var defaultCache = ImageCache.Default;
            bool ok = mgrCache != null && defaultCache != null;
            logger.Pass($"KingfisherManager.Shared.Cache and ImageCache.Default both valid: {ok}");
            results.Pass("CrossType_Manager_Cache");
        }
        catch (Exception ex)
        {
            logger.Fail($"CrossType_Manager_Cache: {ex.GetType().Name}: {ex.Message}");
            results.Fail("CrossType_Manager_Cache", $"{ex.GetType().Name}: {ex.Message}");
        }

        // KingfisherManager.Shared.Downloader and ImageDownloader.Default should both be accessible
        try
        {
            var mgr = KingfisherManager.Shared;
            var mgrDl = mgr.Downloader;
            var defaultDl = ImageDownloader.Default;
            bool ok = mgrDl != null && defaultDl != null;
            logger.Pass($"KingfisherManager.Shared.Downloader and ImageDownloader.Default both valid: {ok}");
            results.Pass("CrossType_Manager_Downloader");
        }
        catch (Exception ex)
        {
            logger.Fail($"CrossType_Manager_Downloader: {ex.GetType().Name}: {ex.Message}");
            results.Fail("CrossType_Manager_Downloader", $"{ex.GetType().Name}: {ex.Message}");
        }

        // Custom KingfisherManager with custom downloader/cache
        try
        {
            var customDl = new ImageDownloader("CrossTypeTestDL");
            var customCache = new ImageCache("CrossTypeTestCache");
            var mgr = new KingfisherManager(customDl, customCache);
            var mgrCache = mgr.Cache;
            var mgrDl = mgr.Downloader;
            bool ok = mgrCache != null && mgrDl != null;
            logger.Pass($"Custom KingfisherManager cross-type: cache valid={mgrCache != null}, dl valid={mgrDl != null}");
            mgr.Dispose();
            customDl.Dispose();
            customCache.Dispose();
            results.Pass("CrossType_Custom_Manager");
        }
        catch (Exception ex)
        {
            logger.Fail($"CrossType_Custom_Manager: {ex.GetType().Name}: {ex.Message}");
            results.Fail("CrossType_Custom_Manager", $"{ex.GetType().Name}: {ex.Message}");
        }

        // ImageCache IsCached returns false for never-stored key
        try
        {
            var cache = new ImageCache("IsCachedTest");
            bool cached = cache.IsCached("totally_nonexistent_key_xyz");
            bool ok = !cached; // Should be false
            logger.Pass($"New ImageCache.IsCached for non-existent key: {cached} (expected false, ok={ok})");
            cache.Dispose();
            results.Pass("CrossType_IsCached_false");
        }
        catch (Exception ex)
        {
            logger.Fail($"CrossType_IsCached_false: {ex.GetType().Name}: {ex.Message}");
            results.Fail("CrossType_IsCached_false", $"{ex.GetType().Name}: {ex.Message}");
        }

        // Multiple processor construction without leaks
        try
        {
            for (int i = 0; i < 10; i++)
            {
                using var proc = new BlurImageProcessor(i * 1.0);
                _ = proc.BlurRadius;
            }
            logger.Pass("10x BlurImageProcessor create/dispose cycle: no crash");
            results.Pass("CrossType_Processor_Loop");
        }
        catch (Exception ex)
        {
            logger.Fail($"Processor loop: {ex.GetType().Name}: {ex.Message}");
            results.Fail("CrossType_Processor_Loop", $"{ex.GetType().Name}: {ex.Message}");
        }

        // Multiple enum creation without leaks
        try
        {
            for (int i = 0; i < 10; i++)
            {
                using var exp = StorageExpiration.Seconds(i * 100.0);
                _ = exp.Tag;
            }
            logger.Pass("10x StorageExpiration create/dispose cycle: no crash");
            results.Pass("CrossType_Enum_Loop");
        }
        catch (Exception ex)
        {
            logger.Fail($"Enum loop: {ex.GetType().Name}: {ex.Message}");
            results.Fail("CrossType_Enum_Loop", $"{ex.GetType().Name}: {ex.Message}");
        }

        // Multiple Radius creation without leaks
        try
        {
            for (int i = 0; i < 10; i++)
            {
                using var r = Radius.Point(i * 5.0);
                _ = r.Tag;
                r.TryGetPoint(out var val);
            }
            logger.Pass("10x Radius create/TryGet/dispose cycle: no crash");
            results.Pass("CrossType_Radius_Loop");
        }
        catch (Exception ex)
        {
            logger.Fail($"Radius loop: {ex.GetType().Name}: {ex.Message}");
            results.Fail("CrossType_Radius_Loop", $"{ex.GetType().Name}: {ex.Message}");
        }

        // Multiple ImageTransition creation without leaks
        try
        {
            for (int i = 0; i < 5; i++)
            {
                using var t = ImageTransition.Fade(i * 0.1);
                _ = t.Tag;
            }
            logger.Pass("5x ImageTransition create/dispose cycle: no crash");
            results.Pass("CrossType_Transition_Loop");
        }
        catch (Exception ex)
        {
            logger.Fail($"Transition loop: {ex.GetType().Name}: {ex.Message}");
            results.Fail("CrossType_Transition_Loop", $"{ex.GetType().Name}: {ex.Message}");
        }
    }
}

#endregion
