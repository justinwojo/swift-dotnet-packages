# B05 validation receipt

Date: 2026-09-14
Package-repository base: `9061316e33084709b2fa6ab5631b44ffa2f6df72`
Reviewed swift-bindings input: `af1f1059a6308d2be3f7c5cc48ff9307c825a487`
Host: macOS (`Darwin`)

## Exact upstream gates

All commands below ran from a detached checkout at the reviewed swift-bindings
commit. The first cold simulator attempts stopped during AOT compilation when the
host volume reached `ENOSPC`; no test ran and no source failure was observed. After
only reproducible build outputs from completed clean worktrees were cleared, the
same gates completed green.

| Command | Runtime | Result |
| --- | --- | --- |
| `dotnet nuke binding-tests --class-filter InOutClosureParamTests --skip-regen` | iOS Simulator / Mono JIT | 18 pass, 0 fail, 0 skip, 0 crash (observed live; full log expired under the harness's five-log retention before the review packet was frozen) |
| `dotnet nuke binding-tests --device --class-filter InOutClosureParamTests` | physical iPhone / NativeAOT | 18 pass, 0 fail, 0 skip, 0 crash; live flavor `IsMonoRuntime=False, IsNativeAotRuntime=True` |
| `dotnet nuke binding-tests --class-filter NativeIntWidthTests --skip-regen` | iOS Simulator / Mono JIT | 12 pass, 0 fail, 0 skip, 0 crash |
| `dotnet nuke binding-tests --device --class-filter NativeIntWidthTests --skip-regen --skip-build` | physical iPhone / NativeAOT | 12 pass, 0 fail, 0 skip, 0 crash; bundle and live flavor verified NativeAOT |
| `dotnet nuke binding-tests --device --mono-aot --class-filter NativeIntWidthTests --skip-regen` | physical iPhone / Mono full-AOT | 12 pass, 0 fail, 0 skip, 0 crash; bundle and live flavor verified Mono full-AOT |

## Exact downstream package provenance

The downstream cells used freshly packed `0.20.0` Runtime and SDK artifacts from
the reviewed commit through an isolated NuGet cache. This prevents an older global
cache extraction with the same version from satisfying restore.

| Package | SHA-256 |
| --- | --- |
| `SwiftBindings.Runtime.0.20.0.nupkg` | `3823d5d98d2a9eaf9efb8596a28ef8f48b50f8e4e0452d30e5b6e800dd96ef89` |
| `SwiftBindings.Sdk.0.20.0.nupkg` | `b27a878ba09a51a64f55c67f2f3a254da48baf4c8e54ff8e32903804291a7904` |

Pack command:

```text
dotnet nuke pack --version 0.20.0 --apple-version 26.2.9 --skip-apple --skip-api-compat --output-dir /private/tmp/b05-sdk-packages-exact
```

This was a validation-only pack. `--skip-api-compat` does not replace any requested
B05 runtime gate; the pack reported its native-export and package-truth checks green.

## Downstream results

The package cells ran in a disposable checkout of the package-repository base with
only this batch's two source migrations applied. SDK version stamping, vendor
xcframework copies, signing configuration, the package cache, and build products
remained outside this deliverable worktree.

| Cell or focused identity | Commands | Result |
| --- | --- | --- |
| Mappedin iOS Simulator | `dotnet nuke BuildTestApp --library Mappedin`; `dotnet nuke ValidateSim --library Mappedin --timeout 60` | 161 pass, 0 fail, 0 skip; `Show3dMapOptions` reported `seeded=0, returned=2` |
| Mappedin iOS device / NativeAOT | `dotnet nuke BuildTestApp --library Mappedin --device --aot`; `dotnet nuke ValidateDevice --library Mappedin --aot --timeout 60` | 161 pass, 0 fail, 0 skip; the same slot writeback assertion passed |
| Nuke focused native-width identity | `dotnet nuke BuildTestApp --library Nuke`; `SIMCTL_CHILD_SWIFT_BINDINGS_TEST_IDENTITY=ImageCache_CostCountLimits dotnet nuke ValidateSim --library Nuke --timeout 30` | 1 pass, 0 fail, 0 skip |

The Mappedin simulator build completed successfully before `ValidateSim`; the later
review packet retained the validation log, not the separate build log. The device
build and validation logs were retained. This distinction is about log retention,
not an inferred result from a different runtime cell.

The focused Nuke run observed the process default
`CountLimitNative=9223372036854775807`, wrote/read native-width value `200`, then
wrote the bounded compatibility alias value `201` before reading that alias and its
native sibling. The post-review focused rerun logged both values explicitly as
`CountLimit=201, CountLimitNative=201`. It did not perform an unchecked narrowing
read of the default.

This receipt does **not** claim the whole Nuke cell. Its remaining closure-identity
failures stay assigned to B03/B12.

## Final Grok review and disposition

One Grok-only review completed under session
`01a0a081-3c5c-7010-8016-4f35f62fdea6`. It found no product-code defects in the
Mappedin or Nuke changes. Its one Medium and two Low evidence findings were
accepted: this receipt now identifies the two logs that were not retained, and the
focused Nuke identity now prints the bounded compatibility values it asserts. Per
the review workflow's Medium/Low stopping rule, these received one worthwhile fix
pass and affected validation, without starting another review round.

Final cheap gate: `git diff --check` passed.
