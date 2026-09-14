#!/bin/zsh
# Manual runner for the same native-first StoreKit Sandbox readiness control the Nuke harness uses.
# Usage: STOREKIT_SANDBOX_PRODUCT_ID=<exact id> ./run.sh ios|tvos [simulator UDID]
set -euo pipefail

here=${0:A:h}
platform=${1:-ios}
product_id=${STOREKIT_SANDBOX_PRODUCT_ID:-}
if [[ -z "$product_id" ]]; then
  echo "STOREKIT_SANDBOX_PRODUCT_ID is required; .storekit files cannot be activated through simctl" >&2
  exit 2
fi
if [[ ! "$product_id" =~ ^[A-Za-z0-9][A-Za-z0-9._-]{2,254}$ ]] \
   || [[ "${product_id:l}" == *nonexistent* ]] \
   || [[ "${product_id:l}" == *placeholder* ]]; then
  echo "STOREKIT_SANDBOX_PRODUCT_ID must be the exact App Store Connect product id; placeholders and .storekit paths are rejected" >&2
  exit 2
fi

case "$platform" in
  ios)
    sdk_name=iphonesimulator
    target=arm64-apple-ios16.0-simulator
    supported_platform=iPhoneSimulator
    device_family=1
    minimum_os=16.0
    runtime_pattern='iOS'
    ;;
  tvos)
    sdk_name=appletvsimulator
    target=arm64-apple-tvos16.0-simulator
    supported_platform=AppleTVSimulator
    device_family=3
    minimum_os=16.0
    runtime_pattern='tvOS'
    ;;
  *)
    echo "platform must be ios or tvos" >&2
    exit 2
    ;;
esac

udid=${2:-$(xcrun simctl list devices booted -j | python3 -c "import json,sys; d=json.load(sys.stdin); print(next(dev['udid'] for r,ds in d['devices'].items() if '$runtime_pattern' in r for dev in ds if dev['state']=='Booted'))")}
sdk=$(xcrun --sdk "$sdk_name" --show-sdk-path)
scratch=$(mktemp -d)
launch_pid=
watchdog_pid=
cleanup() {
  if [[ -n "$watchdog_pid" ]]; then
    kill "$watchdog_pid" 2>/dev/null || true
    wait "$watchdog_pid" 2>/dev/null || true
  fi
  if [[ -n "$launch_pid" ]]; then
    kill "$launch_pid" 2>/dev/null || true
    wait "$launch_pid" 2>/dev/null || true
  fi
  xcrun simctl terminate "$udid" com.swiftbindings.storekit2.tests >/dev/null 2>&1 || true
  rm -rf "$scratch"
}
trap cleanup EXIT INT TERM
out="$scratch/SKControl.app"
mkdir -p "$out"

repo_root=${here:h:h:h:h}
evidence_dir="$repo_root/artifacts/storekit-sandbox-readiness/manual-$(date -u +%Y%m%d-%H%M%S)-p$$-$platform"
mkdir -p "$evidence_dir"
launch_log="$evidence_dir/native-control.log"

xcrun swiftc -target "$target" -sdk "$sdk" -parse-as-library "$here/App.swift" -o "$out/SKControl"
sed -e "s|__BUNDLE_ID__|com.swiftbindings.storekit2.tests|g" \
    -e "s|__SUPPORTED_PLATFORM__|$supported_platform|g" \
    -e "s|__PLATFORM_NAME__|$sdk_name|g" \
    -e "s|__MINIMUM_OS__|$minimum_os|g" \
    -e "s|__DEVICE_FAMILY__|$device_family|g" \
    "$here/SKControl-Info.plist" > "$out/Info.plist"
codesign --force --sign - "$out" 2>/dev/null

token=$(uuidgen | tr -d '-' | tr '[:upper:]' '[:lower:]')
xcrun simctl install "$udid" "$out"
xcrun simctl launch --console --terminate-running-process "$udid" com.swiftbindings.storekit2.tests \
  --storekit-platform "$platform" \
  --storekit-bundle-id com.swiftbindings.storekit2.tests \
  --storekit-sandbox-product-id "$product_id" \
  --storekit-sandbox-run-token "$token" >"$launch_log" 2>&1 &
launch_pid=$!
(
  sleep 75
  if kill -0 "$launch_pid" 2>/dev/null; then
    echo "StoreKit native control exceeded the 75s manual-run deadline" >&2
    kill -TERM "$launch_pid" 2>/dev/null || true
  fi
) &
watchdog_pid=$!

set +e
wait "$launch_pid"
launch_status=$?
set -e
launch_pid=
kill "$watchdog_pid" 2>/dev/null || true
wait "$watchdog_pid" 2>/dev/null || true
watchdog_pid=

cat "$launch_log"
if (( launch_status != 0 )); then
  echo "StoreKit native control failed with exit $launch_status; evidence retained at $evidence_dir" >&2
  exit "$launch_status"
fi

ready_count=$(grep -c '^\[SK-CONTROL\] READY ' "$launch_log" || true)
success_count=$(grep -c '^\[SK-CONTROL\] TEST SUCCESS$' "$launch_log" || true)
if [[ "$ready_count" != 1 ]] \
   || [[ "$success_count" != 1 ]] \
   || ! grep -F "platform=$platform bundle=com.swiftbindings.storekit2.tests product=$product_id token=$token appTransaction=sandbox products=1 " "$launch_log" >/dev/null; then
  echo "StoreKit native control emitted no unique matching Sandbox receipt; evidence retained at $evidence_dir" >&2
  exit 2
fi

receipt_hash=$(shasum -a 256 "$launch_log" | awk '{print $1}')
print -r -- "platform=$platform
simulator_udid=$udid
bundle_id=com.swiftbindings.storekit2.tests
product_id=$product_id
run_token=$token
native_control_sha256=$receipt_hash" >"$evidence_dir/summary.txt"
echo "StoreKit native receipt retained at $evidence_dir"
