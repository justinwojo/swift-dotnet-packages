using System.Diagnostics;
using Nuke.Common;
using Serilog;
using SwiftBindings.Build.Helpers;
using SwiftBindings.Build.Models;

partial class Build
{
    /// <summary>
    /// Self-test for the <see cref="StdoutWatcher"/> state machine, its launch/run budget
    /// split, and the <see cref="IsInfraFailure"/> classifier. The budget arithmetic the CI
    /// orchestrator runs on is asserted next door, in <see cref="TestRunBudgets"/>.
    ///
    /// <list type="bullet">
    ///   <item><b>Failure-marker regression:</b> a line containing
    ///         <c>TEST FAILED: 3 failures</c> classifies as
    ///         <see cref="TestRunStatus.Failed"/>, NOT <see cref="TestRunStatus.Timeout"/>.</item>
    ///   <item><b>[SKIP] footgun-kill:</b> a line starting with
    ///         <c>[SKIP] SIGSEGV protocol existential not yet supported</c>
    ///         does NOT trip <see cref="TestRunStatus.Crashed"/>; the same line
    ///         without the <c>[SKIP]</c> prefix DOES.</item>
    ///   <item><b>End-to-end success:</b> a process logging
    ///         <c>TEST SUCCESS</c> resolves to <see cref="TestRunStatus.Success"/>.</item>
    ///   <item><b>End-to-end timeout:</b> a process logging nothing terminal
    ///         resolves to <see cref="TestRunStatus.Timeout"/> after the
    ///         configured deadline (no kill required).</item>
    ///   <item><b>Infra-failure classifier:</b>
    ///         <see cref="IsInfraFailure"/> tags <see cref="SimulatorBootTimeout"/>
    ///         and <see cref="SimctlCommandError"/>-like messages as retryable;
    ///         a plain <see cref="InvalidOperationException"/> is NOT retryable.</item>
    /// </list>
    ///
    /// <para>
    /// This is intentionally a Nuke target rather than xUnit so it runs from
    /// the same harness developers already use, no extra dependencies required.
    /// </para>
    /// </summary>
    Target ValidateWatcherSelfTest => _ => _
        .Description("Self-test for StdoutWatcher state machine + IsInfraFailure classifier")
        .Executes(async () =>
        {
            var failures = 0;

            void Check(string name, bool ok, string? detail = null)
            {
                if (ok) Log.Information("[ok]   {Name}", name);
                else { Log.Error("[FAIL] {Name}{Detail}", name, detail is null ? "" : ": " + detail); failures++; }
            }

            // ── 1. Failure-marker regression test ────────────────────────
            // The current sim test apps emit `TEST FAILED: {N} failures`,
            // not bare `TEST FAILED`. The state machine MUST classify this
            // as Failed, not Timeout — known footgun that the bash version
            // gets right via grep substring match.
            var failedStatus = StdoutWatcher.Classify("TEST FAILED: 3 failures").Status;
            Check("[failure-marker] 'TEST FAILED: 3 failures' → Failed",
                failedStatus == TestRunStatus.Failed,
                $"actual={failedStatus}");

            // Bare 'TEST FAILED' (corner case)
            var bareFailed = StdoutWatcher.Classify("TEST FAILED").Status;
            Check("[failure-marker] bare 'TEST FAILED' → Failed",
                bareFailed == TestRunStatus.Failed,
                $"actual={bareFailed}");

            // ── 2. [SKIP] footgun-kill ───────────────────────────────────
            var skipLine = "[SKIP] SIGSEGV protocol existential not yet supported";
            var skipStatus = StdoutWatcher.Classify(skipLine).Status;
            Check("[skip-footgun] '[SKIP] SIGSEGV ...' → Running (NOT Crashed)",
                skipStatus == TestRunStatus.Running,
                $"actual={skipStatus}");

            // Without [SKIP] prefix, the same content MUST trip crash detection.
            var noSkipLine = "SIGSEGV protocol existential not yet supported";
            var noSkipStatus = StdoutWatcher.Classify(noSkipLine).Status;
            Check("[skip-footgun] 'SIGSEGV ...' (no prefix) → Crashed",
                noSkipStatus == TestRunStatus.Crashed,
                $"actual={noSkipStatus}");

            // Make sure each crash signal token is detected.
            foreach (var sig in StdoutWatcher.CrashSignals)
            {
                var s = StdoutWatcher.Classify($"crash: {sig} happened").Status;
                Check($"[crash-signal] '{sig}' → Crashed", s == TestRunStatus.Crashed, $"actual={s}");
            }

            // ── 3. Success classification ────────────────────────────────
            var successStatus = StdoutWatcher.Classify("TEST SUCCESS").Status;
            Check("[success] 'TEST SUCCESS' → Success",
                successStatus == TestRunStatus.Success, $"actual={successStatus}");

            // Trailing whitespace tolerated
            var successCrlf = StdoutWatcher.Classify("TEST SUCCESS\r").Status;
            Check("[success] 'TEST SUCCESS\\r' → Success",
                successCrlf == TestRunStatus.Success, $"actual={successCrlf}");

            // ── 4. End-to-end RunAsync: process emits TEST SUCCESS ───────
            // Echo the success marker via /bin/echo and verify the watcher
            // catches it before deadline.
            var psi = new ProcessStartInfo
            {
                FileName = "/bin/sh",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add("echo 'preamble line'; echo 'TEST SUCCESS'; sleep 30");
            var successResult = await StdoutWatcher.RunAsync(psi, TimeSpan.FromSeconds(10));
            Check("[end-to-end] echo 'TEST SUCCESS' → Success",
                successResult.Status == TestRunStatus.Success,
                $"actual={successResult.Status} reason={successResult.Reason}");

            // ── 5. End-to-end RunAsync: TEST FAILED: N failures ──────────
            var psiFail = new ProcessStartInfo
            {
                FileName = "/bin/sh",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            psiFail.ArgumentList.Add("-c");
            psiFail.ArgumentList.Add("echo 'phase 1 ok'; echo 'TEST FAILED: 3 failures'; sleep 30");
            var failResult = await StdoutWatcher.RunAsync(psiFail, TimeSpan.FromSeconds(10));
            Check("[end-to-end] echo 'TEST FAILED: 3 failures' → Failed (NOT Timeout)",
                failResult.Status == TestRunStatus.Failed,
                $"actual={failResult.Status} reason={failResult.Reason}");

            // ── 6. End-to-end RunAsync: [SKIP] does NOT trip crash ───────
            var psiSkip = new ProcessStartInfo
            {
                FileName = "/bin/sh",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            psiSkip.ArgumentList.Add("-c");
            psiSkip.ArgumentList.Add(
                "echo 'starting'; echo '[SKIP] SIGSEGV protocol existential not yet supported'; echo 'TEST SUCCESS'; sleep 30");
            var skipResult = await StdoutWatcher.RunAsync(psiSkip, TimeSpan.FromSeconds(10));
            Check("[end-to-end] [SKIP] SIGSEGV ... + TEST SUCCESS → Success (no false-positive crash)",
                skipResult.Status == TestRunStatus.Success,
                $"actual={skipResult.Status} reason={skipResult.Reason}");

            // Same scenario without [SKIP] prefix → must crash before SUCCESS
            var psiCrash = new ProcessStartInfo
            {
                FileName = "/bin/sh",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            psiCrash.ArgumentList.Add("-c");
            psiCrash.ArgumentList.Add(
                "echo 'starting'; echo 'SIGSEGV happened'; echo 'TEST SUCCESS'; sleep 30");
            var crashResult = await StdoutWatcher.RunAsync(psiCrash, TimeSpan.FromSeconds(10));
            Check("[end-to-end] SIGSEGV (no [SKIP]) → Crashed",
                crashResult.Status == TestRunStatus.Crashed,
                $"actual={crashResult.Status} reason={crashResult.Reason}");

            // ── 7. End-to-end RunAsync: timeout (no marker) ──────────────
            var psiTimeout = new ProcessStartInfo
            {
                FileName = "/bin/sh",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            psiTimeout.ArgumentList.Add("-c");
            psiTimeout.ArgumentList.Add("echo 'phase 1'; sleep 30");
            var timeoutResult = await StdoutWatcher.RunAsync(psiTimeout, TimeSpan.FromSeconds(2));
            Check("[end-to-end] no marker → Timeout after deadline",
                timeoutResult.Status == TestRunStatus.Timeout,
                $"actual={timeoutResult.Status} reason={timeoutResult.Reason}");

            // ── 7b. Launch budget vs run budget ──────────────────────────
            // The case the split exists for: an app whose first line arrives
            // AFTER the run budget would have expired, but well inside the
            // launch budget, must still be given its run budget from that
            // first line. Under a single budget this was a timeout.
            var psiSlowStart = new ProcessStartInfo
            {
                FileName = "/bin/sh",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            psiSlowStart.ArgumentList.Add("-c");
            psiSlowStart.ArgumentList.Add("sleep 5; echo 'app is up'; sleep 1; echo 'TEST SUCCESS'; sleep 30");
            var slowStartSw = Stopwatch.StartNew();
            var slowStart = await StdoutWatcher.RunAsync(
                psiSlowStart, timeout: TimeSpan.FromSeconds(4), launchTimeout: TimeSpan.FromSeconds(25));
            slowStartSw.Stop();
            Check("[budgets] first line after the run budget, inside the launch budget → Success",
                slowStart.Status == TestRunStatus.Success,
                $"actual={slowStart.Status} reason={slowStart.Reason}");
            Check("[budgets] time-to-first-output is recorded",
                slowStart.TimeToFirstOutput is not null && slowStart.TimeToFirstOutput.Value.TotalSeconds >= 4.5,
                $"actual={slowStart.TimeToFirstOutput?.TotalSeconds.ToString("F1") ?? "(null)"}");

            // Silence past the launch budget is a launch timeout, and it is
            // called at the launch budget rather than at the (much larger) run
            // budget.
            var psiSilent = new ProcessStartInfo
            {
                FileName = "/bin/sh",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            psiSilent.ArgumentList.Add("-c");
            psiSilent.ArgumentList.Add("sleep 60");
            var silentSw = Stopwatch.StartNew();
            var silent = await StdoutWatcher.RunAsync(
                psiSilent, timeout: TimeSpan.FromSeconds(45), launchTimeout: TimeSpan.FromSeconds(2));
            silentSw.Stop();
            Check("[budgets] no output at all → Timeout named as the launch budget",
                silent.Status == TestRunStatus.Timeout &&
                (silent.Reason ?? "").Contains("launch budget", StringComparison.Ordinal),
                $"actual={silent.Status} reason={silent.Reason}");
            Check("[budgets] the launch budget is what expired (not the run budget)",
                silentSw.Elapsed < TimeSpan.FromSeconds(20),
                $"elapsed={silentSw.Elapsed.TotalSeconds:F1}s");
            Check("[budgets] a silent launch reports no first output",
                silent.TimeToFirstOutput is null,
                $"actual={silent.TimeToFirstOutput?.TotalSeconds.ToString("F1") ?? "(null)"}");

            // Output but no marker is a RUN-budget timeout, named as such.
            var psiQuietRun = new ProcessStartInfo
            {
                FileName = "/bin/sh",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            psiQuietRun.ArgumentList.Add("-c");
            psiQuietRun.ArgumentList.Add("echo 'app is up'; sleep 60");
            var quietRun = await StdoutWatcher.RunAsync(
                psiQuietRun, timeout: TimeSpan.FromSeconds(2), launchTimeout: TimeSpan.FromSeconds(20));
            Check("[budgets] output but no marker → Timeout named as the run budget",
                quietRun.Status == TestRunStatus.Timeout &&
                (quietRun.Reason ?? "").Contains("run budget", StringComparison.Ordinal),
                $"actual={quietRun.Status} reason={quietRun.Reason}");

            // The watched process is the launcher, not the app, so the launcher's own
            // chatter must NOT end the launch phase — otherwise a startup that hangs
            // after `Launched application with pid …` silently spends the run budget,
            // which is the larger of the two, before anyone notices the app is mute.
            Check("[budgets] the launcher's start confirmation is launcher-owned",
                LaunchDiagnostics.IsLauncherOwnedLine("Launched application with pid 1234"));
            Check("[budgets] a launcher abort line is launcher-owned",
                LaunchDiagnostics.IsLauncherOwnedLine("An error was encountered processing the command"));
            Check("[budgets] a blank line is not evidence the app spoke",
                LaunchDiagnostics.IsLauncherOwnedLine("   "));
            Check("[budgets] an app banner line is the app's",
                !LaunchDiagnostics.IsLauncherOwnedLine("[INFO] suite starting"));
            Check("[budgets] an unrecognised line is treated as the app's",
                !LaunchDiagnostics.IsLauncherOwnedLine("Kingfisher: downloading fixture"));

            var psiLauncherChatter = new ProcessStartInfo
            {
                FileName = "/bin/sh",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            psiLauncherChatter.ArgumentList.Add("-c");
            psiLauncherChatter.ArgumentList.Add(
                "echo 'Launched application with pid 1234'; echo 'Waiting for the application to terminate'; sleep 60");
            var launcherChatterSw = Stopwatch.StartNew();
            var launcherChatter = await StdoutWatcher.RunAsync(
                psiLauncherChatter, timeout: TimeSpan.FromSeconds(45), launchTimeout: TimeSpan.FromSeconds(2));
            launcherChatterSw.Stop();
            Check("[budgets] launcher chatter then app silence → launch-budget timeout",
                launcherChatter.Status == TestRunStatus.Timeout &&
                (launcherChatter.Reason ?? "").Contains("launch budget", StringComparison.Ordinal),
                $"actual={launcherChatter.Status} reason={launcherChatter.Reason}");
            Check("[budgets] launcher chatter does not start the run budget",
                launcherChatterSw.Elapsed < TimeSpan.FromSeconds(20),
                $"elapsed={launcherChatterSw.Elapsed.TotalSeconds:F1}s");
            Check("[budgets] launcher chatter does not count as the app's first output",
                launcherChatter.TimeToFirstOutput is null && launcherChatter.IsLaunchPhaseTimeout,
                $"first-output={launcherChatter.TimeToFirstOutput?.TotalSeconds.ToString("F1") ?? "(null)"} " +
                $"launch-phase={launcherChatter.IsLaunchPhaseTimeout}");

            // A timeout AFTER the app has spoken is a product result: it must not be
            // reported as a launch-phase timeout, which points the reader at install, dyld
            // and startup rather than at a suite that stalled part-way.
            Check("[budgets] a run-budget timeout is NOT a launch-phase timeout",
                !quietRun.IsLaunchPhaseTimeout,
                $"first-output={quietRun.TimeToFirstOutput?.TotalSeconds.ToString("F1") ?? "(null)"}");

            // Omitting the launch budget keeps the single-budget behaviour.
            var psiLegacy = new ProcessStartInfo
            {
                FileName = "/bin/sh",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            psiLegacy.ArgumentList.Add("-c");
            psiLegacy.ArgumentList.Add("sleep 60");
            var legacy = await StdoutWatcher.RunAsync(psiLegacy, TimeSpan.FromSeconds(2));
            Check("[budgets] no launch budget given → the single budget still applies",
                legacy.Status == TestRunStatus.Timeout,
                $"actual={legacy.Status} reason={legacy.Reason}");

            // ── 7c. Launcher abort vs app exit ───────────────────────────
            // A launcher that quits with its own abort wording and no app
            // output produced no verdict; an app that ran and exited without a
            // marker DID, and must stay fail-loud even at exit code 0.
            Check("[launch-abort] generic simctl abort wording alone → ambiguous, no retry",
                !LaunchDiagnostics.LauncherNeverStartedApp(
                    "An error was encountered processing the command (domain=NSPOSIXErrorDomain, code=22)"));
            Check("[launch-abort] abort wording + app output → NOT a launcher abort",
                !LaunchDiagnostics.LauncherNeverStartedApp(
                    "[PASS] something ran\nAn error was encountered processing the command"));
            Check("[launch-abort] abort wording + launcher confirmed start → NOT a launcher abort",
                !LaunchDiagnostics.LauncherNeverStartedApp(
                    "Launched application with pid 1234\ncom.apple.dt.CoreDeviceError 10002"));
            Check("[launch-abort] unrelated output → NOT a launcher abort",
                !LaunchDiagnostics.LauncherNeverStartedApp("dyld: library not loaded"));
            Check("[launch-abort] empty output → NOT a launcher abort",
                !LaunchDiagnostics.LauncherNeverStartedApp(""));
            foreach (var pattern in LaunchDiagnostics.LauncherAbortPatterns)
            {
                Check($"[launch-abort] diagnostic '{pattern}' is launcher-owned",
                    LaunchDiagnostics.IsLauncherOwnedLine($"xcrun: {pattern} (code 22)"));
                Check($"[launch-abort] diagnostic '{pattern}' alone only retries explicit boot failure",
                    LaunchDiagnostics.LauncherNeverStartedApp($"xcrun: {pattern} (code 22)")
                        == (pattern == "Unable to boot device"));
            }

            // Mixed transcripts: product evidence outranks the launcher's abort wording.
            // A loader failure or a crash is the app image being entered and dying — the
            // launcher prints "failed to launch" for exactly that too, and re-launching it
            // would retry a packaging or binding defect into a pass.
            Check("[launch-abort] abort wording + dyld load failure → product failure, NOT a launcher abort",
                !LaunchDiagnostics.LauncherNeverStartedApp(
                    "An error was encountered processing the command (domain=FBSOpenApplicationServiceErrorDomain, code=1)\n" +
                    "dyld: Library not loaded: @rpath/MissingSwiftBindings.framework/MissingSwiftBindings"));
            Check("[launch-abort] abort wording + missing symbol → product failure, NOT a launcher abort",
                !LaunchDiagnostics.LauncherNeverStartedApp(
                    "dyld[4242]: Symbol not found: _$s8SBWrapper3fooyyF\nThe application failed to launch."));
            Check("[launch-abort] abort wording + crash termination → product failure, NOT a launcher abort",
                !LaunchDiagnostics.LauncherNeverStartedApp(
                    "Termination Reason: SIGNAL 6 Abort trap: 6\nThe request to open \"com.x.y\" failed."));
            Check("[launch-abort] abort wording after an Apple-framework test banner → NOT a launcher abort",
                !LaunchDiagnostics.LauncherNeverStartedApp(
                    "[STOREKIT2-TEST] starting suite\nThe application failed to launch."));
            Check("[launch-abort] a banner of the [NAME-TEST] shape counts as app output",
                LaunchDiagnostics.ShowsAppOutput("[MUSICKIT-TEST] hello") && !LaunchDiagnostics.ShowsAppOutput("[not-a-banner] hello"));
            Check("[launch-abort] transport-only abort (no app evidence) → launcher abort",
                LaunchDiagnostics.LauncherNeverStartedApp(
                    "xcrun: error: com.apple.dt.CoreDeviceError 10002 — tunnel connection interrupted"));
            foreach (var diagnostic in new[]
            {
                "Unable to lookup in current state: Shutdown",
                "Unable to boot device",
                "CoreSimulatorService connection interrupted",
                "CoreSimulatorService connection invalidated",
            })
            {
                Check($"[launch-abort] explicit infrastructure diagnostic '{diagnostic}' allows retry",
                    LaunchDiagnostics.LauncherNeverStartedApp(diagnostic));
            }
            Check("[launch-abort] transport wording without launcher context is ambiguous",
                !LaunchDiagnostics.LauncherNeverStartedApp("tunnel connection interrupted"));
            foreach (var evidence in LaunchDiagnostics.ProductFailurePatterns)
            {
                Check($"[launch-abort] product evidence '{evidence}' outranks the abort wording",
                    !LaunchDiagnostics.LauncherNeverStartedApp(
                        $"Unable to lookup in current state: Shutdown\n{evidence} (details)"));
            }
            Check("[launch-abort] a loader line is the app's line, not the launcher's (launch budget ends on it)",
                !LaunchDiagnostics.IsLauncherOwnedLine("dyld: Library not loaded: @rpath/X.framework/X"));
            Check("[launch-abort] an Apple-framework banner line is the app's line",
                !LaunchDiagnostics.IsLauncherOwnedLine("[WEATHERKIT-TEST] booting"));

            // Every re-attempted launch keeps its transcript in the final result.
            var folded = LaunchDiagnostics.WithPriorAttempts(
                new TestRunResult(TestRunStatus.Exited, "final", "[INFO] final run", Array.Empty<string>()),
                new[] { "first abort text", "second abort text" });
            Check("[launch-abort] prior attempt transcripts are preserved in the final output",
                folded.Output.Contains("first abort text", StringComparison.Ordinal)
                && folded.Output.Contains("second abort text", StringComparison.Ordinal)
                && folded.Output.Contains("[INFO] final run", StringComparison.Ordinal)
                && folded.Output.Contains("launch attempt 3 (final)", StringComparison.Ordinal),
                folded.Output);
            var untouched = new TestRunResult(TestRunStatus.Exited, "final", "[INFO] only run", Array.Empty<string>());
            Check("[launch-abort] no prior attempts → result untouched",
                ReferenceEquals(LaunchDiagnostics.WithPriorAttempts(untouched, Array.Empty<string>()), untouched));

            var psiAbort = new ProcessStartInfo
            {
                FileName = "/bin/sh",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            psiAbort.ArgumentList.Add("-c");
            psiAbort.ArgumentList.Add("echo 'An error was encountered processing the command' >&2; echo 'Unable to lookup in current state: Shutdown' >&2; exit 3");
            var abortResult = await StdoutWatcher.RunAsync(psiAbort, TimeSpan.FromSeconds(10));
            Check("[launch-abort] end-to-end launcher abort → LauncherAborted (NOT Exited)",
                abortResult.Status == TestRunStatus.LauncherAborted,
                $"actual={abortResult.Status} reason={abortResult.Reason}");
            Check("[launch-abort] the launcher's exit code is recorded",
                abortResult.ExitCode == 3, $"actual={abortResult.ExitCode?.ToString() ?? "(null)"}");

            Check("[launch-abort] infrastructure failure has no app first-output timestamp",
                abortResult.TimeToFirstOutput is null);
            Check("[launch-abort] observed ordinary app output blocks a transport retry",
                !LaunchDiagnostics.LauncherNeverStartedApp(
                    "com.apple.dt.CoreDeviceError: tunnel connection interrupted", appOutputObserved: true));
            Check("[launch-abort] ordinary app output in transcript blocks a transport retry",
                !LaunchDiagnostics.LauncherNeverStartedApp(
                    "Kingfisher: downloading fixture\ncom.apple.dt.CoreDeviceError: tunnel connection interrupted"));

            foreach (var diagnostic in new[]
            {
                "The application failed to launch",
                "com.apple.dt.CoreDeviceError 10002",
                "FBSOpenApplicationServiceErrorDomain: The request to open was denied",
                "An error was encountered processing the command",
            })
            {
                var psiAmbiguous = new ProcessStartInfo("/bin/sh") { UseShellExecute = false };
                psiAmbiguous.ArgumentList.Add("-c");
                psiAmbiguous.ArgumentList.Add($"echo '{diagnostic}' >&2; exit 3");
                var ambiguous = await StdoutWatcher.RunAsync(psiAmbiguous, TimeSpan.FromSeconds(10));
                Check($"[launch-abort] ambiguous '{diagnostic}' → Exited, no retry",
                    ambiguous.Status == TestRunStatus.Exited && ambiguous.TimeToFirstOutput is null,
                    $"actual={ambiguous.Status} reason={ambiguous.Reason}");
            }

            foreach (var diagnostic in new[]
            {
                "The application failed to launch",
                "com.apple.dt.CoreDeviceError: tunnel connection interrupted",
            })
            {
                var psiOrdinary = new ProcessStartInfo("/bin/sh") { UseShellExecute = false };
                psiOrdinary.ArgumentList.Add("-c");
                psiOrdinary.ArgumentList.Add($"echo 'Kingfisher: downloading fixture'; echo '{diagnostic}'; exit 3");
                var ordinary = await StdoutWatcher.RunAsync(psiOrdinary, TimeSpan.FromSeconds(10));
                Check($"[launch-abort] ordinary app output + '{diagnostic}' → Exited, no retry",
                    ordinary.Status == TestRunStatus.Exited && ordinary.TimeToFirstOutput is not null,
                    $"actual={ordinary.Status} reason={ordinary.Reason}");
            }

            var psiCleanExit = new ProcessStartInfo
            {
                FileName = "/bin/sh",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            psiCleanExit.ArgumentList.Add("-c");
            psiCleanExit.ArgumentList.Add("echo '[INFO] suite starting'; exit 0");
            var cleanExit = await StdoutWatcher.RunAsync(psiCleanExit, TimeSpan.FromSeconds(10));
            Check("[launch-abort] app exits 0 with no marker → still Exited (fail-loud preserved)",
                cleanExit.Status == TestRunStatus.Exited,
                $"actual={cleanExit.Status} reason={cleanExit.Reason}");
            Check("[launch-abort] the app's exit code is recorded",
                cleanExit.ExitCode == 0, $"actual={cleanExit.ExitCode?.ToString() ?? "(null)"}");

            // The retry budget is bounded and the settle grows — a launcher
            // abort may be re-attempted, but not indefinitely.
            Check("[launch-abort] budget is not exhausted before the last attempt",
                !LaunchDiagnostics.LauncherAbortBudgetExhausted(LaunchDiagnostics.MaxLauncherAbortAttempts - 1));
            Check("[launch-abort] budget is exhausted at the attempt cap",
                LaunchDiagnostics.LauncherAbortBudgetExhausted(LaunchDiagnostics.MaxLauncherAbortAttempts));
            Check("[launch-abort] settle delay grows with the abort count",
                LaunchDiagnostics.SettleDelayAfterAbort(2) > LaunchDiagnostics.SettleDelayAfterAbort(1));

            // A launcher abort must reach RunCiSimTest's infra-retry arm — that
            // is what makes it re-prepared rather than recorded as a verdict.
            Check("[launch-abort] the abort message classifies as retryable infrastructure",
                IsInfraFailure(new InvalidOperationException(
                    "launcher aborted before the app started: launcher exited with code 3 before the app produced output")));

            // ── 8. IsInfraFailure classifier ─────────────────────────────
            Check("[is-infra] SimulatorBootTimeout → true",
                IsInfraFailure(new SimulatorBootTimeout("did not boot")));
            Check("[is-infra] SimulatorReadinessTimeout → true",
                IsInfraFailure(new SimulatorReadinessTimeout("not responsive")));
            Check("[is-infra] 'failed to boot' message → true",
                IsInfraFailure(new InvalidOperationException("simctl says: failed to boot")));
            Check("[is-infra] 'CoreSimulatorService connection interrupted' → true",
                IsInfraFailure(new InvalidOperationException("CoreSimulatorService connection interrupted")));
            Check("[is-infra] generic build error → false (NOT retryable)",
                !IsInfraFailure(new InvalidOperationException("dotnet build failed (exit 1)")));

            if (failures > 0)
                throw new InvalidOperationException($"{failures} watcher self-test assertions failed");
            Log.Information("All watcher self-tests passed");
        });
}
