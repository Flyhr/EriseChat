#!/usr/bin/env bash
# EriseChat 源码包打包脚本（固定路径 + 固定文件名，覆盖旧包）
#
# 用法（开发机仓库根目录，Windows 必须用 Git Bash；不要用 cmd 的 bash/WSL）：
#   "D:\Git\bin\bash.exe" infra/package-src.sh   # 输出 <仓库>/dist/erisechat-src.zip
#   bash infra/package-src.sh /tmp/a.zip         # 指定输出路径（仍建议保持固定名）
#
# 前置：工作区必须完全干净（git status --short 无任何输出，含未跟踪文件/测试改动）；
#       测试文件统一放根级 tests/（已 gitignore），否则脚本会拒绝打包。
#
# 产物：dist/erisechat-src.zip（dist/ 已 gitignore），始终覆盖同名旧文件，
#       服务器侧上传目标固定为 ~/erisechat-src.zip，部署命令因此无需随提交变化。
#
# 依赖：git、python3（或 python，仅用于行尾自检）。
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
OUT="${1:-$ROOT/dist/erisechat-src.zip}"

cd "$ROOT"

if [ -n "$(git status --short)" ]; then
  echo "[package] 错误：工作区不干净，先提交改动再打包（git status --short 必须为空，含未跟踪/测试文件；测试放根级 tests/）" >&2
  exit 1
fi

mkdir -p "$(dirname "$OUT")"
rm -f "$OUT"
git archive --format=zip -o "$OUT" HEAD

# 行尾自检：Linux 脚本/容器文件必须是 LF（Windows core.autocrlf 曾导致部署失败）。
# 依次尝试：$PYTHON → python3 → python（需真的可执行，Windows 应用商店的 python3 存根会失败）→ unzip+grep。
PYBIN=""
if [ -n "${PYTHON:-}" ]; then
  PYBIN="$PYTHON"
elif command -v python3 >/dev/null 2>&1 && python3 -c 'pass' >/dev/null 2>&1; then
  PYBIN="python3"
elif command -v python >/dev/null 2>&1 && python -c 'pass' >/dev/null 2>&1; then
  PYBIN="python"
fi

bad=""
if [ -n "$PYBIN" ]; then
  "$PYBIN" - "$OUT" <<'PY'
import sys, zipfile

zip_path = sys.argv[1]
bad = []
with zipfile.ZipFile(zip_path) as z:
    for name in z.namelist():
        if name.endswith((".sh", ".service", ".conf", ".yml", ".yaml")) or name.endswith("Dockerfile"):
            if b"\r\n" in z.read(name):
                bad.append(name)
if bad:
    print("[package] 错误：以下文件含 CRLF，检查 .gitattributes 与换行配置：", *bad, sep="\n  ")
    sys.exit(1)
PY
elif command -v unzip >/dev/null 2>&1; then
  for f in $(git ls-files); do
    case "$f" in
      *.sh|*.service|*.conf|*.yml|*.yaml|Dockerfile|*/Dockerfile) ;;
      *) continue ;;
    esac
    if unzip -p "$OUT" "$f" 2>/dev/null | grep -q $'\r'; then
      echo "[package] 错误：$f 含 CRLF，检查 .gitattributes 与换行配置" >&2
      exit 1
    fi
  done
else
  echo "[package] 警告：未找到可用的 python/unzip，跳过行尾自检（可设置 PYTHON 指定解释器）" >&2
fi

echo "[package] 输出：$OUT"
ls -l "$OUT" | awk '{print "[package] 大小：" $5 " 字节"}'
sha256sum "$OUT" | awk '{print "[package] SHA256：" $1}'
