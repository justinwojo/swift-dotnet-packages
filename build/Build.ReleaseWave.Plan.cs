using System.Text.Json;
using System.Text.RegularExpressions;
using Nuke.Common;
using Nuke.Common.IO;
using Serilog;
using SwiftBindings.Build.Helpers;

partial class Build
{
    // ═══════════════════════════════════════════════════════════════════════
    //  ReleaseWave — planning layer
    //
    //  Everything in this file is either pure (static, no filesystem, no
    //  network) or a thin filesystem/network reader. The split exists so the
    //  parts that decide WHAT the wave will do — spec parsing, version
    //  arithmetic, the dependency graph, topological ordering, cycle
    //  detection, pin bumps — are directly testable in-process by
    //  TestReleaseWave without building a single package.
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Packages that live in this repo for build/test purposes and must never be
    /// published (see CLAUDE.md "Do not ship to nuget.org"). Hard-excluded from
    /// <c>--all</c> AND rejected if named explicitly — a wave is the one place a
    /// typo would actually push them.
    /// </summary>
    internal static readonly string[] DoNotShipLibraries = { "AppIntents" };

    /// <summary>
    /// Per-library simulator-validation timeout overrides, in seconds. The wave's
    /// base timeout comes from <c>--timeout</c>; an override raises it (never
    /// lowers it) for libraries whose test app is legitimately slower.
    ///
    /// <para>MapLibre's test app fetches map tiles from demotiles.maplibre.org
    /// behind a fixed in-app delay, so it cannot print <c>TEST SUCCESS</c> inside
    /// the 30s default.</para>
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, int> WaveTimeoutOverrides =
        new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["MapLibre"] = 60,
        };

    /// <summary>A <c>&lt;PackageReference Include="SwiftBindings.…"&gt;</c> opening tag,
    /// any attribute order / quote style. Deliberately broader than
    /// <see cref="AppleCrossRefTagPattern"/> (which is Apple-only) because a wave
    /// must see every intra-repo package edge, including future third-party ones.</summary>
    static readonly Regex WavePackageRefTagPattern = new(
        @"<PackageReference\b[^>]*?\bInclude\s*=\s*(?<q>[""'])SwiftBindings\.[^""']+\k<q>[^>]*?>",
        RegexOptions.Compiled);

    /// <summary>The <c>Include="SwiftBindings.X"</c> attribute of a matched tag.</summary>
    static readonly Regex WavePackageRefIncludePattern = new(
        @"\bInclude\s*=\s*(?<q>[""'])(?<id>SwiftBindings\.[^""']+)\k<q>",
        RegexOptions.Compiled);

    /// <summary>A <c>&lt;SwiftFrameworkDependency … PackageId="SwiftBindings.…"&gt;</c>
    /// tag. Multi-line by necessity — BlinkIDUX wraps its attributes across three
    /// lines, so <c>[^>]*</c> must be allowed to span newlines (it is: <c>.</c> is
    /// not used, and a negated class matches <c>\n</c>).</summary>
    static readonly Regex WaveSfdTagPattern = new(
        @"<SwiftFrameworkDependency\b[^>]*?\bPackageId\s*=\s*(?<q>[""'])SwiftBindings\.[^""']+\k<q>[^>]*?>",
        RegexOptions.Compiled);

    static readonly Regex WaveSfdPackageIdPattern = new(
        @"\bPackageId\s*=\s*(?<q>[""'])(?<id>SwiftBindings\.[^""']+)\k<q>",
        RegexOptions.Compiled);

    /// <summary>
    /// The <c>PackageVersion="…"</c> attribute of an SFD tag, in the same
    /// pre/ver/post shape <see cref="AppleCrossRefVersionPattern"/> uses so the
    /// same rewrite code drives both.
    ///
    /// <para>Note the two are safely distinct: <c>\bVersion</c> cannot match
    /// inside <c>PackageVersion</c> because <c>e</c>→<c>V</c> is not a word
    /// boundary.</para>
    /// </summary>
    /// <para>The <c>ver</c> group is <c>[^"']+</c>, not <c>[\d.]+</c>: the wave
    /// accepts prerelease and build-metadata versions (<see cref="WaveVersionPattern"/>),
    /// so both READING an existing prerelease pin and VERIFYING a rewrite to one
    /// have to work. A digits-only group silently made prerelease pins invisible
    /// to discovery and made a successful rewrite look un-applied. The group
    /// cannot over-match: it excludes both quote characters and <c>post</c>
    /// backreferences the opening quote.</para>
    static readonly Regex WaveSfdVersionPattern = new(
        @"(?<pre>\bPackageVersion\s*=\s*(?<q>[""']))(?<ver>[^""']+)(?<post>\k<q>)",
        RegexOptions.Compiled);

    /// <summary>
    /// The <c>Version="…"</c> attribute of a <c>PackageReference</c> tag. Same
    /// shape and same prerelease-tolerant <c>ver</c> group as
    /// <see cref="WaveSfdVersionPattern"/>; deliberately NOT
    /// <see cref="AppleCrossRefVersionPattern"/>, whose digits-only group other
    /// targets depend on.
    ///
    /// <para><c>\bVersion</c> cannot match inside <c>PackageVersion</c> (no word
    /// boundary at <c>e</c>→<c>V</c>) nor inside <c>VersionOverride</c> (the
    /// <c>=</c> does not follow).</para>
    /// </summary>
    static readonly Regex WavePackageRefVersionPattern = new(
        @"(?<pre>\bVersion\s*=\s*(?<q>[""']))(?<ver>[^""']+)(?<post>\k<q>)",
        RegexOptions.Compiled);

    /// <summary>A <c>&lt;ProjectReference Include="../Something/X.csproj"&gt;</c> tag.</summary>
    static readonly Regex WaveProjectRefPattern = new(
        @"<ProjectReference\b[^>]*?\bInclude\s*=\s*(?<q>[""'])(?<path>[^""']+\.csproj)\k<q>",
        RegexOptions.Compiled);

    /// <summary>
    /// Accepted version shape for <c>--wave-spec</c> and pin rewrites: SemVer 2.0,
    /// enforced properly rather than approximately.
    ///
    /// <para>The loose earlier form (<c>([-+][0-9A-Za-z.\-]+)?</c>) accepted three
    /// shapes SemVer forbids, each of which then flows into
    /// <see cref="CompareVersions"/> and a version attribute rewrite: an empty dot
    /// component (<c>1.0.0-alpha..1</c>), a leading-zero numeric identifier
    /// (<c>1.0.0-01</c>, which has no defined precedence against <c>-1</c>), and it
    /// could not express a prerelease AND build metadata together
    /// (<c>1.0.0-rc.1+sha</c>) because it took one tail or the other.</para>
    /// </summary>
    static readonly Regex WaveVersionPattern = new(
        @"^[0-9]+\.[0-9]+\.[0-9]+" +
        @"(?:-(?:0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*)" +
        @"(?:\.(?:0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*))*)?" +
        @"(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$",
        RegexOptions.Compiled);

    // ── Models ──────────────────────────────────────────────────────────────

    /// <summary>
    /// How one csproj reaches another repo package. All three kinds create an
    /// ORDERING edge (they all cause the referenced project's tree to be
    /// read or written during a build); only the first two carry a rewritable
    /// version pin.
    /// </summary>
    internal enum WaveRefKind
    {
        /// <summary><c>&lt;PackageReference Include="SwiftBindings.X" Version="V"/&gt;</c> —
        /// restores from a feed, so it is the one that can produce NU1102.</summary>
        PackageReference,

        /// <summary><c>&lt;SwiftFrameworkDependency … PackageId="SwiftBindings.X"
        /// PackageVersion="V"/&gt;</c> — becomes a nuspec dependency, so a stale
        /// pin ships a wrong dependency version.</summary>
        SwiftFrameworkDependency,

        /// <summary><c>&lt;ProjectReference Include="../X/X.csproj"/&gt;</c> — builds the
        /// target from source (no feed, no pin), but writes into the target's
        /// <c>obj/</c>+<c>bin/</c>, so two libraries holding one must not run
        /// concurrently.</summary>
        ProjectReference,
    }

    /// <summary>
    /// One intra-repo package edge read out of a non-test csproj.
    /// <paramref name="PinnedVersion"/> is null for
    /// <see cref="WaveRefKind.ProjectReference"/>, which carries no version.
    /// </summary>
    internal sealed record CrossPackageRef(
        string ConsumerLibrary, string Csproj, string PackageId, string? PinnedVersion, WaveRefKind Kind);

    /// <summary>
    /// A <c>&lt;PackageReference Version="…"&gt;</c> rewrite the wave will perform,
    /// because the base package is IN the wave at a version different from the pin.
    /// Surfaced in the plan output and the manifest — this is the step the release
    /// process does by hand today, so it must never happen invisibly.
    /// </summary>
    internal sealed record WavePinBump(
        string ConsumerLibrary, string Csproj, string PackageId,
        string FromVersion, string ToVersion, WaveRefKind Kind);

    internal sealed record WaveLibraryPlan(
        string Name,
        LibraryKind Kind,
        string Version,
        IReadOnlyList<string> PackageIds,
        IReadOnlyList<string> DependsOn,
        int Level);

    internal sealed record WavePlan(
        IReadOnlyList<WaveLibraryPlan> Libraries,
        IReadOnlyList<WavePinBump> PinBumps,
        IReadOnlyList<IReadOnlyList<string>> Levels,
        string SdkVersion,
        string? GitSha,
        bool GitDirty,
        bool DryRun,
        int MaxParallel);

    // ── Pure: wave-spec parsing ─────────────────────────────────────────────

    /// <summary>
    /// Parse <c>"Nuke=13.0.10,Stripe=26.0.1"</c> into ordered (library, version)
    /// pairs. Every malformed shape is a hard error rather than a silent skip: a
    /// dropped entry in a release wave is a package that quietly doesn't ship.
    /// </summary>
    internal static IReadOnlyList<(string Library, string Version)> ParseWaveSpec(string spec)
    {
        if (string.IsNullOrWhiteSpace(spec))
            throw new InvalidOperationException(
                "--wave-spec is empty (expected \"Lib=1.2.3,Lib2=4.5.6\").");

        var result = new List<(string, string)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var raw in spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = raw.Split('=');
            if (parts.Length != 2)
                throw new InvalidOperationException(
                    $"--wave-spec entry '{raw}' is malformed (expected Library=Version).");

            var lib = parts[0].Trim();
            var version = parts[1].Trim();
            if (lib.Length == 0)
                throw new InvalidOperationException($"--wave-spec entry '{raw}' has an empty library name.");
            if (!WaveVersionPattern.IsMatch(version))
                throw new InvalidOperationException(
                    $"--wave-spec entry '{raw}' has an invalid version '{version}' (expected semver, e.g. 1.2.3).");
            if (!seen.Add(lib))
                throw new InvalidOperationException(
                    $"--wave-spec names '{lib}' more than once — a wave assigns exactly one version per library.");

            result.Add((lib, version));
        }

        if (result.Count == 0)
            throw new InvalidOperationException(
                $"--wave-spec '{spec}' parsed to zero entries (expected \"Lib=1.2.3,…\").");

        return result;
    }

    // ── Pure: version arithmetic ────────────────────────────────────────────

    /// <summary>
    /// Compare two package versions. Numeric major/minor/patch first (missing
    /// components read as 0), then SemVer 2.0 §11.4 precedence for the prerelease
    /// tail: a release outranks the same numeric prerelease, and two prereleases
    /// compare identifier by identifier — numerically where both identifiers are
    /// numeric, with numeric ranking below alphanumeric, and a shorter identifier
    /// list ranking below a longer one that shares its prefix. Build metadata
    /// (<c>+…</c>) is ignored, per SemVer.
    /// </summary>
    internal static int CompareVersions(string a, string b)
    {
        static (int[] Nums, string Pre) Split(string v)
        {
            var core = v;
            var plus = core.IndexOf('+');
            if (plus >= 0) core = core[..plus];              // build metadata: not significant
            var dash = core.IndexOf('-');
            var pre = dash >= 0 ? core[(dash + 1)..] : "";
            if (dash >= 0) core = core[..dash];

            var nums = new int[3];
            var segs = core.Split('.');
            for (var i = 0; i < 3; i++)
                nums[i] = i < segs.Length && int.TryParse(segs[i], out var n) ? n : 0;
            return (nums, pre);
        }

        var (an, ap) = Split(a);
        var (bn, bp) = Split(b);
        for (var i = 0; i < 3; i++)
        {
            var c = an[i].CompareTo(bn[i]);
            if (c != 0) return c;
        }
        if (ap.Length == 0 && bp.Length == 0) return 0;
        if (ap.Length == 0) return 1;   // release > prerelease
        if (bp.Length == 0) return -1;

        // SemVer 2.0 §11.4: compare dot-separated identifiers left to right.
        // Numeric identifiers compare NUMERICALLY (an ordinal compare puts
        // rc.10 below rc.2) and always rank below alphanumeric ones; a shorter
        // prefix ranks below a longer one with the same lead.
        var aIds = ap.Split('.');
        var bIds = bp.Split('.');
        for (var i = 0; i < Math.Max(aIds.Length, bIds.Length); i++)
        {
            if (i >= aIds.Length) return -1;
            if (i >= bIds.Length) return 1;
            // BigInteger, not int: a numeric prerelease identifier has no width
            // limit in SemVer, and these strings come off nuget.org, not just off
            // our own --wave-spec. int.TryParse on an oversized identifier fails,
            // which would silently reclassify a numeric identifier as
            // alphanumeric and invert its precedence.
            var aNum = System.Numerics.BigInteger.TryParse(aIds[i], out var av);
            var bNum = System.Numerics.BigInteger.TryParse(bIds[i], out var bv);
            int c;
            if (aNum && bNum) c = av.CompareTo(bv);
            else if (aNum) c = -1;               // numeric < alphanumeric
            else if (bNum) c = 1;
            else c = string.CompareOrdinal(aIds[i], bIds[i]);
            if (c != 0) return c;
        }
        return 0;
    }

    /// <summary>
    /// The next patch release after <paramref name="version"/>. A prerelease tail
    /// is dropped and the patch still increments (<c>1.2.3-beta → 1.2.4</c>) — the
    /// alternative, promoting to <c>1.2.3</c>, could collide with an already
    /// published release.
    /// </summary>
    internal static string NextPatchVersion(string version)
    {
        var core = version;
        var cut = core.IndexOfAny(new[] { '-', '+' });
        if (cut >= 0) core = core[..cut];
        var segs = core.Split('.');
        var major = segs.Length > 0 && int.TryParse(segs[0], out var a) ? a : 0;
        var minor = segs.Length > 1 && int.TryParse(segs[1], out var b) ? b : 0;
        var patch = segs.Length > 2 && int.TryParse(segs[2], out var c) ? c : 0;
        return $"{major}.{minor}.{patch + 1}";
    }

    /// <summary>
    /// The documented third-party auto-version rule (CLAUDE.md "Versioning
    /// convention"): a new upstream release ships AT the upstream version; a
    /// rebuild of the same upstream bumps the PATCH of the last published NuGet
    /// version. The NuGet patch routinely exceeds the upstream patch, which is why
    /// this compares rather than assuming.
    ///
    /// <paramref name="lastPublished"/> null (never published) ⇒ ship at
    /// <paramref name="upstreamVersion"/>.
    /// </summary>
    internal static string ResolveThirdPartyVersion(string? upstreamVersion, string? lastPublished)
    {
        if (string.IsNullOrEmpty(lastPublished))
            return string.IsNullOrEmpty(upstreamVersion)
                ? throw new InvalidOperationException(
                    "Cannot auto-version: the library has neither a library.json version nor a published NuGet version.")
                : upstreamVersion!;

        if (!string.IsNullOrEmpty(upstreamVersion) && CompareVersions(upstreamVersion!, lastPublished!) > 0)
            return upstreamVersion!;

        return NextPatchVersion(lastPublished!);
    }

    // ── Pure: dependency graph ──────────────────────────────────────────────

    /// <summary>
    /// Reduce the raw intra-repo package edges to a wave-scoped dependency map
    /// (consumer library → base libraries it must run after) plus the
    /// <see cref="WaveRefKind.PackageReference"/> edges whose base is NOT in the
    /// wave (which the caller must verify is installable).
    ///
    /// <para>
    /// ALL THREE edge kinds produce ordering edges, not just PackageReference. A
    /// <c>ProjectReference</c> or a cross-library <c>SwiftFrameworkDependency</c>
    /// does not hit a NuGet feed, but it does make the consumer's build read and
    /// WRITE the producer's tree (<c>obj/</c>, <c>bin/</c>, and for SFD the
    /// producer's <c>.xcframework</c> via
    /// <see cref="BuildCrossLibraryXcframeworkPrerequisites"/>). Two wave slots
    /// touching one tree concurrently is a data race regardless of how the
    /// reference is spelled. Today that is exactly BlinkIDUX → BlinkID, which
    /// carries all three forms at once.
    /// </para>
    /// <para>
    /// Self-edges ARE dropped here: an edge whose producer is the consumer's own
    /// library is intra-library and already sequenced inside a single
    /// <c>BuildAndPackReleaseForLibrary</c> call (Stripe's 14 products cross-refer
    /// via <c>ProjectReference</c>; Facebook's five via SFD). They are dropped for
    /// ORDERING only — <see cref="ComputePinBumps"/> still rewrites their pins.
    /// </para>
    /// </summary>
    internal static (
        IReadOnlyDictionary<string, IReadOnlyList<string>> Deps,
        IReadOnlyList<CrossPackageRef> ExternalBases)
        BuildWaveDeps(
            IReadOnlyCollection<string> waveLibraries,
            IReadOnlyList<CrossPackageRef> refs,
            IReadOnlyDictionary<string, string> packageOwner)
    {
        var inWave = new HashSet<string>(waveLibraries, StringComparer.Ordinal);
        var deps = waveLibraries.ToDictionary(
            l => l, _ => (IReadOnlyList<string>)new List<string>(), StringComparer.Ordinal);
        var external = new List<CrossPackageRef>();

        foreach (var r in refs.OrderBy(r => r.ConsumerLibrary, StringComparer.Ordinal)
                              .ThenBy(r => r.PackageId, StringComparer.Ordinal)
                              .ThenBy(r => r.Kind))
        {
            if (!inWave.Contains(r.ConsumerLibrary))
                continue;                                   // consumer isn't in this wave — not our problem

            if (!packageOwner.TryGetValue(r.PackageId, out var producer))
                continue;                                   // not an intra-repo package (e.g. a third-party dep)

            if (string.Equals(producer, r.ConsumerLibrary, StringComparison.Ordinal))
                continue;                                   // intra-library — one build unit

            if (inWave.Contains(producer))
            {
                var list = (List<string>)deps[r.ConsumerLibrary];
                if (!list.Contains(producer, StringComparer.Ordinal))
                    list.Add(producer);
            }
            else if (r.Kind == WaveRefKind.PackageReference)
            {
                // Only a PackageReference restores from a feed, so only it can
                // fail with NU1102 when its base is absent. An out-of-wave
                // ProjectReference/SFD base builds from the working tree, which
                // is present by definition.
                external.Add(r);
            }
        }

        return (deps, external);
    }

    /// <summary>
    /// Kahn levels over the wave dependency map: level 0 has no in-wave
    /// dependencies, level N depends only on levels &lt; N. Each level is sorted
    /// ordinally so a plan is reproducible. A cycle is a hard error naming the
    /// nodes still standing — a wave cannot order a cycle, and silently picking an
    /// order would produce exactly the NU1102 failure the ordering exists to fix.
    /// </summary>
    internal static IReadOnlyList<IReadOnlyList<string>> TopoLevels(
        IReadOnlyDictionary<string, IReadOnlyList<string>> deps,
        string what = "wave libraries")
    {
        var remaining = deps.ToDictionary(
            kv => kv.Key,
            kv => new HashSet<string>(kv.Value.Where(deps.ContainsKey), StringComparer.Ordinal),
            StringComparer.Ordinal);

        var levels = new List<IReadOnlyList<string>>();
        while (remaining.Count > 0)
        {
            var ready = remaining
                .Where(kv => kv.Value.Count == 0)
                .Select(kv => kv.Key)
                .OrderBy(k => k, StringComparer.Ordinal)
                .ToList();

            if (ready.Count == 0)
            {
                var stuck = remaining
                    .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                    .Select(kv => $"{kv.Key} → [{string.Join(", ", kv.Value.OrderBy(v => v, StringComparer.Ordinal))}]");
                throw new InvalidOperationException(
                    $"ReleaseWave: dependency cycle among {what} — no publishable order exists:\n  " +
                    string.Join("\n  ", stuck));
            }

            levels.Add(ready);
            foreach (var r in ready) remaining.Remove(r);
            foreach (var set in remaining.Values)
                foreach (var r in ready) set.Remove(r);
        }

        return levels;
    }

    /// <summary>
    /// Publish tiers for the packages of ONE library, ordered by their intra-library
    /// sibling references. Tier 0 depends on no sibling; tier N only on tiers
    /// &lt; N. Packages within a tier are independent and safe to push concurrently.
    ///
    /// <para>Why this exists: <see cref="BuildWaveDeps"/> deliberately collapses a
    /// multi-product library into ONE build unit — Stripe's products cross-reference
    /// each other and are built together by the two-pass flow, so there is no
    /// intra-library build ordering to express. Publishing is different. Pushing all
    /// 14 Stripe packages concurrently means <c>SwiftBindings.Stripe.Payments</c> can
    /// go live declaring a dependency on <c>…Core</c> at a version whose own push is
    /// still in flight — or failed. Tiering the pushes makes a base package live
    /// before anything that declares a dependency on it, which is the same guarantee
    /// the library-level levels give across libraries.</para>
    /// </summary>
    /// <param name="packageIds">The library's shippable ids (internal products already excluded).</param>
    /// <param name="refs">All discovered refs; filtered to this library here.</param>
    internal static IReadOnlyList<IReadOnlyList<string>> ComputeIntraLibraryPublishTiers(
        string library,
        IReadOnlyList<string> packageIds,
        IReadOnlyList<CrossPackageRef> refs)
    {
        var shippable = new HashSet<string>(packageIds, StringComparer.Ordinal);
        var deps = packageIds.ToDictionary(
            id => id, _ => (IReadOnlyList<string>)new List<string>(), StringComparer.Ordinal);

        foreach (var r in refs)
        {
            if (!string.Equals(r.ConsumerLibrary, library, StringComparison.Ordinal)) continue;

            // The consumer package id is the csproj basename — the same invariant
            // DiscoverPackageOwners and Pack rely on.
            var consumerId = Path.GetFileNameWithoutExtension(r.Csproj);
            if (!shippable.Contains(consumerId)) continue;   // e.g. a test csproj, or an internal product
            if (!shippable.Contains(r.PackageId)) continue;  // sibling isn't shipped (internal) or is another library's
            if (string.Equals(consumerId, r.PackageId, StringComparison.Ordinal)) continue;

            var list = (List<string>)deps[consumerId];
            if (!list.Contains(r.PackageId, StringComparer.Ordinal))
                list.Add(r.PackageId);
        }

        return TopoLevels(deps, $"the packages of {library}");
    }

    /// <summary>
    /// Compute every version-pin rewrite the wave needs so that no package ships
    /// declaring a dependency on a version the wave is superseding.
    ///
    /// <para>Two distinct cases, both required:</para>
    /// <list type="number">
    ///   <item><b>Inter-library</b> (<c>PackageReference Version</c>, and
    ///     cross-library <c>SFD PackageVersion</c>): the base is a different
    ///     library that this wave is republishing at a new version — e.g.
    ///     MatterSupport pinning Matter, BlinkIDUX pinning BlinkID.</item>
    ///   <item><b>Intra-library</b> (<c>SFD PackageVersion</c> only): a
    ///     multi-product library's sibling pins. Packing passes
    ///     <c>/p:Version</c>, which changes the package's OWN version but not
    ///     the sibling dependency versions baked into these attributes — so
    ///     Stripe released at 26.0.1 would otherwise ship
    ///     <c>SwiftBindings.Stripe.Payments 26.0.1</c> declaring a dependency on
    ///     <c>SwiftBindings.Stripe.Core 26.0.0</c>. These products ship in
    ///     lockstep, so the pin must move with them. Self-edges are therefore
    ///     NOT skipped here (unlike <see cref="BuildWaveDeps"/>).</item>
    /// </list>
    ///
    /// <para><see cref="WaveRefKind.ProjectReference"/> carries no version and
    /// never yields a bump — <c>dotnet pack</c> derives that nuspec dependency
    /// from the referenced project's own version at pack time.</para>
    /// </summary>
    internal static IReadOnlyList<WavePinBump> ComputePinBumps(
        IReadOnlyList<CrossPackageRef> refs,
        IReadOnlyDictionary<string, string> packageOwner,
        IReadOnlyDictionary<string, string> waveVersions)
    {
        var bumps = new List<WavePinBump>();
        foreach (var r in refs.OrderBy(r => r.Csproj, StringComparer.Ordinal)
                              .ThenBy(r => r.PackageId, StringComparer.Ordinal)
                              .ThenBy(r => r.Kind))
        {
            if (r.Kind == WaveRefKind.ProjectReference) continue;   // no version to rewrite
            if (r.PinnedVersion is null) continue;
            if (!waveVersions.ContainsKey(r.ConsumerLibrary)) continue;
            if (!packageOwner.TryGetValue(r.PackageId, out var producer)) continue;
            if (!waveVersions.TryGetValue(producer, out var producerVersion)) continue;
            if (string.Equals(producerVersion, r.PinnedVersion, StringComparison.Ordinal)) continue;

            bumps.Add(new WavePinBump(
                r.ConsumerLibrary, r.Csproj, r.PackageId, r.PinnedVersion, producerVersion, r.Kind));
        }
        return bumps;
    }

    // ── Filesystem readers ──────────────────────────────────────────────────

    /// <summary>
    /// Every non-test package csproj in the repo, keyed by the library directory
    /// that owns it. Skips <c>tests/</c>, <c>obj/</c> and <c>bin/</c> — a generated
    /// csproj under obj/ is build output, not a package.
    /// </summary>
    /// <remarks>
    /// Known limitation, safe on the current tree: because <c>tests/</c> is
    /// skipped, a cross-library reference that exists ONLY in a test csproj
    /// creates no wave ordering edge. Today every cross-library edge also
    /// appears on a package csproj (MatterSupport→Matter and
    /// RealityKit→RealityFoundation as PackageReferences, BlinkIDUX→BlinkID as
    /// ProjectReference + SwiftFrameworkDependency), and the live-tree assertions
    /// in <c>TestReleaseWave</c> fail if that stops being true for the known
    /// pairs. A future test-only cross-library ProjectReference would need to be
    /// added here, or two libraries could write into the same <c>obj/</c>
    /// concurrently.
    /// </remarks>
    IReadOnlyList<(string Library, LibraryKind Kind, AbsolutePath Csproj)> DiscoverPackageCsprojs()
    {
        var result = new List<(string, LibraryKind, AbsolutePath)>();

        void Scan(AbsolutePath root, LibraryKind kind)
        {
            if (!Directory.Exists(root)) return;
            foreach (var dir in Directory.EnumerateDirectories(root).OrderBy(d => d, StringComparer.Ordinal))
            {
                var lib = Path.GetFileName(dir)!;
                foreach (var csproj in Directory.EnumerateFiles(dir, "SwiftBindings.*.csproj", SearchOption.AllDirectories)
                             .OrderBy(f => f, StringComparer.Ordinal))
                {
                    var rel = Path.GetRelativePath(dir, csproj);
                    var segs = rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    if (segs.Any(s => s.Equals("tests", StringComparison.OrdinalIgnoreCase)
                                   || s.Equals("obj", StringComparison.OrdinalIgnoreCase)
                                   || s.Equals("bin", StringComparison.OrdinalIgnoreCase)))
                        continue;
                    result.Add((lib, kind, (AbsolutePath)csproj));
                }
            }
        }

        Scan(LibrariesDir, LibraryKind.ThirdParty);
        Scan(AppleFrameworksDir, LibraryKind.AppleFramework);
        return result;
    }

    /// <summary>
    /// PackageId → owning library directory name, for every package csproj in the
    /// repo. The id is the csproj basename, the same invariant
    /// <see cref="Pack"/> relies on to find the produced <c>.nupkg</c>.
    /// </summary>
    IReadOnlyDictionary<string, string> DiscoverPackageOwners()
    {
        var owners = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (lib, _, csproj) in DiscoverPackageCsprojs())
        {
            var id = Path.GetFileNameWithoutExtension((string)csproj);
            if (owners.TryGetValue(id, out var existing) && !string.Equals(existing, lib, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"Package id collision: {id} is produced by both '{existing}' and '{lib}'.");
            owners[id] = lib;
        }
        return owners;
    }

    /// <summary>
    /// The non-internal package ids a library ships. Third-party libraries read
    /// <c>library.json</c> (so <c>"internal": true</c> products are excluded —
    /// internal products have no csproj and are NEVER packed); Apple frameworks
    /// are a single csproj.
    /// </summary>
    IReadOnlyList<string> ResolveLibraryPackageIds(string library)
    {
        var (kind, dir) = ResolveLibrary(library);
        if (kind == LibraryKind.AppleFramework)
        {
            var csproj = dir.GlobFiles("SwiftBindings.*.csproj").SingleOrDefault()
                ?? throw new InvalidOperationException($"No SwiftBindings.*.csproj found in {dir}");
            return new[] { Path.GetFileNameWithoutExtension((string)csproj) };
        }

        var config = LibraryConfigLoader.Load(LibraryConfigPath(library));
        return config.Products
            .Where(p => !p.Internal)
            .Select(p => Path.GetFileNameWithoutExtension((string)p.CsprojPath(dir)))
            .ToList();
    }

    /// <summary>
    /// Scan every non-test package csproj for all three intra-repo reference
    /// forms (see <see cref="WaveRefKind"/>).
    ///
    /// <para>A <c>PackageReference</c>/<c>SwiftFrameworkDependency</c> tag whose
    /// version attribute can't be read is a hard error, not a skip: the wave's
    /// whole job is to get those versions right, and a pin it can't read is one it
    /// can't order or bump. (This mirrors
    /// <see cref="AssertCrossFrameworkCoherence"/>, which hard-fails on the same
    /// child-element <c>&lt;Version&gt;</c> form for the Apple train.)</para>
    /// </summary>
    IReadOnlyList<CrossPackageRef> DiscoverCrossPackageRefs()
    {
        var refs = new List<CrossPackageRef>();
        var unreadable = new List<string>();
        string Rel(string p) => Path.GetRelativePath(RootDirectory, p);

        foreach (var (lib, _, csproj) in DiscoverPackageCsprojs())
        {
            var content = File.ReadAllText(csproj);

            foreach (Match tag in WavePackageRefTagPattern.Matches(content))
            {
                var idM = WavePackageRefIncludePattern.Match(tag.Value);
                if (!idM.Success) continue;
                var id = idM.Groups["id"].Value;

                var verM = WavePackageRefVersionPattern.Match(tag.Value);
                if (!verM.Success)
                {
                    unreadable.Add($"  {Rel(csproj)} → PackageReference {id} (no Version attribute)");
                    continue;
                }
                refs.Add(new CrossPackageRef(
                    lib, (string)csproj, id, verM.Groups["ver"].Value, WaveRefKind.PackageReference));
            }

            foreach (Match tag in WaveSfdTagPattern.Matches(content))
            {
                var idM = WaveSfdPackageIdPattern.Match(tag.Value);
                if (!idM.Success) continue;
                var id = idM.Groups["id"].Value;

                var verM = WaveSfdVersionPattern.Match(tag.Value);
                if (!verM.Success)
                {
                    unreadable.Add($"  {Rel(csproj)} → SwiftFrameworkDependency {id} (no PackageVersion attribute)");
                    continue;
                }
                refs.Add(new CrossPackageRef(
                    lib, (string)csproj, id, verM.Groups["ver"].Value, WaveRefKind.SwiftFrameworkDependency));
            }

            // ProjectReference targets are resolved by path, then mapped to a
            // package id via the csproj basename — the same id⇔filename
            // invariant Pack relies on. Unresolvable paths are ignored rather
            // than fatal: a ProjectReference into a non-package project is
            // legitimate and carries no version to get wrong.
            foreach (Match m in WaveProjectRefPattern.Matches(content))
            {
                var target = Path.GetFullPath(Path.Combine(Path.GetDirectoryName((string)csproj)!,
                    m.Groups["path"].Value.Replace('\\', '/')));
                var id = Path.GetFileNameWithoutExtension(target);
                if (!id.StartsWith("SwiftBindings.", StringComparison.Ordinal)) continue;
                refs.Add(new CrossPackageRef(lib, (string)csproj, id, null, WaveRefKind.ProjectReference));
            }
        }

        if (unreadable.Count > 0)
            throw new InvalidOperationException(
                $"ReleaseWave: {unreadable.Count} intra-repo reference(s) carry no readable version attribute, " +
                "so the wave cannot order or re-pin them:\n" + string.Join("\n", unreadable) +
                "\nConvert each to attribute form (Version=\"N.N.N\" / PackageVersion=\"N.N.N\").");

        return refs;
    }

    /// <summary>
    /// The single SwiftBindings.Sdk version every package csproj pins. More than
    /// one distinct value is a hard error — the wave would then build half the
    /// catalog against a different generator than the other half.
    /// </summary>
    string DiscoverPinnedSdkVersion()
    {
        var versions = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
        var pattern = new Regex(@"Sdk\s*=\s*[""']SwiftBindings\.Sdk/(?<ver>[^""']+)[""']", RegexOptions.Compiled);

        foreach (var (_, _, csproj) in DiscoverPackageCsprojs())
        {
            var m = pattern.Match(File.ReadAllText(csproj));
            if (!m.Success) continue;
            var v = m.Groups["ver"].Value;
            if (!versions.TryGetValue(v, out var list)) versions[v] = list = new List<string>();
            list.Add(Path.GetRelativePath(RootDirectory, csproj));
        }

        if (versions.Count == 0)
            throw new InvalidOperationException(
                "ReleaseWave: no package csproj pins SwiftBindings.Sdk — cannot determine the SDK under release.");
        if (versions.Count > 1)
            throw new InvalidOperationException(
                "ReleaseWave: package csprojs pin more than one SwiftBindings.Sdk version:\n" +
                string.Join("\n", versions.Select(kv => $"  {kv.Key}: {string.Join(", ", kv.Value.Take(4))}" +
                                                        (kv.Value.Count > 4 ? $", +{kv.Value.Count - 4} more" : ""))) +
                "\nRun `dotnet nuke BumpSdkVersion --version X.Y.Z` before releasing.");

        return versions.Keys.First();
    }

    // ── nuget.org flat-container reads ──────────────────────────────────────

    static readonly HttpClient WaveHttp = new() { Timeout = TimeSpan.FromSeconds(30) };

    /// <summary>
    /// Every version of <paramref name="packageId"/> on nuget.org, oldest first, or
    /// <c>null</c> when the package does not exist (HTTP 404). Any other failure
    /// throws — "we couldn't reach nuget.org" and "this package was never
    /// published" must not collapse into the same answer, because the first makes a
    /// computed version wrong and the second makes it right.
    /// </summary>
    static async Task<IReadOnlyList<string>?> FetchPublishedVersionsAsync(string packageId)
    {
        var url = $"https://api.nuget.org/v3-flatcontainer/{packageId.ToLowerInvariant()}/index.json";
        using var response = await WaveHttp.GetAsync(url);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return null;
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("versions", out var arr))
            return Array.Empty<string>();
        return arr.EnumerateArray().Select(e => e.GetString()).OfType<string>().ToList();
    }

    /// <summary>
    /// The highest published version of <paramref name="packageId"/>, or null if it
    /// has never been published. Uses <see cref="CompareVersions"/> rather than
    /// trusting the index's array order.
    /// </summary>
    static async Task<string?> FetchLatestPublishedAsync(string packageId)
    {
        var versions = await FetchPublishedVersionsAsync(packageId);
        if (versions is null || versions.Count == 0) return null;
        return versions.Aggregate((a, b) => CompareVersions(a, b) >= 0 ? a : b);
    }

    /// <summary>True when the exact (id, version) pair is live on nuget.org.</summary>
    static async Task<bool> IsPublishedAsync(string packageId, string version)
    {
        var versions = await FetchPublishedVersionsAsync(packageId);
        return versions is not null &&
               versions.Any(v => string.Equals(v, version, StringComparison.OrdinalIgnoreCase));
    }

    // ── Plan construction ───────────────────────────────────────────────────

    /// <summary>
    /// Resolve the library set the wave will act on and the version each gets.
    /// Exactly one selector must be supplied; ambiguity is rejected rather than
    /// silently resolved by precedence.
    /// </summary>
    async Task<IReadOnlyList<(string Library, string Version)>> ResolveWaveSelectionAsync()
    {
        var selectors = new List<string>();
        if (!string.IsNullOrWhiteSpace(WaveSpec)) selectors.Add("--wave-spec");
        if (Libraries.Length > 0) selectors.Add("--libraries");
        if (All) selectors.Add("--all");

        if (selectors.Count == 0)
            throw new InvalidOperationException(
                "ReleaseWave needs exactly one of --wave-spec \"Lib=1.2.3,…\", --libraries A,B,C --auto-version, or --all --auto-version.");
        if (selectors.Count > 1)
            throw new InvalidOperationException(
                $"ReleaseWave: {string.Join(" and ", selectors)} are mutually exclusive — pick one.");

        if (!string.IsNullOrWhiteSpace(WaveSpec))
        {
            var parsed = ParseWaveSpec(WaveSpec!);
            AssertLibrariesExistAndShippable(parsed.Select(p => p.Library).ToList());
            return parsed;
        }

        IReadOnlyList<string> names;
        if (Libraries.Length > 0)
        {
            names = Libraries
                .SelectMany(l => l.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                .Distinct(StringComparer.Ordinal)
                .ToList();
            AssertLibrariesExistAndShippable(names);
        }
        else
        {
            names = await ResolvePublishedCatalogAsync();
        }

        if (!AutoVersion)
            throw new InvalidOperationException(
                "--libraries / --all need --auto-version (or use --wave-spec to assign versions explicitly).");

        var resolved = new List<(string, string)>();
        foreach (var lib in names.OrderBy(n => n, StringComparer.Ordinal))
            resolved.Add((lib, await ResolveAutoVersionAsync(lib)));
        return resolved;
    }

    /// <summary>
    /// Reject unknown names, and reject the do-not-ship list even when named
    /// explicitly — a wave is precisely where a typo would push them.
    /// </summary>
    void AssertLibrariesExistAndShippable(IReadOnlyList<string> names)
    {
        var unknown = names
            .Where(n => !Directory.Exists(LibrariesDir / n) && !Directory.Exists(AppleFrameworksDir / n))
            .ToList();
        if (unknown.Count > 0)
            throw new InvalidOperationException(
                $"ReleaseWave: unknown library/framework name(s): {string.Join(", ", unknown)}.");

        var blocked = names.Where(n => DoNotShipLibraries.Contains(n, StringComparer.Ordinal)).ToList();
        if (blocked.Count > 0)
            throw new InvalidOperationException(
                $"ReleaseWave: {string.Join(", ", blocked)} is on the do-not-ship list (CLAUDE.md " +
                "\"Do not ship to nuget.org\") and cannot be included in a wave.");
    }

    /// <summary>
    /// The <c>--all</c> set: every library whose packages ALREADY exist on
    /// nuget.org. Libraries that are in the repo but deliberately unpublished
    /// (Facebook and MapLibre today) are therefore never swept in — shipping a
    /// package for the first time is an explicit act, so it must be named in
    /// <c>--wave-spec</c>/<c>--libraries</c>.
    /// </summary>
    async Task<IReadOnlyList<string>> ResolvePublishedCatalogAsync()
    {
        var candidates = DiscoverAllEntries()
            .Select(e => e.Library)
            .Where(l => !DoNotShipLibraries.Contains(l, StringComparer.Ordinal))
            .ToList();

        var published = new List<string>();
        var skipped = new List<string>();

        foreach (var lib in candidates)
        {
            var ids = ResolveLibraryPackageIds(lib);
            if (ids.Count == 0) { skipped.Add($"{lib} (no packages)"); continue; }

            var anyPublished = false;
            foreach (var id in ids)
                if (await FetchLatestPublishedAsync(id) is not null) { anyPublished = true; break; }

            if (anyPublished) published.Add(lib);
            else skipped.Add($"{lib} (never published)");
        }

        Log.Information("--all: {Count} published librar(ies): {Libs}", published.Count, string.Join(", ", published));
        if (skipped.Count > 0)
            Log.Information("--all: excluded {Count}: {Skipped} — name them explicitly to release them for the first time.",
                skipped.Count, string.Join(", ", skipped));
        if (DoNotShipLibraries.Length > 0)
            Log.Information("--all: do-not-ship excluded: {Libs}", string.Join(", ", DoNotShipLibraries));

        if (published.Count == 0)
            throw new InvalidOperationException(
                "--all resolved to zero libraries — nothing on nuget.org matched this repo's package ids.");

        return published;
    }

    /// <summary>
    /// Auto-version one library per the documented convention. Apple frameworks
    /// share ONE version tracking the SwiftBindings.Apple train (from
    /// <c>--apple-version</c>, else the highest published Apple package + 1 patch);
    /// third-party libraries follow <see cref="ResolveThirdPartyVersion"/>.
    /// </summary>
    async Task<string> ResolveAutoVersionAsync(string library)
    {
        var (kind, _) = ResolveLibrary(library);
        var ids = ResolveLibraryPackageIds(library);

        if (kind == LibraryKind.AppleFramework)
            return _appleWaveVersion ??= await ResolveAppleTrainVersionAsync();

        // Take the highest last-published across the library's product ids: they
        // ship in lockstep, so one lagging sub-package must not drag the wave
        // version back onto an already-published number.
        string? lastPublished = null;
        foreach (var id in ids)
        {
            var latest = await FetchLatestPublishedAsync(id);
            if (latest is not null && (lastPublished is null || CompareVersions(latest, lastPublished) > 0))
                lastPublished = latest;
        }

        string? upstream = null;
        var configPath = LibraryConfigPath(library);
        if (File.Exists(configPath))
            upstream = LibraryConfigLoader.Load(configPath).Version;

        var resolved = ResolveThirdPartyVersion(upstream, lastPublished);
        Log.Information("auto-version {Library}: upstream={Upstream} lastPublished={Last} → {Resolved}",
            library, upstream ?? "(none)", lastPublished ?? "(none)", resolved);
        return resolved;
    }

    string? _appleWaveVersion;

    /// <summary>
    /// The shared Apple-framework version. <c>--apple-version</c> wins; otherwise
    /// it's one patch past the highest published <c>SwiftBindings.Apple.*</c>
    /// version in the repo, which is how the Apple train has always moved.
    /// </summary>
    async Task<string> ResolveAppleTrainVersionAsync()
    {
        if (!string.IsNullOrWhiteSpace(AppleVersion))
        {
            if (!WaveVersionPattern.IsMatch(AppleVersion!))
                throw new InvalidOperationException($"--apple-version '{AppleVersion}' is not a valid semver.");
            return AppleVersion!;
        }

        string? highest = null;
        foreach (var fw in DiscoverAppleFrameworks())
        {
            if (DoNotShipLibraries.Contains(fw, StringComparer.Ordinal)) continue;
            foreach (var id in ResolveLibraryPackageIds(fw))
            {
                var latest = await FetchLatestPublishedAsync(id);
                if (latest is not null && (highest is null || CompareVersions(latest, highest) > 0))
                    highest = latest;
            }
        }

        if (highest is null)
            throw new InvalidOperationException(
                "Cannot auto-version Apple frameworks: no SwiftBindings.Apple.* package is published. " +
                "Pass --apple-version explicitly.");

        var resolved = NextPatchVersion(highest);
        Log.Information("auto-version Apple train: highestPublished={High} → {Resolved}", highest, resolved);
        return resolved;
    }

    /// <summary>
    /// Assemble the full plan: versions, package ids, dependency edges, topo
    /// levels, pin bumps — and verify that every out-of-wave base a dependent pins
    /// is actually installable (published on nuget.org or already staged in
    /// <c>local-packages/</c>). That check is the difference between a wave that
    /// fails at the END with NU1102 and one that refuses to start.
    /// </summary>
    async Task<WavePlan> BuildWavePlanAsync(IReadOnlyList<(string Library, string Version)> selection, bool dryRun)
    {
        var waveVersions = selection.ToDictionary(s => s.Library, s => s.Version, StringComparer.Ordinal);
        var owners = DiscoverPackageOwners();
        var refs = DiscoverCrossPackageRefs();

        var (deps, external) = BuildWaveDeps(waveVersions.Keys, refs, owners);
        var levels = TopoLevels(deps);
        var pinBumps = ComputePinBumps(refs, owners, waveVersions);

        // Out-of-wave bases: the dependent will restore them from nuget.org (or a
        // pre-staged local nupkg). If neither has it, say so NOW and name the fix.
        var missing = new List<string>();
        foreach (var r in external)
        {
            // BuildWaveDeps only reports PackageReference edges as external, and
            // DiscoverCrossPackageRefs hard-fails on a PackageReference with no
            // readable Version — so a null pin here is a harness bug, not input.
            var pinned = r.PinnedVersion
                ?? throw new InvalidOperationException(
                    $"Internal error: external {r.Kind} ref {r.PackageId} in {r.Csproj} has no pinned version.");

            var localNupkg = LocalPackagesDir / $"{r.PackageId}.{pinned}.nupkg";
            if (File.Exists(localNupkg)) continue;
            if (await IsPublishedAsync(r.PackageId, pinned)) continue;

            var producer = owners.TryGetValue(r.PackageId, out var p) ? p : "(unknown library)";
            missing.Add(
                $"  {r.ConsumerLibrary} pins {r.PackageId} {r.PinnedVersion}, which is neither on nuget.org nor in " +
                $"local-packages/ — add '{producer}' to the wave (e.g. --wave-spec \"…,{producer}={r.PinnedVersion}\").");
        }
        if (missing.Count > 0)
            throw new InvalidOperationException(
                $"ReleaseWave: {missing.Count} in-wave librar(ies) depend on a base package that isn't in the wave " +
                "and isn't installable:\n" + string.Join("\n", missing));

        var levelOf = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < levels.Count; i++)
            foreach (var lib in levels[i]) levelOf[lib] = i;

        var plans = selection
            .Select(s => new WaveLibraryPlan(
                s.Library,
                ResolveLibrary(s.Library).Kind,
                s.Version,
                ResolveLibraryPackageIds(s.Library),
                deps[s.Library].OrderBy(d => d, StringComparer.Ordinal).ToList(),
                levelOf[s.Library]))
            .OrderBy(p => p.Level)
            .ThenBy(p => p.Name, StringComparer.Ordinal)
            .ToList();

        return new WavePlan(
            Libraries: plans,
            PinBumps: pinBumps,
            Levels: levels,
            SdkVersion: DiscoverPinnedSdkVersion(),
            GitSha: TryCapture("git", new[] { "-C", (string)RootDirectory, "rev-parse", "HEAD" }),
            GitDirty: TryCapture("git", new[] { "-C", (string)RootDirectory, "status", "--porcelain" })?.Length > 0,
            DryRun: dryRun,
            MaxParallel: ResolveMaxParallel());
    }

    /// <summary>
    /// Apply the computed <see cref="WavePinBump"/>s to disk. Runs once, before any
    /// build, so the plan the operator read is the plan that executes. Idempotent:
    /// re-running with the same versions rewrites nothing.
    /// </summary>
    /// <remarks>
    /// All-or-nothing across FILES, not just within one. Every group is rewritten
    /// and verified in memory first; only if every planned bump applied does
    /// anything reach disk. Writing as it went meant a bump that failed to apply
    /// in the fifth csproj left the first four rewritten — a half-re-pinned
    /// working tree that the operator then has to unpick by hand, on the failure
    /// path of a mechanism whose entire job is version-pin correctness.
    /// </remarks>
    void ApplyPinBumps(IReadOnlyList<WavePinBump> bumps)
    {
        var pending = new List<(string Csproj, string Content, IReadOnlyList<WavePinBump> Bumps)>();

        foreach (var group in bumps.GroupBy(b => b.Csproj, StringComparer.Ordinal))
        {
            var content = File.ReadAllText(group.Key);
            var updated = content;

            foreach (var bump in group)
            {
                // Match only the tag KIND that produced this bump, so a library
                // carrying both a PackageReference and an SFD for the same
                // package id (BlinkIDUX → BlinkID does) gets each rewritten by
                // its own rule rather than one clobbering the other.
                var (tagPattern, versionPattern) = bump.Kind switch
                {
                    WaveRefKind.PackageReference => (
                        new Regex(@"<PackageReference\b[^>]*?\bInclude\s*=\s*(?<q>[""'])"
                                  + Regex.Escape(bump.PackageId) + @"\k<q>[^>]*?>", RegexOptions.Compiled),
                        WavePackageRefVersionPattern),
                    WaveRefKind.SwiftFrameworkDependency => (
                        new Regex(@"<SwiftFrameworkDependency\b[^>]*?\bPackageId\s*=\s*(?<q>[""'])"
                                  + Regex.Escape(bump.PackageId) + @"\k<q>[^>]*?>", RegexOptions.Compiled),
                        WaveSfdVersionPattern),
                    _ => throw new InvalidOperationException(
                        $"ProjectReference bumps carry no version: {bump.PackageId} in {bump.Csproj}"),
                };

                // Count the tags that actually carry the target version AFTER the
                // rewrite. A planned bump that silently rewrites nothing (regex
                // drift, unexpected attribute quoting, a hand-edit between plan
                // and apply) is the one failure mode this whole mechanism exists
                // to prevent: the wave would go on to ship a package still
                // pinned to the OLD sibling version while the plan claims
                // otherwise. Fail loudly instead.
                var applied = 0;
                updated = tagPattern.Replace(updated, tag =>
                {
                    var rewritten = versionPattern.Replace(
                        tag.Value,
                        m => m.Groups["pre"].Value + bump.ToVersion + m.Groups["post"].Value,
                        1);
                    var check = versionPattern.Match(rewritten);
                    if (check.Success && check.Groups["ver"].Value == bump.ToVersion) applied++;
                    return rewritten;
                });

                if (applied == 0)
                    throw new InvalidOperationException(
                        $"Pin bump did not apply: expected {bump.PackageId} " +
                        $"{bump.FromVersion} → {bump.ToVersion} ({bump.Kind}) in " +
                        $"{Path.GetRelativePath(RootDirectory, bump.Csproj)}, but no matching " +
                        "version attribute was rewritten. The csproj changed shape since the " +
                        "plan was computed, or the tag/version pattern needs updating. " +
                        "Aborting before any build so nothing ships with a stale pin.");
            }

            // Not an error: every bump verified above, so an unchanged file just
            // means the csproj already carried the target versions (re-run).
            if (updated == content) continue;
            pending.Add((group.Key, updated, group.ToList()));
        }

        // Every planned bump verified. Only now does anything touch disk.
        foreach (var (csproj, content, groupBumps) in pending)
        {
            File.WriteAllText(csproj, content);
            Log.Information("  pin bump: {Csproj} → {Bumps}",
                Path.GetRelativePath(RootDirectory, csproj),
                string.Join(", ", groupBumps.Select(b => $"{b.PackageId} {b.FromVersion}→{b.ToVersion}")));
        }
    }

    /// <summary>Render the plan as the block printed before any work starts.</summary>
    static string RenderWavePlan(WavePlan plan)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine();
        sb.AppendLine("=== ReleaseWave plan ===");
        sb.AppendLine($"  mode:        {(plan.DryRun ? "DRY RUN (build + pack + validate, no publish)" : "PUBLISH to nuget.org")}");
        sb.AppendLine($"  SDK:         SwiftBindings.Sdk/{plan.SdkVersion}");
        sb.AppendLine($"  git:         {plan.GitSha ?? "(unknown)"}{(plan.GitDirty ? "  [DIRTY WORKING TREE]" : "")}");
        sb.AppendLine($"  parallelism: {plan.MaxParallel} librar(ies) at a time");
        sb.AppendLine();

        var nameWidth = Math.Max(12, plan.Libraries.Max(l => l.Name.Length) + 2);
        sb.AppendLine($"  {"LIBRARY".PadRight(nameWidth)}{"VERSION".PadRight(12)}{"LVL".PadRight(5)}{"PKGS".PadRight(6)}DEPENDS ON");
        sb.AppendLine($"  {new string('─', nameWidth + 12 + 5 + 6 + 12)}");
        foreach (var lib in plan.Libraries)
            sb.AppendLine($"  {lib.Name.PadRight(nameWidth)}{lib.Version.PadRight(12)}{lib.Level.ToString().PadRight(5)}" +
                          $"{lib.PackageIds.Count.ToString().PadRight(6)}" +
                          $"{(lib.DependsOn.Count == 0 ? "—" : string.Join(", ", lib.DependsOn))}");
        sb.AppendLine();

        sb.AppendLine($"  publish order: {string.Join("  →  ", plan.Levels.Select(l => string.Join(", ", l)))}");

        if (plan.PinBumps.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("  PIN BUMPS (csproj rewrites applied before building — review + commit after the wave):");
            foreach (var b in plan.PinBumps)
                sb.AppendLine($"    {b.ConsumerLibrary}: {b.PackageId} {b.FromVersion} → {b.ToVersion}  " +
                              $"[{b.Kind}] {Path.GetFileName(b.Csproj)}");
        }

        sb.AppendLine();
        return sb.ToString();
    }
}
