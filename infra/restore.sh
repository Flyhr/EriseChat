#!/usr/bin/env bash
# EiseChat 备份恢复脚本（演练或故障恢复；用 root 执行）。
#
# 用法：
#   sudo bash infra/restore.sh /var/backups/eisechat/eisechat-20261025-0200.tar.gz
#
# 行为：停服务 → 备份当前数据到 *.pre-restore-* → 解包覆盖 → 修复权限 → 启动 → 健康检查。
# 注意：恢复会覆盖当前 /var/lib/eisechat 下的 chat.db 与 files/，请确认归档正确。
set -euo pipefail

DATA_DIR="${EISECHAT_DATA_DIR:-/var/lib/eisechat}"
SERVICE_NAME=eisechat
ARCHIVE="${1:-}"

log() { echo "[restore] $*"; }
die() { echo "[restore] 错误：$*" >&2; exit 1; }

[ "$(id -u)" = "0" ] || die "请用 root 执行"
[ -n "$ARCHIVE" ] && [ -f "$ARCHIVE" ] || die "用法：sudo bash infra/restore.sh <eisechat-YYYYMMDD-HHMM.tar.gz>"
command -v systemctl >/dev/null 2>&1 || die "未找到 systemd"

log "停止服务 $SERVICE_NAME"
systemctl stop "$SERVICE_NAME" 2>/dev/null || true

safety="$DATA_DIR.pre-restore-$(date +%Y%m%d-%H%M%S)"
mkdir -p "$safety"
log "备份当前数据到 $safety"
[ -f "$DATA_DIR/chat.db" ] && cp -a "$DATA_DIR/chat.db" "$safety"/
[ -d "$DATA_DIR/files" ] && cp -a "$DATA_DIR/files" "$safety"/
[ -d "$DATA_DIR/downloads" ] && cp -a "$DATA_DIR/downloads" "$safety"/

tmp_dir="$(mktemp -d)"
trap 'rm -rf "$tmp_dir"' EXIT
tar -xzf "$ARCHIVE" -C "$tmp_dir"
[ -f "$tmp_dir/chat.db" ] || die "归档中缺少 chat.db"

log "恢复 chat.db 与文件目录"
install -d -o eisechat -g eisechat -m 750 "$DATA_DIR" "$DATA_DIR/files" "$DATA_DIR/downloads"
cp -f "$tmp_dir/chat.db" "$DATA_DIR/chat.db"
if [ -f "$tmp_dir/files.tar.gz" ]; then
  rm -rf "$DATA_DIR/files"
  tar -xzf "$tmp_dir/files.tar.gz" -C "$DATA_DIR"
fi
chown -R eisechat:eisechat "$DATA_DIR"
chmod 600 "$DATA_DIR/chat.db"

log "启动服务并等待健康检查"
systemctl start "$SERVICE_NAME"
healthy=0
for _ in $(seq 1 30); do
  if curl -fsS --max-time 2 http://127.0.0.1:8080/api/health >/dev/null 2>&1; then
    healthy=1
    break
  fi
  sleep 1
done
[ "$healthy" = "1" ] || die "恢复后健康检查失败，请查看 journalctl -u ${SERVICE_NAME} -n 100 --no-pager"

log "恢复完成：$(curl -fsS http://127.0.0.1:8080/api/health)"
log "原有数据保留在 $safety（确认无误后可手工删除）"
log "建议立即用两个账号抽查最近消息与文件下载，并记录本次演练（docs/deploy.md §7）。"
