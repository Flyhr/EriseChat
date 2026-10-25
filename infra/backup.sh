#!/usr/bin/env bash
# EiseChat 每日备份脚本（建议 root/cron 执行；默认每天 02:00）。
#
# 内容：SQLite 在线备份（VACUUM INTO，一致性快照）+ files/downloads 打包 → 单个 tar.gz；
# 保留最近 30 份；可选 rsync/scp 同步到异机；失败以非 0 退出并写日志（日志在备份目录）。
#
# 用法（可放 /etc/cron.d/eisechat-backup）：
#   0 2 * * * root EISECHAT_REMOTE=user@backup-host:/srv/eisechat-backup bash /opt/eisechat-server/infra/backup.sh
set -euo pipefail

DATA_DIR="${EISECHAT_DATA_DIR:-/var/lib/eisechat}"
BACKUP_DIR="${EISECHAT_BACKUP_DIR:-/var/backups/eisechat}"
KEEP="${EISECHAT_BACKUP_KEEP:-30}"
REMOTE="${EISECHAT_REMOTE:-}"
STAMP="$(date +%Y%m%d-%H%M)"
ARCHIVE="$BACKUP_DIR/eisechat-$STAMP.tar.gz"

mkdir -p "$BACKUP_DIR"
LOG="$BACKUP_DIR/backup.log"
exec >>"$LOG" 2>&1

trap 'echo "[backup] $(date -Is) 失败（行号 $LINENO）"; exit 1' ERR

echo "== [backup] $(date -Is) 开始 =="
[ -f "$DATA_DIR/chat.db" ] || { echo "[backup] 未找到 $DATA_DIR/chat.db"; exit 1; }

tmp_dir="$(mktemp -d)"
trap 'rm -rf "$tmp_dir"' EXIT

# 1) SQLite 在线备份：VACUUM INTO 生成一致快照（服务运行中安全）。
if command -v sqlite3 >/dev/null 2>&1; then
  sqlite3 "$DATA_DIR/chat.db" "VACUUM INTO '$tmp_dir/chat.db';"
else
  echo "[backup] WARN 未找到 sqlite3，退化为文件拷贝（一致性无法保证，建议 apt-get install -y sqlite3）"
  cp "$DATA_DIR/chat.db" "$tmp_dir/chat.db"
fi
chmod 600 "$tmp_dir/chat.db"

# 2) 文件目录打包（files/ 消息附件、downloads/ 安装包；不存在则跳过）。
tar -C "$DATA_DIR" -czf "$tmp_dir/files.tar.gz" files downloads 2>/dev/null \
  || tar -C "$DATA_DIR" -czf "$tmp_dir/files.tar.gz" files 2>/dev/null \
  || tar -C "$DATA_DIR" -czf "$tmp_dir/files.tar.gz" --files-from /dev/null

# 3) 合并为单个归档并设置权限。
tar -C "$tmp_dir" -czf "$ARCHIVE" chat.db files.tar.gz
chmod 600 "$ARCHIVE"
echo "[backup] 已生成 $ARCHIVE（$(du -h "$ARCHIVE" | cut -f1)）"

# 4) 可选：同步到异机（对象存储可在此接 rclone/aws cli）。
if [ -n "$REMOTE" ]; then
  if command -v rsync >/dev/null 2>&1; then
    rsync -az --timeout=60 "$ARCHIVE" "$REMOTE"/
    echo "[backup] 已同步到 $REMOTE"
  else
    scp "$ARCHIVE" "$REMOTE"/
    echo "[backup] 已 scp 到 $REMOTE"
  fi
fi

# 5) 保留最近 N 份（按修改时间倒序删除多余文件）。
ls -1t "$BACKUP_DIR"/eisechat-*.tar.gz 2>/dev/null | tail -n +"$((KEEP + 1))" | while read -r old; do
  rm -f "$old"
  echo "[backup] 清理旧备份 $old"
done

echo "== [backup] $(date -Is) 完成 =="
