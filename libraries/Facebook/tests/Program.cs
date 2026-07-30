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
using FBSDKCoreKit;    // Profile, Settings, FBSDKLocation, FBSDKUserAgeRange, FBSDKAccessToken
using FBSDKLoginKit;   // LoginManager, LoginManagerLoginResult, FBLoginButton
using FBSDKShareKit;   // ShareLinkContent, ShareDialog, Hashtag

namespace SwiftBindings.Facebook.Tests;

public static class Program
{
    static void Main(string[] args) => UIApplication.Main(args, null, typeof(AppDelegate));
}

[Register("AppDelegate")]
public sealed class AppDelegate : UIApplicationDelegate
{
    public override UIWindow? Window { get; set; }

    public override bool FinishedLaunching(UIApplication application, NSDictionary launchOptions)
    {
        var pass = 0;
        var fail = 0;
        void Check(string name, Action body)
        {
            try { body(); Console.WriteLine($"PASS: {name}"); pass++; }
            catch (Exception ex) { Console.WriteLine($"FAIL: {name} — {ex.Message}"); fail++; }
        }

        // ==== BRIDGE-CRITICAL: ObjC-defined types resolved to their ObjC bindings ====

        // AccessToken is ObjC-defined (FBSDKAccessToken). Its static getter must
        // marshal through the ObjC binding and be nil before any login.
        Check("FBSDKAccessToken.CurrentAccessToken nil pre-login", () =>
        {
            var current = FBSDKAccessToken.CurrentAccessToken;
            if (current != null) throw new Exception("expected nil current token");
        });

        // FBSDKLocation is an ObjC class the Swift Profile references. Build one
        // from a dictionary (its ObjC factory) and read its properties back.
        Check("FBSDKLocation round-trip via factory", () =>
        {
            var dict = NSDictionary<NSString, NSString>.FromObjectsAndKeys(
                new[] { new NSString("us-ca"), new NSString("California") },
                new[] { new NSString("id"), new NSString("name") });
            var loc = FBSDKLocation.LocationFromDictionary(dict);
            if (loc is null) throw new Exception("factory returned nil");
            if (loc.Id != "us-ca") throw new Exception($"Id={loc.Id}");
            if (loc.Name != "California") throw new Exception($"Name={loc.Name}");
        });

        // FBSDKUserAgeRange is an ObjC class the Swift Profile references.
        Check("FBSDKUserAgeRange round-trip via factory", () =>
        {
            var dict = NSDictionary<NSString, NSNumber>.FromObjectsAndKeys(
                new[] { new NSNumber(21), new NSNumber(65) },
                new[] { new NSString("min"), new NSString("max") });
            var age = FBSDKUserAgeRange.AgeRangeFromDictionary(dict);
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
            var location = FBSDKLocation.LocationFromDictionary(locDict)
                ?? throw new Exception("location factory nil");

            var homeDict = NSDictionary<NSString, NSString>.FromObjectsAndKeys(
                new[] { new NSString("uk-lnd"), new NSString("London") },
                new[] { new NSString("id"), new NSString("name") });
            var hometown = FBSDKLocation.LocationFromDictionary(homeDict)
                ?? throw new Exception("hometown factory nil");

            var ageDict = NSDictionary<NSString, NSNumber>.FromObjectsAndKeys(
                new[] { new NSNumber(21) },
                new[] { new NSString("min") });
            var ageRange = FBSDKUserAgeRange.AgeRangeFromDictionary(ageDict)
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
            if (status != FBSDKAdvertisingTrackingStatus.FBSDKAdvertisingTrackingAllowed
                && status != FBSDKAdvertisingTrackingStatus.FBSDKAdvertisingTrackingDisallowed
                && status != FBSDKAdvertisingTrackingStatus.FBSDKAdvertisingTrackingUnspecified)
                throw new Exception($"unexpected enum value {status}");
        });

        // Settings.appID (Swift-class String property) set/get control.
        Check("Settings.AppID set/get", () =>
        {
            Settings.Shared.AppID = "1234567890";
            if (Settings.Shared.AppID != "1234567890") throw new Exception($"AppID={Settings.Shared.AppID}");
        });

        // THE BRIDGE (cross-module): a Swift-defined LoginManagerLoginResult in
        // FBSDKLoginKit takes and returns a FBSDKAccessToken defined in FBSDKCoreKit.
        Check("LoginManagerLoginResult carries bridged CoreKit AccessToken", () =>
        {
            var token = new FBSDKAccessToken(
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

        Console.WriteLine($"RESULTS: {pass} pass, {fail} fail");
        if (fail == 0 && pass > 0)
            Console.WriteLine("TEST SUCCESS");

        Window = new UIWindow(UIScreen.MainScreen.Bounds) { RootViewController = new UIViewController() };
        Window.MakeKeyAndVisible();
        return true;
    }
}
