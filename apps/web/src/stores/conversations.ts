import { computed, ref } from 'vue'
import { defineStore } from 'pinia'
import type { Conversation, Message } from '../types/frames'
import { conversationsApi } from '../api/conversations'
import { chatWs } from '../core/ws'
import { unreadCount } from '../core/sync'
import { getConversations, saveConversation } from '../core/db'

/** 本地已读游标持久化 key：M2 阶段先本地记录，M3 接入 read.update 后以服务端 last_read_seq 为准。 */
const READ_SEQ_STORAGE_KEY = 'eisechat.readSeq'

function readStoredReadSeq(): Record<string, number> {
  try {
    const raw = window.localStorage.getItem(READ_SEQ_STORAGE_KEY)
    if (!raw) return {}
    const parsed = JSON.parse(raw) as Record<string, unknown> | null
    if (typeof parsed !== 'object' || parsed === null) return {}
    const result: Record<string, number> = {}
    for (const [conversationId, seq] of Object.entries(parsed)) {
      if (typeof seq === 'number' && Number.isFinite(seq) && seq > 0) result[conversationId] = seq
    }
    return result
  } catch {
    return {}
  }
}

function persistReadSeq(map: Record<string, number>): void {
  try {
    window.localStorage.setItem(READ_SEQ_STORAGE_KEY, JSON.stringify(map))
  } catch {
    // localStorage 不可用（隐私模式等）：仅内存记录，不影响使用。
  }
}

/** 把会话快照写入 IndexedDB（只做缓存，不改变服务端事实源）。 */
function persistConversations(conversations: readonly Conversation[]): void {
  for (const conversation of conversations) {
    void saveConversation(conversation).catch(() => {
      // 缓存写入失败不影响主流程（下一次加载会重试）。
    })
  }
}

// 会话列表状态：按 conversationId upsert 合并（conv.updated 推送 / 本地新建共用），
// 展示顺序由 sorted 统一计算（最近消息时间倒序，无消息按创建时间）。
export const useConversationsStore = defineStore('conversations', () => {
  const items = ref<Conversation[]>([])
  const loading = ref(false)
  /** 是否已成功从服务端加载过列表（避免重复拉取；强制刷新用 load(true)）。 */
  const loaded = ref(false)
  /** 本地已读游标：会话未读 = lastSeq − max(本地已读, 服务端 last_read_seq)。 */
  const lastReadSeqByConversation = ref<Record<string, number>>(readStoredReadSeq())
  let unsubscribeFrame: (() => void) | null = null
  let cacheRestored = false

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
    persistConversations(conversations)
  }

  function getById(conversationId: string): Conversation | undefined {
    return items.value.find((item) => item.conversationId === conversationId)
  }

  function unreadOf(conversationId: string): number {
    const conversation = getById(conversationId)
    if (!conversation) return 0
    const localRead = lastReadSeqByConversation.value[conversationId] ?? 0
    const readSeq = Math.max(localRead, conversation.lastReadSeq ?? 0)
    return unreadCount(conversation.lastSeq, readSeq)
  }

  /**
   * 标记会话已读（打开会话/在会话内收到新消息时调用）：
   * M2 暂存本地（localStorage）；M3 接入 read.update 上报服务端后改为以服务端为准。
   */
  function markRead(conversationId: string, seq: number): void {
    if (!Number.isFinite(seq) || seq <= 0) return
    if (seq <= (lastReadSeqByConversation.value[conversationId] ?? 0)) return
    lastReadSeqByConversation.value = {
      ...lastReadSeqByConversation.value,
      [conversationId]: seq,
    }
    persistReadSeq(lastReadSeqByConversation.value)
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
    persistConversations([next])
  }

  /**
   * 启动/刷新时从 IndexedDB 恢复会话快照（先渲染缓存，再由 load() 拉服务端最新列表）。
   * 只恢复、不改变 loaded 标记，因此随后的 load() 仍会请求服务端。
   */
  async function restoreFromCache(): Promise<void> {
    if (cacheRestored) return
    cacheRestored = true
    try {
      const cached = await getConversations()
      if (cached.length > 0 && items.value.length === 0) {
        items.value = [...cached]
      }
    } catch (error) {
      console.warn('[conversations] 读取本地缓存失败', error)
    }
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
      persistConversations(list)
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
    cacheRestored = false
    if (unsubscribeFrame) {
      unsubscribeFrame()
      unsubscribeFrame = null
    }
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
    markRead,
    restoreFromCache,
    load,
    createDirect,
    subscribeRealtime,
    clear,
  }
})
