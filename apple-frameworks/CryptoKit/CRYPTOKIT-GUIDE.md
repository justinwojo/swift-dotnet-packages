# CryptoKit for .NET — Usage Guide

`SwiftBindings.Apple.CryptoKit` exposes Apple's [CryptoKit](https://developer.apple.com/documentation/cryptokit) framework — hashing, message authentication, authenticated encryption, key agreement, and digital signatures — to C# through .NET 10's native Swift interop. These are direct Swift calls, not Objective-C proxy wrappers. CryptoKit's API leans heavily on Swift generics (`<D: DataProtocol>` everywhere), and that has a direct effect on which overloads are usable from C#: the generator emits concrete `byte[]` / `Foundation.Data` specializations of the most common generic methods, and those are the ones you call. This guide maps the working surface and is explicit about the corners that are not reachable from C#.

## Contents

- [Requirements & install](#requirements--install)
- [Naming conventions](#naming-conventions)
- [Quick start: authenticated encryption](#quick-start-authenticated-encryption)
- [Symmetric keys](#symmetric-keys)
- [Authenticated encryption (AES-GCM, ChaChaPoly)](#authenticated-encryption-aes-gcm-chachapoly)
- [Hashing](#hashing)
- [Message authentication (HMAC)](#message-authentication-hmac)
- [Key agreement](#key-agreement)
- [Key derivation (HKDF)](#key-derivation-hkdf)
- [Digital signatures](#digital-signatures)
- [Errors](#errors)
- [Known limitations](#known-limitations)
- [Memory & threading](#memory--threading)
- [Reference links](#reference-links)

## Requirements & install

- .NET 10.0+
- **Target framework: `net10.0-ios26.2` (or `-macos26.2` / `-maccatalyst26.2` / `-tvos26.2`) or higher.** The package is compiled against the iOS 26.2 SDK supplement, so your app's `<TargetFramework>` platform version must be **≥ 26.2** to restore it — an explicit lower TPV (e.g. `net10.0-ios18.0`) or a bare `net10.0-ios` whose installed workload defaults below 26.2 fails restore with `NU1202`. This is the compile-SDK pin, **not** your app's deployment minimum.
- **Deployment minimum is separate** — set your app's real minimum OS with `<SupportedOSPlatformVersion>` (independent of the TFM above). Individual APIs that need a newer OS at runtime are gated by generated `[SupportedOSPlatform]` attributes; the C# compiler flags a call that isn't valid for your target.
- macOS host for development

```
dotnet add package SwiftBindings.Apple.CryptoKit
```

```csharp
using CryptoKit;
```

> **Child namespaces aren't imported by `using CryptoKit;`.** CryptoKit's "caseless enum" namespaces (`AES`, `ChaChaPoly`, `Insecure`, `HPKE`, `P256`, `P384`, `P521`, `Curve25519`) project as **child namespaces** of `CryptoKit`, not as static types. `using CryptoKit;` brings in the top-level types (`SHA256`, `SymmetricKey`, …) but not those child namespaces. Reference them fully-qualified (`CryptoKit.AES.GCM`) or add a `using` alias:
>
> ```csharp
> using AES = CryptoKit.AES;
> using ChaChaPoly = CryptoKit.ChaChaPoly;
> using Insecure = CryptoKit.Insecure;
> using P256 = CryptoKit.P256;
> ```

## Naming conventions

| Swift | C# | Rule |
|---|---|---|
| `func seal<Plaintext: DataProtocol>(...)` | `Seal(byte[] message, …)` / `Seal(Data message, …)` | generic methods get **concrete `byte[]` and `Foundation.Data` overloads**; the open-generic `Seal<TPlaintext>(...)` form is also emitted but uses the raw Swift calling convention and is marked `[Obsolete("SB0001")]` |
| `func finalize() -> Digest` | `FinalizeSwift()` | `finalize` is a reserved word in the runtime, so it is suffixed |
| `enum CryptoKitError { case incorrectKeySize }` | `CryptoKitError` class with `.Tag` (a `CaseTag` enum) + static singletons | Swift enums project to a class with a `CaseTag`; payload-less cases are static properties (`CryptoKitError.IncorrectKeySize`) |
| `init(rawValue:)` / `init(data:)` style | `SymmetricKey.FromData(...)`, `SymmetricKey.FromByteArray(...)` | failable / overloaded initializers become named `From…` static factories |
| `var bitCount: Int` | `BitCount` (`int`) | properties are PascalCase |
| `enum CryptoKitASN1Error: Int` (plain) | C# `enum : int` | plain Swift enums without payloads become ordinary C# enums |

The headline rule for CryptoKit: **prefer the concrete `byte[]` / `Foundation.Data` overload of any method.** The open-generic overloads (`Seal<TPlaintext>`, `Open<TAuthenticatedData>`, `IsValidSignature<S,D>`, `Signature<D>`) are emitted only as `[Obsolete]` stubs and will not run.

## Quick start: authenticated encryption

```csharp
using System.Text;
using CryptoKit;
using AES = CryptoKit.AES;

// 1. Generate a fresh 256-bit key
var key = new SymmetricKey(SymmetricKeySize.Bits256);

// 2. Encrypt — Seal returns a SealedBox bundling nonce + ciphertext + tag
byte[] plaintext = Encoding.UTF8.GetBytes("secret message");
AES.GCM.SealedBox box = AES.GCM.Seal(plaintext, key);

// 3. Persist / transmit the combined representation (nonce || ciphertext || tag)
byte[] onTheWire = box.Combined!;

// 4. Decrypt — Open authenticates the tag and throws on tamper / wrong key
byte[] recovered = AES.GCM.Open(box, key);

Console.WriteLine(Encoding.UTF8.GetString(recovered)); // "secret message"
```

This round-trip — including ChaChaPoly and the wrong-key tamper path — is validated end-to-end on both Mono JIT (simulator) and NativeAOT (device).

## Symmetric keys

`SymmetricKey` is the shared-secret key type used by AES-GCM, ChaChaPoly, and HMAC. `SymmetricKeySize` describes a bit length.

```csharp
// Generate a random key of a standard size
var k256 = new SymmetricKey(SymmetricKeySize.Bits256);
var k128 = new SymmetricKey(SymmetricKeySize.Bits128);

int bits = k256.BitCount;   // 256
```

`SymmetricKeySize` static sizes and constructor:

| Member | Type | |
|---|---|---|
| `SymmetricKeySize.Bits128` | `SymmetricKeySize` | 128-bit preset |
| `SymmetricKeySize.Bits192` | `SymmetricKeySize` | 192-bit preset |
| `SymmetricKeySize.Bits256` | `SymmetricKeySize` | 256-bit preset |
| `new SymmetricKeySize(nint bitCount)` | — | custom bit length |
| `.BitCount` | `int` | the size in bits |

Importing key material instead of generating it:

```csharp
byte[] raw = GetKeyBytesFromKeychain();          // your 32 bytes
SymmetricKey key = SymmetricKey.FromByteArray(raw);

// or from Foundation.Data:
SymmetricKey key2 = SymmetricKey.FromData(someData);
```

`SymmetricKey` also offers `FromData`, `FromByteArray`, and a family of `From…` factories over CryptoKit's own byte-carrying types — `FromCryptoKitSHA256Digest`, `FromCryptoKitSHA384Digest`, `FromCryptoKitSHA512Digest`, `FromCryptoKitSharedSecret`, and so on. These are **conversions, not derivations**: they wrap the bytes they are given as key material. Turning key material into an encryption key of a chosen length is HKDF's job — see [Key derivation (HKDF)](#key-derivation-hkdf). `SymmetricKey` exposes `BitCount`, value equality (`==`, `Equals`), and `IDisposable`.

## Authenticated encryption (AES-GCM, ChaChaPoly)

Both `AES.GCM` and `ChaChaPoly` are static classes exposing `Seal` and `Open`. The usable overloads take `byte[]` or `Foundation.Data` for the message and authenticated-data arguments.

### AES-GCM

```csharp
using AES = CryptoKit.AES;

var key = new SymmetricKey(SymmetricKeySize.Bits256);

// Seal: message + key
AES.GCM.SealedBox box = AES.GCM.Seal(plaintext, key);

// Seal with additional authenticated data (AAD) — authenticated but not encrypted
byte[] aad = Encoding.UTF8.GetBytes("header-v1");
AES.GCM.SealedBox box2 = AES.GCM.Seal(plaintext, key, aad);

// Open: returns the recovered plaintext as byte[], throws on failure
byte[] recovered = AES.GCM.Open(box, key);
```

`AES.GCM.SealedBox` exposes:

| Member | Type | |
|---|---|---|
| `Ciphertext` | `byte[]` | the encrypted bytes |
| `Tag` | `byte[]` | the 16-byte (128-bit) authentication tag |
| `Nonce` | `AES.GCM.Nonce` | the nonce used |
| `Combined` | `byte[]?` | `nonce ‖ ciphertext ‖ tag` — store/transmit this |

Available `Seal` overloads (all return `AES.GCM.SealedBox`):

```csharp
SealedBox Seal(byte[] message, SymmetricKey key);
SealedBox Seal(Foundation.Data message, SymmetricKey key);
SealedBox Seal(byte[] message, SymmetricKey key, byte[] authenticatedData);
SealedBox Seal(byte[] message, SymmetricKey key, Foundation.Data authenticatedData);
SealedBox Seal(Foundation.Data message, SymmetricKey key, byte[] authenticatedData);
SealedBox Seal(Foundation.Data message, SymmetricKey key, Foundation.Data authenticatedData);
```

Usable `Open` overload:

```csharp
byte[] Open(AES.GCM.SealedBox sealedBox, SymmetricKey key);
```

> A nonce is generated automatically by these overloads. There is no usable `Seal(message, key, nonce)` overload (it is part of the obsolete generic surface). There is also no public `SealedBox(nonce:, ciphertext:, tag:)` constructor — to rebuild a box for `Open`, keep the `Combined` bytes and the same box instance, since reconstruction from raw parts is a Swift generic init the generator does not emit.

### ChaChaPoly

Identical shape, in the `CryptoKit.ChaChaPoly` namespace:

```csharp
using ChaChaPoly = CryptoKit.ChaChaPoly;

var box = ChaChaPoly.Seal(plaintext, key);
byte[] recovered = ChaChaPoly.Open(box, key);
```

`ChaChaPoly.SealedBox` exposes `Ciphertext`, `Tag`, `Nonce` (a `ChaChaPoly.Nonce`), and `Combined` — note `Combined` here is a non-null `byte[]`.

### Tamper detection

`Open` authenticates before returning. Opening with the wrong key (or tampered ciphertext) throws — it does not return garbage:

```csharp
try
{
    byte[] recovered = AES.GCM.Open(box, wrongKey);
}
catch (Exception ex)   // surfaces CryptoKit's authenticationFailure
{
    // do NOT trust any bytes — authentication failed
}
```

## Hashing

CryptoKit's SHA-2, SHA-3, and the insecure legacy hashes are exposed as **incremental** hashers: construct, `Update` one or more times, then `FinalizeSwift()` to get the digest.

> **There is no one-shot `SHA256.Hash(data)` static method.** Swift's `static func hash(data:)` is a generic protocol requirement and is not projected to C#. Use the instance flow below.

```csharp
using System.Text;
using CryptoKit;

byte[] data = Encoding.UTF8.GetBytes("hello");

using var hasher = new SHA256();
hasher.Update(data);                 // Update(ReadOnlySpan<byte>)
SHA256Digest digest = hasher.FinalizeSwift();

string hex = digest.Description;     // hex string, e.g. "SHA256 digest: 2cf24d…"
```

Available hash functions and their digest types:

| Hasher | Construct | Digest type | Static info |
|---|---|---|---|
| `SHA256` | `new SHA256()` | `SHA256Digest` | `SHA256.ByteCount`, `SHA256.BlockByteCount` |
| `SHA384` | `new SHA384()` | `SHA384Digest` | `SHA384.ByteCount`, … |
| `SHA512` | `new SHA512()` | `SHA512Digest` | `SHA512.ByteCount`, … |
| `SHA3_256` | `new SHA3_256()` | `SHA3_256Digest` | `SHA3_256.ByteCount`, … |
| `SHA3_384` | `new SHA3_384()` | `SHA3_384Digest` | `SHA3_384.ByteCount`, … |
| `SHA3_512` | `new SHA3_512()` | `SHA3_512Digest` | `SHA3_512.ByteCount`, … |
| `Insecure.MD5` | `new Insecure.MD5()` | `Insecure.MD5Digest` | legacy / interop only |
| `Insecure.SHA1` | `new Insecure.SHA1()` | `Insecure.SHA1Digest` | legacy / interop only |

Each hasher exposes:

```csharp
void Update(ReadOnlySpan<byte> bufferPointer);   // call repeatedly to stream
TDigest FinalizeSwift();                          // produce the digest
```

Digest types (`SHA256Digest`, …) implement `IDigest<T>`, value equality (`==`, `Equals`, `GetHashCode`), expose static `ByteCount`, and render as a hex string via `Description` / `ToString()`. They do not expose a raw `byte[]` accessor. Use `Description` for display, or hand the digest to one of `SymmetricKey`'s digest factories — `SymmetricKey.FromCryptoKitSHA256Digest(digest)` and its SHA-384/SHA-512/SHA-3 siblings — which wrap the digest's bytes as key material (see [Symmetric keys](#symmetric-keys)). No generated member signs a digest: Ed25519 signing takes `byte[]` / `Foundation.Data`, and the P-curve `Signature` members are open generics marked `[Obsolete("SB0001")]` (see [Digital signatures](#digital-signatures)).

> `Insecure.MD5` and `Insecure.SHA1` are intentionally namespaced under `Insecure` because they are cryptographically broken. Use them only for non-security interop (e.g. matching a legacy checksum), never for new security work.

## Message authentication (HMAC)

HMAC is exposed as **static helpers per hash function**, on the extension classes `HMACSHA256CsmExtensions`, `HMACSHA384CsmExtensions`, and `HMACSHA512CsmExtensions`. Both the one-shot path and the incremental builder are reachable through these classes: the CSM (concrete-specialization) engine monomorphizes `HMAC<H>` into per-hash factories — `HMACSHA256CsmExtensions.FromSHA256(key)`, `HMACSHA384CsmExtensions.FromSHA384(key)`, etc. — that hand you a real `HMAC<H>` to feed with `Update`. You can also construct `HMAC<H>` directly with `new HMAC<SHA256>(key)` (same simulator-only NativeAOT caveat as the `From{Hash}` factories — see below).

> **Simulator only (incremental path).** The `From{Hash}` → `Update` → `Finalize` path works on the simulator (Mono JIT) but currently **throws on a NativeAOT device build** — resolving the `HashFunction` protocol conformance descriptor triggers a `dlopen("@rpath/CryptoKit.framework/CryptoKit")` call that fails because CryptoKit is an Apple system framework at `/System/Library/Frameworks`, not bundled in the app's `@rpath` (`TypeInitializationException` → `SwiftRuntimeException: Unable to load library`). The **one-shot** `AuthenticationCode(...)` helpers below work on both simulator and device. Tracked in `apple-framework-gaps/05-residual-gaps.md`; prefer the one-shot helpers for shipped device code.

```csharp
using CryptoKit;

var key = new SymmetricKey(SymmetricKeySize.Bits256);
byte[] message = Encoding.UTF8.GetBytes("authenticate me");

// Compute a SHA-256 HMAC
HashedAuthenticationCode<SHA256> mac =
    HMACSHA256CsmExtensions.AuthenticationCode(message, key);

// Verify a received code against the message (constant-time inside Swift)
byte[] receivedMac = GetMacBytes();
bool ok = HMACSHA256CsmExtensions.IsValidAuthenticationCode(receivedMac, message, key);
```

Each `HMACSHA{256,384,512}CsmExtensions` class provides:

| Method | Returns | |
|---|---|---|
| `AuthenticationCode(byte[] data, SymmetricKey key)` | `HashedAuthenticationCode<H>` | compute the MAC |
| `AuthenticationCode(Foundation.Data data, SymmetricKey key)` | `HashedAuthenticationCode<H>` | |
| `IsValidAuthenticationCode(byte[] code, byte[] data, SymmetricKey key)` | `bool` | verify raw MAC bytes |
| `IsValidAuthenticationCode(Foundation.Data code, …)` | `bool` | several `code`-type overloads (`byte[]`, `Data`, `SymmetricKey`, the `*Digest` types) |
| `From{Hash}(SymmetricKey key)` | `HMAC<H>` | construct the incremental builder (`FromSHA256`, `FromSHA384`, …) — **simulator only** (throws on NativeAOT device; see note above) |
| `Update(HMAC<H> self, byte[] data)` | `void` | incremental update; call repeatedly |
| `Finalize(HMAC<H> self)` | `HashedAuthenticationCode<H>` | finish and return the MAC |

The incremental flow mirrors Swift's `var h = HMAC<SHA256>(key:); h.update(data:); h.finalize()`:

```csharp
// Simulator-only incremental path (use the one-shot AuthenticationCode on device)
var hmac = HMACSHA256CsmExtensions.FromSHA256(key);
HMACSHA256CsmExtensions.Update(hmac, message[..20]);   // static-call form avoids
HMACSHA256CsmExtensions.Update(hmac, message[20..]);   // clashing with object.Finalize
HashedAuthenticationCode<SHA256> mac = HMACSHA256CsmExtensions.Finalize(hmac);
// byte-identical to HMACSHA256CsmExtensions.AuthenticationCode(message, key)
```

`HashedAuthenticationCode<H>` is `IDisposable` and does not expose a raw-bytes accessor directly; to verify, feed the received MAC bytes into the matching `IsValidAuthenticationCode(byte[] code, …)` overload rather than comparing bytes yourself.

## Key agreement

Elliptic-curve Diffie-Hellman is available on the `KeyAgreement` nested classes of `Curve25519`, `P256`, `P384`, and `P521`. Generate a private key, exchange public keys, and derive a `SharedSecret`.

```csharp
using CryptoKit;

// Each side generates a key pair
using var alicePrivate = new Curve25519.KeyAgreement.PrivateKey();
using var bobPrivate   = new Curve25519.KeyAgreement.PrivateKey();

Curve25519.KeyAgreement.PublicKey alicePublic = alicePrivate.PublicKey;
Curve25519.KeyAgreement.PublicKey bobPublic   = bobPrivate.PublicKey;

// Exchange the public keys (alicePublic.RawRepresentation is the byte[] to send),
// then each side derives the same shared secret:
SharedSecret secret = alicePrivate.SharedSecretFromKeyAgreement(bobPublic);
```

Key-agreement members (same shape for `Curve25519`, `P256`, `P384`, `P521`):

| Member | Type | |
|---|---|---|
| `new …KeyAgreement.PrivateKey()` | — | generate a fresh private key |
| `.PublicKey` | `…KeyAgreement.PublicKey` | the matching public key |
| `.RawRepresentation` | `byte[]` | serialize the key for transport |
| `.SharedSecretFromKeyAgreement(publicKeyShare)` | `SharedSecret` | derive the ECDH shared secret |

`SharedSecret` is `IDisposable`, supports value equality, and renders via `Description`/`ToString()`.

`SharedSecret`'s own derivation methods — `hkdfDerivedSymmetricKey(using:salt:sharedInfo:outputByteCount:)` and `x963DerivedSymmetricKey(using:sharedInfo:outputByteCount:)` — are **not** projected (they constrain a protocol with associated types), so the agreement output has to be routed through HKDF explicitly:

```csharp
using SharedSecret secret = alicePrivate.SharedSecretFromKeyAgreement(bobPublic);

// 1. wrap the raw agreement output as input key material (a conversion, not a derivation)
using SymmetricKey ikm = SymmetricKey.FromCryptoKitSharedSecret(secret);

// 2. derive an actual encryption key from it
using SymmetricKey sessionKey = HKDFSHA256CsmExtensions.DeriveKey(
    ikm,
    salt: Encoding.UTF8.GetBytes("my-app-salt"),
    info: Encoding.UTF8.GetBytes("session key v1"),
    outputByteCount: 32);
```

`SymmetricKey.FromCryptoKitSharedSecret(secret)` on its own is **not** a substitute for that second step. It hands back the raw ECDH output as key material — no salt, no context string, no chosen output length — and for P-384/P-521 the raw secret is 48/66 bytes, which is not an AES key size at all. Feed it to HKDF before using it to encrypt.

There is no projected X9.63 KDF, and there is no way to write one over these objects: the agreement output stays inside `SharedSecret`/`SymmetricKey`, and neither exposes its bytes to C# (`SharedSecret` projects `Description`, equality and `Dispose`; `SymmetricKey` projects `BitCount`, the factories above and `Dispose`). If you need X9.63 specifically — to interoperate with an existing protocol, say — do the key agreement on the .NET side instead, where the raw agreement output is reachable: `System.Security.Cryptography.ECDiffieHellman` exposes `DeriveRawSecretAgreement` for the NIST P-curves, and a KDF of your choice can run over that.

## Key derivation (HKDF)

HKDF is projected the same way HMAC is: the CSM engine monomorphizes `HKDF<H>` into a static extension class per hash function — `HKDFSHA256CsmExtensions`, `HKDFSHA384CsmExtensions`, `HKDFSHA512CsmExtensions`, `HKDFSHA3_256CsmExtensions`, `HKDFSHA3_384CsmExtensions`, `HKDFSHA3_512CsmExtensions`, plus `HKDFCryptoKit_Insecure_SHA1CsmExtensions` and `HKDFCryptoKit_Insecure_MD5CsmExtensions` for legacy interop. `HKDF<H>` itself carries no callable members beyond `Payload`/`Dispose` — use the extension classes.

Each class exposes the same four `DeriveKey` overloads (RFC 5869 extract-then-expand, requires iOS 14 / macOS 11 / tvOS 14 / watchOS 7):

| Member | |
|---|---|
| `DeriveKey(SymmetricKey ikm, byte[] salt, byte[] info, nint outputByteCount)` | full form |
| `DeriveKey(SymmetricKey ikm, Data salt, Data info, nint outputByteCount)` | `Foundation.Data` form |
| `DeriveKey(SymmetricKey ikm, byte[] info, nint outputByteCount)` | empty salt |
| `DeriveKey(SymmetricKey ikm, nint outputByteCount)` | empty salt and empty info |

```csharp
using var ikm = SymmetricKey.FromByteArray(inputKeyMaterial);
using SymmetricKey key = HKDFSHA256CsmExtensions.DeriveKey(
    ikm,
    Encoding.UTF8.GetBytes("salt"),
    Encoding.UTF8.GetBytes("context string"),
    32);                       // key.BitCount == 256
```

The `expand` and `extract` entry points of `HKDF<H>` are not projected on their own (same associated-type constraint), so a protocol that needs a bare PRK expansion has to be built from HMAC instead.

This package's tests derive against RFC 5869 test cases 1 and 3 and run the ECDH → HKDF → AES-GCM route above on the iOS simulator; the derived octets are compared with .NET's own `System.Security.Cryptography.HKDF`. A device (NativeAOT) build is not covered by those tests — the incremental `HMAC<H>` builder hits a CSM conformance-descriptor load there (see [Known limitations](#known-limitations)), and whether the HKDF specializations resolve on that path has not been exercised here.

## Digital signatures

**Ed25519 signing via `Curve25519.Signing` is the usable path from C# today.** ECDSA on the `Signing` nested classes of `P256`, `P384`, and `P521` is **not** callable: `signature(for:)` and `isValidSignature(_:for:)` are generic over Swift's `DataProtocol`, and for the P-curves the generator emits only the open-generic forms, marked `[Obsolete("SB0001")]`:

```csharp
// Emitted, but NOT callable — no witness wiring, and the compiler flags SB0001:
public ECDSASignature Signature<D>(D data);
public bool IsValidSignature<D>(ECDSASignature signature, D data);
```

No closed `byte[]` / `Foundation.Data` / `*Digest` overload is emitted for either member on `P256`, `P384` or `P521`, and there is no way to build an `ECDSASignature` from raw bytes, so a P-curve signature cannot be produced or verified from C#. This package's test app carries an explicit skip for the P-curve sign/verify known-answer test naming that gap; the only P-curve surface it exercises is key generation and public-key serialization.

P-curve members that **are** usable:

| Member | Type | |
|---|---|---|
| `new …Signing.PrivateKey()` | — | generate a signing key |
| `.PublicKey` | `…Signing.PublicKey` | the verification key |
| `.RawRepresentation` | `byte[]` | serialize the private key |
| `pub.RawRepresentation` / `.CompressedRepresentation` / `.X963Representation` / `.DerRepresentation` / `.PemRepresentation` | `byte[]` / `string` | serialize the public key |
| `…Signing.PrivateKey.FromByteArray(byte[])` / `.FromData(Data)` | `…Signing.PrivateKey` | import a private key |

> The `ECDSASignature` type lives at `Swift.CryptoKit.P256.Signing.ECDSASignature` (it is supplied by the shared `SwiftBindings.Apple` supplement). It is reachable as a type name, but nothing in the generated P-curve surface produces or consumes one through a callable member.

If you need ECDSA over a P-curve from C#, the options are to sign with Ed25519 instead, or to reach for `System.Security.Cryptography.ECDsa` on the .NET side (which can import the same `X963Representation` / `DerRepresentation` bytes CryptoKit hands out).

**Curve25519/Ed25519 signing and verification** are both available as concrete `byte[]` / `Foundation.Data` overloads — `Signature(...)` returns `byte[]` (the raw Ed25519 signature) rather than a typed `ECDSASignature`, and those bytes go straight back into `IsValidSignature(byte[] signature, byte[] data)`:

```csharp
using var signingKey = new Curve25519.Signing.PrivateKey();
byte[] message = Encoding.UTF8.GetBytes("sign me");
byte[] sig = signingKey.Signature(message);           // byte[] overload
// or: signingKey.Signature(Foundation.Data data)

Curve25519.Signing.PublicKey pub = signingKey.PublicKey;  // .RawRepresentation is byte[]
bool ok = pub.IsValidSignature(sig, message);             // byte[] overloads
```

## Errors

Throwing CryptoKit calls surface as a Swift error projected through `CryptoKitError`, a class with a `CaseTag` discriminator and payload-less static singletons.

```csharp
public enum CryptoKitError.CaseTag : uint
{
    UnderlyingCoreCryptoError = 0,
    IncorrectKeySize          = 1,
    IncorrectParameterSize    = 2,
    AuthenticationFailure     = 3,
    WrapFailure               = 4,
    UnwrapFailure             = 5,
    InvalidParameter          = 6,
}
```

Static singletons and helpers:

```csharp
CryptoKitError e = CryptoKitError.AuthenticationFailure;
if (e.Tag == CryptoKitError.CaseTag.AuthenticationFailure) { /* … */ }

// The one case with a payload:
CryptoKitError underlying = CryptoKitError.UnderlyingCoreCryptoError(errCode);
if (underlying.TryGetUnderlyingCoreCryptoError(out int code)) { /* … */ }
```

`CryptoKitError` supports value equality (`==`, `Equals`). A separate plain enum, `CryptoKitASN1Error : int`, covers ASN.1/PEM decoding faults (`InvalidFieldIdentifier = 0` … `InvalidPEMDocument = 7`).

When a call like `AES.GCM.Open` fails authentication, catch the thrown exception (it carries the `authenticationFailure` semantics) and treat all output as untrusted.

## Known limitations

The generator emits the **concrete `byte[]` / `Foundation.Data` overloads** of CryptoKit's generic methods, which cover the common path. The open-generic forms and a few constructors are emitted only as `[Obsolete("SB0001")]` stubs — they will produce a compiler warning and will not run correctly. Avoid:

- **The generic AEAD overloads** — `AES.GCM.Seal<TPlaintext>(...)`, `AES.GCM.Open<TAuthenticatedData>(...)`, and the ChaChaPoly equivalents. Use the non-generic `byte[]` / `Data` overloads instead (these work).
- **`HMAC<H>` incremental builder on device** — the `HMACSHA{256,384,512}CsmExtensions.From{Hash}(key)` factories and the `Update`/`Finalize` flow work **on the simulator** (Mono JIT). On a NativeAOT device build, resolving the `HashFunction` protocol conformance descriptor triggers a `dlopen("@rpath/CryptoKit.framework/CryptoKit")` call that fails because CryptoKit is an Apple system framework at `/System/Library/Frameworks`, not bundled in the app's `@rpath`; the result is a `TypeInitializationException` on the first CSM call. For device code use the one-shot `AuthenticationCode(...)` helpers. (See HMAC section / `apple-framework-gaps/05-residual-gaps.md`.)
- **P-curve ECDSA sign and verify** — on `P256`/`P384`/`P521`, `Signature<D>(data)` and `IsValidSignature<D>(signature, data)` are emitted **only** as the open-generic `[Obsolete("SB0001")]` forms, with no closed `byte[]` / `Data` / `*Digest` overload and no way to construct an `ECDSASignature` from bytes. P-curve signing is therefore unreachable from C#; the P-curve key types are still usable for key generation, serialization and key agreement. `Curve25519.Signing` (Ed25519) has the concrete overloads and works.
- **`SealedBox` reconstruction from parts** — there is no public `SealedBox(nonce:ciphertext:tag:)` initializer (a Swift generic init). Persist `box.Combined` and keep the box, rather than rebuilding one from raw nonce/ciphertext/tag.
- **One-shot `static func hash(data:)`** on the hash functions — not projected. Use the incremental `new SHA256()` → `Update` → `FinalizeSwift()` flow.
- **`SharedSecret.hkdfDerivedSymmetricKey` / `.x963DerivedSymmetricKey`** — not projected (both constrain a protocol with associated types). HKDF itself **is** reachable through `HKDFSHA*CsmExtensions.DeriveKey(...)`: convert the secret with `SymmetricKey.FromCryptoKitSharedSecret(secret)` and pass it as the input key material, with your own salt, info and output length (see [Key derivation (HKDF)](#key-derivation-hkdf)). The raw conversion on its own is key material, not a derived key. X9.63 has no projected equivalent.
- **HPKE `Sender` / `Recipient` initializers (construction blocked)** — `HPKE.Sender` and `HPKE.Recipient` cannot be constructed from C#. Their initializers all require method-own generic type parameters (constraints over `HPKEDiffieHellmanPublicKey` / `HPKEKEMPublicKey` etc.) — a language limitation C# cannot satisfy. The instance methods (`Sender.Seal(byte[])`, `Sender.Seal(byte[], byte[])`, `Sender.ExportSecret(byte[], nint)`, `Recipient.Open(byte[])`, `Recipient.Open(byte[], byte[])`, `Recipient.ExportSecret(byte[], nint)`) and the `Sender.EncapsulatedKey` property **are** emitted as concrete `byte[]` / `Foundation.Data` overloads, but they are unreachable because no constructor exists to produce a `Sender` or `Recipient` from C#.
- **`AES.KeyWrap.Unwrap<TWrappedKey>`** generic form — obsolete. Concrete `Unwrap(byte[], …)` / `Unwrap(Data, …)` overloads are emitted and work.
- **KEM `Decapsulate`** — **reachable**, unlike the generic-only members above: `MLKEM768.PrivateKey.Decapsulate(byte[] encapsulated)` / `Decapsulate(Data encapsulated)` (and the equivalent on `MLKEM1024` and the Secure Enclave KEM key types) are emitted as concrete overloads by the CSM engine, and the receiver (`new MLKEM768.PrivateKey()`) has a public parameterless constructor, so the call has both a subject and an argument type C# can supply. This package's test app does not currently exercise a KEM encapsulate/decapsulate round trip.

What is actually exercised: this package ships a test app that runs the `byte[]` / `Foundation.Data` paths shown here — hashing, AES-GCM and ChaChaPoly seal/open, HMAC one-shot and incremental, HKDF derivation against RFC 5869 vectors, ECDH key agreement, Ed25519 sign/verify, ML-DSA-65 sign/verify, and P-curve key generation and serialization — on the iOS simulator, with known-answer vectors cross-checked against `System.Security.Cryptography` where a comparable BCL primitive exists. Treat the snippets in this guide as those paths and nothing more:

- The **incremental `HMAC<H>` builder** is simulator-covered; on a device (NativeAOT) build the CSM conformance-descriptor load described above fails, and the test app records that as a known gap rather than a pass. Use the one-shot `AuthenticationCode(...)` helpers there.
- The **HPKE `Sender`/`Recipient`** constructors are unreachable on every platform, so nothing downstream of them is exercised.
- **P-curve ECDSA sign/verify** is not callable and carries an explicit skip in the test app.
- Members not listed above — including the HKDF specializations on a device build — simply have not been run here, which is not the same as knowing they fail.

## Memory & threading

Generated CryptoKit types implement `ISwiftObject` / `IDisposable`. For short-lived locals the finalizer cleans up, but `using var` is the recommended pattern for deterministic cleanup — `Dispose` is safe on every generated type and double-Dispose is a no-op.

```csharp
using var hasher = new SHA256();
using var key = new SymmetricKey(SymmetricKeySize.Bits256);
using var signingKey = new P256.Signing.PrivateKey();
```

- **Sensitive material.** `SymmetricKey`, the private-key types, and `SharedSecret` hold secret bytes. Dispose them as soon as you're done so the underlying Swift buffer is released promptly rather than waiting on the finalizer.
- **`Sendable`.** Many CryptoKit value types (e.g. `AES.GCM.SealedBox`, the key types) are marked `[SwiftSendable]` — they may be shared across .NET threads without external synchronization. The operations themselves are stateless static calls.
- **Static singletons are cached.** `SymmetricKeySize.Bits256`, `CryptoKitError.AuthenticationFailure`, etc. return cached instances; don't dispose them.

## Reference links

- [Apple — CryptoKit](https://developer.apple.com/documentation/cryptokit) — upstream documentation and full API semantics
- [Apple — Performing common cryptographic operations](https://developer.apple.com/documentation/cryptokit/performing-common-cryptographic-operations)
