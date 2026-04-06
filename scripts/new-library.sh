#!/bin/bash
# Scaffold a new library from templates and generate library.json.
#
# Usage:
#   Single-product (source mode):
#     ./scripts/new-library.sh Nuke \
#       --repo https://github.com/kean/Nuke.git \
#       --version 12.8.0 --mode source --scheme Nuke
#
#   Multi-product (binary mode):
#     ./scripts/new-library.sh Stripe \
#       --repo https://github.com/stripe/stripe-ios-spm.git \
#       --version 24.0.0 --mode binary \
#       --products StripeCore,StripePayments,StripePaymentSheet
#
#   Manual mode (proprietary xcframework provisioned out-of-band):
#     ./scripts/new-library.sh Mappedin \
#       --mode manual --version 1.0.0 --scheme Mappedin
#     (--version is the NuGet package version; --repo is rejected)
#
#   Discover available products from an SPM repo:
#     ./scripts/new-library.sh --discover https://github.com/stripe/stripe-ios-spm.git
#
# Options:
#   --repo URL                   SPM repository URL. Required for source/binary;
#                                rejected for manual (no SPM source).
#   --version VER                Package version. Required in ALL modes. For
#                                source/binary this is the SPM git tag; for
#                                manual it's the NuGet package version (the
#                                generated csproj's <Version>).
#   --mode source|binary|manual  Build mode (required)
#   --scheme NAME        Single-product shorthand: uses NAME as the product's
#                        framework. Historically this was an xcodebuild scheme
#                        name; spm-to-xcframework now auto-discovers schemes,
#                        so this is purely a naming convenience. Use --products
#                        for multi-product libraries.
#   --products P1,P2,P3  Comma-separated product names
#   --min-ios VER        Minimum iOS version (default: 15.0)
#   --internal P1,P2     Comma-separated products to mark as internal (no bindings)
#   --revision SHA       Full 40-char commit SHA for verification
#   --discover URL       Discover products from an SPM repo (standalone mode)
#   --vendor NAME        Group csproj / PackageId naming under a vendor prefix.
#                        Example: --vendor Stripe + product StripeCore produces
#                        SwiftBindings.Stripe.Core.csproj with PackageId
#                        SwiftBindings.Stripe.Core. The Swift module name and
#                        xcframework filename are UNCHANGED (still StripeCore).
#                        Every product must start with the vendor prefix.
#   --force              Overwrite existing library directory

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
TEMPLATE_DIR="$REPO_ROOT/templates/library"

die() { echo "Error: $*" >&2; exit 1; }

# Compute the PackageId suffix for a product when --vendor is set.
#
# Usage: vendor_package_id_suffix <vendor> <module-name>
#
# Returns the dot-separated suffix that follows "SwiftBindings." in the
# PackageId. When --vendor is empty, the module name is returned unchanged
# (backward compatible). When --vendor is set, the module must start with the
# vendor prefix; the remainder is joined with a dot.
#
#   vendor="" module="Nuke"           -> "Nuke"            (SwiftBindings.Nuke)
#   vendor="Stripe" module="Stripe"   -> "Stripe"          (SwiftBindings.Stripe)
#   vendor="Stripe" module="StripeCore" -> "Stripe.Core"   (SwiftBindings.Stripe.Core)
#   vendor="Stripe" module="Other"    -> die (not prefixed)
#
# This affects csproj filename and PackageId ONLY. Swift module, framework
# filename, and anything that feeds binding generation are left untouched.
vendor_package_id_suffix() {
    local vendor="$1" module="$2"
    if [ -z "$vendor" ]; then
        echo "$module"
        return 0
    fi
    if [ "$module" = "$vendor" ]; then
        echo "$vendor"
        return 0
    fi
    case "$module" in
        "$vendor"*)
            local tail="${module#"$vendor"}"
            echo "${vendor}.${tail}"
            ;;
        *)
            die "Product '$module' does not start with vendor prefix '$vendor' — either drop --vendor or rename the product"
            ;;
    esac
}

# ── Discover Mode ────────────────────────────────────────────────────────────

discover_products() {
    local repo_url="$1"
    local tmp_dir
    tmp_dir=$(mktemp -d)
    trap "rm -rf '$tmp_dir'" EXIT

    echo "=== Cloning $repo_url (shallow) ==="
    git clone --depth 1 "$repo_url" "$tmp_dir/repo" 2>&1 | tail -1

    echo "=== Dumping Package.swift ==="
    (cd "$tmp_dir/repo" && swift package dump-package > "$tmp_dir/package-dump.json")

    python3 -c "
import json, sys
pkg = json.load(open('$tmp_dir/package-dump.json'))
print(f\"Package: {pkg['name']}\")
print()
products = pkg.get('products', [])
if not products:
    print('No products found.')
    sys.exit(0)
print(f'Products ({len(products)}):')
for p in products:
    ptype = list(p['type'].keys())[0] if isinstance(p['type'], dict) else p['type']
    targets = ', '.join(p.get('targets', []))
    print(f\"  - {p['name']} ({ptype}) -> [{targets}]\")

print()
targets = pkg.get('targets', [])
binary_targets = [t for t in targets if t.get('type') == 'binary']
if binary_targets:
    print(f'Binary targets ({len(binary_targets)}):')
    for t in binary_targets:
        url = t.get('url', t.get('path', 'local'))
        print(f\"  - {t['name']} -> {url}\")
"

    echo ""
    echo "Resolving packages for framework analysis..."
    (cd "$tmp_dir/repo" && swift package resolve 2>&1 | tail -3)

    echo ""
    echo "Framework analysis:"
    (cd "$tmp_dir/repo" && python3 -c "
import json, os, glob

pkg = json.load(open('$tmp_dir/package-dump.json'))
targets = {t['name']: t for t in pkg.get('targets', [])}
artifacts_dir = '.build/artifacts'

for p in pkg.get('products', []):
    name = p['name']
    target_names = p.get('targets', [])

    any_swift = False
    any_objc_only = False
    all_unknown = True

    for tname in target_names:
        t = targets.get(tname, {})
        if t.get('type') == 'binary':
            # Look for resolved xcframework
            for xcfw in glob.glob(f'{artifacts_dir}/**/{tname}.xcframework', recursive=True):
                has_swiftmod = any(
                    os.path.isdir(d) for d in glob.glob(f'{xcfw}/**/Modules/*.swiftmodule', recursive=True)
                )
                if has_swiftmod:
                    any_swift = True
                else:
                    any_objc_only = True
                all_unknown = False
                break
        else:
            # Source target: classify by target type first
            target_type = t.get('type', '')
            if target_type in ('clang', 'system'):
                # C/ObjC target — check for mixed Swift sources as fallback
                src_path = t.get('path', tname)
                has_swift_files = bool(glob.glob(f'{src_path}/**/*.swift', recursive=True))
                if has_swift_files:
                    any_swift = True
                else:
                    any_objc_only = True
            elif t.get('publicHeadersPath'):
                # Swift target with public headers (unusual but possible mixed target)
                src_path = t.get('path', tname)
                has_swift_files = bool(glob.glob(f'{src_path}/**/*.swift', recursive=True))
                if has_swift_files:
                    any_swift = True
                else:
                    any_objc_only = True
            else:
                any_swift = True  # Regular Swift target
            all_unknown = False

    if any_swift:
        print(f'  {name} (Swift)')
    elif any_objc_only:
        print(f'  {name} (ObjC-only) -- use --internal when scaffolding')
    else:
        print(f'  {name} (unknown -- could not determine)')
")
    exit 0
}

# ── Argument Parsing ─────────────────────────────────────────────────────────

LIBRARY_NAME=""
REPO_URL=""
VERSION=""
MODE=""
SCHEME=""
PRODUCTS=""
MIN_IOS="15.0"
REVISION=""
FORCE=false
DISCOVER_URL=""
INTERNAL_PRODUCTS=""
VENDOR=""

while [[ $# -gt 0 ]]; do
    case "$1" in
        --discover)
            DISCOVER_URL="$2"
            shift 2
            ;;
        --repo)
            REPO_URL="$2"
            shift 2
            ;;
        --version)
            VERSION="$2"
            shift 2
            ;;
        --mode)
            MODE="$2"
            shift 2
            ;;
        --scheme)
            SCHEME="$2"
            shift 2
            ;;
        --products)
            PRODUCTS="$2"
            shift 2
            ;;
        --internal)
            INTERNAL_PRODUCTS="$2"
            shift 2
            ;;
        --min-ios)
            MIN_IOS="$2"
            shift 2
            ;;
        --revision)
            REVISION="$2"
            shift 2
            ;;
        --vendor)
            VENDOR="$2"
            shift 2
            ;;
        --force)
            FORCE=true
            shift
            ;;
        -*)
            die "Unknown option '$1'"
            ;;
        *)
            if [ -z "$LIBRARY_NAME" ]; then
                LIBRARY_NAME="$1"
            else
                die "Unexpected argument '$1'"
            fi
            shift
            ;;
    esac
done

# Handle discover mode
if [ -n "$DISCOVER_URL" ]; then
    discover_products "$DISCOVER_URL"
fi

# ── Validate Arguments ───────────────────────────────────────────────────────

[ -n "$LIBRARY_NAME" ] || die "LibraryName is required"
[ -n "$MODE" ] || die "--mode is required"

case "$MODE" in
    source|binary)
        [ -n "$REPO_URL" ] || die "--repo is required for $MODE mode"
        [ -n "$VERSION" ] || die "--version is required for $MODE mode"
        ;;
    manual)
        # --version is still required in manual mode because it's the NuGet
        # package version, which is independent of any SPM tag. Without it the
        # generated csproj would render <Version></Version>, and `dotnet pack`
        # would refuse to produce a package.
        [ -n "$VERSION" ] || die "--version is required for manual mode (it's the NuGet package version, not an SPM tag)"
        # --repo is meaningless in manual mode (no SPM source). Reject it loudly
        # rather than silently writing it into a file nobody will read.
        if [ -n "$REPO_URL" ]; then
            die "--repo is not valid in manual mode (manual xcframeworks have no SPM source)"
        fi
        ;;
    *)
        die "--mode must be 'source', 'binary', or 'manual'"
        ;;
esac

# Determine products
PRODUCT_LIST=()
IS_MULTI=false

if [ -n "$PRODUCTS" ]; then
    IFS=',' read -ra PRODUCT_LIST <<< "$PRODUCTS"
    if [ "${#PRODUCT_LIST[@]}" -gt 1 ]; then
        IS_MULTI=true
    fi
elif [ -n "$SCHEME" ]; then
    PRODUCT_LIST=("$SCHEME")
else
    die "Either --scheme (single product) or --products (one or more) is required"
fi

# For source mode, scheme defaults to the product name
if [ "$MODE" = "source" ] && [ -z "$SCHEME" ] && [ "$IS_MULTI" = false ]; then
    SCHEME="${PRODUCT_LIST[0]}"
fi

# ── Check Target Directory ───────────────────────────────────────────────────

LIB_DIR="$REPO_ROOT/libraries/$LIBRARY_NAME"
if [ -d "$LIB_DIR" ]; then
    if [ "$FORCE" = true ]; then
        echo "Removing existing $LIB_DIR (--force)"
        rm -rf "$LIB_DIR"
    else
        die "libraries/$LIBRARY_NAME/ already exists. Use --force to overwrite."
    fi
fi

mkdir -p "$LIB_DIR"

# ── Generate library.json ────────────────────────────────────────────────────

generate_library_json() {
    python3 -c "
import json

mode = '$MODE'
config = {
    'mode': mode,
    'minIOS': '$MIN_IOS',
    'products': []
}

# repository/version are meaningless in manual mode (the xcframework is
# provisioned out-of-band), so we omit them from the generated config.
if mode != 'manual':
    config = {'repository': '$REPO_URL', 'version': '$VERSION', **config}

revision = '$REVISION'
if revision:
    config['revision'] = revision

products_str = '$PRODUCTS'
scheme = '$SCHEME'
is_multi = $( [ "$IS_MULTI" = true ] && echo "True" || echo "False" )
internal_str = '$INTERNAL_PRODUCTS'
internal_set = set(n.strip() for n in internal_str.split(',') if n.strip()) if internal_str else set()

# The build pipeline no longer writes 'scheme' into library.json —
# spm-to-xcframework auto-discovers xcodebuild schemes from the Package.swift,
# so 'framework' alone is enough. 'useTarget' is not emitted by scaffolding
# either (users add it by hand after --discover shows which Stripe-style
# modules are .target() rather than .library()).
if products_str:
    for name in products_str.split(','):
        name = name.strip()
        product = {'framework': name}
        if is_multi:
            product['subdirectory'] = name
        if name in internal_set:
            product['internal'] = True
        config['products'].append(product)
elif scheme:
    config['products'].append({'framework': scheme})

print(json.dumps(config, indent=2))
" > "$LIB_DIR/library.json"
}

generate_library_json

echo "Created libraries/$LIBRARY_NAME/library.json"

# ── Generate Files From Templates ────────────────────────────────────────────

# build-xcframework.sh (thin wrapper — same for all libraries)
cp "$TEMPLATE_DIR/build-xcframework.sh.template" "$LIB_DIR/build-xcframework.sh"
chmod +x "$LIB_DIR/build-xcframework.sh"

# Build set of internal product names for skipping file generation
INTERNAL_SET=()
if [ -n "$INTERNAL_PRODUCTS" ]; then
    IFS=',' read -ra INTERNAL_SET <<< "$INTERNAL_PRODUCTS"
fi

is_internal() {
    local name="$1"
    if [ ${#INTERNAL_SET[@]} -eq 0 ]; then
        return 1
    fi
    for iname in "${INTERNAL_SET[@]}"; do
        if [ "$(echo "$iname" | xargs)" = "$name" ]; then
            return 0
        fi
    done
    return 1
}

# For each product, generate csproj and README
# Internal products get only a subdirectory (for xcframework output), not csproj
for product_name in "${PRODUCT_LIST[@]}"; do
    product_name=$(echo "$product_name" | xargs)  # trim whitespace
    MODULE_NAME="$product_name"
    MODULE_NAME_LOWER=$(echo "$MODULE_NAME" | tr '[:upper:]' '[:lower:]')
    FRAMEWORK_NAME="$product_name"

    # --vendor affects csproj filename + PackageId naming ONLY. Framework,
    # Swift module, subdirectory, and anything feeding binding generation
    # stay on the raw product name.
    PACKAGE_ID_SUFFIX=$(vendor_package_id_suffix "$VENDOR" "$MODULE_NAME")
    PACKAGE_ID="SwiftBindings.${PACKAGE_ID_SUFFIX}"
    CSPROJ_NAME="${PACKAGE_ID}.csproj"

    if [ "$IS_MULTI" = true ]; then
        PRODUCT_DIR="$LIB_DIR/$product_name"
        mkdir -p "$PRODUCT_DIR"
    else
        PRODUCT_DIR="$LIB_DIR"
    fi

    # Skip csproj/README for internal products
    if is_internal "$product_name"; then
        continue
    fi

    # {PackageId}.csproj — PackageId includes the vendor prefix when set
    sed -e "s|{{PACKAGE_ID}}|$PACKAGE_ID|g" \
        -e "s/{{MODULE_NAME}}/$MODULE_NAME/g" \
        -e "s/{{MODULE_NAME_LOWER}}/$MODULE_NAME_LOWER/g" \
        -e "s/{{VERSION}}/$VERSION/g" \
        "$TEMPLATE_DIR/SwiftBindings.__MODULE_NAME__.csproj.template" > "$PRODUCT_DIR/$CSPROJ_NAME"

    # README.md — MODULE_LINK collapses to a plain module name in manual mode
    # (no meaningful upstream repo URL) or a markdown link for source/binary.
    # This avoids emitting `[Foo]()` with an empty link when --repo is absent.
    if [ "$MODE" = "manual" ]; then
        MODULE_LINK="$MODULE_NAME"
    else
        MODULE_LINK="[$MODULE_NAME]($REPO_URL)"
    fi
    sed -e "s|{{PACKAGE_ID}}|$PACKAGE_ID|g" \
        -e "s|{{MODULE_NAME}}|$MODULE_NAME|g" \
        -e "s|{{MODULE_LINK}}|$MODULE_LINK|g" \
        "$TEMPLATE_DIR/README.md.template" > "$PRODUCT_DIR/README.md"
done

# ── Summary ──────────────────────────────────────────────────────────────────

echo ""
echo "Created libraries/$LIBRARY_NAME/ with:"
echo "  - library.json"
echo "  - build-xcframework.sh (thin wrapper)"
for product_name in "${PRODUCT_LIST[@]}"; do
    product_name=$(echo "$product_name" | xargs)
    suffix=$(vendor_package_id_suffix "$VENDOR" "$product_name")
    csproj_name="SwiftBindings.${suffix}.csproj"
    if is_internal "$product_name"; then
        if [ "$IS_MULTI" = true ]; then
            echo "  - $product_name/ (internal — no bindings)"
        fi
    elif [ "$IS_MULTI" = true ]; then
        echo "  - $product_name/"
        echo "    - $csproj_name"
        echo "    - README.md"
    else
        echo "  - $csproj_name"
        echo "  - README.md"
    fi
done
echo ""
echo "Next steps:"

# Check MODE before IS_MULTI — a multi-product manual library needs the
# manual-provisioning flow, not the two-pass source-build flow.
if [ "$MODE" = "manual" ]; then
    # Manual-mode xcframeworks are provisioned out-of-band — the build step
    # verifies presence rather than producing a new artifact. The flow is the
    # same for single and multi-product manual libraries; only the list of
    # xcframework paths to drop changes.
    echo "  1. Drop the vendor-provided xcframework(s) into place:"
    for product_name in "${PRODUCT_LIST[@]}"; do
        product_name=$(echo "$product_name" | xargs)
        if is_internal "$product_name"; then
            continue
        fi
        if [ "$IS_MULTI" = true ]; then
            echo "       libraries/$LIBRARY_NAME/$product_name/${product_name}.xcframework"
        else
            echo "       libraries/$LIBRARY_NAME/${product_name}.xcframework"
        fi
    done
    echo "     (Download from the vendor portal. Manual xcframeworks are NOT committed.)"
    echo ""
    if [ "$IS_MULTI" = true ]; then
        cat << EOF
  2. Verify all xcframeworks are in place:
       cd libraries/$LIBRARY_NAME && ./build-xcframework.sh --all-products

  3. Scaffold tests:
       ./scripts/new-sim-test.sh $LIBRARY_NAME --all-products

  4. Build + validate:
       cd tests/$LIBRARY_NAME.SimTests && ./build-testapp.sh && ./validate-sim.sh 30
EOF
    else
        cat << EOF
  2. Verify the xcframework is in place:
       cd libraries/$LIBRARY_NAME && ./build-xcframework.sh

  3. Scaffold tests:
       ./scripts/new-sim-test.sh $LIBRARY_NAME

  4. Build + validate:
       cd tests/$LIBRARY_NAME.SimTests && ./build-testapp.sh && ./validate-sim.sh 15
EOF
    fi
elif [ "$IS_MULTI" = true ]; then
    # Canonical multi-product source/binary flow (see CONTRIBUTING.md). Steps
    # 3-6 are the two-pass binding-generation dance required when products
    # reference each other — ProjectReferences can only be injected after the
    # first build emits fresh C# that detect-dependencies.sh can grep for
    # cross-module type usage.
    cat << EOF
  1. Build xcframeworks:
       cd libraries/$LIBRARY_NAME && ./build-xcframework.sh --all-products

  2. Inject SwiftFrameworkDependency items (enables sibling module resolution):
       ./scripts/detect-dependencies.sh libraries/$LIBRARY_NAME --all-products --inject

  3. Clean stale generated output (fresh-generation boundary for pass 4):
       for d in \$(./scripts/build-xcframework.sh libraries/$LIBRARY_NAME --all-products --resolve-products); do
           sub=\${d%%|*}
           rm -rf libraries/$LIBRARY_NAME/\${sub:+\$sub/}obj/Debug/net10.0-ios/swift-binding/
       done

  4. First-pass build — SDK generates fresh C# into obj/.../swift-binding/
     (wrapper compile may fail; that's expected):
       # iterate products via --resolve-products and 'dotnet build' each csproj

  5. Inject ProjectReferences (uses freshly generated C# to find real type deps):
       ./scripts/detect-dependencies.sh libraries/$LIBRARY_NAME --all-products --inject-project-refs

  6. Second-pass build — full build with ProjectReferences in place.

  7. Scaffold tests:
       ./scripts/new-sim-test.sh $LIBRARY_NAME --all-products

  8. Build test app and validate:
       cd tests/$LIBRARY_NAME.SimTests && ./build-testapp.sh && ./validate-sim.sh 30
EOF
else
    cat << EOF
  1. Build xcframework: cd libraries/$LIBRARY_NAME && ./build-xcframework.sh
  2. Scaffold tests:    ./scripts/new-sim-test.sh $LIBRARY_NAME
  3. Build test app:    cd tests/$LIBRARY_NAME.SimTests && ./build-testapp.sh
     (The SDK csproj generates bindings automatically during build)
  4. Validate (sim):    ./validate-sim.sh 15
EOF
fi
