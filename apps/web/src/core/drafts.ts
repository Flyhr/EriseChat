// 草稿存储（F-04，阶段 A｜纯 Web）：按会话保存输入中但未发送的文本，切换会话/刷新/重启后恢复。
// 只保存文本（不保存附件或图片选择状态）；保留原始空白，不做 trim。
// localStorage 在 Firefox 隐私模式、禁用存储或配额满时可能抛异常：所有读写内部 try/catch，
// 失败静默降级（本次会话内输入与发送不受影响，仅失去持久化能力）。
import { MAX_TEXT_LENGTH } from './config'
import { getStorage } from './safeStorage'

/** localStorage 键：结构为 `Record<conversationId, { text, updatedAt }>`。 */
export const DRAFTS_STORAGE_KEY = 'erisechat.drafts.v1'

/** 最多保留最近多少个会话的草稿（避免 localStorage 无限增长）。 */
export const DRAFTS_MAX_CONVERSATIONS = 20

/** 单条草稿结构：updatedAt 为 UTC 毫秒时间戳。 */
export interface DraftEntry {
  text: string
  updatedAt: number
}

/** conversationId → 草稿。 */
export type DraftMap = Record<string, DraftEntry>

/** 可注入的存储接口（Node 单测用假实现；浏览器传 localStorage）。 */
export interface DraftStorage {
  getItem(key: string): string | null
  setItem(key: string, value: string): void
  removeItem(key: string): void
}

function defaultStorage(): DraftStorage | null {
  return getStorage()
}

function isPlainObject(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value)
}

/**
 * 解析存储中的原始字符串：非法 JSON、非对象一律视为空；逐条校验并丢弃无效项。
 * 无效项：conversationId 为空串、text 非字符串/空串/超过 MAX_TEXT_LENGTH、updatedAt 非有限数。
 */
export function parseDrafts(raw: string | null): DraftMap {
  if (!raw) return {}
  let parsed: unknown
  try {
    parsed = JSON.parse(raw)
  } catch {
    return {}
  }
  if (!isPlainObject(parsed)) return {}
  const result: DraftMap = {}
  for (const [conversationId, value] of Object.entries(parsed)) {
    if (!conversationId || !isPlainObject(value)) continue
    const text = value.text
    const updatedAt = value.updatedAt
    if (typeof text !== 'string' || text.length === 0 || text.length > MAX_TEXT_LENGTH) continue
    if (typeof updatedAt !== 'number' || !Number.isFinite(updatedAt)) continue
    result[conversationId] = { text, updatedAt }
  }
  return result
}

export function serializeDrafts(map: DraftMap): string {
  return JSON.stringify(map)
}

/** 读取草稿；存储缺失/不可用/内容损坏都返回空对象，不抛错。 */
export function readDrafts(storage: DraftStorage | null | undefined = defaultStorage()): DraftMap {
  if (!storage) return {}
  try {
    return parseDrafts(storage.getItem(DRAFTS_STORAGE_KEY))
  } catch {
    return {}
  }
}

/** 写入草稿；存储缺失或写入失败（隐私模式/配额满）静默忽略，不阻塞调用方。 */
export function writeDrafts(
  map: DraftMap,
  storage: DraftStorage | null | undefined = defaultStorage(),
): void {
  if (!storage) return
  try {
    storage.setItem(DRAFTS_STORAGE_KEY, serializeDrafts(map))
  } catch {
    // 忽略持久化失败：内存中的草稿仍可正常使用。
  }
}

/** 按 updatedAt 保留最近 limit 个会话（默认 20），返回新对象，不修改入参。 */
export function pruneDrafts(map: DraftMap, limit: number = DRAFTS_MAX_CONVERSATIONS): DraftMap {
  const keep = Math.max(1, Math.floor(limit))
  const entries = Object.entries(map).sort((left, right) => right[1].updatedAt - left[1].updatedAt)
  const result: DraftMap = {}
  for (const [conversationId, entry] of entries.slice(0, keep)) {
    result[conversationId] = entry
  }
  return result
}
