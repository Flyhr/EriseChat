#!/usr/bin/env bash
# EiseChat 服务器 Docker 一键部署 / 升级脚本（需 root 或 docker 组）。
#
# 用法（服务器上，源码解压到 /opt/eisechat/src 后执行）：
#   sudo bash /opt/eisechat/src/infra/docker/deploy.sh
#
# 可选环境变量：
#   EISECHAT_HOME    部署根目录（默认 /opt/eisechat；源码在 $EISECHAT_HOME/src，数据在 data/）
#   APP_VERSION      镜像 tag / 发布版本（默认 1.1.0）
#   DOTNET_REGISTRY  基础镜像源（默认 mcr.microsoft.com；拉取失败可换 mcr.m.daocloud.io）
#   NODE_IMAGE       Node 构建镜像（默认 node:24-alpine；拉取失败可换 docker.m.daocloud.io/library/node:24-alpine）
#   SKIP_BUILD=1     只重启已有镜像，不重新构建
#
# 首次部署会自动生成 $EISECHAT_HOME/.env（含随机 JWT 密钥，权限 600）；SMTP 等配置按需填写后重启。
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
HOME_DIR="${EISECHAT_HOME:-/opt/eisechat}"
DATA_DIR="$HOME_DIR/data"
ENV_FILE="$HOME_DIR/.env"
VERSION="${APP_VERSION:-1.1.0}"
PORT="${EISECHAT_PORT:-6580}"

log() { echo "[deploy] $*"; }
die() { echo "[deploy] 错误：$*" >&2; exit 1; }

[ "$(id -u)" = "0" ] || die "请用 root 执行（sudo bash infra/docker/deploy.sh）"
command -v docker >/dev/null 2>&1 || die "未安装 Docker（见 docs/deploy.md §2）"
docker compose version >/dev/null 2>&1 || die "未安装 docker compose 插件（apt install docker-compose-v2）"

log "准备目录：$HOME_DIR"
install -d -m 755 "$HOME_DIR"
install -d -m 750 "$DATA_DIR" "$DATA_DIR/files" "$DATA_DIR/downloads"

# 容器内以官方镜像的非 root 用户 app(1654) 运行，数据卷需要可写
chown -R 1654:1654 "$DATA_DIR"
chmod 750 "$DATA_DIR" "$DATA_DIR/files" "$DATA_DIR/downloads"

if [ ! -f "$ENV_FILE" ]; then
  log "生成 $ENV_FILE（JWT 密钥随机生成，权限 600；SMTP 项待填写）"
  key="$(python3 -c 'import base64,os;print(base64.b64encode(os.urandom(32)).decode())' 2>/dev/null || openssl rand -base64 32)"
  (
    umask 077
    cat > "$ENV_FILE" <<EOF
# EiseChat 部署配置（docker compose env_file）——权限 600，勿提交、勿外传
ASPNETCORE_ENVIRONMENT=Production
Jwt__Key=$key

# --- SMTP 邮件通道（生产必须配置；不配置则无法发送验证码）---
# Email__SmtpHost=smtp.qq.com
# Email__SmtpPort=465
# Email__SmtpUseSsl=true
# Email__SmtpUser=
# Email__SmtpPassword=
# Email__FromAddress=
# Email__FromName=EiseChat
EOF
  )
  chmod 600 "$ENV_FILE"
else
  log "复用已有 $ENV_FILE（改 SMTP/密钥后重新执行本脚本生效；docker compose restart 不会重读 .env）"
fi

export EISECHAT_DATA_DIR="$DATA_DIR"
export EISECHAT_PORT="$PORT"
export EISECHAT_ENV_FILE="$ENV_FILE"
export APP_VERSION="$VERSION"
export DOTNET_REGISTRY="${DOTNET_REGISTRY:-mcr.microsoft.com}"
export NODE_IMAGE="${NODE_IMAGE:-node:24-alpine}"

cd "$ROOT"
if [ "${SKIP_BUILD:-0}" = "1" ]; then
  log "跳过构建，启动已有镜像 ${APP_VERSION}"
  docker compose up -d
else
  log "构建镜像并启动（首次需拉取基础镜像，耗时取决于网络）"
  docker compose up -d --build
fi

log "等待健康检查 http://127.0.0.1:${PORT}/api/health"
healthy=0
for _ in $(seq 1 90); do
  if curl -fsS --max-time 2 "http://127.0.0.1:${PORT}/api/health" >/dev/null 2>&1; then
    healthy=1
    break
  fi
  sleep 2
done
if [ "$healthy" = "1" ]; then
  log "服务已就绪：$(curl -fsS "http://127.0.0.1:${PORT}/api/health")"
else
  log "健康检查失败，最近日志："
  docker compose logs --tail=80 eisechat || true
  die "部署失败，请根据上方日志排查（docker compose logs -f eisechat）"
fi

docker compose ps
cat <<EOF

[deploy] 完成。
  部署目录：$HOME_DIR（源码 src/，数据 data/，配置 .env）
  端口：仅绑定 127.0.0.1:${PORT}（可用 EISECHAT_PORT 覆盖）
  管理：docker compose ps | logs -f eisechat | restart | down
  更新：改了代码后按 docs/update-deploy.md 重新构建部署
  下一步：配置 SMTP（编辑 $ENV_FILE 后重新执行本脚本，勿用 docker compose restart）；
          HTTPS 入口用 Tailscale Serve（docs/deploy.md §3.4）；管理员提权见同文档。
EOF
