#!/usr/bin/env bash
# Wraps a published Avalonia binary into a macOS .app bundle.
#
# Usage: make-macos-bundle.sh <publish-dir> <output-dir> <rid>
#   publish-dir  Path to dotnet publish output (must contain KillerPDF binary)
#   output-dir   Where to write KillerPDF.app
#   rid          osx-x64 or osx-arm64 (used only in the bundle name)
#
# Produces:
#   <output-dir>/KillerPDF.app/Contents/Info.plist
#   <output-dir>/KillerPDF.app/Contents/MacOS/KillerPDF  (the published binary)
#   <output-dir>/KillerPDF.app/Contents/Resources/kp-icon.icns  (if available)
#
# Unsigned by design. Code-signing / notarization is a separate concern that
# requires an Apple Developer account and `codesign` / `xcrun notarytool`.

set -euo pipefail

PUBLISH_DIR="${1:?publish-dir required}"
OUTPUT_DIR="${2:?output-dir required}"
RID="${3:?rid required (osx-x64 or osx-arm64)}"

VERSION="${VERSION:-1.4.1}"
BUNDLE_ID="net.opticnoise.killerpdf"

APP_DIR="${OUTPUT_DIR}/KillerPDF.app"
CONTENTS_DIR="${APP_DIR}/Contents"
MACOS_DIR="${CONTENTS_DIR}/MacOS"
RESOURCES_DIR="${CONTENTS_DIR}/Resources"

rm -rf "${APP_DIR}"
mkdir -p "${MACOS_DIR}" "${RESOURCES_DIR}"

# Copy the published binary
if [ ! -x "${PUBLISH_DIR}/KillerPDF" ]; then
    echo "error: expected executable at ${PUBLISH_DIR}/KillerPDF" >&2
    exit 1
fi
cp "${PUBLISH_DIR}/KillerPDF" "${MACOS_DIR}/KillerPDF"
chmod +x "${MACOS_DIR}/KillerPDF"

# Optional icon
if [ -f "src/KillerPDF.Avalonia/Assets/kp-icon.icns" ]; then
    cp "src/KillerPDF.Avalonia/Assets/kp-icon.icns" "${RESOURCES_DIR}/kp-icon.icns"
fi

cat > "${CONTENTS_DIR}/Info.plist" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleName</key>
    <string>KillerPDF</string>
    <key>CFBundleDisplayName</key>
    <string>KillerPDF</string>
    <key>CFBundleExecutable</key>
    <string>KillerPDF</string>
    <key>CFBundleIdentifier</key>
    <string>${BUNDLE_ID}</string>
    <key>CFBundlePackageType</key>
    <string>APPL</string>
    <key>CFBundleShortVersionString</key>
    <string>${VERSION}</string>
    <key>CFBundleVersion</key>
    <string>${VERSION}</string>
    <key>CFBundleIconFile</key>
    <string>kp-icon.icns</string>
    <key>LSMinimumSystemVersion</key>
    <string>10.15</string>
    <key>NSHighResolutionCapable</key>
    <true/>
    <key>NSPrincipalClass</key>
    <string>NSApplication</string>
    <key>CFBundleDocumentTypes</key>
    <array>
        <dict>
            <key>CFBundleTypeName</key>
            <string>PDF document</string>
            <key>CFBundleTypeRole</key>
            <string>Editor</string>
            <key>LSItemContentTypes</key>
            <array>
                <string>com.adobe.pdf</string>
            </array>
        </dict>
    </array>
</dict>
</plist>
EOF

echo "Bundle created at: ${APP_DIR} (${RID})"
ls -la "${MACOS_DIR}"
