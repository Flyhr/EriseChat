// F-15 批次 4：会话共享文件的纯函数（无 DOM 依赖，便于 Vitest 单测）。
// 接口字段见 api/conversations.ts 的 SharedFileItem；加载与展示由 ConversationFilesPanel.vue 负责。

import type { SharedFileItem, SharedLinkItem } from '../api/conversations'
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

/**
 * 合并链接分页结果（C2c）：按「messageId + url」去重、保留已有顺序、新条目追加到末尾；不修改入参。
 * 服务端已按 messageSeq 降序返回，因此直接追加即可保持倒序。
 */
export function mergeSharedLinks(
  existing: readonly SharedLinkItem[],
  incoming: readonly SharedLinkItem[],
): SharedLinkItem[] {
  const merged = Array.isArray(existing) ? [...existing] : []
  const seen = new Set(merged.map((item) => `${item.messageId}|${item.url}`))
  if (!Array.isArray(incoming)) return merged
  for (const item of incoming) {
    if (!item || typeof item.url !== 'string' || !item.url) continue
    const key = `${item.messageId}|${item.url}`
    if (seen.has(key)) continue
    seen.add(key)
    merged.push(item)
  }
  return merged
}

/** 月份分组键 `YYYY-MM`（时间非法返回空串，归入「更早」）。 */
export function monthKeyOf(timestamp: number): string {
  if (typeof timestamp !== 'number' || !Number.isFinite(timestamp) || timestamp <= 0) return ''
  const date = new Date(timestamp)
  if (Number.isNaN(date.getTime())) return ''
  return `${date.getFullYear()}-${pad2(date.getMonth() + 1)}`
}

/** 月份分组标签：`YYYY年M月`；非法键返回「更早」。 */
export function formatMonthLabel(key: string): string {
  if (typeof key !== 'string' || !/^\d{4}-\d{2}$/.test(key)) return '更早'
  return `${Number(key.slice(0, 4))}年${Number(key.slice(5, 7))}月`
}

/** 按月分组的条目（保持输入顺序，输入已按时间倒序）。 */
export interface MonthGroup<T> {
  key: string
  label: string
  items: T[]
}

/**
 * 按 createdAt 的月份分组（C2c）：输入已按时间倒序，输出保持同样顺序；
 * 非法时间归入空键「更早」组并排在最后。
 */
export function groupByMonth<T extends { createdAt: number }>(
  items: readonly T[],
): MonthGroup<T>[] {
  const groups: MonthGroup<T>[] = []
  const indexByKey = new Map<string, number>()
  for (const item of Array.isArray(items) ? items : []) {
    if (!item) continue
    const key = monthKeyOf(item.createdAt)
    let index = indexByKey.get(key)
    if (index === undefined) {
      index = groups.length
      indexByKey.set(key, index)
      groups.push({ key, label: formatMonthLabel(key), items: [] })
    }
    groups[index]!.items.push(item)
  }
  const invalid = groups.filter((group) => group.key === '')
  return [...groups.filter((group) => group.key !== ''), ...invalid]
}
