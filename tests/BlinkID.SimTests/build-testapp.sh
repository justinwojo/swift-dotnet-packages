#!/bin/bash
set -euo pipefail
exec "$(dirname "$0")/../../scripts/build-testapp.sh" "$(dirname "$0")" "$@"
