#!/bin/bash
# Shared physical-device validation script.
#
# Usage: scripts/validate-device.sh <test-dir> [--aot] [timeout_seconds] [device_udid]
#
#   test-dir     Path to a tests/<Name>.SimTests directory.
#   --aot        Look in bin/Release/ (NativeAOT publish output) instead of bin/Debug/.
#   timeout      Test timeout in seconds (default: 30).
#   device_udid  Specific device UDID (default: auto-detect first connected device).
#                Use `xcrun devicectl list devices` to find available devices.
#
# APP_NAME and BUNDLE_ID are derived from the test directory basename:
#   tests/Foo.SimTests -> APP_NAME=FooSimTests
#                         BUNDLE_ID=com.swiftbindings.foosimtests

set -e

TEST_DIR="${1:-}"
[ -n "$TEST_DIR" ] || {
    echo "Usage: $0 <test-dir> [--aot] [timeout_seconds] [device_udid]" >&2
    exit 2
}
shift

[ -d "$TEST_DIR" ] || { echo "Error: test directory not found: $TEST_DIR" >&2; exit 2; }
TEST_DIR=$(cd "$TEST_DIR" && pwd)

AOT=false
POSITIONAL=()
for arg in "$@"; do
    case "$arg" in
        --aot) AOT=true ;;
        *) POSITIONAL+=("$arg") ;;
    esac
done

TIMEOUT=${POSITIONAL[0]:-30}
DEVICE=${POSITIONAL[1]:-}

TEST_DIR_NAME=$(basename "$TEST_DIR")
LIB_NAME="${TEST_DIR_NAME%.SimTests}"
if [ "$LIB_NAME" = "$TEST_DIR_NAME" ]; then
    echo "Error: test directory must end with '.SimTests' (got '$TEST_DIR_NAME')" >&2
    exit 2
fi

APP_NAME="${LIB_NAME}SimTests"
LIB_NAME_LOWER=$(echo "$LIB_NAME" | tr '[:upper:]' '[:lower:]')
BUNDLE_ID="com.swiftbindings.${LIB_NAME_LOWER}simtests"
CONFIG=$( [[ "$AOT" == true ]] && echo "Release" || echo "Debug" )
APP_PATH="bin/${CONFIG}/net10.0-ios/ios-arm64/${APP_NAME}.app"

cd "$TEST_DIR"

if [ ! -d "$APP_PATH" ]; then
    echo "Error: App not found at $APP_PATH"
    echo "Run ./build-testapp.sh --device$([ "$AOT" = true ] && echo " --aot") first."
    exit 1
fi

# Auto-detect device if not specified
if [ -z "$DEVICE" ]; then
    TMPJSON=$(mktemp)
    xcrun devicectl list devices --json-output "$TMPJSON" 2>/dev/null
    DEVICE=$(python3 -c "
import json, sys
data = json.load(open('$TMPJSON'))
for d in data.get('result', {}).get('devices', []):
    if d.get('connectionProperties', {}).get('transportType') == 'wired':
        print(d['identifier'])
        sys.exit(0)
for d in data.get('result', {}).get('devices', []):
    if d.get('connectionProperties', {}).get('transportType') == 'localNetwork':
        print(d['identifier'])
        sys.exit(0)
sys.exit(1)
" 2>/dev/null) || true
    rm -f "$TMPJSON"

    if [ -z "$DEVICE" ]; then
        echo "Error: No connected device found."
        echo "Connect a device and try again, or specify a device UDID."
        echo "Available devices: xcrun devicectl list devices"
        exit 1
    fi
    echo "Auto-detected device: $DEVICE"
fi

# Install
echo "Installing ${APP_NAME} on device ${DEVICE}..."
xcrun devicectl device install app --device "$DEVICE" "$APP_PATH" 2>&1

# Launch and capture output
echo "Launching (timeout: ${TIMEOUT}s)..."
OUTPUT_FILE=$(mktemp)
xcrun devicectl device process launch --device "$DEVICE" --terminate-existing --console "$BUNDLE_ID" > "$OUTPUT_FILE" 2>&1 &
PID=$!

# Poll for completion (success, failure, crash, or app exit)
START_TIME=$(date +%s)
RESULT=""
while true; do
    CURRENT_TIME=$(date +%s)
    ELAPSED=$((CURRENT_TIME - START_TIME))
    if [ $ELAPSED -ge $TIMEOUT ]; then
        break
    fi
    sleep 1
    if grep -q "TEST SUCCESS" "$OUTPUT_FILE" 2>/dev/null; then
        RESULT="success"
        break
    fi
    if grep -q "TEST FAILED" "$OUTPUT_FILE" 2>/dev/null; then
        RESULT="failed"
        break
    fi
    if grep -v '\[SKIP\]' "$OUTPUT_FILE" 2>/dev/null | grep -qE "SIGABRT|SIGSEGV|SIGBUS|Fatal error|EXC_BAD_ACCESS"; then
        RESULT="crash"
        break
    fi
    # Check if the process has exited (app terminated without markers)
    if ! kill -0 $PID 2>/dev/null; then
        sleep 1  # Brief pause to let output flush
        # Check again for success/failure after output flush
        if grep -q "TEST SUCCESS" "$OUTPUT_FILE" 2>/dev/null; then
            RESULT="success"
        elif grep -q "TEST FAILED" "$OUTPUT_FILE" 2>/dev/null; then
            RESULT="failed"
        elif grep -v '\[SKIP\]' "$OUTPUT_FILE" 2>/dev/null | grep -qE "SIGABRT|SIGSEGV|SIGBUS|Fatal error|EXC_BAD_ACCESS"; then
            RESULT="crash"
        else
            RESULT="exited"
        fi
        break
    fi
done

# Cleanup
kill $PID 2>/dev/null || true
wait $PID 2>/dev/null || true

# Results
echo "=== APP OUTPUT ==="
cat "$OUTPUT_FILE"
rm -f "$OUTPUT_FILE"

case "$RESULT" in
    success)
        echo "=== VALIDATION PASSED ==="
        exit 0
        ;;
    failed)
        echo "=== TEST FAILED ==="
        exit 1
        ;;
    crash)
        echo "=== CRASH DETECTED ==="
        exit 1
        ;;
    exited)
        echo "=== APP EXITED (no success marker found) ==="
        exit 1
        ;;
    *)
        echo "=== TIMEOUT (${TIMEOUT}s) ==="
        exit 1
        ;;
esac
