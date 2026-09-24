#!/usr/bin/env bash
# 打包发布版本。
#   scripts/publish.sh mac    macOS（Apple Silicon）.app，自带运行时
#   scripts/publish.sh win    Windows x64 单文件 exe，自带运行时
#   scripts/publish.sh        两个都打
# 产物放在 dist/ 下。
set -euo pipefail
cd "$(dirname "$0")/.."

DOTNET="${DOTNET:-dotnet}"
if ! command -v "$DOTNET" >/dev/null 2>&1 && [ -x "$HOME/.dotnet/dotnet" ]; then
  DOTNET="$HOME/.dotnet/dotnet"
  export DOTNET_ROOT="$HOME/.dotnet"
fi
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

VERSION="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' GeoJsonEditor.csproj | head -1)"
APP_NAME="GeoJSON 层级编辑器"
TARGET="${1:-all}"
mkdir -p dist

# 用 Python 打 zip：条目名按 UTF-8 标记写入，Windows 资源管理器解压中文文件名不乱码。
make_zip() {
  python3 - "$1" "$2" <<'PY'
import os, sys, zipfile
src, dst = sys.argv[1], sys.argv[2]
base = os.path.dirname(os.path.abspath(src))
with zipfile.ZipFile(dst, "w", zipfile.ZIP_DEFLATED, compresslevel=9) as z:
    for root, dirs, files in os.walk(src):
        for name in sorted(files):
            full = os.path.join(root, name)
            info = zipfile.ZipInfo.from_file(full, os.path.relpath(full, base))
            info.compress_type = zipfile.ZIP_DEFLATED
            with open(full, "rb") as f:
                z.writestr(info, f.read())
PY
}

publish_mac() {
  local out="dist/obj/osx-arm64"
  rm -rf "$out"
  "$DOTNET" publish -c Release -r osx-arm64 --self-contained true -p:DebugType=none -o "$out"

  local app="dist/$APP_NAME.app"
  rm -rf "$app"
  mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"
  cp -R "$out/." "$app/Contents/MacOS/"
  cp assets/AppIcon.icns "$app/Contents/Resources/AppIcon.icns"
  cp THIRD-PARTY-NOTICES.md "$app/Contents/Resources/"
  cat > "$app/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key><string>$APP_NAME</string>
  <key>CFBundleDisplayName</key><string>$APP_NAME</string>
  <key>CFBundleIdentifier</key><string>app.geojson-editor.hierarchy</string>
  <key>CFBundleExecutable</key><string>GeoJsonEditor</string>
  <key>CFBundleIconFile</key><string>AppIcon</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>$VERSION</string>
  <key>CFBundleVersion</key><string>$VERSION</string>
  <key>LSMinimumSystemVersion</key><string>12.0</string>
  <key>NSHighResolutionCapable</key><true/>
  <key>LSApplicationCategoryType</key><string>public.app-category.productivity</string>
</dict>
</plist>
PLIST
  # 本机自签名（ad-hoc），在自己的 Mac 上直接双击运行
  codesign --force --deep --sign - "$app" >/dev/null 2>&1 || true

  rm -f "dist/GeoJsonEditor-$VERSION-macos-arm64.zip"
  (cd dist && ditto -c -k --keepParent "$APP_NAME.app" "GeoJsonEditor-$VERSION-macos-arm64.zip")
  echo "macOS: dist/$APP_NAME.app"
  echo "       dist/GeoJsonEditor-$VERSION-macos-arm64.zip"
}

publish_win() {
  local out="dist/obj/win-x64"
  rm -rf "$out"
  "$DOTNET" publish -c Release -r win-x64 --self-contained true \
    -p:PublishSingleFile=true \
    -p:IncludeNativeLibrariesForSelfExtract=true \
    -p:EnableCompressionInSingleFile=true \
    -p:DebugType=none \
    -o "$out"

  local name="GeoJsonEditor-$VERSION-win-x64"
  local pkg="dist/$name"
  rm -rf "$pkg"
  mkdir -p "$pkg"
  cp "$out/GeoJsonEditor.exe" "$pkg/"
  cp -R "$out/samples" "$pkg/"
  cp THIRD-PARTY-NOTICES.md "$pkg/"
  cat > "$pkg/使用说明.txt" <<'TXT'
GeoJSON 层级编辑器（Windows x64）

双击 GeoJsonEditor.exe 运行，不需要另外安装 .NET。
首次启动会打开 samples 目录里的示例数据；把 .geojson 文件拖进窗口即可打开或导入。

同时编辑几张地图：文件 → 新建窗口（Ctrl+Shift+N），或者再开一个 GeoJsonEditor.exe。
选中要素按 Ctrl+C 复制，到另一个窗口按 Ctrl+V 粘贴，下级、属性和样式一起带过去；
也能粘贴其他软件复制的 GeoJSON、WKT 或“经度, 纬度”文本。

需要 Windows 10 1809 或更高版本。在线底图需要联网，瓦片缓存在
%LOCALAPPDATA%\GeoJsonEditor\tiles。

第三方组件的许可声明见 THIRD-PARTY-NOTICES.md。
TXT
  # 记事本能正确识别的 UTF-8 BOM + CRLF 换行
  python3 - "$pkg/使用说明.txt" <<'PY'
import sys
p = sys.argv[1]
text = open(p, encoding="utf-8").read().replace("\r\n", "\n").replace("\n", "\r\n")
open(p, "w", encoding="utf-8-sig", newline="").write(text)
PY

  rm -f "dist/$name.zip"
  make_zip "$pkg" "dist/$name.zip"
  echo "Windows: dist/$name/GeoJsonEditor.exe"
  echo "         dist/$name.zip"
}

case "$TARGET" in
  mac) publish_mac ;;
  win) publish_win ;;
  all) publish_mac; publish_win ;;
  *) echo "用法：$0 [mac|win|all]" >&2; exit 1 ;;
esac
