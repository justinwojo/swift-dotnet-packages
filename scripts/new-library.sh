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
#   Discover available products from an SPM repo:
#     ./scripts/new-library.sh --discover https://github.com/stripe/stripe-ios-spm.git
#
# Options:
#   --repo URL           SPM repository URL (required)
#   --version TAG        Git tag (required)
#   --mode source|binary Build mode (required)
#   --scheme SCHEME      Xcode scheme name (source mode, single product)
#   --products P1,P2,P3  Comma-separated product names
#   --min-ios VER        Minimum iOS version (default: 15.0)
#   --internal P1,P2     Comma-separated products to mark as internal (no bindings)
#   --revision SHA       Full 40-char commit SHA for verification
#   --discover URL       Discover products from an SPM repo (standalone mode)
#   --force              Overwrite existing library directory

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
TEMPLATE_DIR="$REPO_ROOT/templates/library"

die() { echo "Error: $*" >&2; exit 1; }

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
[ -n "$REPO_URL" ] || die "--repo is required"
[ -n "$VERSION" ] || die "--version is required"
[ -n "$MODE" ] || die "--mode is required"

case "$MODE" in
    source|binary) ;;
    *) die "--mode must be 'source' or 'binary'" ;;
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

config = {
    'repository': '$REPO_URL',
    'version': '$VERSION',
    'mode': '$MODE',
    'minIOS': '$MIN_IOS',
    'products': []
}

revision = '$REVISION'
if revision:
    config['revision'] = revision

products_str = '$PRODUCTS'
scheme = '$SCHEME'
mode = '$MODE'
is_multi = $( [ "$IS_MULTI" = true ] && echo "True" || echo "False" )
internal_str = '$INTERNAL_PRODUCTS'
internal_set = set(n.strip() for n in internal_str.split(',') if n.strip()) if internal_str else set()

if products_str:
    for name in products_str.split(','):
        name = name.strip()
        product = {'framework': name}
        if mode == 'source':
            product['scheme'] = name
        if is_multi:
            product['subdirectory'] = name
        if name in internal_set:
            product['internal'] = True
        config['products'].append(product)
elif scheme:
    config['products'].append({
        'scheme': scheme,
        'framework': scheme
    })

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

    # Swift.{Module}.csproj
    sed -e "s/{{MODULE_NAME}}/$MODULE_NAME/g" \
        -e "s/{{MODULE_NAME_LOWER}}/$MODULE_NAME_LOWER/g" \
        -e "s/{{VERSION}}/$VERSION/g" \
        "$TEMPLATE_DIR/Swift.__MODULE_NAME__.csproj.template" > "$PRODUCT_DIR/Swift.${MODULE_NAME}.csproj"

    # README.md
    sed -e "s|{{MODULE_NAME}}|$MODULE_NAME|g" \
        -e "s|{{REPOSITORY}}|$REPO_URL|g" \
        "$TEMPLATE_DIR/README.md.template" > "$PRODUCT_DIR/README.md"
done

# ── Summary ──────────────────────────────────────────────────────────────────

echo ""
echo "Created libraries/$LIBRARY_NAME/ with:"
echo "  - library.json"
echo "  - build-xcframework.sh (thin wrapper)"
for product_name in "${PRODUCT_LIST[@]}"; do
    product_name=$(echo "$product_name" | xargs)
    if is_internal "$product_name"; then
        if [ "$IS_MULTI" = true ]; then
            echo "  - $product_name/ (internal — no bindings)"
        fi
    elif [ "$IS_MULTI" = true ]; then
        echo "  - $product_name/"
        echo "    - Swift.${product_name}.csproj"
        echo "    - README.md"
    else
        echo "  - Swift.${product_name}.csproj"
        echo "  - README.md"
    fi
done
echo ""
echo "Next steps:"
echo "  1. Build xcframeworks: cd libraries/$LIBRARY_NAME && ./build-xcframework.sh$([ "$IS_MULTI" = true ] && echo " --all-products")"
echo "  2. Scaffold tests: ./scripts/new-sim-test.sh $LIBRARY_NAME$([ "$IS_MULTI" = true ] && echo " --all-products")"
echo "  3. Build test app:    cd tests/$LIBRARY_NAME.SimTests && ./build-testapp.sh"
echo "     (The SDK csproj generates bindings automatically during build)"
