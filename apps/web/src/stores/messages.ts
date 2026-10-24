import { ref } from 'vue'
import { defineStore } from 'pinia'
import type { Message } from '../types/frames'
import { type GapCheckResult, detectGap, mergeMessages } from '../core/sync'

// 消息缓存与本地 seq 游标。事实源在服务端（AGENTS.md §2.8），本地只做缓存与去重排序。
export const useMessagesStore = defineStore('messages', () => {
  const byConversation = ref<Record<string, Message[]>>({})
  const lastSeqByConversation = ref<Record<string, number>>({})

  function messagesOf(conversationId: string): Message[] {
    return byConversation.value[conversationId] ?? []
  }

  function lastSeqOf(conversationId: string): number {
    return lastSeqByConversation.value[conversationId] ?? 0
  }

  function setLastSeq(conversationId: string, seq: number): void {
    if (seq <= lastSeqOf(conversationId)) return
    lastSeqByConversation.value = { ...lastSeqByConversation.value, [conversationId]: seq }
  }

  /**
   * 合并收到的消息（实时 message.new / 补拉 sync.response）：
   * 去重 + 按 seq 排序，并返回缺口检测结果；存在缺口时调用方应立即发 sync.request。
   */
  function merge(conversationId: string, incoming: readonly Message[]): GapCheckResult {
    const result = detectGap(lastSeqOf(conversationId), incoming)
    const merged = mergeMessages(messagesOf(conversationId), incoming)
    byConversation.value = { ...byConversation.value, [conversationId]: merged }
    setLastSeq(conversationId, result.lastSeq)
    return result
  }

  /** 用补拉结果直接推进游标（调用方已按批处理时使用）。 */
  function advanceLastSeq(conversationId: string, lastSeq: number): void {
    setLastSeq(conversationId, lastSeq)
  }

  function clear(): void {
    byConversation.value = {}
    lastSeqByConversation.value = {}
  }

  return {
    byConversation,
    lastSeqByConversation,
    messagesOf,
    lastSeqOf,
    setLastSeq,
    merge,
    advanceLastSeq,
    clear,
  }
})
