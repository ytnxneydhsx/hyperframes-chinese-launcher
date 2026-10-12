#!/bin/bash
set -euo pipefail
SOURCE_DIR="$(cd "$(dirname "$0")" && pwd)"
RESOURCE_DIR="$(cd "$SOURCE_DIR/../shared/resources" && pwd)"
DEFAULT_OUTPUT_DIR="$SOURCE_DIR/../dist/macos"
OUTPUT_DIR="${LAUNCHER_OUTPUT_DIR:-$DEFAULT_OUTPUT_DIR}"
mkdir -p "$OUTPUT_DIR"
OUTPUT_DIR="$(cd "$OUTPUT_DIR" && pwd)"
APP="$OUTPUT_DIR/HyperFrames中文启动器.app"
ARCH="${LAUNCHER_ARCH:-universal}"
case "$ARCH" in arm64|x86_64|universal) ;; *) echo 'LAUNCHER_ARCH must be universal, arm64 or x86_64' >&2; exit 1 ;; esac
for RESOURCE in translator.js translations.zh-CN.json dynamic.zh-CN.json HyperFrames.icns; do
  if [[ ! -f "$RESOURCE_DIR/$RESOURCE" ]]; then
    echo "Missing resource: $RESOURCE_DIR/$RESOURCE" >&2
    exit 1
  fi
done
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
BUILD_DIR="$(mktemp -d "${TMPDIR:-/tmp}/hfzh-build.XXXXXX")"
trap 'rm -rf "$BUILD_DIR"' EXIT
if [[ "$ARCH" == universal ]]; then
  ARCHITECTURES=(arm64 x86_64)
else
  ARCHITECTURES=("$ARCH")
fi
for TARGET_ARCH in "${ARCHITECTURES[@]}"; do
  xcrun swiftc -swift-version 5 -parse-as-library -O \
    -target "$TARGET_ARCH-apple-macosx13.0" -framework AppKit -framework Foundation \
    "$SOURCE_DIR/Launcher.swift" -o "$BUILD_DIR/launcher-$TARGET_ARCH"
done
if [[ "$ARCH" == universal ]]; then
  xcrun lipo -create "$BUILD_DIR/launcher-arm64" "$BUILD_DIR/launcher-x86_64" \
    -output "$APP/Contents/MacOS/HyperFramesChineseLauncher"
else
  cp "$BUILD_DIR/launcher-$ARCH" "$APP/Contents/MacOS/HyperFramesChineseLauncher"
fi
cp "$SOURCE_DIR/Info.plist" "$APP/Contents/Info.plist"
cp "$RESOURCE_DIR/translator.js" "$APP/Contents/Resources/translator.js"
cp "$RESOURCE_DIR/translations.zh-CN.json" "$APP/Contents/Resources/translations.zh-CN.json"
cp "$RESOURCE_DIR/dynamic.zh-CN.json" "$APP/Contents/Resources/dynamic.zh-CN.json"
if [[ -f "$RESOURCE_DIR/HyperFrames.icns" ]]; then
  cp "$RESOURCE_DIR/HyperFrames.icns" "$APP/Contents/Resources/HyperFrames.icns"
fi
plutil -lint "$APP/Contents/Info.plist"
codesign --force --sign - "$APP"
codesign --verify --strict "$APP"
echo "$APP"
