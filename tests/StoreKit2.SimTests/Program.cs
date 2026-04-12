// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Foundation;
using UIKit;
using StoreKit2;

namespace StoreKit2SimTests;

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
    public override async void ViewDidLoad()
    {
        base.ViewDidLoad();
        View!.BackgroundColor = UIColor.White;

        int passed = 0, failed = 0;

        void Pass(string name)
        {
            passed++;
            Console.WriteLine($"[STOREKIT2-TEST] PASS: {name}");
        }

        void Fail(string name, string error)
        {
            failed++;
            Console.WriteLine($"[STOREKIT2-TEST] FAIL: {name} — {error}");
        }

        // Test 1: AppStore.CanMakePayments
        try
        {
            bool canMake = AppStore.CanMakePayments;
            Console.WriteLine($"[STOREKIT2-TEST] AppStore.CanMakePayments = {canMake}");
            Pass("AppStore.CanMakePayments");
        }
        catch (Exception ex)
        {
            Fail("AppStore.CanMakePayments", ex.Message);
        }

        // Test 2: AppStore.DeviceVerificationID
        try
        {
            Guid? id = AppStore.DeviceVerificationID;
            Console.WriteLine($"[STOREKIT2-TEST] AppStore.DeviceVerificationID = {id?.ToString() ?? "(nil)"}");
            Pass("AppStore.DeviceVerificationID");
        }
        catch (Exception ex)
        {
            Fail("AppStore.DeviceVerificationID", ex.Message);
        }

        // Test 3: Transaction.All async sequence creation (no enumeration)
        try
        {
            var allTransactions = Transaction.All;
            Console.WriteLine($"[STOREKIT2-TEST] Transaction.All type = {allTransactions.GetType().Name}");
            allTransactions.Dispose();
            Pass("Transaction.All");
        }
        catch (Exception ex)
        {
            Fail("Transaction.All", ex.Message);
        }

        // Test 4: Transaction.CurrentEntitlements async sequence
        try
        {
            var entitlements = Transaction.CurrentEntitlements;
            Console.WriteLine($"[STOREKIT2-TEST] Transaction.CurrentEntitlements type = {entitlements.GetType().Name}");
            entitlements.Dispose();
            Pass("Transaction.CurrentEntitlements");
        }
        catch (Exception ex)
        {
            Fail("Transaction.CurrentEntitlements", ex.Message);
        }

        // Test 5: VerificationResult enum tag values
        try
        {
            var unverified = VerificationResult<Transaction>.CaseTag.Unverified;
            var verified = VerificationResult<Transaction>.CaseTag.Verified;
            Console.WriteLine($"[STOREKIT2-TEST] VerificationResult.CaseTag.Unverified = {(uint)unverified}");
            Console.WriteLine($"[STOREKIT2-TEST] VerificationResult.CaseTag.Verified = {(uint)verified}");
            Pass("VerificationResult.CaseTag");
        }
        catch (Exception ex)
        {
            Fail("VerificationResult.CaseTag", ex.Message);
        }

        // Summary
        Console.WriteLine($"[STOREKIT2-TEST] Results: {passed} passed, {failed} failed");
        if (failed == 0)
            Console.WriteLine("TEST SUCCESS");
        else
            Console.WriteLine($"TEST FAILED: {failed} failures");
    }
}
