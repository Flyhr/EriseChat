#!/usr/bin/env bash
# 发布 Linux self-contained 包（在开发机执行；产物不入库）。
#
# 用法：bash infra/publish-linux.sh [版本号]
# 输出：docs/test/publish-linux/chat-server-<版本>-linux-x64.zip
# 说明：self-contained 包自带 .NET 运行时，服务器无需安装 dotnet；上传后交给 infra/install.sh。
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
VERSION="${1:-1.0.0}"
OUT_DIR="${ERISECHAT_PUBLISH_DIR:-$ROOT/docs/test/publish-linux}"
APP_OUT="$OUT_DIR/app"
ZIP="$OUT_DIR/chat-server-${VERSION}-linux-x64.zip"
PYTHON="${ERISECHAT_PYTHON:-python}"
WEB_APP="$ROOT/apps/web"
WEBROOT="$ROOT/server/Chat.Server/wwwroot"

command -v dotnet >/dev/null 2>&1 || { echo "未找到 dotnet，请先安装 .NET SDK" >&2; exit 1; }

echo "[publish] 清理输出目录 $OUT_DIR"
rm -rf "$OUT_DIR"
mkdir -p "$APP_OUT"

# 服务端在 wwwroot/ 提供 Web 前端（构建产物，gitignore）；不先构建会把旧包发上线。
if [ "${ERISECHAT_SKIP_WEB:-0}" = "1" ]; then
  [ -f "$WEBROOT/index.html" ] || { echo "ERISECHAT_SKIP_WEB=1 但 $WEBROOT/index.html 不存在" >&2; exit 1; }
  echo "[publish] 跳过 Web 构建，沿用现有 wwwroot"
else
  command -v npm >/dev/null 2>&1 || { echo "未找到 npm（如需跳过前端构建，设置 ERISECHAT_SKIP_WEB=1 并确认 wwwroot 已就绪）" >&2; exit 1; }
  echo "[publish] 构建 Web 前端（apps/web，base '/' 产物）"
  (cd "$WEB_APP" && npm run build:web)
  echo "[publish] 复制 apps/web/dist/web → server/Chat.Server/wwwroot"
  rm -rf "$WEBROOT"
  mkdir -p "$WEBROOT"
  cp -a "$WEB_APP/dist/web/." "$WEBROOT/"
fi

echo "[publish] dotnet publish（linux-x64，self-contained，版本 $VERSION）"
dotnet publish "$ROOT/server/Chat.Server/Chat.Server.csproj" \
  -c Release \
  -r linux-x64 \
  --self-contained true \
  -p:Version="$VERSION" \
  -o "$APP_OUT"

echo "[publish] 打包 zip"
"$PYTHON" -m zipfile -c "$ZIP" "$APP_OUT"/*

echo "[publish] 校验发布包不含 secrets.json（密钥纪律）"
if "$PYTHON" - "$ZIP" <<'PY'
import sys, zipfile
with zipfile.ZipFile(sys.argv[1]) as archive:
    leaked = [name for name in archive.namelist() if name.split('/')[-1] == 'secrets.json']
if leaked:
    print('secrets.json found in package:', leaked)
    raise SystemExit(1)
PY
then
  echo "[publish] 包内无密钥文件"
else
  rm -f "$ZIP"
  echo "[publish] 错误：发布包含 secrets.json，已删除产物，请修复后重试" >&2
  exit 1
fi

SIZE="$(du -h "$ZIP" | cut -f1)"
SHA="$(sha256sum "$ZIP" | cut -d' ' -f1)"
echo "[publish] 完成：$ZIP"
echo "[publish] 大小：$SIZE"
echo "[publish] SHA256：$SHA"
echo "[publish] 下一步：上传到服务器后执行 sudo bash infra/install.sh $ZIP"
