// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Swift.Runtime;
using VisionKit;
using CoreGraphics;
#if IOS || MACCATALYST
using Foundation;
using UIKit;
#else
using Foundation;
using ImageIO;
#endif
#if IOS
using Vision;
#endif

namespace SwiftBindings.VisionKit.Tests;

internal static class Tests
{
    private static int s_passed;
    private static int s_failed;
    private static int s_skipped;

    // The suite is async, not sync like most sibling apps: the VisionKit surface worth
    // exercising is async, and on iOS/Mac Catalyst ImageAnalysisInteraction is MainActor-
    // isolated (its generated members assert the main thread). Awaiting on the UI thread
    // lets the UIKit synchronization context resume each continuation back on main; blocking
    // the main thread instead would stall the very executor those continuations need.
    public static async Task<int> RunAsync()
    {
        try
        {
            RunSyncTests();
            await RunAsyncTestsAsync();
        }
        catch (Exception ex)
        {
            Fail("test harness", ex.ToString());
        }

        Log($"Results: {s_passed} passed, {s_failed} failed, {s_skipped} skipped");
        if (s_failed == 0)
            Log("TEST SUCCESS", prefixed: false);
        else
            Log($"TEST FAILED: {s_failed} failures", prefixed: false);
        return s_failed;
    }

    private static void RunSyncTests()
    {
        // Type metadata — the Swift runtime can find and describe each bound type.
        MetadataTest<ImageAnalyzer>("ImageAnalyzer metadata");
        MetadataTest<ImageAnalysis>("ImageAnalysis metadata");
        MetadataTest<ImageAnalyzer.Configuration>("ImageAnalyzer.Configuration metadata");
        MetadataTest<ImageAnalyzer.AnalysisTypes>("ImageAnalyzer.AnalysisTypes metadata");
#if IOS
        MetadataTest<DataScannerViewController>("DataScannerViewController metadata");
        MetadataTest<DataScannerViewController.RecognizedDataType>("DataScannerViewController.RecognizedDataType metadata");
        MetadataTest<RecognizedItem>("RecognizedItem metadata");
        MetadataTest<RecognizedItem.Text>("RecognizedItem.Text metadata");
        MetadataTest<RecognizedItem.Barcode>("RecognizedItem.Barcode metadata");
        MetadataTest<RecognizedItem.BoundsInfo>("RecognizedItem.BoundsInfo metadata");
#endif
#if IOS || MACCATALYST
        MetadataTest<ImageAnalysisInteraction>("ImageAnalysisInteraction metadata");
        MetadataTest<ImageAnalysisInteraction.InteractionTypes>("ImageAnalysisInteraction.InteractionTypes metadata");
        MetadataTest<ImageAnalysisInteraction.Subject>("ImageAnalysisInteraction.Subject metadata");
#else
        // macOS vends the AppKit overlay view instead of the UIKit interaction.
        MetadataTest<ImageAnalysisOverlayView>("ImageAnalysisOverlayView metadata");
        MetadataTest<ImageAnalysisOverlayView.InteractionTypes>("ImageAnalysisOverlayView.InteractionTypes metadata");
        MetadataTest<ImageAnalysisOverlayView.Subject>("ImageAnalysisOverlayView.Subject metadata");
#endif

        // OptionSet statics + the generated bitwise operators: each named option carries a
        // distinct bit and the union keeps both.
        try
        {
            using var text = ImageAnalyzer.AnalysisTypes.Text;
            using var codes = ImageAnalyzer.AnalysisTypes.MachineReadableCode;
            using var union = text | codes;
            uint textBits = text.RawValue;
            uint codeBits = codes.RawValue;
            uint unionBits = union.RawValue;
            if (textBits == 0 || codeBits == 0)
                throw new InvalidOperationException($"expected non-zero option bits, got text={textBits} codes={codeBits}");
            if (textBits == codeBits)
                throw new InvalidOperationException($"expected distinct option bits, both were {textBits}");
            if (unionBits != (textBits | codeBits))
                throw new InvalidOperationException($"union bits {unionBits} != {textBits | codeBits}");
            Log($"AnalysisTypes text={textBits} machineReadableCode={codeBits} union={unionBits}");
            Pass("ImageAnalyzer.AnalysisTypes option bits");
        }
        catch (Exception ex)
        {
            Fail("ImageAnalyzer.AnalysisTypes option bits", ex.Message);
        }

        // Configuration construction — a Swift struct built from C# and read back.
        try
        {
            using var requested = ImageAnalyzer.AnalysisTypes.Text | ImageAnalyzer.AnalysisTypes.MachineReadableCode;
            using var config = new ImageAnalyzer.Configuration(requested);
            using var readBack = config.AnalysisTypes;
            if (readBack.RawValue != requested.RawValue)
                throw new InvalidOperationException($"analysisTypes round-trip mismatch: set {requested.RawValue}, read {readBack.RawValue}");
            Log($"Configuration.AnalysisTypes round-tripped as {readBack.RawValue}");
            Pass("ImageAnalyzer.Configuration construction");
        }
        catch (Exception ex)
        {
            Fail("ImageAnalyzer.Configuration construction", ex.Message);
        }

        // Configuration.Locales — a [String] property projected to IReadOnlyList<string>,
        // settable from a C# collection.
        try
        {
            using var config = new ImageAnalyzer.Configuration(ImageAnalyzer.AnalysisTypes.Text);
            var initial = config.Locales;
            config.Locales = new[] { "en-US" };
            var updated = config.Locales;
            if (updated.Count != 1 || updated[0] != "en-US")
                throw new InvalidOperationException($"locales round-trip mismatch: [{string.Join(",", updated)}]");
            Log($"Configuration.Locales default count={initial.Count}, after set=[{string.Join(",", updated)}]");
            Pass("ImageAnalyzer.Configuration.Locales round-trip");
        }
        catch (Exception ex)
        {
            Fail("ImageAnalyzer.Configuration.Locales round-trip", ex.Message);
        }

        // Static availability probes. The value is environment-dependent (a simulator may
        // report either), so the assertion is that the call returns a value at all.
        try
        {
            bool supported = ImageAnalyzer.IsSupported;
            Log($"ImageAnalyzer.IsSupported = {supported}");
            Pass("ImageAnalyzer.IsSupported");
        }
        catch (Exception ex)
        {
            Fail("ImageAnalyzer.IsSupported", ex.Message);
        }

        try
        {
            var languages = ImageAnalyzer.SupportedTextRecognitionLanguages;
            Log($"ImageAnalyzer.SupportedTextRecognitionLanguages count = {languages.Count}");
            Pass("ImageAnalyzer.SupportedTextRecognitionLanguages");
        }
        catch (Exception ex)
        {
            Fail("ImageAnalyzer.SupportedTextRecognitionLanguages", ex.Message);
        }

#if IOS
        // DataScannerViewController's availability statics are MainActor-isolated class vars;
        // the value is false on a simulator without a camera, so assert only that they return.
        try
        {
            bool supported = DataScannerViewController.IsSupported;
            bool available = DataScannerViewController.IsAvailable;
            Log($"DataScannerViewController.IsSupported = {supported}, IsAvailable = {available}");
            Pass("DataScannerViewController.IsSupported/IsAvailable");
        }
        catch (Exception ex)
        {
            Fail("DataScannerViewController.IsSupported/IsAvailable", ex.Message);
        }

        // RecognizedDataType: a Swift enum-like value type whose cases are built through
        // static factories, one of them taking a typed [VNBarcodeSymbology] array.
        try
        {
            using var barcode = DataScannerViewController.RecognizedDataType.Barcode(
                new[] { VNBarcodeSymbology.Ean13, VNBarcodeSymbology.Code128 });
            using var anyBarcode = DataScannerViewController.RecognizedDataType.GetBarcode();
            using var anyText = DataScannerViewController.RecognizedDataType.GetText();
            using var localizedText = DataScannerViewController.RecognizedDataType.Text(new[] { "en-US" });

            var set = new HashSet<DataScannerViewController.RecognizedDataType> { barcode, anyText };
            if (set.Count != 2)
                throw new InvalidOperationException($"expected 2 distinct recognized data types, got {set.Count}");
            if (barcode.Equals(anyText))
                throw new InvalidOperationException("barcode and text cases compared equal");
            Log($"RecognizedDataType set holds {set.Count} distinct cases (barcode+text); " +
                $"any-barcode/localized-text also constructed ({anyBarcode.Equals(barcode)}/{localizedText.Equals(anyText)} equal to their unparameterized peers)");
            Pass("DataScannerViewController.RecognizedDataType construction");
        }
        catch (Exception ex)
        {
            Fail("DataScannerViewController.RecognizedDataType construction", ex.Message);
        }

        try
        {
            var languages = DataScannerViewController.SupportedTextRecognitionLanguages;
            Log($"DataScannerViewController.SupportedTextRecognitionLanguages count = {languages.Count}");
            Pass("DataScannerViewController.SupportedTextRecognitionLanguages");
        }
        catch (Exception ex)
        {
            Fail("DataScannerViewController.SupportedTextRecognitionLanguages", ex.Message);
        }

        RunDataScannerInstanceTests();
#endif
    }

#if IOS
    // The controller itself, not just its statics. Construction, its instance properties, and
    // the start/stop pair all work on a simulator: a UIViewController is constructible without
    // a camera, and only scanning needs one. Every member here is MainActor-isolated, so this
    // runs on the main thread like the static probes above.
    private static void RunDataScannerInstanceTests()
    {
        // The headline call: the seven-argument initializer, every argument passed explicitly
        // and each one different from its default, so the whole parameter list has to marshal
        // (a Swift Set, an enum, and five Bools) and can be read back.
        //
        // The Set is passed EMPTY here so the boundary case stays covered on its own; the
        // populated-Set form — the shape a real consumer writes — is exercised by the next test.
        DataScannerViewController? scanner = null;
        try
        {
            scanner = new DataScannerViewController(
                recognizedDataTypes: new HashSet<DataScannerViewController.RecognizedDataType>(),
                qualityLevel: DataScannerViewController.QualityLevelKind.Fast,
                recognizesMultipleItems: true,
                isHighFrameRateTrackingEnabled: false,
                isPinchToZoomEnabled: false,
                isGuidanceEnabled: false,
                isHighlightingEnabled: true);

            var mismatches = new List<string>();
            if (scanner.QualityLevel != DataScannerViewController.QualityLevelKind.Fast)
                mismatches.Add($"QualityLevel={scanner.QualityLevel}");
            if (!scanner.RecognizesMultipleItems) mismatches.Add("RecognizesMultipleItems=false");
            if (scanner.IsHighFrameRateTrackingEnabled) mismatches.Add("IsHighFrameRateTrackingEnabled=true");
            if (scanner.IsPinchToZoomEnabled) mismatches.Add("IsPinchToZoomEnabled=true");
            if (scanner.IsGuidanceEnabled) mismatches.Add("IsGuidanceEnabled=true");
            if (!scanner.IsHighlightingEnabled) mismatches.Add("IsHighlightingEnabled=false");
            if (mismatches.Count > 0)
                throw new InvalidOperationException(
                    "constructor arguments did not round-trip: " + string.Join(", ", mismatches));

            Log("DataScannerViewController 7-arg init round-tripped: QualityLevel=Fast, " +
                "RecognizesMultipleItems=True, HighFrameRateTracking=False, PinchToZoom=False, " +
                "Guidance=False, Highlighting=True");
            Pass("DataScannerViewController construction (all arguments)");
        }
        catch (Exception ex)
        {
            Fail("DataScannerViewController construction (all arguments)", Describe(ex));
        }

        // The initializer's non-empty recognizedDataTypes argument — the shape a real consumer
        // writes. Building the Swift Set marshals each struct element through Set.insert, which
        // the runtime routes through a plain-cdecl shim rather than the raw CallConvSwift
        // P/Invoke (whose (Bool, @out) tuple return Mono's JIT mis-handles). Constructing with
        // two distinct data types, reading the instance back, and disposing exercises the
        // insert, the set's element count, and its release in one pass — the exact sequence
        // that used to corrupt memory on this runtime.
        try
        {
            using var barcode = DataScannerViewController.RecognizedDataType.Barcode(
                new[] { VNBarcodeSymbology.Ean13, VNBarcodeSymbology.Code128 });
            using var text = DataScannerViewController.RecognizedDataType.Text(new[] { "en-US" });

            using var populated = new DataScannerViewController(
                recognizedDataTypes: new HashSet<DataScannerViewController.RecognizedDataType> { barcode, text },
                qualityLevel: DataScannerViewController.QualityLevelKind.Accurate,
                recognizesMultipleItems: false,
                isHighFrameRateTrackingEnabled: true,
                isPinchToZoomEnabled: true,
                isGuidanceEnabled: true,
                isHighlightingEnabled: false);

            if (populated.QualityLevel != DataScannerViewController.QualityLevelKind.Accurate)
                throw new InvalidOperationException($"QualityLevel was {populated.QualityLevel}, expected Accurate");
            if (populated.RecognizesMultipleItems)
                throw new InvalidOperationException("RecognizesMultipleItems came back true");
            if (!populated.IsHighFrameRateTrackingEnabled || !populated.IsPinchToZoomEnabled || !populated.IsGuidanceEnabled)
                throw new InvalidOperationException("a passed-true flag came back false");
            if (populated.IsHighlightingEnabled)
                throw new InvalidOperationException("IsHighlightingEnabled came back true");

            Log("DataScannerViewController populated-set init: 2 recognized data types (barcode+text) " +
                "marshalled through Set.insert; QualityLevel=Accurate and all five flags round-tripped");
            Pass("DataScannerViewController construction (non-empty recognizedDataTypes)");
        }
        catch (Exception ex)
        {
            Fail("DataScannerViewController construction (non-empty recognizedDataTypes)", Describe(ex));
        }

        // The same initializer through its default arguments — the defaults live in the
        // generated C# signature, so this checks they match what Swift actually applies.
        try
        {
            using var defaults = new DataScannerViewController(
                new HashSet<DataScannerViewController.RecognizedDataType>());

            if (defaults.QualityLevel != DataScannerViewController.QualityLevelKind.Balanced)
                throw new InvalidOperationException($"default QualityLevel was {defaults.QualityLevel}, expected Balanced");
            if (defaults.RecognizesMultipleItems)
                throw new InvalidOperationException("default RecognizesMultipleItems was true");
            if (defaults.IsHighlightingEnabled)
                throw new InvalidOperationException("default IsHighlightingEnabled was true");
            if (!defaults.IsHighFrameRateTrackingEnabled || !defaults.IsPinchToZoomEnabled || !defaults.IsGuidanceEnabled)
                throw new InvalidOperationException("a default-true argument came back false");

            Log("DataScannerViewController default-argument init: QualityLevel=Balanced, " +
                "RecognizesMultipleItems=False, Highlighting=False, the three default-true flags all True");
            Pass("DataScannerViewController construction (default arguments)");
        }
        catch (Exception ex)
        {
            Fail("DataScannerViewController construction (default arguments)", Describe(ex));
        }

        if (scanner is null)
            return;

        try
        {
            // The return direction of the Set bridge — a Swift Set marshalled back out to a
            // managed IReadOnlySet. Empty here, for the reason given above, so this covers the
            // marshalling of the container itself rather than its contents.
            var types = scanner.RecognizedDataTypes;
            if (types.Count != 0)
                throw new InvalidOperationException($"expected the empty set the controller was built with, got {types.Count} case(s)");
            Log($"DataScannerViewController.RecognizedDataTypes read back {types.Count} case(s), matching construction");
            Pass("DataScannerViewController.RecognizedDataTypes read-back");
        }
        catch (Exception ex)
        {
            Fail("DataScannerViewController.RecognizedDataTypes read-back", Describe(ex));
        }

        try
        {
            if (scanner.IsScanning)
                throw new InvalidOperationException("IsScanning was true before StartScanning was ever called");
            double min = scanner.MinZoomFactor;
            double max = scanner.MaxZoomFactor;
            double zoom = scanner.ZoomFactor;
            Log($"DataScannerViewController zoom: Min={min}, Max={max}, Current={zoom}, IsScanning=False");
            Pass("DataScannerViewController.IsScanning/zoom-factor reads");
        }
        catch (Exception ex)
        {
            Fail("DataScannerViewController.IsScanning/zoom-factor reads", Describe(ex));
        }

        // The two writable instance properties on this type: an Optional<CGRect> and a Double.
        // The rect is asserted to echo; the zoom factor is only logged, since the framework is
        // free to clamp it to the capture device's range.
        try
        {
            var region = new CGRect(4, 8, 100, 50);
            scanner.RegionOfInterest = region;
            var readBack = scanner.RegionOfInterest;
            if (readBack is not { } actual)
                throw new InvalidOperationException("RegionOfInterest read back null after being set");
            if (Math.Abs(actual.X - region.X) > 0.001 || Math.Abs(actual.Y - region.Y) > 0.001 ||
                Math.Abs(actual.Width - region.Width) > 0.001 || Math.Abs(actual.Height - region.Height) > 0.001)
                throw new InvalidOperationException($"RegionOfInterest round-tripped as {actual}, expected {region}");

            // ZoomFactor is written and logged but deliberately not asserted: without a capture
            // device the framework reports a zoom range of 0…0 and clamps any write into it, so
            // there is no value this can legitimately demand back. The assertion above is what
            // this test stands on.
            double before = scanner.ZoomFactor;
            scanner.ZoomFactor = 1.5;
            double after = scanner.ZoomFactor;
            Log($"DataScannerViewController.RegionOfInterest round-tripped as {actual}; " +
                $"ZoomFactor {before} -> {after} after writing 1.5 (clamped to the {scanner.MinZoomFactor}…{scanner.MaxZoomFactor} range this device reports)");
            Pass("DataScannerViewController.RegionOfInterest round-trip");
        }
        catch (Exception ex)
        {
            Fail("DataScannerViewController.RegionOfInterest round-trip", Describe(ex));
        }

        // A C# delegate implementation handed to Swift through the generated existential
        // container, then read back. This covers the assignment direction only — Swift calls
        // back exclusively while a scan is running, which a camera-less simulator never
        // reaches, so no callback body can be observed from here.
        try
        {
            var listener = new ScannerDelegate();
            scanner.Delegate = listener;
            var readBack = scanner.Delegate;
            if (readBack is null)
                throw new InvalidOperationException("Delegate read back null while the assigned implementation was still alive");
            Log($"DataScannerViewController.Delegate set to a C# implementation and read back as {readBack.GetType().Name}");
            GC.KeepAlive(listener);
            Pass("DataScannerViewController.Delegate assignment (existential marshalling)");
        }
        catch (Exception ex)
        {
            Fail("DataScannerViewController.Delegate assignment (existential marshalling)", Describe(ex));
        }

        try
        {
            // The stream property is only fetched, never enumerated: iterating it would wait on
            // a scan that a camera-less simulator will never deliver. Fetching it does start a
            // native producer, so it is disposed rather than dropped.
            var items = scanner.RecognizedItems;
            if (items is null)
                throw new InvalidOperationException("RecognizedItems returned null");
            Log($"DataScannerViewController.RecognizedItems surfaced as {items.GetType().Name} (not enumerated)");
            (items as IDisposable)?.Dispose();
            Pass("DataScannerViewController.RecognizedItems shape");
        }
        catch (Exception ex)
        {
            Fail("DataScannerViewController.RecognizedItems shape", Describe(ex));
        }

        try
        {
            // Stopping something that was never started has to be a safe no-op.
            scanner.StopScanning();
            Log("DataScannerViewController.StopScanning on a never-started controller returned normally");
            Pass("DataScannerViewController.StopScanning (no-op)");
        }
        catch (Exception ex)
        {
            Fail("DataScannerViewController.StopScanning (no-op)", Describe(ex));
        }

        // Swift declares startScanning() as throwing when scanning is unavailable, and the
        // generated body marshals that error into a managed exception rather than letting it
        // trap. Which outcome is required depends on the host: where the scanner is
        // unsupported (any simulator), a clean return would mean the wrapper swallowed the
        // call, so the Swift error is mandatory; where it is supported, scanning must actually
        // start.
        bool scannerSupported = DataScannerViewController.IsSupported && DataScannerViewController.IsAvailable;
        try
        {
            scanner.StartScanning();
            if (!scannerSupported)
                throw new InvalidOperationException(
                    "StartScanning returned normally even though the scanner is unsupported here — the Swift error was not surfaced");
            if (!scanner.IsScanning)
                throw new InvalidOperationException("StartScanning returned but IsScanning stayed false");
            Log("DataScannerViewController.StartScanning started a scan on a supported host (IsScanning=True)");
            scanner.StopScanning();
            Pass("DataScannerViewController.StartScanning (catchable failure path)");
        }
        catch (SwiftException ex)
        {
            if (scannerSupported)
                Fail("DataScannerViewController.StartScanning (catchable failure path)",
                    $"the scanner reports as supported here, but starting it failed: {Describe(ex)}");
            else if (!IsScanningUnavailableError(ex))
                Fail("DataScannerViewController.StartScanning (catchable failure path)",
                    $"expected the framework's ScanningUnavailable error, got an unrelated Swift error: {Describe(ex)}");
            else
            {
                Log($"DataScannerViewController.StartScanning surfaced a catchable Swift error, as an unsupported scanner should: {Describe(ex)}");
                Pass("DataScannerViewController.StartScanning (catchable failure path)");
            }
        }
        catch (Exception ex)
        {
            Fail("DataScannerViewController.StartScanning (catchable failure path)",
                $"expected either success or a SwiftException, got {Describe(ex)}");
        }

        scanner.Dispose();
    }

    // startScanning() throws DataScannerViewController.ScanningUnavailable, whose cases the
    // binding surfaces as the exception message (Swift's String(describing:) of the case). Any
    // other message means something failed on the way through the wrapper rather than the
    // framework reporting an unavailable scanner, so the message is matched against the case
    // names the generated enum declares.
    private static bool IsScanningUnavailableError(SwiftException ex)
    {
        foreach (var name in Enum.GetNames<DataScannerViewController.ScanningUnavailable>())
        {
            if (ex.Message.Contains(name, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    // Every member is implemented rather than left to the generated interface defaults: those
    // defaults throw, so an empty implementation would turn any callback Swift did make into
    // an exception crossing back out of a native frame. The counters are here so a future
    // device run with a real camera has something to assert on.
    private sealed class ScannerDelegate : IDataScannerViewControllerDelegate
    {
        public int Callbacks { get; private set; }

        public void DataScannerDidZoom(DataScannerViewController dataScanner) => Callbacks++;

        public void DataScannerDidTapOn(DataScannerViewController dataScanner, RecognizedItem item) => Callbacks++;

        public void DataScannerDidAddAllItems(DataScannerViewController dataScanner,
            IEnumerable<RecognizedItem> addedItems, IEnumerable<RecognizedItem> allItems) => Callbacks++;

        public void DataScannerDidUpdateAllItems(DataScannerViewController dataScanner,
            IEnumerable<RecognizedItem> updatedItems, IEnumerable<RecognizedItem> allItems) => Callbacks++;

        public void DataScannerDidRemoveAllItems(DataScannerViewController dataScanner,
            IEnumerable<RecognizedItem> removedItems, IEnumerable<RecognizedItem> allItems) => Callbacks++;

        public void DataScannerBecameUnavailableWithError(DataScannerViewController dataScanner,
            DataScannerViewController.ScanningUnavailable error) => Callbacks++;
    }
#endif

    private static async Task RunAsyncTestsAsync()
    {
        // Async method with a real payload: analyze a small in-memory image carrying drawn
        // marks. The request marshals an image reference plus a Swift struct across, and the
        // result marshals an ImageAnalysis back; an empty analysis would still be a pass,
        // since what is under test is the bridge rather than the recognizer's accuracy.
        // iOS/Mac Catalyst take the UIImage overload; macOS uses the CGImage one, which needs
        // no AppKit host and so runs in the plain console harness.
#if IOS || MACCATALYST
        const string analyzeName = "ImageAnalyzer.AnalyzeAsync (UIImage)";
#else
        const string analyzeName = "ImageAnalyzer.AnalyzeAsync (CGImage)";
#endif
        ImageAnalysis? analysis = null;
        try
        {
            using var image = MakeTestImage();
            using var analyzer = new ImageAnalyzer();
            using var config = new ImageAnalyzer.Configuration(
                ImageAnalyzer.AnalysisTypes.Text | ImageAnalyzer.AnalysisTypes.MachineReadableCode);

#if IOS || MACCATALYST
            analysis = await analyzer.AnalyzeAsync(image, config).WaitAsync(TimeSpan.FromSeconds(30));
#else
            // The console harness owns the main thread, so nothing drains the main run loop
            // while an await is outstanding. VisionKit's macOS analyzer delivers its result
            // through that loop, so the wait has to turn it rather than simply block.
            analysis = await PumpUntilCompleteAsync(
                analyzer.AnalyzeAsync(image, CGImagePropertyOrientation.Up, config),
                TimeSpan.FromSeconds(30));
#endif
            string transcript = analysis.Transcript;
            using var textOnly = ImageAnalyzer.AnalysisTypes.Text;
            bool hasText = analysis.HasResults(textOnly);
            Log($"ImageAnalyzer.AnalyzeAsync returned an analysis: hasText={hasText}, transcript=\"{transcript}\"");
            Pass(analyzeName);
        }
        catch (TimeoutException)
        {
            // Not a skip: this call has everything it needs (an in-memory image, no camera,
            // no permission prompt), so a Task that never completes is the async bridge
            // failing to resume, which is exactly what this suite exists to catch.
            Fail(analyzeName, "analysis did not complete within 30s");
        }
        catch (Exception ex) when (IsEnvironmentRefusal(ex))
        {
            Skip(analyzeName, $"framework refused the request: {Describe(ex)}");
        }
        catch (Exception ex)
        {
            Fail(analyzeName, Describe(ex));
        }

#if IOS || MACCATALYST
        // Async accessor: `var subjects: Set<Subject> { get async }` on a MainActor-isolated
        // type — the accessor is reached through a generated Get…Async method. It only
        // resolves for an interaction that has an analysis attached, so this runs solely on
        // the analysis produced above. An empty set is a fine outcome; the accessor bridge is
        // what's being exercised.
        const string subjectsName = "ImageAnalysisInteraction.GetSubjectsAsync (async accessor)";
        if (analysis is null)
        {
            Skip(subjectsName, "no analysis was produced to attach — the accessor has no precondition to resolve against");
        }
        else
        {
            try
            {
                var interaction = new ImageAnalysisInteraction();
                interaction.Analysis = analysis;
                if (interaction.Analysis is null)
                    throw new InvalidOperationException("Analysis property read back null after being set");

                var subjects = await interaction.GetSubjectsAsync().WaitAsync(TimeSpan.FromSeconds(15));
                Log($"ImageAnalysisInteraction.GetSubjectsAsync returned {subjects.Count} subject(s)");
                Pass(subjectsName);
            }
            catch (TimeoutException)
            {
                // With an analysis attached the accessor has what it needs, so a pending Task
                // is a bridge failure rather than the framework declining.
                Fail(subjectsName, "accessor did not complete within 15s with an analysis attached");
            }
            catch (Exception ex) when (IsEnvironmentRefusal(ex))
            {
                Skip(subjectsName, $"framework refused the request: {Describe(ex)}");
            }
            catch (Exception ex)
            {
                Fail(subjectsName, Describe(ex));
            }
        }
#endif
        analysis?.Dispose();
    }

#if !IOS && !MACCATALYST
    // Turn the main run loop until the task settles, so a framework callback scheduled on the
    // main queue can actually run. Throws TimeoutException on the same terms as WaitAsync.
    private static async Task<T> PumpUntilCompleteAsync<T>(Task<T> task, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (!task.IsCompleted && DateTime.UtcNow < deadline)
            NSRunLoop.Current.RunUntil(NSDate.Now.AddSeconds(0.05));

        if (!task.IsCompleted)
            throw new TimeoutException();

        return await task;
    }

    // The AppKit-free equivalent: a bitmap context filled white with a black bar drawn into
    // it, handed to the analyzer as a CGImage. No window server or app instance involved, so
    // it runs in the console harness.
    private static CGImage MakeTestImage()
    {
        using var colorSpace = CGColorSpace.CreateDeviceRGB();
        using var context = new CGBitmapContext(
            IntPtr.Zero, 160, 60, 8, 0, colorSpace, CGImageAlphaInfo.PremultipliedFirst);
        context.SetFillColor(1f, 1f, 1f, 1f);
        context.FillRect(new CGRect(0, 0, 160, 60));
        context.SetFillColor(0f, 0f, 0f, 1f);
        context.FillRect(new CGRect(8, 20, 60, 16));
        return context.ToImage() ?? throw new InvalidOperationException("CGBitmapContext produced no image");
    }
#endif

#if IOS || MACCATALYST
    // A small opaque bitmap with drawn text — enough for the analyzer to accept as input.
    private static UIImage MakeTestImage()
    {
        var size = new CGSize(160, 60);
        UIGraphics.BeginImageContextWithOptions(size, opaque: true, scale: 1f);
        try
        {
            UIColor.White.SetFill();
            UIGraphics.RectFill(new CGRect(CGPoint.Empty, size));
            using var text = new NSString("HELLO 12345");
            using var font = UIFont.SystemFontOfSize(24);
            var attributes = new UIStringAttributes { Font = font, ForegroundColor = UIColor.Black };
            text.DrawString(new CGPoint(8, 16), attributes);
            return UIGraphics.GetImageFromCurrentImageContext();
        }
        finally
        {
            UIGraphics.EndImageContext();
        }
    }
#endif

    // A Swift-side error means the framework itself declined the request in this environment
    // (no camera, no entitlement, an unsupported input) — that is a Skip. Everything else is
    // a real defect and stays a Fail, including a PlatformNotSupportedException: every member
    // called here is compiled for this TFM, so an availability refusal would mean the binding
    // shipped a member the platform does not have.
    private static bool IsEnvironmentRefusal(Exception ex) =>
        ex is SwiftException ||
        (ex is AggregateException agg && agg.InnerExceptions.All(IsEnvironmentRefusal));

    private static string Describe(Exception ex) => $"{ex.GetType().Name}: {ex.Message}";

    private static void MetadataTest<T>(string name) where T : class, ISwiftObject
    {
        try
        {
            var metadata = SwiftObjectHelper<T>.GetTypeMetadata();
            if (metadata.Handle == IntPtr.Zero)
                throw new InvalidOperationException("metadata handle is null");
            Pass(name);
        }
        catch (Exception ex)
        {
            Fail(name, ex.Message);
        }
    }

    private static void Pass(string name)
    {
        s_passed++;
        Log($"PASS: {name}");
    }

    private static void Fail(string name, string error)
    {
        s_failed++;
        Log($"FAIL: {name} — {error}");
    }

    private static void Skip(string name, string reason)
    {
        s_skipped++;
        Log($"SKIP: {name} — {reason}");
    }

    internal static void Log(string msg, bool prefixed = true) =>
        Console.WriteLine(prefixed ? $"[VISIONKIT-TEST] {msg}" : msg);
}
