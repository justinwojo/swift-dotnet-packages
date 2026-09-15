using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SwiftBindings.Build.Models;

namespace SwiftBindings.Build.Helpers;

/// <summary>
/// LaunchServices-aware runner for Mac Catalyst app bundles. Every launch gets a fresh token,
/// stdout/stderr files, and immutable receipt so a detached app cannot satisfy the current run
/// with output left by an earlier process.
/// </summary>
public static class MacCatalystLaunchRunner
{
    public sealed record Attempt(int Index, string Transport, string RunToken,
        string Directory, TestRunResult Result);

    public static string CreateEvidenceDirectory(string appName)
    {
        var safeName = string.Concat(appName.Select(c => char.IsLetterOrDigit(c) ? c : '-'));
        var path = Path.Combine(Path.GetTempPath(), "swift-bindings-catalyst-attempts",
            $"{DateTime.UtcNow:yyyyMMdd-HHmmss.fff}Z-{safeName}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    public static Attempt RetainDirectAttempt(string evidenceDirectory, int index, string runToken,
        TestRunResult result)
    {
        var attemptDirectory = CreateAttemptDirectory(evidenceDirectory, index, "direct", runToken);
        WriteNew(Path.Combine(attemptDirectory, "console.log"), result.Output);
        WriteReceipt(attemptDirectory, index, "direct", runToken, result);
        return new(index, "direct", runToken, attemptDirectory, result);
    }

    public static async Task<Attempt> RunLaunchServicesAsync(
        string appPath, string binaryPath, int index, string runToken,
        TimeSpan runTimeout, TimeSpan launchTimeout, string evidenceDirectory,
        bool requireTipKitCorrelation, Action<string>? onLine = null,
        CancellationToken cancellationToken = default)
    {
        if (!CatalystLaunchContract.IsValidRunToken(runToken))
            throw new ArgumentException("Expected a fresh N-format GUID launch token.", nameof(runToken));

        var attemptDirectory = CreateAttemptDirectory(
            evidenceDirectory, index, "launchservices", runToken);
        var stdoutPath = Path.Combine(attemptDirectory, "stdout.log");
        var stderrPath = Path.Combine(attemptDirectory, "stderr.log");
        WriteNew(stdoutPath, string.Empty);
        WriteNew(stderrPath, string.Empty);

        var psi = new ProcessStartInfo("/usr/bin/open")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in new[]
        {
            "-n", "-W", "-F", "-g",
            "--stdout", stdoutPath,
            "--stderr", stderrPath,
            appPath,
            "--args", CatalystLaunchContract.RunTokenArgument, runToken,
        })
            psi.ArgumentList.Add(argument);

        using var launcher = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start /usr/bin/open.");
        var launcherStdoutTask = launcher.StandardOutput.ReadToEndAsync(cancellationToken);
        var launcherStderrTask = launcher.StandardError.ReadToEndAsync(cancellationToken);
        var startedUtc = DateTime.UtcNow;
        DateTime? firstAppOutputUtc = null;
        var stdoutState = new FileTailState(stdoutPath, "app stdout");
        var stderrState = new FileTailState(stderrPath, "app stderr");
        var captured = new StringBuilder();
        var status = TestRunStatus.Running;
        string? reason = null;
        var launcherExitedOnItsOwn = false;

        void Handle(string source, string line)
        {
            firstAppOutputUtc ??= DateTime.UtcNow;
            captured.Append('[').Append(source).Append("] ").AppendLine(line);
            onLine?.Invoke(line);
            if (status != TestRunStatus.Running) return;
            var classified = StdoutWatcher.ClassifyImpl(line, out var lineReason);
            if (classified == TestRunStatus.Running) return;
            status = classified;
            reason = lineReason;
        }

        void Drain(bool includePartial)
        {
            stdoutState.Drain(Handle, includePartial);
            stderrState.Drain(Handle, includePartial);
        }

        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Drain(includePartial: false);
                if (status != TestRunStatus.Running)
                    break;

                if (launcher.HasExited)
                {
                    launcherExitedOnItsOwn = true;
                    await Task.Delay(250, cancellationToken).ConfigureAwait(false);
                    Drain(includePartial: true);
                    break;
                }

                var deadline = firstAppOutputUtc is null
                    ? startedUtc + launchTimeout
                    : firstAppOutputUtc.Value + runTimeout;
                if (DateTime.UtcNow >= deadline)
                {
                    status = TestRunStatus.Timeout;
                    reason = firstAppOutputUtc is null
                        ? $"no output from the Catalyst app after {launchTimeout.TotalSeconds:F0}s (launch budget)"
                        : $"no marker after {runTimeout.TotalSeconds:F0}s of Catalyst app output (run budget)";
                    break;
                }

                await Task.Delay(200, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            TerminateExactAppProcess(binaryPath, runToken);
            if (!launcher.HasExited)
            {
                try
                {
                    using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await launcher.WaitForExitAsync(wait.Token).ConfigureAwait(false);
                }
                catch { /* best-effort wait before terminating only the open process */ }
            }
            if (!launcher.HasExited)
            {
                try { launcher.Kill(entireProcessTree: true); } catch { }
            }
            try { launcher.WaitForExit(2000); } catch { }
            Drain(includePartial: true);
        }

        var launcherStdout = await launcherStdoutTask.ConfigureAwait(false);
        var launcherStderr = await launcherStderrTask.ConfigureAwait(false);
        AppendLauncherOutput(captured, "open stdout", launcherStdout, onLine);
        AppendLauncherOutput(captured, "open stderr", launcherStderr, onLine);
        var output = captured.ToString();

        if (status == TestRunStatus.Running)
        {
            foreach (var line in output.Split('\n'))
            {
                var classified = StdoutWatcher.ClassifyImpl(line, out var lineReason);
                if (classified == TestRunStatus.Running) continue;
                status = classified;
                reason = lineReason;
                break;
            }
        }
        if (status == TestRunStatus.Running)
        {
            if (LaunchDiagnostics.LauncherNeverStartedApp(output, appOutputObserved: firstAppOutputUtc is not null))
            {
                status = TestRunStatus.LauncherAborted;
                reason = $"LaunchServices exited with code {SafeExitCode(launcher)} before the app produced output";
            }
            else
            {
                status = TestRunStatus.Exited;
                reason = $"LaunchServices exited with code {SafeExitCode(launcher)} (no marker)";
            }
        }

        var result = new TestRunResult(
            status, reason, output, Array.Empty<string>(),
            launcherExitedOnItsOwn ? SafeExitCode(launcher) : null,
            firstAppOutputUtc is null ? null : firstAppOutputUtc.Value - startedUtc);
        if (result.Status == TestRunStatus.Success && requireTipKitCorrelation &&
            !CatalystLaunchContract.IsCorrelatedTipKitSuccess(result, runToken))
        {
            result = result with
            {
                Status = TestRunStatus.Failed,
                Reason = "TEST SUCCESS was not correlated with this launch's managed-entry and TipKit result markers",
            };
        }

        WriteNew(Path.Combine(attemptDirectory, "console.log"), result.Output);
        WriteReceipt(attemptDirectory, index, "launchservices", runToken, result);
        return new(index, "launchservices", runToken, attemptDirectory, result);
    }

    public static TestRunResult FoldAttempts(
        IReadOnlyList<Attempt> attempts, TestRunResult final, string? successReason = null)
    {
        var output = new StringBuilder();
        foreach (var attempt in attempts)
        {
            output.Append("=== Catalyst launch attempt ").Append(attempt.Index + 1)
                .Append(" transport=").Append(attempt.Transport)
                .Append(" token=").Append(attempt.RunToken)
                .Append(" status=").Append(attempt.Result.Status)
                .Append(" evidence=").Append(attempt.Directory).AppendLine(" ===");
            output.Append(attempt.Result.Output.TrimEnd()).AppendLine();
        }
        return final with
        {
            Reason = successReason ?? final.Reason,
            Output = output.ToString(),
        };
    }

    static string CreateAttemptDirectory(
        string evidenceDirectory, int index, string transport, string runToken)
    {
        var path = Path.Combine(evidenceDirectory, $"{index:D2}-{transport}-{runToken}");
        Directory.CreateDirectory(path);
        return path;
    }

    static void WriteReceipt(string directory, int index, string transport,
        string runToken, TestRunResult result)
    {
        var receipt = new
        {
            schemaVersion = 1,
            attempt = index,
            transport,
            runToken,
            status = result.Status.ToString(),
            reason = result.Reason,
            managedEntryObserved = result.Output.Contains(
                CatalystLaunchContract.ManagedEntryMarker(runToken), StringComparison.Ordinal),
            correlatedSuccessObserved = result.Output.Contains(
                CatalystLaunchContract.ResultMarker(runToken, success: true), StringComparison.Ordinal),
            exitCode = result.ExitCode,
            timeToFirstOutputSeconds = result.TimeToFirstOutput?.TotalSeconds,
            consoleSha256 = Hash(result.Output),
        };
        WriteNew(Path.Combine(directory, "result.json"),
            JsonSerializer.Serialize(receipt, new JsonSerializerOptions { WriteIndented = true }) + "\n");
    }

    static void AppendLauncherOutput(
        StringBuilder captured, string source, string text, Action<string>? onLine)
    {
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            captured.Append('[').Append(source).Append("] ").AppendLine(line.TrimEnd('\r'));
            onLine?.Invoke(line.TrimEnd('\r'));
        }
    }

    static void TerminateExactAppProcess(string binaryPath, string runToken)
    {
        try
        {
            var psi = new ProcessStartInfo("/bin/ps")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add("-axo");
            psi.ArgumentList.Add("pid=,command=");
            using var ps = Process.Start(psi);
            if (ps is null) return;
            var output = ps.StandardOutput.ReadToEnd();
            ps.WaitForExit(5000);
            foreach (var line in output.Split('\n'))
            {
                var trimmed = line.TrimStart();
                var separator = trimmed.IndexOf(' ');
                if (separator <= 0 || !int.TryParse(trimmed.AsSpan(0, separator), out var pid))
                    continue;
                var command = trimmed[(separator + 1)..];
                if (!command.Contains(binaryPath, StringComparison.Ordinal) ||
                    !command.Contains(runToken, StringComparison.Ordinal))
                    continue;
                try { Process.GetProcessById(pid).Kill(entireProcessTree: true); } catch { }
            }
        }
        catch { /* cleanup is best effort; the launch timeout remains terminal */ }
    }

    static int? SafeExitCode(Process process)
    {
        try { return process.HasExited ? process.ExitCode : null; }
        catch { return null; }
    }

    static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    static void WriteNew(string path, string value)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.Write(value);
    }

    sealed class FileTailState(string path, string source)
    {
        string previous = string.Empty;
        string partial = string.Empty;

        public void Drain(Action<string, string> handle, bool includePartial)
        {
            string current;
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                current = reader.ReadToEnd();
            }
            catch (IOException)
            {
                return;
            }

            if (!current.StartsWith(previous, StringComparison.Ordinal))
                throw new InvalidOperationException($"LaunchServices replaced correlated log {path} while it was being read.");
            partial += current[previous.Length..];
            previous = current;

            while (true)
            {
                var newline = partial.IndexOf('\n');
                if (newline < 0) break;
                var line = partial[..newline].TrimEnd('\r');
                partial = partial[(newline + 1)..];
                handle(source, line);
            }
            if (includePartial && partial.Length > 0)
            {
                handle(source, partial.TrimEnd('\r'));
                partial = string.Empty;
            }
        }
    }
}
