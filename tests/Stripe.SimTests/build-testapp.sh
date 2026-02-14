#!/bin/bash
# Build Stripe simulator test app
set -euo pipefail
cd "$(dirname "$0")"
echo "=== Building Stripe simulator tests ==="
dotnet build . -c Debug
echo "=== Build complete ==="
