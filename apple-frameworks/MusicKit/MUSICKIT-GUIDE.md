# MusicKit for .NET — Usage Guide

`SwiftBindings.Apple.MusicKit` exposes Apple's [MusicKit](https://developer.apple.com/documentation/musickit) framework — Apple Music catalog access, the user's library, personal recommendations, and playback — to C# through .NET 10's native Swift interop. These are direct Swift calls, not Objective-C proxy wrappers. This guide maps the Swift workflow to the generated C# surface and only documents API that exists verbatim in the generated bindings.

## Contents

- [Requirements & install](#requirements--install)
- [Naming conventions](#naming-conventions)
- [Quick start](#quick-start)
- [Authorization](#authorization)
- [Subscription status](#subscription-status)
- [Catalog search](#catalog-search)
- [Search suggestions](#search-suggestions)
- [Personal recommendations](#personal-recommendations)
- [Recently played](#recently-played)
- [Library search](#library-search)
- [Music items & collections](#music-items--collections)
- [Playback](#playback)
- [The user's library](#the-users-library)
- [Enums & descriptions](#enums--descriptions)
- [What you can and can't construct](#what-you-can-and-cant-construct)
- [Memory & threading](#memory--threading)
- [Reference links](#reference-links)

## Requirements & install

- .NET 10.0+
- iOS 26.2+, macOS 26.2+, Mac Catalyst 26.2+, tvOS 26.2+
- macOS host for development
- A `NSAppleMusicUsageDescription` key in your `Info.plist`, the MusicKit capability enabled for your App ID, and (for playback / catalog content) an active Apple Music subscription on the device

```
dotnet add package SwiftBindings.Apple.MusicKit
```

```csharp
using MusicKit;
```

> Many individual members are further gated by `[SupportedOSPlatform]` attributes for the OS version that introduced them (e.g. `MusicCatalogSearchRequest.IncludeTopResults` requires iOS 16 / macOS 13, `AudioVariant.SpatialAudio` requires iOS 17.2). The C# compiler surfaces these as availability warnings against your target — honor them.

## Naming conventions

The generator applies a few consistent transforms over the Swift names. Knowing them makes the whole API predictable:

| Swift | C# | Rule |
|---|---|---|
| `func response() async throws` | `ResponseAsync(CancellationToken = default)` | `async` methods gain an `Async` suffix, return `Task`/`Task<T>`, and accept a trailing defaulted `CancellationToken` |
| `MusicAuthorization.request()` | `MusicAuthorization.RequestAsync()` | static async factory, same rules |
| `request.term` | `request.Term` | properties are PascalCase |
| `var limit: Int?` | `int? Limit` | Swift `Int` projects to `int`; optionals to nullable `int?` |
| `enum Track { case song(Song) }` | `Track` class with `.Tag` (a `CaseTag` enum) + `TryGetSong(out Song)` + static `Track.Song(song)` factory | Swift enums-with-payload project to a class: discriminate on `.Tag`, read payloads with `TryGet*`, build with the static case factories |
| `enum MusicAuthorization.Status` (no payload but RawRepresentable) | `MusicAuthorization.Status` class with static singletons (`NotDetermined`, `Authorized`, …) + a `Tag` | compare singletons with `==` / `.Equals`, or switch on `.Tag` |
| `enum AudioVariant: Int` (plain) | `AudioVariant` C# `enum` | plain Swift enums become real C# enums; an `AudioVariantExtensions.AllCases` list and `.GetDescription()` extension are emitted alongside |
| `MusicItemCollection<Song>` | `MusicItemCollection<Song> : IReadOnlyList<Song>` | Swift collections project to indexed, enumerable C# collections |

`MusicKit` is the C# namespace (it mirrors the Swift module name).

## Quick start

A complete authorization → catalog search flow using `MusicCatalogSearchSuggestionsRequest`. Both `MusicCatalogSearchRequest` and `MusicCatalogSearchSuggestionsRequest` are constructible from C# via array-shim factories (see [What you can and can't construct](#what-you-can-and-cant-construct)); this example uses the suggestions request as the more ergonomic entry point for a term-based query.

```csharp
using MusicKit;

// 1. Make sure we're authorized to talk to Apple Music.
var status = MusicAuthorization.CurrentStatus;
if (status.Tag == MusicAuthorization.Status.CaseTag.NotDetermined)
{
    using var requested = await MusicAuthorization.RequestAsync();
    status = requested;
}

if (status.Tag != MusicAuthorization.Status.CaseTag.Authorized)
{
    Console.WriteLine("Apple Music access not granted.");
    return;
}

// 2. Confirm the account can actually play catalog content.
using var subscription = await MusicSubscription.GetCurrentAsync();
if (!subscription.CanPlayCatalogContent)
{
    Console.WriteLine("No active Apple Music subscription.");
    return;
}

// 3. Ask the catalog for search suggestions for a term.
using var request = MusicCatalogSearchSuggestionsRequest.Create_C11D4260("daft punk");
using var response = await request.ResponseAsync();

foreach (var suggestion in response.Suggestions)
{
    using (suggestion)
        Console.WriteLine($"{suggestion.DisplayTerm} (search: {suggestion.SearchTerm})");
}
```

## Authorization

`MusicAuthorization` is the gate for every Apple Music call.

```csharp
// Synchronous read of the current status — never prompts.
MusicAuthorization.Status status = MusicAuthorization.CurrentStatus;

// Prompt the user (shows the system permission dialog the first time).
using MusicAuthorization.Status result = await MusicAuthorization.RequestAsync();
```

`MusicAuthorization.Status` is a projected Swift enum exposed as a class. There are two equivalent ways to inspect it:

```csharp
// (a) compare against the static singletons
if (result == MusicAuthorization.Status.Authorized) { /* … */ }

// (b) switch on the .Tag discriminant
switch (result.Tag)
{
    case MusicAuthorization.Status.CaseTag.NotDetermined: break;
    case MusicAuthorization.Status.CaseTag.Denied:        break;
    case MusicAuthorization.Status.CaseTag.Restricted:    break;
    case MusicAuthorization.Status.CaseTag.Authorized:    break;
}
```

Singletons: `MusicAuthorization.Status.NotDetermined`, `.Denied`, `.Restricted`, `.Authorized`. `CaseTag` values are `NotDetermined = 0`, `Denied = 1`, `Restricted = 2`, `Authorized = 3`.

> `MusicAuthorization` and `MusicAuthorization.Status` are `ISwiftObject` types. `RequestAsync()` hands you a `Status` you own — `using` it. `CurrentStatus` and the static singletons are managed by the binding; treat them as borrowed reads.

## Subscription status

`MusicSubscription` tells you what the signed-in account is allowed to do.

```csharp
using MusicSubscription sub = await MusicSubscription.GetCurrentAsync();

bool canPlay      = sub.CanPlayCatalogContent;   // has an active subscription
bool canSubscribe = sub.CanBecomeSubscriber;     // eligible to subscribe (show an upsell)
bool cloudLibrary = sub.HasCloudLibraryEnabled;  // iCloud Music Library on
```

Watch for changes with the async sequence:

```csharp
MusicSubscription.Updates updates = MusicSubscription.SubscriptionUpdates;
await foreach (MusicSubscription latest in updates)
{
    using (latest)
        Console.WriteLine($"canPlay={latest.CanPlayCatalogContent}");
}
```

`MusicSubscription.Updates` implements `IAsyncEnumerable<MusicSubscription>`; it's effectively infinite, so run it on a long-lived background task.

`MusicSubscription.Error` is a projected enum (singletons `Unknown`, `PermissionDenied`, `PrivacyAcknowledgementRequired`; `CaseTag` values `0`/`1`/`2`).

## Catalog search

You search the Apple Music catalog through `MusicCatalogSearchRequest` → `MusicCatalogSearchResponse`. The request configures the query; calling `ResponseAsync()` performs it.

```csharp
// request is a MusicCatalogSearchRequest you obtained (see "What you can and can't construct")
request.Limit = 25;          // int? — page size
request.Offset = 0;          // int? — page offset
request.IncludeTopResults = true;   // iOS 16+/macOS 13+
string term = request.Term;  // read-only: the term the request was built with

using MusicCatalogSearchResponse response = await request.ResponseAsync();
```

`MusicCatalogSearchResponse` exposes one typed `MusicItemCollection<T>` per result kind:

| Property | Type |
|---|---|
| `Songs` | `MusicItemCollection<Song>` |
| `Albums` | `MusicItemCollection<Album>` |
| `Artists` | `MusicItemCollection<Artist>` |
| `Playlists` | `MusicItemCollection<Playlist>` |
| `MusicVideos` | `MusicItemCollection<MusicVideo>` |
| `Stations` | `MusicItemCollection<Station>` |
| `Curators` | `MusicItemCollection<Curator>` |
| `RadioShows` | `MusicItemCollection<RadioShow>` |
| `RecordLabels` | `MusicItemCollection<RecordLabel>` |
| `TopResults` | `MusicItemCollection<MusicCatalogSearchResponse.TopResult>` |

```csharp
foreach (Song song in response.Songs)
    using (song)
        Console.WriteLine($"{song.Title} — {song.ArtistName}");
```

`MusicCatalogSearchResponse.TopResult` is a projected enum carrying any of `Album`, `Artist`, `Curator`, `MusicVideo`, `Playlist`, `RadioShow`, `RecordLabel`, `Song`, `Station` (discriminate via `.Tag` / `TryGet*`).

## Search suggestions

Unlike the search request above, `MusicCatalogSearchSuggestionsRequest` **is** directly constructible — it's the most ergonomic catalog entry point from C#:

```csharp
using var request = MusicCatalogSearchSuggestionsRequest.Create_C11D4260("radioh");
using MusicCatalogSearchSuggestionsResponse response = await request.ResponseAsync();

foreach (MusicCatalogSearchSuggestionsResponse.Suggestion s in response.Suggestions)
    using (s)
        Console.WriteLine($"{s.DisplayTerm} → searches for \"{s.SearchTerm}\"");
```

`response.Suggestions` is an `IReadOnlyList<MusicCatalogSearchSuggestionsResponse.Suggestion>`; each `Suggestion` has `DisplayTerm` and `SearchTerm` strings.

## Personal recommendations

`MusicPersonalRecommendationsRequest` has a public parameterless constructor:

```csharp
using var request = new MusicPersonalRecommendationsRequest
{
    Limit = 10,   // int?
    Offset = 0,   // int?
};
using MusicPersonalRecommendationsResponse response = await request.ResponseAsync();
```

`MusicPersonalRecommendation` items carry a `MusicItemCollection<MusicPersonalRecommendation.Item>`, where `Item` is a projected enum over `Album`, `Playlist`, `Station`.

## Recently played

`MusicRecentlyPlayedRequest<T>` is a generic request with a public parameterless constructor. `T` must be a recently-playable item type (`Song`, `MusicVideo`, `Track`, `Station`, `RecentlyPlayedMusicItem`, … — the types implementing `IMusicRecentlyPlayedRequestable`):

```csharp
using var request = new MusicRecentlyPlayedRequest<Song>();
using MusicRecentlyPlayedResponse<Song> response = await request.ResponseAsync();
```

`RecentlyPlayedMusicItem` is a projected enum over `Album`, `Playlist`, `Station` (`CaseTag`: `Album = 0`, `Playlist = 1`, `Station = 2`).

## Library search

`MusicLibrarySearchRequest` → `MusicLibrarySearchResponse` searches the user's own library (not the catalog).

```csharp
// request is a MusicLibrarySearchRequest you obtained (see "What you can and can't construct")
request.Limit = 25;          // int (note: non-nullable on this request)
string term = request.Term;

using MusicLibrarySearchResponse response = await request.ResponseAsync();

foreach (Song song in response.Songs)
    using (song) { /* … */ }
```

`MusicLibrarySearchResponse` exposes `Songs`, `Albums`, `Artists`, `Playlists`, `MusicVideos`, and `TopResults` as `MusicItemCollection<T>` (its `TopResult` is its own nested projected enum).

## Music items & collections

The catalog/library item types — `Song`, `Album`, `Artist`, `Playlist`, `MusicVideo`, `Station`, `Genre`, `Curator`, `RadioShow`, `RecordLabel` — all implement `IMusicItem` and expose read-only metadata properties. A representative slice:

**`Song`** — `Id` (`MusicItemID`), `Title` (`string`), `ArtistName` (`string`), `AlbumTitle` (`string?`), `ComposerName` (`string?`), `Duration` (`double?`, seconds), `TrackNumber` (`int?`), `DiscNumber` (`int?`), `PlayCount` (`int?`), `ContentRating` (`ContentRating?`), `Artwork` (`Artwork?`), `HasLyrics` (`bool`), `IsAppleDigitalMaster` (`bool?`), `Isrc` (`string?`), `WorkName` (`string?`), `MovementName`/`MovementNumber`/`MovementCount`.

**`Album`** — `Id`, `Title`, `ArtistName`, `TrackCount` (`int`), `RecordLabelName` (`string?`), `Copyright` (`string?`), `Upc` (`string?`), `ContentRating` (`ContentRating?`), `Artwork` (`Artwork?`), `EditorialNotes` (`EditorialNotes?`), `IsSingle`/`IsCompilation`/`IsComplete`/`IsAppleDigitalMaster` (`bool?`).

```csharp
using Song song = response.Songs[0];
Console.WriteLine($"{song.Title} — {song.ArtistName} ({song.Duration}s)");
if (song.Artwork is { } art)
    using (art)
        Console.WriteLine($"artwork {art.MaximumWidth}×{art.MaximumHeight}");
```

`MusicItemCollection<T>` is an `IReadOnlyList<T>` — index it, enumerate it, take `.Count`:

```csharp
MusicItemCollection<Song> songs = response.Songs;
int n = songs.Count;
Song first = songs[0];
string? collectionTitle = songs.Title;       // string?
bool more = songs.HasNextBatch;              // paged results available?
foreach (Song s in songs) { /* … */ }
```

It also exposes the Swift `Collection` index helpers (`Index`, `FormIndex`, `Distance`, `StartIndex`, `EndIndex`) in both `int` and `nint` overloads — concrete methods, not stubs (validated by the test app).

`Track` is a projected enum unifying `Song` and `MusicVideo`:

```csharp
// build
Track t = Track.Song(song);

// inspect
switch (t.Tag)                               // CaseTag: Song = 0, MusicVideo = 1
{
    case Track.CaseTag.Song:
        if (t.TryGetSong(out Song s)) using (s) { /* … */ }
        break;
    case Track.CaseTag.MusicVideo:
        if (t.TryGetMusicVideo(out MusicVideo mv)) using (mv) { /* … */ }
        break;
}
```

**`Artwork`** exposes `MaximumWidth` (`int`), `MaximumHeight` (`int`), `AlternateText` (`string?`).

## Playback

Two players are available, both `MusicPlayer` subclasses exposed as singletons:

- **`ApplicationMusicPlayer.Shared`** — plays within your app's own queue.
- **`SystemMusicPlayer.Shared`** — controls the system Music app's queue. **iOS only** — this type is not present in the macOS / Mac Catalyst / tvOS bindings (the test app skips it off-iOS).

### Transport controls

These live on the `MusicPlayer` base class, so they work on either player:

```csharp
ApplicationMusicPlayer player = ApplicationMusicPlayer.Shared;

await player.PrepareToPlayAsync();
await player.PlayAsync();
player.Pause();
player.Stop();
player.RestartCurrentEntry();
await player.SkipToNextEntryAsync();
await player.SkipToPreviousEntryAsync();
player.BeginSeekingForward();
player.BeginSeekingBackward();
player.EndSeeking();
```

Playback state and tunables (`virtual` properties on `MusicPlayer`):

| Member | Type | |
|---|---|---|
| `State` | `MusicPlayer.StateType` | live playback state object |
| `IsPreparedToPlay` | `bool` | |
| `PlaybackTime` | `double` | current position, seconds (get/set to seek) |
| `PlaybackStatus` | `MusicPlayer.PlaybackStatus` | `Stopped`/`Playing`/`Paused`/`Interrupted`/`SeekingForward`/`SeekingBackward` |
| `PlaybackRate` | `float` | |
| `RepeatMode` | `MusicPlayer.RepeatMode?` | `None`/`One`/`All` |
| `ShuffleMode` | `MusicPlayer.ShuffleMode?` | `Off`/`Songs` |
| `AudioVariant` | `AudioVariant?` | currently playing variant |

`MusicPlayer.StateType` mirrors the live values (`PlaybackStatus`, `PlaybackRate`, `RepeatMode`, `ShuffleMode`, `AudioVariant`):

```csharp
MusicPlayer.StateType state = player.State;
if (state.PlaybackStatus == MusicPlayer.PlaybackStatus.Playing) { /* … */ }
```

`PlaybackStatus` values: `Stopped = 0`, `Playing = 1`, `Paused`, `Interrupted`, `SeekingForward`, `SeekingBackward = 5`. `RepeatMode`: `None = 0`, `One = 1`, `All = 2`. `ShuffleMode`: `Off = 0`, `Songs = 1`.

### Building & setting the queue

`ApplicationMusicPlayer.Queue` is a settable `ApplicationMusicPlayer.QueueType` property. `QueueType` has two public constructors:

```csharp
// Play an album starting from a specific track:
using var queue = new ApplicationMusicPlayer.QueueType(album, startTrack);   // (Album, Track)
ApplicationMusicPlayer.Shared.Queue = queue;
await ApplicationMusicPlayer.Shared.PlayAsync();

// Or a playlist starting from a specific entry:
using var queue2 = new ApplicationMusicPlayer.QueueType(playlist, startPlaylistEntry); // (Playlist, Playlist.Entry)
```

The base `MusicPlayer.Queue` type can also be built from arrays of playable items via static factories (`FromSwift_SwiftArray_MusicKit_Song_MusicKit_Song`, `…_Album_…`, `…_Playlist_…`, `…_Track_…`) when you already hold a `Swift.SwiftArray<T>`.

You can insert entries into an existing queue (overloads accept a single item, an `IEnumerable<T>`, or a `Queue.Entry`, plus a position):

```csharp
await player.Queue.InsertAsync(song, MusicPlayer.Queue.EntryInsertionPosition.Tail);
```

`EntryInsertionPosition`: `AfterCurrentEntry = 0`, `Tail = 1`.

`QueueType.Entries` is an indexed collection of `MusicPlayer.Queue.Entry`; each `Entry` exposes `Id`, `Title`, `Subtitle` (`string?`), `Artwork` (`Artwork?`), `Item` (`MusicPlayer.Queue.Entry.ItemType?`, a `Song`/`MusicVideo` projected enum), `StartTime`/`EndTime` (`double?`), `IsTransient` (`bool`). The currently-playing entry is `player.Queue.CurrentEntry`.

### Crossfade transition (ApplicationMusicPlayer)

`ApplicationMusicPlayer.Transition` is a settable `MusicPlayer.Transition` (a projected enum: `None` singleton, or `Crossfade(CrossfadeOptions)`):

```csharp
ApplicationMusicPlayer.Shared.Transition = MusicPlayer.Transition.CrossfadeMethod(8.0); // 8s crossfade
// inspect:
if (player.Transition.TryGetCrossfade(out MusicPlayer.Transition.CrossfadeOptions opts)) using (opts) { }
```

## The user's library

`MusicLibrary.Shared` is the singleton for adding to / editing the user's library (requires authorization, and most operations require an Apple Music subscription).

```csharp
MusicLibrary library = MusicLibrary.Shared;

// Add a catalog item to the library:
await library.AddAsync(album);            // overloads: Album / MusicVideo / Playlist / Song / Track …

// Create a playlist:
using Playlist playlist = await library.CreatePlaylistAsync("Road Trip",
    description: "Summer 2026", authorDisplayName: "Me");

// Create a playlist seeded with items:
using Playlist seeded = await library.CreatePlaylistAsync("Faves", null, null,
    new[] { song1, song2 });              // IEnumerable<Song> (also Album/MusicVideo/Playlist/Track overloads)

// Add an item to an existing playlist (returns the updated playlist):
using Playlist updated = await library.AddAsync(song, playlist);

// Edit playlist metadata / contents:
using Playlist edited = await library.EditAsync(playlist, name: "New Name");
```

`MusicLibrary.Error` is a projected enum with singletons `Unknown`, `PermissionDenied`, `UnableToAddItem`, `ItemAlreadyAdded`, `PlaylistNotInLibrary`, `AddToPlaylistFailed`, `CreatePlaylistFailed`, `EditPlaylistFailed` (`CaseTag` `0`…`7`). Failed library calls throw a Swift error carrying one of these — catch the `Swift.Runtime.SwiftException` family.

> The `MusicLibraryRequest<T>` / `MusicLibrarySectionedRequest<…>` generic request types are present in the bindings for traversing the library by item type, with per-item-type extension classes (`MusicLibraryRequestMusicKit_SongCsmExtensions`, etc.). They're advanced surface; reach for `MusicLibrarySearchRequest` for straightforward lookups.

## Enums & descriptions

Plain Swift integer enums project to real C# enums and ship an `AllCases` list plus a `GetDescription()` extension:

```csharp
foreach (AudioVariant v in AudioVariantExtensions.AllCases)
    Console.WriteLine($"{v}: {v.GetDescription()}");

string atmos = AudioVariant.DolbyAtmos.GetDescription();
```

- **`AudioVariant`**: `DolbyAtmos = 0`, `DolbyAudio = 1`, `Lossless = 2`, `HighResolutionLossless = 3`, `LossyStereo = 4`, `SpatialAudio = 5` (the last requires iOS 17.2 / macOS 14.2). `AudioVariantExtensions.AllCases` + `GetDescription()`.
- **`ContentRating`**: `Clean = 0`, `Explicit = 1`.
- **`MusicCatalogChartKind`**: `MostPlayed = 0`, `CityTop = 1`, `DailyGlobalTop = 2`. `MusicCatalogChartKindExtensions.AllCases` + `GetDescription()`.
- **`MusicPropertySource`**: `Catalog = 0`, `Library = 1`. `MusicPropertySourceExtensions.AllCases`.
- **`Curator.KindType`**, **`Playlist.KindType`**: nested plain enums.

`MusicTokenRequestError` is a projected enum (singletons `Unknown`, `PermissionDenied`, `UserTokenRevoked`, `UserNotSignedIn`, `PrivacyAcknowledgementRequired`, `DeveloperTokenRequestFailed`, `UserTokenRequestFailed`; `CaseTag` `0`…`6`) used by the token-provider types (`MusicUserTokenProvider`, `DefaultMusicTokenProvider`, `MusicTokenRequestOptions`).

## What you can and can't construct

This is the most important practical caveat for MusicKit's request types. Swift constructs most requests with an `init` that takes a search term and an array of result-kind metatypes (`[any MusicCatalogSearchable.Type]`). The generator can't emit those metatype-array initializers directly, so the package ships hand-authored **`Create(term, types)` factory shims** (in `Shims/MusicKitShims.cs`, backed by `@_cdecl` trampolines) that take a `[Flags]` bitmask instead of the Swift metatype array:

| Request type | Constructible from C#? | How |
|---|---|---|
| `MusicCatalogSearchSuggestionsRequest` | ✅ | `MusicCatalogSearchSuggestionsRequest.Create_C11D4260(term)` or `.Create(term, MusicCatalogSearchTypes)` |
| `MusicPersonalRecommendationsRequest` | ✅ | `new MusicPersonalRecommendationsRequest()` |
| `MusicRecentlyPlayedRequest<T>` | ✅ | `new MusicRecentlyPlayedRequest<T>()` |
| `MusicCatalogResourceRequest<T>` | ✅ | `new MusicCatalogResourceRequest<T>()` |
| `MusicCatalogSearchRequest` | ✅ | `MusicCatalogSearchRequest.Create(term, MusicCatalogSearchTypes)` (array-shim factory) |
| `MusicLibrarySearchRequest` | ✅ | `MusicLibrarySearchRequest.Create(term, MusicLibrarySearchTypes)` (array-shim factory) |
| `MusicCatalogChartsRequest` | ❌ | **no public constructor emitted, and no shim** — configure (`Limit`/`Offset`/`IncludeTopResults`) and call `ResponseAsync()` only on an instance you already hold |

```csharp
// Catalog term search — construct via the array-shim factory:
using var req = MusicCatalogSearchRequest.Create("daft punk",
    MusicCatalogSearchTypes.Album | MusicCatalogSearchTypes.Artist);
```

The bitmask is validated: a value carrying bits outside the corresponding `MusicCatalogSearchTypes` / `MusicLibrarySearchTypes` enum throws `ArgumentException` with a diagnostic stray-bit hex string. The response surface (`Songs`, `Albums`, …) and `ResponseAsync()` are usable once you hold an instance. Only `MusicCatalogChartsRequest` still lacks any construction path.

> **What's validated.** The test app exercises authorization status + singletons, `MusicSubscription.GetCurrentAsync()` and its property reads, the player singletons (`ApplicationMusicPlayer.Shared`, `SystemMusicPlayer.Shared` on iOS), `MusicLibrary.Shared`, all the plain/projected enum tags and `GetDescription()` round-trips, `MusicItemCollection<Song>`'s index ergonomics, and type-metadata loads for the core item and request types. Live catalog/library *content* (running an actual search, reading real `Song`/`Album` data, mutating the library, playing audio) requires the MusicKit entitlement + an authorized, subscribed device and is **not** exercised by the test app — those code paths are accurate to the generated signatures but unverified at runtime here.

## Memory & threading

- **Disposal.** Almost every MusicKit type is an `ISwiftObject` wrapping a Swift struct/class and implements `IDisposable`. Use `using` / `using var` for deterministic cleanup — `Dispose` is safe on every generated type and double-Dispose is a no-op. This matters most for items pulled out of a `MusicItemCollection` in a loop and for the response objects.

  ```csharp
  using var sub = await MusicSubscription.GetCurrentAsync();
  foreach (Song s in response.Songs) using (s) { /* … */ }
  ```

  The static singletons (`MusicLibrary.Shared`, `ApplicationMusicPlayer.Shared`, `SystemMusicPlayer.Shared`) and the `MusicAuthorization.Status` singletons are framework-owned — read them, don't dispose them.

- **Async.** Every `…Async` method returns a `Task`/`Task<T>` and accepts a trailing defaulted `CancellationToken`, so you can cancel an in-flight call. Continuations resume on a thread-pool thread — marshal back to your UI thread before touching UI.

- **Async sequences.** `MusicSubscription.SubscriptionUpdates` is an `IAsyncEnumerable<MusicSubscription>` and is effectively infinite; enumerate it on a long-lived background task keyed to your app's lifetime, not a screen's.

- **Sendable.** Types the Swift framework marks `Sendable` (e.g. `MusicAuthorization.Status`, `EditorialNotes`) carry `[SwiftSendable]` and may be shared across .NET threads without external synchronization.

## Reference links

- [Apple — MusicKit](https://developer.apple.com/documentation/musickit)
- [Apple — Requesting authorization to access Apple Music](https://developer.apple.com/documentation/musickit/musicauthorization)
- [Apple — MusicCatalogSearchRequest](https://developer.apple.com/documentation/musickit/musiccatalogsearchrequest)
- [Apple — ApplicationMusicPlayer](https://developer.apple.com/documentation/musickit/applicationmusicplayer)
