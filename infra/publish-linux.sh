#!/usr/bin/env bash
# 发布 Linux self-contained 包（在开发机执行；产物不入库）。
#
# 用法：bash infra/publish-linux.sh [版本号]
# 输出：docs/test/publish-linux/chat-server-<版本>-linux-x64.zip
# 说明：self-contained 包自带 .NET 运行时，服务器无需安装 dotnet；上传后交给 infra/install.sh。
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
VERSION="${1:-1.0.0}"
OUT_DIR="${EISECHAT_PUBLISH_DIR:-$ROOT/docs/test/publish-linux}"
APP_OUT="$OUT_DIR/app"
ZIP="$OUT_DIR/chat-server-${VERSION}-linux-x64.zip"
PYTHON="${EISECHAT_PYTHON:-python}"

command -v dotnet >/dev/null 2>&1 || { echo "未找到 dotnet，请先安装 .NET SDK" >&2; exit 1; }

echo "[publish] 清理输出目录 $OUT_DIR"
rm -rf "$OUT_DIR"
mkdir -p "$APP_OUT"

echo "[publish] dotnet publish（linux-x64，self-contained，版本 $VERSION）"
dotnet publish "$ROOT/server/Chat.Server/Chat.Server.csproj" \
  -c Release \
  -r linux-x64 \
  --self-contained true \
  -p:Version="$VERSION" \
  -o "$APP_OUT"

echo "[publish] 打包 zip"
"$PYTHON" -m zipfile -c "$ZIP" "$APP_OUT"/*

SIZE="$(du -h "$ZIP" | cut -f1)"
SHA="$(sha256sum "$ZIP" | cut -d' ' -f1)"
echo "[publish] 完成：$ZIP"
echo "[publish] 大小：$SIZE"
echo "[publish] SHA256：$SHA"
echo "[publish] 下一步：上传到服务器后执行 sudo bash infra/install.sh $ZIP"
