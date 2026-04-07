using System.Net.Http;
using System.Security.Cryptography;
using Nuke.Common.IO;
using Serilog;

namespace SwiftBindings.Build.Helpers;

/// <summary>
/// Cache + SHA-256-verified installer for the pinned <c>spm-to-xcframework</c>
/// tool. Direct port of <c>scripts/ensure-spm-to-xcframework.sh</c>.
///
/// <para>
/// Pinning constants must be bumped together. The cache filename embeds the
/// short ref, so a new ref/SHA combination automatically invalidates older
/// cached copies. A cached copy whose SHA-256 does not match the pin is a
/// hard error — there is no silent fallback.
/// </para>
/// </summary>
public static class SpmToXcframeworkInstaller
{
    // ── Pinning (bump these three constants together) ───────────────────────
    // Mirror of scripts/ensure-spm-to-xcframework.sh:29-31. If you change one,
    // change the other to keep the bash + Nuke paths in lockstep.
    public const string Ref = "d0a6729812cb80ebe467c88bfdb5ca4490b4bf27";
    public const string Sha256 = "5b57db39a2e9bd161462cd9653a4bd9f7f9bb42f2916c24ed067cc96e48b9377";
    public static readonly string Url =
        $"https://raw.githubusercontent.com/justinwojo/spm-to-xcframework/{Ref}/spm-to-xcframework";

    private const string CachePrefix = "spm-to-xcframework-";

    // Single static HttpClient — avoids socket exhaustion under
    // BuildAllXcframeworks parallelism (Parallel.ForEachAsync, dop=4).
    private static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromMinutes(2) };

    // Process-wide lock around the install path. Without it, two parallel
    // BuildAllXcframeworks workers can both observe a cache miss, both
    // download to separate temps, and race on the final File.Move. The
    // contents are SHA-pinned so a race wouldn't corrupt the file, but it's
    // still a race on an executable file in active use — easier to serialize.
    private static readonly object InstallLock = new();

    /// <summary>
    /// Returns the absolute path of the cached, verified script. Downloads on
    /// cache miss. Throws on SHA-256 mismatch.
    /// </summary>
    public static AbsolutePath EnsureInstalled(AbsolutePath toolsCacheDir)
    {
        var shortRef = Ref.Substring(0, 12);
        var cachePath = toolsCacheDir / $"{CachePrefix}{shortRef}";

        // ── Fast path: cached copy already verified ─────────────────────────
        // Lock-free fast path is safe: File.Exists+VerifyFile only reads, and
        // a winning installer above will have done an atomic POSIX rename so
        // we either see the old (cached, valid) inode or the new (cached,
        // valid) inode — never a partial write.
        if (File.Exists(cachePath))
        {
            if (VerifyFile(cachePath))
                return cachePath;

            // Refuse to use a mismatched cached copy. Same semantics as the
            // bash version: we never silently fall back to stale contents.
            throw new InvalidOperationException(
                $"Cached file {cachePath} has wrong sha256 — refusing to use. " +
                "Delete it manually if you trust the pin bump, then re-run.");
        }

        // ── Slow path: serialize installs to avoid the parallel race ────────
        lock (InstallLock)
        {
            // Re-check under the lock — the worker that won the race may have
            // installed it while we were waiting.
            if (File.Exists(cachePath) && VerifyFile(cachePath))
                return cachePath;

            toolsCacheDir.CreateDirectory();
            var tmpPath = toolsCacheDir / $".download.{Path.GetRandomFileName()}";

            try
            {
                Log.Information("[ensure-spm-to-xcframework] downloading {Url}", Url);
                DownloadFile(Url, tmpPath);

                if (!VerifyFile(tmpPath))
                {
                    var actual = ComputeSha256(tmpPath);
                    throw new InvalidOperationException(
                        $"sha256 mismatch: expected {Sha256}, got {actual}. " +
                        "Either the pinned commit was force-pushed, or the download is corrupt.");
                }

                // chmod +x then atomic rename. The script is single-file Python with
                // a #! shebang, so once executable we can invoke it directly.
                MakeExecutable(tmpPath);
                File.Move(tmpPath, cachePath, overwrite: true);
                tmpPath = null!;

                Log.Information("[ensure-spm-to-xcframework] installed {Path}", cachePath);
                return cachePath;
            }
            finally
            {
                if (tmpPath is not null && File.Exists(tmpPath))
                {
                    try { File.Delete(tmpPath); } catch { /* best-effort cleanup */ }
                }
            }
        }
    }

    private static bool VerifyFile(AbsolutePath path)
        => string.Equals(ComputeSha256(path), Sha256, StringComparison.OrdinalIgnoreCase);

    private static string ComputeSha256(AbsolutePath path)
    {
        using var stream = File.OpenRead(path);
        var hash = SHA256.HashData(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static void DownloadFile(string url, AbsolutePath destination)
    {
        // Synchronous wait is fine here — Nuke targets run on the build thread,
        // and we want any download exceptions to surface immediately. Uses the
        // shared static HttpClient to avoid socket exhaustion under parallelism.
        using var response = HttpClient.GetAsync(url).GetAwaiter().GetResult();
        response.EnsureSuccessStatusCode();
        using var fs = File.Create(destination);
        response.Content.CopyToAsync(fs).GetAwaiter().GetResult();
    }

    private static void MakeExecutable(AbsolutePath path)
    {
        if (OperatingSystem.IsWindows())
            return;

        // 0755 — owner rwx, group/world rx. Matches `chmod +x` behaviour for a
        // file that already had 0644 permissions, which is what curl writes.
        File.SetUnixFileMode(path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
    }
}
