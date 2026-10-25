import { computed, ref } from 'vue'
import { defineStore } from 'pinia'
import type { Conversation, Message } from '../types/frames'
import { conversationsApi } from '../api/conversations'
import { chatWs } from '../core/ws'
import { unreadCount } from '../core/sync'

// 会话列表状态：按 conversationId upsert 合并（conv.updated 推送 / 本地新建共用），
// 展示顺序由 sorted 统一计算（最近消息时间倒序，无消息按创建时间）。
export const useConversationsStore = defineStore('conversations', () => {
  const items = ref<Conversation[]>([])
  const loading = ref(false)
  /** 是否已成功从服务端加载过列表（避免重复拉取；强制刷新用 load(true)）。 */
  const loaded = ref(false)
  let unsubscribeFrame: (() => void) | null = null

  /** 按最新消息时间倒序展示（未读数由 sync.ts 统一计算，M3 接入已读后使用） */
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

  /**
   * 收到一条新消息后更新会话摘要（列表预览/排序用）：
   * 仅更新已在列表中的会话；会话不存在时忽略（列表刷新后自然出现）。
   */
  function applyMessage(conversationId: string, message: Message): void {
    const index = items.value.findIndex((item) => item.conversationId === conversationId)
    if (index < 0) return
    const current = items.value[index]
    const next: Conversation = {
      ...current,
      lastSeq: Math.max(current.lastSeq, message.seq),
      lastMessage: message,
    }
    items.value = [
      ...items.value.slice(0, index),
      next,
      ...items.value.slice(index + 1),
    ]
  }

  /** 从服务端加载当前用户会话列表（幂等：已加载且非强制时直接返回）。 */
  async function load(force = false): Promise<void> {
    if (loading.value) return
    if (loaded.value && !force) return
    loading.value = true
    try {
      const list = await conversationsApi.list()
      items.value = [...list]
      loaded.value = true
    } finally {
      loading.value = false
    }
  }

  /**
   * 与指定用户发起单聊（服务端按 direct_key 去重，已存在时返回原会话）。
   * 成功后写入列表；sorted 按时间排序，新会话自然置顶。
   */
  async function createDirect(userId: string): Promise<Conversation> {
    const conversation = await conversationsApi.create({ type: 'direct', memberIds: [userId] })
    upsert([conversation])
    return conversation
  }

  /**
   * 订阅 WS `conv.updated`：其他端新建会话/会话信息变化时，用服务端推送的完整会话对象更新列表
   * （protocol.md §4.5；重复调用只注册一次）。
   */
  function subscribeRealtime(): () => void {
    if (unsubscribeFrame !== null) return unsubscribeFrame
    unsubscribeFrame = chatWs.on('frame', (frame) => {
      if (frame.type === 'conv.updated') upsert(frame.conversations)
    })
    return unsubscribeFrame
  }

  function clear(): void {
    items.value = []
    loaded.value = false
  }

  return {
    items,
    sorted,
    loading,
    loaded,
    upsert,
    applyMessage,
    getById,
    unreadOf,
    load,
    createDirect,
    subscribeRealtime,
    clear,
  }
})
