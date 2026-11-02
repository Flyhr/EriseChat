// F-28 语音消息纯函数（无 DOM、无 store 依赖，便于单测）。
// 协议见 protocol/protocol.md §7 第 9 条：content 固定 { fileId, durationMs }，音频走 REST。
import type { VoiceContent } from '../types/frames'

/** 语音时长下限（毫秒，与服务端 MessageService.VoiceMinDurationMs 一致）。 */
export const VOICE_MIN_DURATION_MS = 1_000

/** 语音时长上限（毫秒，与服务端 MessageService.VoiceMaxDurationMs 一致）。 */
export const VOICE_MAX_DURATION_MS = 60_000

/** 语音文件大小上限（5MB，与服务端 MessageService.VoiceMaxBytes 一致）。 */
export const VOICE_MAX_BYTES = 5 * 1024 * 1024

/** 语音文件 MIME（服务端白名单只接受 audio/webm）。 */
export const VOICE_MIME = 'audio/webm'

/** 时长是否合法：整数且落在 1000~60000 毫秒（协议 §7 第 9 条）。 */
export function isValidVoiceDuration(durationMs: number): boolean {
  return (
    Number.isFinite(durationMs) &&
    Number.isInteger(durationMs) &&
    durationMs >= VOICE_MIN_DURATION_MS &&
    durationMs <= VOICE_MAX_DURATION_MS
  )
}

/** 毫秒 → `分:秒` 展示（如 7000 → "0:07"）；非法值回退 0:00。 */
export function formatDuration(durationMs: number): string {
  const safe = Number.isFinite(durationMs) && durationMs > 0 ? Math.floor(durationMs) : 0
  const totalSeconds = Math.floor(safe / 1000)
  const minutes = Math.floor(totalSeconds / 60)
  const seconds = totalSeconds % 60
  return `${minutes}:${String(seconds).padStart(2, '0')}`
}

/** 构造语音 content；fileId 为空或时长非法时返回 null（调用方据此拦截发送）。 */
export function buildVoiceContent(fileId: string, durationMs: number): VoiceContent | null {
  const id = typeof fileId === 'string' ? fileId.trim() : ''
  if (!id || !isValidVoiceDuration(durationMs)) return null
  return { fileId: id, durationMs }
}

/** 会话列表/引用条/收藏摘要中的语音占位文案：`[语音] 0:07`。 */
export function voicePreviewText(durationMs: number): string {
  return `[语音] ${formatDuration(durationMs)}`
}

/** 判断 content 是否为语音对象（FileContent 也有 fileId，但没有 durationMs）。 */
export function isVoiceContent(value: unknown): value is VoiceContent {
  if (typeof value !== 'object' || value === null) return false
  if (!('fileId' in value) || !('durationMs' in value)) return false
  const content = value as VoiceContent
  return typeof content.fileId === 'string' && Number.isFinite(content.durationMs)
}

/** 把定位毫秒夹到 0~durationMs（非法值/负值→0；时长非法时→0）。 */
export function clampVoiceSeekMs(ms: number, durationMs: number): number {
  if (!Number.isFinite(durationMs) || durationMs <= 0) return 0
  if (!Number.isFinite(ms)) return 0
  if (ms <= 0) return 0
  return Math.min(Math.floor(ms), Math.floor(durationMs))
}

/** 进度比例 0~1（时长非法或 ≤0 时返回 0）。 */
export function voiceProgressRatio(ms: number, durationMs: number): number {
  if (!Number.isFinite(durationMs) || durationMs <= 0) return 0
  const safeMs = clampVoiceSeekMs(ms, durationMs)
  return Math.min(1, Math.max(0, safeMs / durationMs))
}

/** 拖动/悬停定位时的时间文案：`0:11 / 0:20`（非法值按 0 处理）。 */
export function voiceSeekTimeText(seekMs: number, durationMs: number): string {
  const total = Number.isFinite(durationMs) && durationMs > 0 ? Math.floor(durationMs) : 0
  return `${formatDuration(clampVoiceSeekMs(seekMs, total))} / ${formatDuration(total)}`
}
