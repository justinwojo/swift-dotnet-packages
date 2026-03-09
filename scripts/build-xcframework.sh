#!/bin/bash
# Shared SPM-aware xcframework build script.
# Reads library.json from a library directory and builds xcframeworks.
#
# Usage: scripts/build-xcframework.sh <library-dir> [--products P1,P2] [--all-products] [--resolve-products]
#
# Modes:
#   source  — Clone repo, build from source with xcodebuild
#   binary  — Use swift package resolve to download pre-built xcframeworks
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

REPO=$(json_field "$CONFIG" repository)
VERSION=$(json_field "$CONFIG" version)
TAG=$(json_field "$CONFIG" tag "$VERSION")   # git tag — defaults to version if not set
REVISION=$(json_field "$CONFIG" revision "")
MODE=$(json_field "$CONFIG" mode)
MIN_IOS=$(json_field "$CONFIG" minIOS "15.0")
PRODUCT_COUNT=$(json_array_len "$CONFIG" products)

[ -n "$REPO" ] || die "repository is required in library.json"
[ -n "$VERSION" ] || die "version is required in library.json"
[ -n "$MODE" ] || die "mode is required in library.json"
[ "$PRODUCT_COUNT" -gt 0 ] || die "products array is empty in library.json"

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
        framework=$(json_product_field "$CONFIG" "$idx" framework)
        module=$(json_product_field "$CONFIG" "$idx" module "$framework")
        subdir=$(json_product_field "$CONFIG" "$idx" subdirectory "")
        if [ -n "$subdir" ]; then
            echo "${subdir}|Swift.${module}.csproj"
        else
            echo "|Swift.${module}.csproj"
        fi
    done
    exit 0
fi

# ── Shared Verification ──────────────────────────────────────────────────────

verify_revision() {
    if [ -n "$REVISION" ]; then
        echo "=== Verifying tag $TAG resolves to $REVISION ==="
        REMOTE_SHA=$(git ls-remote "$REPO" "refs/tags/$TAG" "refs/tags/$TAG^{}" 2>/dev/null | tail -1 | awk '{print $1}')
        if [ -z "$REMOTE_SHA" ]; then
            die "Tag '$TAG' not found in $REPO"
        fi
        if [ "$REMOTE_SHA" != "$REVISION" ]; then
            die "Tag '$TAG' resolves to $REMOTE_SHA, expected $REVISION"
        fi
        echo "Revision verified."
    fi
}

# ── Source Mode ──────────────────────────────────────────────────────────────

build_source() {
    local BUILD_DIR="$LIBRARY_DIR/.build-workspace"
    local ARCHIVES_DIR="$BUILD_DIR/archives"
    local DERIVED_DATA="$BUILD_DIR/DerivedData"

    # Clean previous build
    rm -rf "$BUILD_DIR"
    mkdir -p "$BUILD_DIR"

    verify_revision

    echo "=== Cloning $REPO @ $TAG ==="
    git clone --depth 1 --branch "$TAG" "$REPO" "$BUILD_DIR/source"

    # Read buildSettings overrides from library.json
    local BUILD_SETTINGS=()
    while IFS= read -r line; do
        [ -n "$line" ] && BUILD_SETTINGS+=("$line")
    done < <(json_build_settings "$CONFIG")

    # Scheme preflight: verify all requested schemes exist
    echo "=== Verifying schemes ==="
    local AVAILABLE_SCHEMES
    AVAILABLE_SCHEMES=$(cd "$BUILD_DIR/source" && xcodebuild -list -json 2>/dev/null | python3 -c "
import json, sys
try:
    data = json.load(sys.stdin)
    # Handle both workspace and project formats
    if 'workspace' in data:
        schemes = data['workspace'].get('schemes', [])
    elif 'project' in data:
        schemes = data['project'].get('schemes', [])
    else:
        schemes = []
    for s in schemes:
        print(s)
except:
    pass
") || true

    for idx in "${PRODUCT_INDICES[@]}"; do
        local scheme
        scheme=$(json_product_field "$CONFIG" "$idx" scheme "")
        [ -n "$scheme" ] || die "Product at index $idx missing 'scheme' field (required for source mode)"

        if [ -n "$AVAILABLE_SCHEMES" ] && ! echo "$AVAILABLE_SCHEMES" | grep -qx "$scheme"; then
            echo "Error: Scheme '$scheme' not found." >&2
            echo "Available schemes:" >&2
            echo "$AVAILABLE_SCHEMES" | sed 's/^/  - /' >&2
            exit 1
        fi
    done

    # Build each product
    for idx in "${PRODUCT_INDICES[@]}"; do
        local scheme framework module subdir output_dir
        scheme=$(json_product_field "$CONFIG" "$idx" scheme)
        framework=$(json_product_field "$CONFIG" "$idx" framework)
        module=$(json_product_field "$CONFIG" "$idx" module "$framework")
        subdir=$(json_product_field "$CONFIG" "$idx" subdirectory "")

        if [ -n "$subdir" ]; then
            output_dir="$LIBRARY_DIR/$subdir"
        else
            output_dir="$LIBRARY_DIR"
        fi

        local output_xcframework="$output_dir/${framework}.xcframework"
        rm -rf "$output_xcframework"
        mkdir -p "$output_dir"

        echo "=== Building $framework (scheme: $scheme) for iOS device ==="
        (cd "$BUILD_DIR/source" && xcodebuild archive \
            -scheme "$scheme" \
            -destination "generic/platform=iOS" \
            -archivePath "$ARCHIVES_DIR/${framework}-ios-arm64" \
            -derivedDataPath "$DERIVED_DATA/device" \
            BUILD_LIBRARY_FOR_DISTRIBUTION=YES \
            SKIP_INSTALL=NO \
            MACH_O_TYPE=mh_dylib \
            IPHONEOS_DEPLOYMENT_TARGET="$MIN_IOS" \
            ${BUILD_SETTINGS[@]+"${BUILD_SETTINGS[@]}"} \
            -quiet)

        echo "=== Building $framework (scheme: $scheme) for iOS Simulator ==="
        (cd "$BUILD_DIR/source" && xcodebuild archive \
            -scheme "$scheme" \
            -destination "generic/platform=iOS Simulator" \
            -archivePath "$ARCHIVES_DIR/${framework}-ios-simulator" \
            -derivedDataPath "$DERIVED_DATA/simulator" \
            BUILD_LIBRARY_FOR_DISTRIBUTION=YES \
            SKIP_INSTALL=NO \
            MACH_O_TYPE=mh_dylib \
            IPHONEOS_DEPLOYMENT_TARGET="$MIN_IOS" \
            ${BUILD_SETTINGS[@]+"${BUILD_SETTINGS[@]}"} \
            -quiet)

        echo "=== Creating ${framework}.xcframework ==="
        # Find the framework in the archive — location varies by project type:
        #   Xcode projects: Products/Library/Frameworks/
        #   SPM packages:   Products/usr/local/lib/
        local device_fw simulator_fw
        device_fw=$(find "$ARCHIVES_DIR/${framework}-ios-arm64.xcarchive/Products" -name "${framework}.framework" -type d | head -1)
        simulator_fw=$(find "$ARCHIVES_DIR/${framework}-ios-simulator.xcarchive/Products" -name "${framework}.framework" -type d | head -1)
        [ -n "$device_fw" ] || die "${framework}.framework not found in device archive"
        [ -n "$simulator_fw" ] || die "${framework}.framework not found in simulator archive"

        # SPM dynamic library archives don't include Swift module interfaces in the
        # installed framework. Copy them from DerivedData intermediates if missing.
        local dd_variant
        for fw_path in "$device_fw" "$simulator_fw"; do
            # Match the DerivedData subdirectory to the framework slice
            if [ "$fw_path" = "$device_fw" ]; then dd_variant="device"; else dd_variant="simulator"; fi
            if [ ! -d "$fw_path/Modules/${framework}.swiftmodule" ]; then
                local swiftmod
                swiftmod=$(find "$DERIVED_DATA/$dd_variant" -path "*/ArchiveIntermediates/${scheme}/BuildProductsPath/*/${framework}.swiftmodule" -type d 2>/dev/null | head -1)
                if [ -n "$swiftmod" ]; then
                    echo "  Injecting Swift module interfaces into $(basename "$(dirname "$fw_path")")"
                    mkdir -p "$fw_path/Modules"
                    cp -R "$swiftmod" "$fw_path/Modules/"
                fi
            fi
        done

        xcodebuild -create-xcframework \
            -framework "$device_fw" \
            -framework "$simulator_fw" \
            -output "$output_xcframework"

        echo "=== ${framework}.xcframework built successfully ==="
        ls -la "$output_xcframework"
    done

    # Clean up
    rm -rf "$BUILD_DIR"
}

# ── Binary Mode ──────────────────────────────────────────────────────────────

build_binary() {
    local BUILD_DIR="$LIBRARY_DIR/.build-workspace"

    # Clean previous build
    rm -rf "$BUILD_DIR"
    mkdir -p "$BUILD_DIR/Sources"

    verify_revision

    # Convert minIOS (e.g. "15.0") to SPM platform version (e.g. ".v15")
    local SPM_IOS_VER
    SPM_IOS_VER=$(python3 -c "
v = '$MIN_IOS'
major = v.split('.')[0]
print(f'.v{major}')
")

    # Create a minimal Package.swift that depends on the target repo
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

    # Dummy source so SPM is happy
    echo "// placeholder" > "$BUILD_DIR/Sources/Resolver.swift"

    echo "=== Resolving SPM dependencies (binary mode) ==="
    (cd "$BUILD_DIR" && swift package resolve)

    # Find and copy each product's xcframework
    local ARTIFACTS_DIR="$BUILD_DIR/.build/artifacts"

    for idx in "${PRODUCT_INDICES[@]}"; do
        local framework module subdir output_dir artifact_path
        framework=$(json_product_field "$CONFIG" "$idx" framework)
        module=$(json_product_field "$CONFIG" "$idx" module "$framework")
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

        local found_xcframework=""
        if [ -n "$artifact_path" ]; then
            # Explicit path override
            found_xcframework="$ARTIFACTS_DIR/$artifact_path"
            [ -d "$found_xcframework" ] || die "Artifact not found at specified path: $found_xcframework"
        else
            # Search for the xcframework in artifacts (exclude __MACOSX resource fork dirs)
            local matches
            matches=$(find "$ARTIFACTS_DIR" -name "__MACOSX" -prune -o -name "${framework}.xcframework" -type d -print 2>/dev/null || true)
            local match_count
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

        echo "=== Copying ${framework}.xcframework ==="
        cp -R "$found_xcframework" "$output_xcframework"
        echo "=== ${framework}.xcframework copied successfully ==="
        ls -la "$output_xcframework"
    done

    # Clean up
    rm -rf "$BUILD_DIR"
}

# ── Main ─────────────────────────────────────────────────────────────────────

case "$MODE" in
    source)
        build_source
        ;;
    binary)
        build_binary
        ;;
    *)
        die "Unknown mode '$MODE'. Must be 'source' or 'binary'."
        ;;
esac

echo "=== Build complete ==="
