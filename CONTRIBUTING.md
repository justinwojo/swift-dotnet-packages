# Contributing: Adding Libraries

## Quick Start

### Scaffold a new library

```bash
# Single-product (source mode):
./scripts/new-library.sh Nuke \
  --repo https://github.com/kean/Nuke.git \
  --version 12.8.0 --mode source --scheme Nuke

# Multi-product vendor (binary mode):
./scripts/new-library.sh Stripe \
  --repo https://github.com/stripe/stripe-ios-spm.git \
  --version 24.0.0 --mode binary \
  --products StripeCore,StripePayments,StripePaymentSheet

# Discover available products from an SPM repo:
./scripts/new-library.sh --discover https://github.com/stripe/stripe-ios-spm.git
```

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
├── build-xcframework.sh
├── generate-bindings.sh
├── Swift.Nuke.csproj
├── README.md
└── output/
```

Examples: Nuke, CryptoSwift, Lottie, Alamofire

### Multi-package vendors

When a vendor distributes 3+ related frameworks from a single SPM repository, group them under a vendor directory:

```
libraries/Stripe/
├── library.json
├── build-xcframework.sh
├── StripeCore/
│   ├── generate-bindings.sh
│   └── Swift.StripeCore.csproj
├── StripePayments/
│   ├── generate-bindings.sh
│   └── Swift.StripePayments.csproj
└── StripePaymentSheet/
    ├── generate-bindings.sh
    └── Swift.StripePaymentSheet.csproj
```

**Rule of thumb:** Group when there's a shared build step or 3+ packages from the same source repo. For 1-2 packages, flat is fine.

### Dependent packages (no vendor grouping)

When two standalone libraries have a dependency but come from different sources or only have 2 packages, keep them flat with a `ProjectReference`:

```
libraries/BlinkID/
├── Swift.BlinkID.csproj               # Standalone
└── ...
libraries/BlinkIDUX/
├── Swift.BlinkIDUX.csproj             # References BlinkID
└── ...
```

```xml
<!-- libraries/BlinkIDUX/Swift.BlinkIDUX.csproj -->
<ItemGroup>
  <ProjectReference Include="../BlinkID/Swift.BlinkID.csproj" />
</ItemGroup>
```

When published to NuGet, `ProjectReference` automatically becomes a `PackageReference` — consumers who install `Swift.BlinkIDUX` get `Swift.BlinkID` pulled in transitively.

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
- Don't get `generate-bindings.sh`, csproj, or README
- Are excluded from `--resolve-products` (CI skips them for binding generation)

#### SwiftFrameworkDependency auto-detection

Products that import sibling Swift modules need `<SwiftFrameworkDependency>` items in their csproj. Use `detect-dependencies.sh` to auto-detect these from `.swiftinterface` files:

```bash
# Report mode (stdout):
scripts/detect-dependencies.sh libraries/Stripe --all-products

# Inject into csproj files:
scripts/detect-dependencies.sh libraries/Stripe --all-products --inject
```

The `--inject` flag:
- Uses XML comment markers (`<!-- BEGIN/END auto-detected SwiftFrameworkDependency -->`) for idempotent updates
- Migrates existing manual entries on first run (removes sibling entries, keeps non-sibling ones)
- Running twice produces identical output

**Prerequisite**: xcframeworks must be built before running.

**Important**: ObjC-only frameworks (no `.swiftmodule`) are automatically excluded from generated deps. Never add them as `SwiftFrameworkDependency` — this causes the generator to silently produce no output.

#### Two-pass build pattern

Multi-product libraries with cross-module Swift dependencies require a two-pass build. Pass 1 generates bindings but wrapper compilation may fail (e.g., internal `@objc` types referenced in wrapper); the SDK stamps a fingerprint. Pass 2 skips generation and compiles C# successfully.

**Locally:**
```bash
# Pass 1: generate + build (some products may fail)
for product in StripeCore StripePayments ...; do
  (cd libraries/Stripe/$product && ./generate-bindings.sh)
  dotnet build libraries/Stripe/$product/Swift.$product.csproj || true
done

# Pass 2: build only (should succeed)
for product in StripeCore StripePayments ...; do
  dotnet build libraries/Stripe/$product/Swift.$product.csproj
done
```

**In CI:** Set `build_passes: 2` in the matrix entry. The CI workflow automatically runs multiple passes, tolerating build failures on non-final passes.

#### Onboarding checklist for new multi-product vendors

1. Run `--discover` to list products and identify ObjC-only frameworks
2. Scaffold with `--products` and `--internal` flags
3. Build xcframeworks: `./build-xcframework.sh --all-products`
4. Auto-detect deps: `scripts/detect-dependencies.sh libraries/Vendor --all-products --inject`
5. Generate bindings and build (two passes if needed)
6. Scaffold sim tests: `./scripts/new-sim-test.sh Vendor --all-products`
7. Add to CI matrix with appropriate `build_flags` and `build_passes`

## Library Config (`library.json`)

Each library root has a `library.json` that declares its SPM source and products:

```json
{
  "repository": "https://github.com/kean/Nuke.git",
  "version": "12.8.0",
  "mode": "source",
  "minIOS": "15.0",
  "products": [
    { "scheme": "Nuke", "framework": "Nuke" }
  ]
}
```

| Field | Required | Description |
|-------|----------|-------------|
| `repository` | yes | SPM git URL |
| `version` | yes | Git tag (exact pin) |
| `revision` | no | Full 40-char commit SHA — verified at build time |
| `mode` | yes | `"source"` or `"binary"` |
| `minIOS` | no | Min iOS deployment target (default `"15.0"`) |
| `products[]` | yes | Array of products to build |
| `products[].scheme` | source only | Xcode scheme name for xcodebuild |
| `products[].framework` | yes | Framework name (xcframework output name) |
| `products[].module` | no | Swift module name (defaults to `framework`) |
| `products[].subdirectory` | no | Subdirectory for multi-product vendors |
| `products[].artifactPath` | binary only | Override artifact lookup path |
| `products[].internal` | no | `true` to mark as internal-only (no bindings, excluded from `--resolve-products`) |

### Build Modes

**Source mode** (`"mode": "source"`): Clones the repo and builds xcframeworks with xcodebuild. Used for libraries that distribute source (Nuke, Alamofire, etc.).

**Binary mode** (`"mode": "binary"`): Uses `swift package resolve` to download pre-built xcframeworks. Used for vendors that distribute binary xcframeworks via SPM (Stripe, Firebase, etc.).

## Per-Library Checklist

Each library directory should contain:

| File | Purpose |
|------|---------|
| `library.json` | SPM source, version, mode, products |
| `build-xcframework.sh` | Thin wrapper calling `scripts/build-xcframework.sh` |
| `generate-bindings.sh` | Run the generator against the xcframework |
| `Swift.{Name}.csproj` | Library project targeting `net10.0-ios` |
| `README.md` | Package description (included in NuGet package) |
| `output/` | Generated binding output (gitignored) |

## Build Scripts

### Shared build script

The shared `scripts/build-xcframework.sh` reads `library.json` and handles both build modes:

```bash
# Build single product (auto-detected)
cd libraries/Nuke && ./build-xcframework.sh

# Build specific products
cd libraries/Stripe && ./build-xcframework.sh --products StripeCore,StripePayments

# Build all products
cd libraries/Stripe && ./build-xcframework.sh --all-products

# Dry-run: resolve products (for CI)
scripts/build-xcframework.sh libraries/Stripe --all-products --resolve-products
```

### generate-bindings.sh

Runs the swift-bindings generator against the xcframework. Uses `SWIFT_BINDINGS_ROOT` environment variable to locate the generator (defaults to `../../swift-bindings` as a sibling directory).

## CI

The GitHub Actions workflow uses a matrix strategy with richer entries:

```yaml
strategy:
  matrix:
    include:
      - library: Nuke
        build_dir: libraries/Nuke
        test_dir: tests/Nuke.SimTests
        build_flags: ""
      - library: Stripe
        build_dir: libraries/Stripe
        test_dir: tests/Stripe.SimTests
        build_flags: "--all-products"
        build_passes: 2
```

| Field | Required | Description |
|-------|----------|-------------|
| `library` | yes | Display name |
| `build_dir` | yes | Path to library directory |
| `test_dir` | yes | Path to simulator test directory |
| `build_flags` | yes | Flags for `build-xcframework.sh` (e.g. `"--all-products"`) |
| `build_passes` | no | Number of `dotnet build` passes (default 1, use 2 for multi-product vendors with cross-module deps) |

Product lists are derived from `library.json` at runtime via `--resolve-products` to avoid drift between config and CI. Internal products are automatically excluded.

For dependent packages, use `needs:` to enforce build order:

```yaml
jobs:
  build-blinkid:
    # builds first
  build-blinkidux:
    needs: build-blinkid
    # builds after
```

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

```bash
cd tests/LibraryName.SimTests
./build-testapp.sh
# Boot simulator first: xcrun simctl boot <udid>
./validate-sim.sh 15
```

### Adding library-specific tests

Edit `tests/LibraryName.SimTests/Program.cs` — add test methods in `RunLibraryTests()` and call them from there. Each test should:
1. Call an API in a try/catch block
2. Log pass/fail via the `TestLogger`
3. Record result via `TestResults.Pass()` / `.Fail()`

The validate script watches for `TEST SUCCESS` in console output and detects crashes automatically.

## NuGet Naming

| Pattern | Example |
|---------|---------|
| Single library | `Swift.Nuke` |
| Vendor group | `Swift.Stripe.Core`, `Swift.Stripe.Payments` |
| Dependent pair | `Swift.BlinkID`, `Swift.BlinkIDUX` |
