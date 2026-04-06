#!/bin/bash
# Ensure a pinned copy of spm-to-xcframework is available in .tools/.
#
# Prints the absolute path of the cached, verified script on stdout.
# Exits non-zero if the tool can't be obtained and doesn't match the pin.
#
# Usage:
#   tool=$(scripts/ensure-spm-to-xcframework.sh)
#   "$tool" <url> --version <tag> --product <name> ...
#
# The tool is pinned by commit SHA (upstream has no tagged releases yet) and
# verified by SHA-256 of the raw script contents. Bumping the pin:
#   1. Update SPM_TO_XCF_REF to the new commit SHA
#   2. Download the script manually and compute its sha256sum
#   3. Update SPM_TO_XCF_SHA256 to match
#   4. Commit all three constant changes together
#
# Cache layout:
#   .tools/spm-to-xcframework-<short-ref>   (executable, single-file script)
#
# A new ref/sha combination automatically invalidates older cached copies via
# the filename. Existing cached copies whose sha256 matches the pin are reused
# (offline-friendly). A cached copy whose sha256 does NOT match the pin is a
# hard error — we never silently use mismatched contents.

set -euo pipefail

# ── Pinning (bump these three constants together) ───────────────────────────
SPM_TO_XCF_REF="c926f0fea48387f7bc3dd277f83701a83522f844"
SPM_TO_XCF_SHA256="702f08f3009218b723b979e64f14246fa3f0b2022707b5f2e4ea516c49afeb6e"
SPM_TO_XCF_URL="https://raw.githubusercontent.com/justinwojo/spm-to-xcframework/${SPM_TO_XCF_REF}/spm-to-xcframework"

# ── Paths ────────────────────────────────────────────────────────────────────
REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
TOOLS_DIR="$REPO_ROOT/.tools"
CACHE_NAME="spm-to-xcframework-${SPM_TO_XCF_REF:0:12}"
CACHE_PATH="$TOOLS_DIR/$CACHE_NAME"

log() { echo "[ensure-spm-to-xcframework] $*" >&2; }

compute_sha256() {
    # Portable single-file sha256 hex digest (macOS shasum / Linux sha256sum)
    if command -v shasum >/dev/null 2>&1; then
        shasum -a 256 "$1" | awk '{print $1}'
    elif command -v sha256sum >/dev/null 2>&1; then
        sha256sum "$1" | awk '{print $1}'
    else
        echo "Error: neither shasum nor sha256sum is available" >&2
        exit 1
    fi
}

verify_file() {
    local path="$1"
    local actual
    actual=$(compute_sha256 "$path")
    [ "$actual" = "$SPM_TO_XCF_SHA256" ]
}

# ── Fast path: cached copy already verified ─────────────────────────────────
if [ -f "$CACHE_PATH" ]; then
    if verify_file "$CACHE_PATH"; then
        echo "$CACHE_PATH"
        exit 0
    else
        log "cached file $CACHE_PATH has wrong sha256 — refusing to use"
        log "delete it manually if you trust the pin bump, then re-run"
        exit 1
    fi
fi

# ── Slow path: fetch, verify, install atomically ────────────────────────────
mkdir -p "$TOOLS_DIR"
tmp=$(mktemp "$TOOLS_DIR/.download.XXXXXX")
trap 'rm -f "$tmp"' EXIT

log "downloading $SPM_TO_XCF_URL"
if ! curl -sSfL "$SPM_TO_XCF_URL" -o "$tmp"; then
    log "download failed"
    log "if you have a matching copy at $CACHE_PATH it would have been used; none found"
    exit 1
fi

if ! verify_file "$tmp"; then
    actual=$(compute_sha256 "$tmp")
    log "sha256 mismatch: expected $SPM_TO_XCF_SHA256, got $actual"
    log "either the pinned commit was force-pushed, or the download is corrupt"
    exit 1
fi

chmod +x "$tmp"
mv "$tmp" "$CACHE_PATH"
trap - EXIT

log "installed $CACHE_PATH"
echo "$CACHE_PATH"
