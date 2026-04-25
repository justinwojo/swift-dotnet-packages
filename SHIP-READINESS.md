# Ship Readiness Guide

**Authoritative audit:** Round 7 — 2026-04-25 (post SDK rebuild + closing revalidation against fresh Stripe xcframeworks)
**SDK tested:** `SwiftBindings.Sdk 0.8.0` + `SwiftBindings.Runtime 0.8.0` + `SwiftBindings.Templates 0.8.0` + `SwiftBindings.Apple 26.2.0` (local-packages drop 2026-04-25 04:25 — rebuilt fresh against `swift-bindings@f936e30d` to pick up Session 3's F3 fix).
**Generator-side blocker doc (current round):** `/Users/wojo/Dev/swift-bindings/src/docs/ship-blockers-round7.md` — F4 (`YamlLikeTbdFormatParser` does not consume multi-line `objc-eh-types` continuation lines, blocks 5 of 12 Stripe products on fresh-build pipeline).
**Generator-side prior round:** `/Users/wojo/Dev/swift-bindings/src/docs/ship-blockers-round6.md` (Round 6 enumerated F1, F2, F3 — all RESOLVED in Round 7; see verification table in `ship-blockers-round7.md`).
**Previous round docs (history):** `ship-blockers-round6.md`, `ship-blockers-round5.md`, `ship-blockers-2026-04-22.md`, `ship-blockers-round4.md`, `ship-blockers-round3.md`, `ship-blockers-round2.md`, `ship-blockers.md` — all under `/Users/wojo/Dev/swift-bindings/src/docs/`.

### Open generator-side blockers (for the next round)

| ID | Area | Impact | Where it manifests |
|---|---|---|---|
| **F4** | `YamlLikeTbdFormatParser` multi-line `objc-eh-types` handling | Generator parses TBD's `objc-eh-types: [ A, B,` first line as warning, then throws `Invalid key-value pair format` on continuation line | Stripe fresh-build pipeline (`dotnet nuke RunCiSimTest --library Stripe`) — 5 of 12 products fail at generator step (StripePayments, StripePaymentsUI, StripePaymentSheet, StripeIssuing, Stripe umbrella). Suggested fix in `ship-blockers-round7.md` §F4. |

**Resolved in Round 7 (no longer blockers):** F1 ✅ (InjectFrameworkDeps preserves user-authored deps), F2 ✅ (spm-to-xcframework mixed-framework Headers/+Modules now emitted by pin `5909bd5`+`e9e46f2`), F3 ✅ (CryptoKit AEAD primary flow runtime-verified on both runtimes; commit `swift-bindings@f936e30d`).

**Repo state caveat:** Stripe csprojs are now F1-clean (worked through `RunCiSimTest --library Stripe` cleanly when it ran on the 7 unblocked products). The 5 F4-blocked products keep their existing csprojs untouched — F4 fails at the generator binding step before csproj rewrite. Two unpushed local commits in `swift-dotnet-packages` (`c73f7d3` F1 verification, `5523e23` spm-to-xcframework pin bump) remain pending NuGet publish.

---

## TL;DR

**16 shippable NuGet packages today** = 9 Apple frameworks clean + 2 Apple frameworks with README caveat + 6 third-party libraries (StripeUICore is internal-only; GRDB will be removed). **Stripe (12 products) is on HOLD pending F4** — the fresh-build generator pipeline aborts on 5 of 12 Stripe products. Once F4 lands, Stripe's 12 products plus the existing 11 ship for a **27-package** publish set.

Round 7 confirmed that Round 6's three blockers (F1, F2, F3) are all resolved at the gate level. CryptoKit's primary AEAD round-trip (`AES.GCM.Seal/Open` + `ChaChaPoly.Seal/Open`) is now reachable from C# on both Mono JIT sim and NativeAOT device — the **CryptoKit "primary AEAD ships" framing is accurate**. Round 7's net-new finding **F4** surfaced when Session 2's regenerated Stripe xcframeworks exposed a TBD-parser gap that Round 6's cached pre-Session-2 xcframeworks did not exercise. Details: `ship-blockers-round7.md` §F4.

| Bucket | Count | Packages |
|---|---|---|
| **Clean SHIP** | 9 Apple + 6 third-party = **15** | **Apple (9):** **CryptoKit** (promoted Round 7 — F3 cleared; AEAD primary flow verified), LiveCommunicationKit, ProximityReader, RoomPlan, FamilyControls, Translation, StoreKit2, WeatherKit, MusicKit. **Third-party (6):** Nuke, Lottie, Kingfisher, BlinkID, BlinkIDUX, Mappedin. |
| **SHIP with README caveat** | 2 Apple | TipKit (permanent — `@_alwaysEmitIntoClient` DSL, 12 SB0001 unchanged), WorkoutKit (HealthKit writes deferred). |
| **HOLD pending F4** | 12 Stripe products | **Stripe umbrella + 11 sub-products** (StripeCore, StripePayments, StripePaymentsUI, StripeApplePay, StripeConnect, StripeIdentity, StripeIssuing, StripeCardScan, StripeFinancialConnections, StripePaymentSheet, StripeUICore-internal). Fresh-build generator step aborts on 5 of 12 products via F4 (multi-line `objc-eh-types` parser bug). Cached pre-Session-2 xcframeworks would still build; shipping demands a fresh-build path. |
| **Shelved (not shipping for 1.0)** | 1 | ActivityKit — user-facing value doesn't justify a C#-source-generator-emits-Swift-companion subsystem (Round 5 decision 2026-04-23). |

---

## Current validation results (2026-04-25, Round 7 — closing revalidation against fresh SDK rebuild)

Validated against the SDK drop that landed in `local-packages/` at 04:25 today (Round 7 rebuild against `swift-bindings@f936e30d`, which carries Session 3's F3 fix). Two runtimes (Mono JIT sim + NativeAOT device on iPhone 13). Round 7 un-skipped the 4 CryptoKit AEAD tests that were blocked by F3 in Round 6 (Tests 26–29) and confirmed runtime pass on both runtimes. Round 7 also surfaced **F4** (a TBD-parser gap exposed by Session 2's regenerated Stripe xcframeworks) — see `ship-blockers-round7.md` §F4.

### Apple frameworks (11 × 2 runtimes — ActivityKit shelved)

| Framework | Sim (Mono JIT) | Device (NativeAOT) | SB0001 (iOS) | Classification |
|---|---|---|---|---|
| **CryptoKit** | **PASS (40/0/0)** | **PASS (40/0/0)** | 38 | **SHIP** — Round 7 promoted from HOLD; F3 cleared, AEAD primary flow verified end-to-end on both runtimes (Tests 26–29). 38 SB0001 ancillary residual unchanged (HMAC ctor, Signature gen, `Unwrap`/`Decapsulate`/`ExportSecret`, `Open<TAD>`, 3-arg `IsValidSignature`). |
| FamilyControls | PASS (15/0/0) | PASS (15/0/0) | 0 | **SHIP** |
| LiveCommunicationKit | PASS (18/0/0) | PASS (18/0/0) | 0 | **SHIP** |
| MusicKit | PASS (37/0/0) | PASS (37/0/0) | 0 | **SHIP** |
| ProximityReader | PASS (10/0/0) | PASS (10/0/0) | 0 | **SHIP** |
| RoomPlan | PASS (29/0/0) | PASS (29/0/0) | 0 | **SHIP** |
| StoreKit2 | PASS (36/0/0) | PASS (36/0/0) | 0 | **SHIP** |
| TipKit | PASS (20/0/0) | PASS (20/0/0) | 12 | SHIP w/ permanent caveat (result-builder DSL) |
| Translation | PASS (12/0/0) | PASS (12/0/0) | 0 | **SHIP** |
| WeatherKit | PASS (27/0/0) | PASS (27/0/0) | 0 | **SHIP** |
| WorkoutKit | PASS (25/0/0) | PASS (25/0/0) | 0 | SHIP w/ caveat (HealthKit writes deferred) |

**Totals:** 269 sim assertions pass / 0 fail / 0 skip. 269 device pass / 0 fail / 0 skip. **Round 7 deltas vs. Round 6:** CryptoKit `+4 PASS / -4 SKIP` from un-skipping Tests 26–29 against the F3 fix; all other rows unchanged. (Round 6 stated 283/4-skip totals; recounting Round 6's per-row data also yields ~265 + 4 skip, so the prior round's stated total appears to have been an arithmetic artefact, not a Round 7 regression.)

### Stripe (12 products via umbrella) — HOLD pending F4

**Sim and device build BLOCKED at the generator step on 5 of 12 products** (StripePayments, StripePaymentsUI, StripePaymentSheet, StripeIssuing, Stripe umbrella). The 7 unblocked products (StripeCore, StripeApplePay, StripeCardScan, StripeFinancialConnections, StripeIdentity, StripeConnect, StripeUICore) generate fine but the consumer test app links the umbrella, so end-to-end Stripe validation cannot run.

```
RunCiSimTest Stripe (sim):    Pipeline failed: dotnet build (sim) failed with exit 1
ValidateDevice Stripe (AOT):  BuildTestApp failed; ValidateDevice not invoked
```

Round 6 reported `300/0` against cached pre-Session-2 xcframeworks (which did not exercise the parser path that fails in F4). Round 7's regenerated xcframeworks are F2-correct (Headers/+Modules present everywhere) but expose F4 in the parser. PassKit-placeholder gap on StripeApplePay + StripeIssuing remains deferred (separate from F4).

### Third-party (6 libraries)

| Library | Sim | Device | Status |
|---|---|---|---|
| BlinkID | PASS (305/0/0) | PASS (305/0/0) | SHIP |
| BlinkIDUX | PASS (146/0/1) | PASS (146/0/1) | SHIP (see `ship-blockers-round5.md` §Out-of-scope for CaptureService actor note) |
| Kingfisher | PASS (248/0/1) | PASS (248/0/1) | SHIP |
| Lottie | PASS (89/0/0) | PASS (89/0/0) | SHIP |
| Mappedin | PASS (257/0/0) | PASS (257/0/0) | SHIP |
| Nuke | PASS (77/0/0) | PASS (77/0/0) | SHIP |

(Vendor count includes GRDB at PASS 247/0/0 sim + device — kept building this round though it will be removed before publish per "Excluded Libraries" note. Aggregate 1369/1369 across all 7 vendor builds incl. GRDB.)

Plus 15 `sim-validation` apps (Alamofire, Kingfisher, RxSwift, SnapKit, CryptoSwift, KeychainAccess, Starscream, DeviceKit, PhoneNumberKit, Reachability, Swinject, ObjectMapper, SwiftyBeaver, XMLCoder, BonMot) all PASS sim + device — 439 assertions per runtime, 0 fail.

---

## Round 6 → Round 7 blocker closures

Round 6 had three blockers (F1, F2, F3) on top of the Round 5 HOLD closures. Round 7 verified all three at the gate level — see `ship-blockers-round7.md` for the full verification record. Summary:

- **F1 (`InjectFrameworkDeps` regression)** — RESOLVED. `RunCiSimTest --library Stripe` runs the inject-deps pass without dropping ObjC-only `SwiftFrameworkDependency` (Stripe3DS2) or user-set `<SwiftWrapperRequired>false</SwiftWrapperRequired>`. Verified by `git diff libraries/Stripe/Stripe*/SwiftBindings.Stripe.*.csproj | grep -E "Stripe3DS2|SwiftWrapperRequired"` → empty.
- **F2 (`spm-to-xcframework` mixed-framework Headers/+Modules drop)** — RESOLVED. Pin bump to `5909bd5`+`e9e46f2` (Session 2). Every Stripe framework slice now has `Headers/` + `Modules/module.modulemap`. Verified for all 14 Stripe products × 2 slices.
- **F3 (CryptoKit `SymmetricKey → AEAD-CSM` marshalling)** — RESOLVED. `swift-bindings@f936e30d` discriminates non-frozen struct vs class in CSM `@_cdecl` wrappers (PayloadHandle case). CryptoKit Tests 26–29 (un-skipped from Round 6) pass on both runtimes — primary AEAD round-trip reachable from C#.

## Round 5 finish-plan outcomes (HOLD closures)

Each Round 5 HOLD has a corresponding §8 grep gate; Round 7 ran the same gates against the freshly-rebuilt drop. All four still PASS.

### 1. StoreKit2 — `VerificationResult<T>.TryGetVerified` ✅ LANDED (Session 5)

**Gate:** `grep -n "TryGetVerified\|TryGetUnverified" apple-frameworks/StoreKit2/obj/Debug/net10.0-ios26.2/swift-binding/StoreKit2.cs`
**Result:** matches at `StoreKit2.cs:3576` (`TryGetUnverified`) and `:3656` (`TryGetVerified`). Both emit as concrete methods with `out TSignedType` payload extraction, not SB0001 stubs. IAP transaction-validation flow is reachable from C#.

### 2. WeatherKit — `Forecast<T>` collection projection ✅ LANDED (Session 3 / `d5482212`)

**Gate:** `grep -n "GetEnumerator\|IEnumerable\|IEnumerator" apple-frameworks/WeatherKit/obj/Debug/net10.0-ios26.2/swift-binding/WeatherKit.cs`
**Result:** `WeatherKit.cs:14027` declares `Forecast<TElement> : ... IReadOnlyList<TElement>` with full `GetEnumerator` / `Count` / `this[int]` surface. `foreach (var h in weather.HourlyForecast)` compiles.

### 3. MusicKit — Issue C ✅ LANDED (Session 6)

**Gate:** `grep -c 'Obsolete.*SB0001' apple-frameworks/MusicKit/obj/Debug/net10.0-ios26.2/swift-binding/MusicKit.cs`
**Result:** **0** SB0001 (was 4 in Round 5). The 4 `MusicItemCollection<T>` ergonomics methods (`Index`, `FormIndex`, `Distance`, indexed `index(_:offsetBy:)`) emit as concrete methods. The `DoesPairingSatisfyAssociatedTypeConstraints` relaxation in the SDK is verified by the absence of any `ProtocolWitnessTable.GetOrThrow<TMusicItemType, IMusicItem>` boilerplate at the previously-flagged sites.

### 4. CryptoKit — AEAD reachable ✅ LANDED (`swift-bindings@f936e30d`, Round 7)

**Gate:** `grep -nE "public .* Seal\(" apple-frameworks/CryptoKit/obj/Debug/net10.0-ios26.2/swift-binding/CryptoKit.cs | grep -v Obsolete | wc -l`
**Result:** **12** non-`[Obsolete]` `Seal(` overloads (AES.GCM + ChaChaPoly, sync-throws CSM). 2 non-`[Obsolete]` `Open(` overloads on the same families. **Symbol-level gate GREEN since Round 6.**

**Runtime gate (Round 7):** End-to-end `SymmetricKey → AES.GCM.Seal → SealedBox → AES.GCM.Open` round-trip from C# **passes** on both Mono JIT sim and NativeAOT device. CryptoKit Tests 26 (AES.GCM round-trip), 27 (ChaChaPoly round-trip), 28 (tamper detection), 29 (Seal-with-AD dispatch) all PASS. `apple-frameworks/CryptoKit/tests/Tests.cs` had its 4 `Skip(...)` calls reverted to the original try/catch round-trip bodies in Session 3.

**F3 deviation note:** Test 28 exercises tamper detection via wrong-key (not byte-flipped ciphertext — `SealedBox(nonce:, ciphertext:, tag:)` is a generic init the binding generator does not yet emit). Test 29 stops at Seal verification rather than full Seal→Open<TAD> round-trip because the generic `Open<TAD>` overload is `CallConvSwift` direct and hits Issue 1 in `feedback_mono_jit_blame.md` (Mono JIT `!ji->async` assertion on synchronous CallConvSwift) — confirmed upstream. Both deviations preserve the "primary AEAD reachable" criterion. See `ship-blockers-round7.md` §F3 deviation analysis.

The 38 residual SB0001 are confined to the ancillary cluster (HMAC ctor, Signature gen, `Unwrap`/`Decapsulate`/`ExportSecret`, `Open<TAD>` with authenticated-data tuple, 3-arg `IsValidSignature`) and require either future method-level-generics work or are upstream `@_alwaysEmitIntoClient` (separate caveat).

---

## Per-NEAR-SHIP caveats (READMEs to ensure before publish)

### 1. TipKit — 12 result-builder DSL methods

Nine methods on `Tips.ActionsBuilder` (`BuildExpression`, `BuildPartialBlock`, `BuildArray`, `BuildEither`, `BuildLimitedAvailability`, `BuildOptional`, `BuildFinalResult`); 2 on `Tips.TipOptionsBuilder`; 1 `ITipOption` return-type. All marked `@_alwaysEmitIntoClient` in Apple's Swift stdlib — no exported ABI symbol. **Permanent** upstream limitation.

See existing `apple-frameworks/TipKit/README.md` — already reflects this.

### 2. WorkoutKit — HealthKit write surface deferred

`WorkoutScheduler.Shared` / `MaxAllowedScheduledWorkoutCount` / `IsSupported` / `ScheduleAsync` all emit cleanly. HealthKit-backed writes (the scheduler can schedule but not mutate HealthKit data models) are out-of-scope for this SDK round.

**README text:** "Scheduler read/write works via `WorkoutScheduler.Shared`. HealthKit-backed data writes are out-of-scope for this SDK version."

---

## Publishing checklist

1. **Resolve Stripe F4 before publishing Stripe.** The `YamlLikeTbdFormatParser` does not consume multi-line `objc-eh-types` continuation lines, blocking 5 of 12 Stripe products at the generator step on the fresh-build pipeline. Suggested one-line fix at `src/Swift.Bindings/src/Demangler/TbdParser/Parsing/YamlLikeTbdFormatParser.cs:329-332` (default arm in `ParseExports` switch should detect unclosed `[` and call `ParseMultiLineArray()`). See `ship-blockers-round7.md` §F4. Until F4 lands, Stripe cannot ship.
2. **Finalize SDK version.** 0.8.0 → 0.9.0 (or 1.0.0) on the publish commit. Update `local-packages/` nupkgs and every csproj `SwiftBindings.Sdk` PackageReference. Remember: do NOT pin `SwiftBindings.Runtime` — it flows transitively from the SDK.
3. **Per-package READMEs.** Ensure every caveated package has a README documenting its gap. TipKit already does. Add (or update) one for WorkoutKit. ActivityKit's existing README still applies for the shelved-for-1.0 case.
4. **Pack.** `dotnet nuke BuildAndPackRelease --library <X> --version <V> --output <dir>` for each. `--dry-run` first to inspect manifest, then real run.
5. **Publish.** `dotnet nuke PublishRelease --packages-dir <dir> --nuget-api-key <K>` (parallelism=5, `--skip-duplicate`).

---

## How to re-evaluate after the next SDK drop

Run this once the generator team announces a new drop in `local-packages/`:

### 0. Starting context (for a fresh Claude session)

1. Read this file top-to-bottom. The Round 7 section above is authoritative; `ship-blockers-round7.md` enumerates F4 (the open generator-side blocker) and verification recipes for F1/F2/F3 closure.
2. Confirm the SDK drop landed: `ls -la local-packages/ | head`.
3. Check with the user whether any SHIP/caveat classifications should change before you begin (e.g., a new round closes F4 or the residual CryptoKit ancillary cluster, or surfaces a regression worth re-classifying).
4. Run steps 1–10 below.
5. For anything that moved SB0001 / tombstone / named-gap state, repeat the focused per-package audit. Do NOT spawn a wide agent swarm — a 2–3-agent pass over the affected packages is right-sized.

### 1. Update SDK + clear caches

```bash
dotnet nuget locals all --clear
# if SDK version changed:
grep -rl 'SwiftBindings.Sdk/0.8.0' libraries apple-frameworks \
  | xargs sed -i '' 's|SwiftBindings.Sdk/0.8.0|SwiftBindings.Sdk/<new>|g'
```

### 2. Wipe binding output + stamps

```bash
find libraries apple-frameworks -path "*/obj/*/swift-binding" -type d -exec rm -rf {} + 2>/dev/null
find libraries apple-frameworks -name "swift-binding.stamp" -delete 2>/dev/null
```

(For a truly clean rebuild, also wipe `obj/` and `bin/` — the Round 5 validation did this. The SDK's fingerprint mechanism handles incremental correctly, but full wipe removes any stale state from prior rounds.)

### 3. Rebuild Apple frameworks (multi-TFM)

```bash
# ActivityKit shelved for 1.0 — keep it in the loop only if revisiting that decision.
for fw in CryptoKit FamilyControls LiveCommunicationKit MusicKit ProximityReader RoomPlan StoreKit2 TipKit Translation WeatherKit WorkoutKit; do
  echo "=== $fw ===" && dotnet build "apple-frameworks/$fw/SwiftBindings.$fw.csproj" -v q 2>&1 | tail -3
done
```

### 4. Rebuild third-party + Stripe

```bash
for lib in Nuke Lottie Kingfisher BlinkID BlinkIDUX Mappedin; do
  dotnet build "libraries/$lib/SwiftBindings.$lib.csproj" -v q 2>&1 | tail -3
done
```

**Stripe — read this before running anything:**

If F1 (`InjectFrameworkDeps` regression) and F2 (`spm-to-xcframework` regression) are both fixed in the new SDK drop:
```bash
# Discard the Round 6 F1 workaround state, then run the standard two-pass:
git checkout -- libraries/Stripe/Stripe*/SwiftBindings.Stripe.*.csproj
dotnet nuke BuildLibrary --library Stripe --all-products
```

If F1 / F2 are NOT yet fixed (Round 6 state):
```bash
# Keep the working-copy Stripe csprojs (they ARE the workaround). Build by hand in dep order against cached xcframeworks:
for p in StripeCore StripeApplePay StripeCardScan StripeFinancialConnections StripeIdentity StripeIssuing StripePayments StripePaymentsUI StripeUICore StripeConnect StripePaymentSheet Stripe; do
  dotnet build "libraries/Stripe/$p/SwiftBindings.Stripe.${p#Stripe}.csproj" -v q 2>&1 | tail -3
done
# (csproj filename suffix mapping: StripeCore → Core, Stripe umbrella → just `Stripe`. Confirm against `libraries/Stripe/library.json` order field.)
```

Verify F1/F2 are fixed before trusting the Nuke target:
```bash
# F1 fix check — run BuildLibrary, then diff a Stripe csproj. If Stripe3DS2 SwiftFrameworkDependency is gone or SwiftWrapperRequired is missing, F1 still regresses.
git diff libraries/Stripe/StripePayments/SwiftBindings.Stripe.Payments.csproj | grep -E "Stripe3DS2|SwiftWrapperRequired"

# F2 fix check — fresh xcframework should have Headers/ and module.modulemap:
ls -la libraries/Stripe/StripePayments/StripePayments.xcframework/ios-arm64/StripePayments.framework/{Headers,Modules} 2>&1 | head
```

### 5. SB0001 + tombstone sweep

```bash
for cs in apple-frameworks/*/obj/Debug/net10.0-ios26.2/swift-binding/*.cs \
          libraries/*/obj/Debug/net10.0-ios/swift-binding/*.cs; do
  lib=$(echo "$cs" | sed 's|.*/\(libraries\|apple-frameworks\)/\([^/]*\)/.*|\2|')
  file=$(basename "$cs")
  [ "${file%.cs}" = "$lib" ] || continue
  n=$(grep -c 'Obsolete.*SB0001' "$cs" 2>/dev/null)
  [ "$n" -gt 0 ] && echo "$lib: $n"
done

# Tombstones (empty generic type bodies)
for cs in apple-frameworks/*/obj/Debug/net10.0-ios26.2/swift-binding/*.cs; do
  lib=$(echo "$cs" | sed 's|apple-frameworks/\([^/]*\)/.*|\1|')
  python3 -c "
import re
src = open('$cs').read()
for m in re.finditer(r'public\s+(?:partial\s+)?(?:struct|class|sealed class)\s+(\w+<[^>]+>)\s*\{\s*\}', src):
    print(f'$lib: tombstone -> {m.group(1)}')
"
done
```

### 6. Sim validation (Mono JIT)

```bash
dotnet tool restore  # if first time in a fresh clone
dotnet nuke BootSim
for fw in CryptoKit FamilyControls LiveCommunicationKit MusicKit ProximityReader RoomPlan StoreKit2 TipKit Translation WeatherKit WorkoutKit; do
  dotnet nuke BuildTestApp --library $fw && dotnet nuke ValidateSim --library $fw --timeout 60
done
```

### 7. Device validation (NativeAOT) — iPhone required

```bash
# IMPORTANT: ValidateDevice needs --aot to find the Release-config .app:
for fw in CryptoKit FamilyControls LiveCommunicationKit MusicKit ProximityReader RoomPlan StoreKit2 TipKit Translation WeatherKit WorkoutKit; do
  dotnet nuke BuildTestApp --library $fw --device --aot \
    && dotnet nuke ValidateDevice --library $fw --aot --timeout 120
done
```

Each run emits `Results: X passed, Y failed, Z skipped` followed by `TEST SUCCESS` or crash signal. ValidateDevice returns 0 on success, nonzero on any failure.

### 8. Per-package gap verification

The four Round 5 HOLD gates are all GREEN as of Round 6 — re-run them as regression checks against any new drop:

```bash
# StoreKit2 TryGetVerified — Round 6: GREEN (matches at :3576, :3656)
grep -n "TryGetVerified\|TryGetUnverified" apple-frameworks/StoreKit2/obj/Debug/net10.0-ios26.2/swift-binding/StoreKit2.cs

# WeatherKit Forecast<T> iterator — Round 6: GREEN (`Forecast<TElement> : ... IReadOnlyList<TElement>` at :14027)
grep -n "GetEnumerator\|IEnumerable\|IReadOnlyList" apple-frameworks/WeatherKit/obj/Debug/net10.0-ios26.2/swift-binding/WeatherKit.cs | head -10

# MusicKit Issue C — Round 6: GREEN (0 SB0001, was 4)
grep -c 'Obsolete.*SB0001' apple-frameworks/MusicKit/obj/Debug/net10.0-ios26.2/swift-binding/MusicKit.cs

# CryptoKit AEAD symbol-level — Round 6: GREEN (12 non-obsolete `Seal(` overloads on AES.GCM + ChaChaPoly)
grep -nE "public .* Seal\(" apple-frameworks/CryptoKit/obj/Debug/net10.0-ios26.2/swift-binding/CryptoKit.cs | grep -v Obsolete | wc -l

# CryptoKit AEAD runtime gate (F3) — Round 6: RED (Tests 26-29 are Skip; un-Skip and re-run when SDK ships F3 fix)
# Verification flow once F3 is announced fixed:
#   1. In apple-frameworks/CryptoKit/tests/Tests.cs, replace each Skip(...) at Tests 26-29 with the original try/catch round-trip.
#   2. dotnet nuke BuildTestApp --library CryptoKit && dotnet nuke ValidateSim --library CryptoKit --timeout 60
#   3. dotnet nuke BuildTestApp --library CryptoKit --device --aot && dotnet nuke ValidateDevice --library CryptoKit --aot --timeout 120
# Expected: Tests 26 (AES.GCM round-trip), 27 (ChaChaPoly round-trip), 28 (tamper detection), 29 (Seal+AD) all PASS.
# If any still throws CryptoKitError.incorrectKeySize, F3 is not actually fixed — keep CryptoKit on HOLD.
grep -c 'Skip(.*F3' apple-frameworks/CryptoKit/tests/Tests.cs   # Round 6: 4. Should drop to 0 once F3 lands.
```

If a future drop regresses any of these to RED, mark the affected package back to HOLD and notify the generator team via a fresh `ship-blockers-round<N>.md`.

### 9. Classification update

Based on §8 results, update the TL;DR table at the top of this doc. If a SHIP regresses to HOLD: move it to a HOLD section with audit findings. If a caveated package clears its caveat: remove the row and promote to clean SHIP.

### 10. Commit the doc update

Every validation round updates this doc in the same commit that bumps `SwiftBindings.Sdk` (zero-regression policy). Leave the `ship-blockers-round<N>.md` link pointing to the current round's blocker doc.

---

## Test conventions (for adding new coverage)

Every Apple framework test lives at `apple-frameworks/<Name>/tests/Tests.cs`. The convention:

- `Pass(name)` on success, `Fail(name, reason)` otherwise.
- Print `Results: X passed, Y failed, Z skipped` then `TEST SUCCESS` (or crash) to stdout.
- `ValidateSim` / `ValidateDevice` watch stdout for `TEST SUCCESS`.

When adding a new assertion for a previously-HOLD primary flow, hit the **real API**, not just metadata. Metadata-only assertions (`typeof(X)`, enum values, singleton accessors) prove the binding linked — they do NOT prove the flow works. The audit flagged this specifically for CryptoKit (33 assertions, all metadata, zero real crypto) and StoreKit2 (35 assertions, zero VerificationResult payload extraction).

---

## Environment Notes

- **Primary repo:** `/Users/wojo/Dev/swift-dotnet-packages`
- **Generator repo:** `/Users/wojo/Dev/swift-bindings`
- **Local SDK drop:** `/Users/wojo/Dev/swift-dotnet-packages/local-packages/`
- **.NET SDK:** 10.0.103 (pinned in `global.json`)
- **Platform target default:** `net10.0-ios` for third-party libraries; Apple frameworks are multi-TFM (`net10.0-ios26.2`, `tvos26.2`, `macos26.2`, `maccatalyst26.2`)
- **Physical device for NativeAOT validation:** iPhone 13 (udid auto-detected via `xcrun devicectl list devices`)

## Excluded Libraries

- **GRDB** — will not ship as NuGet, to be removed from repo.
