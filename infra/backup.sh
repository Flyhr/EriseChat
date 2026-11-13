#!/usr/bin/env bash
# EriseChat 每日备份脚本（建议 root/cron 执行；默认每天 02:00）。
#
# 内容：SQLite 在线备份（VACUUM INTO，一致性快照）+ files/（必需）、downloads/（可选）打包 → 单个 tar.gz；
# 保留最近 30 份；可选 rsync/scp 同步到异机；失败以非 0 退出并写日志（日志在备份目录）。
# 全脚本 umask 077：日志、快照、临时归档与最终备份均仅当前用户可读写。
#
# 用法（可放 /etc/cron.d/erisechat-backup）：
#   0 2 * * * root ERISECHAT_REMOTE=user@backup-host:/srv/erisechat-backup bash /opt/erisechat-server/infra/backup.sh
set -euo pipefail

# 备份含聊天记录与附件：日志、SQLite 快照、临时归档一律从创建时起仅当前用户可读写。
# 必须在 mkdir / exec 打开日志之前设置，否则日志与备份目录会以默认权限落地。
umask 077

DATA_DIR="${ERISECHAT_DATA_DIR:-/var/lib/erisechat}"
BACKUP_DIR="${ERISECHAT_BACKUP_DIR:-/var/backups/erisechat}"
KEEP="${ERISECHAT_BACKUP_KEEP:-30}"
REMOTE="${ERISECHAT_REMOTE:-}"
STAMP="$(date +%Y%m%d-%H%M)"
ARCHIVE="$BACKUP_DIR/erisechat-$STAMP.tar.gz"

mkdir -p "$BACKUP_DIR"
LOG="$BACKUP_DIR/backup.log"
exec >>"$LOG" 2>&1

trap 'echo "[backup] $(date -Is) 失败（行号 $LINENO）"; exit 1' ERR

echo "== [backup] $(date -Is) 开始 =="
[ -f "$DATA_DIR/chat.db" ] || { echo "[backup] 未找到 $DATA_DIR/chat.db"; exit 1; }
# files/ 是消息附件的必需目录；缺失说明数据目录不完整，绝不能生成「少了附件却报成功」的备份。
[ -d "$DATA_DIR/files" ] || {
  echo "[backup] 缺少附件目录 $DATA_DIR/files，拒绝生成不完整的备份"
  exit 1
}

tmp_dir="$(mktemp -d)"
trap 'rm -rf "$tmp_dir"' EXIT

# 1) SQLite 在线备份：必须生成一致性快照。缺 sqlite3 或快照失败一律非零退出，
#    绝不复制运行中的 chat.db —— 那样会得到不一致、可能无法恢复的备份。
command -v sqlite3 >/dev/null 2>&1 || {
  echo "[backup] 未找到 sqlite3，无法生成一致性快照；请安装后重试（不复制运行中的数据库）"
  exit 1
}
sqlite3 "$DATA_DIR/chat.db" "VACUUM INTO '$tmp_dir/chat.db';"
[ -s "$tmp_dir/chat.db" ] || { echo "[backup] 数据库快照为空，拒绝发布备份"; exit 1; }
chmod 600 "$tmp_dir/chat.db"

# 2) 附件归档：files/ 必需（上方已校验），downloads/ 可选。
#    不再生成空附件归档，也不再用 2>/dev/null 的回退吞掉真实 tar 错误（磁盘满、权限、坏文件）。
archive_targets=(files)
if [ -d "$DATA_DIR/downloads" ]; then
  archive_targets+=(downloads)
fi
tar -C "$DATA_DIR" -czf "$tmp_dir/files.tar.gz" "${archive_targets[@]}"

# 3) 先写同目录临时归档，快照与附件都成功后原子 mv 发布最终文件；
#    中途失败只留下临时文件并由 EXIT trap 清理，不会出现半截的正式备份。
tmp_archive="$ARCHIVE.tmp.$$"
trap 'rm -rf "$tmp_dir" "$tmp_archive"' EXIT
tar -C "$tmp_dir" -czf "$tmp_archive" chat.db files.tar.gz
chmod 600 "$tmp_archive"
mv -f "$tmp_archive" "$ARCHIVE"
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
ls -1t "$BACKUP_DIR"/erisechat-*.tar.gz 2>/dev/null | tail -n +"$((KEEP + 1))" | while read -r old; do
  rm -f "$old"
  echo "[backup] 清理旧备份 $old"
done

echo "== [backup] $(date -Is) 完成 =="
