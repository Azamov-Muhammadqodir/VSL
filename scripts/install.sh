#!/usr/bin/env bash
# Publishes VSL and registers it with the desktop: `vsl` command, app menu entry and icon.
set -euo pipefail

repo="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
dotnet_root="${DOTNET_ROOT:-$HOME/.dotnet}"
app_dir="$HOME/.local/share/VSL/app"
bin_dir="$HOME/.local/bin"
apps_dir="$HOME/.local/share/applications"
icons_dir="$HOME/.local/share/icons/hicolor"

echo "Publishing VSL to $app_dir ..."
rm -rf "$app_dir"
"$dotnet_root/dotnet" publish "$repo/src/VSL/VSL.csproj" -c Release -r linux-x64 --self-contained false -o "$app_dir" -nologo -v:quiet

mkdir -p "$bin_dir" "$apps_dir" "$icons_dir/scalable/apps" "$icons_dir/256x256/apps"
cat > "$bin_dir/vsl" <<LAUNCHER
#!/bin/sh
# Desktop sessions do not read ~/.bashrc, so point the apphost at the user's .NET install explicitly.
export DOTNET_ROOT="\${DOTNET_ROOT:-$dotnet_root}"
exec "$app_dir/VSL" "\$@"
LAUNCHER
chmod +x "$bin_dir/vsl"

cp "$repo/src/VSL/Assets/vsl.svg" "$icons_dir/scalable/apps/vsl.svg"
cp "$repo/src/VSL/Assets/vsl.png" "$icons_dir/256x256/apps/vsl.png"

cat > "$apps_dir/vsl.desktop" <<DESKTOP
[Desktop Entry]
Type=Application
Name=VSL
GenericName=Visual Studio for Linux
Comment=C#, ASP.NET Core and Blazor IDE
Exec=$bin_dir/vsl %f
Icon=vsl
Terminal=false
Categories=Development;IDE;
Keywords=visual;studio;csharp;dotnet;blazor;razor;
StartupWMClass=VSL
DESKTOP

command -v update-desktop-database >/dev/null && update-desktop-database "$apps_dir" || true
command -v gtk-update-icon-cache >/dev/null && gtk-update-icon-cache -q -t "$icons_dir" || true
echo "Installed. Run 'vsl path/to/App.sln' or open VSL from the applications menu."
