#!/bin/bash
# Build Nuke simulator test app
set -euo pipefail
cd "$(dirname "$0")"
echo "=== Building Nuke simulator tests ==="
dotnet build . -c Debug
echo "=== Build complete ==="
