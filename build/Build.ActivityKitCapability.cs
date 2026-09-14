using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Nuke.Common.IO;
using Serilog;
using SwiftBindings.Build.Models;

partial class Build
{
    const string ActivityKitLibrary = "ActivityKit";
    const string ActivityKitPushBundleId = "com.swiftbindings.activitykit.push.tests";

    string? EffectiveActivityKitProvisioningProfile =>
        !string.IsNullOrWhiteSpace(ActivityKitProvisioningProfile)
            ? ActivityKitProvisioningProfile
            : Environment.GetEnvironmentVariable("ACTIVITYKIT_PROVISIONING_PROFILE")
                ?? Environment.GetEnvironmentVariable("PROVISIONING_PROFILE");

    sealed record TestAppDescriptor(
        AbsolutePath TestDir,
        string AppName,
        string BundleId,
        bool RequiresActivityKitPushCapability);

    sealed record ActivityKitProvisioningSelection(string Name, string Uuid, string Path, string Plist);

    IEnumerable<string> ActivityKitCodesignArguments(TestAppDescriptor app)
    {
        if (!app.RequiresActivityKitPushCapability)
            return Array.Empty<string>();

        var requested = EffectiveActivityKitProvisioningProfile;
        if (string.IsNullOrWhiteSpace(requested))
            throw new InvalidOperationException(
                "The ActivityKit physical-device push-token lane requires " +
                "--activitykit-provisioning-profile (or ACTIVITYKIT_PROVISIONING_PROFILE) naming an " +
                "explicit App ID profile that grants the 'aps-environment' entitlement. Wildcard or " +
                "implicit profiles are not accepted.");

        var profile = RequireActivityKitProvisioningProfile(requested, expectedDeviceUdid: null);
        return new[] { $"-p:CodesignProvision={profile.Uuid}" };
    }

    ActivityKitProvisioningSelection RequireActivityKitProvisioningProfile(
        string requested,
        string? expectedDeviceUdid)
    {
        var profileDirs = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Library", "Developer", "Xcode", "UserData", "Provisioning Profiles"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Library", "MobileDevice", "Provisioning Profiles"),
        };
        var candidates = profileDirs
            .Where(Directory.Exists)
            .SelectMany(dir => Directory.EnumerateFiles(dir, "*.mobileprovision"))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var requestedPath = File.Exists(requested) ? Path.GetFullPath(requested) : null;
        if (requestedPath is not null)
            candidates = candidates.Append(requestedPath).Distinct(StringComparer.Ordinal).ToArray();

        foreach (var path in candidates)
        {
            var decoded = Capture("/usr/bin/security", ["cms", "-D", "-i", path]);
            var plist = decoded.ExitCode == 0 ? ExtractPlist(decoded.Combined) : null;
            if (plist is null) continue;
            var document = XDocument.Parse(plist, LoadOptions.None);
            var dictionary = document.Root?.Element("dict");
            if (dictionary is null) continue;
            var name = ActivityKitPlistString(dictionary, "Name");
            var uuid = ActivityKitPlistString(dictionary, "UUID");
            var matches = string.Equals(path, requestedPath, StringComparison.Ordinal)
                || string.Equals(Path.GetFileNameWithoutExtension(path), requested, StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, requested, StringComparison.Ordinal)
                || string.Equals(uuid, requested, StringComparison.OrdinalIgnoreCase);
            if (!matches) continue;

            var verdict = ActivityKitEntitlementGate.InspectProvisioningProfile(
                plist, ActivityKitPushBundleId, expectedDeviceUdid, DateTimeOffset.UtcNow);
            if (!verdict.Success)
                throw new InvalidOperationException($"{verdict.Message} Profile: {name ?? path}");
            if (string.IsNullOrWhiteSpace(uuid))
                throw new InvalidOperationException(
                    $"ActivityKit push-token capability preflight failed: provisioning profile '{name ?? path}' has no UUID.");
            Log.Information("ActivityKit provisioning profile preflight passed: {Name} ({Uuid})", name, uuid);
            return new ActivityKitProvisioningSelection(name ?? requested, uuid, path, plist);
        }

        throw new InvalidOperationException(
            $"ActivityKit push-token capability preflight failed: provisioning profile '{requested}' was not found or " +
            $"could not be decoded; required entitlement '{ActivityKitEntitlementGate.RequiredEntitlement}' could not be proved.");
    }

    static string? ActivityKitPlistString(XElement dictionary, string key)
    {
        var children = dictionary.Elements().ToArray();
        for (var index = 0; index + 1 < children.Length; index++)
            if (children[index].Name.LocalName == "key" && children[index].Value == key
                && children[index + 1].Name.LocalName == "string")
                return children[index + 1].Value;
        return null;
    }

    /// <summary>
    /// Resolve a test app once for a lane. ActivityKit physical-device lanes
    /// intentionally use a distinct app/project/bundle id whose profile can
    /// carry APNs; every other lane keeps the ordinary app.
    /// </summary>
    TestAppDescriptor ResolveTestApp(string library, bool forDevice)
    {
        var (kind, libDir) = ResolveLibrary(library);
        var usePushApp = forDevice
            && kind == LibraryKind.AppleFramework
            && string.Equals(library, ActivityKitLibrary, StringComparison.Ordinal);
        var testDir = libDir / (usePushApp ? "tests-device" : "tests");
        if (!Directory.Exists(testDir))
            throw new InvalidOperationException(
                $"Test directory not found for '{library}' ({(forDevice ? "device" : "non-device")} lane). Expected: {testDir}");

        if (usePushApp)
            return new TestAppDescriptor(
                testDir,
                "SwiftBindings.Apple.ActivityKit.PushToken.Tests",
                ActivityKitPushBundleId,
                RequiresActivityKitPushCapability: true);

        var appName = kind == LibraryKind.AppleFramework
            ? $"SwiftBindings.Apple.{library}.Tests"
            : $"SwiftBindings.{library}.Tests";
        return new TestAppDescriptor(
            testDir,
            appName,
            $"com.swiftbindings.{library.ToLowerInvariant()}.tests",
            RequiresActivityKitPushCapability: false);
    }

    /// <summary>
    /// Verify the final signed bundle and its embedded provisioning profile
    /// immediately before installation. Raw evidence is retained under
    /// artifacts/activitykit-capability; the summary carries hashes so a
    /// durable receipt can identify the exact proof without committing a
    /// provisioning profile.
    /// </summary>
    void VerifyActivityKitPushCapability(
        TestAppDescriptor app,
        AbsolutePath appPath,
        string? deviceUdid,
        Action<string>? onLine)
    {
        if (!app.RequiresActivityKitPushCapability) return;

        void Info(string line)
        {
            if (onLine is null) Log.Information("{Line}", line);
            else onLine(line);
        }

        var verify = Capture("/usr/bin/codesign", ["--verify", "--strict", (string)appPath]);
        if (verify.ExitCode != 0)
            throw new InvalidOperationException(
                $"ActivityKit push-token capability preflight failed: final app signature verification failed for {appPath}: {verify.Combined.Trim()}");

        var signed = Capture("/usr/bin/codesign", ["--display", "--entitlements", ":-", (string)appPath]);
        if (signed.ExitCode != 0)
            throw new InvalidOperationException(
                $"ActivityKit push-token capability preflight failed: could not read signed app entitlements from {appPath}: {signed.Combined.Trim()}");
        var signedPlist = ExtractPlist(signed.Combined)
            ?? throw new InvalidOperationException(
                $"ActivityKit push-token capability preflight failed: signed app entitlements for {appPath} contained no plist; required entitlement '{ActivityKitEntitlementGate.RequiredEntitlement}' could not be proved.");

        var embeddedProfile = appPath / "embedded.mobileprovision";
        if (!File.Exists(embeddedProfile))
            throw new InvalidOperationException(
                $"ActivityKit push-token capability preflight failed: final app {appPath} has no embedded.mobileprovision; required entitlement '{ActivityKitEntitlementGate.RequiredEntitlement}' could not be proved.");
        var profile = Capture("/usr/bin/security", ["cms", "-D", "-i", (string)embeddedProfile]);
        if (profile.ExitCode != 0)
            throw new InvalidOperationException(
                $"ActivityKit push-token capability preflight failed: embedded provisioning profile in {appPath} could not be decoded: {profile.Combined.Trim()}");
        var profilePlist = ExtractPlist(profile.Combined)
            ?? throw new InvalidOperationException(
                $"ActivityKit push-token capability preflight failed: embedded provisioning profile in {appPath} contained no plist; required entitlement '{ActivityKitEntitlementGate.RequiredEntitlement}' could not be proved.");

        var provisioningDeviceUdid = string.IsNullOrWhiteSpace(deviceUdid)
            ? null
            : ResolveActivityKitProvisioningDeviceUdid(deviceUdid);
        var verdict = ActivityKitEntitlementGate.Inspect(
            signedPlist, profilePlist, app.BundleId, provisioningDeviceUdid, DateTimeOffset.UtcNow);
        if (!verdict.Success)
            throw new InvalidOperationException($"{verdict.Message} App: {appPath}");

        var evidenceDir = RootDirectory / "artifacts" / "activitykit-capability" /
            $"{DateTime.UtcNow:yyyyMMdd-HHmmss.fffZ}-p{Environment.ProcessId}";
        Directory.CreateDirectory(evidenceDir);
        File.WriteAllText(evidenceDir / "signed-entitlements.plist", signedPlist);
        File.WriteAllText(evidenceDir / "embedded-profile.plist", profilePlist);
        var signedHash = Sha256(signedPlist);
        var profileHash = Sha256(profilePlist);
        File.WriteAllText(evidenceDir / "summary.txt",
            $"app={appPath}\n" +
            $"bundle_id={app.BundleId}\n" +
            $"devicectl_identifier={deviceUdid ?? "<not supplied>"}\n" +
            $"provisioning_device_udid={provisioningDeviceUdid ?? "<not supplied>"}\n" +
            $"entitlement={ActivityKitEntitlementGate.RequiredEntitlement}\n" +
            $"environment={verdict.Environment}\n" +
            $"application_identifier={verdict.ApplicationIdentifier}\n" +
            $"team_identifier={verdict.TeamIdentifier}\n" +
            $"signed_entitlements_sha256={signedHash}\n" +
            $"embedded_profile_sha256={profileHash}\n");

        Info($"ActivityKit push-token capability verified: {verdict.Message}");
        Info($"ActivityKit signed-entitlement/profile evidence: {evidenceDir}");
    }

    static (int ExitCode, string Combined) Capture(string fileName, IReadOnlyList<string> arguments)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
            process.StartInfo.ArgumentList.Add(argument);
        process.Start();
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, stdout + "\n" + stderr);
    }

    static string? ExtractPlist(string text)
    {
        var start = text.IndexOf("<?xml", StringComparison.Ordinal);
        if (start < 0) start = text.IndexOf("<plist", StringComparison.Ordinal);
        var end = text.LastIndexOf("</plist>", StringComparison.Ordinal);
        return start >= 0 && end >= start
            ? text[start..(end + "</plist>".Length)]
            : null;
    }

    static string Sha256(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    static string ResolveActivityKitProvisioningDeviceUdid(string devicectlIdentifier)
    {
        var details = Capture(
            "/usr/bin/xcrun", ["devicectl", "device", "info", "details", "--device", devicectlIdentifier]);
        if (details.ExitCode != 0)
            throw new InvalidOperationException(
                $"ActivityKit push-token capability preflight failed: could not resolve the provisioning UDID " +
                $"for device '{devicectlIdentifier}': {details.Combined.Trim()}");
        var match = Regex.Match(details.Combined, @"(?m)^\s*• udid:\s*(\S+)\s*$", RegexOptions.CultureInvariant);
        if (!match.Success)
            throw new InvalidOperationException(
                $"ActivityKit push-token capability preflight failed: device details for '{devicectlIdentifier}' " +
                "did not contain a hardware provisioning UDID.");
        return match.Groups[1].Value;
    }
}
