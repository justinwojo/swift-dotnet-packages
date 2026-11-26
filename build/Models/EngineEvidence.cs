using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace SwiftBindings.Build.Models;

/// <summary>
/// Reads which engine a built Apple app bundle runs on from what the bundle carries, so a cell
/// can prove its engine before it launches anything. A cell that built the wrong flavor (a stale
/// bundle from the other configuration, a pairing that silently fell back to another SDK) would
/// otherwise pass under a runtime it never exercised.
///
/// <para>Native file names alone cannot decide it: the workloads link Mono and the Xamarin host
/// statically on Mac Catalyst and on device, and turn CoreCLR's dylibs into frameworks on device.
/// What every bundle that runs managed code does carry is its <c>System.Private.CoreLib.dll</c>
/// and the app's own assembly, so the evidence is read from those:</para>
/// <list type="bullet">
///   <item>Runtime: Mono's CoreLib defines types in the <c>Mono</c> namespace, CoreCLR's does not.
///         CoreCLR must also be corroborated by <c>libcoreclr.dylib</c> or
///         <c>libcoreclr.framework</c>, and Mono must not ship next to it. (NativeAOT device
///         cells are proven separately, by <c>AssertDeviceAppFlavor</c>.)</item>
///   <item>ReadyToRun vs interpreter (.NET 11): ReadyToRun needs a ReadyToRun header (a managed
///         native header) on the app assembly and on every <c>SwiftBindings.*</c> assembly; an IL
///         binding next to a ReadyToRun app proves neither engine. The interpreter needs an IL app
///         assembly: Debug builds leave project-built assemblies out of the composite image, while
///         package assemblies (the Apple supplement, the runtime) stay precompiled.</item>
///   <item>macOS: CoreCLR with the JIT; any CoreCLR bundle without Mono qualifies.</item>
/// </list>
/// Anything else (no CoreLib, both runtimes, an unreadable assembly) fails closed.
/// </summary>
public static class EngineEvidence
{
    public const string MonoLibrary = "libmonosgen-2.0.dylib";
    public const string CoreClrLibrary = "libcoreclr.dylib";
    public const string CoreClrFramework = "libcoreclr.framework";
    public const string CoreLibAssembly = "System.Private.CoreLib.dll";

    /// <summary>What a bundle carries, reduced to the facts the classification reads.</summary>
    /// <param name="CoreLibIsMono">Null when the bundle has no CoreLib.</param>
    /// <param name="AppIsReadyToRun">Null when the app assembly is not in the bundle.</param>
    /// <param name="MixedReadyToRun">The app is ReadyToRun but a <c>SwiftBindings.*</c> assembly is IL only.</param>
    public sealed record Facts(bool? CoreLibIsMono, bool MonoLibrary, bool CoreClrLibrary, bool? AppIsReadyToRun,
        bool MixedReadyToRun = false);

    public sealed record Finding(AppEngine? Engine, IReadOnlyList<string> Markers);

    /// <summary>Inspect a bundle directory and name the engine it runs
    /// <paramref name="appAssembly"/> (the app's own assembly, e.g. <c>SwiftBindings.Nuke.Tests</c>) on.</summary>
    public static Finding Inspect(string bundlePath, string appAssembly)
    {
        if (!Directory.Exists(bundlePath))
            return new Finding(null, new[] { "no bundle" });

        string? coreLib = null, app = null;
        var bindings = new List<string>();
        bool monoLib = false, coreClrLib = false;
        var appFile = appAssembly.EndsWith(".dll", StringComparison.Ordinal) ? appAssembly : appAssembly + ".dll";
        foreach (var entry in Directory.EnumerateFileSystemEntries(bundlePath, "*", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(entry);
            if (name == MonoLibrary) monoLib = true;
            else if (name is CoreClrLibrary or CoreClrFramework) coreClrLib = true;
            else if (name == CoreLibAssembly) coreLib ??= entry;
            else if (name == appFile) app ??= entry;
            else if (name.StartsWith("SwiftBindings.", StringComparison.Ordinal) && name.EndsWith(".dll", StringComparison.Ordinal))
                bindings.Add(entry);
        }
        try
        {
            return Classify(new Facts(
                coreLib is null ? null : DefinesMonoNamespace(coreLib),
                monoLib, coreClrLib,
                app is null ? null : HasReadyToRunCode(app),
                app is not null && HasReadyToRunCode(app) && !bindings.All(HasReadyToRunCode)));
        }
        catch (Exception ex) when (ex is BadImageFormatException or IOException or InvalidOperationException)
        {
            return new Finding(null, new[] { $"unreadable assembly: {ex.Message}" });
        }
    }

    /// <summary>The pure classification over a bundle's facts (exposed for the self-test).</summary>
    public static Finding Classify(Facts f)
    {
        var markers = new List<string>();
        markers.Add(f.CoreLibIsMono switch { true => "Mono CoreLib", false => "CoreCLR CoreLib", null => "no CoreLib" });
        if (f.MonoLibrary) markers.Add(MonoLibrary);
        if (f.CoreClrLibrary) markers.Add("libcoreclr");
        markers.Add(f.AppIsReadyToRun switch { true => "app is ReadyToRun", false => "app is IL only", null => "app assembly missing" });
        if (f.MixedReadyToRun) markers.Add("a SwiftBindings assembly is IL only next to a ReadyToRun app");

        AppEngine? engine = f.MixedReadyToRun ? null : (f.CoreLibIsMono, f.MonoLibrary, f.CoreClrLibrary, f.AppIsReadyToRun) switch
        {
            (true, _, false, not null) => AppEngine.Mono,
            (false, false, true, false) => AppEngine.CoreClrInterpreter,
            (false, false, true, true) => AppEngine.CoreClrReadyToRun,
            _ => null,
        };
        return new Finding(engine, markers);
    }

    /// <summary>
    /// Null when the bundle proves <paramref name="expected"/>; otherwise the reason it does not.
    /// </summary>
    public static string? Mismatch(string bundlePath, string appAssembly, AppEngine expected)
    {
        if (!Directory.Exists(bundlePath))
            return $"no app bundle at {bundlePath}, so its engine cannot be proven";
        var f = Inspect(bundlePath, appAssembly);
        if (f.Engine == expected) return null;
        // macOS has no interpreter/ReadyToRun choice: CoreCLR (either classification) is the JIT.
        if (expected == AppEngine.CoreClrJit
            && f.Engine is AppEngine.CoreClrInterpreter or AppEngine.CoreClrReadyToRun)
            return null;
        var found = string.Join(", ", f.Markers);
        return f.Engine is null
            ? $"the bundle at {bundlePath} does not prove any single engine (found: {found}); expected {ToolchainPairing.EngineName(expected)}"
            : $"the bundle at {bundlePath} runs {ToolchainPairing.EngineName(f.Engine.Value)} (found: {found}), " +
              $"not the {ToolchainPairing.EngineName(expected)} this cell exists to cover";
    }

    /// <summary>Mono's CoreLib defines runtime types in the <c>Mono</c> namespace (e.g.
    /// <c>Mono.RuntimeStructs</c>); CoreCLR's defines none.</summary>
    static bool DefinesMonoNamespace(string coreLib)
    {
        using var pe = new PEReader(File.OpenRead(coreLib));
        if (!pe.HasMetadata)
            throw new InvalidOperationException($"{coreLib} has no metadata");
        var md = pe.GetMetadataReader();
        foreach (var h in md.TypeDefinitions)
            if (md.StringComparer.Equals(md.GetTypeDefinition(h).Namespace, "Mono"))
                return true;
        return false;
    }

    /// <summary>True when the assembly carries precompiled ReadyToRun code (a managed native
    /// header, including the component header crossgen writes into a composite's inputs).</summary>
    static bool HasReadyToRunCode(string assembly)
    {
        using var pe = new PEReader(File.OpenRead(assembly));
        var cor = pe.PEHeaders.CorHeader
            ?? throw new InvalidOperationException($"{assembly} is not a managed assembly");
        return cor.ManagedNativeHeaderDirectory.Size > 0;
    }
}
