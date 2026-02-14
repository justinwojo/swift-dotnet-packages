#!/bin/bash
# Build xcframework(s) from SPM source
set -euo pipefail
cd "$(dirname "$0")"
../../scripts/build-xcframework.sh . "$@"
