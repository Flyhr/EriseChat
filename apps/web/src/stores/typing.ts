import { ref } from 'vue'
import { defineStore } from 'pinia'
import { chatWs, type WsState } from '../core/ws'
import {
  pruneTyping,
  TYPING_TTL_MS,
  typingLabelOf,
  upsertTyping,
  type TypingEntry,
} from '../core/typing'
import type { ServerFrame } from '../types/frames'
import { useAuthStore } from './auth'

// 输入中状态（M3-09）：由服务端 typing 帧驱动，3 秒无新帧自动清除；
// 仅内存状态（protocol.md §4.7 不落库），会话切换/登出/断连时清理。
export const useTypingStore = defineStore('typing', () => {
  const typingByConversation = ref<Record<string, TypingEntry[]>>({})
  const timers = new Map<string, ReturnType<typeof setTimeout>>()
  const unsubscribers: Array<() => void> = []
  let subscribed = false

  function removeConversation(conversationId: string): void {
    const timer = timers.get(conversationId)
    if (timer) {
      clearTimeout(timer)
      timers.delete(conversationId)
    }
    if (typingByConversation.value[conversationId] === undefined) return
    const next = { ...typingByConversation.value }
    delete next[conversationId]
    typingByConversation.value = next
  }

  /** 定时剔除过期条目；仍有剩余时按最早过期时间继续调度（简单可靠，不做精确到毫秒的逐条定时器）。 */
  function schedulePrune(conversationId: string): void {
    const existing = timers.get(conversationId)
    if (existing) clearTimeout(existing)
    timers.set(
      conversationId,
      setTimeout(() => {
        timers.delete(conversationId)
        const remaining = pruneTyping(typingByConversation.value[conversationId] ?? [], Date.now())
        if (remaining.length === 0) {
          removeConversation(conversationId)
          return
        }
        typingByConversation.value = {
          ...typingByConversation.value,
          [conversationId]: remaining,
        }
        schedulePrune(conversationId)
      }, TYPING_TTL_MS + 50),
    )
  }

  function markTyping(conversationId: string, userId: string): void {
    if (!conversationId || !userId) return
    const entries = upsertTyping(typingByConversation.value[conversationId] ?? [], userId, Date.now())
    typingByConversation.value = { ...typingByConversation.value, [conversationId]: entries }
    schedulePrune(conversationId)
  }

  /** typing 提示文案（单聊「对方正在输入…」、群聊「X 正在输入…」/多人）。 */
  function typingLabel(
    conversationId: string,
    conversationType: 'direct' | 'group',
    nameOf: (userId: string) => string,
  ): string {
    const entries = typingByConversation.value[conversationId] ?? []
    const names = entries.map((entry) => nameOf(entry.userId))
    return typingLabelOf(conversationType, names)
  }

  /** 清空指定会话的输入中状态（会话切换时调用）。 */
  function clearConversation(conversationId: string): void {
    removeConversation(conversationId)
  }

  /** 清空全部输入中状态（登出/切账号/断连时调用）。 */
  function clear(): void {
    for (const timer of timers.values()) clearTimeout(timer)
    timers.clear()
    typingByConversation.value = {}
  }

  /**
   * 订阅 WS 帧（只注册一次）：typing 帧 > 记录发送者（忽略自己）；连接离开 authed 状态即清空。
   */
  function subscribeRealtime(): () => void {
    if (subscribed) return dispose
    subscribed = true
    unsubscribers.push(
      chatWs.on('frame', (frame: ServerFrame) => {
        if (frame.type !== 'typing' || !('userId' in frame)) return
        if (frame.userId === useAuthStore().user?.userId) return
        markTyping(frame.conversationId, frame.userId)
      }),
    )
    unsubscribers.push(
      chatWs.on('statechange', (state: WsState) => {
        if (state !== 'authed') clear()
      }),
    )
    return dispose
  }

  function dispose(): void {
    for (const unsubscribe of unsubscribers.splice(0)) unsubscribe()
    clear()
    subscribed = false
  }

  return {
    typingByConversation,
    typingLabel,
    clearConversation,
    clear,
    subscribeRealtime,
  }
})
