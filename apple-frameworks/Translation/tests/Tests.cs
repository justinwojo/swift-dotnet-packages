// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Threading.Tasks;
using Translation;
using Swift.Runtime;

namespace SwiftBindings.Translation.Tests;

internal static class Tests
{
    /// <summary>
    /// Awaits <paramref name="task"/> but gives up after <paramref name="budget"/>. The runner is
    /// started from the UIKit main thread and must never block on a Swift async completion, so every
    /// await here is bounded and a hang surfaces as a FAIL instead of a silently green five-second
    /// sleep.
    /// </summary>
    internal static async Task<T> WithTimeout<T>(Task<T> task, TimeSpan budget, string what)
    {
        var timeout = Task.Delay(budget);
        var winner = await Task.WhenAny(task, timeout).ConfigureAwait(true);
        if (!ReferenceEquals(winner, task))
            throw new TimeoutException($"{what} did not complete within {budget.TotalSeconds:0.#}s");
        return await task.ConfigureAwait(true);
    }

    public static async Task<int> RunAsync()
    {
        // Get off ViewDidLoad's synchronous prologue so the run loop is pumping before the first
        // Swift async continuation is posted back to the main queue.
        await Task.Yield();

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

        // ------------------------------------------------------------------------------------
        // Availability await coverage.
        //
        // LanguageAvailability's whole surface is async and, before this wave, none of it was
        // exercised: the suite only read plain enum values and constructed value types, so an
        // availability call that hung, trapped, or returned a garbage discriminator would have
        // gone unnoticed. Every call below is awaited with a bounded budget and asserts a
        // specific documented outcome.
        //
        // LanguageAvailability's async members require iOS 18.0; the app targets a newer
        // deployment floor, but assert the precondition explicitly so a mis-provisioned lane
        // reports a NAMED environment skip rather than a PlatformNotSupportedException FAIL.
        // ------------------------------------------------------------------------------------
        bool availabilityOs = OperatingSystem.IsIOSVersionAtLeast(18, 0);
        IReadOnlyList<Swift.Foundation.Locale.Language>? supportedLanguages = null;

        // Test 8: LanguageAvailability.supportedLanguages — the async property that returns the
        // framework's own supported-language list. This does not depend on any downloaded model,
        // an Apple account, or network access, so an empty or null list is a real defect, not an
        // environment condition.
        if (!availabilityOs)
        {
            Skip("LanguageAvailability.supportedLanguages", "environment: LanguageAvailability async members require iOS 18.0 or later");
        }
        else
        {
            try
            {
                var la = new LanguageAvailability();
                supportedLanguages = await WithTimeout(
                    la.GetSupportedLanguagesAsync(),
                    TimeSpan.FromSeconds(30),
                    "LanguageAvailability.supportedLanguages").ConfigureAwait(true);

                if (supportedLanguages is null)
                    throw new InvalidOperationException("supportedLanguages returned null");
                if (supportedLanguages.Count == 0)
                    throw new InvalidOperationException("supportedLanguages returned an empty list; Translation always reports a non-empty supported set");

                for (int i = 0; i < supportedLanguages.Count; i++)
                {
                    var lang = supportedLanguages[i];
                    if (lang is null)
                        throw new InvalidOperationException($"supportedLanguages[{i}] was null");
                    if (lang.Payload.DangerousGetHandle() == IntPtr.Zero)
                        throw new InvalidOperationException($"supportedLanguages[{i}] carries a null native payload");
                }

                Log($"supportedLanguages returned {supportedLanguages.Count} language value(s)");
                Pass("LanguageAvailability.supportedLanguages");
            }
            catch (Exception ex)
            {
                Fail("LanguageAvailability.supportedLanguages", $"{ex.GetType().Name}: {ex.Message}");
            }
        }

        // Test 9: LanguageAvailability.status(for text:, to:) — the language-identification arm.
        // Passing a nil target asks the framework to identify the text's language only. The
        // documented outcome is one of the three Status cases; anything outside the enum means the
        // discriminator was marshalled wrong.
        if (!availabilityOs)
        {
            Skip("LanguageAvailability.status(for text:)", "environment: LanguageAvailability async members require iOS 18.0 or later");
        }
        else
        {
            try
            {
                var la = new LanguageAvailability();
                var status = await WithTimeout(
                    la.StatusMethodAsync("Hello, world! This is a sentence in English.", target: null),
                    TimeSpan.FromSeconds(30),
                    "LanguageAvailability.status(for text:, to: nil)").ConfigureAwait(true);

                if (!Enum.IsDefined(typeof(LanguageAvailability.Status), status))
                    throw new InvalidOperationException($"status returned an undefined Status discriminator: {(int)status}");

                Log($"status(for: <english text>, to: nil) = {status}");
                Pass("LanguageAvailability.status(for text:)");
            }
            catch (Exception ex)
            {
                Fail("LanguageAvailability.status(for text:)", $"{ex.GetType().Name}: {ex.Message}");
            }
        }

        // Test 10: LanguageAvailability.status(from:to:) — the Locale.Language-consuming overload.
        // Today a consumer cannot construct a Locale.Language (BQ-08), so the only reachable source
        // value is one handed back by supportedLanguages. Round-tripping a language value straight
        // back into the framework is what proves the opaque struct survives the trip out and in.
        if (!availabilityOs)
        {
            Skip("LanguageAvailability.status(from:to:)", "environment: LanguageAvailability async members require iOS 18.0 or later");
        }
        else if (supportedLanguages is null || supportedLanguages.Count == 0)
        {
            Fail("LanguageAvailability.status(from:to:)", "mandatory fixture missing: supportedLanguages produced no Locale.Language value to feed back in (see Test 8)");
        }
        else
        {
            try
            {
                var la = new LanguageAvailability();
                var source = supportedLanguages[0];
                var target = supportedLanguages.Count > 1 ? supportedLanguages[1] : supportedLanguages[0];

                var status = await WithTimeout(
                    la.StatusMethodAsync(source, target),
                    TimeSpan.FromSeconds(30),
                    "LanguageAvailability.status(from:to:)").ConfigureAwait(true);

                if (!Enum.IsDefined(typeof(LanguageAvailability.Status), status))
                    throw new InvalidOperationException($"status returned an undefined Status discriminator: {(int)status}");

                // The source value must survive being passed in — the generated call copies it
                // through the value-witness table, so a consumed/destroyed payload would show up
                // as a freed handle here.
                if (source.Payload.DangerousGetHandle() == IntPtr.Zero)
                    throw new InvalidOperationException("the source Locale.Language payload was cleared by the call");

                Log($"status(from: supported[0], to: supported[{(supportedLanguages.Count > 1 ? 1 : 0)}]) = {status}");
                Pass("LanguageAvailability.status(from:to:)");
            }
            catch (Exception ex)
            {
                Fail("LanguageAvailability.status(from:to:)", $"{ex.GetType().Name}: {ex.Message}");
            }
        }

        // Test 11: identifier-based Locale.Language construction and identifier access (BQ-08).
        //
        // The audit's acceptance criterion is "expose identifier-based language construction and
        // stable identifier/code access", then "compile and run en/es construction, availability
        // enumeration with identifiable results". Those tests are written against exactly that API
        // and live in LanguageConstructionTests.cs, which does not compile against today's Apple
        // supplement — Locale.Language has only a private native-handle constructor, Payload and
        // Dispose. The file is therefore excluded from compilation behind the
        // SWIFTBINDINGS_BQ08_LOCALE_LANGUAGE_IDENTIFIER_API symbol and is turned on in the same
        // change that lands the supplement fix.
#if SWIFTBINDINGS_BQ08_LOCALE_LANGUAGE_IDENTIFIER_API
        foreach (var (name, error) in await LanguageConstructionTests.RunAsync().ConfigureAwait(true))
        {
            if (error is null)
                Pass(name);
            else
                Fail(name, error);
        }
#else
        Skip("Locale.Language identifier construction (BQ-08)",
             "binding gap: Swift.Foundation.Locale.Language exposes no identifier constructor and no identifier/code accessor, "
             + "so en/es construction cannot be written today; the tests exist in LanguageConstructionTests.cs behind "
             + "SWIFTBINDINGS_BQ08_LOCALE_LANGUAGE_IDENTIFIER_API and are enabled with the supplement fix");
#endif

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
