#!/bin/bash
set -euo pipefail
exec "$(dirname "$0")/../../scripts/validate-device.sh" "$(dirname "$0")" "$@"
