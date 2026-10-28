// F-21：自定义表情的输入校验与展示辅助纯函数（不触碰 DOM/IndexedDB，便于单测）。
// 约束（计划 §F-21 D3）：PNG/JPEG/WebP/GIF；原始 ≤2MB；GIF ≤1MB；非 GIF 展示时压缩到 128px/≤48KB 并保留 PNG 透明。

export const CUSTOM_EMOJI_MAX_SOURCE_BYTES = 2 * 1024 * 1024
export const CUSTOM_EMOJI_GIF_MAX_BYTES = 1 * 1024 * 1024
export const CUSTOM_EMOJI_EDGE = 128
export const CUSTOM_EMOJI_TARGET_BYTES = 48 * 1024

export const CUSTOM_EMOJI_MIMES = ['image/png', 'image/jpeg', 'image/webp', 'image/gif'] as const

export type CustomEmojiInputCheck =
  | { ok: true; needsCompress: boolean }
  | { ok: false; reason: string }

const REASON_MIME = '仅支持 PNG/JPEG/WebP/GIF 图片'
const REASON_EMPTY = '图片文件为空'
const REASON_TOO_LARGE = '图片不能超过 2MB'
const REASON_GIF_TOO_LARGE = 'GIF 不能超过 1MB'

/** 校验用户选择的图片能否作为自定义表情；需要压缩的格式返回 needsCompress=true。 */
export function checkCustomEmojiFile(file: Pick<File, 'type' | 'size'>): CustomEmojiInputCheck {
  const mime = typeof file.type === 'string' ? file.type.toLowerCase() : ''
  if (!(CUSTOM_EMOJI_MIMES as readonly string[]).includes(mime)) {
    return { ok: false, reason: REASON_MIME }
  }
  const size = Number.isFinite(file.size) ? file.size : -1
  if (size <= 0) return { ok: false, reason: REASON_EMPTY }
  if (mime === 'image/gif') {
    return size > CUSTOM_EMOJI_GIF_MAX_BYTES ? { ok: false, reason: REASON_GIF_TOO_LARGE } : { ok: true, needsCompress: false }
  }
  return size > CUSTOM_EMOJI_MAX_SOURCE_BYTES ? { ok: false, reason: REASON_TOO_LARGE } : { ok: true, needsCompress: true }
}

/** 发送时使用的文件名（GIF 保留 .gif，其余统一 .png/.jpg/.webp）。 */
export function customEmojiFileName(mime: string): string {
  switch ((mime || '').toLowerCase()) {
    case 'image/gif':
      return 'emoji.gif'
    case 'image/jpeg':
      return 'emoji.jpg'
    case 'image/webp':
      return 'emoji.webp'
    default:
      return 'emoji.png'
  }
}

/** 按创建时间倒序排序（稳定、不改入参；非法时间按 0 处理）。 */
export function sortCustomEmojis<T extends { createdAt: number }>(items: readonly T[]): T[] {
  return items
    .map((item) => ({ item, time: Number.isFinite(item.createdAt) ? item.createdAt : 0 }))
    .sort((left, right) => right.time - left.time)
    .map((entry) => entry.item)
}
