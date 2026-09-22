using System.IO.Compression;
using Nuke.Common.IO;
using SwiftBindings.Build.Models;

partial class Build
{
    // ═══════════════════════════════════════════════════════════════════════
    //  Native provenance for the release-consumption lane.
    //
    //  Swapping <ProjectReference> for <PackageReference> re-points the MANAGED
    //  reference and nothing else. Native code travels a separate road: a test
    //  csproj names the frameworks it embeds directly, with relative paths into
    //  the working tree — `../MapLibre.xcframework`, and inside a target,
    //  `../<Lib>/obj/$(Configuration)/net10.0-ios/swift-binding/<Lib>SwiftBindings.xcframework`
    //  — because NativeReference does not propagate through a ProjectReference.
    //  Those items survive the swap, and the release workflow runs a source
    //  build before this lane, so the artifacts they point at exist.
    //
    //  So a package that ships a missing or wrong framework could still produce
    //  a green lane: the app links the working tree's native code, the managed
    //  MVID check passes because the managed DLL really did come out of the
    //  package, the suite runs, and a broken package gets published.
    //
    //  Two mechanisms close that, and each covers the other's failure mode:
    //
    //   1. The generated lane props drops every NativeReference that resolves
    //      into the working tree (see RenderLanePropsXml). The package's own
    //      buildTransitive targets — and, for a binding package, the
    //      resources.zip the iOS SDK unpacks — put the package's frameworks
    //      back, so what links is the package's native code.
    //
    //   2. This file asserts the OUTCOME rather than trusting the mechanism:
    //      every framework the built .app embeds must be a build the packages in
    //      the lane's own cache ship, and every iOS-simulator framework a staged
    //      package ships must be embedded. Identity is the Mach-O LC_UUID, not
    //      the file name and not a whole-file hash — the SDK thins and re-signs
    //      what it embeds, so only the UUID survives the trip (see MachOImage).
    //
    //  If the drop in (1) ever stops taking effect, (2) reds: the embedded
    //  binary would then be the working tree's build, whose UUID is not in the
    //  package — unless the two are literally the same bytes, in which case the
    //  package does ship what ran and the lane's claim holds anyway.
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>One framework slice a package ships, with the identity of its binary.</summary>
    internal sealed record PackageNativeImage(
        string PackageId,
        string Version,
        string Framework,
        string Slice,
        string Origin,
        IReadOnlyList<string> Uuids);

    /// <summary>One framework the built <c>.app</c> actually carries.</summary>
    internal sealed record EmbeddedNativeImage(
        string Framework,
        string Path,
        IReadOnlyList<string> Uuids);

    /// <summary>
    /// Assert the native half of package consumption: the app's frameworks came
    /// out of the lane's package cache, and the staged packages' frameworks are
    /// the ones that got there.
    /// </summary>
    internal static void AssertEmbeddedFrameworksCameFromPackages(
        AbsolutePath appPath,
        AbsolutePath packagesDir,
        IReadOnlyCollection<(string PackageId, string Version)> resolvedPackages,
        IReadOnlyCollection<(string PackageId, string Version)> stagedPackages,
        string laneLabel,
        Action<string> info)
    {
        var embedded = IndexEmbeddedFrameworks(appPath);
        var packageImages = ResolvedImages(IndexPackageNativeImages(packagesDir), resolvedPackages);

        var failures = NativeProvenanceFailures(embedded, packageImages, resolvedPackages, stagedPackages);
        if (failures.Count > 0)
            throw new InvalidOperationException(
                $"{laneLabel}: the built app's native code is not the packages' native code:\n  " +
                string.Join("\n  ", failures));

        foreach (var image in embedded.OrderBy(e => e.Framework, StringComparer.Ordinal))
        {
            var source = packageImages.First(p => p.Uuids.Intersect(image.Uuids, StringComparer.Ordinal).Any());
            info($"  {image.Framework}.framework in the app bundle is {source.PackageId} {source.Version}'s own build " +
                 $"(uuid {image.Uuids[0]})");
        }
    }

    // ── Pure judging (covered by TestReleaseConsumption) ────────────────────

    /// <summary>
    /// An xcframework slice directory the iOS Simulator build could have drawn
    /// from. Apple's slice naming is <c>&lt;platform&gt;[-variant]</c>, so an
    /// iOS simulator slice both starts with <c>ios-</c> and names the simulator
    /// — which is what separates it from the device slice beside it and from the
    /// tvOS/macOS/Catalyst slices of the same xcframework.
    /// </summary>
    internal static bool IsIosSimulatorSlice(string slice) =>
        slice.StartsWith("ios-", StringComparison.OrdinalIgnoreCase)
        && slice.Contains("simulator", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The two-directional check, as a pure function of what was found.
    ///
    /// <list type="number">
    /// <item><b>Nothing foreign got in.</b> Every framework in the bundle must
    /// share a UUID with a framework one of the packages THIS RESTORE RESOLVED
    /// ships. A framework with no readable UUID fails too — an unidentifiable
    /// binary is not evidence of anything, and treating it as acceptable would
    /// reopen the hole for exactly the artifacts that are hardest to
    /// attribute.</item>
    /// <item><b>Everything staged got out.</b> Every iOS-simulator framework
    /// the EXACT (id, version) UNDER RELEASE ships must be in the bundle.
    /// Without this the gate is satisfiable by a package that ships no native
    /// code at all.</item>
    /// </list>
    ///
    /// <para>Both rules key on id AND version, because the lane's package cache
    /// is deliberately kept between runs: earlier releases of the same packages
    /// are still sitting in it. Attributing an embedded binary to one of those
    /// would accept native code the restore never chose, and demanding a
    /// retained older version's frameworks would red a correct run. What the
    /// restore resolved is the whole universe, and the versions under release
    /// are the subset that must actually show up.</para>
    ///
    /// <para>A slice whose binary carries no UUID is a static archive rather
    /// than a linked image; it is never embedded, so it is not required to
    /// appear — and it can never satisfy rule 1 for anything either.</para>
    /// </summary>
    internal static IReadOnlyList<string> NativeProvenanceFailures(
        IReadOnlyList<EmbeddedNativeImage> embedded,
        IReadOnlyList<PackageNativeImage> packageImages,
        IReadOnlyCollection<(string PackageId, string Version)> resolvedPackages,
        IReadOnlyCollection<(string PackageId, string Version)> stagedPackages)
    {
        var failures = new List<string>();

        // Callers hand in the cache index; narrowing it here as well keeps the
        // pure function honest when it is exercised directly.
        packageImages = ResolvedImages(packageImages, resolvedPackages);

        if (embedded.Count > 0 && resolvedPackages.Count == 0)
            return new[]
            {
                "the restore graph named no packages, so there is nothing to attribute the app's frameworks to.",
            };

        foreach (var image in embedded.OrderBy(e => e.Framework, StringComparer.Ordinal))
        {
            if (image.Uuids.Count == 0)
            {
                failures.Add(
                    $"{image.Framework}.framework is embedded at {image.Path} but carries no Mach-O LC_UUID, so " +
                    "there is no way to say which build it is. The lane cannot call this package consumption.");
                continue;
            }

            var match = packageImages.FirstOrDefault(p => p.Uuids.Intersect(image.Uuids, StringComparer.Ordinal).Any());
            if (match is null)
                failures.Add(
                    $"{image.Framework}.framework (uuid {string.Join(", ", image.Uuids)}) matches no framework shipped " +
                    "by any package this restore resolved. It came from somewhere else — a working-tree xcframework " +
                    "left by the source build, or a version of the package the restore did not choose — so this run " +
                    "says nothing about the native code the package would install.");
        }

        var embeddedUuids = embedded.SelectMany(e => e.Uuids).ToHashSet(StringComparer.Ordinal);

        var stagedImages = packageImages
            .Where(p => Names(stagedPackages, p))
            .Where(p => IsIosSimulatorSlice(p.Slice))
            .Where(p => p.Uuids.Count > 0)
            .ToList();

        foreach (var image in stagedImages.OrderBy(p => p.Origin, StringComparer.Ordinal))
        {
            if (!image.Uuids.Any(embeddedUuids.Contains))
                failures.Add(
                    $"{image.PackageId} {image.Version} ships {image.Framework}.framework at {image.Origin}, but no " +
                    "framework of that build is embedded in the app. The app linked something else, so the native " +
                    "code under release is not the native code that ran.");
        }

        var stagedWithNative = stagedImages
            .Select(p => $"{p.PackageId}/{p.Version}")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        // SwiftBindings.Apple.<Framework> binds an OS framework and ships managed
        // assemblies only. There is no wrapper xcframework for this check to find.
        // The managed identity gate still has to see that assembly. A third-party
        // package with no identifiable native image stays a failure.
        var stagedWithoutNative = stagedPackages
            .Where(s => !stagedWithNative.Contains($"{s.PackageId}/{s.Version}"))
            .Where(s => !IsSystemFrameworkBinding(s.PackageId))
            .Select(s => $"{s.PackageId} {s.Version}")
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();
        if (stagedWithoutNative.Count > 0)
            failures.Add(
                $"[{string.Join(", ", stagedWithoutNative)}] ship no iOS-simulator framework the lane could identify, " +
                "so the native provenance check would pass vacuously for them. Every SwiftBindings binding package " +
                "carries at least a wrapper xcframework; a package that does not is either mis-packed or a shape this " +
                "gate has not been taught, and both need a human rather than a green.");

        return failures;
    }

    static bool IsSystemFrameworkBinding(string packageId) =>
        packageId.StartsWith("SwiftBindings.Apple.", StringComparison.OrdinalIgnoreCase);

    /// <summary>Does this (id, version) list name the package that shipped this image?</summary>
    static bool Names(IReadOnlyCollection<(string PackageId, string Version)> packages, PackageNativeImage image) =>
        packages.Any(p =>
            string.Equals(p.PackageId, image.PackageId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(p.Version, image.Version, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The cache index narrowed to the versions the restore resolved. The lane's
    /// cache outlives a run, so everything else in it is a package the app was
    /// not supposed to link.
    /// </summary>
    internal static IReadOnlyList<PackageNativeImage> ResolvedImages(
        IReadOnlyList<PackageNativeImage> packageImages,
        IReadOnlyCollection<(string PackageId, string Version)> resolvedPackages) =>
        packageImages.Where(p => Names(resolvedPackages, p)).ToList();

    // ── Filesystem indexing ─────────────────────────────────────────────────

    /// <summary>Every framework under <c>&lt;App&gt;.app/Frameworks</c>, with its identity.</summary>
    internal static IReadOnlyList<EmbeddedNativeImage> IndexEmbeddedFrameworks(AbsolutePath appPath)
    {
        var frameworksDir = appPath / "Frameworks";
        if (!Directory.Exists(frameworksDir)) return Array.Empty<EmbeddedNativeImage>();

        return Directory
            .EnumerateDirectories(frameworksDir, "*.framework")
            .OrderBy(d => d, StringComparer.Ordinal)
            .Select(dir =>
            {
                var name = Path.GetFileNameWithoutExtension(dir);
                var binary = FrameworkBinaryPath(dir, name);
                return new EmbeddedNativeImage(
                    name,
                    binary ?? dir,
                    binary is null ? Array.Empty<string>() : MachOImage.ReadUuids(binary));
            })
            .ToList();
    }

    /// <summary>
    /// Every framework slice shipped by a SwiftBindings package extracted into
    /// the lane's isolated cache — from both native asset layouts this repo
    /// publishes: loose xcframeworks (an SDK binding package's
    /// <c>runtimes/&lt;rid&gt;/native/</c>, the runtime package's
    /// <c>native/</c>) and the <c>lib/&lt;tfm&gt;/&lt;Id&gt;.resources.zip</c>
    /// an ObjC binding package uses to carry its NativeReferences.
    ///
    /// <para>Scoped to <c>SwiftBindings*</c> package folders: those are the only
    /// packages in the graph that ship an xcframework, and walking the whole
    /// restored .NET/iOS closure to prove it would cost seconds per run.</para>
    /// </summary>
    internal static IReadOnlyList<PackageNativeImage> IndexPackageNativeImages(AbsolutePath packagesDir)
    {
        var images = new List<PackageNativeImage>();
        if (!Directory.Exists(packagesDir)) return images;

        foreach (var packageDir in Directory.EnumerateDirectories(packagesDir).OrderBy(d => d, StringComparer.Ordinal))
        {
            var id = Path.GetFileName(packageDir);
            if (!id.StartsWith("swiftbindings", StringComparison.OrdinalIgnoreCase)) continue;

            foreach (var versionDir in Directory.EnumerateDirectories(packageDir).OrderBy(d => d, StringComparer.Ordinal))
            {
                var version = Path.GetFileName(versionDir);

                foreach (var xcframework in Directory
                             .EnumerateDirectories(versionDir, "*.xcframework", SearchOption.AllDirectories)
                             .OrderBy(d => d, StringComparer.Ordinal))
                    images.AddRange(ReadXcframework(xcframework, id, version, versionDir));

                foreach (var archive in Directory
                             .EnumerateFiles(versionDir, "*.resources.zip", SearchOption.AllDirectories)
                             .OrderBy(f => f, StringComparer.Ordinal))
                    images.AddRange(ReadResourcesArchive(archive, id, version, versionDir));
            }
        }

        return images;
    }

    static IEnumerable<PackageNativeImage> ReadXcframework(
        string xcframework, string packageId, string version, string packageRoot)
    {
        foreach (var sliceDir in Directory.EnumerateDirectories(xcframework).OrderBy(d => d, StringComparer.Ordinal))
        {
            var slice = Path.GetFileName(sliceDir);
            foreach (var frameworkDir in Directory
                         .EnumerateDirectories(sliceDir, "*.framework")
                         .OrderBy(d => d, StringComparer.Ordinal))
            {
                var name = Path.GetFileNameWithoutExtension(frameworkDir);
                var binary = FrameworkBinaryPath(frameworkDir, name);
                if (binary is null) continue;

                yield return new PackageNativeImage(
                    packageId, version, name, slice,
                    Path.GetRelativePath(packageRoot, frameworkDir),
                    MachOImage.ReadUuids(binary));
            }
        }
    }

    static IEnumerable<PackageNativeImage> ReadResourcesArchive(
        string archivePath, string packageId, string version, string packageRoot)
    {
        var results = new List<PackageNativeImage>();
        ZipArchive archive;
        try
        {
            archive = ZipFile.OpenRead(archivePath);
        }
        catch (InvalidDataException)
        {
            return results;
        }

        using (archive)
        {
            foreach (var entry in archive.Entries)
            {
                var (slice, framework) = ParseFrameworkBinaryEntry(entry.FullName);
                if (slice is null || framework is null) continue;

                // The entry stream is forward-only; the Mach-O parse seeks, so
                // the binary is materialised in memory first. Only framework
                // binaries reach here, so this is one image at a time, not the
                // whole archive.
                using var buffer = new MemoryStream();
                using (var stream = entry.Open()) stream.CopyTo(buffer);
                buffer.Position = 0;

                results.Add(new PackageNativeImage(
                    packageId, version, framework, slice,
                    $"{Path.GetRelativePath(packageRoot, archivePath)}!{entry.FullName}",
                    MachOImage.ReadUuids(buffer)));
            }
        }

        return results;
    }

    /// <summary>
    /// Recognise <c>…/&lt;Anything&gt;.xcframework/&lt;slice&gt;/&lt;Name&gt;.framework/&lt;Name&gt;</c>
    /// inside a resources archive and return the slice and framework name.
    /// Anything else — an Info.plist, a headers directory, a loose dylib — is
    /// not a framework binary and yields nulls.
    /// </summary>
    internal static (string? Slice, string? Framework) ParseFrameworkBinaryEntry(string entryPath)
    {
        var parts = entryPath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 4) return (null, null);

        var last = parts[^1];
        var frameworkDir = parts[^2];
        if (!frameworkDir.EndsWith(".framework", StringComparison.OrdinalIgnoreCase)) return (null, null);
        if (!string.Equals(frameworkDir[..^".framework".Length], last, StringComparison.Ordinal)) return (null, null);

        var slice = parts[^3];
        if (!parts[^4].EndsWith(".xcframework", StringComparison.OrdinalIgnoreCase)) return (null, null);

        return (slice, last);
    }

    /// <summary>
    /// The Mach-O inside a framework bundle: flat on Apple's embedded platforms,
    /// behind <c>Versions/</c> on macOS. Null when the bundle carries no binary
    /// of its own name.
    /// </summary>
    static string? FrameworkBinaryPath(string frameworkDir, string name)
    {
        var flat = Path.Combine(frameworkDir, name);
        if (File.Exists(flat)) return flat;

        var versioned = Path.Combine(frameworkDir, "Versions", "A", name);
        return File.Exists(versioned) ? versioned : null;
    }
}
