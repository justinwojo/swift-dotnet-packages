using Nuke.Common;
using SwiftBindings.Build.Models;

partial class Build
{
    Target TestActivityKitCapability => _ => _
        .Description("Self-test the fail-closed ActivityKit signed-entitlement/profile gate")
        .Executes(() =>
        {
            const string bundle = "com.swiftbindings.activitykit.push.tests";
            const string device = "00008120-TEST";
            var future = DateTimeOffset.UtcNow.AddDays(30).ToString("O");

            InvalidOperationException? runtimeMissing = null;
            try
            {
                ActivityKitEntitlementGate.RequireRuntimeEnvironment(null);
            }
            catch (InvalidOperationException ex)
            {
                runtimeMissing = ex;
            }
            if (runtimeMissing is null)
                throw new InvalidOperationException("Expected missing runtime entitlement to fail closed.");
            Assert.True(runtimeMissing.Message.Contains("aps-environment", StringComparison.Ordinal));
            Assert.True(runtimeMissing.Message.Contains("SessionCore.PermissionsError Code=3", StringComparison.Ordinal));

            string Signed(string? environment, string appId = "TL2K6QUQEH." + bundle) => Plist(
                Entry("application-identifier", appId) +
                Entry("com.apple.developer.team-identifier", "TL2K6QUQEH") +
                (environment is null ? "" : Entry(ActivityKitEntitlementGate.RequiredEntitlement, environment)));

            string Profile(string? environment, string appId = "TL2K6QUQEH." + bundle) => Plist(
                $"<key>ExpirationDate</key><date>{future}</date>" +
                "<key>ProvisionedDevices</key><array><string>" + device + "</string></array>" +
                "<key>TeamIdentifier</key><array><string>TL2K6QUQEH</string></array>" +
                "<key>Entitlements</key><dict>" +
                Entry("application-identifier", appId) +
                Entry("com.apple.developer.team-identifier", "TL2K6QUQEH") +
                (environment is null ? "" : Entry(ActivityKitEntitlementGate.RequiredEntitlement, environment)) +
                "</dict>");

            var missingSigned = ActivityKitEntitlementGate.Inspect(
                Signed(null), Profile("development"), bundle, device, DateTimeOffset.UtcNow);
            Assert.False(missingSigned.Success);
            Assert.True(missingSigned.Message.Contains("signed app is missing required entitlement 'aps-environment'", StringComparison.Ordinal));

            var missingProfile = ActivityKitEntitlementGate.Inspect(
                Signed("development"), Profile(null), bundle, device, DateTimeOffset.UtcNow);
            Assert.False(missingProfile.Success);
            Assert.True(missingProfile.Message.Contains("embedded provisioning profile is missing required entitlement 'aps-environment'", StringComparison.Ordinal));

            var wildcard = ActivityKitEntitlementGate.Inspect(
                Signed("development", "TL2K6QUQEH.com.*"),
                Profile("development", "TL2K6QUQEH.com.*"), bundle, device, DateTimeOffset.UtcNow);
            Assert.False(wildcard.Success);
            Assert.True(wildcard.Message.Contains("wildcard application-identifier", StringComparison.Ordinal));

            var mismatch = ActivityKitEntitlementGate.Inspect(
                Signed("development"), Profile("production"), bundle, device, DateTimeOffset.UtcNow);
            Assert.False(mismatch.Success);
            Assert.True(mismatch.Message.Contains("disagrees", StringComparison.Ordinal));

            var unsupportedEnvironment = ActivityKitEntitlementGate.Inspect(
                Signed("sandbox"), Profile("sandbox"), bundle, device, DateTimeOffset.UtcNow);
            Assert.False(unsupportedEnvironment.Success);
            Assert.True(unsupportedEnvironment.Message.Contains("unsupported entitlement 'aps-environment'", StringComparison.Ordinal));

            var wrongTeamPrefix = ActivityKitEntitlementGate.Inspect(
                Signed("development", "OTHERTEAM." + bundle),
                Profile("development", "OTHERTEAM." + bundle), bundle, device, DateTimeOffset.UtcNow);
            Assert.False(wrongTeamPrefix.Success);
            Assert.True(wrongTeamPrefix.Message.Contains("does not use team", StringComparison.Ordinal));

            var valid = ActivityKitEntitlementGate.Inspect(
                Signed("development"), Profile("development"), bundle, device, DateTimeOffset.UtcNow);
            Assert.True(valid.Success);
            Assert.True(valid.Environment == "development");

            var wrongDevice = ActivityKitEntitlementGate.InspectProvisioningProfile(
                Profile("development"), bundle, "00008120-OTHER", DateTimeOffset.UtcNow);
            Assert.False(wrongDevice.Success);
            Assert.True(wrongDevice.Message.Contains("does not include device", StringComparison.Ordinal));

            var expired = ActivityKitEntitlementGate.InspectProvisioningProfile(
                Profile("development"), bundle, device, DateTimeOffset.UtcNow.AddDays(31));
            Assert.False(expired.Success);
            Assert.True(expired.Message.Contains("expired", StringComparison.Ordinal));

            var validProfile = ActivityKitEntitlementGate.InspectProvisioningProfile(
                Profile("development"), bundle, device, DateTimeOffset.UtcNow);
            Assert.True(validProfile.Success);

            static string Entry(string key, string value) => $"<key>{key}</key><string>{value}</string>";
            static string Plist(string body) => $"<?xml version=\"1.0\"?><plist version=\"1.0\"><dict>{body}</dict></plist>";
        });
}
