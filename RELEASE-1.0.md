# Release 1.0 — Tracking

Working doc for shipping the first NuGet release across the full set of validated packages. Replaces `SHIP-READINESS.md` (which captured the validation rounds — keep around for one cycle in case we need the round-by-round history, then delete).

## Goal

Publish 29 NuGet packages to nuget.org against the `SwiftBindings.Sdk 0.8.0` / `Apple 26.2.0` drop already validated in Round 8 (2026-04-25). Currently only `SwiftBindings.Nuke` and `SwiftBindings.Lottie` are public.

## Publish set (29)

| Group | Packages |
|---|---|
| Apple frameworks — clean (9) | CryptoKit, FamilyControls, LiveCommunicationKit, MusicKit, ProximityReader, RoomPlan, StoreKit2, Translation, WeatherKit |
| Apple frameworks — caveat (2) | TipKit (`@_alwaysEmitIntoClient` DSL — README already explains), WorkoutKit (HealthKit writes deferred — README needs update) |
| Third-party (6) | Nuke, Lottie, Kingfisher, BlinkID, BlinkIDUX, Mappedin |
| Stripe (12) | Stripe (umbrella), StripeCore, StripePayments, StripePaymentsUI, StripePaymentSheet, StripeApplePay, StripeConnect, StripeIdentity, StripeIssuing, StripeCardScan, StripeFinancialConnections, StripeUICore |

**StripeUICore note:** Stripe documents `StripeUICore` as `@_spi`/internal in their iOS SDK. We ship it as a NuGet anyway because 6 sibling Stripe sub-packages (Connect, Identity, FinancialConnections, Issuing, PaymentsUI, PaymentSheet) declare it as a `SwiftFrameworkDependency` with `PackageId`/`PackageVersion` attributes — those become nuspec dependency entries, so the consuming nupkgs require `SwiftBindings.Stripe.UICore` to resolve at install time. The `@_spi` warning lives in `libraries/Stripe/StripeUICore/README.md` Caveats so consumers know not to use it directly.

**Excluded:**
- ActivityKit — shelved for 1.0 (would require C# source generator emitting Swift companion). Keep README documenting the decision.
- GRDB — moving to `sim-validation` (see Track E). No NuGet package.

---

## Tracks

Each track is independent enough to run as its own focused session. Recommended order: B → (C, D, E in parallel) → F → A → G. Top-level README (A) goes last so it can describe the final shipped state — versions, package list, and any caveats — accurately on day one.

**Status:** B (per-package READMEs), C (Stripe zip mode), and E (GRDB → sim-validation) DONE. F partial (per-csproj PackageReadmeFile, Stripe version drift, LICENSE confirmed, NU1504 root-caused, StripeUICore ship-as-NuGet resolved — 3 sub-items still open). D, A, G pending.

### Track A — Top-level README rewrite

**Why:** Current README claims "WIP, packages coming soon" and lists 5 of 28 packages. Out of date the moment we publish.

**Tasks:**
- [ ] Drop the "Work in progress" banner
- [ ] Replace the Libraries table with the full 28-package list, grouped (Apple frameworks / Third-party / Stripe), with NuGet badges (`https://img.shields.io/nuget/v/<PackageId>`)
- [ ] Add an "Apple framework support" subsection explaining these target the system SDK (no SPM checkout) and require iOS 26.2+ on the build host
- [ ] Sample app section: keep, expand once samples ship beyond Nuke + Lottie (separate work, not 1.0-blocking)
- [ ] Drop the per-library Mode column from the front-page table — it's an implementation detail; keep it in CLAUDE.md only
- [ ] Cross-link to per-package READMEs for caveats (TipKit, WorkoutKit)

**Out of scope:** wiki, generated API docs (see Documentation Strategy below).

---

### Track B — Per-package README audit (DONE 2026-04-25)

**Outcome:** Every shipping package now has a README that follows the Nuke/Lottie gold-standard structure (1-line description, install snippet, minimal C# usage example, upstream docs link, caveats where applicable). The pre-audit assumption that all 21 READMEs were already in place turned out to be stale: 10 of the 13 Apple-framework packages (`CryptoKit`, `FamilyControls`, `LiveCommunicationKit`, `MusicKit`, `ProximityReader`, `RoomPlan`, `StoreKit2`, `Translation`, `WeatherKit`, `WorkoutKit`) had no README at all and were created from scratch.

**Tasks:**
- [x] Audit each README for: 1-line description, install snippet, minimal usage example (5–15 lines of C#), link to upstream Swift docs, caveats section
- [x] **TipKit** — verified `@_alwaysEmitIntoClient` DSL gap wording, added install/requirements/usage prelude to match standard structure
- [x] **WorkoutKit** — README created with the "HealthKit-backed writes are out-of-scope for this SDK version" caveat
- [x] **ActivityKit** — reframed from "coming soon" to **"Status: not shipping for 1.0"** with the structural reasoning intact
- [x] **Stripe sub-packages (11 public + StripeUICore)** — each rewritten with its specific role in the Stripe iOS SDK, a short C# usage snippet, and a link to the matching Stripe doc page. `StripeUICore` README explicitly notes it is not published — pulled in transitively via `SwiftFrameworkDependency`.
- [x] **CryptoKit** — README created with primary-AEAD usage examples and the residual SB0001 ancillary cluster note (HMAC ctor, Signature gen, `Unwrap`/`Decapsulate`/`ExportSecret`, `Open<TAD>`, 3-arg `IsValidSignature`).

**Files touched:**
- 10 new READMEs under `apple-frameworks/{CryptoKit,FamilyControls,LiveCommunicationKit,MusicKit,ProximityReader,RoomPlan,StoreKit2,Translation,WeatherKit,WorkoutKit}/README.md`.
- 2 reworked READMEs under `apple-frameworks/{ActivityKit,TipKit}/README.md`.
- 4 reworked READMEs under `libraries/{BlinkID,BlinkIDUX,Kingfisher,Mappedin}/README.md`.
- 12 reworked READMEs under `libraries/Stripe/{Stripe,StripeApplePay,StripeCardScan,StripeConnect,StripeCore,StripeFinancialConnections,StripeIdentity,StripeIssuing,StripePayments,StripePaymentSheet,StripePaymentsUI,StripeUICore}/README.md`.

**Pattern followed:** `libraries/Nuke/README.md` and `libraries/Lottie/README.md` (already on the gold-standard structure; left untouched).

---

### Track C — Stripe: switch to Stripe's prebuilt xcframeworks (DONE 2026-04-25)

**Outcome:** New `"zip"` build mode added to the Nuke harness. Stripe `library.json` flipped to `mode: "zip"` with `zipUrl: "https://github.com/stripe/stripe-ios/releases/download/{version}/Stripe.xcframework.zip"`, version bumped 25.6.2 → 25.11.0. End-to-end re-validated:
- `BuildXcframework --library Stripe --all-products`: ~5s (vs minutes for source mode).
- `BuildLibrary --library Stripe --all-products`: 8:46 against released `SwiftBindings.Sdk 0.8.0` from nuget.org (local-packages cleared, nuget cache cleared).
- Sim (Mono JIT): **298/0/0** PASS.
- Device (NativeAOT, iPhone): **298/0/0** PASS.
- (vs Round 8's 299 — diff is the removed `PaymentSheetError.FetchPaymentMethodsFailure` test, which 25.11.0 dropped.)

**Files changed:**
- `build/Models/LibraryConfig.cs` — added `BuildMode.Zip`, added `ZipUrl` field.
- `build/Helpers/LibraryConfig.cs` — validate `zipUrl` + `version` for zip mode.
- `build/Build.Xcframework.cs` — `BuildZip` method (download + extract + verify + install) and `DownloadFile` helper.
- `libraries/Stripe/library.json` — flipped to zip mode, dropped all `useTarget: true`, version 25.11.0.
- `libraries/Stripe/tests/Program.cs` — removed `FetchPaymentMethodsFailure` singleton test (API removed in 25.11.0).
- `CLAUDE.md` — documented the new mode, added Stripe-specific section, flagged the bin/obj cleanup gotcha.

**Findings to follow up (NOT blockers):**
- Each Stripe product csproj emits NU1504 duplicate `SwiftBindings.Apple [26.2.0,)` PackageReference warning — likely an `InjectFrameworkDeps` / `InjectProjectRefs` issue. Non-blocking but worth a Track F cleanup.
- StripeCryptoOnramp is in the zip but not in `library.json` — new public product since 25.6.2. Add as a follow-up if we want full coverage.
- `StripeUICore` still lacks `internal: true` in `library.json` despite SHIP-READINESS treating it as internal-only. Track F item.
- Incremental build gotcha when xcframeworks change: MSBuild can leave stale framework binaries inside `.app/Frameworks/`. Workaround documented in CLAUDE.md (wipe both `bin/Debug` + `bin/Release` after a Stripe version bump).

---

### Track D — CI pipeline review for the full publish set

**Why:** CI has been exercised on a few libraries at a time. Before publishing 28 packages, sanity-check the matrix runs end-to-end on `workflow_dispatch --all`.

**Tasks:**
- [ ] Trigger `workflow_dispatch` with `--all` to run the full matrix (28 entries) on `macos-26`. Watch for runner image / Xcode version drift.
- [ ] **Apple frameworks pipeline:** confirm `BuildAppleFramework` + `PackValidateAppleFramework` work for all 11. These rely on the runner's bundled Xcode iOS 26.2 SDK — flag if the runner image lags.
- [ ] **Third-party pipeline:** confirm `BuildLibrary` + `PackValidate` + `RunCiSimTest` work for all 6 + Stripe.
- [ ] **Stripe-specific:** if Track C lands binary mode, the build time should drop substantially. Verify and document.
- [ ] Decide whether to gate publish on green CI (probably yes) — add a workflow_dispatch publish job that consumes `BuildAndPackRelease` + `PublishRelease` only on the publish branch.

**Out of scope for 1.0:** wider runner-fleet diversification, ARM-Linux build (we're Apple-only).

---

### Track E — GRDB: move to `sim-validation` (DONE 2026-04-25 pending deletion confirm)

**Why:** GRDB was validated alongside the publish set but isn't shipping as a NuGet. User-local validation lives at `/Users/wojo/Dev/sim-validation` (already has Alamofire, Kingfisher, RxSwift, Snapkit, etc.) — that's the right home for it.

**Tasks:**
- [x] Move `libraries/GRDB/` → `/Users/wojo/Dev/sim-validation/GRDB/` (whole directory — library project + tests/ + xcframework). Self-contained build verified: `dotnet build SwiftBindings.GRDB.csproj` and `dotnet build tests/SwiftBindings.GRDB.Tests.csproj` both 0 errors. Added a minimal `Directory.Build.props` at the destination that sets `<TargetFramework>net10.0-ios</TargetFramework>` (must be set before Sdk.props evaluates — SWIFTBIND010 otherwise) and excludes `tests/**` from library compilation.
- [x] No CI matrix change needed — `Build.Ci.cs:DiscoverLibraries` is purely directory-based (`libraries/*/library.json`), so removing `libraries/GRDB/` drops it from the matrix automatically.
- [ ] **Pending user confirm**: delete `libraries/GRDB/` from this repo. (No reason to keep the xcframework smoke-test in CI — GRDB isn't shipping.)
- [x] CLAUDE.md does not actually mention GRDB or "16 libraries" — those live in auto-memory only. Updated.

---

### Track F — Final caveat polish + housekeeping

**Session 2026-04-25 progress:** Per-csproj package metadata, Stripe version drift, NU1504 root cause.

**Tasks:**
- [ ] Bump `SwiftBindings.Sdk` version on the publish commit if shipping at 0.8.0 is undesirable (TBD: 0.9.0? 1.0.0?). Update `local-packages/` and every csproj `PackageReference`. Reminder: do NOT pin `SwiftBindings.Runtime`.
- [ ] ~~Commit the `spm-to-xcframework` pin bump (`a2269a2c`)~~ — obsoleted by Track C; Stripe no longer uses spm-to-xcframework. The pin bump still helps other source-mode libraries that ship `<Package>_<Target>.bundle` resources.
- [ ] Delete `SHIP-READINESS.md` once round history is no longer needed (or move to `docs/history/` if we want to retain).
- [x] **Per-csproj `PackageReadmeFile`** — 13 csprojs (12 Apple frameworks + `StripeUICore`) shipped without `<PackageReadmeFile>README.md</PackageReadmeFile>` and without packing the README; nuget.org would have rendered an empty README tab for those packages. Added `<PackageReadmeFile>README.md</PackageReadmeFile>` plus `<None Include="README.md" Pack="true" PackagePath="/" />` to each, matching the pattern already in Nuke / Lottie / StripeCore. All 28 shipping csprojs now pack their README. (Centralizing into `Directory.Build.props` would have required removing duplicate `<None>` items from the 17 csprojs that already had them — chose surgical adds instead.) `Directory.Build.props` itself already has correct shared metadata (`Authors`, `RepositoryUrl`, `PackageProjectUrl`, `PackageLicenseExpression`, `PackageTags`, `PackageIcon`); `Company` is intentionally omitted.
- [x] **Stripe csproj version drift fixed** — all 12 Stripe csprojs were pinned to `<Version>25.6.2</Version>` and `PackageVersion="25.6.2"` on every `SwiftFrameworkDependency`, even though `library.json` was bumped to 25.11.0 in Track C. NuGets would have advertised version 25.6.2 while wrapping 25.11.0 binaries. Bulk-bumped to 25.11.0 across all 12 csprojs (verified clean restore on `StripePayments`).
- [x] **LICENSE confirmed** — repo-root `LICENSE` is MIT covering this repo's bindings. Per-vendor license attribution lives in each per-package README (Stripe MIT, BlinkID/Microblink commercial, Mappedin commercial, etc.) — the vendor SDK files themselves are pulled at build time from upstream and not redistributed in our nupkgs.
- [x] ~~**Mark `StripeUICore` as `"internal": true`**~~ — resolved 2026-04-25, NOT doing the flip. Inverse of the original SHIP-READINESS classification: 6 sibling Stripe csprojs (Connect, Identity, FinancialConnections, Issuing, PaymentsUI, PaymentSheet) declare `<SwiftFrameworkDependency PackageId="SwiftBindings.Stripe.UICore" PackageVersion="…"/>`, which the SDK turns into nuspec dependency entries. Flipping `library.json` to `internal: true` would skip packing UICore, but the 6 sibling .nupkgs would still advertise it as a NuGet dependency and consumer installs would fail with `Unable to find package SwiftBindings.Stripe.UICore`. The alternative — vendoring UICore.xcframework into each consuming nupkg via `<NativeReference>` — would produce duplicate frameworks at link time when consumers install >1 sub-package. Decision: ship UICore as the 12th Stripe NuGet (29th overall). The Stripe `@_spi`/private-API warning lives in the StripeUICore README Caveats, not in absence-from-nuget.org.
- [ ] **NU1504 on Stripe products is an SDK bug** — root-caused 2026-04-25. Two sites in `SwiftBindings.Sdk/0.8.0` both add `<PackageReference Include="SwiftBindings.Apple" Version="[26.2.0,)"/>` for projects that use the Apple supplement: (a) eval-time in `Sdk/Sdk.props:138-140` (unconditional, so restore picks it up), (b) build-time in `Sdk/Sdk.targets:1061` inside `_InjectAppleSupplementPrototype` when the generator sets `_SwiftBindingNeedsAppleSupplement=True`. For Stripe csprojs both gates fire and NuGet sees the same item twice. Apple framework / Nuke / Lottie csprojs only trip the props-side, hence no warning. **Fix belongs in the SDK source** (swift-bindings repo), not here — easiest fix is to drop the targets-side ItemGroup since the props-side already covers cold restore, or have the targets-side use `<PackageReference Update="SwiftBindings.Apple">` to refine the version instead of re-adding. Non-blocking for 1.0 publish; ship as part of the next SDK drop.
- [ ] **Decide on StripeCryptoOnramp** — present in Stripe's release zip but not in our `library.json`. Skip for 1.0 or add as 12th public product (would need its own csproj + tests).

---

### Track G — Pack + publish

**Run last, after all of the above are green.**

- [x] **`BuildAndPackRelease` now handles Apple frameworks** — added 2026-04-26. The target dispatches on `ResolveLibrary` kind: third-party path runs the existing `BuildLibraryAtConfiguration` + `PackAtConfiguration` flow; Apple-framework path runs a single `dotnet build -c Release` + `dotnet pack --no-build -c Release -p:Version=<v>`. Both converge on `GenerateReleaseManifest`. Validated locally with WeatherKit dry-run: clean nupkg with all 4 TFM lib/ folders, README + icon embedded, MIT license expression, `SwiftBindings.Apple 26.2.0` + `SwiftBindings.Runtime [0.8.0, 0.9.0)` declared as dependencies per TFM. Without this fix `release.yml` would have thrown `FileNotFoundException` on `library.json` for any apple-frameworks/ entry.
- [ ] **Pilot: WeatherKit** — recommended first publish (multi-TFM stress test, SHIP-quality, README polished)
  1. Local dry-run: `dotnet nuke BuildAndPackRelease --library WeatherKit --version 26.2.0 --output ./release-out --dry-run` → inspect `release-out/SwiftBindings.WeatherKit.26.2.0.nupkg`
  2. CI dry-run: trigger `release.yml` `workflow_dispatch` with `tag=WeatherKit/26.2.0`, `dry_run=true` → download artifact, inspect
  3. CI publish: same dispatch with `dry_run=false` → first real package on nuget.org
- [ ] For each remaining package: `dotnet nuke BuildAndPackRelease --library <X> --version <V> --output ./release-out --dry-run`
- [ ] Inspect `release-out/release-manifest.json` for each
- [ ] Run without `--dry-run` for the real pack
- [ ] `dotnet nuke PublishRelease --packages-dir ./release-out --nuget-api-key <K>` (parallelism=5, `--skip-duplicate`)
- [ ] After publish: tag the commit (`v1.0.0`?) and create a GitHub release with the publish set listed
- [ ] Update top-level README NuGet badges to point at live packages
- [ ] Announce (where? — separate decision)

---

## Documentation strategy (decided for 1.0)

**Stance:** "Make it work as close to native as possible, follow the native docs." No wiki, no auto-generated API site.

**What we ship:**
1. **Per-package README** inside each .nupkg (Track B above) — short, with one canonical usage example and links to the upstream Swift docs.
2. **Sample app** at `samples/SwiftBindingsSamples` — demonstrates real wiring. Currently covers Nuke + Lottie. Adding samples for more packages is post-1.0 work.
3. **CLAUDE.md / CONTRIBUTING.md** — internal/contributor docs, not published.

**Why not a wiki:**
- Auto-generated docs from generated bindings risk drift and hallucination of APIs.
- Maintenance burden — every SDK bump that changes a binding signature would need a wiki refresh.
- Native docs (Apple Developer, Stripe docs, Nuke docs, etc.) are authoritative and already comprehensive. Our value-add is the binding, not re-explaining the library.

**Reconsider if:** users repeatedly file issues asking "how do I do X in C#" for the same patterns. At that point a small "C# patterns for native Swift APIs" doc (one page, in-repo) makes sense — but only when there's evidence of demand.

---

## Open questions

1. **SDK version on publish.** Stay at 0.8.0, bump to 0.9.0, or jump to 1.0.0? The Apple package is at 26.2.0 (calver tied to the iOS SDK), which is independent.
2. **Stripe version on publish.** Bump to current stable (25.11.0 or later) as part of Track C — re-validation happens against the new version anyway.
3. **GitHub release tagging.** Per-package tags (`Nuke/12.8.0`) or a single repo tag (`v1.0.0`)? Per-package matches NuGet but creates a lot of tags.
4. **Sample app scope.** Add samples for Stripe / Apple frameworks before 1.0, or ship with Nuke+Lottie samples and expand later?
