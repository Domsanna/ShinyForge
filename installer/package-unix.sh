#!/usr/bin/env bash
# Packages the published macOS / Linux builds (called by build-unix.ps1, runs in WSL / Linux).
#   $1 = dist-unix folder (with publish-osx-arm64, publish-osx-x64, publish-linux-x64, ShinyForge.icns)
#   $2 = version (1.X.Z)   $3 = macOS bundle version (the same 1.X.Z)
set -euo pipefail
DIST="$1"; VERSION="$2"; BUILD="$3"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
echo "Packaging ShinyForge $VERSION in $WORK"

need() { command -v "$1" >/dev/null || { echo "missing tool: $1 (sudo apt install $2)"; exit 1; }; }
need zip zip; need dpkg-deb dpkg; need curl curl

# rcodesign (Apple code signing from Linux, ad-hoc - no certificate) for the macOS app.
RCODESIGN="$WORK/rcodesign"
curl -sSL -o "$WORK/rcs.tar.gz" "https://github.com/indygreg/apple-platform-rs/releases/download/apple-codesign%2F0.29.0/apple-codesign-0.29.0-x86_64-unknown-linux-musl.tar.gz"
curl -sSL -o "$WORK/rcs.sha256" "https://github.com/indygreg/apple-platform-rs/releases/download/apple-codesign%2F0.29.0/apple-codesign-0.29.0-x86_64-unknown-linux-musl.tar.gz.sha256"
echo "$(cut -d' ' -f1 < "$WORK/rcs.sha256")  $WORK/rcs.tar.gz" | sha256sum -c - >/dev/null
tar -xzf "$WORK/rcs.tar.gz" -C "$WORK"
mv "$WORK"/apple-codesign-*/rcodesign "$RCODESIGN"
chmod +x "$RCODESIGN"

backup_readme() {
  mkdir -p "$1/Backup Tools"
  printf '%s\n' "Put programs ShinyForge needs here (FFmpeg, Python 3, pokeldn, esptool). ShinyForge first checks the computer, then this folder (sub-folders included)." > "$1/Backup Tools/README.txt"
}
portable_flag() { printf '%s\n' "ShinyForge portable mode: settings and data are stored in the Data folder next to the program." > "$1/portable.flag"; }

# ---------------------------------------------------------------- macOS
for ARCH in arm64 x64; do
  SRC="$DIST/publish-osx-$ARCH"
  APP="$WORK/mac-$ARCH/ShinyForge.app"
  mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
  cp -r "$SRC"/. "$APP/Contents/MacOS/"
  cp "$DIST/ShinyForge.icns" "$APP/Contents/Resources/ShinyForge.icns"
  cat > "$APP/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key><string>ShinyForge</string>
  <key>CFBundleDisplayName</key><string>ShinyForge</string>
  <key>CFBundleIdentifier</key><string>io.github.domsanna.shinyforge</string>
  <key>CFBundleExecutable</key><string>ShinyForge</string>
  <key>CFBundleIconFile</key><string>ShinyForge.icns</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>$VERSION</string>
  <key>CFBundleVersion</key><string>$BUILD</string>
  <key>LSMinimumSystemVersion</key><string>13.0</string>
  <key>NSHighResolutionCapable</key><true/>
  <key>NSCameraUsageDescription</key><string>ShinyForge records gameplay from your capture card.</string>
  <key>NSMicrophoneUsageDescription</key><string>ShinyForge records the game sound from your capture card.</string>
</dict>
</plist>
PLIST
  find "$APP/Contents/MacOS" -type f \( -name "ShinyForge" -o -name "*.dylib" -o -name "createdump" \) -exec chmod +x {} +
  # Apple silicon only runs signed code: an ad-hoc signature (no developer certificate - Gatekeeper still asks once).
  "$RCODESIGN" sign "$APP" >/dev/null 2>&1 || { echo "codesign failed for $ARCH"; "$RCODESIGN" sign "$APP"; exit 1; }
  (cd "$WORK/mac-$ARCH" && zip -qry "$DIST/ShinyForge-macOS-$ARCH.zip" ShinyForge.app)
  PORT="$WORK/macport-$ARCH/ShinyForge Portable"
  mkdir -p "$PORT"
  cp -a "$APP" "$PORT/"
  portable_flag "$PORT"; backup_readme "$PORT"
  (cd "$WORK/macport-$ARCH" && zip -qry "$DIST/ShinyForge-macOS-$ARCH-Portable.zip" "ShinyForge Portable")
  echo "macOS $ARCH done"
done

# ---------------------------------------------------------------- Linux
SRC="$DIST/publish-linux-x64"
PORT="$WORK/linport/ShinyForge Portable"
mkdir -p "$PORT"
cp -r "$SRC"/. "$PORT/"
chmod +x "$PORT/ShinyForge"
portable_flag "$PORT"; backup_readme "$PORT"
tar -C "$WORK/linport" -czf "$DIST/ShinyForge-Linux-x64-Portable.tar.gz" "ShinyForge Portable"

PKG="$WORK/deb"
mkdir -p "$PKG/DEBIAN" "$PKG/opt/shinyforge" "$PKG/usr/bin" "$PKG/usr/share/applications" "$PKG/usr/share/icons/hicolor/256x256/apps"
cp -r "$SRC"/. "$PKG/opt/shinyforge/"
chmod -R u=rwX,go=rX "$PKG/opt/shinyforge"
chmod 755 "$PKG/opt/shinyforge/ShinyForge"
ln -s /opt/shinyforge/ShinyForge "$PKG/usr/bin/shinyforge"
cp "$SRC/Assets/ShinyForge.png" "$PKG/usr/share/icons/hicolor/256x256/apps/shinyforge.png"
cat > "$PKG/usr/share/applications/shinyforge.desktop" <<DESKTOP
[Desktop Entry]
Type=Application
Name=ShinyForge
Comment=Pokémon shiny hunting, recording, Pokédex tracking and save tools
Exec=/opt/shinyforge/ShinyForge
Icon=shinyforge
Terminal=false
Categories=Game;Utility;
DESKTOP
SIZE=$(du -sk "$PKG/opt" | cut -f1)
cat > "$PKG/DEBIAN/control" <<CONTROL
Package: shinyforge
Version: $VERSION
Section: games
Priority: optional
Architecture: amd64
Installed-Size: $SIZE
Depends: libc6, libfontconfig1, libx11-6, libice6, libsm6
Recommends: ffmpeg, python3, python3-opencv, python3-numpy, python3-yaml, xdg-utils
Maintainer: ShinyForge <143049964+Domsanna@users.noreply.github.com>
Homepage: https://github.com/Domsanna/ShinyForge
Description: All-in-one Pokémon companion
 Automatic shiny hunting, gameplay recording, Pokédex tracking, Checkpoint saves,
 save export and wireless trades (pokeldn) for Nintendo Switch and 3DS games.
CONTROL
dpkg-deb --build --root-owner-group "$PKG" "$DIST/shinyforge_${VERSION}_amd64.deb" >/dev/null
echo "Linux done"
