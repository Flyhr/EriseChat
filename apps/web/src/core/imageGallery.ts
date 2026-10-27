// F-08 批次 1：图片预览的列表提取、索引切换与缩放纯函数（无 DOM 依赖，便于 Vitest 单测）。
// 预览 UI 与鉴权 blob 加载由 ImagePreview.vue / Chat.vue 负责，本文件只做纯计算。

/** 单条消息中与预览相关的最小结构（避免依赖具体帧类型）。 */
export interface ImageGalleryMessage {
  messageId: string
  msgType: string
  content: string | { fileId?: string | null; name?: string | null; text?: string | null }
}

/** 预览条目：消息 ID + 展示用文件名。 */
export interface ImageGalleryItem {
  messageId: string
  name: string
}

/** 缩放边界与步长。 */
export interface ScaleOptions {
  min?: number
  max?: number
  step?: number
}

export const MIN_SCALE = 1
export const MAX_SCALE = 4
export const SCALE_STEP = 0.25

/**
 * 按消息顺序提取可预览的图片条目：仅 msgType==='image' 且 content 带有效 fileId。
 * 只处理已加载的消息（不强求服务端全量）；异常 content 直接跳过，不抛错。
 */
export function imageItemsOf(messages: readonly ImageGalleryMessage[]): ImageGalleryItem[] {
  if (!Array.isArray(messages)) return []
  const items: ImageGalleryItem[] = []
  for (const message of messages) {
    if (!message || message.msgType !== 'image') continue
    const content = message.content
    if (typeof content !== 'object' || content === null) continue
    const fileId = 'fileId' in content ? content.fileId : null
    if (typeof fileId !== 'string' || !fileId) continue
    const name = 'name' in content && typeof content.name === 'string' && content.name.trim()
      ? content.name
      : '图片'
    items.push({ messageId: message.messageId, name })
  }
  return items
}

/**
 * 计算切换后的索引：total<=0 返回 -1；wrap=true 时环形回绕，否则钳制在 [0,total-1]。
 * 非有限 delta 按 0 处理，避免出现 NaN。
 */
export function stepIndex(index: number, delta: number, total: number, wrap = true): number {
  if (!Number.isFinite(total) || total <= 0) return -1
  const count = Math.floor(total)
  const safeIndex = Number.isFinite(index) ? Math.trunc(index) : 0
  const step = Number.isFinite(delta) ? Math.trunc(delta) : 0
  const next = safeIndex + step
  if (wrap) return ((next % count) + count) % count
  return Math.min(count - 1, Math.max(0, next))
}

/** 把缩放值钳制到 [min,max]；非法值回退 min。 */
export function clampScale(scale: number, options: ScaleOptions = {}): number {
  const min = normalizeBound(options.min, MIN_SCALE)
  const max = normalizeBound(options.max, MAX_SCALE)
  const lower = Math.min(min, max)
  const upper = Math.max(min, max)
  const value = Number.isFinite(scale) ? scale : lower
  return Math.min(upper, Math.max(lower, value))
}

/** 按步长缩放并四舍五入到 0.01，避免 1.0000001 之类的浮点噪声；结果钳制在区间内。 */
export function nextScale(scale: number, delta: number, options: ScaleOptions = {}): number {
  const base = clampScale(scale, options)
  const rawStep = typeof options.step === 'number' && Number.isFinite(options.step) ? options.step : SCALE_STEP
  const stepSize = rawStep > 0 ? rawStep : SCALE_STEP
  const safeDelta = Number.isFinite(delta) ? delta : 0
  const raw = base + safeDelta * stepSize
  const rounded = Math.round(raw * 100) / 100
  return clampScale(rounded, options)
}

/** 缩放百分比文案，如 1.5 → "150%"；非法值按 100%。 */
export function formatScale(scale: number): string {
  const value = Number.isFinite(scale) ? scale : 1
  return `${Math.round(value * 100)}%`
}

function normalizeBound(value: number | undefined, fallback: number): number {
  return Number.isFinite(value) ? (value as number) : fallback
}
