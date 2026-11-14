// 阅读位置（批次 B2）：记录「当前账号 + 当前服务器」下每个会话上次主动浏览到的消息与偏移。
// 只用于进入会话时的落点恢复，不参与未读计算；贴底离开会清除该会话的记录，下次按未读/最新进入。
// 存储按作用域隔离（换账号或换服务器互不影响），容量按最近使用裁剪，写入失败静默降级。

import { readStorage, writeStorage } from './safeStorage'

/** 阅读位置的 localStorage 键（单键存全部作用域，便于一次性裁剪）。 */
export const READING_POSITION_STORAGE_KEY = 'erisechat.readingPositions'
/** 最多保留的会话条数（跨作用域合计）：远超日常使用量，避免 localStorage 无限增长。 */
const MAX_ENTRIES = 40

export interface ReadingAnchor {
  messageId: string
  /** 该消息行顶部相对滚动容器顶部的偏移（CSS px，可为负）。 */
  offset: number
}

interface StoredEntry extends ReadingAnchor {
  updatedAt: number
}

type StoredMap = Record<string, Record<string, StoredEntry>>

/** 作用域键：同一浏览器里不同账号/服务器各自独立。 */
export function readingPositionScope(userId: string, serverOrigin: string): string {
  return `${userId || 'anonymous'}@${serverOrigin || 'same-origin'}`
}

function readMap(): StoredMap {
  const raw = readStorage(READING_POSITION_STORAGE_KEY)
  if (!raw) return {}
  try {
    const parsed = JSON.parse(raw) as unknown
    if (typeof parsed !== 'object' || parsed === null || Array.isArray(parsed)) return {}
    return parsed as StoredMap
  } catch {
    return {}
  }
}

/** 读取某会话上次主动浏览的位置；无记录或结构非法时返回 null。 */
export function readReadingAnchor(scope: string, conversationId: string): ReadingAnchor | null {
  if (!scope || !conversationId) return null
  const entry = readMap()[scope]?.[conversationId]
  if (!entry || typeof entry.messageId !== 'string' || entry.messageId.length === 0) return null
  const offset = typeof entry.offset === 'number' && Number.isFinite(entry.offset) ? entry.offset : 0
  return { messageId: entry.messageId, offset }
}

/** 写入某会话的阅读位置；写入前按最近使用裁剪，失败时静默（不影响会话使用）。 */
export function writeReadingAnchor(
  scope: string,
  conversationId: string,
  anchor: ReadingAnchor,
): void {
  if (!scope || !conversationId || !anchor.messageId) return
  const map = readMap()
  const scoped = map[scope] ?? {}
  scoped[conversationId] = {
    messageId: anchor.messageId,
    offset: Math.round(anchor.offset),
    updatedAt: Date.now(),
  }
  map[scope] = scoped

  const flat: { scope: string; conversationId: string; updatedAt: number }[] = []
  for (const [scopeKey, entries] of Object.entries(map)) {
    for (const [conversationKey, entry] of Object.entries(entries)) {
      if (!entry || typeof entry.messageId !== 'string') continue
      flat.push({ scope: scopeKey, conversationId: conversationKey, updatedAt: entry.updatedAt ?? 0 })
    }
  }
  if (flat.length > MAX_ENTRIES) {
    flat.sort((a, b) => b.updatedAt - a.updatedAt)
    for (const stale of flat.slice(MAX_ENTRIES)) {
      delete map[stale.scope]?.[stale.conversationId]
      if (map[stale.scope] && Object.keys(map[stale.scope]).length === 0) delete map[stale.scope]
    }
  }

  writeStorage(READING_POSITION_STORAGE_KEY, JSON.stringify(map))
}

/** 清除某会话的阅读位置（离开时贴底、或会话被移除时调用）。 */
export function clearReadingAnchor(scope: string, conversationId: string): void {
  if (!scope || !conversationId) return
  const map = readMap()
  if (!map[scope]?.[conversationId]) return
  delete map[scope][conversationId]
  if (Object.keys(map[scope]).length === 0) delete map[scope]
  writeStorage(READING_POSITION_STORAGE_KEY, JSON.stringify(map))
}
