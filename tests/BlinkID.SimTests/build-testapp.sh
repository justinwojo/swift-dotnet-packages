#!/bin/bash
# Build BlinkID simulator test app
set -euo pipefail
cd "$(dirname "$0")"
echo "=== Building BlinkID simulator tests ==="
dotnet build . -c Debug
echo "=== Build complete ==="
