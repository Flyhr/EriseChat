// 输入中状态纯逻辑（M3-09）：
// 1) 服务端 typing 帧产生的「某用户正在输入」条目维护（3 秒无新帧自动过期）；
// 2) typing 提示文案（单聊/群聊/多人）；不依赖 Vue/DOM，便于单测。
export const TYPING_TTL_MS = 3000

export interface TypingEntry {
  userId: string
  /** 过期时间（UTC 毫秒）：每次收到该用户新的 typing 帧就顺延。 */
  expiresAt: number
}

/** 记录/刷新某用户的输入中条目（同一用户只保留一条，过期时间顺延）。 */
export function upsertTyping(
  entries: readonly TypingEntry[],
  userId: string,
  nowMs: number,
  ttlMs: number = TYPING_TTL_MS,
): TypingEntry[] {
  if (!userId) return [...entries]
  const without = entries.filter((entry) => entry.userId !== userId)
  return [...without, { userId, expiresAt: nowMs + ttlMs }]
}

/** 过滤掉已过期的输入中条目。 */
export function pruneTyping(entries: readonly TypingEntry[], nowMs: number): TypingEntry[] {
  return entries.filter((entry) => entry.expiresAt > nowMs)
}

/**
 * typing 提示文案（protocol.md §4.7）：
 * 单聊固定「对方正在输入…」；群聊 1 人「X 正在输入…」、2 人列出两人、3 人及以上「多人正在输入…」。
 */
export function typingLabelOf(conversationType: 'direct' | 'group', names: readonly string[]): string {
  const filled = names.map((name) => name.trim()).filter((name) => name.length > 0)
  // 无人输入时必须返回空串：单聊文案不含昵称，但也不能在没有条目时常驻显示。
  if (filled.length === 0) return ''
  if (conversationType === 'direct') return '对方正在输入…'
  if (filled.length === 1) return `${filled[0]} 正在输入…`
  if (filled.length === 2) return `${filled[0]}、${filled[1]} 正在输入…`
  return '多人正在输入…'
}
