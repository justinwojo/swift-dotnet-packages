// Facebook (Meta) iOS SDK binding test app.
//
// Validates the ObjC->Swift type bridge for the mixed (ObjC + Swift) Facebook
// modules: ObjC-defined types (AccessToken, Location, UserAgeRange,
// AdvertisingTrackingStatus, AuthenticationToken) now resolve to their real ObjC
// bindings when referenced from Swift-defined classes (Profile, Settings,
// LoginManagerLoginResult) instead of degrading to `object`/AnyType or being
// dropped. The bridge-critical checks build a Swift class from bridged ObjC
// values and read them back; the forward checks confirm the rest of the surface
// still constructs.

using System;
using Foundation;
using UIKit;
using FBSDKCoreKit;    // Profile, Settings, Location, UserAgeRange, AccessToken
using FBSDKLoginKit;   // LoginManager, LoginManagerLoginResult, FBLoginButton
using FBSDKShareKit;   // ShareLinkContent, ShareDialog, Hashtag
using FBAEMKit;        // AEMReporter, AEMSettings, AEMAdvertiserRuleOperator
// FBSDKCoreKit_Basics is imported under an alias. This originally worked around a
// compile-time ambiguity: the FBSDKCoreKit binding re-declared seven type names that
// FBSDKCoreKit_Basics also owns, so a plain import made every one of them an
// ambiguous reference. The generator no longer emits those Core-side re-declarations
// (asserted at runtime by the CoreBasics registration check below), so the alias is
// now only a readability choice — it keeps `Basics.`-qualified call sites obvious.
using Basics = global::FBSDKCoreKit_Basics;

namespace SwiftBindings.Facebook.Tests;

public static class Program
{
    static void Main(string[] args) => UIApplication.Main(args, null, typeof(AppDelegate));
}

/// <summary>
/// Minimal concrete <c>NSURLSessionDataDelegate</c> used to construct
/// <c>FBSDKURLSession</c>. The generated ctor parameter is not
/// <c>[NullAllowed]</c> (matching the ObjC header), so a real delegate object
/// is required — passing null throws <see cref="ArgumentNullException"/> in the
/// binding's null check before ever reaching ObjC.
/// </summary>
public sealed class NullSessionDelegate : NSUrlSessionDataDelegate
{
}

[Register("AppDelegate")]
public sealed class AppDelegate : UIApplicationDelegate
{
    public override UIWindow? Window { get; set; }

    public override bool FinishedLaunching(UIApplication application, NSDictionary launchOptions)
    {
        var pass = 0;
        var fail = 0;
        var skip = 0;
        var failures = new System.Collections.Generic.List<string>();
        void Check(string name, Action body)
        {
            try { body(); Console.WriteLine($"PASS: {name}"); pass++; }
            catch (Exception ex)
            {
                Console.WriteLine($"FAIL: {name} — {ex.GetType().Name}: {ex.Message}");
                failures.Add(name);
                fail++;
            }
        }
        void Skip(string name, string reason)
        {
            Console.WriteLine($"SKIP: {name} — {reason}");
            skip++;
        }
        // ==== CoreBasics managed assembly registration ====
        //
        // FBSDKCoreKit's generated binding used to re-declare seven ObjC names that
        // FBSDKCoreKit_Basics also owns (FBSDKCrashHandler, FBSDKCrashObserving,
        // FBSDKDataPersisting, FBSDKFileDataExtracting, FBSDKFileManaging,
        // FBSDKInfoDictionaryProviding, FBSDKNotificationDelivering) — as empty
        // [Protocol] shims on the Core side, as the real types on the Basics side.
        // Two managed types carrying one native name broke both registrars: the
        // dynamic one aborted the CoreBasics assembly at the first collision (logged
        // as "Could not register the assembly ... Cannot register two managed types
        // ... with the same native name" and then swallowed), and the static /
        // managed-static one failed the BUILD with MT4118 — which, since
        // managed-static is forced for NativeAOT and is the default off-simulator,
        // meant no device or App Store build of this graph was possible.
        //
        // The generator no longer emits the Core-side shims. This check asserts that
        // directly, by reading the ObjC names the two assemblies actually declare:
        // every contested name must be carried by CoreBasics and by NOTHING in Core.
        // That is the exact condition the registrars enforce, so it is red in the
        // broken state (Core declared all seven) and green now. The direct binding
        // calls further below cannot substitute for it — they resolve through
        // Class.GetHandle against the already-loaded native class and so kept passing
        // even while the dynamic registrar was aborting the CoreBasics assembly.
        Check("CoreBasics managed assembly registration completed", () =>
        {
            // 1 class + 6 protocols. Each protocol yields both a [Protocol] interface
            // and a [Register] model type, so a name legitimately appears more than
            // once WITHIN CoreBasics; what must never happen is the same native name
            // appearing in Core as well.
            var contested = new[]
            {
                "FBSDKCrashHandler", "FBSDKCrashObserving", "FBSDKDataPersisting",
                "FBSDKFileDataExtracting", "FBSDKFileManaging",
                "FBSDKInfoDictionaryProviding", "FBSDKNotificationDelivering",
            };

            // bgen names the managed type after the ObjC name, so a re-declaration is
            // visible as a same-named type in the Core assembly — which is also why a
            // plain `using FBSDKCoreKit_Basics;` used to be an ambiguous reference.
            var basicsAsm = typeof(Basics.FBSDKCrashHandler).Assembly;
            var coreAsm = typeof(Settings).Assembly;

            // Positive control first: if the CoreBasics types cannot be found at all
            // (wrong assembly, renamed namespace), the absence check below would pass
            // vacuously and report health that was never measured.
            var missing = new System.Collections.Generic.List<string>();
            foreach (var n in contested)
                if (basicsAsm.GetType("FBSDKCoreKit_Basics." + n) is null) missing.Add(n);
            if (missing.Count > 0)
                throw new Exception($"CoreBasics does not declare: {string.Join(", ", missing)}");

            var duplicated = new System.Collections.Generic.List<string>();
            foreach (var n in contested)
                if (coreAsm.GetType("FBSDKCoreKit." + n) is not null) duplicated.Add(n);
            if (duplicated.Count > 0)
                throw new Exception(
                    "FBSDKCoreKit re-declares CoreBasics-owned native name(s): " +
                    string.Join(", ", duplicated));
        });

        // ==== BRIDGE-CRITICAL: ObjC-defined types resolved to their ObjC bindings ====

        // AccessToken is ObjC-defined (raw ObjC name FBSDKAccessToken). Its static getter must
        // marshal through the ObjC binding and be nil before any login.
        Check("AccessToken.CurrentAccessToken nil pre-login", () =>
        {
            var current = AccessToken.CurrentAccessToken;
            if (current != null) throw new Exception("expected nil current token");
        });

        // Location is an ObjC class the Swift Profile references. Build one
        // from a dictionary (its ObjC factory) and read its properties back.
        Check("Location round-trip via factory", () =>
        {
            var dict = NSDictionary<NSString, NSString>.FromObjectsAndKeys(
                new[] { new NSString("us-ca"), new NSString("California") },
                new[] { new NSString("id"), new NSString("name") });
            var loc = Location.LocationFromDictionary(dict);
            if (loc is null) throw new Exception("factory returned nil");
            if (loc.Id != "us-ca") throw new Exception($"Id={loc.Id}");
            if (loc.Name != "California") throw new Exception($"Name={loc.Name}");
        });

        // UserAgeRange is an ObjC class the Swift Profile references.
        Check("UserAgeRange round-trip via factory", () =>
        {
            var dict = NSDictionary<NSString, NSNumber>.FromObjectsAndKeys(
                new[] { new NSNumber(21), new NSNumber(65) },
                new[] { new NSString("min"), new NSString("max") });
            var age = UserAgeRange.AgeRangeFromDictionary(dict);
            if (age is null) throw new Exception("factory returned nil");
            if (age.Min?.Int32Value != 21) throw new Exception($"Min={age.Min}");
        });

        // THE BRIDGE (intra-module): a Swift-defined Profile takes ObjC-defined
        // Location / UserAgeRange arguments and returns them from its getters.
        Check("Profile carries bridged ObjC Location + AgeRange", () =>
        {
            var locDict = NSDictionary<NSString, NSString>.FromObjectsAndKeys(
                new[] { new NSString("us-ca"), new NSString("California") },
                new[] { new NSString("id"), new NSString("name") });
            var location = Location.LocationFromDictionary(locDict)
                ?? throw new Exception("location factory nil");

            var homeDict = NSDictionary<NSString, NSString>.FromObjectsAndKeys(
                new[] { new NSString("uk-lnd"), new NSString("London") },
                new[] { new NSString("id"), new NSString("name") });
            var hometown = Location.LocationFromDictionary(homeDict)
                ?? throw new Exception("hometown factory nil");

            var ageDict = NSDictionary<NSString, NSNumber>.FromObjectsAndKeys(
                new[] { new NSNumber(21) },
                new[] { new NSString("min") });
            var ageRange = UserAgeRange.AgeRangeFromDictionary(ageDict)
                ?? throw new Exception("age factory nil");

            using var profile = new Profile(
                "uid-1", "Ada", null, "Lovelace", "Ada Lovelace", null, null,
                ageRange: ageRange, hometown: hometown, location: location);

            if (profile.Location?.Id != "us-ca") throw new Exception($"Location.Id={profile.Location?.Id}");
            if (profile.Hometown?.Name != "London") throw new Exception($"Hometown.Name={profile.Hometown?.Name}");
            if (profile.AgeRange?.Min?.Int32Value != 21) throw new Exception($"AgeRange.Min={profile.AgeRange?.Min}");
        });

        // Settings.advertisingTrackingStatus is an ObjC-defined NS_ENUM read via a
        // Swift-class property. The getter must marshal to the bridged enum.
        Check("Settings.AdvertisingTrackingStatus is a bridged enum", () =>
        {
            var status = Settings.Shared.AdvertisingTrackingStatus;
            if (status != AdvertisingTrackingStatus.FBSDKAdvertisingTrackingAllowed
                && status != AdvertisingTrackingStatus.FBSDKAdvertisingTrackingDisallowed
                && status != AdvertisingTrackingStatus.FBSDKAdvertisingTrackingUnspecified)
                throw new Exception($"unexpected enum value {status}");
        });

        // Settings.appID (Swift-class String property) set/get control.
        Check("Settings.AppID set/get", () =>
        {
            Settings.Shared.AppID = "1234567890";
            if (Settings.Shared.AppID != "1234567890") throw new Exception($"AppID={Settings.Shared.AppID}");
        });

        // THE BRIDGE (cross-module): a Swift-defined LoginManagerLoginResult in
        // FBSDKLoginKit takes and returns an AccessToken defined in FBSDKCoreKit.
        Check("LoginManagerLoginResult carries bridged CoreKit AccessToken", () =>
        {
            var token = new AccessToken(
                "tok", new[] { "public_profile" }, Array.Empty<string>(), Array.Empty<string>(),
                "test-app-id", "user-1", null, null, null);
            if (token.AppID != "test-app-id") throw new Exception($"token.AppID={token.AppID}");

            using var result = new LoginManagerLoginResult(
                token, null, false, new System.Collections.Generic.HashSet<string> { "public_profile" },
                new System.Collections.Generic.HashSet<string>());

            if (result.Token?.AppID != "test-app-id") throw new Exception($"result.Token.AppID={result.Token?.AppID}");
        });

        // THE BRIDGE (NS_TYPED_ENUM): FBSDKLoginAuthType is an ObjC
        // NS_TYPED_EXTENSIBLE_ENUM (typedef NSString *FBSDKLoginAuthType) imported
        // into Swift as the LoginAuthType newtype. It previously degraded to
        // AnyType, so the authType property and every authType-carrying initializer
        // parameter were dropped from the binding. It now projects to
        // Foundation.NSString and round-trips through the ObjC-bridgeable path.
        //
        // Read-only property on a Swift value object: pass "rerequest" (a canonical
        // FBSDKLoginAuthType) through the initializer and read it back.
        Check("LoginConfiguration carries bridged NS_TYPED_ENUM authType", () =>
        {
            var ok = LoginConfiguration.TryCreate(
                new[] { "public_profile" }, LoginTracking.Enabled, "nonce-abc-123", null,
                new NSString("rerequest"), out var config);
            if (!ok || config is null) throw new Exception("LoginConfiguration.TryCreate returned false/nil");
            if (config.AuthType?.ToString() != "rerequest")
                throw new Exception($"AuthType={config.AuthType?.ToString() ?? "<null>"}");
        });

        // Settable authType on the Swift @objc FBLoginButton: set then read back
        // exercises both the ObjC-bridgeable setter (NSString -> LoginAuthType) and
        // getter (LoginAuthType -> NSString).
        Check("FBLoginButton.AuthType set/get round-trip", () =>
        {
            using var btn = new FBLoginButton(Swift.CGRect.Zero);
            btn.AuthType = new NSString("rerequest");
            if (btn.AuthType?.ToString() != "rerequest")
                throw new Exception($"AuthType={btn.AuthType?.ToString() ?? "<null>"}");
        });

        // ==== FORWARD SURFACE: the rest of the FB binding still constructs ====

        Check("LoginManager ctor", () =>
        {
            using var mgr = new LoginManager();
        });
        Check("FBLoginButton ctor", () =>
        {
            using var btn = new FBLoginButton(Swift.CGRect.Zero);
        });
        Check("Share types construct", () =>
        {
            using var hashtag = new Hashtag("#dotnet");
            using var content = new ShareLinkContent();
            using var dialog = new ShareDialog(null, null, null);
        });

        // ==== FBSDKCoreKit_Basics: the pure-ObjC kernel module ====
        //
        // Until now this module had a binding project but ZERO runtime coverage —
        // and in fact shipped an assembly with no binding types at all, because the
        // csproj was missing <SwiftFrameworkType>ObjC</SwiftFrameworkType> +
        // <IsBindingProject>true</IsBindingProject>, so bgen never compiled the
        // generated ApiDefinition.cs. These tests exercise the real thing.
        Check("Basics FBSDKBase64 string round-trip", () =>
        {
            var encoded = Basics.FBSDKBase64.EncodeString("hello .NET");
            if (encoded != "aGVsbG8gLk5FVA==") throw new Exception($"encoded={encoded ?? "<null>"}");
            var decoded = Basics.FBSDKBase64.DecodeAsString(encoded);
            if (decoded != "hello .NET") throw new Exception($"decoded={decoded ?? "<null>"}");
        });

        Check("Basics FBSDKBase64 decode to NSData", () =>
        {
            var data = Basics.FBSDKBase64.DecodeAsData("aGVsbG8gLk5FVA==");
            if (data is null) throw new Exception("DecodeAsData returned nil");
            if (data.Length != 10) throw new Exception($"length={data.Length}");
        });

        Check("Basics FBSDKBase64.Base64FromBase64Url", () =>
        {
            // base64url uses - and _ where standard base64 uses + and /. Assert the exact
            // output modulo '=' padding (how much padding the helper appends is an
            // implementation detail) — a StartsWith check alone would accept a corrupted
            // tail such as "a+b/c-corrupt".
            var b64 = Basics.FBSDKBase64.Base64FromBase64Url("a-b_c");
            if (b64 is null) throw new Exception("Base64FromBase64Url returned nil");
            if (b64.TrimEnd('=') != "a+b/c")
                throw new Exception($"expected \"a+b/c\" (plus optional '=' padding), got b64='{b64}'");
        });

        Check("Basics FBSDKBasicUtility URL encode/decode round-trip", () =>
        {
            const string raw = "a b&c=d/e?f";
            var enc = Basics.FBSDKBasicUtility.URLEncode(raw);
            if (enc == raw) throw new Exception($"URLEncode was a no-op: {enc}");
            var dec = Basics.FBSDKBasicUtility.URLDecode(enc);
            if (dec != raw) throw new Exception($"round-trip gave '{dec}'");
        });

        Check("Basics FBSDKBasicUtility query-string round-trip", () =>
        {
            var dict = NSDictionary.FromObjectsAndKeys(
                new NSObject[] { new NSString("1"), new NSString("two") },
                new NSObject[] { new NSString("a"), new NSString("b") });
            var qs = Basics.FBSDKBasicUtility.QueryStringWithDictionary(dict, out var err, null);
            if (err != null) throw new Exception($"error: {err.LocalizedDescription}");
            if (string.IsNullOrEmpty(qs)) throw new Exception("empty query string");
            var back = Basics.FBSDKBasicUtility.DictionaryWithQueryString(qs);
            if (back is null) throw new Exception("DictionaryWithQueryString returned nil");
            if (back["a"]?.ToString() != "1" || back["b"]?.ToString() != "two")
                throw new Exception($"round-trip gave a={back["a"]}, b={back["b"]}");
        });

        // out NSError marshalling on a pure-ObjC static, both directions.
        Check("Basics FBSDKBasicUtility JSON round-trip (out NSError)", () =>
        {
            var payload = NSDictionary.FromObjectsAndKeys(
                new NSObject[] { new NSNumber(42) }, new NSObject[] { new NSString("answer") });
            var json = Basics.FBSDKBasicUtility.JSONStringForObject(payload, out var writeErr, null);
            if (writeErr != null) throw new Exception($"write error: {writeErr.LocalizedDescription}");
            if (json is null || !json.Contains("answer")) throw new Exception($"json={json ?? "<null>"}");

            var parsed = Basics.FBSDKBasicUtility.ObjectForJSONString(json, out var readErr);
            if (readErr != null) throw new Exception($"read error: {readErr.LocalizedDescription}");
            if (parsed is not NSDictionary d) throw new Exception($"parsed is {parsed?.GetType().Name ?? "<null>"}");
            if (((NSNumber)d[(NSString)"answer"]).Int32Value != 42) throw new Exception("value mismatch");
        });

        Check("Basics FBSDKBasicUtility.Gzip compresses", () =>
        {
            var raw = NSData.FromString(new string('x', 512));
            var gz = Basics.FBSDKBasicUtility.Gzip(raw);
            if (gz is null) throw new Exception("gzip returned nil");
            if (gz.Length >= raw.Length) throw new Exception($"no compression: {raw.Length} -> {gz.Length}");
        });

        Check("Basics FBSDKBasicUtility.SHA256Hash", () =>
        {
            var hash = Basics.FBSDKBasicUtility.SHA256Hash(new NSString("abc"));
            // SHA-256("abc") is a well-known constant.
            if (hash != "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad")
                throw new Exception($"hash={hash ?? "<null>"}");
        });

        Check("Basics FBSDKBasicUtility.AnonymousID is a UUID-shaped string", () =>
        {
            // FBSDKCoreKit_Basics builds this as the literal format "XZ%@" over a fresh
            // NSUUID (the "XZ%@" format string is present in the shipped framework binary),
            // so the exact expected shape is "XZ" + a 36-char UUID. Asserting that shape
            // rather than "non-empty" is what makes a broken string marshal visible.
            var id = Basics.FBSDKBasicUtility.AnonymousID();
            if (string.IsNullOrEmpty(id)) throw new Exception("empty anonymous id");
            if (!id.StartsWith("XZ", StringComparison.Ordinal))
                throw new Exception($"expected the \"XZ\" prefix, got '{id}'");
            if (id.Length != 38)
                throw new Exception($"expected \"XZ\" + a 36-char UUID (38 chars), got {id.Length}: '{id}'");
            if (!Guid.TryParse(id[2..], out _))
                throw new Exception($"the 36 chars after \"XZ\" are not a UUID: '{id}'");
        });

        Check("Basics FBSDKTypeUtility scalar coercions", () =>
        {
            if (Basics.FBSDKTypeUtility.StringValueOrNil(new NSString("s")) != "s") throw new Exception("StringValueOrNil");
            if (Basics.FBSDKTypeUtility.IntegerValue(new NSNumber(7)) != 7) throw new Exception("IntegerValue");
            if (Basics.FBSDKTypeUtility.DoubleValue(new NSNumber(2.5)) != 2.5) throw new Exception("DoubleValue");
            if (!Basics.FBSDKTypeUtility.BoolValue(NSNumber.FromBoolean(true))) throw new Exception("BoolValue");
            if (Basics.FBSDKTypeUtility.UnsignedIntegerValue(new NSNumber(9)) != 9) throw new Exception("UnsignedIntegerValue");
            if (Basics.FBSDKTypeUtility.NumberValue(new NSNumber(3))?.Int32Value != 3) throw new Exception("NumberValue");
            // Wrong-type input must coerce to the documented zero/nil, not crash.
            if (Basics.FBSDKTypeUtility.IntegerValue(new NSString("nope")) != 0) throw new Exception("IntegerValue(non-number)");
        });

        Check("Basics FBSDKTypeUtility.CoercedToURLValue", () =>
        {
            var url = Basics.FBSDKTypeUtility.CoercedToURLValue(new NSString("https://example.com/a"));
            if (url?.AbsoluteString != "https://example.com/a") throw new Exception($"url={url?.AbsoluteString ?? "<null>"}");
        });

        Check("Basics FBSDKTypeUtility array/dictionary guards", () =>
        {
            var arr = NSArray.FromObjects(new NSString("x"), new NSString("y"));
            var got = Basics.FBSDKTypeUtility.ArrayValue(arr);
            if (got is null || got.Length != 2) throw new Exception($"ArrayValue len={got?.Length}");
            if (Basics.FBSDKTypeUtility.Array(got, 1)?.ToString() != "y") throw new Exception("Array(objectAtIndex:)");
            // Out-of-range must return nil rather than throwing NSRangeException.
            if (Basics.FBSDKTypeUtility.Array(got, 99) != null) throw new Exception("expected nil for OOB index");

            var mutable = new NSMutableDictionary();
            Basics.FBSDKTypeUtility.Dictionary(mutable, new NSString("v"), (NSString)"k");
            if (mutable[(NSString)"k"]?.ToString() != "v") throw new Exception("Dictionary(setObject:forKey:)");
            var dv = Basics.FBSDKTypeUtility.DictionaryValue(mutable);
            if (dv is null || dv.Count != 1) throw new Exception($"DictionaryValue count={dv?.Count}");
        });

        Check("Basics FBSDKTypeUtility NSJSON wrappers (out NSError)", () =>
        {
            var obj = NSDictionary.FromObjectsAndKeys(
                new NSObject[] { new NSString("v") }, new NSObject[] { new NSString("k") });
            var data = Basics.FBSDKTypeUtility.DataWithJSONObject(obj, 0, out var wErr);
            if (wErr != null) throw new Exception($"write: {wErr.LocalizedDescription}");
            if (data is null) throw new Exception("DataWithJSONObject returned nil");
            var back = Basics.FBSDKTypeUtility.JSONObjectWithData(data, 0, out var rErr);
            if (rErr != null) throw new Exception($"read: {rErr.LocalizedDescription}");
            if (back is not NSDictionary d || d[(NSString)"k"]?.ToString() != "v")
                throw new Exception("round-trip mismatch");
        });

        // FBSDKCrashHandler is one of the SEVEN native names FBSDKCoreKit also
        // re-declares. Reaching the REAL class (the one that answers +getFBSDKVersion)
        // through the CoreBasics binding proves the ObjC runtime resolved to the
        // Basics implementation and not to CoreKit's empty protocol shim.
        Check("Basics FBSDKCrashHandler.GetFBSDKVersion", () =>
        {
            var version = Basics.FBSDKCrashHandler.GetFBSDKVersion();
            if (string.IsNullOrEmpty(version)) throw new Exception("empty version");
            if (!version.StartsWith("18.")) throw new Exception($"unexpected version '{version}'");
        });

        Check("Basics FBSDKCrashHandler.Shared conforms to the protocol", () =>
        {
            var shared = Basics.FBSDKCrashHandler.Shared;
            if (shared is null) throw new Exception("Shared is nil");
            // Calling through the protocol interface exercises the generated wrapper.
            shared.ClearCrashReportFiles();
        });

        Check("Basics FBSDKURLSession ctor + properties + Valid()", () =>
        {
            using var sessionDelegate = new NullSessionDelegate();
            using var session = new Basics.FBSDKURLSession(sessionDelegate, NSOperationQueue.MainQueue);
            if (session.Handle == IntPtr.Zero) throw new Exception("null native handle");
            // `delegate` is a weak id<NSURLSessionDataDelegate>: reading it back must
            // resolve to the very managed peer we handed in, which is the interesting
            // marshalling round-trip here.
            if (!ReferenceEquals(session.Delegate, sessionDelegate))
                throw new Exception("delegate did not round-trip to the managed peer");
            if (session.DelegateQueue is null) throw new Exception("delegateQueue lost");
            // `session` is a lazily-created nullable NSURLSession, so `valid`
            // (which is just `_session != nil`) is false until the first task is
            // started. What is under test is that the BOOL selector marshals, not
            // its value — assert the documented pre-use state explicitly.
            if (session.Valid()) throw new Exception("expected valid=false before the first task");
            session.InvalidateAndCancel();
        });

        Skip("Basics FBSDKLibAnalyzer.GetMethodsTable",
             "walks every loaded Mach-O image symbolicating ObjC methods; multi-second and " +
             "output is environment-dependent, so it is not a deterministic gate candidate");

        // ==== FBAEMKit: Aggregated Event Measurement ====
        //
        // Second module with zero prior runtime coverage. AEM is a Swift-mode
        // binding (it ships a generated FBAEMKitSwiftBindings.xcframework wrapper),
        // so these calls also exercise the Swift calling convention, not just ObjC.

        Check("AEM AEMAdvertiserRuleOperator enum raw values", () =>
        {
            if ((long)AEMAdvertiserRuleOperator.Unknown != 0) throw new Exception("Unknown");
            if ((long)AEMAdvertiserRuleOperator.And != 1) throw new Exception("And");
            if ((long)AEMAdvertiserRuleOperator.RegexMatch != 10) throw new Exception("RegexMatch");
            if ((long)AEMAdvertiserRuleOperator.GreaterThanOrEqual != 16) throw new Exception("GreaterThanOrEqual");
        });

        Check("AEM AEMSettings.GetAppID is callable", () =>
        {
            // AEMSettings.appID is sourced from the main bundle's FacebookAppID key, so
            // the answer is fully determined here: assert the marshalled String? equals
            // exactly what the bundle declares (nil when the key is absent, as in this
            // test app). A "non-nil implies non-empty" check would pass on any garbage.
            var expected = NSBundle.MainBundle.ObjectForInfoDictionary("FacebookAppID") as NSString;
            var appId = AEMSettings.GetAppID();
            if (appId != expected?.ToString())
                throw new Exception($"appID='{appId ?? "<nil>"}' but Info.plist FacebookAppID='{expected?.ToString() ?? "<absent>"}'");
        });

        Check("AEM AEMReporter ctor (Swift class instantiation)", () =>
        {
            using var reporter = new AEMReporter();
            if (reporter.Handle == IntPtr.Zero) throw new Exception("null native handle");
        });

        // Write-only by construction: AEMReporter exposes these three flags as static
        // Swift setters with no corresponding getter, so there is nothing to read back
        // and no-op setters would be indistinguishable from working ones. What this
        // does establish is that a static Swift func taking a Bool marshals across the
        // Swift calling convention without trapping — named accordingly rather than
        // claiming the flags were "proven writable".
        Check("AEM AEMReporter static Bool setters marshal (no getter to assert)", () =>
        {
            AEMReporter.SetConversionFilteringEnabled(true);
            AEMReporter.SetCatalogMatchingEnabled(true);
            AEMReporter.SetAdvertiserRuleMatchInServerEnabled(false);
            // Call each one again with the opposite value so both branches marshal.
            AEMReporter.SetConversionFilteringEnabled(false);
            AEMReporter.SetCatalogMatchingEnabled(false);
            AEMReporter.SetAdvertiserRuleMatchInServerEnabled(true);
        });

        // The five AEMReporter members whose Swift signatures reach a module-internal
        // type (IsContentOptimized, CatalogRequestParameters, RuleMatchRequestParameters,
        // LoadRuleMatch, AddConfigurations) have no ABI-correct call path: no @_cdecl
        // wrapper can be generated and the direct P/Invoke signature is not blittable.
        // The generator now emits them as [Obsolete(DiagnosticId = "SB0009")] tombstones
        // whose bodies throw NotSupportedException instead of issuing the unsound native
        // call — so the contract to assert is "declared, and throws managed, never
        // faults native". A managed exception here is the FIX working; a process fault
        // is a regression back to the unsound direct P/Invoke.
        Check("AEM AEMReporter module-internal members throw NotSupportedException (SB0009 tombstone)", () =>
        {
#pragma warning disable CS0618 // the tombstone is [Obsolete] by design; calling it IS the test
            try
            {
                AEMReporter.IsContentOptimized(null);
                throw new Exception("IsContentOptimized(null) returned instead of throwing NotSupportedException");
            }
            catch (NotSupportedException)
            {
                // Expected: the tombstone body threw before any native transition.
            }
#pragma warning restore CS0618
        });

        Skip("AEM AEMReporter.Configure / Enable / HandleMethod",
             "starts the real AEM pipeline (disk cache writes + network requests to " +
             "graph.facebook.com) and needs an IAEMNetworking implementation; not " +
             "deterministic offline in a test app");

        Console.WriteLine($"RESULTS: {pass} pass, {fail} fail, {skip} skip");
        if (fail == 0 && pass > 0)
        {
            Console.WriteLine("TEST SUCCESS");
        }
        else
        {
            foreach (var name in failures) Console.WriteLine($"FAILED TEST: {name}");
            Console.WriteLine($"TEST FAILED: {fail} test(s) failed ({pass} passed, {skip} skipped)");
        }

        Window = new UIWindow(UIScreen.MainScreen.Bounds) { RootViewController = new UIViewController() };
        Window.MakeKeyAndVisible();
        return true;
    }
}
