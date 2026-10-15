#!/usr/bin/env bash
# Copies the Material Icon Theme (MIT, https://github.com/material-extensions/vscode-material-icon-theme)
# from a VS Code extension package into src/VSL/Assets/MaterialIcons: the dark icons, the file/folder name
# mapping and the license. Usage: scripts/update-material-icons.sh pkief.material-icon-theme-X.vsix
set -euo pipefail

VSIX=$(realpath "$1")
OUT="$(cd "$(dirname "$0")/.." && pwd)/src/VSL/Assets/MaterialIcons"
WORK=$(mktemp -d)
trap 'rm -rf "$WORK"' EXIT

unzip -q "$VSIX" 'extension/icons/*' 'extension/dist/material-icons.json' 'extension/LICENSE*' 'extension/package.json' -d "$WORK"
rm -rf "$OUT"
mkdir -p "$OUT/icons"
# VSL has a dark theme only, so the light variants are left out.
find "$WORK/extension/icons" -name '*.svg' ! -name '*_light.svg' -exec cp {} "$OUT/icons/" \;
cp "$WORK/extension/dist/material-icons.json" "$OUT/material-icons.json"
cp "$WORK"/extension/LICENSE* "$OUT/LICENSE.txt"
version=$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["version"])' "$WORK/extension/package.json")
echo "Material Icon Theme $version (MIT, Material Extensions): https://github.com/material-extensions/vscode-material-icon-theme" > "$OUT/SOURCE.txt"
echo "Copied $(ls "$OUT/icons" | wc -l) icons from Material Icon Theme $version"
