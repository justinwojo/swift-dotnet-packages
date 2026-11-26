#!/bin/bash
# Select one of the supported .NET + Xcode pairings (toolchains/<pairing>.json).
#
#   source scripts/use-toolchain.sh <pairing>          # this shell now builds for <pairing>
#   source scripts/use-toolchain.sh --reset            # restore global.json and clear the pairing
#   scripts/use-toolchain.sh <pairing> -- <cmd> [args] # run one command on <pairing>, then
#                                                      # restore global.json
#
# Pairings: net10-xcode26 (default when none is selected), net10-xcode27, net11-xcode27.
#
# The pairing pins the SDK and workload set exactly (global.json, roll-forward disabled),
# selects its dotnet root and Xcode (DOTNET_ROOT, PATH, DEVELOPER_DIR), and exports
# SwiftBindingsPairing, which Directory.Build.props turns into the target frameworks and
# copy labels and which the Nuke harness reads for paths, simulators and engines.
# SWIFTBINDINGS_NUGET_PACKAGES_ROOT, when set, gives each pairing a private package cache
# (NUGET_PACKAGES=<root>/<pairing>): the pairings restore the same package ids and versions,
# and one pairing's extraction must never serve another's restore.
# Clean bin/ and obj/ when switching pairings that share a TFM folder.

_sb_repo="$(cd "$(dirname "${BASH_SOURCE[0]:-$0}")/.." && pwd)"
_sb_global_backup="$_sb_repo/.global.json.use-toolchain"

_sb_restore_global() {
  if [ -f "$_sb_global_backup" ]; then
    mv -f "$_sb_global_backup" "$_sb_repo/global.json"
  fi
}

if [ "${1:-}" = "--reset" ]; then
  _sb_restore_global
  unset SwiftBindingsPairing SWIFTBINDINGS_SIM_RUNTIMES SWIFTBINDINGS_TV_SIM_RUNTIMES \
        DEVELOPER_DIR DOTNET_ROOT DOTNET_MULTILEVEL_LOOKUP
  echo "toolchain pairing cleared; open a new shell to restore PATH" >&2
  unset -f _sb_restore_global
  return 0 2>/dev/null || exit 0
fi

_sb_pairing="${1:-}"
_sb_json="$_sb_repo/toolchains/$_sb_pairing.json"
if [ -z "$_sb_pairing" ] || [ ! -f "$_sb_json" ]; then
  echo "usage: source scripts/use-toolchain.sh <net10-xcode26|net10-xcode27|net11-xcode27|--reset>" >&2
  echo "       scripts/use-toolchain.sh <pairing> -- <command> [args...]" >&2
  [ -n "$_sb_pairing" ] && echo "unknown pairing '$_sb_pairing' (see toolchains/)" >&2
  return 2 2>/dev/null || exit 2
fi

_sb_get() {
  python3 -c 'import json,os,sys
v = json.load(open(sys.argv[1]))[sys.argv[2]]
print(",".join(v) if isinstance(v, list) else os.path.expanduser(str(v)))' "$_sb_json" "$1"
}
_sb_sdk="$(_sb_get sdkVersion)"
_sb_workload="$(_sb_get workloadVersion)"
# CI runners install .NET and Xcode in their own places: SWIFTBINDINGS_DOTNET_ROOT and
# SWIFTBINDINGS_DEVELOPER_DIR override the recorded locations (the versions stay pinned).
_sb_root="${SWIFTBINDINGS_DOTNET_ROOT:-$(_sb_get dotnetRoot)}"
_sb_devdir="${SWIFTBINDINGS_DEVELOPER_DIR:-$(_sb_get developerDirectory)}"

_sb_fail() { echo "use-toolchain: $*" >&2; }
if [ "$(_sb_get pairing)" != "$_sb_pairing" ]; then
  _sb_fail "$_sb_json names pairing '$(_sb_get pairing)'"; return 1 2>/dev/null || exit 1
fi
if [ ! -x "$_sb_root/dotnet" ]; then
  _sb_fail "no dotnet at $_sb_root"; return 1 2>/dev/null || exit 1
fi
if [ ! -d "$_sb_root/sdk/$_sb_sdk" ]; then
  _sb_fail "SDK $_sb_sdk is not installed under $_sb_root"; return 1 2>/dev/null || exit 1
fi
if [ ! -d "$_sb_devdir" ]; then
  _sb_fail "no Xcode at $_sb_devdir"; return 1 2>/dev/null || exit 1
fi

if [ "${2:-}" = "--" ]; then
  # One command: keep a private copy of whatever global.json is in force now (the committed
  # one, or the pin of a shell that sourced another pairing) and put exactly that back after,
  # even on a signal. Two commands on different pairings still cannot share one checkout.
  _sb_cmd_backup="$(mktemp "$_sb_repo/.global.json.use-toolchain.XXXXXX")"
  cp "$_sb_repo/global.json" "$_sb_cmd_backup"
  trap 'mv -f "$_sb_cmd_backup" "$_sb_repo/global.json"' EXIT
else
  # A sourced shell: keep the committed global.json once; every later switch rewrites the pin.
  [ -f "$_sb_global_backup" ] || cp "$_sb_repo/global.json" "$_sb_global_backup"
fi
cat > "$_sb_repo/global.json" <<JSON
{
  "sdk": {
    "version": "$_sb_sdk",
    "rollForward": "disable",
    "workloadVersion": "$_sb_workload"
  }
}
JSON

export DOTNET_ROOT="$_sb_root"
export DOTNET_MULTILEVEL_LOOKUP=0
export PATH="$_sb_root:$PATH"
export DEVELOPER_DIR="$_sb_devdir"
export SwiftBindingsPairing="$_sb_pairing"
export SWIFTBINDINGS_SIM_RUNTIMES="$(_sb_get simRuntimes)"
export SWIFTBINDINGS_TV_SIM_RUNTIMES="$(_sb_get tvSimRuntimes)"
if [ -n "${SWIFTBINDINGS_NUGET_PACKAGES_ROOT:-}" ]; then
  export NUGET_PACKAGES="$SWIFTBINDINGS_NUGET_PACKAGES_ROOT/$_sb_pairing"
  mkdir -p "$NUGET_PACKAGES"
fi

echo "pairing $_sb_pairing: dotnet $(dotnet --version) ($_sb_root), $(xcodebuild -version | head -1)," \
     "$(_sb_get targetFramework)-<platform>$(_sb_get tfmPlatformVersion) (packs as <platform>$(_sb_get platformLabel))${NUGET_PACKAGES:+, NUGET_PACKAGES=$NUGET_PACKAGES}" >&2
unset -f _sb_get _sb_fail

if [ "${2:-}" = "--" ]; then
  shift 2
  "$@"
  exit $?   # the EXIT trap restores the global.json this command found
fi
