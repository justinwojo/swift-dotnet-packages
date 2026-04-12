// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Translation;
using Swift.Runtime;

namespace SwiftBindings.Translation.Tests;

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

        // Local helper: verify metadata handle is non-zero for a given ISwiftObject type.
        void MetadataTest<T>(string name) where T : ISwiftObject
        {
            try
            {
                var md = SwiftObjectHelper<T>.GetTypeMetadata();
                if (md.Handle == IntPtr.Zero)
                    throw new InvalidOperationException("null handle");
                Pass($"{name} metadata");
            }
            catch (Exception ex) { Fail($"{name} metadata", ex.Message); }
        }

        // Test 1: LanguageAvailability.Status plain enum — verify integer values.
        try
        {
            if ((int)LanguageAvailability.Status.Installed != 0)
                throw new InvalidOperationException($"Installed expected 0, got {(int)LanguageAvailability.Status.Installed}");
            if ((int)LanguageAvailability.Status.Supported != 1)
                throw new InvalidOperationException($"Supported expected 1, got {(int)LanguageAvailability.Status.Supported}");
            if ((int)LanguageAvailability.Status.Unsupported != 2)
                throw new InvalidOperationException($"Unsupported expected 2, got {(int)LanguageAvailability.Status.Unsupported}");
            Pass("LanguageAvailability.Status values");
        }
        catch (Exception ex)
        {
            Fail("LanguageAvailability.Status values", ex.Message);
        }

        // Test 2: LanguageAvailability() public constructor — verify instance is non-null.
        try
        {
            var la = new LanguageAvailability();
            if (la is null)
                throw new InvalidOperationException("LanguageAvailability() returned null");
            Pass("LanguageAvailability constructor");
        }
        catch (Exception ex)
        {
            Fail("LanguageAvailability constructor", ex.Message);
        }

        // Test 3: TranslationError static singletons — verify they are non-null and reachable.
        // These are Swift enum cases vended as static properties; just touching them validates
        // that the cdecl wrapper linked correctly and the Swift runtime is reachable.
        try
        {
            var e1 = TranslationError.UnsupportedSourceLanguage;
            var e2 = TranslationError.UnsupportedTargetLanguage;
            var e3 = TranslationError.UnsupportedLanguagePairing;
            var e4 = TranslationError.UnableToIdentifyLanguage;
            var e5 = TranslationError.NothingToTranslate;
            var e6 = TranslationError.InternalError;
            if (e1 is null || e2 is null || e3 is null || e4 is null || e5 is null || e6 is null)
                throw new InvalidOperationException("one or more static TranslationError singletons was null");
            Pass("TranslationError static singletons");
        }
        catch (Exception ex)
        {
            Fail("TranslationError static singletons", ex.Message);
        }

        // Test 4: TranslationError.ErrorDescription and FailureReason — verify they return
        // without crashing (value may be null or a localised string).
        try
        {
            var err = TranslationError.UnsupportedSourceLanguage;
            string? desc = err.ErrorDescription;
            string? reason = err.FailureReason;
            Log($"UnsupportedSourceLanguage.ErrorDescription = {desc ?? "<null>"}");
            Log($"UnsupportedSourceLanguage.FailureReason = {reason ?? "<null>"}");
            Pass("TranslationError property reads");
        }
        catch (Exception ex)
        {
            Fail("TranslationError property reads", ex.Message);
        }

        // Test 5: TranslationSession.Request constructor round-trip.
        // Construct a Request with a known source text and verify the SourceText
        // property returns the same string. ClientIdentifier is optional (nil by default).
        try
        {
            var req = new TranslationSession.Request("Hello, world!");
            string sourceText = req.SourceText;
            if (sourceText != "Hello, world!")
                throw new InvalidOperationException($"SourceText expected 'Hello, world!', got '{sourceText}'");
            string? clientId = req.ClientIdentifier;
            if (clientId is not null)
                throw new InvalidOperationException($"ClientIdentifier expected null, got '{clientId}'");
            Pass("TranslationSession.Request constructor round-trip");
        }
        catch (Exception ex)
        {
            Fail("TranslationSession.Request constructor round-trip", ex.Message);
        }

        // Test 6: TranslationSession.Request with explicit clientIdentifier.
        try
        {
            var req = new TranslationSession.Request("Bonjour", clientIdentifier: "req-42");
            string sourceText = req.SourceText;
            string? clientId = req.ClientIdentifier;
            if (sourceText != "Bonjour")
                throw new InvalidOperationException($"SourceText expected 'Bonjour', got '{sourceText}'");
            if (clientId != "req-42")
                throw new InvalidOperationException($"ClientIdentifier expected 'req-42', got '{clientId}'");
            Pass("TranslationSession.Request with clientIdentifier");
        }
        catch (Exception ex)
        {
            Fail("TranslationSession.Request with clientIdentifier", ex.Message);
        }

        // Test 7: Metadata loads for core Translation types.
        MetadataTest<LanguageAvailability>("LanguageAvailability");
        MetadataTest<TranslationError>("TranslationError");
        MetadataTest<TranslationSession>("TranslationSession");
        MetadataTest<TranslationSession.Request>("TranslationSession.Request");
        MetadataTest<TranslationSession.Response>("TranslationSession.Response");
        MetadataTest<TranslationSession.Configuration>("TranslationSession.Configuration");

        // Summary
        Log($"Results: {passed} passed, {failed} failed, {skipped} skipped");
        if (failed == 0)
            Log("TEST SUCCESS", prefixed: false);
        else
            Log($"TEST FAILED: {failed} failures", prefixed: false);
        return failed;
    }

    internal static void Log(string msg, bool prefixed = true) =>
        Console.WriteLine(prefixed ? $"[TRANSLATION-TEST] {msg}" : msg);
}
