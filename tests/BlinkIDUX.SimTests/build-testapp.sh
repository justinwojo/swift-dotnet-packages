#!/bin/bash
set -euo pipefail
cd "$(dirname "$0")"
echo "=== Building BlinkIDUX simulator tests ==="
dotnet build . -c Debug
echo "=== Build complete ==="
