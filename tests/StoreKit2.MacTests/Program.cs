// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using StoreKit2;

int passed = 0, failed = 0;

void Pass(string name)
{
    passed++;
    Console.WriteLine($"[STOREKIT2-MAC] PASS: {name}");
}

void Fail(string name, string error)
{
    failed++;
    Console.WriteLine($"[STOREKIT2-MAC] FAIL: {name} — {error}");
}

// Test 1: AppStore.CanMakePayments
try
{
    bool canMake = AppStore.CanMakePayments;
    Console.WriteLine($"[STOREKIT2-MAC] AppStore.CanMakePayments = {canMake}");
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
    Console.WriteLine($"[STOREKIT2-MAC] AppStore.DeviceVerificationID = {id?.ToString() ?? "(nil)"}");
    Pass("AppStore.DeviceVerificationID");
}
catch (Exception ex)
{
    Fail("AppStore.DeviceVerificationID", ex.Message);
}

// Test 3: Transaction.All async sequence creation
try
{
    var allTransactions = Transaction.All;
    Console.WriteLine($"[STOREKIT2-MAC] Transaction.All type = {allTransactions.GetType().Name}");
    allTransactions.Dispose();
    Pass("Transaction.All");
}
catch (Exception ex)
{
    Fail("Transaction.All", ex.Message);
}

// Test 4: VerificationResult enum tag values
try
{
    var unverified = VerificationResult<Transaction>.CaseTag.Unverified;
    var verified = VerificationResult<Transaction>.CaseTag.Verified;
    Console.WriteLine($"[STOREKIT2-MAC] VerificationResult.CaseTag.Unverified = {(uint)unverified}");
    Console.WriteLine($"[STOREKIT2-MAC] VerificationResult.CaseTag.Verified = {(uint)verified}");
    Pass("VerificationResult.CaseTag");
}
catch (Exception ex)
{
    Fail("VerificationResult.CaseTag", ex.Message);
}

// Summary
Console.WriteLine($"[STOREKIT2-MAC] Results: {passed} passed, {failed} failed");
if (failed == 0)
    Console.WriteLine("TEST SUCCESS");
else
    Console.WriteLine($"TEST FAILED: {failed} failures");
