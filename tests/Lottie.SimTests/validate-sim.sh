#!/bin/bash
# Validates Lottie simulator tests
# Usage: ./validate-sim.sh [timeout_seconds] [device_udid]
# Returns exit code 0 on success, 1 on failure/crash/timeout
#
# device_udid: specific simulator UDID (default: "booted")
#   In CI, pass the resolved UDID to avoid ambiguity with multiple booted sims.
#   Locally, omit to target whichever simulator is booted.

set -e

TIMEOUT=${1:-15}
DEVICE=${2:-booted}
APP_NAME="LottieSimTests"
APP_PATH="bin/Debug/net10.0-ios/iossimulator-arm64/${APP_NAME}.app"
BUNDLE_ID="com.swiftbindings.lottiesimtests"
CRASH_LOG_DIR="$HOME/Library/Logs/DiagnosticReports"

cd "$(dirname "$0")"

if [ ! -d "$APP_PATH" ]; then
    echo "Error: App not found at $APP_PATH"
    echo "Run ./build-testapp.sh first."
    exit 1
fi

# Record crash log count before
BEFORE_CRASH_COUNT=$(ls -1 "$CRASH_LOG_DIR"/${APP_NAME}*.ips 2>/dev/null | wc -l || echo 0)

# Install + launch (use explicit UDID to avoid ambiguity)
echo "Installing ${APP_NAME} on device ${DEVICE}..."
xcrun simctl install "$DEVICE" "$APP_PATH"

echo "Launching (timeout: ${TIMEOUT}s)..."
OUTPUT_FILE=$(mktemp)
xcrun simctl launch --console --terminate-running-process "$DEVICE" "$BUNDLE_ID" > "$OUTPUT_FILE" 2>&1 &
PID=$!

# Poll for success/crash
ELAPSED=0
SUCCESS=false
while [ $ELAPSED -lt $TIMEOUT ]; do
    sleep 1
    ELAPSED=$((ELAPSED + 1))
    if grep -q "TEST SUCCESS" "$OUTPUT_FILE" 2>/dev/null; then
        SUCCESS=true
        break
    fi
    if grep -qE "SIGABRT|SIGSEGV|SIGBUS|Fatal error|CRASH|EXC_BAD_ACCESS" "$OUTPUT_FILE" 2>/dev/null; then
        echo "=== CRASH DETECTED ==="
        cat "$OUTPUT_FILE"
        rm -f "$OUTPUT_FILE"
        kill $PID 2>/dev/null || true
        exit 1
    fi
done

# Cleanup
xcrun simctl terminate "$DEVICE" "$BUNDLE_ID" 2>/dev/null || true
kill $PID 2>/dev/null || true

# Check crash logs
AFTER_CRASH_COUNT=$(ls -1 "$CRASH_LOG_DIR"/${APP_NAME}*.ips 2>/dev/null | wc -l || echo 0)
if [ "$AFTER_CRASH_COUNT" -gt "$BEFORE_CRASH_COUNT" ]; then
    echo "=== CRASH LOG DETECTED ==="
    ls -t "$CRASH_LOG_DIR"/${APP_NAME}*.ips | head -1 | xargs head -50
    cat "$OUTPUT_FILE"
    rm -f "$OUTPUT_FILE"
    exit 1
fi

# Results
echo "=== APP OUTPUT ==="
cat "$OUTPUT_FILE"
rm -f "$OUTPUT_FILE"

if [ "$SUCCESS" = true ]; then
    echo "=== VALIDATION PASSED ==="
    exit 0
else
    echo "=== TIMEOUT (no success marker found) ==="
    exit 1
fi
