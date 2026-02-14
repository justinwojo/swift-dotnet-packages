# Stripe Binding: Improvements and Action Items

Issues and improvements identified during the Stripe iOS SDK binding effort, organized by where the fix should be made.

---

## swift-bindings Generator Fixes

These should be addressed in the [swift-bindings](https://github.com/justinwojo/swift-dotnet-bindings) repo.

### P0: ObjC Enum-as-NSObject Marshalling

**Impact**: 2 of 11 Stripe products fail to compile (StripePayments, StripePaymentsUI)

The generator incorrectly marshals ObjC enum types as NSObject reference types. It emits `Runtime.GetNSObject<EnumType>()` and nullable reference patterns (`value?.Handle`) for types that are value-type enums in .NET.

**Fix**: Detect when a Swift/ObjC type maps to a .NET enum (value type) and emit integer-based marshalling instead of NSObject-based marshalling. Affected UIKit types include `UIBarStyle`, `UIKeyboardAppearance`, `NSTextAlignment` (renamed to `UITextAlignment` in .NET), `UITextFieldViewMode`, `UIControlContentVerticalAlignment`, `UIActivityIndicatorViewStyle`, `UIBlurEffectStyle`, `UILayoutPriority`.

**See**: `swift-bindings/src/docs/binding-gaps-stripe.md` Issue 1

### P1: Silent Empty Output on Dependency Failure

**Impact**: Confusing developer experience — empty DLL produced without error

When the generator encounters an unresolvable `--framework-dependency` (e.g., an ObjC-only framework), it exits with code 0 but produces no `.cs` output. The SDK then compiles an empty DLL. Consumers get a misleading "namespace not found" error.

**Fix**: Exit with non-zero when no C# bindings are produced, or at minimum emit a clear warning.

### P2: URL Constructor Arity Mismatch

**Impact**: StripePayments fails to compile

Generated code emits `new URL(someArg)` but the .NET `URL`/`NSUrl` type doesn't have a matching single-argument constructor.

**Fix**: Use the correct .NET constructor signature for `Foundation.NSUrl`.

### P3: Internal Type References in Swift Wrapper

**Impact**: Wrapper xcframework not produced (non-blocking for C# compilation)

The generated Swift wrapper references `@objc` types that are `internal` in Swift (e.g., `STPAnalyticsClient` in StripeCore). These appear in symbol graphs but can't be accessed from outside the module.

**Fix**: Filter out types with internal access level from wrapper generation, or skip wrapper functions that reference inaccessible types.

---

## swift-dotnet-packages Script Improvements

These should be addressed in this repo's scripts.

### Multi-Product Build: Preserve Internal Dependencies

The `scripts/build-xcframework.sh` script cleans up `.build-workspace/` after building (`rm -rf "$BUILD_DIR"`). This removes internal dependency frameworks (Stripe3DS2, StripeUICore, StripeCameraCore) that are needed for binding generation.

**Current workaround**: Add internal frameworks to `library.json` with `"internal": true` and build them as separate products.

**Potential improvement**: The build script could automatically detect and preserve internal dependency xcframeworks from the xcodebuild archives. These could be placed in a `.deps/` directory within the library root.

### Scaffolding: SwiftFrameworkDependency Auto-Detection

The `new-library.sh` scaffolding script doesn't generate `<SwiftFrameworkDependency>` items. For multi-product vendors, developers must manually determine and add these.

**Potential improvement**: After building xcframeworks, analyze `.swiftinterface` import statements to auto-detect inter-product dependencies and generate the correct csproj items.

### Scaffolding: ObjC-Only Framework Detection

The `--discover` mode doesn't distinguish Swift modules from ObjC-only frameworks. When all discovered products are added, ObjC-only frameworks (like Stripe3DS2) cause silent failures.

**Potential improvement**: During discovery, check for `.swiftmodule` directories to identify which products are Swift vs ObjC-only, and flag them accordingly.

### CI: Two-Pass Build Support

Multi-product vendor libraries with cross-module dependencies require a two-pass build due to the SDK fingerprint mechanism. The CI workflow should handle this pattern.

**Potential improvement**: Add a `build_passes: 2` matrix field for vendors that need it, or detect the pattern automatically in `build-testapp.sh`.

---

## Documentation Updates

### CONTRIBUTING.md

Add a section on multi-product vendor patterns covering:
- Internal dependencies (`"internal": true` in library.json)
- `SwiftFrameworkDependency` configuration
- ObjC-only framework handling
- Two-pass build pattern

### CI Workflow

Add Stripe to the CI matrix when the generator enum issues are resolved. Current config would be:
```yaml
- library: Stripe
  build_dir: libraries/Stripe
  test_dir: tests/Stripe.SimTests
  build_flags: "--all-products"
```
