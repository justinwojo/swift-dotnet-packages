#!/bin/zsh
# Builds the pure-Swift StoreKit control app, installs it on a booted iOS simulator through the same
# `simctl install` / `simctl launch --console` path the .NET test apps use, and prints its verdict
# lines. Usage: ./run.sh [<simulator UDID>]  (defaults to the first booted simulator).
set -euo pipefail

here=${0:A:h}
udid=${1:-$(xcrun simctl list devices booted -j | python3 -c 'import json,sys; d=json.load(sys.stdin); print(next(dev["udid"] for r in d["devices"].values() for dev in r if dev["state"]=="Booted"))')}
sdk=$(xcrun --sdk iphonesimulator --show-sdk-path)
out=$(mktemp -d)/SKControl.app
mkdir -p "$out"

xcrun swiftc -target arm64-apple-ios16.0-simulator -sdk "$sdk" -parse-as-library "$here/App.swift" -o "$out/SKControl"
cp "$here/SKControl-Info.plist" "$out/Info.plist"
codesign --force --sign - "$out" 2>/dev/null

xcrun simctl install "$udid" "$out"
echo "installed on $udid; running (the app exits itself after 150s at the latest)..."
xcrun simctl launch --console --terminate-running-process "$udid" com.swiftbindings.storekit2.control | grep --line-buffered -E 'verdict|resolved|start|done'
