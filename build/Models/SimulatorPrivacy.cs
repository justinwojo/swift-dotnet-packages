// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System.Xml.Linq;
using System.Text.RegularExpressions;

namespace SwiftBindings.Build.Models;

/// <summary>
/// Pure policy for pre-answering the privacy prompts a test app can raise on a shared simulator.
///
/// <para>A system permission alert outlives the app that raised it: neither terminating nor
/// uninstalling the app takes it down, only a reboot does, and while it is up no other app on that
/// simulator can become active. A VisionKit cell once left a camera alert behind and turned every
/// later cell on the same simulator that needs an active app red. So before launch the harness
/// answers every service the app declares a usage string for (granted, as a user who tapped Allow
/// would), and after the run it resets those answers. A declared service it has no way to answer
/// refuses the launch rather than risk the alert.</para>
///
/// <para>The usage strings in the bundle's Info.plist decide the services: an app without one
/// cannot raise that service's prompt at all (iOS terminates it instead). simctl grants most
/// services; the camera and the few others it does not know are written into the simulator's
/// own TCC database with the same row simctl writes for the ones it does.</para>
/// </summary>
internal static class SimulatorPrivacy
{
    /// <summary>Usage-string key → the <c>simctl privacy</c> service that answers it.</summary>
    internal static readonly IReadOnlyDictionary<string, string> SimctlServices =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["NSCalendarsUsageDescription"] = "calendar",
            ["NSCalendarsFullAccessUsageDescription"] = "calendar",
            ["NSCalendarsWriteOnlyAccessUsageDescription"] = "calendar",
            ["NSContactsUsageDescription"] = "contacts",
            ["NSLocationWhenInUseUsageDescription"] = "location",
            ["NSLocationUsageDescription"] = "location",
            ["NSLocationAlwaysAndWhenInUseUsageDescription"] = "location-always",
            ["NSLocationAlwaysUsageDescription"] = "location-always",
            ["NSPhotoLibraryUsageDescription"] = "photos",
            ["NSPhotoLibraryAddUsageDescription"] = "photos-add",
            ["NSAppleMusicUsageDescription"] = "media-library",
            ["NSMicrophoneUsageDescription"] = "microphone",
            ["NSMotionUsageDescription"] = "motion",
            ["NSRemindersUsageDescription"] = "reminders",
            ["NSRemindersFullAccessUsageDescription"] = "reminders",
            ["NSSiriUsageDescription"] = "siri",
        };

    /// <summary>
    /// Usage-string key → the TCC service simctl cannot grant, answered by a row in the
    /// simulator's TCC database. <c>simctl privacy</c> has no camera service in any installed Xcode.
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, string> DatabaseServices =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["NSCameraUsageDescription"] = "kTCCServiceCamera",
            ["NSBluetoothAlwaysUsageDescription"] = "kTCCServiceBluetoothAlways",
            ["NSBluetoothPeripheralUsageDescription"] = "kTCCServiceBluetoothAlways",
            ["NSSpeechRecognitionUsageDescription"] = "kTCCServiceSpeechRecognition",
            ["NSUserTrackingUsageDescription"] = "kTCCServiceUserTracking",
            ["NSFaceIDUsageDescription"] = "kTCCServiceFaceID",
        };

    /// <summary>What to answer before launch, and the declared keys neither route answers (the launch is refused).</summary>
    internal sealed record Plan(
        IReadOnlyList<string> SimctlServices,
        IReadOnlyList<string> DatabaseServices,
        IReadOnlyList<string> Unanswered)
    {
        internal bool IsEmpty => SimctlServices.Count == 0 && DatabaseServices.Count == 0 && Unanswered.Count == 0;
        internal bool HasAnswers => SimctlServices.Count > 0 || DatabaseServices.Count > 0;
    }

    /// <summary>The Info.plist keys that declare a privacy usage string.</summary>
    internal static bool IsUsageKey(string key) =>
        key.StartsWith("NS", StringComparison.Ordinal) && key.EndsWith("UsageDescription", StringComparison.Ordinal);

    /// <summary>Top-level usage keys of an Info.plist rendered as XML (<c>plutil -convert xml1</c>).</summary>
    internal static IReadOnlyList<string> UsageKeysFromPlistXml(string xml)
    {
        var doc = XDocument.Parse(xml, LoadOptions.None);
        var dict = doc.Root?.Elements("dict").SingleOrDefault()
            ?? throw new InvalidOperationException("Info.plist is not a dictionary.");
        return dict.Elements("key")
            .Select(k => k.Value)
            .Where(IsUsageKey)
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();
    }

    internal static Plan PlanFor(IEnumerable<string> usageKeys)
    {
        var simctl = new SortedSet<string>(StringComparer.Ordinal);
        var database = new SortedSet<string>(StringComparer.Ordinal);
        var unanswered = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var key in usageKeys)
        {
            if (SimctlServices.TryGetValue(key, out var service)) simctl.Add(service);
            else if (DatabaseServices.TryGetValue(key, out var tcc)) database.Add(tcc);
            else unanswered.Add(key);
        }
        return new Plan([.. simctl], [.. database], [.. unanswered]);
    }

    static readonly Regex s_bundleId = new(@"^[A-Za-z0-9][A-Za-z0-9.\-]*$", RegexOptions.CultureInvariant);
    static readonly Regex s_tccService = new(@"^kTCCService[A-Za-z]+$", RegexOptions.CultureInvariant);

    /// <summary>
    /// The SQL that grants <paramref name="service"/> to <paramref name="bundleId"/>: the row
    /// <c>simctl privacy grant</c> writes (allowed, set by the system, version 1). Both values are
    /// validated, since they are spliced into the statement.
    /// </summary>
    internal static string GrantSql(string service, string bundleId)
    {
        if (!s_tccService.IsMatch(service))
            throw new ArgumentException($"Not a TCC service name: '{service}'", nameof(service));
        if (!s_bundleId.IsMatch(bundleId))
            throw new ArgumentException($"Not a bundle identifier: '{bundleId}'", nameof(bundleId));
        return "INSERT OR REPLACE INTO access " +
               "(service, client, client_type, auth_value, auth_reason, auth_version, indirect_object_identifier, flags) " +
               $"VALUES ('{service}', '{bundleId}', 0, 2, 4, 1, 'UNUSED', 0);";
    }
}
