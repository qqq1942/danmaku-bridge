#!/usr/bin/env bash
# 编译 DanmakuSpawn 插件，可选直接部署到 TShock 的插件目录
#
#   ./build.sh                只编译
#   ./build.sh --deploy       编译 + 复制到 ../plugins（compose 里的 -additionalplugins 目录）
set -euo pipefail

cd "$(dirname "$0")"

export PATH="$HOME/.dotnet-sdk:$PATH"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1

if ! command -v dotnet >/dev/null 2>&1; then
  echo "找不到 dotnet，先装 .NET 9 SDK：" >&2
  echo '  curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 9.0 --install-dir "$HOME/.dotnet-sdk" --no-path' >&2
  exit 1
fi

dotnet build -c Release -v minimal --nologo

DLL="bin/Release/DanmakuSpawn.dll"
echo
echo "构建完成：$(pwd)/$DLL"

if [ "${1:-}" = "--deploy" ]; then
  DEST="$(cd .. && pwd)/plugins"
  mkdir -p "$DEST"
  cp -v "$DLL" "$DEST/"
  echo
  echo "已部署到 $DEST/DanmakuSpawn.dll"
  echo "接下来重启容器让它加载： cd $(cd .. && pwd) && docker compose restart tshock"
else
  echo "部署：cp $DLL ../plugins/ && docker compose restart tshock"
fi
