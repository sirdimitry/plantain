#!/bin/bash
set -euo pipefail
PROJECT_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$PROJECT_ROOT"
VERSION="$(cat VERSION)"
if ! [[ "$VERSION" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
  echo "Invalid VERSION" >&2
  exit 1
fi
APP="dist/Plantain.app"
mkdir -p build/arm64 build/x86_64 build/AppIcon.iconset "$APP/Contents/MacOS" "$APP/Contents/Resources"
for ARCH in arm64 x86_64; do
  swiftc -swift-version 5 -parse-as-library -module-name Plantain \
    -target "$ARCH-apple-macos13.0" -O \
    -module-cache-path "build/$ARCH/cache" \
    Sources/*.swift -o "build/$ARCH/Plantain"
done
lipo -create build/arm64/Plantain build/x86_64/Plantain -output "$APP/Contents/MacOS/Plantain"
strip -S -x "$APP/Contents/MacOS/Plantain"
swift -module-cache-path build/arm64/cache scripts/GenerateIcons.swift \
  Assets/AppIcon.png build/AppIcon.iconset "$APP/Contents/Resources/AppIcon.icns"
cp Assets/AppIcon.png "$APP/Contents/Resources/AppArtwork.png"
cp Assets/GitHub.png "$APP/Contents/Resources/GitHub.png"
cat > "$APP/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
<key>CFBundleExecutable</key><string>Plantain</string>
<key>CFBundleIdentifier</key><string>local.sirdimitry.diskloader</string>
<key>CFBundleName</key><string>Подорожник</string>
<key>CFBundleDisplayName</key><string>Подорожник</string>
<key>CFBundlePackageType</key><string>APPL</string>
<key>CFBundleShortVersionString</key><string>$VERSION</string>
<key>CFBundleVersion</key><string>4</string>
<key>CFBundleIconFile</key><string>AppIcon</string>
<key>LSMinimumSystemVersion</key><string>13.0</string>
<key>NSHighResolutionCapable</key><true/>
<key>NSPrincipalClass</key><string>NSApplication</string>
</dict></plist>
PLIST
codesign --force --options runtime --sign - "$APP"
codesign --verify --deep --strict "$APP"
echo "Built: $APP ($VERSION, Apple Silicon + Intel)"
if [[ "${1:-}" == "--dmg" ]]; then
  STAGE="$(mktemp -d "$PROJECT_ROOT/build/dmg.XXXXXX")"
  trap 'rm -rf "$STAGE"' EXIT
  ditto "$APP" "$STAGE/Plantain.app"
  ln -s /Applications "$STAGE/Applications"
  cp docs/USAGE.ru.txt "$STAGE/Прочитайте меня.txt"
  hdiutil create -volname "Подорожник $VERSION" -srcfolder "$STAGE" \
    -format UDZO -ov "dist/Plantain-$VERSION-universal.dmg"
fi
