#!/usr/bin/env bash
# Nuke Build entry point.
#
# This file exists for two reasons:
#   1. The Nuke CLI (`dotnet nuke`, restored from .config/dotnet-tools.json)
#      uses build.sh / build.ps1 as discovery markers to locate the build
#      project. Without one of these files in the repo root, `dotnet nuke`
#      cannot find _build.csproj and prompts for setup.
#   2. As a thin convenience wrapper for anyone who prefers `./build.sh
#      <Target>` over `dotnet nuke <Target>`. Both invocations are
#      equivalent — they end up running the same build/_build.csproj.
#
# Prerequisites: .NET SDK 10 (pinned in global.json) and `dotnet tool
# restore` already run once after cloning. CI sets both up explicitly.
set -eo pipefail
SCRIPT_DIR=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
dotnet run --project "$SCRIPT_DIR/build/_build.csproj" -- "$@"
