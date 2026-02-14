#!/bin/bash
# Generate C# bindings from StripePaymentSheet xcframework
#
# Prerequisites:
#   - StripePaymentSheet.xcframework must exist (run build-xcframework.sh first)
#   - swift-bindings generator must be available

set -euo pipefail

cd "$(dirname "$0")"

# Path to the swift-bindings generator
GENERATOR_PROJECT="${SWIFT_BINDINGS_ROOT:-../../../swift-bindings}/src/Swift.Bindings/src"

if [ ! -d "StripePaymentSheet.xcframework" ]; then
  echo "Error: StripePaymentSheet.xcframework not found. Run build-xcframework.sh first."
  exit 1
fi

echo "=== Generating StripePaymentSheet bindings ==="
dotnet run --project "$GENERATOR_PROJECT" -- \
  --xcframework StripePaymentSheet.xcframework \
  -o output/

echo "=== Bindings generated ==="
ls -la output/
