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
# a vendor's artifactbundle. Delegates to spm-to-xcframework --binary, which
# resolves the tag via SPM and copies each matching xcframework out of
# .build/artifacts. `useTarget` is source-mode-only and not honored here —
# binary artifacts are always products.
#
# Revision pinning note: the upstream tool only verifies --revision in its
# source-mode clone path. Binary mode resolves through SPM's resolver shim
# and never calls verify_revision, so forwarding --revision to the tool would
# silently degrade to "no verification". Until upstream extends verification
# into the binary path, the wrapper does its own git ls-remote check before
# the tool runs and refuses to call the tool on mismatch.

build_binary() {
    local tool
    tool=$("$ENSURE_SPM_TOOL")

    # Tag SHA verification — must run in the wrapper because the tool's
    # binary path doesn't call its own verify_revision (see comment above).
    if [ -n "$REVISION" ]; then
        echo "=== Verifying tag '$VERSION' resolves to $REVISION ==="
        local remote_sha
        remote_sha=$(git ls-remote "$REPO" \
            "refs/tags/$VERSION" "refs/tags/${VERSION}^{}" \
            "refs/tags/v$VERSION" "refs/tags/v${VERSION}^{}" 2>/dev/null \
            | tail -1 | awk '{print $1}')
        [ -n "$remote_sha" ] || die "Tag '$VERSION' (or 'v$VERSION') not found in $REPO"
        [ "$remote_sha" = "$REVISION" ] || die "Tag '$VERSION' resolves to $remote_sha, expected $REVISION"
        echo "Revision verified."
    fi

    local BUILD_DIR="$LIBRARY_DIR/.build-workspace"
    local OUTPUT_DIR="$BUILD_DIR/xcframeworks"
    rm -rf "$BUILD_DIR"
    mkdir -p "$OUTPUT_DIR"

    # Refuse to silently ignore the legacy `artifactPath` disambiguation
    # field. The new tool's binary planner dedupes by product name (first
    # match wins) and exposes no per-product path override. If a future
    # vendor needs this, the right fix is upstream — fail loudly here so
    # it can't ship a wrong-artifact build.
    local PRODUCT_FLAGS=()
    local idx framework artifact_path
    for idx in "${PRODUCT_INDICES[@]}"; do
        framework=$(json_product_field "$CONFIG" "$idx" framework)
        artifact_path=$(json_product_field "$CONFIG" "$idx" artifactPath "")
        [ -z "$artifact_path" ] || die "Product '$framework' sets 'artifactPath', which is no longer supported. The pinned spm-to-xcframework binary planner dedupes by product name and has no per-product path override. Remove the field or file an upstream feature request."
        PRODUCT_FLAGS+=(--product "$framework")
    done

    echo "=== Building binary-mode xcframeworks via spm-to-xcframework ==="
    "$tool" "$REPO" \
        --version "$VERSION" \
        --binary \
        --output "$OUTPUT_DIR" \
        --min-ios "$MIN_IOS" \
        "${PRODUCT_FLAGS[@]}"

    install_products "$OUTPUT_DIR"

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
