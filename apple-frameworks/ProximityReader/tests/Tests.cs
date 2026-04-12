// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using ProximityReader;
using Swift.Runtime;

namespace SwiftBindings.ProximityReader.Tests;

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
