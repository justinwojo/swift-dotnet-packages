#!/bin/bash
# Build Nuke test app
# Usage: ./build-testapp.sh [--device] [--aot]
#   No flags      = simulator build (Mono JIT, Debug)
#   --device      = device build (Mono AOT, Debug, for quick iteration)
#   --device --aot = device build (NativeAOT, Release, for release validation)
set -euo pipefail
cd "$(dirname "$0")"

AOT=false
DEVICE=false
for arg in "$@"; do
    case "$arg" in
        --device) DEVICE=true ;;
        --aot) AOT=true ;;
    esac
done

if [[ "$DEVICE" == true && "$AOT" == true ]]; then
    echo "=== Building Nuke tests for device (NativeAOT) ==="
    dotnet publish . -c Release -r ios-arm64 \
        -p:PublishAot=true \
        -p:PublishAotUsingRuntimePack=true \
        -p:CodesignKey="Apple Development: Justin Wojciechowski (KBKS29A36Q)" \
        -p:CodesignProvision="Wildcard Dev" \
        -p:TeamIdentifierPrefix=TL2K6QUQEH
elif [[ "$DEVICE" == true ]]; then
    echo "=== Building Nuke tests for device ==="
    dotnet build . -c Debug -p:RuntimeIdentifier=ios-arm64
else
    echo "=== Building Nuke tests for simulator ==="
    dotnet build . -c Debug
fi
echo "=== Build complete ==="
