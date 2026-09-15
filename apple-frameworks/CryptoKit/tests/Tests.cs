// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Text;
using CryptoKit;
using Swift.Runtime;

// Caseless Swift enums in CryptoKit (HPKE, AES, Insecure, P256, P384, P521) emit
// as C# child namespaces of CryptoKit, not as static types. `using CryptoKit;`
// imports types but not child namespaces, so reference them via aliases.
using HPKE = CryptoKit.HPKE;
using AES = CryptoKit.AES;
using Insecure = CryptoKit.Insecure;
using P256 = CryptoKit.P256;
using P384 = CryptoKit.P384;
using P521 = CryptoKit.P521;
using MLDSA65 = CryptoKit.MLDSA65;
using Curve25519 = CryptoKit.Curve25519;

namespace SwiftBindings.CryptoKit.Tests;

internal static class Tests
{
    /// <summary>
    /// Resident set size of this process in bytes, read straight from the Mach task info. This is
    /// the only footprint probe that sees memory the Swift side allocated: a native buffer leaked by
    /// an unbalanced owned-Data return is invisible to <c>GC.GetTotalMemory</c>. Returns 0 when the
    /// probe is unavailable.
    /// </summary>
    private static ulong ResidentBytes()
    {
        const uint MachTaskBasicInfo = 20;
        // mach_task_basic_info is 48 bytes: virtual_size, resident_size, resident_size_max (8 each),
        // user_time, system_time (8 each), policy, suspend_count (4 each) => 12 natural_t words.
        uint count = 12;
        var buf = new uint[12];
        try
        {
            if (task_info(mach_task_self(), MachTaskBasicInfo, buf, ref count) != 0)
                return 0;
        }
        catch (Exception)
        {
            return 0;
        }
        // resident_size is the second 64-bit field => words [2] (low) and [3] (high).
        return buf[2] | ((ulong)buf[3] << 32);
    }

    [System.Runtime.InteropServices.DllImport("/usr/lib/libSystem.dylib")]
    private static extern IntPtr mach_task_self();

    [System.Runtime.InteropServices.DllImport("/usr/lib/libSystem.dylib")]
    private static extern int task_info(IntPtr target, uint flavor, uint[] taskInfo, ref uint taskInfoCount);

    /// <summary>
    /// Extracts the trailing lowercase-hex run from a CryptoKit <c>description</c>
    /// (e.g. "SHA256 digest: ba7816bf…", "HMAC with SHA256: b0344c61…"). CryptoKit's Digest and
    /// MessageAuthenticationCode types expose no raw-byte accessor through the binding, so the
    /// description is the only channel for the octets; a description that carries no hex run of the
    /// expected length makes the caller fail rather than silently compare nothing.
    /// </summary>
    private static string HexFromDescription(string? description)
    {
        if (string.IsNullOrEmpty(description))
            return string.Empty;
        int end = description.Length;
        int i = end - 1;
        while (i >= 0 && Uri.IsHexDigit(description[i]))
            i--;
        return description[(i + 1)..end].ToLowerInvariant();
    }

    private static string ToHex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

    /// <summary>
    /// Asserts that <paramref name="ex"/> reports the CryptoKit AUTHENTICATION FAILURE category and
    /// nothing else. Throws with the observed category when it does not, so the caller's test fails.
    ///
    /// <para>Two carriers are accepted, because the generated binding uses different error paths for
    /// different member shapes. An async or otherwise dispatcher-routed member reconstructs a typed
    /// <c>SwiftException&lt;CryptoKitError&gt;</c> with a real discriminator, and that is the strong
    /// form checked first. A synchronous throwing member — which is what every AES.GCM entry point
    /// is — reports through the <c>errorOut</c> pointer and <c>SwiftMarshal.ThrowSwiftError</c>,
    /// producing an untyped <c>SwiftException</c> whose message is Swift's own
    /// <c>String(describing:)</c> of the error value. The generated typed dispatcher
    /// <c>_SBW_dispatchSwiftError_CryptoKit</c> exists in the wrapper but nothing on the synchronous
    /// path calls it, so for these members the case name in the message is the only carrier the
    /// category has. Matching it exactly is still a category assertion: an
    /// <c>incorrectKeySize</c>, an <c>underlyingCoreCryptoError</c>, or any non-Swift managed
    /// exception all fail here.</para>
    /// </summary>
    private static void AssertAuthenticationFailure(SwiftException ex, string what)
    {
        if (ex is SwiftException<CryptoKitError> typed)
        {
            var err = typed.Error;
            if (err is null)
                throw new InvalidOperationException(
                    $"{what}: SwiftException<CryptoKitError> carried no typed payload (message: {typed.Message})");
            if (err.Tag != CryptoKitError.CaseTag.AuthenticationFailure)
                throw new InvalidOperationException(
                    $"{what}: expected CryptoKitError.authenticationFailure, got CryptoKitError.{err.Tag}");
            Log($"{what} threw typed CryptoKitError.{err.Tag}");
            return;
        }

        // Untyped carrier: the message is Swift's String(describing: CryptoKitError.authenticationFailure).
        const string expected = "authenticationFailure";
        if (!string.Equals(ex.Message?.Trim(), expected, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"{what}: expected the authenticationFailure category, got untyped SwiftException with message '{ex.Message}'");
        Log($"{what} threw untyped SwiftException carrying the {expected} category "
            + "(the synchronous errorOut path does not reach the generated typed-error dispatcher)");
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void ExpectAuthenticationFailure(Func<byte[]> operation, string what)
    {
        try
        {
            var result = operation();
            throw new InvalidOperationException(
                $"{what} returned {result?.Length ?? -1} byte(s) instead of throwing");
        }
        catch (SwiftException sex)
        {
            AssertAuthenticationFailure(sex, what);
        }
    }

    public static async Task<int> RunAsync()
    {
        await Task.Yield(); // run the body off ViewDidLoad's synchronous prologue
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

        // Local helper for metadata tests.
        void MetadataTest<T>(string name) where T : ISwiftObject
        {
            try
            {
                var md = SwiftObjectHelper<T>.GetTypeMetadata();
                if (md.Handle == IntPtr.Zero)
                    throw new InvalidOperationException("null handle");
                Log($"{name} metadata size = {md.Size}");
                Pass($"{name} metadata");
            }
            catch (Exception ex)
            {
                Fail($"{name} metadata", ex.Message);
            }
        }

        // Test 1: SHA256 metadata loads.
        MetadataTest<SHA256>("SHA256");

        // Test 2: SHA384 metadata loads.
        MetadataTest<SHA384>("SHA384");

        // Test 3: SHA512 metadata loads.
        MetadataTest<SHA512>("SHA512");

        // Test 4: SHA3_256 metadata loads.
        MetadataTest<SHA3_256>("SHA3_256");

        // Test 5: SHA256Digest metadata loads.
        MetadataTest<SHA256Digest>("SHA256Digest");

        // Test 6: SymmetricKey metadata loads.
        MetadataTest<SymmetricKey>("SymmetricKey");

        // Test 7: SymmetricKeySize metadata loads.
        MetadataTest<SymmetricKeySize>("SymmetricKeySize");

        // Test 8: CryptoKitError metadata loads.
        MetadataTest<CryptoKitError>("CryptoKitError");

        // Test 9: HPKE.Ciphersuite metadata loads.
        MetadataTest<HPKE.Ciphersuite>("HPKE.Ciphersuite");

        // Test 10: CryptoKitASN1Error plain enum values are correct.
        try
        {
            if ((int)CryptoKitASN1Error.InvalidFieldIdentifier != 0)
                throw new InvalidOperationException($"expected 0, got {(int)CryptoKitASN1Error.InvalidFieldIdentifier}");
            if ((int)CryptoKitASN1Error.InvalidPEMDocument != 7)
                throw new InvalidOperationException($"expected 7, got {(int)CryptoKitASN1Error.InvalidPEMDocument}");
            Pass("CryptoKitASN1Error values");
        }
        catch (Exception ex)
        {
            Fail("CryptoKitASN1Error values", ex.Message);
        }

        // Test 11: HPKE.KDF plain enum values are correct.
        try
        {
            if ((int)HPKE.KDF.HkdfSHA256 != 0)
                throw new InvalidOperationException($"expected 0, got {(int)HPKE.KDF.HkdfSHA256}");
            if ((int)HPKE.KDF.HkdfSHA384 != 1)
                throw new InvalidOperationException($"expected 1, got {(int)HPKE.KDF.HkdfSHA384}");
            if ((int)HPKE.KDF.HkdfSHA512 != 2)
                throw new InvalidOperationException($"expected 2, got {(int)HPKE.KDF.HkdfSHA512}");
            Pass("HPKE.KDF values");
        }
        catch (Exception ex)
        {
            Fail("HPKE.KDF values", ex.Message);
        }

        // Test 12: HPKE.KEM plain enum values are correct.
        try
        {
            if ((int)HPKE.KEM.P256HkdfSHA256 != 0)
                throw new InvalidOperationException($"expected 0, got {(int)HPKE.KEM.P256HkdfSHA256}");
            if ((int)HPKE.KEM.Curve25519_HKDF_SHA256 != 3)
                throw new InvalidOperationException($"expected 3, got {(int)HPKE.KEM.Curve25519_HKDF_SHA256}");
            if ((int)HPKE.KEM.XWingMLKEM768X25519 != 4)
                throw new InvalidOperationException($"expected 4, got {(int)HPKE.KEM.XWingMLKEM768X25519}");
            Pass("HPKE.KEM values");
        }
        catch (Exception ex)
        {
            Fail("HPKE.KEM values", ex.Message);
        }

        // Test 13: HPKE.AEAD plain enum values are correct.
        try
        {
            if ((int)HPKE.AEAD.AesGcm128 != 0)
                throw new InvalidOperationException($"expected 0, got {(int)HPKE.AEAD.AesGcm128}");
            if ((int)HPKE.AEAD.AesGcm256 != 1)
                throw new InvalidOperationException($"expected 1, got {(int)HPKE.AEAD.AesGcm256}");
            if ((int)HPKE.AEAD.ChaChaPoly != 2)
                throw new InvalidOperationException($"expected 2, got {(int)HPKE.AEAD.ChaChaPoly}");
            Pass("HPKE.AEAD values");
        }
        catch (Exception ex)
        {
            Fail("HPKE.AEAD values", ex.Message);
        }

        // Test 14: CryptoKitError.CaseTag enum tag values match Swift's enum ordering.
        try
        {
            if ((uint)CryptoKitError.CaseTag.IncorrectKeySize != 1u)
                throw new InvalidOperationException($"IncorrectKeySize expected 1, got {(uint)CryptoKitError.CaseTag.IncorrectKeySize}");
            if ((uint)CryptoKitError.CaseTag.AuthenticationFailure != 3u)
                throw new InvalidOperationException($"AuthenticationFailure expected 3, got {(uint)CryptoKitError.CaseTag.AuthenticationFailure}");
            if ((uint)CryptoKitError.CaseTag.InvalidParameter != 6u)
                throw new InvalidOperationException($"InvalidParameter expected 6, got {(uint)CryptoKitError.CaseTag.InvalidParameter}");
            Pass("CryptoKitError.CaseTag values");
        }
        catch (Exception ex)
        {
            Fail("CryptoKitError.CaseTag values", ex.Message);
        }

        // Test 15: CryptoKitError singleton singletons are reachable and have the correct Tag.
        try
        {
            var incorrectKeySize = CryptoKitError.IncorrectKeySize;
            if (incorrectKeySize is null)
                throw new InvalidOperationException("IncorrectKeySize is null");
            if (incorrectKeySize.Tag != CryptoKitError.CaseTag.IncorrectKeySize)
                throw new InvalidOperationException($"expected IncorrectKeySize tag, got {incorrectKeySize.Tag}");
            var authFailure = CryptoKitError.AuthenticationFailure;
            if (authFailure.Tag != CryptoKitError.CaseTag.AuthenticationFailure)
                throw new InvalidOperationException($"expected AuthenticationFailure tag, got {authFailure.Tag}");
            Pass("CryptoKitError singleton tags");
        }
        catch (Exception ex)
        {
            Fail("CryptoKitError singleton tags", ex.Message);
        }

        // Test 16: HPKE.KDF.AllCases extension method returns all 3 cases.
        try
        {
            var all = HPKEKDFExtensions.AllCases;
            if (all is null || all.Count == 0)
                throw new InvalidOperationException("AllCases is null or empty");
            if (all.Count != 3)
                throw new InvalidOperationException($"expected 3 cases, got {all.Count}");
            Pass("HPKE.KDF.AllCases");
        }
        catch (Exception ex)
        {
            Fail("HPKE.KDF.AllCases", ex.Message);
        }

        // Test 17: HPKE.KEM.AllCases extension method returns all 5 cases.
        try
        {
            var all = HPKEKEMExtensions.AllCases;
            if (all is null || all.Count == 0)
                throw new InvalidOperationException("AllCases is null or empty");
            if (all.Count != 5)
                throw new InvalidOperationException($"expected 5 cases, got {all.Count}");
            Pass("HPKE.KEM.AllCases");
        }
        catch (Exception ex)
        {
            Fail("HPKE.KEM.AllCases", ex.Message);
        }

        // Test 18: HPKE.AEAD.AllCases extension method returns all 4 cases.
        try
        {
            var all = HPKEAEADExtensions.AllCases;
            if (all is null || all.Count == 0)
                throw new InvalidOperationException("AllCases is null or empty");
            if (all.Count != 4)
                throw new InvalidOperationException($"expected 4 cases, got {all.Count}");
            Pass("HPKE.AEAD.AllCases");
        }
        catch (Exception ex)
        {
            Fail("HPKE.AEAD.AllCases", ex.Message);
        }

        // Test 19: SymmetricKeySize static singleton Bits128 is reachable (does not crash).
        try
        {
            var size128 = SymmetricKeySize.Bits128;
            if (size128 is null)
                throw new InvalidOperationException("Bits128 is null");
            Pass("SymmetricKeySize.Bits128 reachable");
        }
        catch (Exception ex)
        {
            Fail("SymmetricKeySize.Bits128 reachable", ex.Message);
        }

        // Test 20: SymmetricKeySize static singleton Bits256 is reachable (does not crash).
        try
        {
            var size256 = SymmetricKeySize.Bits256;
            if (size256 is null)
                throw new InvalidOperationException("Bits256 is null");
            Pass("SymmetricKeySize.Bits256 reachable");
        }
        catch (Exception ex)
        {
            Fail("SymmetricKeySize.Bits256 reachable", ex.Message);
        }

        // Test 21: P256/P384/P521 key metadata loads — representative coverage across all curves.
        MetadataTest<P256.Signing.PublicKey>("P256.Signing.PublicKey");
        MetadataTest<P384.Signing.PublicKey>("P384.Signing.PublicKey");
        MetadataTest<P521.Signing.PublicKey>("P521.Signing.PublicKey");
        MetadataTest<P384.KeyAgreement.PublicKey>("P384.KeyAgreement.PublicKey");
        MetadataTest<P521.KeyAgreement.PublicKey>("P521.KeyAgreement.PublicKey");

        // Test 26: P384/P521 private key and signature metadata.
        MetadataTest<P384.Signing.PrivateKey>("P384.Signing.PrivateKey");
        MetadataTest<P521.Signing.PrivateKey>("P521.Signing.PrivateKey");
        MetadataTest<Swift.CryptoKit.P256.Signing.ECDSASignature>("P256.Signing.ECDSASignature");
        MetadataTest<Swift.CryptoKit.P384.Signing.ECDSASignature>("P384.Signing.ECDSASignature");
        MetadataTest<Swift.CryptoKit.P521.Signing.ECDSASignature>("P521.Signing.ECDSASignature");

        // Test 31: AES.GCM.Nonce metadata loads.
        MetadataTest<AES.GCM.Nonce>("AES.GCM.Nonce");

        // Test 23: ChaChaPoly.Nonce metadata loads.
        MetadataTest<ChaChaPoly.Nonce>("ChaChaPoly.Nonce");

        // Test 24: Insecure.SHA1 metadata loads.
        MetadataTest<Insecure.SHA1>("Insecure.SHA1");

        // Test 25a: SymmetricKeySize.Bits256.BitCount round-trips correctly.
        // Diagnostic baseline for the AEAD round-trip tests — if BitCount is not 256, AES.GCM
        // will reject the resulting key with `incorrectKeySize`.
        try
        {
            var size = SymmetricKeySize.Bits256;
            int bits = size.BitCount;
            Log($"SymmetricKeySize.Bits256.BitCount = {bits}");
            if (bits != 256)
                throw new InvalidOperationException($"expected 256, got {bits}");
            Pass("SymmetricKeySize.Bits256.BitCount round-trip");
        }
        catch (Exception ex)
        {
            Fail("SymmetricKeySize.Bits256.BitCount round-trip", ex.Message);
        }

        // Test 25b: Construct SymmetricKeySize via explicit nint bitCount, verify BitCount round-trips.
        try
        {
            var size = new SymmetricKeySize((nint)256);
            int bits = size.BitCount;
            Log($"new SymmetricKeySize(256).BitCount = {bits}");
            if (bits != 256)
                throw new InvalidOperationException($"expected 256, got {bits}");
            Pass("new SymmetricKeySize(256).BitCount round-trip");
        }
        catch (Exception ex)
        {
            Fail("new SymmetricKeySize(256).BitCount round-trip", ex.Message);
        }

        // Test 25c: SymmetricKey constructed from a 256-bit size has BitCount == 256.
        try
        {
            var key = new SymmetricKey(new SymmetricKeySize((nint)256));
            int bits = key.BitCount;
            Log($"SymmetricKey(SymmetricKeySize(256)).BitCount = {bits}");
            if (bits != 256)
                throw new InvalidOperationException($"expected 256, got {bits}");
            Pass("SymmetricKey size round-trip");
        }
        catch (Exception ex)
        {
            Fail("SymmetricKey size round-trip", ex.Message);
        }

        // Tests 26–29: AEAD round-trip from C#. End-to-end authenticated encryption.
        //
        // The CSM-emitted Seal/Open overloads pass SymmetricKey through the
        // ConcreteProtocolSpecializationEmitter PayloadHandle path. Non-frozen-struct
        // params on that path require .assumingMemoryBound(...).pointee reconstruction
        // (a value-witness-table-aware load of the heap buffer); a class-style
        // unsafeBitCast(OpaquePointer) reconstruction would mis-read the pointer's
        // bits as the struct payload, surfacing as AES.GCM seeing an incorrectKeySize
        // even though SymmetricKey.BitCount == 256.

        // Test 26: AES.GCM Seal/Open round-trip via the non-generic byte[] CSM overload.
        // Exercises SBW_CSM_CryptoKit_GCM_Swift_Array_Swift_UInt8_seal_3E6CC09E — the
        // SymmetricKey CSM-marshalling code path.
        try
        {
            var key = new SymmetricKey(SymmetricKeySize.Bits256);
            var plaintext = Encoding.UTF8.GetBytes("hello AES.GCM");
            var sealedBox = AES.GCM.Seal(plaintext, key);
            var recovered = AES.GCM.Open(sealedBox, key);
            if (recovered is null)
                throw new InvalidOperationException("Open returned null");
            if (recovered.Length != plaintext.Length)
                throw new InvalidOperationException($"length mismatch: {recovered.Length} vs {plaintext.Length}");
            for (int i = 0; i < plaintext.Length; i++)
            {
                if (recovered[i] != plaintext[i])
                    throw new InvalidOperationException($"byte mismatch at {i}: {recovered[i]} vs {plaintext[i]}");
            }
            Pass("AES.GCM round-trip");
        }
        catch (Exception ex)
        {
            Fail("AES.GCM round-trip", ex.Message);
        }

        // Test 27: ChaChaPoly Seal/Open round-trip via the non-generic byte[] CSM overload.
        try
        {
            var key = new SymmetricKey(SymmetricKeySize.Bits256);
            var plaintext = Encoding.UTF8.GetBytes("hello ChaChaPoly");
            var sealedBox = ChaChaPoly.Seal(plaintext, key);
            var recovered = ChaChaPoly.Open(sealedBox, key);
            if (recovered is null)
                throw new InvalidOperationException("Open returned null");
            if (recovered.Length != plaintext.Length)
                throw new InvalidOperationException($"length mismatch: {recovered.Length} vs {plaintext.Length}");
            for (int i = 0; i < plaintext.Length; i++)
            {
                if (recovered[i] != plaintext[i])
                    throw new InvalidOperationException($"byte mismatch at {i}: {recovered[i]} vs {plaintext[i]}");
            }
            Pass("ChaChaPoly round-trip");
        }
        catch (Exception ex)
        {
            Fail("ChaChaPoly round-trip", ex.Message);
        }

        // Test 28: AES.GCM authentication — Open with the wrong key must fail with the AUTHENTICATION
        // FAILURE category, not merely "some exception". The previous version caught bare `catch`,
        // so a NullReferenceException, a marshalling crash surfaced as an InvalidOperationException,
        // or a PlatformNotSupportedException all counted as "tamper detected". We now require the
        // Swift-thrown typed error CryptoKitError with CaseTag.AuthenticationFailure (3) — the exact
        // category CryptoKit documents for a GCM tag mismatch. Anything else fails the test.
        // (SealedBox(nonce:, ciphertext:, tag:) ctor is unavailable — Swift generic init not
        // emittable in C# — so we exercise the auth path by opening with a different key.)
        try
        {
            var key1 = new SymmetricKey(SymmetricKeySize.Bits256);
            var key2 = new SymmetricKey(SymmetricKeySize.Bits256);
            var plaintext = Encoding.UTF8.GetBytes("auth-test plaintext");
            var sealedBox = AES.GCM.Seal(plaintext, key1);

            ExpectAuthenticationFailure(() => AES.GCM.Open(sealedBox, key2), "wrong-key Open");
            Pass("AES.GCM wrong-key Open reports CryptoKitError.authenticationFailure");
        }
        catch (Exception ex)
        {
            Fail("AES.GCM wrong-key Open reports CryptoKitError.authenticationFailure",
                 $"{ex.GetType().Name}: {ex.Message}");
        }

        // Test 29: AES.GCM Seal-with-AD dispatch — exercises the 4-arg CSM Seal overload
        // (SBW_CSM_CryptoKit_GCM_Swift_Array_Swift_UInt8_Swift_Array_Swift_UInt8_seal_C2C5F492),
        // a *different* CSM specialization than the 3-arg overload Tests 26/27 use (a
        // `where AD: DataProtocol` constraint adds a second metadata-routed parameter).
        // Validates the SymmetricKey CSM marshalling on the multi-conformer CSM path.
        //
        // The AAD box is now actually OPENED, both ways — sealing alone never proved the additional
        // authenticated data was bound to the box at all. We use the CLOSED byte[] CSM overload
        // Open(SealedBox, SymmetricKey, byte[]) rather than the open generic Open<TAD>, so this
        // stays on the same cdecl CSM carrier as the Seal side:
        //   * Open with the SAME AAD must return the original plaintext, byte for byte;
        //   * Open with a DIFFERENT AAD must fail with CryptoKitError.authenticationFailure —
        //     that is the assertion that proves the AAD is authenticated rather than ignored.
        try
        {
            var key = new SymmetricKey(SymmetricKeySize.Bits256);
            var plaintext = Encoding.UTF8.GetBytes("plaintext with AD");
            var aad = Encoding.UTF8.GetBytes("authenticated-header");
            var wrongAad = Encoding.UTF8.GetBytes("authenticated-headeR"); // one bit different
            var sealedBox = AES.GCM.Seal(plaintext, key, aad);

            // First matching open proves the call itself works; the second call below
            // proves the first did not consume or corrupt the resilient SealedBox payload.
            var recoveredBeforeProperties = AES.GCM.Open(sealedBox, key, aad);
            if (recoveredBeforeProperties is null || !recoveredBeforeProperties.AsSpan().SequenceEqual(plaintext))
                throw new InvalidOperationException("first Open with matching AAD failed");

            var ciphertext = sealedBox.Ciphertext;
            var tag = sealedBox.Tag;
            if (ciphertext is null || ciphertext.Length != plaintext.Length)
                throw new InvalidOperationException($"ciphertext length unexpected: {ciphertext?.Length ?? -1}");
            if (tag is null || tag.Length != 16) // AES-GCM tag is 128 bits
                throw new InvalidOperationException($"tag length unexpected: {tag?.Length ?? -1}");

            // (1) Correct AAD round-trips.
            var recovered = AES.GCM.Open(sealedBox, key, aad);
            if (recovered is null)
                throw new InvalidOperationException("Open with matching AAD returned null");
            if (!recovered.AsSpan().SequenceEqual(plaintext))
                throw new InvalidOperationException(
                    $"Open with matching AAD returned '{Encoding.UTF8.GetString(recovered)}', expected '{Encoding.UTF8.GetString(plaintext)}'");

            // (2) Wrong AAD must be rejected with the authentication-failure category.
            ExpectAuthenticationFailure(
                () => AES.GCM.Open(sealedBox, key, wrongAad),
                "mismatched-AAD Open");

            Pass("AES.GCM Seal/Open with AD round-trips and rejects a mismatched AAD");
        }
        catch (Exception ex)
        {
            Fail("AES.GCM Seal/Open with AD round-trips and rejects a mismatched AAD",
                 $"{ex.GetType().Name}: {ex.Message}");
        }

        // Test 29b: independent known-answer vectors for SHA-2 and HMAC-SHA-2.
        //
        // Every other hash/MAC assertion in this file compares CryptoKit against CryptoKit
        // (incremental vs one-shot), which cannot detect a marshalling bug that corrupts BOTH sides
        // identically — e.g. a length that is silently truncated on the way in. These vectors are
        // external: each result is checked against (a) the published constant (FIPS 180-4 "abc" and
        // the empty-string digest; RFC 4231 HMAC test cases 1 and 2) AND (b) the same computation
        // performed by .NET's own System.Security.Cryptography in-process. Three independent
        // implementations must agree, so a typo in a constant here fails loudly instead of hiding.
        //
        // CryptoKit's Digest/MAC types expose no raw-byte accessor through the binding, so the
        // octets are read out of `description` (which ends in the lowercase hex) — see
        // HexFromDescription.
        try
        {
            int vectorsChecked = 0;

            void CheckDigest(string label, string input, string expectedHex, string actualHex, byte[] bclHash)
            {
                var bclHex = ToHex(bclHash);
                if (bclHex != expectedHex)
                    throw new InvalidOperationException(
                        $"{label}: the hard-coded vector disagrees with .NET's own hash — vector '{expectedHex}', BCL '{bclHex}' (the constant in this test is wrong)");
                if (actualHex.Length != expectedHex.Length)
                    throw new InvalidOperationException(
                        $"{label}: CryptoKit description exposed {actualHex.Length} hex chars, expected {expectedHex.Length}");
                if (actualHex != expectedHex)
                    throw new InvalidOperationException(
                        $"{label}: CryptoKit produced '{actualHex}', expected '{expectedHex}' for input '{input}'");
                vectorsChecked++;
            }

            // --- FIPS 180-4 SHA-2 digests over "abc" and over the empty message. -----------------
            var abc = Encoding.ASCII.GetBytes("abc");
            var empty = Array.Empty<byte>();

            var sha256 = new SHA256();
            sha256.Update(abc);
            CheckDigest("SHA-256(\"abc\")", "abc",
                "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",
                HexFromDescription(sha256.FinalizeSwift().Description),
                System.Security.Cryptography.SHA256.HashData(abc));

            var sha256Empty = new SHA256();
            sha256Empty.Update(empty);
            CheckDigest("SHA-256(\"\")", "",
                "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
                HexFromDescription(sha256Empty.FinalizeSwift().Description),
                System.Security.Cryptography.SHA256.HashData(empty));

            var sha384 = new SHA384();
            sha384.Update(abc);
            CheckDigest("SHA-384(\"abc\")", "abc",
                "cb00753f45a35e8bb5a03d699ac65007272c32ab0eded1631a8b605a43ff5bed8086072ba1e7cc2358baeca134c825a7",
                HexFromDescription(sha384.FinalizeSwift().Description),
                System.Security.Cryptography.SHA384.HashData(abc));

            var sha512 = new SHA512();
            sha512.Update(abc);
            CheckDigest("SHA-512(\"abc\")", "abc",
                "ddaf35a193617abacc417349ae20413112e6fa4e89a97ea20a9eeee64b55d39a2192992a274fc1a836ba3c23a3feebbd454d4423643ce80e2a9ac94fa54ca49f",
                HexFromDescription(sha512.FinalizeSwift().Description),
                System.Security.Cryptography.SHA512.HashData(abc));

            if (vectorsChecked != 4)
                throw new InvalidOperationException($"expected 4 SHA-2 known-answer vectors, checked {vectorsChecked}");
            Log($"SHA-2 known-answer vectors verified: {vectorsChecked} (CryptoKit == published constant == .NET BCL)");
            Pass("SHA-2 known-answer vectors (FIPS 180-4)");
        }
        catch (Exception ex)
        {
            Fail("SHA-2 known-answer vectors (FIPS 180-4)", $"{ex.GetType().Name}: {ex.Message}");
        }

        // Tests 30–31: HMAC incremental Update/Finalize via the CSM factories.
        //
        // Session 02 (apple-framework-gaps/02-csm-cryptokit.md) restored the HMAC
        // ConcreteSpecializationEngine factories (`FromSHA256`/`FromSHA384`/…) that
        // monomorphize `HMAC<H>` over each concrete hash conformer. Before that fix
        // the incremental HMAC surface was unconstructible. These tests exercise the
        // full incremental path — construct via the CSM factory, feed the message in
        // two chunks through `Update`, `Finalize` — and assert the result is bit-for-bit
        // identical to CryptoKit's own one-shot `AuthenticationCode` over the same input.
        // Matching a trusted single-call reference proves the incremental binding is
        // correct, not merely non-crashing.
        //
        // RUNTIME GATE. The CSM factories monomorphize the generic `HMAC<H>`
        // ISwiftObject, and the generated `HMAC{SHA256,SHA384}CsmExtensions` static
        // initializer resolves the hash conformer's protocol-conformance descriptor by
        // dlopen-ing the framework through `@rpath/CryptoKit.framework/CryptoKit`. On a
        // physical device that path does NOT resolve — CryptoKit is an Apple SYSTEM
        // framework under /System/Library/Frameworks, not embedded in the app's @rpath —
        // so the class constructor throws TypeInitializationException wrapping "Unable to
        // load library: @rpath/CryptoKit.framework/CryptoKit" on the first CSM call. The
        // simulator resolves @rpath to the framework, so the same call works there.
        //
        // Two things keep that gate from being a blanket amnesty for AOT runs:
        //
        //  * It is keyed on the failure's own SIGNATURE — the @rpath framework load —
        //    rather than on "a static initializer threw". IsDynamicCodeSupported is
        //    false under BOTH physical-device runtimes (NativeAOT and Mono full-AOT)
        //    AND under the AOT-compiled tvOS simulator build, so "AOT plus
        //    TypeInitializationException" would silently absolve a genuine static-init
        //    regression on any of the three.
        //  * The runtime flavor is read from MonoVM's own corlib types rather than from
        //    RuntimeFeature, because Mono full-AOT and NativeAOT are indistinguishable
        //    through RuntimeFeature. It is named in the skip line, so a Mono full-AOT
        //    skip can never be read as the NativeAOT lane's.
        bool dynamicCodeSupported = System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported;
        bool monoRuntime = Type.GetType("Mono.Runtime") is not null
            || Type.GetType("Mono.RuntimeStructs") is not null
            || Type.GetType("Mono.RuntimeMarshal") is not null;
        string runtimeFlavor = dynamicCodeSupported
            ? (monoRuntime ? "Mono JIT" : "CoreCLR")
            : (monoRuntime ? "Mono full-AOT" : "NativeAOT");
        const string csmRpathLoadSignature = "@rpath/CryptoKit.framework";
        const string csmAotGapReason =
            "SDK gap: CSM conformance-descriptor load dlopens @rpath/CryptoKit.framework, which doesn't resolve the Apple system framework on device";

        // True only when the thrown chain actually names the @rpath framework load —
        // the one failure this gate is allowed to forgive.
        static bool IsRpathFrameworkLoadFailure(Exception? ex)
        {
            for (var e = ex; e is not null; e = e.InnerException)
                if (e.Message is { } m && m.Contains(csmRpathLoadSignature, StringComparison.Ordinal))
                    return true;
            return false;
        }

        // Local helper: classify a CSM exception as the documented device gap (Skip) or
        // a real failure (Fail). Surfaces the InnerException for triage.
        void CsmResult(string name, Exception ex)
        {
            string detail = ex is TypeInitializationException && ex.InnerException is { } inner
                ? $"{ex.GetType().Name} -> {inner.GetType().Name}: {inner.Message}"
                : $"{ex.GetType().Name}: {ex.Message}";
            if (!dynamicCodeSupported && ex is TypeInitializationException && IsRpathFrameworkLoadFailure(ex))
                Skip(name, $"{csmAotGapReason} — {runtimeFlavor} lane ({detail})");
            else
                Fail(name, $"[{runtimeFlavor}] {detail}");
        }

        // Test 30: HMAC<SHA256> incremental == one-shot, 32-byte MAC.
        try
        {
            var key = new SymmetricKey(SymmetricKeySize.Bits256);
            var msg = Encoding.UTF8.GetBytes("The quick brown fox jumps over the lazy dog");
            var oneShot = HMACSHA256CsmExtensions.AuthenticationCode(msg, key);
            var hmac = HMACSHA256CsmExtensions.FromSHA256(key);
            // Static-call form (not `hmac.Finalize()`) to avoid colliding with object.Finalize.
            HMACSHA256CsmExtensions.Update(hmac, msg[..20]);
            HMACSHA256CsmExtensions.Update(hmac, msg[20..]);
            var incremental = HMACSHA256CsmExtensions.Finalize(hmac);
            Log($"HMAC<SHA256> ByteCount={incremental.ByteCount} desc={incremental.Description}");
            if (incremental.ByteCount != 32)
                throw new InvalidOperationException($"unexpected MAC length {incremental.ByteCount}");
            if (incremental.Description != oneShot.Description)
                throw new InvalidOperationException(
                    $"incremental != one-shot: '{incremental.Description}' vs '{oneShot.Description}'");
            Pass("HMAC<SHA256> incremental == one-shot");
        }
        catch (Exception ex)
        {
            CsmResult("HMAC<SHA256> incremental == one-shot", ex);
        }

        // Test 31: HMAC<SHA384> incremental == one-shot, 48-byte MAC (second CSM specialization).
        try
        {
            var key = new SymmetricKey(SymmetricKeySize.Bits256);
            var msg = Encoding.UTF8.GetBytes("incremental HMAC over multiple Update calls");
            var oneShot = HMACSHA384CsmExtensions.AuthenticationCode(msg, key);
            var hmac = HMACSHA384CsmExtensions.FromSHA384(key);
            HMACSHA384CsmExtensions.Update(hmac, msg[..11]);
            HMACSHA384CsmExtensions.Update(hmac, msg[11..]);
            var incremental = HMACSHA384CsmExtensions.Finalize(hmac);
            Log($"HMAC<SHA384> ByteCount={incremental.ByteCount}");
            if (incremental.ByteCount != 48)
                throw new InvalidOperationException($"unexpected MAC length {incremental.ByteCount}");
            if (incremental.Description != oneShot.Description)
                throw new InvalidOperationException(
                    $"incremental != one-shot: '{incremental.Description}' vs '{oneShot.Description}'");
            Pass("HMAC<SHA384> incremental == one-shot");
        }
        catch (Exception ex)
        {
            CsmResult("HMAC<SHA384> incremental == one-shot", ex);
        }

        // Test 31b: HMAC known-answer vectors (RFC 4231 test cases 1 and 2).
        // Tests 30/31 compare CryptoKit's incremental HMAC against CryptoKit's own one-shot HMAC —
        // a self-consistency check that cannot catch a marshalling bug corrupting both paths the
        // same way (a truncated key, a mis-sized message span). These vectors are external: each MAC
        // is checked against the published RFC constant AND against .NET's own HMAC over the same
        // key/message, so three independent implementations must agree.
        // Classified through CsmResult: on device the HMAC CSM static cctor hits the documented
        // @rpath system-framework gap and Skips, exactly as Tests 30/31 do.
        try
        {
            int vectorsChecked = 0;
            void CheckMac(string label, string expectedHex, string actualHex, byte[] bclMac)
            {
                var bclHex = ToHex(bclMac);
                if (bclHex != expectedHex)
                    throw new InvalidOperationException(
                        $"{label}: the hard-coded RFC 4231 vector disagrees with .NET's own HMAC — vector '{expectedHex}', BCL '{bclHex}' (the constant in this test is wrong)");
                if (actualHex.Length != expectedHex.Length)
                    throw new InvalidOperationException(
                        $"{label}: CryptoKit description exposed {actualHex.Length} hex chars, expected {expectedHex.Length}");
                if (actualHex != expectedHex)
                    throw new InvalidOperationException($"{label}: CryptoKit produced '{actualHex}', expected '{expectedHex}'");
                vectorsChecked++;
            }

            // RFC 4231 test case 1: key = 0x0b x 20, data = "Hi There".
            var rfcKey1 = Enumerable.Repeat((byte)0x0b, 20).ToArray();
            var rfcData1 = Encoding.ASCII.GetBytes("Hi There");
            // RFC 4231 test case 2: key = "Jefe", data = "what do ya want for nothing?".
            var rfcKey2 = Encoding.ASCII.GetBytes("Jefe");
            var rfcData2 = Encoding.ASCII.GetBytes("what do ya want for nothing?");

            var k1 = SymmetricKey.FromByteArray(rfcKey1);
            var k2 = SymmetricKey.FromByteArray(rfcKey2);

            CheckMac("HMAC-SHA-256 RFC4231 TC1",
                "b0344c61d8db38535ca8afceaf0bf12b881dc200c9833da726e9376c2e32cff7",
                HexFromDescription(HMACSHA256CsmExtensions.AuthenticationCode(rfcData1, k1).Description),
                System.Security.Cryptography.HMACSHA256.HashData(rfcKey1, rfcData1));

            CheckMac("HMAC-SHA-256 RFC4231 TC2",
                "5bdcc146bf60754e6a042426089575c75a003f089d2739839dec58b964ec3843",
                HexFromDescription(HMACSHA256CsmExtensions.AuthenticationCode(rfcData2, k2).Description),
                System.Security.Cryptography.HMACSHA256.HashData(rfcKey2, rfcData2));

            CheckMac("HMAC-SHA-384 RFC4231 TC1",
                "afd03944d84895626b0825f4ab46907f15f9dadbe4101ec682aa034c7cebc59cfaea9ea9076ede7f4af152e8b2fa9cb6",
                HexFromDescription(HMACSHA384CsmExtensions.AuthenticationCode(rfcData1, k1).Description),
                System.Security.Cryptography.HMACSHA384.HashData(rfcKey1, rfcData1));

            CheckMac("HMAC-SHA-384 RFC4231 TC2",
                "af45d2e376484031617f78d2b58a6b1b9c7ef464f5a01b47e42ec3736322445e8e2240ca5e69e2c78b3239ecfab21649",
                HexFromDescription(HMACSHA384CsmExtensions.AuthenticationCode(rfcData2, k2).Description),
                System.Security.Cryptography.HMACSHA384.HashData(rfcKey2, rfcData2));

            if (vectorsChecked != 4)
                throw new InvalidOperationException($"expected 4 HMAC known-answer vectors, checked {vectorsChecked}");
            Log($"HMAC known-answer vectors verified: {vectorsChecked} (CryptoKit == RFC 4231 == .NET BCL)");
            Pass("HMAC-SHA-2 known-answer vectors (RFC 4231)");
        }
        catch (Exception ex)
        {
            CsmResult("HMAC-SHA-2 known-answer vectors (RFC 4231)", ex);
        }

        // Test 32: MLDSA65 context-string verify round-trip via the byte[] CSM overloads.
        //
        // ML-DSA (FIPS 204) is the post-quantum signature scheme exposed in CryptoKit on
        // iOS/macOS 26+; its `PublicKey.isValidSignature(_:for:context:)` and matching
        // `PrivateKey.signature(for:context:)` are the carriers of the 3-arg context-string
        // verify overloads — the ECDSA P256/P384/P521 surfaces have no context parameter
        // in Apple's API. Each unbound `where … DataProtocol` parameter expands into a
        // 2^N byte[]/Data cartesian on CSM, so PublicKey emits 8 IsValidSignature
        // overloads and PrivateKey emits 4 Signature(data, context) overloads. This test
        // drives the all-byte[] variants — the (byte[], byte[], byte[]) verify and the
        // (byte[], byte[]) sign — proving the wire carriers, the context-string parameter
        // routing, and the Data-return CSM (Swift.Foundation.Data → byte[] via the
        // existing CryptoKit Sign-side projection) all land round-trip.
        //
        // The CSM dispatches here are direct cdecl entries (SBW_CSM_..._signature_...,
        // SBW_CSM_..._isValidSignature_...) emitted by the per-package
        // CryptoKitSwiftBindings wrapper — they do NOT take the HMAC<H>-style
        // ConcreteSpecializationEngine static-cctor path, so the @rpath system-framework
        // dlopen gap (Test 30/31's NativeAOT-only Skip) does not affect this test. Sim
        // gate only — device coverage matches the existing CryptoKit cell.
        try
        {
            using var privateKey = new MLDSA65.PrivateKey();
            using var publicKey = privateKey.PublicKey;
            var msg = Encoding.UTF8.GetBytes("post-quantum context-string verify");
            var context = Encoding.UTF8.GetBytes("application/cryptokit-test/v1");
            var wrongContext = Encoding.UTF8.GetBytes("application/cryptokit-test/v2");
            var sig = privateKey.Signature(msg, context);
            if (sig is null || sig.Length == 0)
                throw new InvalidOperationException("Signature returned empty");
            if (!publicKey.IsValidSignature(sig, msg, context))
                throw new InvalidOperationException("verify with matching context returned false");
            if (publicKey.IsValidSignature(sig, msg, wrongContext))
                throw new InvalidOperationException("verify with wrong context returned true");
            Pass("MLDSA65 context-string verify round-trip");
        }
        catch (Exception ex)
        {
            Fail("MLDSA65 context-string verify round-trip", ex.Message);
        }

        // Test 33: Curve25519.Signing (Ed25519) sign + verify round-trip via the byte[] CSM
        // overloads. Earlier SDKs surfaced PrivateKey.Signature only as the [Obsolete] open
        // generic `Signature<D>` — there was no callable C# entrypoint, so the guide listed
        // Curve25519 signing as verification-only. The CSM engine now emits concrete
        // byte[]/Foundation.Data overloads (SBW_CSM_..._PrivateKey_..._signature_...), and
        // PublicKey.IsValidSignature has its 4-overload byte[]/Data cartesian. This test
        // exercises the all-byte[] variants of both halves to lock the fix in place.
        try
        {
            using var privateKey = new Curve25519.Signing.PrivateKey();
            using var publicKey = privateKey.PublicKey;
            var msg = Encoding.UTF8.GetBytes("ed25519 round-trip");
            var sig = privateKey.Signature(msg);
            if (sig is null || sig.Length == 0)
                throw new InvalidOperationException("Signature returned empty");
            if (!publicKey.IsValidSignature(sig, msg))
                throw new InvalidOperationException("verify with matching message returned false");
            var tampered = (byte[])msg.Clone();
            tampered[0] ^= 0x01;
            if (publicKey.IsValidSignature(sig, tampered))
                throw new InvalidOperationException("verify with tampered message returned true");
            Pass("Curve25519.Signing sign + verify round-trip");
        }
        catch (Exception ex)
        {
            Fail("Curve25519.Signing sign + verify round-trip", ex.Message);
        }

        // Test 34: NIST P-curve ECDSA signing keys — key generation + public-key
        // serialization round-trip with curve-defined byte lengths (a real semantic
        // invariant, not construct-and-read-metadata). Each key's public-key raw
        // representation is x||y (2 * field-size bytes); X9.63 is a 0x04 prefix + raw;
        // compressed is a 1-byte prefix + x-coordinate (field-size + 1).
        //
        // NOTE: the full P256/P384/P521 ECDSA sign -> verify known-answer test is Skipped
        // just below — the generator does not yet emit a callable sign entrypoint for
        // these curves (see that Skip for the exact gap).
        try
        {
            using var p256 = new P256.Signing.PrivateKey();
            using var p256Pub = p256.PublicKey;
            if (p256Pub.RawRepresentation.Length != 64)
                throw new InvalidOperationException($"P256 raw len {p256Pub.RawRepresentation.Length} != 64");
            if (p256Pub.X963Representation.Length != 65)
                throw new InvalidOperationException($"P256 x963 len {p256Pub.X963Representation.Length} != 65");
            if (p256Pub.CompressedRepresentation.Length != 33)
                throw new InvalidOperationException($"P256 compressed len {p256Pub.CompressedRepresentation.Length} != 33");

            using var p384 = new P384.Signing.PrivateKey();
            using var p384Pub = p384.PublicKey;
            if (p384Pub.RawRepresentation.Length != 96)
                throw new InvalidOperationException($"P384 raw len {p384Pub.RawRepresentation.Length} != 96");
            if (p384Pub.X963Representation.Length != 97)
                throw new InvalidOperationException($"P384 x963 len {p384Pub.X963Representation.Length} != 97");
            if (p384Pub.CompressedRepresentation.Length != 49)
                throw new InvalidOperationException($"P384 compressed len {p384Pub.CompressedRepresentation.Length} != 49");

            using var p521 = new P521.Signing.PrivateKey();
            using var p521Pub = p521.PublicKey;
            if (p521Pub.RawRepresentation.Length != 132)
                throw new InvalidOperationException($"P521 raw len {p521Pub.RawRepresentation.Length} != 132");
            if (p521Pub.X963Representation.Length != 133)
                throw new InvalidOperationException($"P521 x963 len {p521Pub.X963Representation.Length} != 133");
            if (p521Pub.CompressedRepresentation.Length != 67)
                throw new InvalidOperationException($"P521 compressed len {p521Pub.CompressedRepresentation.Length} != 67");

            Pass("P256/P384/P521 signing key generation + public-key serialization");
        }
        catch (Exception ex)
        {
            Fail("P256/P384/P521 signing key generation + public-key serialization", ex.Message);
        }

        // Test 34b: ownership accounting on repeated large owned-Data returns.
        //
        // `AES.GCM.open(_:using:)` returns a Swift `Data` that the wrapper hands back as a fresh
        // byte[]. If the wrapper does not balance the +1 the Swift side gave it, every call strands
        // the plaintext buffer natively — invisible to the GC, so the managed heap stays flat while
        // the process footprint climbs. We drive 2000 opens of a 1 MiB payload and watch the Mach
        // resident set: a one-buffer-per-call leak is ~2 GiB, which would jetsam the app before the
        // loop ends, so the probe checks every 100 iterations and FAILS FAST once growth crosses
        // 256 MiB rather than letting the process die and take the remaining tests with it.
        // The bound is deliberately generous (256 MiB over 2 GiB of traffic) so ordinary allocator
        // retention and page reuse cannot red it; a real per-call leak crosses it inside ~300 calls.
        try
        {
            var baselineRss = ResidentBytes();
            if (baselineRss == 0)
            {
                Fail("AES.GCM.Open ownership accounting (2000 x 1 MiB)",
                     "mach task_info resident-size probe unavailable — cannot measure native footprint, so this cannot be asserted");
            }
            else
            {
                const int iterations = 2000;
                const int payloadBytes = 1024 * 1024;
                const ulong growthBudget = 256UL * 1024 * 1024;

                var key = new SymmetricKey(SymmetricKeySize.Bits256);
                var payload = new byte[payloadBytes];
                new Random(20260905).NextBytes(payload);
                var box = AES.GCM.Seal(payload, key);

                // Warm up so first-call allocator growth is not counted as a leak.
                for (int i = 0; i < 10; i++)
                    _ = AES.GCM.Open(box, key);
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                var startRss = ResidentBytes();

                ulong worstGrowth = 0;
                int completed = 0;
                for (int i = 0; i < iterations; i++)
                {
                    var opened = AES.GCM.Open(box, key);
                    if (opened is null || opened.Length != payloadBytes)
                        throw new InvalidOperationException(
                            $"iteration {i}: Open returned {opened?.Length ?? -1} bytes, expected {payloadBytes}");
                    completed = i + 1;

                    if ((i + 1) % 100 == 0)
                    {
                        GC.Collect();
                        GC.WaitForPendingFinalizers();
                        GC.Collect();
                        var now = ResidentBytes();
                        var growth = now > startRss ? now - startRss : 0UL;
                        if (growth > worstGrowth)
                            worstGrowth = growth;
                        if (growth > growthBudget)
                            throw new InvalidOperationException(
                                $"resident set grew {growth / (1024 * 1024)} MiB after {completed} opens of {payloadBytes / 1024} KiB " +
                                $"(budget {growthBudget / (1024 * 1024)} MiB) — the owned Data returned by AES.GCM.open is not being released");
                    }
                }

                Log($"AES.GCM.Open x{completed} of {payloadBytes / 1024} KiB: peak RSS growth {worstGrowth / (1024 * 1024)} MiB " +
                    $"(start {startRss / (1024 * 1024)} MiB, budget {growthBudget / (1024 * 1024)} MiB)");
                Pass("AES.GCM.Open ownership accounting (2000 x 1 MiB)");
            }
        }
        catch (Exception ex)
        {
            Fail("AES.GCM.Open ownership accounting (2000 x 1 MiB)", $"{ex.GetType().Name}: {ex.Message}");
        }

        // Test 34c: HKDF<SHA256> key derivation against RFC 5869 test vectors.
        //
        // This is the derivation surface a consumer actually has: SharedSecret's own
        // hkdfDerivedSymmetricKey / x963DerivedSymmetricKey are not projected (they constrain a
        // protocol with associated types), but the free-standing HKDF<H>.deriveKey specializations
        // are, as HKDFSHA256CsmExtensions.DeriveKey(inputKeyMaterial, salt, info, outputByteCount).
        // Two overload shapes are exercised: the salt+info form (RFC 5869 test case 1) and the
        // info-only form, whose salt defaults to empty (test case 3).
        //
        // Each vector is checked against the published RFC constant AND against .NET's own
        // System.Security.Cryptography.HKDF in-process, so a typo in a constant here fails loudly
        // rather than agreeing with itself. SymmetricKey exposes no raw-byte accessor through the
        // binding, so the derived octets are compared through the generated native equality
        // operator against a key imported from the expected bytes — a derivation that produced the
        // wrong bytes, or a length silently truncated on the way in, reds this.
        try
        {
            var ikm = new byte[22];
            Array.Fill(ikm, (byte)0x0b);
            var salt = new byte[] { 0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0a, 0x0b, 0x0c };
            var info = new byte[] { 0xf0, 0xf1, 0xf2, 0xf3, 0xf4, 0xf5, 0xf6, 0xf7, 0xf8, 0xf9 };
            const int okmLen = 42;
            const string tc1Okm =
                "3cb25f25faacd57a90434f64d0362f2a2d2d0a90cf1a5a4c5db02d56ecc4c5bf34007208d5b887185865";
            const string tc3Okm =
                "8da4e775a563c18f715f802a063c5a31b8a11f5c5ee1879ec3454e5f3c738d2d9d201395faa4b61a96c8";

            void CheckDerived(string label, string expectedHex, byte[] bclOkm, SymmetricKey derived)
            {
                var bclHex = ToHex(bclOkm);
                if (bclHex != expectedHex)
                    throw new InvalidOperationException(
                        $"{label}: the hard-coded vector disagrees with .NET's own HKDF — vector '{expectedHex}', " +
                        $"BCL '{bclHex}' (the constant in this test is wrong)");
                if (derived.BitCount != expectedHex.Length / 2 * 8)
                    throw new InvalidOperationException(
                        $"{label}: derived key is {derived.BitCount} bits, expected {expectedHex.Length / 2 * 8}");
                using var expected = SymmetricKey.FromByteArray(Convert.FromHexString(expectedHex));
                if (!derived.Equals(expected))
                    throw new InvalidOperationException(
                        $"{label}: CryptoKit's derived key does not equal the RFC 5869 output key material");
                Log($"{label}: {expectedHex.Length / 2}-byte OKM matches RFC 5869 and .NET HKDF");
            }

            using var ikmKey = SymmetricKey.FromByteArray(ikm);

            using var tc1 = HKDFSHA256CsmExtensions.DeriveKey(ikmKey, salt, info, okmLen);
            CheckDerived(
                "HKDF<SHA256> deriveKey(ikm, salt, info, 42) [RFC 5869 TC1]",
                tc1Okm,
                System.Security.Cryptography.HKDF.DeriveKey(
                    System.Security.Cryptography.HashAlgorithmName.SHA256, ikm, okmLen, salt, info),
                tc1);

            using var tc3 = HKDFSHA256CsmExtensions.DeriveKey(ikmKey, Array.Empty<byte>(), okmLen);
            CheckDerived(
                "HKDF<SHA256> deriveKey(ikm, info, 42) [RFC 5869 TC3, empty salt+info]",
                tc3Okm,
                System.Security.Cryptography.HKDF.DeriveKey(
                    System.Security.Cryptography.HashAlgorithmName.SHA256, ikm, okmLen,
                    Array.Empty<byte>(), Array.Empty<byte>()),
                tc3);

            Pass("HKDF<SHA256> deriveKey known-answer (RFC 5869 TC1 + TC3)");
        }
        catch (Exception ex)
        {
            CsmResult("HKDF<SHA256> deriveKey known-answer (RFC 5869 TC1 + TC3)", ex);
        }

        // Test 34d: ECDH shared secret -> HKDF -> AES-GCM, the whole route the guide describes.
        //
        // SymmetricKey.FromCryptoKitSharedSecret converts the raw agreement output into key
        // material; HKDF turns that material into an encryption key of a chosen length under a
        // salt and a context string. The assertions are that both sides land on the same key
        // (they exchanged nothing but public keys), that the derived key is the requested size,
        // that a different context string yields a different key (so the info parameter is
        // actually reaching the native call rather than being dropped), and that the derived key
        // decrypts what it encrypted.
        try
        {
            using var alicePrivate = new Curve25519.KeyAgreement.PrivateKey();
            using var bobPrivate = new Curve25519.KeyAgreement.PrivateKey();
            using var alicePublic = alicePrivate.PublicKey;
            using var bobPublic = bobPrivate.PublicKey;

            using var aliceSecret = alicePrivate.SharedSecretFromKeyAgreement(bobPublic);
            using var bobSecret = bobPrivate.SharedSecretFromKeyAgreement(alicePublic);
            if (!aliceSecret.Equals(bobSecret))
                throw new InvalidOperationException("the two ECDH shared secrets differ");

            var salt = Encoding.UTF8.GetBytes("swiftbindings-cryptokit-test-salt");
            var info = Encoding.UTF8.GetBytes("aes-gcm session key v1");
            var otherInfo = Encoding.UTF8.GetBytes("aes-gcm session key v2");

            using var aliceIkm = SymmetricKey.FromCryptoKitSharedSecret(aliceSecret);
            using var bobIkm = SymmetricKey.FromCryptoKitSharedSecret(bobSecret);
            using var aliceKey = HKDFSHA256CsmExtensions.DeriveKey(aliceIkm, salt, info, 32);
            using var bobKey = HKDFSHA256CsmExtensions.DeriveKey(bobIkm, salt, info, 32);
            using var otherContextKey = HKDFSHA256CsmExtensions.DeriveKey(aliceIkm, salt, otherInfo, 32);

            if (aliceKey.BitCount != 256)
                throw new InvalidOperationException($"derived key is {aliceKey.BitCount} bits, expected 256");
            if (!aliceKey.Equals(bobKey))
                throw new InvalidOperationException(
                    "the two sides derived different keys from the same shared secret, salt and info");
            if (aliceKey.Equals(otherContextKey))
                throw new InvalidOperationException(
                    "changing the info string did not change the derived key — the context is not reaching HKDF");

            var message = Encoding.UTF8.GetBytes("ECDH -> HKDF -> AES-GCM round trip");
            using var box = AES.GCM.Seal(message, aliceKey);
            var opened = AES.GCM.Open(box, bobKey);
            if (opened is null || !opened.AsSpan().SequenceEqual(message))
                throw new InvalidOperationException(
                    $"round trip returned {opened?.Length ?? -1} bytes, expected {message.Length} matching bytes");

            Log($"ECDH -> HKDF -> AES-GCM: derived {aliceKey.BitCount}-bit key on both sides, round trip OK");
            Pass("ECDH shared secret -> HKDF<SHA256> -> AES.GCM round trip");
        }
        catch (Exception ex)
        {
            CsmResult("ECDH shared secret -> HKDF<SHA256> -> AES.GCM round trip", ex);
        }

        // Test 35 (Skip): P256/P384/P521 ECDSA sign -> verify known-answer test. Blocked by a
        // generator capability gap: PrivateKey.signature(for:) and PublicKey.isValidSignature
        // surface ONLY as the uncallable [Obsolete] open-generic Signature<D>/IsValidSignature<D>
        // (constrained to Swift DataProtocol; no witness wiring from C#). The CSM engine emits a
        // closed byte[]/Data sign overload for Curve25519 (whose sign returns Data — see Test 33),
        // but NOT for the P-curves, whose sign returns the nested ECDSASignature struct and for
        // which no closed overload nor a byte-array ECDSASignature ctor is emitted. Revisit when
        // the generator emits closed sign/verify overloads for generic-DataProtocol methods
        // returning a nested value type; then clone Test 33's sign/verify/tamper shape per curve.
        Skip("P256/P384/P521 ECDSA sign+verify known-answer test",
            "generator gap: signature(for:)/isValidSignature only as uncallable [Obsolete] open-generic; no closed byte[]/Data overload for ECDSASignature-returning sign");

        // Summary
        Log($"Results: {passed} passed, {failed} failed, {skipped} skipped");
        if (failed == 0)
            Log("TEST SUCCESS", prefixed: false);
        else
            Log($"TEST FAILED: {failed} failures", prefixed: false);
        return failed;
    }

    internal static void Log(string msg, bool prefixed = true) =>
        Console.WriteLine(prefixed ? $"[CRYPTOKIT-TEST] {msg}" : msg);
}
