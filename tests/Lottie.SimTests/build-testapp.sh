#!/bin/bash
# Build Lottie simulator test app
set -euo pipefail
cd "$(dirname "$0")"
echo "=== Building Lottie simulator tests ==="
dotnet build . -c Debug
echo "=== Build complete ==="
