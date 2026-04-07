# Contributing: Adding Libraries

## Quick Start

### Scaffold a new library

```bash
# Single-product (source mode):
./scripts/new-library.sh Nuke \
  --repo https://github.com/kean/Nuke.git \
  --version 12.8.0 --mode source --scheme Nuke

# Multi-product vendor (with `--vendor` → vendor-prefixed NuGet packages):
./scripts/new-library.sh Stripe \
  --repo https://github.com/stripe/stripe-ios.git \
  --version 25.6.2 --mode source --vendor Stripe \
  --products StripeCore,StripePayments,StripePaymentSheet

# Discover available products from an SPM repo:
./scripts/new-library.sh --discover https://github.com/stripe/stripe-ios.git
```

### `--vendor` naming convention

For vendors that ship multiple modules from one SPM repo, pass `--vendor <Name>` to group the NuGet packages under a dotted namespace:

| With `--vendor Stripe` | Product `StripeCore` | Product `StripePayments` | Product `Stripe` (umbrella) |
|---|---|---|---|
| csproj filename | `SwiftBindings.Stripe.Core.csproj` | `SwiftBindings.Stripe.Payments.csproj` | `SwiftBindings.Stripe.csproj` |
| PackageId | `SwiftBindings.Stripe.Core` | `SwiftBindings.Stripe.Payments` | `SwiftBindings.Stripe` |
| Swift module | `StripeCore` (unchanged) | `StripePayments` (unchanged) | `Stripe` (unchanged) |
| Framework | `StripeCore.xcframework` (unchanged) | `StripePayments.xcframework` (unchanged) | `Stripe.xcframework` (unchanged) |

**Invariant:** `--vendor` affects csproj filename and PackageId ONLY. It never modifies `products[].module`, `products[].framework`, or any path that feeds binding generation or namespace-based dependency detection. Every listed product must start with the vendor prefix — `new-library.sh` fails loudly if you try `--vendor Stripe --products Foo`.

### Scaffold simulator tests

```bash
# Single-product
./scripts/new-sim-test.sh Nuke

# Multi-product vendor
./scripts/new-sim-test.sh Stripe --all-products

# Cross-repo dependency
./scripts/new-sim-test.sh BlinkIDUX --with BlinkID
```

## Directory Structure

### Single-package libraries

Libraries that produce one NuGet package go directly under `libraries/`:

```
libraries/Nuke/
├── library.json
├── SwiftBindings.Nuke.csproj
└── README.md
```

Examples: Nuke, CryptoSwift, Lottie, Alamofire

### Multi-package vendors

When a vendor distributes 2+ related frameworks from a single SPM repository, group them under a vendor directory:

```
libraries/Stripe/
├── library.json
├── StripeCore/
│   └── SwiftBindings.Stripe.Core.csproj
├── StripePayments/
│   └── SwiftBindings.Stripe.Payments.csproj
└── StripePaymentSheet/
    └── SwiftBindings.Stripe.PaymentSheet.csproj
```

**Rule of thumb:** Group when there's a shared build step or 2+ packages from the same source repo.

### Dependent packages

When two or more related libraries have a dependency, group them under a parent directory with a `ProjectReference`:

```
libraries/BlinkID/
├── library.json
├── BlinkID/
│   └── SwiftBindings.BlinkID.csproj
└── BlinkIDUX/
    └── SwiftBindings.BlinkIDUX.csproj         # References BlinkID
```

```xml
<!-- libraries/BlinkID/BlinkIDUX/SwiftBindings.BlinkIDUX.csproj -->
<ItemGroup>
  <ProjectReference Include="../BlinkID/SwiftBindings.BlinkID.csproj" />
</ItemGroup>
```

When published to NuGet, `ProjectReference` automatically becomes a `PackageReference` — consumers who install `SwiftBindings.BlinkIDUX` get `SwiftBindings.BlinkID` pulled in transitively.

### Multi-product vendor guide

Vendors like Stripe distribute many frameworks from a single SPM repo. These require extra configuration beyond what `new-library.sh` generates by default.

#### Discovering products and identifying ObjC-only frameworks

Use `--discover` to list products and detect which are Swift vs ObjC-only:

```bash
./scripts/new-library.sh --discover https://github.com/stripe/stripe-ios.git
```

The output annotates each product:
- `(Swift)` — has a Swift module, generates bindings normally
- `(ObjC-only)` — no Swift module, must be marked internal
- `(unknown)` — could not determine; inspect manually

#### Internal dependencies

Some vendors include internal frameworks (ObjC-only or non-public) that other products depend on at runtime. Mark these with `--internal` during scaffolding:

```bash
./scripts/new-library.sh Stripe \
  --repo https://github.com/stripe/stripe-ios.git \
  --version 25.6.2 --mode source \
  --products StripeCore,StripePayments,...,Stripe3DS2,StripeUICore,StripeCameraCore \
  --internal Stripe3DS2,StripeUICore,StripeCameraCore
```

Internal products:
- Are built as xcframeworks (needed at runtime)
- Get `"internal": true` in `library.json`
- Don't get a csproj or README
- Are excluded from `--resolve-products` (CI skips them for binding generation)

#### Cross-module dependency injection

Multi-product libraries where products import sibling Swift modules need `<SwiftFrameworkDependency>` items (for the SDK's binding generator) and `<ProjectReference>` items (for the C# compiler) in each product csproj. Both are produced by Nuke targets in `build/Build.Dependencies.cs`:

- `InjectFrameworkDeps --library Foo [--all-products]` — writes a `<!-- BEGIN/END auto-detected SwiftFrameworkDependency -->` block by inspecting `.swiftinterface` files inside each xcframework. Idempotent. ObjC-only frameworks (no `.swiftmodule`) are filtered out automatically — never add them as `SwiftFrameworkDependency` or the generator will silently produce no output.
- `InjectProjectRefs --library Foo [--all-products]` — writes a `<!-- BEGIN/END auto-detected ProjectReference -->` block. **Behavior-derived**: it greps the freshly generated C# under `obj/.../swift-binding/*.cs` for `\bModuleName\.Identifier` and adds a `ProjectReference` only when the generated code actually mentions the sibling. Idempotent on re-runs.
- `CleanSwiftBindingOutput --library Foo [--all-products]` — wipes every product's `swift-binding/` directory before a fresh first-pass build.

`InjectProjectRefs` enforces a strict freshness boundary. `obj/.../swift-binding/swift-binding.stamp` must exist and be newer than both:
- the csproj (csproj hasn't been re-injected since the last generation)
- `<framework>.xcframework/Info.plist` (xcframework hasn't been rebuilt since the last generation)

If either check fails for any product, the run aborts without touching any csproj.

#### Canonical multi-product build

`BuildLibrary` chains everything internally — no manual two-pass dance:

```bash
dotnet nuke BuildLibrary --library Stripe --all-products
```

Under the hood:
1. `BuildXcframework` for every product
2. `InjectFrameworkDeps`
3. Pass 1 `dotnet build` for each product csproj (wrapper compile may fail; expected — generator emits fresh C#)
4. `InjectProjectRefs` (reads the fresh C# from step 3)
5. Pass 2 `dotnet build` for each product csproj (must succeed)

**When auto-detection is insufficient:** If pass 5 still fails with missing-type errors, the grep may have missed a reference. Look at the failing `.cs` file, identify the sibling module being referenced, and verify it appears in that product's `obj/.../swift-binding/*.cs`. If the reference lives only in the Swift wrapper (`*.Wrapper.swift`) and not in the generated C#, injection won't pick it up — hand-author the `ProjectReference` outside the `<!-- BEGIN/END auto-detected ProjectReference -->` block (hand-authored refs inside the auto-block are rewritten on every run).

#### Onboarding checklist for new multi-product vendors

1. Run `./scripts/new-library.sh --discover <repo-url>` to list products and identify ObjC-only frameworks
2. Scaffold with `--vendor`, `--products`, and `--internal` flags
3. Build the library end-to-end: `dotnet nuke BuildLibrary --library Vendor --all-products`
4. Scaffold sim tests: `./scripts/new-sim-test.sh Vendor --all-products`
5. Push the branch — CI auto-detects the new library from `library.json`

## Library Config (`library.json`)

Each library root has a `library.json` that declares its SPM source and products:

```json
{
  "repository": "https://github.com/kean/Nuke.git",
  "version": "12.8.0",
  "mode": "source",
  "minIOS": "15.0",
  "products": [
    { "framework": "Nuke" }
  ]
}
```

| Field | Required | Description |
|-------|----------|-------------|
| `repository` | source/binary | SPM git URL |
| `version` | source/binary | Git tag (exact pin) |
| `revision` | no | Full 40-char commit SHA — verified at build time |
| `mode` | yes | `"source"`, `"binary"`, or `"manual"` |
| `minIOS` | no | Min iOS deployment target (default `"15.0"`) |
| `products[]` | yes | Array of products to build |
| `products[].framework` | yes | SPM product (or target, with `useTarget`) name and xcframework output filename |
| `products[].module` | no | Swift module name (defaults to `framework`) |
| `products[].subdirectory` | no | Subdirectory for multi-product vendors |
| `products[].useTarget` | no | `true` → pass `--target` to `spm-to-xcframework` instead of `--product`. Required for SPM `.target(...)` entries not exposed as `.library(...)` — e.g. 11 of Stripe's 14 modules. |
| `products[].internal` | no | `true` to mark as internal-only (no bindings, excluded from `--resolve-products` and sim-test scaffolding) |

### Build Modes

The `mode` field tells `BuildXcframework` how to obtain the compiled frameworks:

**Source mode** (`"mode": "source"`): Delegates to the pinned `spm-to-xcframework` tool in `.tools/`. The tool clones the repository, discovers schemes, archives each requested product for iOS device and iOS Simulator, and merges the slices into one `<framework>.xcframework` per product. The Nuke harness then moves each output into the library's directory layout (honoring `subdirectory` for multi-product vendors). Used by Nuke, Lottie, Stripe, Kingfisher, BlinkIDUX, etc.

**Binary mode** (`"mode": "binary"`): Delegates to `spm-to-xcframework --binary`, which resolves the vendor's binary SPM package via SPM and copies each product's prebuilt xcframework out of `.build/artifacts/` (pruning `__MACOSX` AppleDouble ghosts that some vendor zips ship). Used only by BlinkID today. The harness still handles `revision` SHA verification itself before calling the tool, because the tool's binary path doesn't run its source-mode `verify_revision` check.

**Manual mode** (`"mode": "manual"`): No build — the xcframework is provisioned out-of-band. `BuildXcframework` verifies each expected `<framework>.xcframework` directory exists under the library root and errors with the missing paths otherwise. Used for proprietary artifacts (Mappedin) that must be downloaded from a vendor portal. Manual xcframeworks are never committed.

**Naming tip:** For source mode, `framework` must match a real SPM product or target name, because it's passed to `spm-to-xcframework` as `--product` or `--target`. The old `scheme` field (previously used for `xcodebuild -scheme`) is no longer needed — the tool auto-discovers schemes from the package. For example, Stripe's umbrella product used to carry `"scheme": "StripeiOS"` alongside `"framework": "Stripe"`; now only `"framework": "Stripe"` is kept, and the tool figures out the `StripeiOS` scheme internally.

## Per-Library Checklist

Each library directory should contain:

| File | Purpose |
|------|---------|
| `library.json` | SPM source, version, mode, products |
| `SwiftBindings.{Name}.csproj` | SDK csproj — generates bindings + compiles during `dotnet build` |
| `README.md` | Package description (included in NuGet package) |

Build orchestration lives in the Nuke harness at the repo root (`dotnet nuke <Target>`); no per-library shell wrappers are written. The Nuke CLI is pinned in `.config/dotnet-tools.json`; run `dotnet tool restore` once after cloning to install it.

## Build Targets

The Nuke harness exposes one target per concern. The most common entry points:

```bash
# Build the xcframework(s) for a library
dotnet nuke BuildXcframework --library Nuke
dotnet nuke BuildXcframework --library Stripe --products StripeCore,StripePayments
dotnet nuke BuildXcframework --library Stripe --all-products

# End-to-end build (xcframeworks + dependency injection + dotnet build)
dotnet nuke BuildLibrary --library Stripe --all-products

# Pack
dotnet nuke Pack --library Nuke --version 12.8.0 --output /tmp/packages

# Release flow (build + pack at -c Release + manifest)
dotnet nuke BuildAndPackRelease --library Nuke --version 12.8.0 --output /tmp/packages --dry-run

# CI matrix detection
dotnet nuke ListChangedLibraries --base-sha origin/main --head-sha HEAD --json
```

## CI

The GitHub Actions workflow auto-detects libraries from `libraries/*/library.json`. On PRs, `ListChangedLibraries` emits a GHA matrix containing only the libraries touched by the diff (or every library when shared infra under `build/`, `.nuke/`, `scripts/`, `templates/`, `Directory.Build.props`, `global.json`, or `.github/workflows/ci.yml` is affected). On `workflow_dispatch`, `--all` rebuilds everything.

Internal products are automatically excluded from binding generation. Build flags (multi-product, two-pass) are derived from `library.json` at runtime — no manual matrix configuration needed.

Per-library CI steps live in `.github/workflows/ci.yml` and call `BuildLibrary`, `PackValidate`, and `RunCiSimTest`. Cross-library ordering for dependent packages (e.g. `BlinkIDUX` after `BlinkID`) is handled inside the matrix entry — `BuildLibrary` builds dependents bottom-up using the multi-pass orchestration.

## Simulator Tests

Each library can have a simulator test app that validates bindings on iOS Simulator.

### Creating tests for a new library

```bash
# Simple single-product
./scripts/new-sim-test.sh Nuke

# Stripe subset
./scripts/new-sim-test.sh Stripe --products StripeCore,StripePayments

# Full Stripe
./scripts/new-sim-test.sh Stripe --all-products

# Cross-repo dependency
./scripts/new-sim-test.sh BlinkIDUX --with BlinkID

# Complex cross-vendor
./scripts/new-sim-test.sh Checkout --with Stripe:StripeCore,StripePaymentSheet
```

This scaffolds `tests/LibraryName.SimTests/` from the template in `templates/sim-test/`.
Use `--module` when the Swift module name differs from the library name (single-product only).
Use `--force` to overwrite an existing test directory.

### Running tests locally

The Nuke harness derives `APP_NAME` and `BUNDLE_ID` from the test directory basename, so there's nothing to configure per library.

```bash
# Simulator build + validate (default)
dotnet nuke BuildTestApp --library LibraryName
dotnet nuke BootSim
dotnet nuke ValidateSim --library LibraryName --timeout 15

# Physical device (Mono AOT — Debug)
dotnet nuke BuildTestApp --library LibraryName --device
dotnet nuke ValidateDevice --library LibraryName --timeout 30

# Physical device (NativeAOT — Release; used for release validation)
export CODESIGN_IDENTITY="Apple Development: Your Name (TEAMID)"
export PROVISIONING_PROFILE="Wildcard Dev"
export TEAM_ID="TL2K6QUQEH"
dotnet nuke BuildTestApp --library LibraryName --device --aot
dotnet nuke ValidateDevice --library LibraryName --aot --timeout 30
```

**AOT device test prerequisites** (manual QA only — not run in CI):
- Physical iOS device connected via USB or on the same network
- Xcode with a working `xcrun devicectl` toolchain
- Signing credentials exported as `CODESIGN_IDENTITY`, `PROVISIONING_PROFILE`, `TEAM_ID` env vars. `BuildTestApp` fails with a clear error listing the missing vars when `--aot --device` is invoked without them. CI does not have these credentials, so `--aot --device` is manual-QA-only — confirm the critical libraries (the 5 that historically supported NativeAOT: BlinkID, BlinkIDUX, Lottie, Nuke, Stripe) before cutting a release.

### Adding library-specific tests

Edit `tests/LibraryName.SimTests/Program.cs` — add test methods in `RunLibraryTests()` and call them from there. Each test should:
1. Call an API in a try/catch block
2. Log pass/fail via the `TestLogger`
3. Record result via `TestResults.Pass()` / `.Fail()`

`ValidateSim` / `ValidateDevice` watch stdout for `TEST SUCCESS` and detect crashes automatically.

## Pinned Tools (`.tools/`)

External tooling used by the build pipeline is pinned by version and SHA-256 inside `build/Build.Xcframework.cs`. The cached binary lives in `.tools/spm-to-xcframework-<short-sha>` and is gitignored.

To bump the pinned `spm-to-xcframework` version:

1. Pick the new commit SHA on `justinwojo/spm-to-xcframework` main (or a tag, when one exists).
2. Download the script contents and compute its SHA-256:
   ```bash
   curl -sfL "https://raw.githubusercontent.com/justinwojo/spm-to-xcframework/<sha>/spm-to-xcframework" | shasum -a 256
   ```
3. Update the three constants at the top of `build/Build.Xcframework.cs`:
   - `SpmToXcfRef` — the new commit SHA (or tag)
   - `SpmToXcfSha256` — the computed digest
   - `SpmToXcfUrl` — derived from `SpmToXcfRef` (only changes if the URL scheme changes)
4. Remove any stale `.tools/spm-to-xcframework-*` files (the filename embeds the short SHA, so older copies become orphans once the pin moves).
5. Re-run `dotnet nuke BuildXcframework --library Nuke` to confirm the harness downloads and verifies cleanly.

The harness refuses to use a cached copy whose SHA-256 doesn't match the pin — there is no silent fallback to stale contents.

## NuGet Naming

| Pattern | Example |
|---------|---------|
| Single library | `SwiftBindings.Nuke` |
| Vendor group | `SwiftBindings.Stripe.Core`, `SwiftBindings.Stripe.Payments` |
| Dependent pair | `SwiftBindings.BlinkID`, `SwiftBindings.BlinkIDUX` |
