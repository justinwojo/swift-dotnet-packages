// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Foundation;
using UIKit;
using RoomPlan;
using Swift.Runtime;

namespace RoomPlanSimTests;

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
            Console.WriteLine($"[ROOMPLAN-TEST] PASS: {name}");
        }

        void Fail(string name, string error)
        {
            failed++;
            Console.WriteLine($"[ROOMPLAN-TEST] FAIL: {name} — {error}");
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

        // Core room-capture types — exercises metadata symbol resolution for
        // both struct wrappers and the enum wrappers (CapturedElementCategory).
        MetadataTest<CapturedElementCategory>("CapturedElementCategory metadata");
        MetadataTest<CapturedRoomData>("CapturedRoomData metadata");
        MetadataTest<CapturedStructure>("CapturedStructure metadata");
        MetadataTest<CapturedRoom>("CapturedRoom metadata");
        MetadataTest<CapturedRoom.Section>("CapturedRoom.Section metadata");
        MetadataTest<CapturedRoom.Surface>("CapturedRoom.Surface metadata");
        MetadataTest<CapturedRoom.Object>("CapturedRoom.Object metadata");
        MetadataTest<CapturedRoom.USDExportOptions>("CapturedRoom.USDExportOptions metadata");
        MetadataTest<RoomBuilder.ConfigurationOptions>("RoomBuilder.ConfigurationOptions metadata");

        // Nested plain-enum values: exercise that the generated enum has the
        // correct layout and Swift case order.
        try
        {
            if ((int)RoomCaptureSession.CaptureError.ExceedSceneSizeLimit != 0 ||
                (int)RoomCaptureSession.CaptureError.InternalError != 5)
                throw new InvalidOperationException("CaptureError values mismatch");
            Pass("RoomCaptureSession.CaptureError values");
        }
        catch (Exception ex)
        {
            Fail("RoomCaptureSession.CaptureError values", ex.Message);
        }

        try
        {
            if ((int)RoomCaptureSession.Instruction.MoveCloseToWall != 0 ||
                (int)RoomCaptureSession.Instruction.LowTexture != 5)
                throw new InvalidOperationException("Instruction values mismatch");
            Pass("RoomCaptureSession.Instruction values");
        }
        catch (Exception ex)
        {
            Fail("RoomCaptureSession.Instruction values", ex.Message);
        }

        // Error-description extension — pure enum tag → Swift cdecl round-trip.
        // This is the only call that actually crosses the ABI from managed to Swift
        // without needing permissions or an active session.
        try
        {
            var desc = RoomCaptureSessionCaptureErrorExtensions.GetErrorDescription(
                RoomCaptureSession.CaptureError.ExceedSceneSizeLimit);
            Console.WriteLine($"[ROOMPLAN-TEST] ExceedSceneSizeLimit description = {desc}");
            Pass("RoomCaptureSession.CaptureError.GetErrorDescription");
        }
        catch (Exception ex)
        {
            Fail("RoomCaptureSession.CaptureError.GetErrorDescription", ex.Message);
        }

        // AllCases on CapturedRoom.Surface.Edge — exercises the extension static
        // property path (reflection-based enum case enumeration, no Swift call).
        try
        {
            var all = CapturedRoomSurfaceEdgeExtensions.AllCases;
            if (all.Count == 0)
                throw new InvalidOperationException("Edge.AllCases is empty");
            Pass("CapturedRoom.Surface.Edge.AllCases");
        }
        catch (Exception ex)
        {
            Fail("CapturedRoom.Surface.Edge.AllCases", ex.Message);
        }

        Console.WriteLine($"[ROOMPLAN-TEST] Results: {passed} passed, {failed} failed");
        if (failed == 0)
            Console.WriteLine("TEST SUCCESS");
        else
            Console.WriteLine($"TEST FAILED: {failed} failures");
    }
}
