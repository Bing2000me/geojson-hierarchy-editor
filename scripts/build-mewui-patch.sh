#!/usr/bin/env bash
# 重新生成 vendor/packages 里打过补丁的 Aprillz.MewUI.Platform.Win32 0.21.1.1。
#
# MewUI 0.21.1 在 Windows 上销毁窗口时无条件调用 ReleaseCapture()。Tooltip 关闭时是异步销毁的，
# 会把按钮刚拿到的鼠标捕获一起释放，结果工具栏按钮第一次点击只关掉 Tooltip，要点第二次才生效。
# 上游问题 https://github.com/aprillz/MewUI/issues/253 ，修复提交 ce00cdc（0.21.1 发布之后），还没有发布新版本。
# 这里用 v0.21.1 的源码加上这一处补丁，只重新编译 Win32 平台程序集：程序集版本仍是 0.21.1.0，
# 其余 MewUI 包继续用 NuGet 上的 0.21.1。
# 上游发布包含这个修复的版本后：删掉 vendor/ 目录和 nuget.config，去掉 csproj 里对
# Aprillz.MewUI.Platform.Win32 的单独引用，把所有 Aprillz.MewUI* 包升到新版本即可。
#
# 用法：
#   scripts/build-mewui-patch.sh                               自动下载 v0.21.1 源码
#   MEWUI_SRC=/path/to/MewUI scripts/build-mewui-patch.sh      使用已有的 v0.21.1 源码目录（不会被修改）
set -euo pipefail
cd "$(dirname "$0")/.."
ROOT="$PWD"

DOTNET="${DOTNET:-dotnet}"
if ! command -v "$DOTNET" >/dev/null 2>&1 && [ -x "$HOME/.dotnet/dotnet" ]; then
  DOTNET="$HOME/.dotnet/dotnet"
  export DOTNET_ROOT="$HOME/.dotnet"
fi
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

PKG_ID="Aprillz.MewUI.Platform.Win32"
PKG_VERSION="0.21.1.1"
PATCH="$ROOT/vendor/mewui-0.21.1-win32-release-capture.patch"

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

if [ -n "${MEWUI_SRC:-}" ]; then
  cp -R "$MEWUI_SRC" "$WORK/src"
else
  git clone --quiet --depth 1 --branch v0.21.1 https://github.com/aprillz/MewUI.git "$WORK/src"
fi

cd "$WORK/src"
if git apply --check "$PATCH" 2>/dev/null; then
  git apply "$PATCH"
elif git apply --reverse --check "$PATCH" 2>/dev/null; then
  echo "源码里已经有这个补丁，直接编译。"
else
  echo "补丁无法应用：源码不是 MewUI v0.21.1？" >&2
  exit 1
fi

"$DOTNET" build src/MewUI.Platform.Win32/MewUI.Platform.Win32.csproj -c Release \
  -p:MewUITargetFrameworks=net10.0 \
  -p:ContinuousIntegrationBuild=true

OUT="src/MewUI.Platform.Win32/bin/Release/net10.0"
PACK="$WORK/pack"
mkdir -p "$PACK/lib/net10.0"
cp "$OUT/$PKG_ID.dll" "$PACK/lib/net10.0/"
[ -f "$OUT/$PKG_ID.xml" ] && cp "$OUT/$PKG_ID.xml" "$PACK/lib/net10.0/"
cp LICENSE "$PACK/LICENSE.txt"
cp "$PATCH" "$PACK/"

cat > "$PACK/$PKG_ID.nuspec" <<NUSPEC
<?xml version="1.0" encoding="utf-8"?>
<package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
  <metadata>
    <id>$PKG_ID</id>
    <version>$PKG_VERSION</version>
    <authors>Aprillz</authors>
    <license type="file">LICENSE.txt</license>
    <projectUrl>https://github.com/aprillz/MewUI</projectUrl>
    <description>Win32 platform host for Aprillz.MewUI 0.21.1, rebuilt from the v0.21.1 source with upstream commit ce00cdc, the fix for issue #253 (release the mouse capture only from the window that holds it). Local package for GeoJsonEditor; not an official release.</description>
    <dependencies>
      <group targetFramework="net10.0">
        <dependency id="Aprillz.MewUI.Core" version="0.21.1" exclude="Build,Analyzers" />
      </group>
    </dependencies>
  </metadata>
  <files>
    <file src="lib/**" target="lib" />
    <file src="LICENSE.txt" target="" />
    <file src="$(basename "$PATCH")" target="" />
  </files>
</package>
NUSPEC

cat > "$PACK/pack.csproj" <<'CSPROJ'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <NoBuild>true</NoBuild>
    <IncludeBuildOutput>false</IncludeBuildOutput>
    <NuspecFile>Aprillz.MewUI.Platform.Win32.nuspec</NuspecFile>
    <NuspecBasePath>$(MSBuildProjectDirectory)</NuspecBasePath>
    <NoWarn>$(NoWarn);NU5100;NU5128</NoWarn>
  </PropertyGroup>
</Project>
CSPROJ

mkdir -p "$ROOT/vendor/packages"
rm -f "$ROOT/vendor/packages/"*.nupkg
"$DOTNET" pack "$PACK/pack.csproj" -o "$ROOT/vendor/packages" >/dev/null

# NuGet 按“包名 + 版本”缓存，同版本重新打包后要清掉旧缓存，下次还原才会用新包
rm -rf "$HOME/.nuget/packages/aprillz.mewui.platform.win32/$PKG_VERSION"

echo "已生成：$(ls "$ROOT/vendor/packages/"*.nupkg)"
