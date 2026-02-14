#!/bin/bash
# Scaffold a new simulator test app from the sim-test template.
#
# Usage: ./scripts/new-sim-test.sh <LibraryName> [--module <ModuleName>] [--force]
#
# Arguments:
#   LibraryName           PascalCase library name (e.g. Nuke, CryptoSwift)
#   --module ModuleName   Swift module name if different from LibraryName
#   --force               Overwrite existing test directory

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
TEMPLATE_DIR="$REPO_ROOT/templates/sim-test"

# Parse arguments
LIBRARY_NAME=""
MODULE_NAME=""
FORCE=false

while [[ $# -gt 0 ]]; do
    case "$1" in
        --module)
            MODULE_NAME="$2"
            shift 2
            ;;
        --force)
            FORCE=true
            shift
            ;;
        -*)
            echo "Error: Unknown option '$1'"
            echo "Usage: $0 <LibraryName> [--module <ModuleName>] [--force]"
            exit 1
            ;;
        *)
            if [ -z "$LIBRARY_NAME" ]; then
                LIBRARY_NAME="$1"
            else
                echo "Error: Unexpected argument '$1'"
                exit 1
            fi
            shift
            ;;
    esac
done

if [ -z "$LIBRARY_NAME" ]; then
    echo "Error: LibraryName is required"
    echo "Usage: $0 <LibraryName> [--module <ModuleName>] [--force]"
    exit 1
fi

# Default module name = library name
if [ -z "$MODULE_NAME" ]; then
    MODULE_NAME="$LIBRARY_NAME"
fi

# Derive lowercase variant
LIBRARY_NAME_LOWER=$(echo "$LIBRARY_NAME" | tr '[:upper:]' '[:lower:]')

# Validate library directory exists
if [ ! -d "$REPO_ROOT/libraries/$LIBRARY_NAME" ]; then
    echo "Error: libraries/$LIBRARY_NAME/ does not exist."
    echo "Set up the library bindings first, then run this script."
    exit 1
fi

# Check target directory
TARGET_DIR="$REPO_ROOT/tests/${LIBRARY_NAME}.SimTests"
if [ -d "$TARGET_DIR" ]; then
    if [ "$FORCE" = true ]; then
        echo "Removing existing $TARGET_DIR (--force)"
        rm -rf "$TARGET_DIR"
    else
        echo "Error: $TARGET_DIR already exists."
        echo "Use --force to overwrite."
        exit 1
    fi
fi

# Validate template directory
if [ ! -d "$TEMPLATE_DIR" ]; then
    echo "Error: Template directory not found at $TEMPLATE_DIR"
    exit 1
fi

# Create target directory
mkdir -p "$TARGET_DIR"

# Process each template file
for template in "$TEMPLATE_DIR"/*.template; do
    filename=$(basename "$template" .template)

    # Replace __LIBRARY_NAME__ in filename
    filename="${filename//__LIBRARY_NAME__/$LIBRARY_NAME}"

    # Perform placeholder substitution and write to target
    sed -e "s/{{LIBRARY_NAME}}/$LIBRARY_NAME/g" \
        -e "s/{{LIBRARY_NAME_LOWER}}/$LIBRARY_NAME_LOWER/g" \
        -e "s/{{MODULE_NAME}}/$MODULE_NAME/g" \
        "$template" > "$TARGET_DIR/$filename"
done

# Make shell scripts executable
chmod +x "$TARGET_DIR"/*.sh

echo "Created tests/${LIBRARY_NAME}.SimTests/"
echo ""
echo "Next steps:"
echo "  1. Generate bindings: cd libraries/${LIBRARY_NAME} && ./generate-bindings.sh"
echo "  2. Build test app:    cd tests/${LIBRARY_NAME}.SimTests && ./build-testapp.sh"
echo "  3. Boot simulator:    xcrun simctl boot <device-udid>"
echo "  4. Validate:          ./validate-sim.sh 15"
echo "  5. Add library-specific tests to Program.cs"
