#!/bin/bash
# Build script for WebViewPlugin.bundle
# Creates a native macOS plugin for Unity/Tuanjie using WKWebView

set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SOURCE_FILE="$SCRIPT_DIR/WebViewPlugin.mm"
BUNDLE_NAME="WebViewPlugin.bundle"
# Output to Plugins/macOS/ directory (one level up)
OUTPUT_DIR="$(dirname "$SCRIPT_DIR")"
BUNDLE_DIR="$OUTPUT_DIR/$BUNDLE_NAME"

echo "=== Building WebViewPlugin for macOS ==="
echo "Source: $SOURCE_FILE"
echo "Output: $BUNDLE_DIR"
echo ""

# Clean previous builds (both in source dir and output dir)
echo "Cleaning previous builds..."
if [ -d "$SCRIPT_DIR/$BUNDLE_NAME" ]; then
    echo "  Removing old bundle from source directory..."
    rm -rf "$SCRIPT_DIR/$BUNDLE_NAME"
fi
if [ -f "$SCRIPT_DIR/$BUNDLE_NAME.meta" ]; then
    echo "  Removing old .meta file from source directory..."
    rm -f "$SCRIPT_DIR/$BUNDLE_NAME.meta"
fi
if [ -d "$BUNDLE_DIR" ]; then
    echo "  Removing old bundle from output directory..."
    rm -rf "$BUNDLE_DIR"
fi
if [ -f "$BUNDLE_DIR.meta" ]; then
    echo "  Removing old .meta file from output directory..."
    rm -f "$BUNDLE_DIR.meta"
fi

# Create bundle structure
mkdir -p "$BUNDLE_DIR/Contents/MacOS"

# Compile for both architectures
echo "Compiling for arm64 and x86_64..."
clang++ -dynamiclib \
    -ObjC++ \
    -std=c++14 \
    -arch arm64 \
    -arch x86_64 \
    -framework Foundation \
    -framework AppKit \
    -framework WebKit \
    -fobjc-arc \
    -O2 \
    -o "$BUNDLE_DIR/Contents/MacOS/WebViewPlugin" \
    "$SOURCE_FILE"

# Verify architectures
echo "Verifying architectures..."
lipo -info "$BUNDLE_DIR/Contents/MacOS/WebViewPlugin"

# Create Info.plist
cat > "$BUNDLE_DIR/Contents/Info.plist" << 'EOF'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleDevelopmentRegion</key>
    <string>en</string>
    <key>CFBundleExecutable</key>
    <string>WebViewPlugin</string>
    <key>CFBundleIdentifier</key>
    <string>cn.tuanjie.codely.webview</string>
    <key>CFBundleInfoDictionaryVersion</key>
    <string>6.0</string>
    <key>CFBundleName</key>
    <string>WebViewPlugin</string>
    <key>CFBundlePackageType</key>
    <string>BNDL</string>
    <key>CFBundleShortVersionString</key>
    <string>1.0</string>
    <key>CFBundleVersion</key>
    <string>1</string>
    <key>NSPrincipalClass</key>
    <string></string>
</dict>
</plist>
EOF

echo ""
echo "=== Build completed successfully! ==="
echo ""
echo "📦 Bundle location: $BUNDLE_DIR"
echo "📏 Bundle size: $(du -sh "$BUNDLE_DIR" | cut -f1)"
echo ""
echo "✅ Plugin is ready to use in Unity/Tuanjie!"
echo "   Location: Plugins/macOS/WebViewPlugin.bundle"
