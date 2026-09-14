# B08 / D01 ActivityKit capability validation receipt

Date: 2026-09-14
State: implementation, non-device validation, and the Grok-only review cycle are
complete in a local repository commit; the capability-qualified physical-device gate
remains pending an explicit APNs provisioning profile.

## Provenance

- Package-repository base: `15bff6126cc89fbb067505e133ab3752bf98cac2`.
- Reviewed `swift-bindings` input: `74d44106f9c302741b92b4a6f5c6a7e7cb386825`.
- Host: macOS (`Darwin`).
- Validation used an isolated package-repository worktree. The shared checkout at
  `/Users/wojo/Dev/swift-dotnet-packages` was not modified.

Fresh `0.20.0` packages were built from the reviewed upstream input. Each nuspec
records repository commit `74d44106f9c302741b92b4a6f5c6a7e7cb386825`.

| Package | SHA-256 |
| --- | --- |
| `SwiftBindings.Runtime.0.20.0.nupkg` | `423f59b42bf1e1f69a89c3a2d881ee7686cd566f9acaf58e91a006ab60b15008` |
| `SwiftBindings.Sdk.0.20.0.nupkg` | `e23b69bc7a12dc2a1a19e64f1e7f7b7a614c99cc2ae4826bd0b271f22541485c` |
| `SwiftBindings.Apple.26.2.10.nupkg` | `0113e8e26c8504af2ae6b37a057475dc74fb4ca9fac08e8dfebacdffb94bafd5` |

The ActivityKit project SDK version was changed to `0.20.0` only while running these
exact-package gates. That validation-only edit is not part of the final source change.

## Capability contract

The simulator app now uses a non-push request for observer register/replace/release.
The physical-device lane selects a dedicated bundle,
`com.swiftbindings.activitykit.push.tests`, and requires an explicitly named profile.
Before building, the harness rejects a missing, wildcard, expired, wrong-bundle,
wrong-team, or non-APNs profile. Immediately before installation it verifies the
final app signature, the signed `aps-environment`, the embedded profile, and target
device membership, then retains the decoded evidence and hashes under
`artifacts/activitykit-capability/`. The launched app independently reads its live
signed `aps-environment` before the request, so a manually re-signed launch fails with
the capability and original `SessionCore.PermissionsError Code=3` signature named.

The installed `Wildcard Dev` profile was used as the negative control. The device
build failed before compilation with:

```text
ActivityKit push-token capability preflight failed: provisioning profile is missing required entitlement 'aps-environment'. Profile: Wildcard Dev
```

Fail-closed log:
`.nuke/temp/build.2026-09-14_13-59-00.log`, SHA-256
`10689c37ea1428bcd863af1f11968fa67d2bfc31f34cb8a4dcb73f57dd3e913b`.
The Nuke temp-log retention later rotated this file; the exact diagnostic and digest
were recorded before rotation and remain the durable negative-control receipt.

The pure capability-policy self-test passed after covering the exact runtime-missing
message, missing signed/profile entitlements, wildcard App IDs, mismatched and
unsupported environments, incorrect team prefixes, expiry, device membership, and
valid explicit development evidence:

```text
dotnet build build/_build.csproj -c Debug --no-restore
dotnet nuke TestActivityKitCapability --no-logo
```

The dedicated app source also compiled successfully through the simulator toolchain,
which exercises the new project, linked UIKit host, request/observer code, and package
exclusion without claiming that push-token behavior can run there:

```text
dotnet build apple-frameworks/ActivityKit/tests-device/SwiftBindings.Apple.ActivityKit.PushToken.Tests.csproj \
  -c Debug -r iossimulator-arm64 -p:CodesignKey=-
```

Result: 0 errors. Existing generated-binding, resource-deduplication, platform-analysis,
and trim warnings remained warnings.

## Exact-package simulator result

Commands:

```text
dotnet nuke BuildTestApp --library ActivityKit --no-logo
dotnet nuke ValidateSim --library ActivityKit --timeout 120 --no-logo
```

Result: 27 passed, 0 failed, 0 skipped. The exact renamed assertion was
`LiveActivity non-push observer register/replace/release`; no simulator push-token
request was claimed as supported.

| Evidence | SHA-256 |
| --- | --- |
| `.nuke/temp/build.2026-09-14_14-09-17.log` (build; later rotated) | `6b89950ec29028f98e1ca2ac77b9347c2e14500f34c913499e2eefd695fc1e69` |
| `.nuke/temp/build.2026-09-14_14-11-22.log` (run) | `12ea3cf980f8c1a42cb8ac7f049b94312f160b447d97881398fc6c4dbe931478` |

## Deferred external physical-device qualification

Pending. The connected iPhone is paired, in Developer Mode, and resolves from
CoreDevice identifier `559479FD-3C60-51E4-8B2C-872D8CBA8B54` to provisioning UDID
`00008110-00114C122E46801E`. The only installed profile is wildcard and has no
`aps-environment`; Xcode currently has no Apple developer account configured.

Closure requires an explicit profile for the dedicated bundle that grants
`aps-environment` and includes that device. The final NativeAOT run must pass the
separate `push-token request preflight` and
`push-token observer register/replace/release` verdicts. Signed entitlement/profile
hashes and the exact run result must be appended when external qualification resumes;
this batch's source review does not convert the deferred gate into a pass.

Resume from this repository with freshly packed artifacts from the qualifying
`swift-bindings` commit. Put their exact Runtime, SDK, and Apple packages in
`local-packages/`, verify their SHA-256 values and nuspec repository commit, and apply
this validation-only SDK pin (do not include it in the package-repository commit):

```diff
-<Project Sdk="SwiftBindings.Sdk/0.19.4">
+<Project Sdk="SwiftBindings.Sdk/0.20.0">
```

Evict or isolate any pre-existing `SwiftBindings.Sdk/0.20.0` and
`SwiftBindings.Runtime/0.20.0` NuGet cache entries so restore must consume those exact
files. Then run:

```text
dotnet nuke ValidateDeviceLane --library ActivityKit \
  --activitykit-provisioning-profile '<EXPLICIT_APNS_PROFILE_NAME_UUID_OR_PATH>' \
  --device-udid 559479FD-3C60-51E4-8B2C-872D8CBA8B54 --no-logo
```

Do not accept an installed/launchable app as substitute evidence. Closure requires
the preinstall verifier's `ActivityKit push-token capability verified` line, retained
signed/profile hashes, `push-token request preflight`,
`push-token observer register/replace/release`, and `TEST SUCCESS` from the same run.

## Grok round-1 repairs and validation

The integrated two-repository Grok-only review retained the package live-process probe
as Medium and four package-facing evidence/maintenance issues as Low. All were
accepted. The dedicated app now probes its own signed `aps-environment` before request;
the self-test covers runtime error naming, expiry, device membership, and valid profile
preflight; all three original simulator observer requests now pass
`usePushToken: false` explicitly; and this receipt
now distinguishes build-time from preinstall checks and gives the exact temporary SDK
pin needed for resume.

After those repairs, the Nuke build compiled, `TestActivityKitCapability` passed, and
the dedicated app compiled through the simulator toolchain with the live SecTask/CF
probe included (0 errors). The repository-pinned SDK simulator app was rebuilt and
rerun after the focused follow-up pinned the original observer request with explicit
`usePushToken: false`: 27 passed, 0 failed, 0 skipped; log
`.nuke/temp/build.2026-09-14_15-24-18.log`, SHA-256
`29b1315ea5ca16f17cfba8bfb545c7f7c178c35bf43cf3e8aa1012f1b042edcd`.
This affected-source rerun supplements rather than replaces the exact-0.20.0 package
run above. Full review identity and every retained, merged, demoted, and dropped
candidate are preserved in the upstream
`b08-grok-review.md`. Because round 1 accepted High findings in the first-party repo,
the coordinator resumed the same Grok session for a focused follow-up. It found no
remaining Critical, High, or Medium issue. Its one Low caught the three observer calls
that the initial explicit-non-push edit missed; those calls are now pinned and the
final 27/27 run above validates the package site. Per the Medium/Low stopping rule, no
further review ran.
