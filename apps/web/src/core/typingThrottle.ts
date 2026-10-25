// 客户端 typing 节流（M3-09）：同一会话 3 秒内最多发送一次 typing 帧（protocol.md §4.7 客户端节流；
// 服务端另有「连接 × 会话」1 秒节流，超频静默丢弃）。时间可注入，便于单测。
export const TYPING_THROTTLE_MS = 3000
const MAX_TRACKED_CONVERSATIONS = 1000

export class TypingThrottle {
  private readonly lastSentAt = new Map<string, number>()

  /** 距离上次发送超过窗口（默认 3 秒）时返回 true，并记录本次发送时间。 */
  shouldSend(
    conversationId: string,
    nowMs: number = Date.now(),
    windowMs: number = TYPING_THROTTLE_MS,
  ): boolean {
    if (!conversationId) return false
    const previous = this.lastSentAt.get(conversationId)
    if (previous !== undefined && nowMs - previous < windowMs) return false
    this.lastSentAt.set(conversationId, nowMs)
    if (this.lastSentAt.size > MAX_TRACKED_CONVERSATIONS) {
      const cutoff = nowMs - 10 * windowMs
      for (const [id, sentAt] of this.lastSentAt) {
        if (sentAt < cutoff) this.lastSentAt.delete(id)
      }
    }
    return true
  }

  /** 重置单个会话（会话切换/清空输入）或全部节流记录。 */
  reset(conversationId?: string): void {
    if (conversationId === undefined) {
      this.lastSentAt.clear()
      return
    }
    this.lastSentAt.delete(conversationId)
  }
}
