#!/bin/bash
# Build GRDB test app
# Usage: ./build-testapp.sh [--device]
set -euo pipefail
cd "$(dirname "$0")"

if [[ "${1:-}" == "--device" ]]; then
    echo "=== Building GRDB tests for device ==="
    dotnet build . -c Debug -p:RuntimeIdentifier=ios-arm64
else
    echo "=== Building GRDB tests for simulator ==="
    dotnet build . -c Debug
fi
echo "=== Build complete ==="
