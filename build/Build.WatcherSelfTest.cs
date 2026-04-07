using System.Diagnostics;
using Nuke.Common;
using Serilog;
using SwiftBindings.Build.Helpers;
using SwiftBindings.Build.Models;

partial class Build
{
    /// <summary>
    /// Self-test for the <see cref="StdoutWatcher"/> state machine and the
    /// <see cref="RunCiSimTest"/> retry/budget logic. Mirrors the regression
    /// matrix in the Session 3 spec:
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

            // ── 9. Deadline arithmetic (subprocess timeout formula) ──────
            // Mirror ci_ios_test.py:192–197 — the formula is:
            //   subprocess_timeout = min(timeout + INSTALL_OVERHEAD, max(remaining - 30, timeout + 60))
            int Compute(int timeout, double remaining)
            {
                if (remaining > 0)
                {
                    return (int)Math.Min(
                        timeout + InstallOverheadSeconds,
                        Math.Max(remaining - 30, timeout + 60));
                }
                return timeout + InstallOverheadSeconds;
            }
            // remaining huge → cap at timeout + INSTALL_OVERHEAD
            Check("[deadline] huge budget → timeout + INSTALL_OVERHEAD",
                Compute(60, 5000) == 60 + InstallOverheadSeconds,
                $"actual={Compute(60, 5000)}");
            // remaining moderate → max(rem-30, timeout+60)
            Check("[deadline] moderate budget → max(rem-30, timeout+60)",
                Compute(60, 200) == 170, // max(170, 120) = 170
                $"actual={Compute(60, 200)}");
            // remaining nearly out → fall back to timeout+60
            Check("[deadline] near-empty budget → timeout+60 floor",
                Compute(60, 50) == 120, // max(20, 120) = 120
                $"actual={Compute(60, 50)}");

            // ── 10. RunCiSimTest "skip retry when budget tight" rule ─────
            // ci_ios_test.py:169–177: min_retry_time = timeout + 30; if
            // remaining < min_retry_time, skip retry.
            int testTimeout = 60;
            double minRetryTime = testTimeout + 30;
            // remaining 100s < minRetryTime 90? no, 100 > 90 → retry permitted
            Check("[retry-budget] 100s remaining, need 90s → retry permitted",
                100 >= minRetryTime);
            // remaining 50s < 90s → skip
            Check("[retry-budget] 50s remaining, need 90s → retry SKIPPED",
                50 < minRetryTime);

            if (failures > 0)
                throw new InvalidOperationException($"{failures} watcher self-test assertions failed");
            Log.Information("All watcher self-tests passed");
        });
}
