import { computed, ref } from 'vue'
import { defineStore } from 'pinia'
import type { Conversation } from '../types/frames'
import { unreadCount } from '../core/sync'

// 会话列表状态：按 conversationId upsert 合并（conv.list / conv.updated / message.new 共用）。
export const useConversationsStore = defineStore('conversations', () => {
  const items = ref<Conversation[]>([])

  /** 按最新消息时间倒序展示（未读数由 sync.ts 统一计算） */
  const sorted = computed(() =>
    [...items.value].sort((a, b) => {
      const aTime = a.lastMessage?.createdAt ?? a.createdAt
      const bTime = b.lastMessage?.createdAt ?? b.createdAt
      return bTime - aTime
    }),
  )

  function upsert(conversations: readonly Conversation[]): void {
    const byId = new Map(items.value.map((item) => [item.conversationId, item]))
    for (const conversation of conversations) byId.set(conversation.conversationId, conversation)
    items.value = [...byId.values()]
  }

  function getById(conversationId: string): Conversation | undefined {
    return items.value.find((item) => item.conversationId === conversationId)
  }

  function unreadOf(conversationId: string): number {
    const conversation = getById(conversationId)
    if (!conversation) return 0
    return unreadCount(conversation.lastSeq, conversation.lastReadSeq)
  }

  function clear(): void {
    items.value = []
  }

  return {
    items,
    sorted,
    upsert,
    getById,
    unreadOf,
    clear,
  }
})
