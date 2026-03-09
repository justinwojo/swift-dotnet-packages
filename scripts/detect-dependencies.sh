#!/bin/bash
# Auto-detect SwiftFrameworkDependency items by analyzing .swiftinterface import statements.
#
# Usage: scripts/detect-dependencies.sh <library-dir> [--products P1,P2] [--all-products] [--inject]
#
# Modes:
#   Default         — print human-readable dependency report to stdout
#   --inject        — update csproj files in-place with auto-detected dependencies
#
# Prerequisites: xcframeworks must be built before running this script.

set -euo pipefail

source "$(dirname "$0")/lib.sh"

# ── Argument Parsing ─────────────────────────────────────────────────────────

LIBRARY_DIR=""
REQUESTED_PRODUCTS=""
ALL_PRODUCTS=false
INJECT=false

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

[ -n "$LIBRARY_DIR" ] || die "Usage: $0 <library-dir> [--products P1,P2] [--all-products] [--inject]"

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

if [ "$INJECT" = false ]; then
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

    # Find csproj file
    csproj_dir="$LIBRARY_DIR"
    [ -n "$sub" ] && csproj_dir="$csproj_dir/$sub"
    csproj_file="$csproj_dir/SwiftBindings.${mod}.csproj"

    if [ ! -f "$csproj_file" ]; then
        echo "Warning: $csproj_file not found, skipping" >&2
        continue
    fi

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
