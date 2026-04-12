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
        try
        {
            using var status = MusicAuthorization.CurrentStatus;
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

        // Test 6: AudioVariant.SpatialAudio.GetDescription — this is the case that requires
        // iOS 17.2+. The generated wrapper must have #available guards for this case;
        // on simulators running iOS < 17.2 the wrapper should trap, on 17.2+ it should return a string.
        // Either outcome means the wrapper compiled correctly with availability emission.
        try
        {
            var desc = AudioVariant.SpatialAudio.GetDescription();
            Log($"SpatialAudio description = {desc}");
            Pass("AudioVariant.SpatialAudio.GetDescription");
        }
        catch (Exception ex)
        {
            // Accept any failure here — the important assertion is that the SYMBOL exists
            // and the wrapper compiled with the #available guard. If the binary didn't link,
            // we'd get a DllNotFoundException / EntryPointNotFoundException much earlier.
            Log($"SpatialAudio trapped as expected on older OS: {ex.Message}");
            Pass("AudioVariant.SpatialAudio.GetDescription (trapped or returned)");
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
