// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

// ---------------------------------------------------------------------------------------------
// Locale.Language identifier construction / identifier access — audit item BQ-08.
//
// BQ-08: "Translation's language type cannot be constructed or identified from normal C#".
// Swift.Foundation.Locale.Language, as generated into the Apple supplement today, has only a
// private native-handle constructor, a Payload property and Dispose. There is no
// identifier-based constructor and no language/code identifier accessor, so a consumer cannot
// select an en -> es pair or identify the languages that an availability query hands back
// without writing their own native bridge. That makes the framework's core purpose
// unreachable from C#.
//
// BQ-08's acceptance criterion is: "expose identifier-based language construction and stable
// identifier/code access, with the necessary supporting types. Compile and run en/es
// construction, availability enumeration with identifiable results, then an installed-language
// translation test on a compatible OS."
//
// The tests below are written against exactly that API — the shape the supplement is expected
// to grow — and therefore DO NOT COMPILE against the current supplement. The whole file is
// excluded from compilation behind SWIFTBINDINGS_BQ08_LOCALE_LANGUAGE_IDENTIFIER_API. Define
// that symbol in the same change that lands the supplement fix; the file is the ready-made
// acceptance gate for it. Nothing else in the suite defines the symbol, so this file compiles
// to nothing until then.
//
// Expected API once BQ-08 lands (mirroring Swift's own Locale.Language surface):
//     new Swift.Foundation.Locale.Language(string identifier)
//     Swift.Foundation.Locale.Language.MinimalIdentifier   -> string
//     Swift.Foundation.Locale.Language.MaximalIdentifier   -> string
//     Swift.Foundation.Locale.Language.LanguageCode        -> Swift.Foundation.Locale.LanguageCode?
//     Swift.Foundation.Locale.LanguageCode.Identifier      -> string
// ---------------------------------------------------------------------------------------------

#if SWIFTBINDINGS_BQ08_LOCALE_LANGUAGE_IDENTIFIER_API

using System.Threading.Tasks;
using Translation;
using Swift.Runtime;

namespace SwiftBindings.Translation.Tests;

internal static class LanguageConstructionTests
{
    /// <summary>
    /// Runs the BQ-08 acceptance tests. Each entry is (test name, failure text) where a null
    /// failure text means the test passed; the caller folds these into its own tally so these
    /// tests are counted and reported exactly like every other test in the suite.
    /// </summary>
    public static async Task<IReadOnlyList<(string Name, string? Error)>> RunAsync()
    {
        var results = new List<(string Name, string? Error)>();

        void Record(string name, Action body)
        {
            try { body(); results.Add((name, null)); }
            catch (Exception ex) { results.Add((name, $"{ex.GetType().Name}: {ex.Message}")); }
        }

        async Task RecordAsync(string name, Func<Task> body)
        {
            try { await body().ConfigureAwait(true); results.Add((name, null)); }
            catch (Exception ex) { results.Add((name, $"{ex.GetType().Name}: {ex.Message}")); }
        }

        // Test BQ08-1: identifier-based construction produces a live value for both members of the
        // en -> es pair the audit names.
        Record("BQ-08 Locale.Language(identifier:) construction", () =>
        {
            var en = new Swift.Foundation.Locale.Language("en");
            var es = new Swift.Foundation.Locale.Language("es");
            if (en.Payload.DangerousGetHandle() == IntPtr.Zero)
                throw new InvalidOperationException("Language(\"en\") produced a null native payload");
            if (es.Payload.DangerousGetHandle() == IntPtr.Zero)
                throw new InvalidOperationException("Language(\"es\") produced a null native payload");
        });

        // Test BQ08-2: identifier access round-trips what was constructed. Foundation's minimal
        // identifier for a bare language subtag is the subtag itself; the maximal identifier
        // expands to language-Script_Region, so it must start with the language subtag.
        Record("BQ-08 Locale.Language identifier round-trip", () =>
        {
            foreach (var subtag in new[] { "en", "es" })
            {
                var lang = new Swift.Foundation.Locale.Language(subtag);

                string minimal = lang.MinimalIdentifier;
                if (minimal != subtag)
                    throw new InvalidOperationException($"MinimalIdentifier for \"{subtag}\" expected \"{subtag}\", got \"{minimal}\"");

                string maximal = lang.MaximalIdentifier;
                if (!maximal.StartsWith(subtag, StringComparison.Ordinal))
                    throw new InvalidOperationException($"MaximalIdentifier for \"{subtag}\" expected to start with \"{subtag}\", got \"{maximal}\"");

                var code = lang.LanguageCode;
                if (code is null)
                    throw new InvalidOperationException($"LanguageCode for \"{subtag}\" was null");
                if (code.Identifier != subtag)
                    throw new InvalidOperationException($"LanguageCode.Identifier for \"{subtag}\" expected \"{subtag}\", got \"{code.Identifier}\"");
            }
        });

        // Test BQ08-3: availability enumeration with identifiable results — the second half of the
        // acceptance criterion. Every language the framework reports must be nameable, and the set
        // must contain English, which Translation supports on every shipping configuration.
        await RecordAsync("BQ-08 supportedLanguages are identifiable", async () =>
        {
            var la = new LanguageAvailability();
            var languages = await Tests.WithTimeout(
                la.GetSupportedLanguagesAsync(),
                TimeSpan.FromSeconds(30),
                "LanguageAvailability.supportedLanguages").ConfigureAwait(true);

            if (languages is null || languages.Count == 0)
                throw new InvalidOperationException("supportedLanguages returned no languages");

            var identifiers = new List<string>(languages.Count);
            for (int i = 0; i < languages.Count; i++)
            {
                string id = languages[i].MinimalIdentifier;
                if (string.IsNullOrWhiteSpace(id))
                    throw new InvalidOperationException($"supportedLanguages[{i}] has an empty MinimalIdentifier");
                identifiers.Add(id);
            }

            bool hasEnglish = identifiers.Exists(id =>
                id.Equals("en", StringComparison.OrdinalIgnoreCase)
                || id.StartsWith("en-", StringComparison.OrdinalIgnoreCase)
                || id.StartsWith("en_", StringComparison.OrdinalIgnoreCase));
            if (!hasEnglish)
                throw new InvalidOperationException($"supportedLanguages did not include English; got [{string.Join(", ", identifiers)}]");

            Tests.Log($"supportedLanguages identifiers: {string.Join(", ", identifiers)}");
        });

        // Test BQ08-4: a constructed en -> es pair reaches the pairing-status API and returns a
        // defined Status. This is the call a consumer actually needs and cannot write today.
        await RecordAsync("BQ-08 status(from: en, to: es)", async () =>
        {
            var la = new LanguageAvailability();
            var status = await Tests.WithTimeout(
                la.StatusMethodAsync(new Swift.Foundation.Locale.Language("en"), new Swift.Foundation.Locale.Language("es")),
                TimeSpan.FromSeconds(30),
                "LanguageAvailability.status(from: en, to: es)").ConfigureAwait(true);

            if (!Enum.IsDefined(typeof(LanguageAvailability.Status), status))
                throw new InvalidOperationException($"status returned an undefined Status discriminator: {(int)status}");

            Tests.Log($"status(from: en, to: es) = {status}");
        });

        // Test BQ08-5: a well-formed but unsupported language subtag must come back as Unsupported
        // rather than as an error or a garbage discriminator. "zxx" is the ISO 639-2 code for
        // "no linguistic content", so it is a valid subtag that Translation cannot translate.
        await RecordAsync("BQ-08 status for an unsupported language", async () =>
        {
            var la = new LanguageAvailability();
            var status = await Tests.WithTimeout(
                la.StatusMethodAsync(new Swift.Foundation.Locale.Language("zxx"), new Swift.Foundation.Locale.Language("en")),
                TimeSpan.FromSeconds(30),
                "LanguageAvailability.status(from: zxx, to: en)").ConfigureAwait(true);

            if (status != LanguageAvailability.Status.Unsupported)
                throw new InvalidOperationException($"status(from: zxx, to: en) expected Unsupported, got {status}");
        });

        return results;
    }
}

#endif
