# Contributing: Adding Libraries

## Directory Structure

### Single-package libraries

Libraries that produce one NuGet package go directly under `libraries/`:

```
libraries/Nuke/
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
├── build-xcframeworks.sh              # One script builds all from SPM
├── StripeCore/
│   └── Swift.Stripe.Core.csproj
├── StripePayments/
│   └── Swift.Stripe.Payments.csproj
├── StripePaymentSheet/
│   └── Swift.Stripe.PaymentSheet.csproj
└── ...
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

## Per-Library Checklist

Each library directory should contain:

| File | Purpose |
|------|---------|
| `build-xcframework.sh` | Fetch from SPM + build xcframework |
| `generate-bindings.sh` | Run the generator against the xcframework |
| `Swift.{Name}.csproj` | Library project targeting `net10.0-ios` |
| `README.md` | Package description (included in NuGet package) |
| `output/` | Generated binding output (gitignored) |

## Build Scripts

### build-xcframework.sh

Clones the library from its SPM repository (pinned to a specific version tag), builds for iOS device + simulator, and creates the xcframework. Build workspace is cleaned up after.

For vendor-grouped libraries, a single `build-xcframeworks.sh` clones once and builds all frameworks from the same source.

### generate-bindings.sh

Runs the swift-bindings generator against the xcframework. Uses `SWIFT_BINDINGS_ROOT` environment variable to locate the generator (defaults to `../../swift-bindings` as a sibling directory).

## CI

The GitHub Actions workflow uses a matrix strategy. Single-package libraries are individual matrix entries. Vendor groups are built as a unit.

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
./scripts/new-sim-test.sh LibraryName [--module ModuleName] [--force]
```

This scaffolds `tests/LibraryName.SimTests/` from the template in `templates/sim-test/`.
Use `--module` when the Swift module name differs from the library name. Use `--force` to overwrite an existing test directory.

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
