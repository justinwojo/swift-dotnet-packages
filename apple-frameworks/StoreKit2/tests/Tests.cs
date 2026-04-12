// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using StoreKit2;

namespace SwiftBindings.StoreKit2.Tests;

internal static class Tests
{
    public static int Run()
    {
        int passed = 0, failed = 0, skipped = 0;

        void Pass(string name)
        {
            passed++;
            Log($"PASS: {name}");
        }

        void Fail(string name, string error)
        {
            failed++;
            Log($"FAIL: {name} — {error}");
        }

        void Skip(string name, string reason)
        {
            skipped++;
            Log($"SKIP: {name} — {reason}");
        }

        // Test 1: AppStore.CanMakePayments
        try
        {
            bool canMake = AppStore.CanMakePayments;
            Log($"AppStore.CanMakePayments = {canMake}");
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
            Log($"AppStore.DeviceVerificationID = {id?.ToString() ?? "(nil)"}");
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
            Log($"Transaction.All type = {allTransactions.GetType().Name}");
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
            Log($"Transaction.CurrentEntitlements type = {entitlements.GetType().Name}");
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
            Log($"VerificationResult.CaseTag.Unverified = {(uint)unverified}");
            Log($"VerificationResult.CaseTag.Verified = {(uint)verified}");
            Pass("VerificationResult.CaseTag");
        }
        catch (Exception ex)
        {
            Fail("VerificationResult.CaseTag", ex.Message);
        }

        // Summary
        Log($"Results: {passed} passed, {failed} failed, {skipped} skipped");
        if (failed == 0)
            Log("TEST SUCCESS", prefixed: false);
        else
            Log($"TEST FAILED: {failed} failures", prefixed: false);
        return failed;
    }

    internal static void Log(string msg, bool prefixed = true) =>
        Console.WriteLine(prefixed ? $"[STOREKIT2-TEST] {msg}" : msg);
}
