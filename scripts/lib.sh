#!/bin/bash
# Shared helpers for library.json parsing.
# Source this file from other scripts: source "$(dirname "$0")/lib.sh"

die() { echo "Error: $*" >&2; exit 1; }

json_field() {
    # json_field <file> <field> [default]
    local default_val="${3:-}"
    local val
    val=$(python3 -c "
import json, sys
data = json.load(open('$1'))
keys = '$2'.split('.')
for k in keys:
    if k.startswith('['):
        data = data[int(k.strip('[]'))]
    else:
        data = data.get(k)
        if data is None:
            print('$default_val')
            sys.exit(0)
print(data)
" 2>/dev/null)
    echo "$val"
}

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
    # json_product_bool <file> <index> <field> -> "true" or ""
    local val
    val=$(json_product_field "$1" "$2" "$3" "")
    # Python prints True for json true; normalize
    if [ "$val" = "True" ] || [ "$val" = "true" ]; then
        echo "true"
    fi
}

json_product_names() {
    # Print all product framework names, one per line
    python3 -c "
import json
data = json.load(open('$1'))
for p in data['products']:
    print(p['framework'])
"
}

json_build_settings() {
    # Print KEY=VALUE lines from the buildSettings object in library.json
    python3 -c "
import json
data = json.load(open('$1'))
for k, v in data.get('buildSettings', {}).items():
    print(f'{k}={v}')
"
}
