#!/usr/bin/env bash
# EiseChat 服务器首次安装 / 升级脚本（Ubuntu 22.04/24.04，用 root 执行）。
#
# 前置：
#   1) 已按 infra/publish-linux.sh 在开发机产出 chat-server-x.y.z-linux-x64.zip 并上传到服务器；
#   2) 服务器已放行安全组 22/80/443，域名 A 记录已指向本机；
#   3) 已安装 Caddy（可选，见 docs/deploy.md）。
#
# 用法：
#   sudo bash infra/install.sh chat-server-1.0.0-linux-x64.zip
#
# 本脚本不写死任何凭据：JWT 密钥由服务端首次启动自动生成到 /opt/eisechat-server/secrets.json（权限 600）。
set -euo pipefail

APP_DIR=/opt/eisechat-server
DATA_DIR=/var/lib/eisechat
SERVICE_NAME=eisechat
SERVICE_FILE="/etc/systemd/system/${SERVICE_NAME}.service"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PACKAGE="${1:-}"

log() { echo "[install] $*"; }
die() { echo "[install] 错误：$*" >&2; exit 1; }

[ "$(id -u)" = "0" ] || die "请用 root 执行（sudo bash infra/install.sh <zip>）"
command -v systemctl >/dev/null 2>&1 || die "未找到 systemd（本脚本面向 Ubuntu 22.04/24.04）"
command -v unzip >/dev/null 2>&1 || die "未找到 unzip，请先 apt-get install -y unzip"
[ -n "$PACKAGE" ] && [ -f "$PACKAGE" ] || die "用法：sudo bash infra/install.sh <chat-server-x.y.z-linux-x64.zip>"

log "创建系统用户与目录"
id -u eisechat >/dev/null 2>&1 || useradd --system --home "$DATA_DIR" --shell /usr/sbin/nologin eisechat
install -d -o eisechat -g eisechat -m 755 "$APP_DIR"
install -d -o eisechat -g eisechat -m 750 "$DATA_DIR" "$DATA_DIR/files" "$DATA_DIR/downloads"

log "解压应用包到 $APP_DIR（覆盖升级，保留数据目录）"
tmp_dir="$(mktemp -d)"
trap 'rm -rf "$tmp_dir"' EXIT
unzip -q "$PACKAGE" -d "$tmp_dir"
cp -a "$tmp_dir"/. "$APP_DIR"/
chown -R eisechat:eisechat "$APP_DIR"
chmod 755 "$APP_DIR/Chat.Server"

log "安装 systemd 服务"
[ -f "$SCRIPT_DIR/eisechat.service" ] || die "缺少 $SCRIPT_DIR/eisechat.service"
cp "$SCRIPT_DIR/eisechat.service" "$SERVICE_FILE"
systemctl daemon-reload
systemctl enable "$SERVICE_NAME"
systemctl restart "$SERVICE_NAME"

log "等待服务健康检查（127.0.0.1:6580/api/health）"
healthy=0
for _ in $(seq 1 30); do
  if curl -fsS --max-time 2 http://127.0.0.1:6580/api/health >/dev/null 2>&1; then
    healthy=1
    break
  fi
  sleep 1
done
if [ "$healthy" = "1" ]; then
  log "服务已启动：$(curl -fsS http://127.0.0.1:6580/api/health)"
else
  die "健康检查失败，请查看：journalctl -u ${SERVICE_NAME} -n 100 --no-pager"
fi

log "Caddy 配置（可选，需已安装 Caddy）"
if command -v caddy >/dev/null 2>&1; then
  if [ -f "$SCRIPT_DIR/caddy/Caddyfile" ]; then
    backup="/etc/caddy/Caddyfile.bak-$(date +%Y%m%d-%H%M%S)"
    [ -f /etc/caddy/Caddyfile ] && cp /etc/caddy/Caddyfile "$backup" && log "已备份原 Caddyfile 到 $backup"
    cp "$SCRIPT_DIR/caddy/Caddyfile" /etc/caddy/Caddyfile
    log "已写入 /etc/caddy/Caddyfile；请先把 chat.example.com 改成真实域名，再执行 systemctl reload caddy"
  fi
else
  log "未检测到 Caddy（可跳过，Web 端可通过 http://<公网IP>:6580 临时访问，不推荐长期使用）"
fi

cat <<EOF

[install] 完成。
  应用目录：$APP_DIR（首次启动自动生成 secrets.json，权限 600）
  密钥文件：$APP_DIR/secrets.json（JWT/SMTP 凭据，仅 root 与 eisechat 用户可读）
  数据目录：$DATA_DIR（chat.db / files / downloads）
  服务管理：systemctl status|restart|stop $SERVICE_NAME
  查看日志：journalctl -u $SERVICE_NAME -n 100 --no-pager
  健康检查：curl http://127.0.0.1:6580/api/health

注册账号：打开 https://<你的域名>/ 在登录页切换到「注册」用邮箱验证码注册（首个账号角色为 user）。
管理员提权（v1 无后台提权界面，需手工执行一次）：
  sqlite3 $DATA_DIR/chat.db "UPDATE users SET role='admin' WHERE email='<你的邮箱>';"
管理员登录后可在 Web 端 /admin 查看用户与审计日志（按 M3 计划）。

下一步：配置邮件通道（见 docs/deploy.md §5），否则验证码只输出到服务端控制台（生产环境不可用）。
EOF
