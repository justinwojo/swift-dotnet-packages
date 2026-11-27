// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Nuke.Common;
using SwiftBindings.Build.Models;

partial class Build
{
    /// <summary>
    /// Self-test for the simulator privacy policy (<c>Models/SimulatorPrivacy.cs</c>): which
    /// declared usage strings are answered through simctl, which through the TCC database, and
    /// the statements that write and remove the database answers.
    /// </summary>
    Target TestSimulatorPrivacy => _ => _
        .Description("Self-test the simulator privacy pre-answer policy")
        .Executes(() =>
        {
            const string plist = """
                <?xml version="1.0" encoding="UTF-8"?>
                <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
                <plist version="1.0">
                <dict>
                  <key>CFBundleIdentifier</key><string>com.swiftbindings.visionkit.tests</string>
                  <key>NSCameraUsageDescription</key><string>scan</string>
                  <key>NSAppleMusicUsageDescription</key><string>music</string>
                  <key>NSHealthShareUsageDescription</key><string>health</string>
                  <key>Nested</key>
                  <dict><key>NSMicrophoneUsageDescription</key><string>not top level</string></dict>
                </dict>
                </plist>
                """;
            var keys = SimulatorPrivacy.UsageKeysFromPlistXml(plist);
            Assert.True(keys.SequenceEqual(new[]
            {
                "NSAppleMusicUsageDescription", "NSCameraUsageDescription", "NSHealthShareUsageDescription",
            }), $"usage keys: {string.Join(", ", keys)}");

            // The camera has no simctl service, so it goes to the database; the music library
            // goes through simctl; a key neither knows is reported (the launcher refuses it), never dropped.
            var plan = SimulatorPrivacy.PlanFor(keys);
            Assert.True(plan.SimctlServices.SequenceEqual(new[] { "media-library" }));
            Assert.True(plan.DatabaseServices.SequenceEqual(new[] { "kTCCServiceCamera" }));
            Assert.True(plan.Unanswered.SequenceEqual(new[] { "NSHealthShareUsageDescription" }));
            Assert.True(plan.HasAnswers);
            Assert.False(SimulatorPrivacy.PlanFor(new[] { "NSHealthShareUsageDescription" }).HasAnswers);

            // Two keys for one service grant it once.
            var calendar = SimulatorPrivacy.PlanFor(new[]
            {
                "NSCalendarsUsageDescription", "NSCalendarsFullAccessUsageDescription",
            });
            Assert.True(calendar.SimctlServices.SequenceEqual(new[] { "calendar" }));

            // No usage strings: nothing to answer, and nothing to take back after the run.
            Assert.False(SimulatorPrivacy.PlanFor(Array.Empty<string>()).HasAnswers);
            Assert.True(SimulatorPrivacy.UsageKeysFromPlistXml(
                "<plist version=\"1.0\"><dict><key>CFBundleName</key><string>x</string></dict></plist>").Count == 0);

            // The grant row is the one simctl writes for the services it knows.
            Assert.True(SimulatorPrivacy.GrantSql("kTCCServiceCamera", "com.swiftbindings.visionkit.tests") ==
                "INSERT OR REPLACE INTO access (service, client, client_type, auth_value, auth_reason, auth_version, " +
                "indirect_object_identifier, flags) VALUES ('kTCCServiceCamera', 'com.swiftbindings.visionkit.tests', " +
                "0, 2, 4, 1, 'UNUSED', 0);");

            // The reset removes the plan's database answers and nothing else.
            Assert.True(SimulatorPrivacy.ResetSql(new[] { "kTCCServiceCamera" }, "com.example.app") ==
                "DELETE FROM access WHERE client = 'com.example.app' AND client_type = 0 AND service IN ('kTCCServiceCamera');");

            // Values spliced into SQL are validated.
            foreach (var (service, bundle) in new[]
                     {
                         ("kTCCServiceCamera", "x'); DROP TABLE access; --"),
                         ("kTCCServiceCamera'", "com.example.app"),
                         ("camera", "com.example.app"),
                     })
            {
                var threw = false;
                try { SimulatorPrivacy.GrantSql(service, bundle); }
                catch (ArgumentException) { threw = true; }
                Assert.True(threw, $"GrantSql accepted service '{service}', bundle '{bundle}'");
            }

            Serilog.Log.Information("Simulator privacy self-test passed.");
        });
}
