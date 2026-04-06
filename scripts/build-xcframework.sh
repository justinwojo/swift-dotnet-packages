#!/bin/bash
# Shared SPM-aware xcframework build script.
#
# Reads library.json from a library directory and produces xcframeworks. For
# source and binary modes, delegates the actual build to the pinned
# spm-to-xcframework tool in .tools/. Manual mode is a verification-only path
# used for proprietary xcframeworks that must be provisioned out-of-band.
#
# Usage: scripts/build-xcframework.sh <library-dir> [--products P1,P2] [--all-products] [--resolve-products]
#
# Modes (set in library.json):
#   source  — Clone repo, build from source with spm-to-xcframework
#   binary  — Use spm-to-xcframework --binary to download pre-built xcframeworks
#   manual  — Verify xcframeworks exist at expected locations (no build)
#
# Product selection:
#   Single product in config (no flags needed)  — builds it automatically
#   Multiple products + no flags                — fails with helpful error
#   --products P1,P2                            — builds specified subset
#   --all-products                              — builds everything in config
#   --resolve-products                          — dry-run: prints subdirectory|csproj pairs

set -euo pipefail

# ── Helpers ──────────────────────────────────────────────────────────────────

source "$(dirname "$0")/lib.sh"

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
ENSURE_SPM_TOOL="$REPO_ROOT/scripts/ensure-spm-to-xcframework.sh"

# ── Argument Parsing ─────────────────────────────────────────────────────────

LIBRARY_DIR=""
REQUESTED_PRODUCTS=""
ALL_PRODUCTS=false
RESOLVE_ONLY=false

while [[ $# -gt 0 ]]; do
    case "$1" in
        --products)
            REQUESTED_PRODUCTS="$2"
            shift 2
            ;;
        --all-products)
            ALL_PRODUCTS=true
            shift
            ;;
        --resolve-products)
            RESOLVE_ONLY=true
            shift
            ;;
        -*)
            die "Unknown option '$1'"
            ;;
        *)
            if [ -z "$LIBRARY_DIR" ]; then
                LIBRARY_DIR="$1"
            else
                die "Unexpected argument '$1'"
            fi
            shift
            ;;
    esac
done

[ -n "$LIBRARY_DIR" ] || die "Usage: $0 <library-dir> [--products P1,P2] [--all-products] [--resolve-products]"

# Resolve to absolute path
LIBRARY_DIR=$(cd "$LIBRARY_DIR" && pwd)

CONFIG="$LIBRARY_DIR/library.json"
[ -f "$CONFIG" ] || die "library.json not found in $LIBRARY_DIR"

# ── Read Config ──────────────────────────────────────────────────────────────

MODE=$(json_field "$CONFIG" mode)
MIN_IOS=$(json_field "$CONFIG" minIOS "15.0")
PRODUCT_COUNT=$(json_array_len "$CONFIG" products)

[ -n "$MODE" ] || die "mode is required in library.json"
[ "$PRODUCT_COUNT" -gt 0 ] || die "products array is empty in library.json"

# repository/version are required for source and binary modes, not for manual
REPO=$(json_field "$CONFIG" repository "")
VERSION=$(json_field "$CONFIG" version "")
REVISION=$(json_field "$CONFIG" revision "")

case "$MODE" in
    source|binary)
        [ -n "$REPO" ] || die "repository is required in library.json for $MODE mode"
        [ -n "$VERSION" ] || die "version is required in library.json for $MODE mode"
        ;;
    manual)
        ;;
    *)
        die "Unknown mode '$MODE'. Must be 'source', 'binary', or 'manual'."
        ;;
esac

# ── Resolve Product List ─────────────────────────────────────────────────────

# Build array of product indices to process
PRODUCT_INDICES=()

if [ -n "$REQUESTED_PRODUCTS" ]; then
    # --products P1,P2: find indices of requested products
    IFS=',' read -ra REQ_NAMES <<< "$REQUESTED_PRODUCTS"
    ALL_NAMES=$(json_product_names "$CONFIG")
    for req in "${REQ_NAMES[@]}"; do
        found=false
        idx=0
        while IFS= read -r name; do
            if [ "$name" = "$req" ]; then
                PRODUCT_INDICES+=("$idx")
                found=true
                break
            fi
            ((idx++))
        done <<< "$ALL_NAMES"
        [ "$found" = true ] || die "Product '$req' not found in library.json. Available: $(echo "$ALL_NAMES" | tr '\n' ', ' | sed 's/,$//')"
    done
elif [ "$ALL_PRODUCTS" = true ]; then
    # --all-products: build everything
    for ((i=0; i<PRODUCT_COUNT; i++)); do
        PRODUCT_INDICES+=("$i")
    done
elif [ "$PRODUCT_COUNT" -eq 1 ]; then
    # Single product: auto-select
    PRODUCT_INDICES=(0)
else
    # Multiple products, no selection flag: fail with helpful message
    echo "Error: library.json has $PRODUCT_COUNT products. Specify which to build:" >&2
    echo "" >&2
    echo "Available products:" >&2
    json_product_names "$CONFIG" | while IFS= read -r name; do
        echo "  - $name" >&2
    done
    echo "" >&2
    echo "Use --products P1,P2 to select specific products, or --all-products for all." >&2
    exit 1
fi

# ── Resolve Products Mode ───────────────────────────────────────────────────

if [ "$RESOLVE_ONLY" = true ]; then
    # Print subdirectory|csproj pairs for CI consumption
    # Skip internal products (no bindings, no csproj)
    for idx in "${PRODUCT_INDICES[@]}"; do
        if [ "$(json_product_bool "$CONFIG" "$idx" internal)" = "true" ]; then
            continue
        fi
        subdir=$(json_product_field "$CONFIG" "$idx" subdirectory "")
        if [ -n "$subdir" ]; then
            subdir_path="$LIBRARY_DIR/$subdir"
        else
            subdir_path="$LIBRARY_DIR"
        fi
        # Discover the actual csproj on disk via the shared helper (fails on 0 or >1 matches)
        csproj_file=$(discover_single_csproj "$subdir_path")
        csproj_name=$(basename "$csproj_file")
        if [ -n "$subdir" ]; then
            echo "${subdir}|${csproj_name}"
        else
            echo "|${csproj_name}"
        fi
    done
    exit 0
fi

# ── Install xcframework from tool output dir into library layout ────────────

# Moves $OUTPUT_DIR/<framework>.xcframework into the product's final location
# inside LIBRARY_DIR. Honors the optional `subdirectory` field in library.json
# so multi-product vendors (e.g. Stripe) get their xcframeworks placed under
# libraries/Stripe/StripeCore/StripeCore.xcframework etc.
install_products() {
    local output_dir="$1"
    for idx in "${PRODUCT_INDICES[@]}"; do
        local framework subdir target_dir src dst
        framework=$(json_product_field "$CONFIG" "$idx" framework)
        subdir=$(json_product_field "$CONFIG" "$idx" subdirectory "")

        if [ -n "$subdir" ]; then
            target_dir="$LIBRARY_DIR/$subdir"
        else
            target_dir="$LIBRARY_DIR"
        fi

        src="$output_dir/${framework}.xcframework"
        dst="$target_dir/${framework}.xcframework"

        [ -d "$src" ] || die "Expected $src after build, not found. The tool did not produce this product."

        mkdir -p "$target_dir"
        rm -rf "$dst"
        mv "$src" "$dst"
        echo "Installed $dst"
    done
}

# ── Source Mode ──────────────────────────────────────────────────────────────

# Walks selected product indices and emits --product/--target flags for
# spm-to-xcframework. The `useTarget` field opts into the tool's SPM-target
# escape hatch (frameworks shipped as .target() rather than .library() —
# e.g. most of Stripe's modules). Inlined because bash 3.2 (macOS default)
# doesn't support `local -n` namerefs for returning an array.
build_source() {
    local tool
    tool=$("$ENSURE_SPM_TOOL")

    local BUILD_DIR="$LIBRARY_DIR/.build-workspace"
    local OUTPUT_DIR="$BUILD_DIR/xcframeworks"
    rm -rf "$BUILD_DIR"
    mkdir -p "$OUTPUT_DIR"

    local PRODUCT_FLAGS=()
    local idx framework use_target
    for idx in "${PRODUCT_INDICES[@]}"; do
        framework=$(json_product_field "$CONFIG" "$idx" framework)
        use_target=$(json_product_bool "$CONFIG" "$idx" useTarget)
        if [ "$use_target" = "true" ]; then
            PRODUCT_FLAGS+=(--target "$framework")
        else
            PRODUCT_FLAGS+=(--product "$framework")
        fi
    done

    local EXTRA_FLAGS=()
    [ -n "$REVISION" ] && EXTRA_FLAGS+=(--revision "$REVISION")

    echo "=== Building source-mode xcframeworks via spm-to-xcframework ==="
    "$tool" "$REPO" \
        --version "$VERSION" \
        --output "$OUTPUT_DIR" \
        --min-ios "$MIN_IOS" \
        "${PRODUCT_FLAGS[@]}" \
        ${EXTRA_FLAGS[@]+"${EXTRA_FLAGS[@]}"}

    install_products "$OUTPUT_DIR"

    rm -rf "$BUILD_DIR"
}

# ── Binary Mode ──────────────────────────────────────────────────────────────
#
# Binary mode downloads pre-built xcframeworks from an SPM package that wraps
# a vendor's artifactbundle. Ideally this would go through spm-to-xcframework
# --binary too, but upstream currently injects the resolved tag name verbatim
# into the SPM manifest's `exact:` field, which rejects v-prefixed tags
# (SPM exact: requires a bare semver). BlinkID pins to `v7.6.2`, so that path
# fails at manifest-evaluation time.
#
# Until the upstream tool learns to strip or redirect v-prefixes in binary
# mode, we drive SPM directly from here with a tiny resolver package. This is
# the same logic the script has used historically — it works for any
# binary-mode vendor package, not just BlinkID.

build_binary() {
    local BUILD_DIR="$LIBRARY_DIR/.build-workspace"

    rm -rf "$BUILD_DIR"
    mkdir -p "$BUILD_DIR/Sources"

    # Optional tag SHA verification — matches the source-mode path
    if [ -n "$REVISION" ]; then
        echo "=== Verifying tag resolves to $REVISION ==="
        local remote_sha
        remote_sha=$(git ls-remote "$REPO" "refs/tags/$VERSION" "refs/tags/${VERSION}^{}" "refs/tags/v$VERSION" "refs/tags/v${VERSION}^{}" 2>/dev/null | tail -1 | awk '{print $1}')
        [ -n "$remote_sha" ] || die "Tag '$VERSION' (or 'v$VERSION') not found in $REPO"
        [ "$remote_sha" = "$REVISION" ] || die "Tag '$VERSION' resolves to $remote_sha, expected $REVISION"
        echo "Revision verified."
    fi

    # Convert minIOS (e.g. "15.0") to SPM platform version (e.g. ".v15")
    local SPM_IOS_VER
    SPM_IOS_VER=$(python3 -c "print(f'.v{\"$MIN_IOS\".split(\".\")[0]}')")

    # Minimal resolver package: depends on the target repo exact: $VERSION,
    # which SPM resolves to whichever tag form the remote publishes (v-prefixed
    # or not). The dummy target keeps SPM happy.
    cat > "$BUILD_DIR/Package.swift" << SWIFT
// swift-tools-version:5.9
import PackageDescription
let package = Package(
    name: "Resolver",
    platforms: [.iOS($SPM_IOS_VER)],
    dependencies: [
        .package(url: "$REPO", exact: "$VERSION")
    ],
    targets: [.target(name: "Resolver", path: "Sources")]
)
SWIFT
    echo "// placeholder" > "$BUILD_DIR/Sources/Resolver.swift"

    echo "=== Resolving binary SPM artifacts ==="
    (cd "$BUILD_DIR" && swift package resolve)

    # SPM deposits binary xcframeworks under .build/artifacts. Locate each
    # requested product and copy it into place. Skip __MACOSX resource-fork
    # directories that some artifactbundles ship with.
    local ARTIFACTS_DIR="$BUILD_DIR/.build/artifacts"

    for idx in "${PRODUCT_INDICES[@]}"; do
        local framework subdir output_dir artifact_path found_xcframework
        framework=$(json_product_field "$CONFIG" "$idx" framework)
        subdir=$(json_product_field "$CONFIG" "$idx" subdirectory "")
        artifact_path=$(json_product_field "$CONFIG" "$idx" artifactPath "")

        if [ -n "$subdir" ]; then
            output_dir="$LIBRARY_DIR/$subdir"
        else
            output_dir="$LIBRARY_DIR"
        fi
        mkdir -p "$output_dir"

        local output_xcframework="$output_dir/${framework}.xcframework"
        rm -rf "$output_xcframework"

        if [ -n "$artifact_path" ]; then
            found_xcframework="$ARTIFACTS_DIR/$artifact_path"
            [ -d "$found_xcframework" ] || die "Artifact not found at specified path: $found_xcframework"
        else
            local matches match_count
            matches=$(find "$ARTIFACTS_DIR" -name "__MACOSX" -prune -o -name "${framework}.xcframework" -type d -print 2>/dev/null || true)
            match_count=$(echo "$matches" | grep -c . 2>/dev/null || echo 0)
            if [ "$match_count" -eq 0 ] || [ -z "$matches" ]; then
                echo "Error: ${framework}.xcframework not found in artifacts." >&2
                echo "Contents of $ARTIFACTS_DIR:" >&2
                find "$ARTIFACTS_DIR" -name "*.xcframework" -type d 2>/dev/null | sed 's/^/  /' >&2
                exit 1
            elif [ "$match_count" -gt 1 ]; then
                echo "Error: Multiple ${framework}.xcframework matches found:" >&2
                echo "$matches" | sed 's/^/  /' >&2
                echo "Use 'artifactPath' in library.json to disambiguate." >&2
                exit 1
            fi
            found_xcframework=$(echo "$matches" | head -1)
        fi

        cp -R "$found_xcframework" "$output_xcframework"
        echo "Installed $output_xcframework"
    done

    rm -rf "$BUILD_DIR"
}

# ── Manual Mode ──────────────────────────────────────────────────────────────

# Manual mode is a verification-only path: the xcframework is provisioned
# out-of-band (for proprietary libraries like Mappedin that ship via a vendor
# portal). We never commit these artifacts, so the build pipeline just checks
# that they are present in the expected location on the local machine.
verify_manual() {
    local missing=()
    for idx in "${PRODUCT_INDICES[@]}"; do
        local framework subdir xcfw_path
        framework=$(json_product_field "$CONFIG" "$idx" framework)
        subdir=$(json_product_field "$CONFIG" "$idx" subdirectory "")
        if [ -n "$subdir" ]; then
            xcfw_path="$LIBRARY_DIR/$subdir/${framework}.xcframework"
        else
            xcfw_path="$LIBRARY_DIR/${framework}.xcframework"
        fi
        if [ -d "$xcfw_path" ]; then
            echo "Manual xcframework present: $xcfw_path"
        else
            missing+=("$xcfw_path")
        fi
    done
    if [ ${#missing[@]} -gt 0 ]; then
        echo "" >&2
        echo "Error: manual-mode library requires these xcframeworks to be provisioned:" >&2
        for p in "${missing[@]}"; do
            echo "  - $p" >&2
        done
        echo "" >&2
        echo "Manual-mode xcframeworks are proprietary artifacts and are not committed" >&2
        echo "to the repo. Download them from the vendor portal and place them at the" >&2
        echo "paths above before running the build." >&2
        exit 1
    fi
}

# ── Main ─────────────────────────────────────────────────────────────────────

case "$MODE" in
    source)
        build_source
        ;;
    binary)
        build_binary
        ;;
    manual)
        verify_manual
        ;;
esac

echo "=== Build complete ==="
