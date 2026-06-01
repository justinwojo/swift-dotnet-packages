# Sim-Test Orchestration Follow-ups

Tracked items deferred from the 2026-05-18 reliability pass. Context: shipping
`apple-frameworks/Matter` (a ~92k-LoC ObjC binding) on `macos-26` Apple Silicon
runners surfaced two consecutive `RunCiSimTest` failures — once on `simctl list
devices -j timed out after 60s`, once on `Swift.Bindings.dll exited with code 1`
with no captured stderr. Both reviewers (Codex `019e3e1f-ef1c-7eb3-92ca-31225e4a1fef`,
Grok `019e3e23-71c4-7bb0-8897-32721a3f0601`) converged on root cause: the
`Task.Run` parallel boot+build pattern saturates the 3-vCPU runner when the
build side is a heavy binding generator pass.

## Landed in this pass

| Change | Where |
| --- | --- |
| Serialize boot+build in `RunCiSimTest` | `build/Build.Ci.cs:368-396` (parallel block removed, sequential path retained) |
| Add `"timed out after"` to `InfraFailurePatterns` | `build/Build.Ci.cs:301-318` |
| Bump `SimulatorFleet.CommandTimeout` 60s → 120s | `build/Helpers/SimulatorFleet.cs:25` |
| Capture `dotnet build` binlog in `BuildTestAppForRunCi` | `build/Build.Ci.cs` → `-bl:<DiagDir>/testapp-build-<library>.binlog` |
| `always() && pack==success` gate on package upload | `.github/workflows/release.yml:163,175` |
| GHA `timeout-minutes: 20 → 30` | `release.yml:140`, `ci.yml:148,240` |

## Deferred

### HIGH-ish

- **Per-verb simctl timeout split.** `SimulatorFleet.CommandTimeout` is one knob
  shared by `list devices`, `list runtimes`, `create`, `boot`, `bootstatus`,
  and the readiness `spawn launchctl print system` probe. Cold enumeration
  legitimately needs a longer envelope; boot-state polling should fail-fast
  and rely on the outer retry. Today's coarse 120s bump papers over both.
  - Where: `build/Helpers/SimulatorFleet.cs:25, 371, 562, 588-626`
  - Concrete shape: per-call `timeout:` overrides on `RunSimctl` already exist;
    plumb distinct constants (e.g. `ColdListTimeout = 180s`,
    `BootPollTimeout = 30s`) through the call sites instead of relying on the
    default.

- **Decouple sim validation from publish gating.** Even with the `always()`
  upload fix, the `publish` job in `release.yml:189` depends on the `build`
  job's success — a sim failure still prevents publish from running. The
  cleaner refactor is a split: `pack` job (always uploads artifacts) → `sim-validate`
  job (consumes artifacts) → `publish` job (depends on both, with a
  manual-approval override path).
  - Where: `.github/workflows/release.yml:36-191`

### MEDIUM

- **Effective CancellationToken / hung-simctl interruption.** The previous
  parallel block (now removed) documented at `Build.Ci.cs:425-430` that
  `cts.Cancel()` does not interrupt in-flight `simctl create/boot/bootstatus`.
  With serialization that risk is gone for `RunCiSimTest`, but other code paths
  (`PrepareFreshSimulator`, `EnsureReusable`) still call into the same fleet
  without cancellation. If we ever bring parallelism back, this needs to be
  solved properly — likely by exposing a `Process.Kill(entireProcessTree: true)`
  hook on `RunSimctlRaw`.

- **MSBuild parallelism caps on macos-26.** Zero occurrences of `-m:`,
  `/maxcpucount`, `MSBUILD_CPU_COUNT`, or `/p:BuildInParallel=false` anywhere
  in `build/` or the workflows. On a 3-vCPU runner, allowing MSBuild's default
  scheduler to spin up `N_proc` worker nodes plus a Roslyn server can amplify
  OOM and starvation. Worth experimenting with `dotnet build -m:1` for heavy
  Apple framework test apps.
  - Where: `build/Build.Library.cs:148-177` (`RunDotnet`),
    `build/Build.Ci.cs:711-731` (`BuildTestAppForRunCi`)

- **SDK-side generator stderr capture.** When `Swift.Bindings.dll` exits 1 with
  no stdout/stderr, the harness has nothing to log because the SDK's `Exec`
  task discarded it. This is an upstream `SwiftBindings.Sdk` change (not in
  this repo): improve `Sdk.targets` to redirect the generator's stdout/stderr
  to a file under `obj/.../swift-binding/generator.log` and reference the path
  in the failure diagnostic. Track in the SDK repo, not here.

- **Per-library "heavy" classification.** `InstallOverheadSeconds = 480` and
  `MaxInfraRetries = 1` are uniform across a 5-file Nuke test app and a 92k-LoC
  Matter binding. Heavy bindings deserve more retry slack and possibly a
  longer install/launch envelope. Could be a flag on `library.json` (e.g.
  `"heavy": true`) consumed by `RunCiSimTest`.

### LOW

- **`ci.yml` package artifact persistence.** Today `PackValidate` writes to
  `${{ runner.temp }}/ci-packages` and never uploads. For PR debugging of
  pack-only failures, uploading these as a CI artifact would mirror the
  release.yml behaviour and make pack regressions easier to triage.
  - Where: `.github/workflows/ci.yml:144`

- **CoreSimulator preflight/warmup step.** Codex round 1 suggested a dedicated
  `simctl list runtimes` + `simctl list devices -j` + pick/create + responsiveness
  probe step *before* heavy build work. Today fleet management is interleaved
  with the build phase via `EnsureReusable`. A preflight step would surface
  cold CoreSimulator instability earlier, separately from build failures.

- **Cleanup corrupted unicode in `ci.yml` apple-framework job header comment**
  (line 165). Cosmetic; no functional impact.

## Re-evaluating the parallel pattern later

The current sequential path costs ~one simulator boot (~60s) of wall-clock per
release. If a future runner SKU (more vCPUs, larger memory) makes the
contention go away, the parallel pattern can be re-introduced behind a flag
(e.g. `--parallel-prepare`). The shape of the previous code is preserved in
git history (`Build.Ci.cs` pre-2026-05-18) for reference.
