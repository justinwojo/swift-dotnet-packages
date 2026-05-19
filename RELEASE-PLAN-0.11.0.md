# Release Plan — SDK 0.11.0 / Apple 26.2.3 wave

Re-ship every library we already ship on the new SDK, plus first-ship Matter, MatterSupport, BlinkID, BlinkIDUX, and Mappedin. Bump Nuke and Stripe to current upstream while we're at it.

## Scope

### Excluded from this repo's ship list
- `libraries/Kingfisher` — not shipping from this repo
- `apple-frameworks/ActivityKit` — not shipping from this repo

Directories stay in place; skipped at pack/publish time.

### Third-party (5 libraries, 17 NuGet packages)

| Library | Native bump | NuGet version | Revalidate? |
|---|---|---|---|
| Nuke | 13.0.5 → **13.0.6** | 13.0.6 | yes — sim + device |
| Stripe (13 sub-packages) | 25.11.0 → **25.15.0** | 25.15.0 | yes — sim + device (4 minors; expect test-app fixups) |
| BlinkID | 7.7.0 → **7.8.0** | 7.8.0 (first ship) | yes — sim + device |
| BlinkIDUX | 7.7.0 → **7.8.0** | 7.8.0 (first ship) | yes — sim + device |
| Lottie | no change (4.6.0) | 4.6.1 → **4.6.2** | no — binding-only bump |
| Mappedin | no change (6.2.0) | **6.2.0** (first ship) | no — binding-only bump |

### Apple frameworks (15 packages, all at 26.2.3)

- **Re-ship** (currently at 26.2.1 on nuget.org): CryptoKit, FamilyControls, LiveCommunicationKit, MusicKit, ProximityReader, RealityFoundation, RealityKit, RoomPlan, StoreKit2, TipKit, Translation, WeatherKit, WorkoutKit
- **First ship**: Matter, MatterSupport (closes issue #38)
- No revalidation — already validated against SDK 0.11.0 / Apple 26.2.3

**MusicKit note:** `MusicLibraryRequest<T>` remains suppressed pending the KeyPath subsystem (see `swift-bindings/src/docs/keypath-subsystem/00-overview.md`). This is the same suppression that exists in the currently-shipped 26.2.1 binding, not a 26.2.3 regression — call it out in the package README so consumers don't expect the surface to land. Before pack, regen MusicKit at 0.11.0 and grep the generated C# to confirm `MusicLibraryRequest` is still tombstoned (rather than silently emitting broken keypath methods).

## Execution sequence

### 1. Version bumps in source

- `library.json` upstream `version` field: Nuke 13.0.6, BlinkID 7.8.0, BlinkIDUX 7.8.0, Stripe 25.15.0
- csproj `<Version>` to match: Nuke, BlinkID, BlinkIDUX, all 13 Stripe sub-packages
- csproj `<Version>` for Lottie: 4.6.0 → 4.6.2 (binding-only patch on top of unchanged native)
- Mappedin csproj already at 6.2.0 — verify, no edit expected

### 2. Validate the four version-bumped libraries

In this order (lowest-risk first; fix test apps as needed before moving on):

1. **Nuke 13.0.6** — patch bump, lowest risk
2. **BlinkID 7.8.0**
3. **BlinkIDUX 7.8.0**
4. **Stripe 25.15.0** — 4 minors; memory flags API drift between Stripe minors (25.11.0 already dropped `PaymentSheetError.FetchPaymentMethodsFailure`)

Per library:
```
dotnet nuke BuildLibrary --library X --all-products
dotnet nuke BuildTestApp --library X                         # sim
dotnet nuke ValidateSim --library X --timeout 30
dotnet nuke BuildTestApp --library X --device --aot          # device, NativeAOT
dotnet nuke ValidateDevice --library X --timeout 60
```

### 3. Pack everything

`BuildAndPackRelease --library X --version V` for each shipping library. Skip Kingfisher and ActivityKit.

### 4. Publish

`PublishRelease --packages-dir … --nuget-api-key …` — parallel push (=5), `--skip-duplicate`.

### 5. Post-ship

- Update `MEMORY.md` `## Current SDK Version` block with new third-party versions
- Tag the repo

## Watch-items during execution

- **Stripe test app fixups likely.** After zip re-download for the new version, wipe `bin/Debug` + `bin/Release` + `obj/` under `libraries/Stripe/**` before `BuildTestApp` — per CLAUDE.md, MSBuild's incremental `<NativeReference>` copy can miss the change and ship stale frameworks (`Symbol not found` at dyld load).
- **BlinkID 7.8.0 ingest mode.** Confirm 7.8.0 still ships as `BlinkID.xcframework.zip` / `BlinkIDUX.xcframework.zip` on the GitHub release page (we're on zip mode).
- **MusicKit suppression verify.** Regen-and-grep before pack to confirm `MusicLibraryRequest<T>` is still tombstoned at 0.11.0.

## Decision log

- Bump Stripe to 25.15.0 (4 minors) rather than holding at 25.11.0 — aligns with "all third-party on latest" goal; accept test-app drift risk.
- Bump BlinkID/BlinkIDUX to 7.8.0 (upstream dropped after the recent in-repo bump to 7.7.0).
- Hold Kingfisher and ActivityKit out of this wave — not shipping from this repo.
- Ship MusicKit 26.2.3 despite the KeyPath gap — gap is pre-existing in 26.2.1, and 0.11.0 still improves MusicKit's non-KeyPath surface.
