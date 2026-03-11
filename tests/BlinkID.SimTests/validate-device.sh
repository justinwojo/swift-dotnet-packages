#!/bin/bash
# Validates BlinkID tests on a physical iOS device
# Usage: ./validate-device.sh [timeout_seconds] [device_udid]
# Returns exit code 0 on success, 1 on failure/crash/timeout
#
# device_udid: specific device UDID (default: auto-detect first connected device)
#   Use `xcrun devicectl list devices` to find available devices.

set -e

TIMEOUT=${1:-30}
DEVICE=${2:-}
APP_NAME="BlinkIDSimTests"
APP_PATH="bin/Debug/net10.0-ios/ios-arm64/${APP_NAME}.app"
BUNDLE_ID="com.swiftbindings.blinkidsimtests"

cd "$(dirname "$0")"

if [ ! -d "$APP_PATH" ]; then
    echo "Error: App not found at $APP_PATH"
    echo "Run ./build-testapp.sh --device first."
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
ELAPSED=0
RESULT=""
while [ $ELAPSED -lt $TIMEOUT ]; do
    sleep 0.25
    ELAPSED=$((ELAPSED + 1))
    if grep -q "TEST SUCCESS" "$OUTPUT_FILE" 2>/dev/null; then
        RESULT="success"
        break
    fi
    if grep -q "TEST FAILED" "$OUTPUT_FILE" 2>/dev/null; then
        RESULT="failed"
        break
    fi
    if grep -qE "SIGABRT|SIGSEGV|SIGBUS|Fatal error|CRASH|EXC_BAD_ACCESS" "$OUTPUT_FILE" 2>/dev/null; then
        RESULT="crash"
        break
    fi
    # Check if the process has exited (app terminated without markers)
    if ! kill -0 $PID 2>/dev/null; then
        sleep 0.5  # Brief pause to let output flush
        RESULT="exited"
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
