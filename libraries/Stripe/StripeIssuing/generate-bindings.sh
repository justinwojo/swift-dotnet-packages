#!/bin/bash
# Generate C# bindings from StripeIssuing xcframework
#
# Prerequisites:
#   - StripeIssuing.xcframework must exist (run build-xcframework.sh first)
#   - swift-bindings generator must be available

set -euo pipefail

cd "$(dirname "$0")"

# Path to the swift-bindings generator
GENERATOR_PROJECT="${SWIFT_BINDINGS_ROOT:-../../../swift-bindings}/src/Swift.Bindings/src"

if [ ! -d "StripeIssuing.xcframework" ]; then
  echo "Error: StripeIssuing.xcframework not found. Run build-xcframework.sh first."
  exit 1
fi

echo "=== Generating StripeIssuing bindings ==="
dotnet run --project "$GENERATOR_PROJECT" -- \
  --xcframework StripeIssuing.xcframework \
  -o output/

echo "=== Bindings generated ==="
ls -la output/
