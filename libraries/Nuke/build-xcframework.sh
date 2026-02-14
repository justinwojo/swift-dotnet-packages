#!/bin/bash
# Build Nuke xcframework from Swift Package Manager source
#
# Clones Nuke from GitHub, builds for iOS device + simulator,
# and creates a universal xcframework.

set -euo pipefail

cd "$(dirname "$0")"

NUKE_VERSION="12.8.0"
NUKE_REPO="https://github.com/kean/Nuke.git"
MIN_IOS="15.0"

BUILD_DIR=".build-workspace"
ARCHIVES_DIR="$BUILD_DIR/archives"
OUTPUT="Nuke.xcframework"

# Clean previous build
rm -rf "$BUILD_DIR" "$OUTPUT"
mkdir -p "$BUILD_DIR"

echo "=== Cloning Nuke $NUKE_VERSION ==="
git clone --depth 1 --branch "$NUKE_VERSION" "$NUKE_REPO" "$BUILD_DIR/Nuke"

cd "$BUILD_DIR/Nuke"

echo "=== Building for iOS device (arm64) ==="
xcodebuild archive \
  -scheme Nuke \
  -destination "generic/platform=iOS" \
  -archivePath "../archives/ios-arm64" \
  BUILD_LIBRARY_FOR_DISTRIBUTION=YES \
  SKIP_INSTALL=NO \
  IPHONEOS_DEPLOYMENT_TARGET="$MIN_IOS" \
  -quiet

echo "=== Building for iOS Simulator (arm64 + x86_64) ==="
xcodebuild archive \
  -scheme Nuke \
  -destination "generic/platform=iOS Simulator" \
  -archivePath "../archives/ios-simulator" \
  BUILD_LIBRARY_FOR_DISTRIBUTION=YES \
  SKIP_INSTALL=NO \
  IPHONEOS_DEPLOYMENT_TARGET="$MIN_IOS" \
  -quiet

cd ../..

echo "=== Creating xcframework ==="
xcodebuild -create-xcframework \
  -framework "$ARCHIVES_DIR/ios-arm64.xcarchive/Products/Library/Frameworks/Nuke.framework" \
  -framework "$ARCHIVES_DIR/ios-simulator.xcarchive/Products/Library/Frameworks/Nuke.framework" \
  -output "$OUTPUT"

# Clean up build workspace
rm -rf "$BUILD_DIR"

echo "=== Nuke.xcframework built successfully ==="
ls -la "$OUTPUT"
