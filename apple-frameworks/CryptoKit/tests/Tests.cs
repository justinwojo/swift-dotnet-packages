// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using CryptoKit;
using Swift.Runtime;

namespace SwiftBindings.CryptoKit.Tests;

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

        // Test 4: Sha3256 metadata loads.
        MetadataTest<Sha3256>("Sha3256");

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
            if ((int)HPKE.KDF.HkdfSha256 != 0)
                throw new InvalidOperationException($"expected 0, got {(int)HPKE.KDF.HkdfSha256}");
            if ((int)HPKE.KDF.HkdfSha384 != 1)
                throw new InvalidOperationException($"expected 1, got {(int)HPKE.KDF.HkdfSha384}");
            if ((int)HPKE.KDF.HkdfSha512 != 2)
                throw new InvalidOperationException($"expected 2, got {(int)HPKE.KDF.HkdfSha512}");
            Pass("HPKE.KDF values");
        }
        catch (Exception ex)
        {
            Fail("HPKE.KDF values", ex.Message);
        }

        // Test 12: HPKE.KEM plain enum values are correct.
        try
        {
            if ((int)HPKE.KEM.P256HkdfSha256 != 0)
                throw new InvalidOperationException($"expected 0, got {(int)HPKE.KEM.P256HkdfSha256}");
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
        MetadataTest<P256.Signing.ECDSASignature>("P256.Signing.ECDSASignature");
        MetadataTest<P384.Signing.ECDSASignature>("P384.Signing.ECDSASignature");
        MetadataTest<P521.Signing.ECDSASignature>("P521.Signing.ECDSASignature");

        // Test 31: AES.GCM.Nonce metadata loads.
        MetadataTest<AES.GCM.Nonce>("AES.GCM.Nonce");

        // Test 23: ChaChaPoly.Nonce metadata loads.
        MetadataTest<ChaChaPoly.Nonce>("ChaChaPoly.Nonce");

        // Test 24: Insecure.SHA1 metadata loads.
        MetadataTest<Insecure.SHA1>("Insecure.SHA1");

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
