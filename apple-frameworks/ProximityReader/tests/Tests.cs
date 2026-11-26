// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using ProximityReader;
using Swift.Runtime;

namespace SwiftBindings.ProximityReader.Tests;

internal static class Tests
{
    /// <summary>
    /// Checks a generated enum (or <c>CaseTag</c>) against the binding's contract: each member's value
    /// identifies its Swift case by name, not by position, so the named members must be defined, no
    /// member may equal <c>default</c> (which names no case), and no two members may share a value.
    /// </summary>
    private static void AssertNameKeyedMembers<T>(params T[] named) where T : struct, Enum
    {
        foreach (var member in named)
        {
            if (!Enum.IsDefined(member))
                throw new InvalidOperationException($"{typeof(T).Name} value {member} is not a defined member");
        }
        var seen = new HashSet<T>();
        foreach (var member in Enum.GetValues<T>())
        {
            if (EqualityComparer<T>.Default.Equals(member, default))
                throw new InvalidOperationException($"{typeof(T).Name}.{member} equals default, which names no case");
            if (!seen.Add(member))
                throw new InvalidOperationException($"{typeof(T).Name}.{member} shares its value with another member");
        }
    }

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
            AssertNameKeyedMembers(MobileDocumentReaderError.Unknown, MobileDocumentReaderError.InvalidResponse);
            Pass("MobileDocumentReaderError values");
        }
        catch (Exception ex)
        {
            Fail("MobileDocumentReaderError values", ex.Message);
        }

        // MobileDocumentReaderError.GetErrorDescription: C# extension + P/Invoke pair
        // resolve to the Swift @_cdecl wrapper. The previous skip comment here was stale
        // (the parity bug was fixed before May 2026); verify the call doesn't crash and
        // returns either the LocalizedError string or null on OS versions that vend nil.
        try
        {
            var desc = MobileDocumentReaderError.Unknown.GetErrorDescription();
            Log($"MobileDocumentReaderError.Unknown description = {desc ?? "<null>"}");
            Pass("MobileDocumentReaderError.GetErrorDescription");
        }
        catch (Exception ex)
        {
            Fail("MobileDocumentReaderError.GetErrorDescription", ex.Message);
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
        Console.WriteLine(prefixed ? $"[PROXIMITYREADER-TEST] {msg}" : msg);
}
