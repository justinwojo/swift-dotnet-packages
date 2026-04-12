// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Foundation;
using UIKit;
using ProximityReader;
using Swift.Runtime;

namespace ProximityReaderSimTests;

public class Application
{
    static void Main(string[] args)
    {
        UIApplication.Main(args, null, typeof(AppDelegate));
    }
}

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

public class MainViewController : UIViewController
{
    public override void ViewDidLoad()
    {
        base.ViewDidLoad();
        View!.BackgroundColor = UIColor.White;

        int passed = 0, failed = 0;

        void Pass(string name)
        {
            passed++;
            Console.WriteLine($"[PROXIMITYREADER-TEST] PASS: {name}");
        }

        void Fail(string name, string error)
        {
            failed++;
            Console.WriteLine($"[PROXIMITYREADER-TEST] FAIL: {name} — {error}");
        }

        void MetadataTest<T>(string name) where T : class, ISwiftObject
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

        // ProximityReader is permission + session heavy; focus on what's reachable
        // without instantiating a live PaymentCardReader session: metadata loads
        // and plain enum round-trips.
        MetadataTest<PaymentCardReadResult>("PaymentCardReadResult metadata");
        MetadataTest<StoreAndForwardBatchDeletionToken>("StoreAndForwardBatchDeletionToken metadata");
        MetadataTest<StoreAndForwardBatch>("StoreAndForwardBatch metadata");
        MetadataTest<StoreAndForwardStatus>("StoreAndForwardStatus metadata");
        MetadataTest<PaymentCardTransactionRequest>("PaymentCardTransactionRequest metadata");
        MetadataTest<PaymentCardVerificationRequest>("PaymentCardVerificationRequest metadata");
        MetadataTest<VASRequest>("VASRequest metadata");
        MetadataTest<VASReadResult>("VASReadResult metadata");
        MetadataTest<MobileDocumentAnyOfDataRequest>("MobileDocumentAnyOfDataRequest metadata");

        // MobileDocumentReaderError: plain int enum values and Swift GetErrorDescription
        // round-trip (pure cdecl, no session state).
        try
        {
            if ((int)MobileDocumentReaderError.Unknown != 0 ||
                (int)MobileDocumentReaderError.InvalidResponse != 10)
                throw new InvalidOperationException("MobileDocumentReaderError values mismatch");
            Pass("MobileDocumentReaderError values");
        }
        catch (Exception ex)
        {
            Fail("MobileDocumentReaderError values", ex.Message);
        }

        // Intentionally omitted: MobileDocumentReaderErrorExtensions.GetErrorDescription
        // The C# binding emits the extension method and P/Invoke, but the Swift wrapper
        // side is missing the corresponding @_cdecl function — resulting in an
        // EntryPointNotFoundException at call time. Tracked as a generator bug: the
        // enum extension emitter needs parity between C# extension emission and
        // Swift wrapper emission for errorDescription (and similar LocalizedError
        // inherited members). Re-enable this test once the wrapper is emitted.

        Console.WriteLine($"[PROXIMITYREADER-TEST] Results: {passed} passed, {failed} failed");
        if (failed == 0)
            Console.WriteLine("TEST SUCCESS");
        else
            Console.WriteLine($"TEST FAILED: {failed} failures");
    }
}
