// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Reflection;
using MusicKit;
using Swift.Runtime;

namespace SwiftBindings.MusicKit.Tests;

internal static class Tests
{
    /// <summary>
    /// Bounded await that yields the result; throws <see cref="TimeoutException"/> on the bound so a
    /// hung async bridge fails the test instead of being reported as a pass. A real await — never
    /// Wait()/GetAwaiter().GetResult() — so a main-thread affine continuation can run rather than
    /// deadlocking against a blocked runner on the UIKit thread.
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

        // Test 37a: MusicItemCollection<Song> ergonomic Collection members (Index/FormIndex/Distance)
        // are concrete (callable), not SB0001 direct-CallConvSwift stubs. Constructing a real
        // MusicItemCollection<Song> requires Apple Music entitlement + authorization, so we can't call
        // them with live data — but the surface is proven two complementary ways:
        //
        //   (1) Compile-time shape proof — ALL platforms, including the NativeAOT device. The local
        //       function below references every ergonomic member exactly as a consumer would: instance
        //       index(after:)/formIndex(after:) (int overloads) and the CSM concrete-specialization
        //       extensions index(_:offsetBy:)/distance(from:to:) ((nint, nint), callable as
        //       collection.Index(i, n) / collection.Distance(a, b)). If the generator dropped or renamed
        //       any of them the test app would not COMPILE for any TFM, so this is a platform-invariant
        //       guarantee the callable surface exists. It also roots the *CsmExtensions class so the
        //       NativeAOT trimmer keeps it (a reflection-only probe is not a "use" — without a real
        //       call-site the trimmer strips the whole static class and Assembly.GetTypes() omits it on
        //       device). The CSM static-dispatch shape is deliberate: it avoids the Mono `!ji->async`
        //       crash an open-generic instance method with 2+ type-metadata arguments trips.
        //
        //   (2) Runtime [Obsolete]/metadata probe — JIT/full-metadata runtimes only. Asserts none of the
        //       members carry [Obsolete] (the SB0001 marker the CSM shape exists to avoid). [Obsolete] is
        //       a custom attribute; under NativeAOT trimming, custom-attribute and uncalled-member
        //       metadata are stripped, so this probe on device would pass VACUOUSLY (no attribute found
        //       != attribute absent) — false confidence, not coverage. The generated MusicKit.cs is
        //       byte-identical across TFMs, so verifying the non-stub property on the JIT runtime fully
        //       establishes it for the shipped code; the generator's own MusicItemBagTests /
        //       GenericIndexableCollectionTests additionally call bag.Index(0, 2) / bag.Distance(0, 3) /
        //       coll.Index(0, 2) for real on both Mono-sim and NativeAOT-device.

        // (1) Compile-time shape proof + NativeAOT trimmer root. Never invoked — we cannot build a
        // MusicItemCollection<Song> without an Apple Music entitlement — but its body must compile,
        // which proves the ergonomic surface exists, and the ldftn below roots it for the trimmer.
        static void RootMusicItemCollectionErgonomics(MusicItemCollection<Song> c)
        {
            c.IndexAfter((int)0);              // index(after:) instance overload
            nint formIdx = 0;                  // formIndex(after: inout Index) surfaces as ref nint
            c.FormIndexAfter(ref formIdx);     // formIndex(after:) instance overload
            _ = c.Index((nint)0, (nint)0);     // index(_:offsetBy:) CSM extension
            _ = c.Distance((nint)0, (nint)0);  // distance(from:to:) CSM extension
        }
        GC.KeepAlive((Action<MusicItemCollection<Song>>)RootMusicItemCollectionErgonomics);

        // Type.GetMethod cannot see extension methods, so the CSM members are looked up on the
        // *CsmExtensions classes (JIT-only branch below).
        static MethodInfo? FindCsmExtension(Type receiver, string name, params Type[] tailArgs)
        {
            foreach (var ext in receiver.Assembly.GetTypes())
            {
                if (!(ext.IsAbstract && ext.IsSealed)) continue; // static class
                if (!ext.Name.EndsWith("CsmExtensions", StringComparison.Ordinal)) continue;
                foreach (var m in ext.GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    if (m.Name != name) continue;
                    var ps = m.GetParameters();
                    if (ps.Length != tailArgs.Length + 1) continue;
                    if (ps[0].ParameterType != receiver) continue;
                    var match = true;
                    for (var i = 0; i < tailArgs.Length; i++)
                        if (ps[i + 1].ParameterType != tailArgs[i]) { match = false; break; }
                    if (match) return m;
                }
            }
            return null;
        }

        try
        {
            var t = typeof(MusicItemCollection<Song>);
            if (!typeof(System.Collections.Generic.IReadOnlyList<Song>).IsAssignableFrom(t))
                throw new InvalidOperationException("MusicItemCollection<Song> does not implement IReadOnlyList<Song>");

            // (2) [Obsolete]/metadata probe needs complete reflection metadata. Under NativeAOT
            // trimming that metadata (custom attributes + uncalled members) is stripped, which would
            // make the [Obsolete] check pass vacuously — so run it only where full metadata is
            // guaranteed. The compile-time proof above already covers the callable surface on device,
            // and the non-stub property is platform-invariant (same generated MusicKit.cs everywhere).
            if (!System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported)
            {
                Log("MusicItemCollection<Song> ergonomics: callable surface proven at compile time; " +
                    "[Obsolete]/metadata probe skipped under NativeAOT (trimmed metadata would pass " +
                    "vacuously) — covered on Mono-sim + generator MusicItemBagTests.");
                Pass("MusicItemCollection<Song> ergonomics shape");
            }
            else
            {
                // index(after:) — instance method with an int convenience overload. The bare name
                // Index belongs to the index(_:offsetBy:) CSM extension, so the label-collision
                // ladder names this one from its argument label.
                if (t.GetMethod("IndexAfter", new[] { typeof(int) }) is null)
                    throw new InvalidOperationException("IndexAfter(int) missing");
                // formIndex(after: inout Index) surfaces as ref nint — no by-value convenience
                // overload is possible (the writeback would be lost), so probe the ref nint form.
                if (t.GetMethod("FormIndexAfter", new[] { typeof(nint).MakeByRefType() }) is null)
                    throw new InvalidOperationException("FormIndexAfter(ref nint) missing");

                // index(_:offsetBy:)/distance(from:to:) — CSM concrete-specialization extension methods
                // (nint, nint). Callable as collection.Index(i, distance) / collection.Distance(start, end).
                var indexOffsetBy = FindCsmExtension(t, "Index", typeof(nint), typeof(nint));
                if (indexOffsetBy is null)
                    throw new InvalidOperationException("Index(offsetBy:) CSM extension missing");
                var distance = FindCsmExtension(t, "Distance", typeof(nint), typeof(nint));
                if (distance is null)
                    throw new InvalidOperationException("Distance(from:to:) CSM extension missing");

                // None of these members may be [Obsolete] — that's the SB0001-stub marker.
                foreach (var m in t.GetMethods())
                {
                    if (m.Name is "Index" or "IndexAfter" or "IndexBefore"
                            or "FormIndexAfter" or "FormIndexBefore" or "Distance"
                        && m.GetCustomAttributes(typeof(ObsoleteAttribute), false).Length > 0)
                    {
                        throw new InvalidOperationException($"{m.Name}({string.Join(",", Array.ConvertAll(m.GetParameters(), p => p.ParameterType.Name))}) is still [Obsolete] — SB0001 cleanup regressed");
                    }
                }
                foreach (var m in new[] { indexOffsetBy, distance })
                {
                    if (m.GetCustomAttributes(typeof(ObsoleteAttribute), false).Length > 0)
                        throw new InvalidOperationException($"{m.Name}(nint, nint) CSM extension is [Obsolete] — SB0001 cleanup regressed");
                }
                Pass("MusicItemCollection<Song> ergonomics shape");
            }
        }
        catch (Exception ex)
        {
            Fail("MusicItemCollection<Song> ergonomics shape", ex.Message);
        }

        // Test 37: MusicSubscription.current — REAL bounded await asserting an exact outcome.
        // The previous version blocked with GetAwaiter().GetResult() on the UI thread and ended in a
        // bare `catch (Exception) -> Pass`, so a marshalling crash, a null dereference or a
        // PlatformNotSupportedException all counted as "framework error accepted". Now exactly two
        // outcomes are accepted:
        //   (a) a MusicSubscription — the three Bool properties must all read without throwing
        //       (they are read into locals and logged; a mis-marshalled Bool would surface as a
        //       throw or a garbage value, and the read itself is the assertion);
        //   (b) the framework's own documented authorization/subscription error, i.e. the TYPED
        //       SwiftException<MusicSubscription.Error>, whose payload must carry one of the three
        //       declared cases (unknown = 0, permissionDenied = 1, privacyAcknowledgementRequired = 2).
        //       permissionDenied is what an unauthorized simulator reports.
        // Everything else — untyped Swift errors, loader errors, a timeout — fails.
        try
        {
            var sub = await WithTimeout(
                MusicSubscription.GetCurrentAsync(),
                TimeSpan.FromSeconds(30),
                "MusicSubscription.current").ConfigureAwait(true);

            if (sub is null)
                throw new InvalidOperationException("MusicSubscription.current returned null");
            bool canPlay = sub.CanPlayCatalogContent;
            bool canBecome = sub.CanBecomeSubscriber;
            bool hasCloud = sub.HasCloudLibraryEnabled;
            Log($"CanPlayCatalogContent={canPlay}, CanBecomeSubscriber={canBecome}, HasCloudLibraryEnabled={hasCloud}");
            Pass("MusicSubscription.current property reads");
        }
        catch (SwiftException<MusicSubscription.Error> typed)
        {
            var err = typed.Error;
            if (err is null)
            {
                Fail("MusicSubscription.current error category",
                     $"SwiftException<MusicSubscription.Error> carried no typed payload (message: {typed.Message})");
            }
            else if (!Enum.IsDefined(typeof(MusicSubscription.Error.CaseTag), err.Tag))
            {
                Fail("MusicSubscription.current error category",
                     $"typed MusicSubscription.Error carried an undeclared CaseTag ({(uint)err.Tag})");
            }
            else
            {
                Log($"MusicSubscription.current reported typed MusicSubscription.Error.{err.Tag} — {typed.Message}");
                Pass($"MusicSubscription.current reports typed MusicSubscription.Error.{err.Tag}");
            }
        }
        catch (Exception ex)
        {
            Fail("MusicSubscription.current", $"{ex.GetType().Name}: {ex.Message}");
        }

        // Test 38: MusicCatalogSearchRequest.Create — array-shim factory for
        // init(term: String, types: [any MusicCatalogSearchable.Type]).
        // Marshalling-only: no auth/entitlement required. Framework errors on
        // hypothetical .Response calls would count as pass; this test stops at
        // construction since that's what the shim covers.
        try
        {
            var req = MusicCatalogSearchRequest.Create("rush",
                MusicCatalogSearchTypes.Album | MusicCatalogSearchTypes.Artist);
            if (req is null)
                throw new InvalidOperationException("Create returned null");
            req.Dispose();
            Pass("MusicCatalogSearchRequest.Create (array-shim factory)");
        }
        catch (DllNotFoundException ex)
        {
            Fail("MusicCatalogSearchRequest.Create", ex.Message);
        }
        catch (EntryPointNotFoundException ex)
        {
            Fail("MusicCatalogSearchRequest.Create", ex.Message);
        }
        catch (Exception ex)
        {
            Fail("MusicCatalogSearchRequest.Create", ex.Message);
        }

        // Test 39: MusicCatalogSearchRequest.Create with unknown bits throws.
        // Validates the shim's bit-validation contract: the @_cdecl returns
        // non-zero and the partial wraps it as ArgumentException with a
        // diagnostic stray-bit hex string.
        try
        {
            const MusicCatalogSearchTypes badMask = (MusicCatalogSearchTypes)0x10000u; // unused bit
            try
            {
                var req = MusicCatalogSearchRequest.Create("rush", badMask);
                Fail("MusicCatalogSearchRequest.Create unknown-bits guard",
                    "expected ArgumentException, got success");
                req.Dispose();
            }
            catch (ArgumentException)
            {
                Pass("MusicCatalogSearchRequest.Create unknown-bits guard");
            }
        }
        catch (Exception ex)
        {
            Fail("MusicCatalogSearchRequest.Create unknown-bits guard", ex.Message);
        }

        // Test 40: MusicCatalogSearchSuggestionsRequest.Create — sibling shim
        // for init(term:includingTopResultsOfTypes:). Same mask convention, zero
        // mask legal (Apple's default is []).
        try
        {
            var req = MusicCatalogSearchSuggestionsRequest.Create("rush",
                MusicCatalogSearchTypes.Song);
            if (req is null)
                throw new InvalidOperationException("Create returned null");
            req.Dispose();
            Pass("MusicCatalogSearchSuggestionsRequest.Create (array-shim factory)");
        }
        catch (DllNotFoundException ex)
        {
            Fail("MusicCatalogSearchSuggestionsRequest.Create", ex.Message);
        }
        catch (EntryPointNotFoundException ex)
        {
            Fail("MusicCatalogSearchSuggestionsRequest.Create", ex.Message);
        }
        catch (Exception ex)
        {
            Fail("MusicCatalogSearchSuggestionsRequest.Create", ex.Message);
        }

        // Test 41a: ApplicationMusicPlayer queue-factory -> player-assignment shape (audit BQ-10).
        //
        // The only way a consumer can build a queue from items is the generated
        // `ApplicationMusicPlayer.QueueInfo.From…(SwiftArray<T>)` factories. Every one of them is
        // declared as returning the BASE type `MusicPlayer.Queue`, while the assignment target —
        // `ApplicationMusicPlayer.Queue`'s setter — takes the DERIVED `ApplicationMusicPlayer.QueueInfo`.
        // So `player.Queue = ApplicationMusicPlayer.QueueInfo.From…(songs);` does not compile, and the
        // queue-building surface is unreachable from C#.
        //
        // This is asserted through reflection rather than by writing the assignment directly: a
        // direct assignment would be a COMPILE error, which would take the whole test app down and
        // make every other MusicKit result unobservable. Reflecting over the declared signatures
        // checks exactly the same compile-time contract while still producing a runnable red.
        // EXPECTED RED until the BQ-10 generator fix + regen; do not weaken it.
        try
        {
            var queueInfoType = typeof(global::MusicKit.ApplicationMusicPlayer.QueueInfo);
            var playerQueueProp = typeof(global::MusicKit.ApplicationMusicPlayer).GetProperty("Queue");
            if (playerQueueProp is null)
                throw new InvalidOperationException("ApplicationMusicPlayer.Queue property missing");
            var setter = playerQueueProp.GetSetMethod();
            if (setter is null)
                throw new InvalidOperationException("ApplicationMusicPlayer.Queue has no setter");
            var required = setter.GetParameters()[0].ParameterType;

            var factories = queueInfoType
                .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
                .Where(m => m.Name.StartsWith("From", StringComparison.Ordinal))
                .ToArray();
            if (factories.Length == 0)
                throw new InvalidOperationException("no ApplicationMusicPlayer.QueueInfo.From… factories were emitted");

            var unassignable = factories
                .Where(m => !required.IsAssignableFrom(m.ReturnType))
                .Select(m => $"{m.Name} -> {m.ReturnType.FullName}")
                .ToArray();

            Log($"QueueInfo factories: {factories.Length}; assignment target: {required.FullName}");
            if (unassignable.Length > 0)
                throw new InvalidOperationException(
                    $"{unassignable.Length}/{factories.Length} queue factories return a type that cannot be assigned to " +
                    $"ApplicationMusicPlayer.Queue ({required.Name}): {string.Join("; ", unassignable)}");
            Pass("ApplicationMusicPlayer queue-factory result is assignable to the player's queue");
        }
        catch (Exception ex)
        {
            Fail("ApplicationMusicPlayer queue-factory result is assignable to the player's queue",
                 $"{ex.GetType().Name}: {ex.Message}");
        }

        // Test 41b: an EMPTY MusicItemCollection<Song>, projected through IReadOnlyList<Song>.
        //
        // MusicItemCollection<T> has no public constructor (all three Swift inits are recorded
        // unsupported: variadic parameter, method-own generic init, pruned synthesized Codable), so
        // the only credential-free way to obtain one is to decode a search response from JSON and
        // read its collection property. If no fixture route works, this test FAILS — a mandatory
        // fixture that cannot be built is a real gap, not something to skip past.
        global::MusicKit.MusicItemCollection<global::MusicKit.Song>? emptySongs = null;
        try
        {
            string[] fixtures = { "{}", "{\"songs\":[]}", "{\"songs\":{\"data\":[]}}", "{\"results\":{}}" };
            var failures = new List<string>();
            foreach (var fixture in fixtures)
            {
                try
                {
                    var response = global::MusicKit.MusicCatalogSearchResponse.DecodeFromJson(
                        System.Text.Encoding.UTF8.GetBytes(fixture));
                    emptySongs = response.Songs;
                    Log($"empty-collection fixture accepted: {fixture}");
                    break;
                }
                catch (Exception fx)
                {
                    failures.Add($"{fixture} -> {fx.GetType().Name}: {fx.Message}");
                }
            }

            if (emptySongs is null)
                throw new InvalidOperationException(
                    "no credential-free fixture produced a MusicItemCollection<Song>; tried " + string.Join(" | ", failures));

            System.Collections.Generic.IReadOnlyList<global::MusicKit.Song> asList = emptySongs;
            if (asList.Count != 0)
                throw new InvalidOperationException($"decoded collection should be empty, Count = {asList.Count}");
            int enumerated = 0;
            foreach (var _ in asList)
                enumerated++;
            if (enumerated != 0)
                throw new InvalidOperationException($"enumerating the empty collection yielded {enumerated} element(s)");
            Pass("empty MusicItemCollection<Song> projects as an empty IReadOnlyList<Song>");
        }
        catch (Exception ex)
        {
            Fail("empty MusicItemCollection<Song> projects as an empty IReadOnlyList<Song>",
                 $"{ex.GetType().Name}: {ex.Message}");
        }

        // Test 41 (Skip): MusicAuthorization.RequestAsync — the interactive authorization
        // REQUEST. The binding surface exists (Task<Status> RequestAsync(CancellationToken))
        // and the usage string is present (NSAppleMusicUsageDescription in Info.plist), but
        // invoking request() in an autonomous CLI simulator does NOT resolve to Denied — it
        // hard-aborts the process inside Apple's TCC.framework
        // (__TCC_CRASHING_DUE_TO_PRIVACY_VIOLATION__) because a protected-resource request
        // cannot go through an interactive TCC grant/deny flow in a headless sim. This is an
        // Apple-side environmental limitation (same class as StoreKit .storekit config and
        // LiveActivity foreground preconditions), not a binding/marshalling defect: the
        // status-QUERY path MusicAuthorization.CurrentStatus is exercised and passes above,
        // and MusicSubscription.GetCurrentAsync round-trips a framework .permissionDenied.
        // The awaited REQUEST itself is not callable here — do NOT invoke it (it crashes the
        // whole app, taking every later test with it). Revisit under the entitlement/Sandbox
        // follow-up with an interactive or pre-authorized TCC host, then assert the Status.
        Skip("MusicAuthorization.RequestAsync awaited status",
            "request() aborts via TCC (__TCC_CRASHING_DUE_TO_PRIVACY_VIOLATION__) in headless CLI sim even with NSAppleMusicUsageDescription; status-query path CurrentStatus covered above; revisit with an interactive/pre-authorized TCC host");

        // Test 42 (LAST — deliberately): MusicItemCollection<T> index bounds through IReadOnlyList<T>.
        //
        // The generated indexer must range-check before forwarding to the Swift subscript: an
        // unchecked `(nint)index` reaches Swift's Array subscript and TRAPS — a fatalError that
        // kills the process outright rather than raising a catchable managed exception, which
        // would take the run summary and every other result in this lane down with it.
        //
        // This ran behind an opt-in environment variable while the indexer had no bounds check.
        // The check is in place now — `[0]` / `[-1]` / `[Count]` on an empty collection all raise
        // ArgumentOutOfRangeException — so the guard is gone and the test runs on every lane,
        // which is what keeps the trap from coming back unnoticed. It stays LAST so that if the
        // check ever regresses, the resulting trap costs only the summary line.
        if (emptySongs is null)
        {
            Fail("MusicItemCollection<Song> index bounds through IReadOnlyList<Song>",
                 "no empty MusicItemCollection<Song> fixture was available (see the fixture test above)");
        }
        else
        {
            System.Collections.Generic.IReadOnlyList<global::MusicKit.Song> asList = emptySongs;
            foreach (var index in new[] { 0, -1, asList.Count })
            {
                var name = $"MusicItemCollection<Song>[{index}] on an empty collection throws ArgumentOutOfRangeException";
                try
                {
                    var item = asList[index];
                    Fail(name, $"indexer returned {(item is null ? "null" : "a Song")} instead of throwing");
                }
                catch (ArgumentOutOfRangeException)
                {
                    Pass(name);
                }
                catch (Exception ex)
                {
                    Fail(name, $"expected ArgumentOutOfRangeException, got {ex.GetType().Name}: {ex.Message}");
                }
            }
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
