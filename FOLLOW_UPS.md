# Follow-ups

Known issues and deferred work that didn't block landing the tooling-simplification
commit (`10c25b9`). Grouped by where the fix lives.

## Upstream — `spm-to-xcframework`

All five upstream bugs documented in this section and in
`SPM_TO_XCFRAMEWORK_NOTES.md` were resolved by the Python rewrite landed in
`d0a6729812cb80ebe467c88bfdb5ca4490b4bf27`. The wrapper now delegates both
source and binary mode straight to the tool, GRDB builds source-mode cleanly,
and BlinkID builds binary-mode cleanly. The current pin in
`scripts/ensure-spm-to-xcframework.sh` is `d0a6729`.

## Internal follow-ups (not blocking)

### `detect-dependencies.sh --inject` attribute loss

**Symptom:** running `--inject` (the `SwiftFrameworkDependency` injector, distinct
from `--inject-project-refs`) on a csproj that has hand-added `PackageId="..."` or
`PackageVersion="..."` attributes on `SwiftFrameworkDependency` items strips those
attributes.

**Status:** pre-existing bug, surfaced during Phase 3a testing in the big
tooling-simplification session but left alone because it was out of scope. Not
currently breaking any library — Stripe is the only multi-product vendor, and
its hand-authored attributes get regenerated from `library.json` on re-inject.

**Fix:** make the `--inject` Python driver preserve arbitrary XML attributes when
rewriting the auto-detected `SwiftFrameworkDependency` block, similar to how
`--inject-project-refs` preserves out-of-block hand-authored `ProjectReference`
entries.

### `tests/Stripe.SimTests/StripeSimTests.csproj` drift

**Symptom:** the Stripe sim test csproj is hand-maintained with non-template
customizations (`BeforeBuild` target, `IncludeSwiftBindingsRuntimeNative=true`,
etc.) and is missing a `ProjectReference` to `StripeUICore`.

**Status:** not currently breaking — the sim test passes because Stripe's specific
test code doesn't exercise the missing paths. But the drift means re-scaffolding
via `./scripts/new-sim-test.sh Stripe --all-products --force` would wipe the
customizations.

**Fix options:**

- Extend `new-sim-test.sh` to emit the `BeforeBuild` target and runtime-native
  flag when the library has internal products, then re-scaffold Stripe and add
  the missing `StripeUICore` reference.
- Or document the customizations inline in the csproj with a `<!-- DO NOT
  regenerate -->` comment and accept the drift.

## Pre-existing files not touched this round

- `multi-framework-packaging-audit.md` at the repo root was already untracked
  when the tooling-simplification work started. It's unrelated to this arc and
  was intentionally left out of commit `10c25b9`. Dispose of it or track it
  separately as its own thing.
