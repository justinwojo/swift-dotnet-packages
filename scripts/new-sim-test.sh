#!/bin/bash
# Scaffold a new simulator test app from the sim-test template.
#
# Usage: ./scripts/new-sim-test.sh <LibraryName> [options]
#
# Arguments:
#   LibraryName                   PascalCase library name (e.g. Nuke, Stripe)
#   --module ModuleName           Swift module name if different from LibraryName
#                                 (single-product only; errors with --products/--all-products)
#   --products P1,P2              Build only specified products from library.json
#   --all-products                Include all products from library.json
#   --with <root>[:P1,P2]         Add cross-repo dependency (repeatable)
#   --force                       Overwrite existing test directory
#
# Single product in library.json + no flags → auto-selects it (backward compatible)
# Multiple products + no --products/--all-products → fails with helpful error

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
TEMPLATE_DIR="$REPO_ROOT/templates/sim-test"

# ── Helpers ──────────────────────────────────────────────────────────────────
#
# These helpers were previously sourced from scripts/lib.sh. That file was
# removed when the Nuke migration deleted the bash build orchestration, but
# scaffolding still parses library.json so the relevant helpers are inlined
# here. Keep them minimal — anything more elaborate belongs in the Nuke
# harness, not in bash.

die() { echo "Error: $*" >&2; exit 1; }

json_array_len() {
    python3 -c "import json; data=json.load(open('$1')); print(len(data.get('$2', [])))"
}

json_product_field() {
    # json_product_field <file> <index> <field> [default]
    python3 -c "
import json
data = json.load(open('$1'))
product = data['products'][$2]
print(product.get('$3', '${4:-}'))
"
}

json_product_bool() {
    local val
    val=$(json_product_field "$1" "$2" "$3" "")
    if [ "$val" = "True" ] || [ "$val" = "true" ]; then
        echo "true"
    fi
}

json_product_names() {
    python3 -c "
import json
data = json.load(open('$1'))
for p in data['products']:
    print(p['framework'])
"
}

# discover_single_csproj <dir>
#
# Find the single SwiftBindings.*.csproj file in <dir>. Fails loudly when
# zero or multiple matches exist — vendor-prefixed names (e.g.
# SwiftBindings.Stripe.Core.csproj for module StripeCore) mean the filename
# can't be predicted from the module name alone, so discovery on disk is
# the canonical resolution path.
discover_single_csproj() {
    local dir="$1"
    [ -d "$dir" ] || die "discover_single_csproj: directory not found: $dir"
    local matches=()
    while IFS= read -r -d '' f; do
        matches+=("$f")
    done < <(find "$dir" -maxdepth 1 -name 'SwiftBindings.*.csproj' -print0 2>/dev/null)
    case "${#matches[@]}" in
        0) die "No SwiftBindings.*.csproj found in $dir" ;;
        1) echo "${matches[0]}" ;;
        *) die "Multiple SwiftBindings.*.csproj found in $dir: ${matches[*]}" ;;
    esac
}

# Resolve a library's products to an array of "lib_dir|framework|module|subdir" entries.
# Arguments: <library_name> <product_filter> (product_filter: "" for auto, "*" for all, "P1,P2" for specific)
resolve_library_products() {
    local lib_name="$1"
    local product_filter="$2"
    local lib_dir="$REPO_ROOT/libraries/$lib_name"
    local config="$lib_dir/library.json"

    [ -d "$lib_dir" ] || die "libraries/$lib_name/ does not exist."

    local product_count
    local use_legacy=false

    if [ -f "$config" ]; then
        product_count=$(json_array_len "$config" products)
    else
        # Legacy library without library.json — treat as single product
        use_legacy=true
        product_count=1
    fi

    if [ "$use_legacy" = true ]; then
        # No library.json: assume framework = module = library name, no subdirectory
        echo "$lib_name|$lib_name|$lib_name|"
        return
    fi

    # Determine which indices to include
    local indices=()
    if [ "$product_filter" = "*" ]; then
        for ((i=0; i<product_count; i++)); do
            indices+=("$i")
        done
    elif [ -n "$product_filter" ]; then
        IFS=',' read -ra req_names <<< "$product_filter"
        local all_names
        all_names=$(json_product_names "$config")
        for req in "${req_names[@]}"; do
            local found=false idx=0
            while IFS= read -r name; do
                if [ "$name" = "$req" ]; then
                    indices+=("$idx")
                    found=true
                    break
                fi
                ((idx++))
            done <<< "$all_names"
            [ "$found" = true ] || die "Product '$req' not found in libraries/$lib_name/library.json. Available: $(echo "$all_names" | tr '\n' ', ' | sed 's/,$//')"
        done
    elif [ "$product_count" -eq 1 ]; then
        indices=(0)
    else
        echo "Error: libraries/$lib_name/library.json has $product_count products. Specify which to include:" >&2
        echo "" >&2
        echo "Available products:" >&2
        json_product_names "$config" | while IFS= read -r name; do
            echo "  - $name" >&2
        done
        echo "" >&2
        echo "Use --products P1,P2 or --all-products." >&2
        exit 1
    fi

    for idx in "${indices[@]}"; do
        local framework module subdir is_internal
        framework=$(json_product_field "$config" "$idx" framework)
        module=$(json_product_field "$config" "$idx" module "$framework")
        subdir=$(json_product_field "$config" "$idx" subdirectory "")
        is_internal=$(json_product_bool "$config" "$idx" internal)
        # Fifth pipe-separated field: "true" when the product is internal
        # (framework built, no csproj/bindings — runtime-only dep).
        echo "$lib_name|$framework|$module|$subdir|$is_internal"
    done
}

# ── Argument Parsing ─────────────────────────────────────────────────────────

LIBRARY_NAME=""
MODULE_NAME=""
PRODUCTS_FLAG=""
ALL_PRODUCTS=false
FORCE=false
WITH_LIBS=()  # Array of "root:filter" pairs

while [[ $# -gt 0 ]]; do
    case "$1" in
        --module)
            MODULE_NAME="$2"
            shift 2
            ;;
        --products)
            PRODUCTS_FLAG="$2"
            shift 2
            ;;
        --all-products)
            ALL_PRODUCTS=true
            shift
            ;;
        --with)
            WITH_LIBS+=("$2")
            shift 2
            ;;
        --force)
            FORCE=true
            shift
            ;;
        -*)
            die "Unknown option '$1'"
            ;;
        *)
            if [ -z "$LIBRARY_NAME" ]; then
                LIBRARY_NAME="$1"
            else
                die "Unexpected argument '$1'"
            fi
            shift
            ;;
    esac
done

[ -n "$LIBRARY_NAME" ] || die "LibraryName is required. Usage: $0 <LibraryName> [--products P1,P2] [--all-products] [--with <root>[:P1,P2]] [--module M] [--force]"

# Validate flag combinations
if [ -n "$MODULE_NAME" ] && { [ -n "$PRODUCTS_FLAG" ] || [ "$ALL_PRODUCTS" = true ]; }; then
    die "--module cannot be used with --products or --all-products (module names come from library.json in multi-product mode)"
fi

# Derive lowercase variant
LIBRARY_NAME_LOWER=$(echo "$LIBRARY_NAME" | tr '[:upper:]' '[:lower:]')

# Validate library directory exists
[ -d "$REPO_ROOT/libraries/$LIBRARY_NAME" ] || die "libraries/$LIBRARY_NAME/ does not exist. Set up the library bindings first."

# ── Resolve All Products ─────────────────────────────────────────────────────

# Determine product filter for root library
ROOT_FILTER=""
if [ "$ALL_PRODUCTS" = true ]; then
    ROOT_FILTER="*"
elif [ -n "$PRODUCTS_FLAG" ]; then
    ROOT_FILTER="$PRODUCTS_FLAG"
fi

# Resolve root library products
RESOLVED_PRODUCTS=()
while IFS= read -r line; do
    RESOLVED_PRODUCTS+=("$line")
done < <(resolve_library_products "$LIBRARY_NAME" "$ROOT_FILTER")

# Resolve --with libraries
if [ ${#WITH_LIBS[@]} -gt 0 ]; then
    for with_spec in "${WITH_LIBS[@]}"; do
        local_root="${with_spec%%:*}"
        local_filter=""
        if [[ "$with_spec" == *":"* ]]; then
            local_filter="${with_spec#*:}"
        fi
        while IFS= read -r line; do
            RESOLVED_PRODUCTS+=("$line")
        done < <(resolve_library_products "$local_root" "$local_filter")
    done
fi

TOTAL_PRODUCTS=${#RESOLVED_PRODUCTS[@]}

# Apply --module override for single-product case
if [ -n "$MODULE_NAME" ] && [ "$TOTAL_PRODUCTS" -eq 1 ]; then
    IFS='|' read -r rp_lib rp_fw rp_mod rp_sub rp_int <<< "${RESOLVED_PRODUCTS[0]}"
    RESOLVED_PRODUCTS[0]="$rp_lib|$rp_fw|$MODULE_NAME|$rp_sub|$rp_int"
fi

# Extract the first module name for template placeholders (used in single-product templates)
IFS='|' read -r _ _ FIRST_MODULE _ _ <<< "${RESOLVED_PRODUCTS[0]}"

# If --module was not explicitly set and we're in single-product mode, use the resolved module
if [ -z "$MODULE_NAME" ]; then
    MODULE_NAME="$FIRST_MODULE"
fi

# ── Check Target Directory ───────────────────────────────────────────────────

TARGET_DIR="$REPO_ROOT/tests/${LIBRARY_NAME}.SimTests"
if [ -d "$TARGET_DIR" ]; then
    if [ "$FORCE" = true ]; then
        echo "Removing existing $TARGET_DIR (--force)"
        rm -rf "$TARGET_DIR"
    else
        die "$TARGET_DIR already exists. Use --force to overwrite."
    fi
fi

# Validate template directory
[ -d "$TEMPLATE_DIR" ] || die "Template directory not found at $TEMPLATE_DIR"

# ── Build Using Statements ───────────────────────────────────────────────────

USING_STATEMENTS=""
for entry in "${RESOLVED_PRODUCTS[@]}"; do
    IFS='|' read -r _ _ mod _ is_int <<< "$entry"
    # Internal products have no bindings namespace — skip the using statement
    [ "$is_int" = "true" ] && continue
    USING_STATEMENTS+="using ${mod};"$'\n'
done
# Remove trailing newline
USING_STATEMENTS="${USING_STATEMENTS%$'\n'}"

# ── Generate csproj ──────────────────────────────────────────────────────────

generate_csproj() {
    if [ "$TOTAL_PRODUCTS" -eq 1 ]; then
        # Single product: use template with sed substitution
        return 1  # Signal to use template
    fi

    # Multi-product: generate programmatically
    local csproj_path="$TARGET_DIR/${LIBRARY_NAME}SimTests.csproj"

    cat > "$csproj_path" << 'CSPROJ_HEADER'
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0-ios</TargetFramework>
    <RuntimeIdentifier Condition="'$(RuntimeIdentifier)' == ''">iossimulator-arm64</RuntimeIdentifier>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
CSPROJ_HEADER

    echo "    <ApplicationId>com.swiftbindings.${LIBRARY_NAME_LOWER}simtests</ApplicationId>" >> "$csproj_path"
    cat >> "$csproj_path" << 'CSPROJ_PROPS'
    <ApplicationDisplayVersion>1.0</ApplicationDisplayVersion>
    <ApplicationVersion>1</ApplicationVersion>
    <SupportedOSPlatformVersion>15.0</SupportedOSPlatformVersion>
    <IncludeSwiftBindingsRuntimeNative>true</IncludeSwiftBindingsRuntimeNative>
  </PropertyGroup>

  <ItemGroup>
    <None Include="Info.plist" />
  </ItemGroup>

  <!-- Reference library projects — brings in bindings and SwiftBindings.Runtime transitively -->
  <ItemGroup>
CSPROJ_PROPS

    for entry in "${RESOLVED_PRODUCTS[@]}"; do
        IFS='|' read -r lib_name framework module subdir is_int <<< "$entry"
        # Internal products have no csproj — they're NativeReference-only
        [ "$is_int" = "true" ] && continue
        # Discover the actual csproj filename on disk — it may differ from
        # SwiftBindings.${module}.csproj for vendor-prefixed packages
        # (e.g. SwiftBindings.Stripe.Core.csproj for module StripeCore).
        local disk_dir="$REPO_ROOT/libraries/${lib_name}"
        [ -n "$subdir" ] && disk_dir="$disk_dir/$subdir"
        local csproj_file csproj_name rel_dir
        csproj_file=$(discover_single_csproj "$disk_dir")
        csproj_name=$(basename "$csproj_file")
        rel_dir="../../libraries/${lib_name}"
        [ -n "$subdir" ] && rel_dir="$rel_dir/$subdir"
        echo "    <ProjectReference Include=\"${rel_dir}/${csproj_name}\" />" >> "$csproj_path"
    done

    cat >> "$csproj_path" << 'CSPROJ_REFS_END'
  </ItemGroup>

  <!-- Native frameworks must be referenced directly (NativeReference doesn't propagate through ProjectReference) -->
  <ItemGroup>
CSPROJ_REFS_END

    for entry in "${RESOLVED_PRODUCTS[@]}"; do
        IFS='|' read -r lib_name framework module subdir is_int <<< "$entry"
        local base_path
        if [ -n "$subdir" ]; then
            base_path="../../libraries/${lib_name}/${subdir}"
        else
            base_path="../../libraries/${lib_name}"
        fi

        cat >> "$csproj_path" << CSPROJ_NATIVE
    <NativeReference Include="${base_path}/${framework}.xcframework">
      <Kind>Framework</Kind>
    </NativeReference>
CSPROJ_NATIVE
    done

    cat >> "$csproj_path" << 'CSPROJ_NATIVE_END'
  </ItemGroup>

  <!-- Wrapper xcframework: must be in a Target (not static ItemGroup) so the Exists() check
       runs AFTER the library project builds. Static ItemGroups evaluate before any build targets,
       so on a clean build the wrapper doesn't exist yet and gets silently omitted. -->
  <Target Name="_AddSwiftWrapperReference"
          BeforeTargets="ResolveNativeReferences"
          DependsOnTargets="ResolveProjectReferences">
    <ItemGroup>
CSPROJ_NATIVE_END

    for entry in "${RESOLVED_PRODUCTS[@]}"; do
        IFS='|' read -r lib_name framework module subdir is_int <<< "$entry"
        # Internal products have no csproj — no binding wrapper to reference.
        [ "$is_int" = "true" ] && continue
        local base_path
        if [ -n "$subdir" ]; then
            base_path="../../libraries/${lib_name}/${subdir}"
        else
            base_path="../../libraries/${lib_name}"
        fi
        cat >> "$csproj_path" << CSPROJ_WRAPPER
      <NativeReference Include="${base_path}/obj/\$(Configuration)/net10.0-ios/swift-binding/${module}SwiftBindings.xcframework"
                       Condition="Exists('${base_path}/obj/\$(Configuration)/net10.0-ios/swift-binding/${module}SwiftBindings.xcframework')">
        <Kind>Framework</Kind>
      </NativeReference>
CSPROJ_WRAPPER
    done

    cat >> "$csproj_path" << 'CSPROJ_END'
    </ItemGroup>
  </Target>

</Project>
CSPROJ_END

    return 0
}

# ── Create Test Directory and Files ──────────────────────────────────────────

mkdir -p "$TARGET_DIR"

# Try generating multi-product csproj
if generate_csproj; then
    SKIP_CSPROJ_TEMPLATE=true
else
    SKIP_CSPROJ_TEMPLATE=false
fi

# Process each template file
for template in "$TEMPLATE_DIR"/*.template; do
    filename=$(basename "$template" .template)

    # Replace __LIBRARY_NAME__ in filename
    filename="${filename//__LIBRARY_NAME__/$LIBRARY_NAME}"

    # Skip csproj template if we generated it programmatically
    if [ "$SKIP_CSPROJ_TEMPLATE" = true ] && [[ "$filename" == *".csproj" ]]; then
        continue
    fi

    # Perform placeholder substitution and write to target
    sed -e "s/{{LIBRARY_NAME}}/$LIBRARY_NAME/g" \
        -e "s/{{LIBRARY_NAME_LOWER}}/$LIBRARY_NAME_LOWER/g" \
        -e "s/{{MODULE_NAME}}/$MODULE_NAME/g" \
        "$template" > "$TARGET_DIR/$filename"

    # Handle {{USING_STATEMENTS}} — sed can't handle multi-line, use python3
    if grep -q '{{USING_STATEMENTS}}' "$TARGET_DIR/$filename" 2>/dev/null; then
        python3 -c "
import sys
with open('$TARGET_DIR/$filename', 'r') as f:
    content = f.read()
content = content.replace('{{USING_STATEMENTS}}', '''$USING_STATEMENTS''')
with open('$TARGET_DIR/$filename', 'w') as f:
    f.write(content)
"
    fi
done

echo "Created tests/${LIBRARY_NAME}.SimTests/"
echo ""
echo "Resolved products ($TOTAL_PRODUCTS):"
for entry in "${RESOLVED_PRODUCTS[@]}"; do
    IFS='|' read -r lib_name framework module subdir is_int <<< "$entry"
    local_tag=""
    [ "$is_int" = "true" ] && local_tag=" [internal]"
    if [ -n "$subdir" ]; then
        echo "  - $framework (module: $module, path: libraries/$lib_name/$subdir)$local_tag"
    else
        echo "  - $framework (module: $module, path: libraries/$lib_name)$local_tag"
    fi
done
echo ""
echo "Next steps:"
echo "  1. Build library:     ./build.sh BuildLibrary --library ${LIBRARY_NAME}"
echo "  2. Build test app:    ./build.sh BuildTestApp --library ${LIBRARY_NAME}"
echo "     For device:        ./build.sh BuildTestApp --library ${LIBRARY_NAME} --device"
echo "     (The SDK csproj generates bindings automatically during build)"
echo "  3. Validate (sim):    ./build.sh ValidateSim --library ${LIBRARY_NAME} --timeout 15"
echo "     Validate (device): ./build.sh ValidateDevice --library ${LIBRARY_NAME} --timeout 30"
echo "  4. Add library-specific tests to Program.cs"
