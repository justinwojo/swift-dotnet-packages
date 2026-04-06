#!/bin/bash
# Shared test-app build script.
#
# Usage: scripts/build-testapp.sh <test-dir> [--device] [--aot]
#
#   test-dir  Path to a tests/<Name>.SimTests directory.
#   --device  Build for physical device (ios-arm64) instead of the simulator.
#   --aot     Use NativeAOT publish (Release). Requires --device. Also requires
#             codesign environment variables (see below).
#
# Modes:
#   (no flags)       Simulator build  (iossimulator-arm64, Debug, Mono JIT)
#   --device         Device build     (ios-arm64,          Debug, Mono AOT)
#   --device --aot   Device build     (ios-arm64,          Release, NativeAOT)
#
# Codesign (required only for --device --aot):
#   CODESIGN_IDENTITY     e.g. "Apple Development: Name (TEAMID)"
#   PROVISIONING_PROFILE  e.g. "Wildcard Dev"
#   TEAM_ID               e.g. "TL2K6QUQEH"

set -euo pipefail

TEST_DIR="${1:-}"
[ -n "$TEST_DIR" ] || {
    echo "Usage: $0 <test-dir> [--device] [--aot]" >&2
    exit 2
}
shift

[ -d "$TEST_DIR" ] || { echo "Error: test directory not found: $TEST_DIR" >&2; exit 2; }
TEST_DIR=$(cd "$TEST_DIR" && pwd)

TEST_DIR_NAME=$(basename "$TEST_DIR")
LIB_NAME="${TEST_DIR_NAME%.SimTests}"
if [ "$LIB_NAME" = "$TEST_DIR_NAME" ]; then
    echo "Error: test directory must end with '.SimTests' (got '$TEST_DIR_NAME')" >&2
    exit 2
fi

AOT=false
DEVICE=false
for arg in "$@"; do
    case "$arg" in
        --device) DEVICE=true ;;
        --aot)    AOT=true ;;
        *) echo "Unknown option: $arg" >&2; exit 2 ;;
    esac
done

if [[ "$AOT" == true && "$DEVICE" != true ]]; then
    echo "Error: --aot requires --device" >&2
    exit 2
fi

cd "$TEST_DIR"

if [[ "$DEVICE" == true && "$AOT" == true ]]; then
    # NativeAOT release build needs codesigning from env vars
    missing=()
    [ -n "${CODESIGN_IDENTITY:-}" ]    || missing+=("CODESIGN_IDENTITY")
    [ -n "${PROVISIONING_PROFILE:-}" ] || missing+=("PROVISIONING_PROFILE")
    [ -n "${TEAM_ID:-}" ]              || missing+=("TEAM_ID")

    if [ ${#missing[@]} -gt 0 ]; then
        echo "Error: --aot --device requires the following environment variables:" >&2
        for var in "${missing[@]}"; do echo "  $var" >&2; done
        echo "" >&2
        echo "Example:" >&2
        echo "  export CODESIGN_IDENTITY=\"Apple Development: Name (TEAMID)\"" >&2
        echo "  export PROVISIONING_PROFILE=\"Wildcard Dev\"" >&2
        echo "  export TEAM_ID=\"TL2K6QUQEH\"" >&2
        exit 2
    fi

    echo "=== Building ${LIB_NAME} tests for device (NativeAOT) ==="
    dotnet publish . -c Release -r ios-arm64 \
        -p:PublishAot=true \
        -p:PublishAotUsingRuntimePack=true \
        -p:CodesignKey="$CODESIGN_IDENTITY" \
        -p:CodesignProvision="$PROVISIONING_PROFILE" \
        -p:TeamIdentifierPrefix="$TEAM_ID"
elif [[ "$DEVICE" == true ]]; then
    echo "=== Building ${LIB_NAME} tests for device ==="
    dotnet build . -c Debug -p:RuntimeIdentifier=ios-arm64
else
    echo "=== Building ${LIB_NAME} tests for simulator ==="
    dotnet build . -c Debug
fi
echo "=== Build complete ==="
