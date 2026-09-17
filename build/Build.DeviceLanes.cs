using Nuke.Common;
using Nuke.Common.IO;
using Serilog;
using SwiftBindings.Build.Models;

partial class Build
{
    // ═══════════════════════════════════════════════════════════════════════
    //  The two device runtimes.
    //
    //  A physical iPhone can run this repo's test app under two entirely
    //  different runtimes, and until now the matrix only ever ran one of them:
    //
    //    * NativeAOT   — `dotnet publish -c Release -r ios-arm64 -p:PublishAot=true`.
    //                    OPT-IN: an app gets it only because its author asked.
    //    * Mono full-AOT — `dotnet build -c Debug -r ios-arm64`, no PublishAot.
    //                    The .NET-for-iOS DEFAULT: what a MAUI app ships on
    //                    unless its author opts into NativeAOT.
    //
    //  They differ in marshalling, P/Invoke dispatch and generic instantiation,
    //  so "the device cell is green" was a statement about the runtime fewer
    //  consumers actually ship.
    //
    //  The two lanes are separated structurally, not by convention:
    //    * different configuration (Release vs Debug) ⇒ different bin/obj trees,
    //      so neither can install the other's bundle;
    //    * the built bundle's SHAPE is checked before install — a Mono full-AOT
    //      app ships managed assemblies, a NativeAOT app does not;
    //    * the app's OWN runtime banner is read back off the device and must
    //      agree with the lane, so a cell that somehow ran the other runtime
    //      fails instead of reporting a green under the wrong name.
    // ═══════════════════════════════════════════════════════════════════════

    [Parameter("ValidateDeviceLane: run the Mono full-AOT device lane (dotnet build -c Debug, no PublishAot) " +
               "instead of the NativeAOT one. Mono full-AOT is the .NET-for-iOS default; NativeAOT is opt-in.")]
    readonly bool MonoAot;

    /// <summary>
    /// Run ONE library through a full device lane — build, bundle-shape gate,
    /// install + run, runtime-banner gate — without the version stamping and
    /// whole-matrix scheduling of <c>RegressionValidate</c>. This is the
    /// single-cell entry point for the same code path the matrix's two device
    /// cells use, so the lane can be exercised (and a device regression
    /// bisected) one library at a time.
    /// </summary>
    Target ValidateDeviceLane => _ => _
        .Description("Build + validate one library on a physical device through the gated device lane " +
                     "(--library Foo [--mono-aot] [--device-udid X])")
        .Requires(() => Library)
        .Executes(() =>
        {
            var result = RunDeviceLane(Library!, MonoAot, DeviceUdid, line => Log.Information("{Line}", line));
            ReportTerminalStatus(result, Library!);
        });

    static readonly object DeviceLanePropsLock = new();

    /// <summary>
    /// Write (once per process) the lane props that inject the evidence probe
    /// into a device build, and return the path to pass as
    /// <c>-p:SwiftBindingsLaneProps=</c>. No reference rewriting: the device
    /// cells keep their ProjectReferences and only add the probe.
    /// </summary>
    AbsolutePath EnsureDeviceLaneProps()
    {
        var dir = RootDirectory / "artifacts" / "lane-evidence";
        lock (DeviceLanePropsLock)
        {
            var props = dir / LanePropsFileName;
            if (!File.Exists(props) || !File.Exists(dir / LaneEvidenceFileName))
                return WriteLaneProps(dir, Array.Empty<(string, string, string)>(), withEvidence: true);
            return props;
        }
    }

    /// <summary>
    /// Refuse to run a device bundle whose shape contradicts the lane that
    /// built it, before it is ever installed.
    ///
    /// <para>Mono full-AOT keeps the managed assemblies in the bundle and AOTs
    /// them into companion native code; NativeAOT compiles everything into the
    /// executable and ships no managed assemblies at all. So the presence of
    /// <c>System.Private.CoreLib.dll</c> in the app bundle separates the two
    /// without launching anything — which matters because the alternative
    /// failure mode is a stale bundle from the sibling lane passing as this
    /// lane's result.</para>
    /// </summary>
    internal static void AssertDeviceAppFlavor(AbsolutePath appPath, bool expectMono, string laneLabel)
    {
        if (!Directory.Exists(appPath))
            throw new InvalidOperationException($"{laneLabel}: no app bundle at {appPath}.");

        var managed = Directory
            .EnumerateFiles(appPath, "System.Private.CoreLib.dll", SearchOption.AllDirectories)
            .Any();

        if (managed == expectMono)
        {
            Log.Information("{Lane}: bundle shape agrees with the lane ({Shape})",
                laneLabel, managed ? "managed assemblies present — Mono" : "no managed assemblies — NativeAOT");
            return;
        }

        throw new InvalidOperationException(
            $"{laneLabel}: the built bundle at {appPath} has the WRONG SHAPE for this lane. " +
            $"Expected {(expectMono ? "Mono full-AOT (managed assemblies present)" : "NativeAOT (no managed assemblies)")}, " +
            $"found {(managed ? "managed assemblies" : "none")}. " +
            "Refusing to install it: reporting the other runtime's bundle under this lane's name would be a " +
            "false green about the runtime the cell exists to cover.");
    }

    /// <summary>
    /// Build, install and run the test app on a physical device under one of
    /// the two device runtimes, gated on the bundle shape before install and on
    /// the app's own runtime banner after the run.
    /// </summary>
    TestRunResult RunDeviceLane(
        string library, bool mono, string? deviceUdid, Action<string> sink)
    {
        var laneLabel = $"{library}/{(mono ? "ios-device-monoaot" : "ios-device")}";
        var app = ResolveTestApp(library, forDevice: true);
        var testDir = app.TestDir;
        var laneProps = EnsureDeviceLaneProps();
        var extraArgs = new[] { $"-p:SwiftBindingsLaneProps={laneProps}" }
            .Concat(ActivityKitCodesignArguments(app))
            .ToArray();

        // Both device paths do their own restore: a multi-RID assets.json can't
        // be produced cleanly in .NET 10 (NETSDK1083), so the device build's
        // implicit restore overwrites the sim-RID assets file. The same
        // library's sim cell has already completed before either device cell is
        // dispatched, so the overwrite cannot race a --no-restore reader.
        if (mono)
            BuildTestAppDevice(library, testDir, onLine: sink, noRestore: false, extraArgs: extraArgs);
        else
            BuildTestAppNativeAot(library, testDir, onLine: sink, noRestore: false, extraArgs: extraArgs);

        var appName = app.AppName;
        var rid = ResolveRid("ios-arm64");
        var appPath = ResolveAppPath(testDir, mono ? "Debug" : "Release", "ios", rid, appName);
        AssertDeviceAppFlavor(appPath, expectMono: mono, laneLabel);

        var result = ValidateDeviceFor(library, Timeout, deviceUdid, aot: !mono, rid, onLine: sink, resolvedApp: app);

        // Only a run that got far enough to be judged is worth judging: a crash
        // or timeout is reported as itself, not as missing evidence. An unqualified
        // run completed its offline suite, so its runtime claim is judged too.
        if (result.Status is TestRunStatus.Success or TestRunStatus.Unqualified)
            AssertRuntimeFlavor(result.Output, expectMono: mono, laneLabel);

        return result;
    }
}
