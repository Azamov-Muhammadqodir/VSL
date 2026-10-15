#!/usr/bin/env bash
# Linux packages for VSL: .deb (Debian/Ubuntu: `vsl` command, menu entry and icon; apt installs the
# dependencies), .AppImage (any distribution, no install) and .tar.gz (unpack anywhere).
# None needs a .NET runtime: VSL is published self-contained. Working on solutions still needs the
# .NET SDK, as for any .NET IDE. Used by .github/workflows/release.yml and runnable locally:
#   dotnet publish src/VSL -c Release -r linux-x64 --self-contained -o out -p:Version=1.2.3
#   deploy/linux/package.sh out 1.2.3 dist
# The AppImage needs appimagetool (APPIMAGETOOL or on PATH); without it the .deb and .tar.gz are built.
set -euo pipefail

SRC=$(realpath "$1")
VERSION=$2
DIST=$(mkdir -p "$3" && realpath "$3")
HERE=$(cd "$(dirname "$0")" && pwd)
ASSETS=$(realpath "$HERE/../../src/VSL/Assets")
WORK=$(mktemp -d)
trap 'rm -rf "$WORK"' EXIT

if [[ ! -x "$SRC/VSL" ]]; then
  echo "$SRC/VSL not found: publish VSL first (see the header of this script)" >&2
  exit 1
fi

# Copies the published app, keeping it executable and everything else readable.
copy_app() {
  mkdir -p "$1"
  cp -a "$SRC/." "$1/"
  chmod -R u=rwX,go=rX "$1"
  chmod 755 "$1/VSL"
}

# ---- .deb ----
# Avalonia renders through fontconfig/X11; .NET needs ICU and OpenSSL. Their sonames differ between
# releases, so each dependency lists the alternatives. git powers Git Changes and the change margin.
DEB=$WORK/deb
mkdir -p "$DEB/DEBIAN" "$DEB/usr/bin" "$DEB/usr/share/applications" \
  "$DEB/usr/share/icons/hicolor/256x256/apps" "$DEB/usr/share/icons/hicolor/scalable/apps"
copy_app "$DEB/opt/vsl"
ln -s /opt/vsl/VSL "$DEB/usr/bin/vsl"
install -m 644 "$HERE/vsl.desktop" "$DEB/usr/share/applications/vsl.desktop"
install -m 644 "$ASSETS/vsl.png" "$DEB/usr/share/icons/hicolor/256x256/apps/vsl.png"
install -m 644 "$ASSETS/vsl.svg" "$DEB/usr/share/icons/hicolor/scalable/apps/vsl.svg"
cat > "$DEB/DEBIAN/control" <<CONTROL
Package: vsl
Version: $VERSION
Section: devel
Priority: optional
Architecture: amd64
Maintainer: Nodirbek Abdulaxadov <noreply@github.com>
Homepage: https://github.com/nbkabdulakhadov/VSL
Installed-Size: $(du -sk "$DEB" | cut -f1)
Depends: libc6, libstdc++6, libfontconfig1, libx11-6, libice6, libsm6,
 libssl3t64 | libssl3,
 libicu78 | libicu77 | libicu76 | libicu75 | libicu74 | libicu72 | libicu71 | libicu70
Recommends: git
Suggests: dotnet-sdk-10.0
Description: VSL - Visual Studio for Linux
 A native Linux IDE for C#, ASP.NET Core and Blazor with a Visual Studio look
 and workflow: Roslyn IntelliSense, build and run, debugger, Test Explorer,
 Git, NuGet, terminal and a Claude Code window. Needs the .NET SDK to build
 and run solutions.
CONTROL
chmod -R u=rwX,go=rX "$DEB/DEBIAN" "$DEB/usr"
dpkg-deb --build --root-owner-group "$DEB" "$DIST/vsl_${VERSION}_amd64.deb" >/dev/null
echo "$DIST/vsl_${VERSION}_amd64.deb"

# ---- .tar.gz ----
TAR=$WORK/tar/vsl-$VERSION
copy_app "$TAR"
install -m 644 "$HERE/vsl.desktop" "$ASSETS/vsl.png" "$ASSETS/vsl.svg" "$TAR/"
tar -C "$WORK/tar" -czf "$DIST/vsl-${VERSION}-linux-x64.tar.gz" "vsl-$VERSION"
echo "$DIST/vsl-${VERSION}-linux-x64.tar.gz"

# ---- AppImage ----
TOOL=${APPIMAGETOOL:-$(command -v appimagetool || true)}
if [[ -z "$TOOL" ]]; then
  echo "appimagetool not found: AppImage skipped" >&2
  exit 0
fi
APP=$WORK/VSL.AppDir
copy_app "$APP/usr/lib/vsl"
install -m 644 "$ASSETS/vsl.png" "$APP/vsl.png"
install -m 644 "$HERE/vsl.desktop" "$APP/vsl.desktop"
cat > "$APP/AppRun" <<'RUN'
#!/bin/sh
HERE=$(dirname "$(readlink -f "$0")")
# Without ICU on the host, .NET runs in invariant-globalization mode instead of failing.
if ! ldconfig -p 2>/dev/null | grep -q libicuuc; then export DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1; fi
exec "$HERE/usr/lib/vsl/VSL" "$@"
RUN
chmod 755 "$APP/AppRun"
ARCH=x86_64 APPIMAGE_EXTRACT_AND_RUN=1 "$TOOL" --no-appstream "$APP" "$DIST/VSL-${VERSION}-x86_64.AppImage" >/dev/null
echo "$DIST/VSL-${VERSION}-x86_64.AppImage"
