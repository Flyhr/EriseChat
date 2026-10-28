// F-15 批次 4：会话共享文件的纯函数（无 DOM 依赖，便于 Vitest 单测）。
// 接口字段见 api/conversations.ts 的 SharedFileItem；加载与展示由 ConversationFilesPanel.vue 负责。

import type { SharedFileItem } from '../api/conversations'
import type { ImageGalleryItem } from './imageGallery'

/** 共享文件分页大小（与服务端默认 limit 一致）。 */
export const SHARED_FILES_PAGE_SIZE = 50

/** 按 MIME 判断文件是图片还是普通文件（与服务端 image/file 分类一致）。 */
export function fileKindOf(mime: string | null | undefined): 'image' | 'file' {
  return typeof mime === 'string' && mime.trim().toLowerCase().startsWith('image/')
    ? 'image'
    : 'file'
}

/** 补零到两位。 */
function pad2(value: number): string {
  return value < 10 ? `0${value}` : String(value)
}

/**
 * 共享文件的时间展示（F-15）：
 * - 今天：`今天 HH:mm`
 * - 今年：`MM-DD`
 * - 往年：`YYYY-MM-DD`
 * 时间非法时返回 `—`。
 */
export function formatSharedFileTime(timestamp: number, now: number = Date.now()): string {
  if (typeof timestamp !== 'number' || !Number.isFinite(timestamp) || timestamp <= 0) return '—'
  const date = new Date(timestamp)
  const nowDate = new Date(Number.isFinite(now) ? now : Date.now())
  if (Number.isNaN(date.getTime())) return '—'
  if (
    date.getFullYear() === nowDate.getFullYear() &&
    date.getMonth() === nowDate.getMonth() &&
    date.getDate() === nowDate.getDate()
  ) {
    return `今天 ${pad2(date.getHours())}:${pad2(date.getMinutes())}`
  }
  if (date.getFullYear() === nowDate.getFullYear()) {
    return `${pad2(date.getMonth() + 1)}-${pad2(date.getDate())}`
  }
  return `${date.getFullYear()}-${pad2(date.getMonth() + 1)}-${pad2(date.getDate())}`
}

/**
 * 合并分页结果：按 fileId 去重、保留已有顺序、新条目追加到末尾；不修改入参。
 * 服务端已按 messageSeq 降序返回，因此直接追加即可保持倒序。
 */
export function mergeSharedFiles(
  existing: readonly SharedFileItem[],
  incoming: readonly SharedFileItem[],
): SharedFileItem[] {
  const merged = Array.isArray(existing) ? [...existing] : []
  const seen = new Set(merged.map((item) => item.fileId))
  if (!Array.isArray(incoming)) return merged
  for (const item of incoming) {
    if (!item || typeof item.fileId !== 'string' || !item.fileId) continue
    if (seen.has(item.fileId)) continue
    seen.add(item.fileId)
    merged.push(item)
  }
  return merged
}

/** 转为图片预览条目（ImagePreview 只关心 messageId + 展示名）。 */
export function toPreviewItems(files: readonly SharedFileItem[]): ImageGalleryItem[] {
  if (!Array.isArray(files)) return []
  const items: ImageGalleryItem[] = []
  for (const file of files) {
    if (!file || typeof file.messageId !== 'string' || !file.messageId) continue
    items.push({ messageId: file.messageId, name: file.name || '图片' })
  }
  return items
}
