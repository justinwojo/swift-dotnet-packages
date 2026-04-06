#!/bin/bash
# Shared iOS Simulator validation script.
#
# Usage: scripts/validate-sim.sh <test-dir> [timeout_seconds] [device_udid]
#
#   test-dir     Path to a tests/<Name>.SimTests directory.
#   timeout      Test timeout in seconds (default: 15).
#   device_udid  Specific simulator UDID (default: "booted").
#                In CI, pass the resolved UDID to avoid ambiguity with multiple
#                booted sims. Locally, omit to target whichever sim is booted.
#
# APP_NAME and BUNDLE_ID are derived from the test directory basename:
#   tests/Foo.SimTests -> APP_NAME=FooSimTests
#                         BUNDLE_ID=com.swiftbindings.foosimtests

set -e

TEST_DIR="${1:-}"
[ -n "$TEST_DIR" ] || {
    echo "Usage: $0 <test-dir> [timeout_seconds] [device_udid]" >&2
    exit 2
}
shift

[ -d "$TEST_DIR" ] || { echo "Error: test directory not found: $TEST_DIR" >&2; exit 2; }
TEST_DIR=$(cd "$TEST_DIR" && pwd)

TIMEOUT=${1:-15}
DEVICE=${2:-booted}

TEST_DIR_NAME=$(basename "$TEST_DIR")
LIB_NAME="${TEST_DIR_NAME%.SimTests}"
if [ "$LIB_NAME" = "$TEST_DIR_NAME" ]; then
    echo "Error: test directory must end with '.SimTests' (got '$TEST_DIR_NAME')" >&2
    exit 2
fi

APP_NAME="${LIB_NAME}SimTests"
LIB_NAME_LOWER=$(echo "$LIB_NAME" | tr '[:upper:]' '[:lower:]')
BUNDLE_ID="com.swiftbindings.${LIB_NAME_LOWER}simtests"
APP_PATH="bin/Debug/net10.0-ios/iossimulator-arm64/${APP_NAME}.app"
CRASH_LOG_DIR="$HOME/Library/Logs/DiagnosticReports"

cd "$TEST_DIR"

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

# Poll for success/failure/crash
ELAPSED=0
SUCCESS=false
while [ $ELAPSED -lt $TIMEOUT ]; do
    sleep 1
    ELAPSED=$((ELAPSED + 1))
    if grep -q "TEST SUCCESS" "$OUTPUT_FILE" 2>/dev/null; then
        SUCCESS=true
        break
    fi
    if grep -q "TEST FAILED" "$OUTPUT_FILE" 2>/dev/null; then
        echo "=== TEST FAILED ==="
        cat "$OUTPUT_FILE"
        rm -f "$OUTPUT_FILE"
        xcrun simctl terminate "$DEVICE" "$BUNDLE_ID" 2>/dev/null || true
        kill $PID 2>/dev/null || true
        exit 1
    fi
    if grep -v '\[SKIP\]' "$OUTPUT_FILE" 2>/dev/null | grep -qE "SIGABRT|SIGSEGV|SIGBUS|Fatal error|CRASH|EXC_BAD_ACCESS"; then
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
