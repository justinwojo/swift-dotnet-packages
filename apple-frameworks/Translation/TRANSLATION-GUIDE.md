# Translation for .NET — Usage Guide

`SwiftBindings.Apple.Translation` exposes Apple's [Translation](https://developer.apple.com/documentation/translation) framework to C# through .NET 10's native Swift interop — direct Swift calls, not Objective-C proxy wrappers. The framework does on-device, privacy-preserving text translation and language-availability queries. This guide maps the Swift workflow to the generated C# surface.

The API is small. The one wrinkle to understand up front: in Swift, a `TranslationSession` is normally vended *for you* by SwiftUI's `.translationTask` modifier — you don't construct it directly in typical app code. The binding still exposes the session type and its `Translate`/`Translations` methods, plus the request/response value types and the availability/error helpers, so this guide covers what you can drive from C#.

## Contents

- [Requirements & install](#requirements--install)
- [Naming conventions](#naming-conventions)
- [Quick start](#quick-start)
- [Building requests and reading responses](#building-requests-and-reading-responses)
- [Translating with a session](#translating-with-a-session)
- [Batch translation](#batch-translation)
- [Language availability](#language-availability)
- [Configuration](#configuration)
- [Errors](#errors)
- [Memory & threading](#memory--threading)
- [Reference links](#reference-links)

## Requirements & install

- .NET 10.0+
- iOS 26.2+
- macOS host for development

```
dotnet add package SwiftBindings.Apple.Translation
```

```csharp
using Translation;
```

Languages must be downloaded on-device before translation succeeds; the system prompts the user for downloads through the SwiftUI translation flow. Translation is unavailable in some regions and on some devices.

## Naming conventions

The generator applies a few consistent transforms over the Swift names:

| Swift | C# | Rule |
|---|---|---|
| `func translate(_:) async throws` | `TranslateAsync(string)` | `async` methods gain an `Async` suffix and return `Task`/`Task<T>` |
| `func translations(from:) async` | `TranslationsAsync(IEnumerable<…>)` | first argument label dropped; `Async` suffix added |
| `session.translate(batch:)` (sync, returns an `AsyncSequence`) | `Translate(IEnumerable<…>)` → `BatchResponse` | non-`async` member keeps its name; the returned Swift `AsyncSequence` projects to a C# `IAsyncEnumerable<>` |
| `request.sourceText` | `request.SourceText` | properties are PascalCase |
| `enum TranslationError { case nothingToTranslate }` | `TranslationError.NothingToTranslate` (static property) | error cases project to static singletons on a wrapper class |
| `enum LanguageAvailability.Status` | `LanguageAvailability.Status` | a plain C# `enum` (no payload) backed by `int` |

Every `*Async` method also accepts a trailing `CancellationToken` (defaulted), so you can cancel an in-flight call.

## Quick start

`TranslationSession.Request` and `TranslationSession.Response` are plain value types you can construct and read directly — useful for building up batches and unit-testing your own translation plumbing:

```csharp
using Translation;

// A translation request is just (source text, optional correlation id).
var request = new TranslationSession.Request("Hello, world!");
Console.WriteLine(request.SourceText);        // "Hello, world!"
Console.WriteLine(request.ClientIdentifier);  // null (none supplied)

// Tag a request so you can match it to its response later.
var tagged = new TranslationSession.Request("Bonjour", clientIdentifier: "msg-42");
Console.WriteLine(tagged.ClientIdentifier);   // "msg-42"
```

To actually run a translation you need a live `TranslationSession`, which Apple's design hands you via the SwiftUI `.translationTask` modifier — see [Translating with a session](#translating-with-a-session).

## Building requests and reading responses

### `TranslationSession.Request`

A request carries the text to translate plus an optional client identifier used to correlate it with its response in a batch.

```csharp
public Request(string sourceText, string? clientIdentifier = null)
```

| Member | Type | |
|---|---|---|
| `SourceText` | `string` | the text to translate |
| `ClientIdentifier` | `string?` | your correlation id (null if not supplied) |

### `TranslationSession.Response`

A response carries the translated text and the resolved languages. You can also construct one directly (handy for tests/mocks).

```csharp
public Response(
    Swift.Foundation.Locale.Language sourceLanguage,
    Swift.Foundation.Locale.Language targetLanguage,
    string sourceText,
    string targetText,
    string? clientIdentifier = null)
```

| Member | Type | |
|---|---|---|
| `SourceLanguage` | `Swift.Foundation.Locale.Language` | detected/used source language |
| `TargetLanguage` | `Swift.Foundation.Locale.Language` | target language |
| `SourceText` | `string` | original text |
| `TargetText` | `string` | translated text |
| `ClientIdentifier` | `string?` | echoes the request's identifier |

## Translating with a session

A `TranslationSession` performs the work. In Apple's intended design you obtain it from SwiftUI's `.translationTask` modifier rather than constructing it; the binding does emit a constructor, but a session is only functional once the system has wired it to a translation context.

The session exposes these members:

| Member | Signature | |
|---|---|---|
| `TranslateAsync` | `Task<TranslationSession.Response> TranslateAsync(string @string, CancellationToken ct = default)` | translate a single string |
| `TranslationsAsync` | `Task<IReadOnlyList<TranslationSession.Response>> TranslationsAsync(IEnumerable<TranslationSession.Request> batch, CancellationToken ct = default)` | translate a batch, collected into a list |
| `Translate` | `TranslationSession.BatchResponse Translate(IEnumerable<TranslationSession.Request> batch)` | translate a batch, streamed (see below) |
| `PrepareTranslationAsync` | `Task PrepareTranslationAsync(CancellationToken ct = default)` | pre-download / warm up the language model |
| `GetIsReadyAsync` | `Task<bool> GetIsReadyAsync(CancellationToken ct = default)` | whether the session can translate now |
| `SourceLanguage` | `Swift.Foundation.Locale.Language?` (get) | resolved source language, if known |
| `TargetLanguage` | `Swift.Foundation.Locale.Language?` (get) | resolved target language, if known |
| `Cancel()` | `void` | cancel in-flight work |

```csharp
// Given a live session vended by the system:
var response = await session.TranslateAsync("Good morning");
Console.WriteLine(response.TargetText);

if (!await session.GetIsReadyAsync())
    await session.PrepareTranslationAsync();   // trigger any needed downloads
```

`TranslateAsync` is `throws` in Swift — failures surface as a `Swift.Runtime.SwiftException` (see [Errors](#errors)).

## Batch translation

For many strings, send a batch of `Request`s. There are two shapes:

`TranslationsAsync` awaits the whole batch and returns an `IReadOnlyList<Response>`:

```csharp
var requests = new[]
{
    new TranslationSession.Request("Hello",   clientIdentifier: "1"),
    new TranslationSession.Request("Goodbye", clientIdentifier: "2"),
};

IReadOnlyList<TranslationSession.Response> results =
    await session.TranslationsAsync(requests);

foreach (var r in results)
    Console.WriteLine($"{r.ClientIdentifier}: {r.SourceText} → {r.TargetText}");
```

`Translate` returns a `TranslationSession.BatchResponse` that streams responses as they complete. It projects to a C# `IAsyncEnumerable<TranslationSession.Response>`, so you can `await foreach` it:

```csharp
TranslationSession.BatchResponse batch = session.Translate(requests);

await foreach (TranslationSession.Response r in batch)
{
    Console.WriteLine($"{r.ClientIdentifier}: {r.TargetText}");
}
```

`BatchResponse` also exposes the lower-level iterator directly (`MakeAsyncIterator()` / `GetAsyncEnumerator(...)`), whose `AsyncIterator.NextAsync(CancellationToken)` returns `Task<TranslationSession.Response?>` (null at end of stream) — but the `await foreach` form above is the idiomatic way to consume it.

## Language availability

`LanguageAvailability` answers "can I translate from X to Y, and is the model installed?" Construct it directly:

```csharp
var availability = new LanguageAvailability();

// All languages the framework can translate.
IReadOnlyList<Swift.Foundation.Locale.Language> langs =
    await availability.GetSupportedLanguagesAsync();

// Status of a specific pairing (target may be null to ask "from this source to the user's preferred language").
var source = /* a Swift.Foundation.Locale.Language */;
LanguageAvailability.Status status =
    await availability.StatusMethodAsync(source, target: null);
```

`StatusMethodAsync` has two overloads — one takes a source `Language`, the other takes raw `string` text (the framework detects the language):

```csharp
public Task<LanguageAvailability.Status> StatusMethodAsync(
    Swift.Foundation.Locale.Language source,
    Swift.Foundation.Locale.Language? target,
    CancellationToken ct = default);

public Task<LanguageAvailability.Status> StatusMethodAsync(
    string text,
    Swift.Foundation.Locale.Language? target,
    CancellationToken ct = default);
```

`LanguageAvailability.Status` is a plain `enum` (`int`-backed):

| Case | Value | Meaning |
|---|---|---|
| `Installed` | 0 | model downloaded and ready |
| `Supported` | 1 | pairing supported, model not yet downloaded |
| `Unsupported` | 2 | pairing not supported |

```csharp
if (status == LanguageAvailability.Status.Unsupported)
    Console.WriteLine("That language pair isn't available.");
```

## Configuration

`TranslationSession.Configuration` is the value SwiftUI uses to drive `.translationTask` (it tells the system which languages to translate between). The binding emits it so you can construct and inspect one:

```csharp
public Configuration(
    Swift.Foundation.Locale.Language? source = null,
    Swift.Foundation.Locale.Language? target = null);
```

| Member | Type | |
|---|---|---|
| `Source` | `Swift.Foundation.Locale.Language?` (get) | requested source language (null = auto-detect) |
| `Target` | `Swift.Foundation.Locale.Language?` (get) | requested target language (null = system choice) |
| `Version` | `int` (get) | bump to force a re-translation of the same content |
| `Invalidate()` | `void` | invalidate the current configuration |

`Configuration` implements value equality (`==`, `!=`, `Equals`, `GetHashCode`).

## Errors

Throwing session calls surface the documented domain errors as **`Swift.Runtime.SwiftException<TranslationError>`** — the generic exception carries the matching `TranslationError` singleton as its typed payload. Catch that to read the typed error. Plain **`Swift.Runtime.SwiftException`** (the base type) is the fallback, used only when the error type is unknown or the typed payload can't be marshalled — so keep a `catch (SwiftException ex)` after the typed catch. The error cases are also vended as static singletons on `TranslationError` for inspection:

```csharp
using Swift.Runtime;

try
{
    var response = await session.TranslateAsync("…");
}
catch (SwiftException<TranslationError> ex)
{
    // ex.Error is the typed TranslationError payload
    TranslationError error = ex.Error;
    Console.WriteLine(ex.Message);
}
catch (SwiftException ex)
{
    // Fallback: unknown error type or typed marshal failure
    Console.WriteLine(ex.Message);
}
```

`TranslationError` static cases:

| Case | |
|---|---|
| `UnsupportedSourceLanguage` | source language not supported |
| `UnsupportedTargetLanguage` | target language not supported |
| `UnsupportedLanguagePairing` | the specific pair can't be translated |
| `UnableToIdentifyLanguage` | language detection failed |
| `NothingToTranslate` | empty/blank input |
| `AlreadyCancelled` | the session was already cancelled |
| `NotInstalled` | the required model isn't downloaded |
| `InternalError` | framework-internal failure |

Each case exposes localized text:

```csharp
TranslationError err = TranslationError.UnsupportedLanguagePairing;
string? description = err.ErrorDescription;   // may be null
string? reason = err.FailureReason;           // may be null
```

> `ErrorDescription` and `FailureReason` are `string?` and can legitimately be `null` depending on OS version — always null-check before display.

## Memory & threading

Generated types implement `ISwiftObject` / `IDisposable`. For short-lived locals the finalizer cleans up, but `using var` is the recommended pattern for deterministic cleanup — `Dispose` is safe on every generated type and double-Dispose is a no-op.

```csharp
using var availability = new LanguageAvailability();
using var request = new TranslationSession.Request("Hello");
```

- **Cold async sequences.** `BatchResponse` (and its `AsyncIterator`) start the underlying Swift `AsyncSequence` when you begin enumerating. Don't enumerate the same `BatchResponse` twice.
- **Cancellation.** Pass a `CancellationToken` to any `*Async` call, or call `session.Cancel()` to stop in-flight work.
- **Session lifetime.** A `TranslationSession` is only useful while the system context that produced it (the SwiftUI `.translationTask`) is alive.

## Reference links

- [Apple — Translation framework](https://developer.apple.com/documentation/translation)
- [Apple — Translating text within your app](https://developer.apple.com/documentation/translation/translating-text-within-your-app)
