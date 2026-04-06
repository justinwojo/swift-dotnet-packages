#!/bin/bash
# Auto-detect cross-module dependencies for bound Swift libraries.
#
# Usage: scripts/detect-dependencies.sh <library-dir> [--products P1,P2] [--all-products]
#                                                     [--inject | --inject-project-refs]
#                                                     [--clean-first]
#
# Modes:
#   Default              — print a human-readable dependency report to stdout.
#                          Reads .swiftinterface files inside xcframeworks.
#
#   --inject             — update csproj files in place with auto-detected
#                          SwiftFrameworkDependency items. Source of truth is
#                          `import` statements in .swiftinterface. Prerequisite:
#                          xcframeworks are already built for every product.
#
#   --inject-project-refs — update csproj files with a ProjectReference block
#                          derived from grepping the FRESHLY-GENERATED C# under
#                          obj/Debug/net10.0-ios/swift-binding/. A ProjectRef is
#                          added iff the generated C# actually references types
#                          from the sibling's module namespace. Prerequisite:
#                          pass-1 `dotnet build` has completed and emitted
#                          swift-binding.stamp newer than both the csproj and
#                          the xcframework/Info.plist.
#
#   --clean-first        — before the freshness checks, rm -rf each product's
#                          obj/.../swift-binding/ directory. Use together with
#                          --inject-project-refs on a second pass to guarantee
#                          the generated C# is fresh. Exits after cleanup; the
#                          caller must run `dotnet build` and re-run the script.
#
# Prerequisites:
#   --inject:              xcframeworks built
#   --inject-project-refs: first-pass `dotnet build` completed (or use
#                          --clean-first + rebuild + re-run)

set -euo pipefail

source "$(dirname "$0")/lib.sh"

# ── Argument Parsing ─────────────────────────────────────────────────────────

LIBRARY_DIR=""
REQUESTED_PRODUCTS=""
ALL_PRODUCTS=false
INJECT=false
INJECT_PROJECT_REFS=false
CLEAN_FIRST=false

while [[ $# -gt 0 ]]; do
    case "$1" in
        --products)
            REQUESTED_PRODUCTS="$2"
            shift 2
            ;;
        --all-products)
            ALL_PRODUCTS=true
            shift
            ;;
        --inject)
            INJECT=true
            shift
            ;;
        --inject-project-refs)
            INJECT_PROJECT_REFS=true
            shift
            ;;
        --clean-first)
            CLEAN_FIRST=true
            shift
            ;;
        -*)
            die "Unknown option '$1'"
            ;;
        *)
            if [ -z "$LIBRARY_DIR" ]; then
                LIBRARY_DIR="$1"
            else
                die "Unexpected argument '$1'"
            fi
            shift
            ;;
    esac
done

[ -n "$LIBRARY_DIR" ] || die "Usage: $0 <library-dir> [--products P1,P2] [--all-products] [--inject | --inject-project-refs] [--clean-first]"

if [ "$INJECT" = true ] && [ "$INJECT_PROJECT_REFS" = true ]; then
    die "--inject and --inject-project-refs are mutually exclusive (they run in separate passes — see CONTRIBUTING.md)"
fi

# Resolve to absolute path
LIBRARY_DIR=$(cd "$LIBRARY_DIR" && pwd)

CONFIG="$LIBRARY_DIR/library.json"
[ -f "$CONFIG" ] || die "library.json not found in $LIBRARY_DIR"

# ── Read Config ──────────────────────────────────────────────────────────────

PRODUCT_COUNT=$(json_array_len "$CONFIG" products)
[ "$PRODUCT_COUNT" -gt 0 ] || die "products array is empty in library.json"

# ── Resolve Product List ─────────────────────────────────────────────────────

PRODUCT_INDICES=()

if [ -n "$REQUESTED_PRODUCTS" ]; then
    IFS=',' read -ra REQ_NAMES <<< "$REQUESTED_PRODUCTS"
    ALL_NAMES=$(json_product_names "$CONFIG")
    for req in "${REQ_NAMES[@]}"; do
        found=false
        idx=0
        while IFS= read -r name; do
            if [ "$name" = "$req" ]; then
                PRODUCT_INDICES+=("$idx")
                found=true
                break
            fi
            ((idx++))
        done <<< "$ALL_NAMES"
        [ "$found" = true ] || die "Product '$req' not found in library.json."
    done
elif [ "$ALL_PRODUCTS" = true ]; then
    for ((i=0; i<PRODUCT_COUNT; i++)); do
        PRODUCT_INDICES+=("$i")
    done
elif [ "$PRODUCT_COUNT" -eq 1 ]; then
    PRODUCT_INDICES=(0)
else
    echo "Error: library.json has $PRODUCT_COUNT products. Specify which to analyze:" >&2
    echo "" >&2
    json_product_names "$CONFIG" | while IFS= read -r name; do
        echo "  - $name" >&2
    done
    echo "" >&2
    echo "Use --products P1,P2 or --all-products." >&2
    exit 1
fi

# ── Build Product Metadata ───────────────────────────────────────────────────

# Collect all product framework names, modules, subdirs, and internal status
ALL_FRAMEWORKS=()
ALL_MODULES=()
ALL_SUBDIRS=()
ALL_INTERNAL=()

for ((i=0; i<PRODUCT_COUNT; i++)); do
    fw=$(json_product_field "$CONFIG" "$i" framework)
    mod=$(json_product_field "$CONFIG" "$i" module "$fw")
    sub=$(json_product_field "$CONFIG" "$i" subdirectory "")
    is_int=$(json_product_bool "$CONFIG" "$i" internal)
    ALL_FRAMEWORKS+=("$fw")
    ALL_MODULES+=("$mod")
    ALL_SUBDIRS+=("$sub")
    ALL_INTERNAL+=("$is_int")
done

# Track which modules have a .swiftmodule (are Swift, not ObjC-only)
SWIFT_MODULES=()
for ((i=0; i<PRODUCT_COUNT; i++)); do
    fw="${ALL_FRAMEWORKS[$i]}"
    sub="${ALL_SUBDIRS[$i]}"

    xcfw_dir="$LIBRARY_DIR"
    [ -n "$sub" ] && xcfw_dir="$xcfw_dir/$sub"
    xcfw_dir="$xcfw_dir/${fw}.xcframework"

    if [ -d "$xcfw_dir" ]; then
        has_swiftmod=$(find "$xcfw_dir" -type d -name "*.swiftmodule" 2>/dev/null | head -1)
        if [ -n "$has_swiftmod" ]; then
            SWIFT_MODULES+=("${ALL_MODULES[$i]}")
        fi
    fi
done

# ── Analyze Dependencies ─────────────────────────────────────────────────────

analyze_product() {
    local idx="$1"
    local fw="${ALL_FRAMEWORKS[$idx]}"
    local mod="${ALL_MODULES[$idx]}"
    local sub="${ALL_SUBDIRS[$idx]}"

    local xcfw_dir="$LIBRARY_DIR"
    [ -n "$sub" ] && xcfw_dir="$xcfw_dir/$sub"
    xcfw_dir="$xcfw_dir/${fw}.xcframework"

    if [ ! -d "$xcfw_dir" ]; then
        echo "SKIP:$fw:xcframework not found"
        return
    fi

    # Find .swiftinterface file
    # Prefer device slice (ios-arm64), then any slice; skip .private.swiftinterface
    local swiftinterface=""
    for si in $(find "$xcfw_dir" -path "*/ios-arm64*" -name "*.swiftinterface" ! -name "*.private.swiftinterface" 2>/dev/null); do
        swiftinterface="$si"
        break
    done
    if [ -z "$swiftinterface" ]; then
        for si in $(find "$xcfw_dir" -name "*.swiftinterface" ! -name "*.private.swiftinterface" 2>/dev/null); do
            swiftinterface="$si"
            break
        done
    fi

    if [ -z "$swiftinterface" ]; then
        echo "SKIP:$fw:no .swiftinterface found (ObjC-only?)"
        return
    fi

    # Extract import lines from the swiftinterface
    local imports
    imports=$(python3 -c "
import re
imports = set()
with open('$swiftinterface') as f:
    in_imports = False
    for line in f:
        line = line.strip()
        if not line or line.startswith('//'):
            continue
        m = re.match(r'^(?:@_exported\s+)?import\s+(\w+)', line)
        if m:
            in_imports = True
            imports.add(m.group(1))
        elif in_imports:
            break
for i in sorted(imports):
    print(i)
")

    # Cross-reference imports against sibling product modules
    local deps=()
    while IFS= read -r imp_mod; do
        [ -z "$imp_mod" ] && continue
        [ "$imp_mod" = "$mod" ] && continue

        for ((j=0; j<PRODUCT_COUNT; j++)); do
            if [ "${ALL_MODULES[$j]}" = "$imp_mod" ]; then
                # Check if this sibling has a .swiftmodule (is Swift)
                local is_swift=false
                for sm in "${SWIFT_MODULES[@]}"; do
                    if [ "$sm" = "$imp_mod" ]; then
                        is_swift=true
                        break
                    fi
                done
                if [ "$is_swift" = true ]; then
                    deps+=("${ALL_SUBDIRS[$j]}|${ALL_FRAMEWORKS[$j]}")
                fi
                break
            fi
        done
    done <<< "$imports"

    if [ ${#deps[@]} -eq 0 ]; then
        echo "DEPS:$fw:NONE"
    else
        local sorted_deps
        sorted_deps=$(printf '%s\n' "${deps[@]}" | sort)
        while IFS= read -r dep; do
            echo "DEPS:$fw:$dep"
        done <<< "$sorted_deps"
    fi
}

# Collect all results
RESULTS=()
for idx in "${PRODUCT_INDICES[@]}"; do
    if [ "${ALL_INTERNAL[$idx]}" = "true" ]; then
        continue
    fi
    while IFS= read -r line; do
        RESULTS+=("$line")
    done < <(analyze_product "$idx")
done

# ── Report Mode ──────────────────────────────────────────────────────────────

if [ "$INJECT" = false ] && [ "$INJECT_PROJECT_REFS" = false ]; then
    current_product=""
    for line in "${RESULTS[@]}"; do
        IFS=':' read -r type fw rest <<< "$line"
        if [ "$fw" != "$current_product" ]; then
            [ -n "$current_product" ] && echo ""
            echo "$fw:"
            current_product="$fw"
        fi
        case "$type" in
            SKIP)
                echo "  (skipped: $rest)"
                ;;
            DEPS)
                if [ "$rest" = "NONE" ]; then
                    echo "  (no sibling dependencies)"
                else
                    IFS='|' read -r dep_sub dep_fw <<< "$rest"
                    if [ -n "$dep_sub" ]; then
                        echo "  <SwiftFrameworkDependency Include=\"../$dep_sub/$dep_fw.xcframework\" />"
                    else
                        echo "  <SwiftFrameworkDependency Include=\"../$dep_fw/$dep_fw.xcframework\" />"
                    fi
                fi
                ;;
        esac
    done
    exit 0
fi

# ── ProjectReference Injection Mode ──────────────────────────────────────────
#
# Walks each product's freshly generated C# under obj/.../swift-binding/,
# greps for sibling module namespace usage, and injects a ProjectReference
# block into the csproj. Distinct from --inject, which writes
# SwiftFrameworkDependency from .swiftinterface imports.
#
# The freshness check is critical: stale C# from a previous build can cause
# ghost type references (false positives from now-removed deps) or miss new
# ones (false negatives after adding a dep). The SDK writes
# obj/.../swift-binding/swift-binding.stamp at the end of every generation
# pass, so mtime(stamp) > mtime(csproj) and mtime(stamp) > mtime(xcframework)
# both need to hold for the C# to be trustworthy.

if [ "$INJECT_PROJECT_REFS" = true ]; then
    # Handle --clean-first: wipe each product's swift-binding/ output so the
    # next dotnet build regenerates from scratch. This is a cleanup-only pass;
    # the caller must rebuild + re-invoke without --clean-first.
    if [ "$CLEAN_FIRST" = true ]; then
        for idx in "${PRODUCT_INDICES[@]}"; do
            if [ "${ALL_INTERNAL[$idx]}" = "true" ]; then
                continue
            fi
            sub="${ALL_SUBDIRS[$idx]}"
            csproj_dir="$LIBRARY_DIR"
            [ -n "$sub" ] && csproj_dir="$csproj_dir/$sub"
            swift_binding_dir="$csproj_dir/obj/Debug/net10.0-ios/swift-binding"
            if [ -d "$swift_binding_dir" ]; then
                rm -rf "$swift_binding_dir"
                echo "Cleaned $swift_binding_dir"
            fi
        done
        echo ""
        echo "Stale swift-binding/ output removed. Now run 'dotnet build' for each"
        echo "product (first pass), then re-run this script WITHOUT --clean-first."
        exit 0
    fi

    # Build per-product entries: one pipe-separated record per non-internal
    # product with fields the Python driver needs. Entries look like:
    #   fw|module|csproj_abs_path|swift_binding_dir|xcframework_plist
    PRODUCT_ENTRIES=()
    SIBLING_ENTRIES=()

    for idx in "${PRODUCT_INDICES[@]}"; do
        if [ "${ALL_INTERNAL[$idx]}" = "true" ]; then
            continue
        fi
        fw="${ALL_FRAMEWORKS[$idx]}"
        mod="${ALL_MODULES[$idx]}"
        sub="${ALL_SUBDIRS[$idx]}"

        csproj_dir="$LIBRARY_DIR"
        [ -n "$sub" ] && csproj_dir="$csproj_dir/$sub"

        # Strict discovery — fail loudly on 0 or >1 matches. Partial runs on
        # malformed layouts would silently drop products from the analysis.
        csproj_file=$(discover_single_csproj "$csproj_dir") || exit 1

        swift_binding_dir="$csproj_dir/obj/Debug/net10.0-ios/swift-binding"
        xcfw_plist="$csproj_dir/${fw}.xcframework/Info.plist"

        PRODUCT_ENTRIES+=("${fw}|${mod}|${csproj_file}|${swift_binding_dir}|${xcfw_plist}")
    done

    # Sibling table covers ALL products (including the ones NOT selected for
    # this run), because a product may reference a sibling that wasn't in
    # --products. A sibling is a valid ProjectReference candidate iff it is
    # non-internal AND has a discoverable csproj.
    for ((i=0; i<PRODUCT_COUNT; i++)); do
        if [ "${ALL_INTERNAL[$i]}" = "true" ]; then
            continue
        fi
        fw="${ALL_FRAMEWORKS[$i]}"
        mod="${ALL_MODULES[$i]}"
        sub="${ALL_SUBDIRS[$i]}"

        csproj_dir="$LIBRARY_DIR"
        [ -n "$sub" ] && csproj_dir="$csproj_dir/$sub"

        # Strict discovery — a silently-skipped sibling would disappear from
        # dependency inference, causing false negatives in the auto-block.
        csproj_file=$(discover_single_csproj "$csproj_dir") || exit 1
        SIBLING_ENTRIES+=("${fw}|${mod}|${csproj_file}")
    done

    # Hand everything to a single Python driver. It's more ergonomic than
    # stringly-typed bash for the freshness comparisons and csproj rewrite.
    # Propagate the Python exit status — the bash wrapper should fail if any
    # freshness check fails or the rewrite can't complete.
    set +e
    python3 - "$LIBRARY_DIR" << PYEOF
import json, os, re, sys
from pathlib import Path

LIBRARY_DIR = sys.argv[1]

# Parse the pipe-delimited entries emitted by bash.
products = []
for line in """${PRODUCT_ENTRIES[@]+$(printf '%s\n' "${PRODUCT_ENTRIES[@]}")}""".strip().splitlines():
    if not line:
        continue
    fw, mod, csproj, sb_dir, plist = line.split("|")
    products.append({
        "framework": fw,
        "module": mod,
        "csproj": csproj,
        "swift_binding_dir": sb_dir,
        "xcfw_plist": plist,
    })

siblings = []
for line in """${SIBLING_ENTRIES[@]+$(printf '%s\n' "${SIBLING_ENTRIES[@]}")}""".strip().splitlines():
    if not line:
        continue
    fw, mod, csproj = line.split("|")
    siblings.append({"framework": fw, "module": mod, "csproj": csproj})

BEGIN_MARKER = "<!-- BEGIN auto-detected ProjectReference -->"
END_MARKER = "<!-- END auto-detected ProjectReference -->"

def die(msg):
    print(f"Error: {msg}", file=sys.stderr)
    sys.exit(1)

def mtime(path):
    try:
        return os.path.getmtime(path)
    except OSError:
        return None

def check_freshness(prod):
    stamp = os.path.join(prod["swift_binding_dir"], "swift-binding.stamp")
    if not os.path.isfile(stamp):
        die(
            f"Missing freshness marker for {prod['framework']}:\n"
            f"  expected: {stamp}\n"
            f"  cause: the first-pass 'dotnet build' hasn't been run (or was cleaned)\n"
            f"  fix: cd into {os.path.dirname(prod['csproj'])} && dotnet build, then re-run"
        )

    # Backstop: the stamp without any .cs files means the generator crashed.
    sb_dir = Path(prod["swift_binding_dir"])
    cs_files = sorted(sb_dir.glob("*.cs"))
    if not cs_files:
        die(
            f"swift-binding.stamp present but no .cs files for {prod['framework']}:\n"
            f"  dir: {sb_dir}\n"
            f"  this usually means the generator failed mid-run. Re-run 'dotnet build' "
            f"and investigate any SWIFTBIND warnings."
        )

    stamp_mt = mtime(stamp)
    csproj_mt = mtime(prod["csproj"])
    if csproj_mt is not None and csproj_mt > stamp_mt:
        die(
            f"Stale generated C# for {prod['framework']}:\n"
            f"  csproj modified at {csproj_mt} is newer than stamp at {stamp_mt}\n"
            f"  (the csproj was rewritten since the last generation pass)\n"
            f"  fix: re-run 'dotnet build' to regenerate bindings, then re-run this script."
        )

    plist_mt = mtime(prod["xcfw_plist"])
    if plist_mt is not None and plist_mt > stamp_mt:
        die(
            f"Stale generated C# for {prod['framework']}:\n"
            f"  xcframework Info.plist at {plist_mt} is newer than stamp at {stamp_mt}\n"
            f"  (the xcframework was rebuilt since the last generation pass)\n"
            f"  fix: re-run 'dotnet build' to regenerate bindings, then re-run this script."
        )

    return cs_files

# Strip C# comments so doc comments like "/// communicate with Stripe." don't
# trigger false positives on the \bModule\. grep below. We're conservative —
# the regexes handle // line comments and /* ... */ block comments. String
# literals containing "//" or "/*" are rare in generated bindings (which just
# have method signatures + XML doc comments), so we don't bother with a full
# C# lexer.
_LINE_COMMENT_RE = re.compile(r"//[^\n]*")
_BLOCK_COMMENT_RE = re.compile(r"/\*.*?\*/", re.DOTALL)

def strip_comments(text):
    text = _BLOCK_COMMENT_RE.sub("", text)
    text = _LINE_COMMENT_RE.sub("", text)
    return text

def collect_cs_content(cs_files):
    parts = []
    for p in cs_files:
        try:
            parts.append(strip_comments(p.read_text(encoding="utf-8", errors="replace")))
        except OSError:
            pass
    return "\n".join(parts)

def rel_path(src_csproj, dst_csproj):
    """Relative path from src_csproj's directory to dst_csproj."""
    return os.path.relpath(dst_csproj, os.path.dirname(src_csproj))

def detect_refs(prod, content):
    """Return list of sibling csprojs whose module is referenced by content."""
    hits = []
    for sib in siblings:
        if sib["csproj"] == prod["csproj"]:
            continue  # skip self
        mod = sib["module"]
        if not mod:
            continue
        # Match \bMod\. anywhere in the generated C#. The C# emits module-level
        # namespaces (e.g. StripeCore.STPAPIClient), so a bare module-dot match
        # is the right signal.
        pattern = re.compile(r"\b" + re.escape(mod) + r"\.")
        if pattern.search(content):
            hits.append(sib)
    return hits

def build_block(src_csproj, hits):
    if not hits:
        return ""
    lines = [BEGIN_MARKER, "  <ItemGroup>"]
    for h in sorted(hits, key=lambda s: s["module"]):
        rel = rel_path(src_csproj, h["csproj"])
        lines.append(f'    <ProjectReference Include="{rel}" />')
    lines.append("  </ItemGroup>")
    lines.append("  " + END_MARKER)
    return "\n".join(lines)

# Lookup set of sibling csproj paths (any form the csproj might reference).
# We normalize to the csproj basename so we can recognize "../StripeCore/
# SwiftBindings.Stripe.Core.csproj" as a sibling regardless of relative prefix.
SIBLING_BASENAMES = {os.path.basename(s["csproj"]) for s in siblings}

def is_sibling_ref(include_path):
    """True if include_path points at a sibling csproj we manage."""
    return os.path.basename(include_path) in SIBLING_BASENAMES

def strip_auto_block(content):
    """Remove any previous auto-detected block and normalize whitespace.

    Always safe — called on every run for idempotence. The strip + re-insert
    should produce the exact same surrounding whitespace across runs, so we
    normalize the removed block to a single paragraph break.
    """
    return re.sub(
        r"\s*" + re.escape(BEGIN_MARKER) + r".*?" + re.escape(END_MARKER) + r"\s*",
        "\n\n",
        content,
        flags=re.DOTALL,
    )

def migrate_sibling_refs(content):
    """Strip hand-authored sibling ProjectReference items.

    Walks each ItemGroup. If an ItemGroup contains only sibling ProjectRefs
    (and no other elements), the whole group is removed. If it also has
    non-sibling items, only the sibling refs are excised.

    ONLY called on first-run migration (when the csproj has never been
    processed before — i.e. no BEGIN/END auto-block markers exist). On
    subsequent runs, hand-authored sibling refs outside the auto-block are
    preserved as an escape hatch — see CONTRIBUTING.md.
    """
    lines = content.split("\n")
    out = []
    i = 0
    while i < len(lines):
        line = lines[i]
        stripped = line.strip()
        if stripped == "<ItemGroup>":
            # Lookahead: collect this group up to </ItemGroup>
            group_lines = [line]
            i += 1
            while i < len(lines) and lines[i].strip() != "</ItemGroup>":
                group_lines.append(lines[i])
                i += 1
            if i < len(lines):
                group_lines.append(lines[i])  # </ItemGroup>
                i += 1

            # Filter sibling ProjectRefs from this group
            filtered = [group_lines[0]]  # <ItemGroup>
            for body in group_lines[1:-1]:
                m = re.search(r'<ProjectReference\s+Include="([^"]+)"', body)
                if m and is_sibling_ref(m.group(1)):
                    continue  # drop — will be re-emitted in the auto-block
                filtered.append(body)
            filtered.append(group_lines[-1])  # </ItemGroup>

            # If the filtered group has no body elements (only <ItemGroup>
            # and </ItemGroup>), drop it entirely — including any leading
            # whitespace-only lines and trailing blank separator.
            has_body = any(l.strip() for l in filtered[1:-1])
            if not has_body:
                # Drop preceding blank line if present
                while out and not out[-1].strip():
                    out.pop()
                continue

            out.extend(filtered)
            continue
        out.append(line)
        i += 1

    return "\n".join(out)

def extract_outside_sibling_refs(content):
    """Return set of sibling csproj basenames referenced in content.

    The caller must pass content AFTER strip_auto_block has removed the
    auto-managed block, so only hand-authored out-of-block refs remain.
    Basenames are used so we match regardless of the relative-path prefix
    the user wrote (e.g. '../Foo/SwiftBindings.Foo.csproj' vs
    '../Foo/../Foo/SwiftBindings.Foo.csproj').
    """
    refs = set()
    for m in re.finditer(r'<ProjectReference\s+Include="([^"]+)"', content):
        include = m.group(1)
        if is_sibling_ref(include):
            refs.add(os.path.basename(include))
    return refs

def rewrite_csproj(csproj_path, hits):
    """Rewrite the csproj with the auto-detected block (migration-aware).

    Two modes, dispatched on whether the csproj already has the auto-block:

    First run (no BEGIN/END markers in the original):
      - MIGRATION. Hand-authored sibling ProjectReference items found in
        the csproj are UNIONED with the detection hits before the block is
        built, then the hand-authored entries are stripped from their old
        ItemGroups. This is the key carry-forward semantic: if a user has
        manually added a sibling ref precisely because grep couldn't find
        the dependency, that ref survives the migration inside the fresh
        auto-block instead of being silently deleted.

    Subsequent runs (markers present):
      - The auto-block is stripped and rebuilt from detection.
      - Hand-authored sibling refs OUTSIDE the auto-block are preserved as
        an escape hatch for cases where detection misses a real dependency
        (see CONTRIBUTING.md). Detection-found refs that duplicate a
        preserved hand-authored ref are suppressed from the new auto-block
        to avoid duplicate ProjectReference items.

    Non-sibling ProjectReferences are preserved in both modes.
    """
    with open(csproj_path, "r", encoding="utf-8") as f:
        original = f.read()

    had_auto_block = BEGIN_MARKER in original and END_MARKER in original

    content = strip_auto_block(original)

    filtered_hits = list(hits)
    if not had_auto_block:
        # First run — union hand-authored sibling refs INTO the detection
        # hits BEFORE stripping them, so refs grep missed are carried
        # forward rather than silently deleted.
        manual_basenames = extract_outside_sibling_refs(content)
        existing_modules = {h["module"] for h in filtered_hits}
        carried = []
        for sib in siblings:
            if (
                os.path.basename(sib["csproj"]) in manual_basenames
                and sib["module"] not in existing_modules
            ):
                filtered_hits.append(sib)
                existing_modules.add(sib["module"])
                carried.append(sib["module"])
        if carried:
            print(
                f"    (migrating hand-authored refs into auto-block: "
                f"{', '.join(sorted(carried))})"
            )

        content = migrate_sibling_refs(content)
    else:
        # Subsequent runs — preserve out-of-block hand-authored refs.
        # De-dupe: suppress detection hits that are already hand-authored.
        preserved_basenames = extract_outside_sibling_refs(content)
        preserved_modules = {
            sib["module"]
            for sib in siblings
            if os.path.basename(sib["csproj"]) in preserved_basenames
        }
        if preserved_modules:
            filtered_hits = [h for h in hits if h["module"] not in preserved_modules]
            suppressed = sorted(
                {h["module"] for h in hits} & preserved_modules
            )
            if suppressed:
                print(
                    f"    (keeping hand-authored out-of-block refs: "
                    f"{', '.join(suppressed)})"
                )

    new_block = build_block(csproj_path, filtered_hits)
    if new_block:
        # Normalize whitespace immediately before </Project> and insert the
        # block with a canonical blank-line separator on both sides. This is
        # what makes re-runs idempotent: strip_auto_block leaves "\n\n" where
        # the block used to be, and this re-insertion restores the same
        # surrounding whitespace regardless of prior spacing in the csproj.
        content = re.sub(
            r"\s*</Project>",
            lambda _: "\n\n" + new_block + "\n\n</Project>",
            content,
            count=1,
        )
    # If new_block is empty (no cross-module refs), we still wrote the
    # stripped content above, so a cleanup-only run is still a valid update.

    if content != original:
        with open(csproj_path, "w", encoding="utf-8") as f:
            f.write(content)
        return True
    return False

if not products:
    print("No non-internal products to process.")
    sys.exit(0)

print(f"Analyzing ProjectReference needs for {len(products)} product(s)...")
print()

# Pass 1: freshness check for ALL products BEFORE any writes. If any product
# is stale, abort without touching the csprojs — it's an all-or-nothing run.
freshness_cache = {}
for prod in products:
    freshness_cache[prod["csproj"]] = check_freshness(prod)

# Pass 2: detect refs + rewrite csprojs. Safe to write now — every product
# has fresh, trustworthy C#.
any_changes = False
for prod in products:
    cs_files = freshness_cache[prod["csproj"]]
    content = collect_cs_content(cs_files)
    hits = detect_refs(prod, content)

    label = prod["framework"]
    if hits:
        needs = ", ".join(sorted(h["module"] for h in hits))
        print(f"  {label} → needs: [{needs}]")
    else:
        print(f"  {label} → needs: (none)")

    if rewrite_csproj(prod["csproj"], hits):
        any_changes = True
        print(f"    updated {os.path.relpath(prod['csproj'], LIBRARY_DIR)}")
    else:
        print(f"    no changes to {os.path.relpath(prod['csproj'], LIBRARY_DIR)}")

print()
if any_changes:
    print("ProjectReference block(s) updated. Run 'dotnet build' (second pass).")
else:
    print("All csprojs already up to date — no changes made.")
PYEOF

    py_rc=$?
    set -e
    exit $py_rc
fi

# ── Inject Mode ──────────────────────────────────────────────────────────────

# Write data to a temp JSON file for clean Python consumption
INJECT_DATA=$(mktemp)
trap "rm -f '$INJECT_DATA'" EXIT

# Build the inject data: one JSON object with siblings list and per-product deps
python3 -c "
import json

# Build known sibling paths
siblings = []
frameworks = $(python3 -c "import json; data=json.load(open('$CONFIG')); print(json.dumps([p['framework'] for p in data['products']]))")
subdirs = $(python3 -c "import json; data=json.load(open('$CONFIG')); print(json.dumps([p.get('subdirectory', '') for p in data['products']]))")
for fw, sub in zip(frameworks, subdirs):
    if sub:
        siblings.append(f'../{sub}/{fw}.xcframework')
    else:
        siblings.append(f'../{fw}/{fw}.xcframework')

json.dump({'siblings': siblings}, open('$INJECT_DATA', 'w'))
"

for idx in "${PRODUCT_INDICES[@]}"; do
    if [ "${ALL_INTERNAL[$idx]}" = "true" ]; then
        continue
    fi

    fw="${ALL_FRAMEWORKS[$idx]}"
    mod="${ALL_MODULES[$idx]}"
    sub="${ALL_SUBDIRS[$idx]}"

    # Discover csproj on disk (vendor-prefixed packages have names like
    # SwiftBindings.Stripe.Core.csproj, not SwiftBindings.StripeCore.csproj).
    # Strict discovery — fail loudly on 0 or >1 matches.
    csproj_dir="$LIBRARY_DIR"
    [ -n "$sub" ] && csproj_dir="$csproj_dir/$sub"

    csproj_file=$(discover_single_csproj "$csproj_dir") || exit 1

    # Collect dep XML lines for this product
    dep_includes=()
    for line in "${RESULTS[@]}"; do
        IFS=':' read -r type line_fw rest <<< "$line"
        if [ "$line_fw" = "$fw" ] && [ "$type" = "DEPS" ] && [ "$rest" != "NONE" ]; then
            IFS='|' read -r dep_sub dep_fw <<< "$rest"
            if [ -n "$dep_sub" ]; then
                dep_includes+=("../$dep_sub/$dep_fw.xcframework")
            else
                dep_includes+=("../$dep_fw/$dep_fw.xcframework")
            fi
        fi
    done

    # Write dep includes as a JSON array for Python
    deps_json="["
    first=true
    for inc in ${dep_includes[@]+"${dep_includes[@]}"}; do
        if [ "$first" = true ]; then
            deps_json="$deps_json\"$inc\""
            first=false
        else
            deps_json="$deps_json,\"$inc\""
        fi
    done
    deps_json="$deps_json]"

    python3 -c "
import re, json, sys

csproj_file = '$csproj_file'
inject_data = json.load(open('$INJECT_DATA'))
known_siblings = set(inject_data['siblings'])
dep_includes = $deps_json

BEGIN_MARKER = '<!-- BEGIN auto-detected SwiftFrameworkDependency -->'
END_MARKER = '<!-- END auto-detected SwiftFrameworkDependency -->'

with open(csproj_file, 'r') as f:
    content = f.read()
    original = content

# Build the auto-detected block
if dep_includes:
    lines = [BEGIN_MARKER, '  <ItemGroup>']
    for inc in sorted(dep_includes):
        lines.append(f'    <SwiftFrameworkDependency Include=\"{inc}\" />')
    lines.append('  </ItemGroup>')
    lines.append('  ' + END_MARKER)
    deps_block = '\n'.join(lines)
else:
    deps_block = ''

has_markers = BEGIN_MARKER in content and END_MARKER in content

if has_markers:
    # Replace everything between markers (inclusive)
    pattern = re.escape(BEGIN_MARKER) + r'.*?' + re.escape(END_MARKER)
    if deps_block:
        content = re.sub(pattern, deps_block, content, flags=re.DOTALL)
    else:
        # Remove markers and surrounding whitespace
        content = re.sub(r'\n?\s*' + re.escape(BEGIN_MARKER) + r'.*?' + re.escape(END_MARKER) + r'\s*\n?', '\n', content, flags=re.DOTALL)
else:
    # Check for existing manual SwiftFrameworkDependency entries
    manual_matches = re.findall(r'<SwiftFrameworkDependency\s+Include=\"([^\"]+)\"', content)

    if manual_matches:
        # Migration: remove sibling manual entries, keep non-sibling ones
        sibling_count = 0
        kept_count = 0
        lines = content.split('\n')
        new_lines = []
        in_sfd_itemgroup = False
        itemgroup_start = -1
        itemgroup_empty_after_removal = True

        i = 0
        while i < len(lines):
            line = lines[i]
            stripped = line.strip()

            if '<ItemGroup>' in stripped and i + 1 < len(lines):
                # Scan until </ItemGroup> to detect SwiftFrameworkDependency entries
                has_sfd = False
                for p in range(i + 1, len(lines)):
                    if 'SwiftFrameworkDependency' in lines[p]:
                        has_sfd = True
                        break
                    if '</ItemGroup>' in lines[p]:
                        break
                if has_sfd:
                    in_sfd_itemgroup = True
                    itemgroup_start = len(new_lines)
                    itemgroup_empty_after_removal = True
                    new_lines.append(line)
                    i += 1
                    continue

            if in_sfd_itemgroup:
                if '</ItemGroup>' in stripped:
                    in_sfd_itemgroup = False
                    if itemgroup_empty_after_removal:
                        new_lines = new_lines[:itemgroup_start]
                        # Remove preceding comment about framework dependencies
                        while new_lines and new_lines[-1].strip().startswith('<!--'):
                            new_lines.pop()
                        # Remove preceding blank line
                        while new_lines and not new_lines[-1].strip():
                            new_lines.pop()
                    else:
                        new_lines.append(line)
                    i += 1
                    continue

                sfd_match = re.search(r'<SwiftFrameworkDependency\s+Include=\"([^\"]+)\"', stripped)
                if sfd_match:
                    include_path = sfd_match.group(1)
                    if include_path in known_siblings:
                        sibling_count += 1
                        i += 1
                        continue
                    else:
                        kept_count += 1
                        itemgroup_empty_after_removal = False
                        new_lines.append(line)
                        i += 1
                        continue
                elif stripped.startswith('<!--'):
                    # Keep non-SFD comments, skip SFD-related ones
                    if 'SwiftFrameworkDependency' in stripped or 'ObjC-only' in stripped:
                        i += 1
                        continue
                    new_lines.append(line)
                    i += 1
                    continue
                else:
                    if stripped:
                        itemgroup_empty_after_removal = False
                    new_lines.append(line)
                    i += 1
                    continue

            new_lines.append(line)
            i += 1

        content = '\n'.join(new_lines)
        if sibling_count > 0 or kept_count > 0:
            print(f'Migrated {sibling_count} sibling SwiftFrameworkDependency entries to auto-detected block in {csproj_file}; kept {kept_count} non-sibling entries')

    # Insert auto-detected block before </Project>
    if deps_block:
        content = content.replace('</Project>', deps_block + '\n\n</Project>')

if content != original:
    with open(csproj_file, 'w') as f:
        f.write(content)
    print(f'Updated {csproj_file}')
else:
    print(f'No changes needed for {csproj_file}')
"

done
