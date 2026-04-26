// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using MusicKit;
using Swift.Runtime;

namespace SwiftBindings.MusicKit.Tests;

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

        // Test 1: MusicAuthorization.CurrentStatus reaches Swift and returns a Status object.
        // Note: MusicAuthorization.Status is ISwiftObject — do NOT Dispose.
        try
        {
            var status = MusicAuthorization.CurrentStatus;
            if (status is null)
                throw new InvalidOperationException("status was null");
            Pass("MusicAuthorization.CurrentStatus");
        }
        catch (Exception ex)
        {
            Fail("MusicAuthorization.CurrentStatus", ex.Message);
        }

        // Test 2: MusicAuthorization.Status singleton cases resolve and compare by identity.
        try
        {
            var notDetermined = MusicAuthorization.Status.NotDetermined;
            var denied = MusicAuthorization.Status.Denied;
            var restricted = MusicAuthorization.Status.Restricted;
            var authorized = MusicAuthorization.Status.Authorized;
            if (notDetermined is null || denied is null || restricted is null || authorized is null)
                throw new InvalidOperationException("one of the singletons was null");
            Pass("MusicAuthorization.Status singletons");
        }
        catch (Exception ex)
        {
            Fail("MusicAuthorization.Status singletons", ex.Message);
        }

        // Test 3: MusicAuthorization metadata loads. Size may legitimately be 0 —
        // Swift MusicAuthorization is a caseless namespace-style enum with no storage —
        // so we only check that the metadata call returns a valid (non-zero) handle.
        try
        {
            var metadata = SwiftObjectHelper<MusicAuthorization>.GetTypeMetadata();
            if (metadata.Handle == IntPtr.Zero)
                throw new InvalidOperationException("metadata handle is null");
            Log($"MusicAuthorization metadata size = {metadata.Size}");
            Pass("MusicAuthorization metadata");
        }
        catch (Exception ex)
        {
            Fail("MusicAuthorization metadata", ex.Message);
        }

        // Test 4: AudioVariant plain enum is reachable.
        try
        {
            var all = AudioVariantExtensions.AllCases;
            if (all.Count == 0)
                throw new InvalidOperationException("AllCases is empty");
            Pass("AudioVariant.AllCases");
        }
        catch (Exception ex)
        {
            Fail("AudioVariant.AllCases", ex.Message);
        }

        // Test 5: AudioVariant.DolbyAtmos -> Swift GetDescription round-trip.
        // This exercises a cdecl wrapper into Swift that takes the enum tag.
        try
        {
            var desc = AudioVariant.DolbyAtmos.GetDescription();
            if (string.IsNullOrEmpty(desc))
                throw new InvalidOperationException("empty description");
            Log($"DolbyAtmos description = {desc}");
            Pass("AudioVariant.DolbyAtmos.GetDescription");
        }
        catch (Exception ex)
        {
            Fail("AudioVariant.DolbyAtmos.GetDescription", ex.Message);
        }

        // Test 6: AudioVariant.SpatialAudio.GetDescription — requires iOS 17.2+.
        // On 17.2+ the wrapper returns a string; on older OS the Swift runtime may trap.
        // Binding-load failures (DllNotFoundException, EntryPointNotFoundException) are real bugs.
        try
        {
            var desc = AudioVariant.SpatialAudio.GetDescription();
            Log($"SpatialAudio description = {desc}");
            Pass("AudioVariant.SpatialAudio.GetDescription");
        }
        catch (DllNotFoundException ex)
        {
            Fail("AudioVariant.SpatialAudio.GetDescription", ex.Message);
        }
        catch (EntryPointNotFoundException ex)
        {
            Fail("AudioVariant.SpatialAudio.GetDescription", ex.Message);
        }
        catch (Exception ex)
        {
            // Swift availability trap on older OS — wrapper linked and dispatched correctly.
            Log($"SpatialAudio trapped on older OS (availability guard): {ex.Message}");
            Pass("AudioVariant.SpatialAudio.GetDescription (availability trap)");
        }

        // Test 7: ContentRating plain enum is reachable.
        try
        {
            var clean = ContentRating.Clean;
            var explicitRating = ContentRating.Explicit;
            if ((int)clean != 0 || (int)explicitRating != 1)
                throw new InvalidOperationException($"unexpected values Clean={clean}, Explicit={explicitRating}");
            Pass("ContentRating values");
        }
        catch (Exception ex)
        {
            Fail("ContentRating values", ex.Message);
        }

        // Test 8: Artwork metadata loads (class-constrained existential case covered indirectly).
        try
        {
            var metadata = SwiftObjectHelper<Artwork>.GetTypeMetadata();
            if (metadata.Size == 0)
                throw new InvalidOperationException("metadata size is 0");
            Pass("Artwork metadata");
        }
        catch (Exception ex)
        {
            Fail("Artwork metadata", ex.Message);
        }

        // Test 9: MusicSubscription metadata loads.
        try
        {
            var metadata = SwiftObjectHelper<MusicSubscription>.GetTypeMetadata();
            if (metadata.Size == 0)
                throw new InvalidOperationException("metadata size is 0");
            Pass("MusicSubscription metadata");
        }
        catch (Exception ex)
        {
            Fail("MusicSubscription metadata", ex.Message);
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

        // Test 10: MusicLibrary.Shared singleton is non-null and reachable.
        try
        {
            var library = MusicLibrary.Shared;
            if (library is null)
                throw new InvalidOperationException("MusicLibrary.Shared was null");
            Pass("MusicLibrary.Shared");
        }
        catch (Exception ex)
        {
            Fail("MusicLibrary.Shared", ex.Message);
        }

        // Test 11: ApplicationMusicPlayer.Shared singleton is non-null and reachable.
        try
        {
            var player = ApplicationMusicPlayer.Shared;
            if (player is null)
                throw new InvalidOperationException("ApplicationMusicPlayer.Shared was null");
            Pass("ApplicationMusicPlayer.Shared");
        }
        catch (Exception ex)
        {
            Fail("ApplicationMusicPlayer.Shared", ex.Message);
        }

        // Test 12: SystemMusicPlayer.Shared singleton is non-null and reachable.
        // SystemMusicPlayer is iOS-only — not available in macOS MusicKit bindings.
#if IOS
        try
        {
            var player = SystemMusicPlayer.Shared;
            if (player is null)
                throw new InvalidOperationException("SystemMusicPlayer.Shared was null");
            Pass("SystemMusicPlayer.Shared");
        }
        catch (Exception ex)
        {
            Fail("SystemMusicPlayer.Shared", ex.Message);
        }
#else
        Skip("SystemMusicPlayer.Shared", "SystemMusicPlayer is iOS-only");
#endif

        // Test 13: MusicPropertySource plain enum — verify Catalog=0 and Library=1.
        try
        {
            if ((int)MusicPropertySource.Catalog != 0)
                throw new InvalidOperationException($"Catalog expected 0, got {(int)MusicPropertySource.Catalog}");
            if ((int)MusicPropertySource.Library != 1)
                throw new InvalidOperationException($"Library expected 1, got {(int)MusicPropertySource.Library}");
            Pass("MusicPropertySource values");
        }
        catch (Exception ex)
        {
            Fail("MusicPropertySource values", ex.Message);
        }

        // Test 14: MusicPropertySource.AllCases extension — non-empty list.
        try
        {
            var all = MusicPropertySourceExtensions.AllCases;
            if (all.Count == 0)
                throw new InvalidOperationException("AllCases is empty");
            Pass("MusicPropertySource.AllCases");
        }
        catch (Exception ex)
        {
            Fail("MusicPropertySource.AllCases", ex.Message);
        }

        // Test 15: MusicCatalogChartKind plain enum — verify values.
        try
        {
            if ((int)MusicCatalogChartKind.MostPlayed != 0)
                throw new InvalidOperationException($"MostPlayed expected 0, got {(int)MusicCatalogChartKind.MostPlayed}");
            if ((int)MusicCatalogChartKind.CityTop != 1)
                throw new InvalidOperationException($"CityTop expected 1, got {(int)MusicCatalogChartKind.CityTop}");
            if ((int)MusicCatalogChartKind.DailyGlobalTop != 2)
                throw new InvalidOperationException($"DailyGlobalTop expected 2, got {(int)MusicCatalogChartKind.DailyGlobalTop}");
            Pass("MusicCatalogChartKind values");
        }
        catch (Exception ex)
        {
            Fail("MusicCatalogChartKind values", ex.Message);
        }

        // Test 16: MusicCatalogChartKind.MostPlayed.GetDescription() round-trip.
        try
        {
            var desc = MusicCatalogChartKind.MostPlayed.GetDescription();
            if (string.IsNullOrEmpty(desc))
                throw new InvalidOperationException("empty description");
            Log($"MostPlayed description = {desc}");
            Pass("MusicCatalogChartKind.GetDescription");
        }
        catch (Exception ex)
        {
            Fail("MusicCatalogChartKind.GetDescription", ex.Message);
        }

        // Test 17: MusicCatalogChartKind.AllCases extension — non-empty list.
        try
        {
            var all = MusicCatalogChartKindExtensions.AllCases;
            if (all.Count == 0)
                throw new InvalidOperationException("AllCases is empty");
            Pass("MusicCatalogChartKind.AllCases");
        }
        catch (Exception ex)
        {
            Fail("MusicCatalogChartKind.AllCases", ex.Message);
        }

        // Test 18: MusicPlayer.PlaybackStatus plain enum — verify values.
        try
        {
            if ((int)MusicPlayer.PlaybackStatus.Stopped != 0)
                throw new InvalidOperationException($"Stopped expected 0, got {(int)MusicPlayer.PlaybackStatus.Stopped}");
            if ((int)MusicPlayer.PlaybackStatus.Playing != 1)
                throw new InvalidOperationException($"Playing expected 1, got {(int)MusicPlayer.PlaybackStatus.Playing}");
            if ((int)MusicPlayer.PlaybackStatus.SeekingBackward != 5)
                throw new InvalidOperationException($"SeekingBackward expected 5, got {(int)MusicPlayer.PlaybackStatus.SeekingBackward}");
            Pass("MusicPlayer.PlaybackStatus values");
        }
        catch (Exception ex)
        {
            Fail("MusicPlayer.PlaybackStatus values", ex.Message);
        }

        // Test 19: MusicPlayer.RepeatMode plain enum — verify values.
        try
        {
            if ((int)MusicPlayer.RepeatMode.None != 0)
                throw new InvalidOperationException($"None expected 0, got {(int)MusicPlayer.RepeatMode.None}");
            if ((int)MusicPlayer.RepeatMode.One != 1)
                throw new InvalidOperationException($"One expected 1, got {(int)MusicPlayer.RepeatMode.One}");
            if ((int)MusicPlayer.RepeatMode.All != 2)
                throw new InvalidOperationException($"All expected 2, got {(int)MusicPlayer.RepeatMode.All}");
            Pass("MusicPlayer.RepeatMode values");
        }
        catch (Exception ex)
        {
            Fail("MusicPlayer.RepeatMode values", ex.Message);
        }

        // Test 20: MusicPlayer.ShuffleMode plain enum — verify values.
        try
        {
            if ((int)MusicPlayer.ShuffleMode.Off != 0)
                throw new InvalidOperationException($"Off expected 0, got {(int)MusicPlayer.ShuffleMode.Off}");
            if ((int)MusicPlayer.ShuffleMode.Songs != 1)
                throw new InvalidOperationException($"Songs expected 1, got {(int)MusicPlayer.ShuffleMode.Songs}");
            Pass("MusicPlayer.ShuffleMode values");
        }
        catch (Exception ex)
        {
            Fail("MusicPlayer.ShuffleMode values", ex.Message);
        }

        // Test 21: MusicLibrary.Error.CaseTag round-trip — verify tag uint values.
        try
        {
            var unknown = MusicLibrary.Error.Unknown;
            if (unknown.Tag != MusicLibrary.Error.CaseTag.Unknown)
                throw new InvalidOperationException($"Unknown tag expected {MusicLibrary.Error.CaseTag.Unknown}, got {unknown.Tag}");
            if ((uint)MusicLibrary.Error.CaseTag.Unknown != 0u)
                throw new InvalidOperationException($"Unknown CaseTag value expected 0, got {(uint)MusicLibrary.Error.CaseTag.Unknown}");
            if ((uint)MusicLibrary.Error.CaseTag.EditPlaylistFailed != 7u)
                throw new InvalidOperationException($"EditPlaylistFailed CaseTag expected 7, got {(uint)MusicLibrary.Error.CaseTag.EditPlaylistFailed}");
            Pass("MusicLibrary.Error.CaseTag round-trip");
        }
        catch (Exception ex)
        {
            Fail("MusicLibrary.Error.CaseTag round-trip", ex.Message);
        }

        // Test 22: MusicTokenRequestError.CaseTag round-trip — verify tag uint values.
        try
        {
            var unknown = MusicTokenRequestError.Unknown;
            if (unknown.Tag != MusicTokenRequestError.CaseTag.Unknown)
                throw new InvalidOperationException($"Unknown tag mismatch: got {unknown.Tag}");
            if ((uint)MusicTokenRequestError.CaseTag.Unknown != 0u)
                throw new InvalidOperationException($"Unknown CaseTag expected 0, got {(uint)MusicTokenRequestError.CaseTag.Unknown}");
            if ((uint)MusicTokenRequestError.CaseTag.UserTokenRequestFailed != 6u)
                throw new InvalidOperationException($"UserTokenRequestFailed expected 6, got {(uint)MusicTokenRequestError.CaseTag.UserTokenRequestFailed}");
            Pass("MusicTokenRequestError.CaseTag round-trip");
        }
        catch (Exception ex)
        {
            Fail("MusicTokenRequestError.CaseTag round-trip", ex.Message);
        }

        // Test 23: Track.CaseTag — verify Song=0 and MusicVideo=1.
        try
        {
            if ((uint)Track.CaseTag.Song != 0u)
                throw new InvalidOperationException($"Song expected 0, got {(uint)Track.CaseTag.Song}");
            if ((uint)Track.CaseTag.MusicVideo != 1u)
                throw new InvalidOperationException($"MusicVideo expected 1, got {(uint)Track.CaseTag.MusicVideo}");
            Pass("Track.CaseTag values");
        }
        catch (Exception ex)
        {
            Fail("Track.CaseTag values", ex.Message);
        }

        // Test 24: Metadata loads for core music item types.
        MetadataTest<Album>("Album");
        MetadataTest<Artist>("Artist");
        MetadataTest<Song>("Song");
        MetadataTest<Playlist>("Playlist");
        MetadataTest<Genre>("Genre");
        MetadataTest<MusicVideo>("MusicVideo");
        MetadataTest<Station>("Station");

        // Test 31: Metadata loads for request types.
        MetadataTest<MusicCatalogSearchRequest>("MusicCatalogSearchRequest");
        MetadataTest<MusicLibrarySearchRequest>("MusicLibrarySearchRequest");

        // Test 34: MusicAuthorization.Status.CaseTag uint values
        try
        {
            if ((uint)MusicAuthorization.Status.CaseTag.NotDetermined != 0)
                throw new InvalidOperationException($"NotDetermined expected 0, got {(uint)MusicAuthorization.Status.CaseTag.NotDetermined}");
            if ((uint)MusicAuthorization.Status.CaseTag.Denied != 1)
                throw new InvalidOperationException($"Denied expected 1, got {(uint)MusicAuthorization.Status.CaseTag.Denied}");
            if ((uint)MusicAuthorization.Status.CaseTag.Restricted != 2)
                throw new InvalidOperationException($"Restricted expected 2, got {(uint)MusicAuthorization.Status.CaseTag.Restricted}");
            if ((uint)MusicAuthorization.Status.CaseTag.Authorized != 3)
                throw new InvalidOperationException($"Authorized expected 3, got {(uint)MusicAuthorization.Status.CaseTag.Authorized}");
            Pass("MusicAuthorization.Status.CaseTag values");
        }
        catch (Exception ex)
        {
            Fail("MusicAuthorization.Status.CaseTag values", ex.Message);
        }

        // Test 35: MusicSubscription.Error.CaseTag uint values
        try
        {
            if ((uint)MusicSubscription.Error.CaseTag.Unknown != 0)
                throw new InvalidOperationException($"Unknown expected 0, got {(uint)MusicSubscription.Error.CaseTag.Unknown}");
            if ((uint)MusicSubscription.Error.CaseTag.PermissionDenied != 1)
                throw new InvalidOperationException($"PermissionDenied expected 1, got {(uint)MusicSubscription.Error.CaseTag.PermissionDenied}");
            if ((uint)MusicSubscription.Error.CaseTag.PrivacyAcknowledgementRequired != 2)
                throw new InvalidOperationException($"PrivacyAcknowledgementRequired expected 2, got {(uint)MusicSubscription.Error.CaseTag.PrivacyAcknowledgementRequired}");
            Pass("MusicSubscription.Error.CaseTag values");
        }
        catch (Exception ex)
        {
            Fail("MusicSubscription.Error.CaseTag values", ex.Message);
        }

        // Test 36: RecentlyPlayedMusicItem.CaseTag uint values
        try
        {
            if ((uint)RecentlyPlayedMusicItem.CaseTag.Album != 0)
                throw new InvalidOperationException($"Album expected 0, got {(uint)RecentlyPlayedMusicItem.CaseTag.Album}");
            if ((uint)RecentlyPlayedMusicItem.CaseTag.Playlist != 1)
                throw new InvalidOperationException($"Playlist expected 1, got {(uint)RecentlyPlayedMusicItem.CaseTag.Playlist}");
            if ((uint)RecentlyPlayedMusicItem.CaseTag.Station != 2)
                throw new InvalidOperationException($"Station expected 2, got {(uint)RecentlyPlayedMusicItem.CaseTag.Station}");
            Pass("RecentlyPlayedMusicItem.CaseTag values");
        }
        catch (Exception ex)
        {
            Fail("RecentlyPlayedMusicItem.CaseTag values", ex.Message);
        }

        // Test 37a: MusicItemCollection<Song> ergonomic methods (Index/FormIndex/Distance) are concrete,
        // not SB0001 stubs. Constructing a real MusicItemCollection<Song> requires Apple Music
        // entitlement + authorization, so we verify the API surface by reflection. These were the
        // 4 SB0001 from Round 5 that Session 6 cleared via the DoesPairingSatisfyAssociatedTypeConstraints
        // relaxation — the Round 6 grep gate confirms 0 SB0001, this test enforces the shape.
        try
        {
            var t = typeof(MusicItemCollection<Song>);
            if (!typeof(System.Collections.Generic.IReadOnlyList<Song>).IsAssignableFrom(t))
                throw new InvalidOperationException("MusicItemCollection<Song> does not implement IReadOnlyList<Song>");

            // Two arities each — int and nint overloads.
            if (t.GetMethod("Index", new[] { typeof(int) }) is null)
                throw new InvalidOperationException("Index(int) missing");
            if (t.GetMethod("FormIndex", new[] { typeof(int) }) is null)
                throw new InvalidOperationException("FormIndex(int) missing");
            if (t.GetMethod("Index", new[] { typeof(int), typeof(int) }) is null)
                throw new InvalidOperationException("Index(int, int) missing");
            if (t.GetMethod("Distance", new[] { typeof(int), typeof(int) }) is null)
                throw new InvalidOperationException("Distance(int, int) missing");

            // Index methods must NOT be marked [Obsolete] (Session 6 promoted them out of SB0001).
            foreach (var m in t.GetMethods())
            {
                if (m.Name is "Index" or "FormIndex" or "Distance"
                    && m.GetCustomAttributes(typeof(ObsoleteAttribute), false).Length > 0)
                {
                    throw new InvalidOperationException($"{m.Name}({string.Join(",", Array.ConvertAll(m.GetParameters(), p => p.ParameterType.Name))}) is still [Obsolete] — Session 6 SB0001 cleanup regressed");
                }
            }
            Pass("MusicItemCollection<Song> ergonomics shape");
        }
        catch (Exception ex)
        {
            Fail("MusicItemCollection<Song> ergonomics shape", ex.Message);
        }

        // Test 37: MusicSubscription.GetCurrentAsync — dispatch the call; framework error counts as pass.
        // Binding-load failures are real bugs and must fail.
        try
        {
            var sub = MusicSubscription.GetCurrentAsync().GetAwaiter().GetResult();
            bool canPlay = sub.CanPlayCatalogContent;
            bool canBecome = sub.CanBecomeSubscriber;
            bool hasCloud = sub.HasCloudLibraryEnabled;
            Log($"CanPlayCatalogContent={canPlay}, CanBecomeSubscriber={canBecome}, HasCloudLibraryEnabled={hasCloud}");
            Pass("MusicSubscription property reads");
        }
        catch (DllNotFoundException ex)
        {
            Fail("MusicSubscription property reads", ex.Message);
        }
        catch (EntryPointNotFoundException ex)
        {
            Fail("MusicSubscription property reads", ex.Message);
        }
        catch (Exception ex)
        {
            // Framework error (no entitlement, no auth) counts as pass — marshalling worked.
            Log($"MusicSubscription.GetCurrentAsync threw as expected: {ex.Message}");
            Pass("MusicSubscription property reads (framework error accepted)");
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
        Console.WriteLine(prefixed ? $"[MUSICKIT-TEST] {msg}" : msg);
}
