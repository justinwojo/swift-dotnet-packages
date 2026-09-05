// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#if IOS
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using AVFoundation;
using CoreFoundation;
using Foundation;
using Swift.Runtime;
using UIKit;
using Vision;
using VisionKit;

namespace SwiftBindings.VisionKit.Tests;

/// <summary>
/// Opt-in, attended, physical-device scenario: VisionKit itself recognises a real QR code through
/// the camera and calls back into a C# <see cref="IDataScannerViewControllerDelegate"/>.
///
/// Every other test in this app drives the delegate by hand through a read-back proxy, which
/// proves the dispatch path but never observes VisionKit producing a recognition. This scenario
/// is the only place the framework's own callback — with a real <see cref="RecognizedItem"/>
/// carrying a real barcode payload — reaches managed code, so it is the one that answers the
/// question issue #46 actually asked. It needs a camera, a person holding the phone, and QR codes
/// whose payloads are handed in through <c>SB_LIVE_SCAN_EXPECT</c> (space-separated, in order),
/// so it never runs from the unattended suite.
///
/// The flow mirrors the reporter's app: the reporter's exact scanner configuration, the delegate
/// assigned before presentation, <c>PresentViewController(scanner, true, scanner.StartScanning)</c>,
/// and a shutdown routine that stops, dismisses and disposes. Three payloads drive three steps:
/// <list type="number">
///   <item>Session 1 — first recognition after a forced collection with the implementation held.</item>
///   <item>Session 1 — a second recognition after another forced collection mid-scan; then an
///   orderly teardown from outside the callback and a fresh session.</item>
///   <item>Session 2 — recognition on a re-created scanner and delegate, with the shutdown routine
///   invoked from <em>inside</em> the recognition callback, as the reporter's app does.</item>
/// </list>
/// Nil and zero-length payloads cannot pass: each step passes only when a barcode payload equal
/// to that step's expected string is read out of an item VisionKit added.
/// </summary>
internal sealed class LiveScanScenario
{
    private const string Tag = "[SCAN]";

    private readonly UIViewController _host;
    private readonly string[] _expected;
    private readonly int _timeoutSeconds;

    private int _step;
    private int _session;
    private DataScannerViewController? _scanner;
    private DataScannerViewController.RecognizedDataType? _barcodes;
    private LiveScanDelegate? _delegate;
    private UILabel? _overlay;
    private NSTimer? _timeout;

    private int _stepsPassed;
    private int _gcCheckpoints;
    private int _failures;
    private int _degradationReports;
    private bool _finished;
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    // Callback tallies, reported in the summary.
    private int _added, _updated, _removed, _zoomed, _tapped, _unavailable;

    private LiveScanScenario(UIViewController host, string[] expected, int timeoutSeconds)
    {
        _host = host;
        _expected = expected;
        _timeoutSeconds = timeoutSeconds;
    }

    /// <summary>The scenario is requested by launching the app with <c>SB_LIVE_SCAN_EXPECT</c> set.</summary>
    public static bool IsRequested =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SB_LIVE_SCAN_EXPECT"));

    public static void Start(UIViewController host)
    {
        var expected = (Environment.GetEnvironmentVariable("SB_LIVE_SCAN_EXPECT") ?? string.Empty)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var timeout = int.TryParse(Environment.GetEnvironmentVariable("SB_LIVE_SCAN_TIMEOUT"), out var t) ? t : 180;
        if (expected.Length != 3)
        {
            Log($"BLOCKED: SB_LIVE_SCAN_EXPECT must carry exactly 3 payloads, got {expected.Length}");
            Log("SCENARIO BLOCKED", prefixed: false);
            return;
        }
        new LiveScanScenario(host, expected, timeout).Run();
    }

    private void Run()
    {
        Log("live QR recognition scenario starting");
        Log($"runtime: IsMonoRuntime={SwiftRuntimeInfo.IsMonoRuntime}, IsNativeAotRuntime={SwiftRuntimeInfo.IsNativeAotRuntime}, " +
            $"Rid={RuntimeInformation.RuntimeIdentifier}, Framework={RuntimeInformation.FrameworkDescription}");
        Log($"device: {UIDevice.CurrentDevice.Model} iOS {UIDevice.CurrentDevice.SystemVersion}");
        Log($"binding assembly: {Describe(typeof(DataScannerViewController).Assembly)}");
        Log($"runtime assembly: {Describe(typeof(SwiftRuntimeInfo).Assembly)}");
        Log($"expected payloads: step1={_expected[0]} step2={_expected[1]} step3={_expected[2]}; per-step timeout {_timeoutSeconds}s");

        // A collected-implementation report on a delegate this scenario holds for its whole life
        // would mean the rooting fix regressed; count them so the summary can refuse a pass.
        ProxyDegradation.ImplCollected += OnImplCollected;

        try
        {
            bool supported = DataScannerViewController.IsSupported;
            bool available = DataScannerViewController.IsAvailable;
            Log($"DataScannerViewController.IsSupported={supported} IsAvailable={available}");
            if (!supported || !available)
            {
                Blocked("scanner unsupported or unavailable on this device");
                return;
            }
        }
        catch (Exception ex)
        {
            Fail($"IsSupported/IsAvailable threw: {ex}");
            Finish();
            return;
        }

        var status = AVCaptureDevice.GetAuthorizationStatus(AVAuthorizationMediaType.Video);
        Log($"camera authorization: {status}");
        if (status == AVAuthorizationStatus.Authorized)
        {
            StartSession();
            return;
        }
        if (status != AVAuthorizationStatus.NotDetermined)
        {
            Blocked($"camera permission is {status}");
            return;
        }
        AVCaptureDevice.RequestAccessForMediaType(AVAuthorizationMediaType.Video, granted =>
        {
            DispatchQueue.MainQueue.DispatchAsync(() =>
            {
                Log($"camera permission request answered: granted={granted}");
                if (granted) StartSession();
                else Blocked("camera permission refused");
            });
        });
    }

    private void StartSession()
    {
        _session++;
        Log($"=== session {_session} start (step {_step + 1}, expecting {_expected[_step]}) ===");
        try
        {
            // The reporter's configuration, verbatim.
            _barcodes = DataScannerViewController.RecognizedDataType.Barcode(new[] { VNBarcodeSymbology.QR });
            _scanner = new DataScannerViewController(
                recognizedDataTypes: new HashSet<DataScannerViewController.RecognizedDataType> { _barcodes },
                qualityLevel: DataScannerViewController.QualityLevelKind.Accurate,
                recognizesMultipleItems: false,
                isHighFrameRateTrackingEnabled: true,
                isPinchToZoomEnabled: true,
                isGuidanceEnabled: true,
                isHighlightingEnabled: false);

            _delegate = new LiveScanDelegate(this);
            _scanner.Delegate = _delegate;
            Log($"session {_session}: delegate assigned (impl #{_delegate.Id})");

            ForceGc($"session {_session}: after delegate assignment, implementation held");

            var readBack = _scanner.Delegate;
            if (readBack is null)
            {
                Fail($"session {_session}: Delegate read back null after a forced collection");
                Finish();
                return;
            }
            Log($"session {_session}: delegate read back as {readBack.GetType().Name} after the collection");
            (readBack as IDisposable)?.Dispose();

            var scanner = _scanner;
            _host.PresentViewController(scanner, true, () =>
            {
                try
                {
                    InstallOverlay(scanner);
                    scanner.StartScanning();
                    Log($"session {_session}: presented and scanning (IsScanning={scanner.IsScanning})");
                    ArmTimeout();
                }
                catch (Exception ex)
                {
                    Fail($"session {_session}: StartScanning threw: {ex}");
                    Finish();
                }
            });
        }
        catch (Exception ex)
        {
            Fail($"session {_session}: setup threw: {ex}");
            Finish();
        }
    }

    // ---- delegate arrivals (main thread, inside VisionKit's own callback frame) ----

    internal void OnAdded(DataScannerViewController dataScanner, IEnumerable<RecognizedItem> addedItems, IEnumerable<RecognizedItem> allItems, int implId)
    {
        _added++;
        if (_finished) return;
        var added = Enumerate(addedItems, out var addedCount);
        var all = Enumerate(allItems, out var allCount);
        bool sameInstance = ReferenceEquals(dataScanner, _scanner);
        bool sameDelegate = _delegate is not null && implId == _delegate.Id;
        Log($"didAdd session={_session} impl#{implId} added={addedCount} all={allCount} " +
            $"addedPayloads=[{string.Join(", ", added)}] allPayloads=[{string.Join(", ", all)}] " +
            $"receiverIsOriginalInstance={sameInstance} receiverEqualsScanner={dataScanner.Equals(_scanner)}");

        if (!sameDelegate)
            Fail($"didAdd reached implementation #{implId}, but session {_session} holds #{_delegate?.Id}");

        var want = _expected[_step];
        bool inAdded = added.Contains(want);
        bool inAll = all.Contains(want);
        if (!inAdded)
        {
            Log($"didAdd carried no payload equal to '{want}' (still waiting)");
            return;
        }
        if (!inAll)
            Fail($"'{want}' was in addedItems but not in allItems");
        if (!sameInstance)
            Fail("the scanner handed to the callback is not the managed instance the scenario presented");

        _stepsPassed++;
        Log($"STEP {_step + 1} PASS expected={want} observed={want} t={_clock.Elapsed.TotalSeconds:F1}s");
        DisarmTimeout();

        switch (_step)
        {
            case 0:
                // Keep scanning on the same session; collect again with the implementation still
                // held, then wait for a different code. Scheduled off the callback frame so the
                // collection does not run under VisionKit's own stack.
                _step = 1;
                DispatchQueue.MainQueue.DispatchAsync(() =>
                {
                    ForceGc($"session {_session}: mid-scan, implementation held");
                    SetOverlay($"Step 2: scan {_expected[1]}");
                    Log($"WAITING for step 2 payload {_expected[1]}");
                    ArmTimeout();
                });
                break;
            case 1:
                // Orderly teardown from outside the callback, then a brand-new session.
                _step = 2;
                DispatchQueue.MainQueue.DispatchAsync(() => StopSession(afterDismiss: () =>
                {
                    ForceGc($"session 1 torn down, delegate released");
                    Log("SESSION 1 COMPLETE");
                    StartSession();
                }));
                break;
            case 2:
                // The reporter's flow: shutdown requested from inside the recognition callback.
                Log("requesting shutdown from inside DataScannerDidAddAllItems");
                StopSession(afterDismiss: () =>
                {
                    ForceGc("session 2 torn down from inside the callback, delegate released");
                    Log("SESSION 2 COMPLETE");
                    Finish();
                });
                break;
        }
    }

    internal void OnUpdated(IEnumerable<RecognizedItem> updated, IEnumerable<RecognizedItem> all)
    {
        _updated++;
        // Reading through the items keeps this member as real as the reporter's: the array and
        // each element still cross the boundary and get inspected even though nothing is asserted.
        Enumerate(updated, out var u);
        Enumerate(all, out var a);
        if (_updated <= 3 || _updated % 25 == 0)
            Log($"didUpdate #{_updated} updated={u} all={a}");
    }

    internal void OnRemoved(IEnumerable<RecognizedItem> removed, IEnumerable<RecognizedItem> all)
    {
        _removed++;
        var r = Enumerate(removed, out var rc);
        Enumerate(all, out var ac);
        Log($"didRemove removed={rc} all={ac} removedPayloads=[{string.Join(", ", r)}]");
    }

    internal void OnZoom() => _zoomed++;

    internal void OnTap(RecognizedItem item)
    {
        _tapped++;
        Log($"didTapOn tag={item.Tag}");
    }

    internal void OnUnavailable(DataScannerViewController.ScanningUnavailable error)
    {
        _unavailable++;
        Fail($"becameUnavailableWithError: {error}");
        Finish();
    }

    // ---- helpers ----

    private static List<string> Enumerate(IEnumerable<RecognizedItem> items, out int count)
    {
        var payloads = new List<string>();
        count = 0;
        foreach (var item in items)
        {
            count++;
            if (item.Tag == RecognizedItem.CaseTag.Barcode && item.TryGetBarcode(out var barcode))
                payloads.Add(barcode.PayloadStringValue ?? "(null payload)");
            else
                payloads.Add($"({item.Tag})");
        }
        return payloads;
    }

    private void StopSession(Action afterDismiss)
    {
        var scanner = _scanner;
        var barcodes = _barcodes;
        _scanner = null;
        _barcodes = null;
        if (scanner is null)
        {
            afterDismiss();
            return;
        }
        try
        {
            scanner.StopScanning();
            Log($"session {_session}: StopScanning returned (IsScanning={scanner.IsScanning})");
        }
        catch (Exception ex)
        {
            Fail($"session {_session}: StopScanning threw: {ex}");
        }
        _host.DismissViewController(true, () =>
        {
            try
            {
                scanner.Dispose();
                barcodes?.Dispose();
                _delegate = null;
                _overlay = null;
                Log($"session {_session}: dismissed and disposed, delegate reference dropped");
            }
            catch (Exception ex)
            {
                Fail($"session {_session}: teardown threw: {ex}");
            }
            afterDismiss();
        });
    }

    private void ForceGc(string checkpoint)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        _gcCheckpoints++;
        Log($"GC checkpoint #{_gcCheckpoints}: {checkpoint}");
    }

    private void InstallOverlay(DataScannerViewController scanner)
    {
        try
        {
            var container = scanner.OverlayContainerView;
            var label = new UILabel
            {
                Text = $"Step {_step + 1}: scan {_expected[_step]}",
                TextColor = UIColor.White,
                BackgroundColor = UIColor.FromRGBA(0, 0, 0, 0.6f),
                TextAlignment = UITextAlignment.Center,
                Font = UIFont.BoldSystemFontOfSize(22),
                TranslatesAutoresizingMaskIntoConstraints = false,
            };
            container.AddSubview(label);
            NSLayoutConstraint.ActivateConstraints(new[]
            {
                label.TopAnchor.ConstraintEqualTo(container.SafeAreaLayoutGuide.TopAnchor, 16),
                label.LeadingAnchor.ConstraintEqualTo(container.LeadingAnchor, 16),
                label.TrailingAnchor.ConstraintEqualTo(container.TrailingAnchor, -16),
                label.HeightAnchor.ConstraintEqualTo(48),
            });
            _overlay = label;
        }
        catch (Exception ex)
        {
            // Cosmetic only; the Mac-side log is the real instruction channel.
            Log($"overlay label not installed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void SetOverlay(string text)
    {
        if (_overlay is not null) _overlay.Text = text;
    }

    private void ArmTimeout()
    {
        DisarmTimeout();
        var step = _step;
        _timeout = NSTimer.CreateScheduledTimer(TimeSpan.FromSeconds(_timeoutSeconds), _ =>
        {
            Fail($"TIMEOUT after {_timeoutSeconds}s waiting for step {step + 1} payload {_expected[step]}");
            StopSession(afterDismiss: Finish);
        });
    }

    private void DisarmTimeout()
    {
        _timeout?.Invalidate();
        _timeout = null;
    }

    private void OnImplCollected(object? sender, SwiftProxyImplCollectedEventArgs e)
    {
        _degradationReports++;
        Fail($"DEGRADATION REPORTED: collected implementation on '{e.Member}' while the scenario still holds its delegate");
    }

    private void Blocked(string why)
    {
        Log($"BLOCKED: {why}");
        ProxyDegradation.ImplCollected -= OnImplCollected;
        Log("SCENARIO BLOCKED", prefixed: false);
    }

    private void Fail(string what)
    {
        _failures++;
        Log($"FAIL: {what}");
    }

    private void Finish()
    {
        if (_finished) return;
        _finished = true;
        DisarmTimeout();
        ProxyDegradation.ImplCollected -= OnImplCollected;
        Log($"summary: steps passed={_stepsPassed}/3 sessions={_session} callbacks: didAdd={_added} didUpdate={_updated} " +
            $"didRemove={_removed} didZoom={_zoomed} didTap={_tapped} unavailable={_unavailable}; " +
            $"gcCheckpoints={_gcCheckpoints} degradationReports={_degradationReports} failures={_failures} " +
            $"elapsed={_clock.Elapsed.TotalSeconds:F1}s");
        bool allSteps = _stepsPassed == 3 && _session == 2 && _added > 0;
        if (_failures == 0 && allSteps)
            Log("TEST SUCCESS", prefixed: false);
        else
            Log($"TEST FAILED: {_failures} failures, steps passed {_stepsPassed}/3", prefixed: false);
    }

    private static string Describe(Assembly asm)
    {
        var name = asm.GetName();
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        return $"{name.Name} {name.Version} (informational: {info ?? "n/a"})";
    }

    internal static void Log(string msg, bool prefixed = true)
    {
        Console.WriteLine(prefixed ? $"{Tag} {msg}" : msg);
        Console.Out.Flush();
    }

    /// <summary>
    /// All six requirements implemented, so a throwing interface default can never stand in for a
    /// callback VisionKit made. Every arrival is forwarded to the scenario with this instance's id
    /// so a callback landing on a stale implementation from an earlier session is visible.
    /// </summary>
    private sealed class LiveScanDelegate : IDataScannerViewControllerDelegate
    {
        private static int s_next;
        private readonly LiveScanScenario _owner;
        public int Id { get; } = Interlocked.Increment(ref s_next);

        public LiveScanDelegate(LiveScanScenario owner) => _owner = owner;

        public void DataScannerDidZoom(DataScannerViewController dataScanner) => _owner.OnZoom();

        public void DataScannerDidTapOn(DataScannerViewController dataScanner, RecognizedItem item) => _owner.OnTap(item);

        public void DataScannerDidAddAllItems(DataScannerViewController dataScanner,
            IEnumerable<RecognizedItem> addedItems, IEnumerable<RecognizedItem> allItems)
            => _owner.OnAdded(dataScanner, addedItems, allItems, Id);

        public void DataScannerDidUpdateAllItems(DataScannerViewController dataScanner,
            IEnumerable<RecognizedItem> updatedItems, IEnumerable<RecognizedItem> allItems)
            => _owner.OnUpdated(updatedItems, allItems);

        public void DataScannerDidRemoveAllItems(DataScannerViewController dataScanner,
            IEnumerable<RecognizedItem> removedItems, IEnumerable<RecognizedItem> allItems)
            => _owner.OnRemoved(removedItems, allItems);

        public void DataScannerBecameUnavailableWithError(DataScannerViewController dataScanner,
            DataScannerViewController.ScanningUnavailable error)
            => _owner.OnUnavailable(error);
    }
}
#endif
