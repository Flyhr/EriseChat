import { computed, ref } from 'vue'
import { defineStore } from 'pinia'
import type { Conversation, Message } from '../types/frames'
import { conversationsApi } from '../api/conversations'
import { sortConversations } from '../core/conversationSort'
import { chatWs } from '../core/ws'
import { unreadCount } from '../core/sync'
import { deleteRecord, getConversations, saveConversation, STORE_CONVERSATIONS } from '../core/db'
import { useAuthStore } from './auth'

/** 本地已读游标持久化 key：用于页面内即时展示与服务端上报兜底；服务端 last_read_seq 是事实源（read.update）。 */
const READ_SEQ_STORAGE_KEY = 'eisechat.readSeq'

/** 被移出/退出群聊后的缓存清理回调（messages store 注册；避免两个 store 循环 import）。 */
type ConversationRemovedHandler = (conversationId: string) => void

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
  /**
   * 单聊对端已读游标（M3-09）：conversationId → userId → 最大已读 seq，
   * 来自服务端 read.updated（单聊只推给对方）；登出/切账号/clear 时清空。
   */
  const peerReadSeq = ref<Record<string, Record<string, number>>>({})
  /** 最近一次收到 removedConversationIds 的会话（Chat 页据此跳回 /chat 并提示；消费后调用 clearRemovedNotice 清空）。 */
  const lastRemovedConversationId = ref<string | null>(null)
  /** 最近一批被移出/退出的会话 ID（同一帧可能包含多个）。 */
  const removedConversationIds = ref<string[]>([])
  /** 当前正在查看的会话（Chat 页监听路由写入；提示音/通知据此判断是否正在看该会话）。 */
  const activeConversationId = ref<string | null>(null)
  const removedHandlers: ConversationRemovedHandler[] = []
  let unsubscribeFrame: (() => void) | null = null
  let unsubscribeState: (() => void) | null = null
  let cacheRestored = false

  /** 展示顺序（F-10）：置顶优先，组内按最近消息时间倒序（core/conversationSort 纯函数）。 */
  const sorted = computed(() => sortConversations(items.value))

  function upsert(conversations: readonly Conversation[]): void {
    if (conversations.length === 0) return
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
   * 全部会话的未读总数（F-02）：左导航徽标与桌面托盘/任务栏指示共用。
   * 逐会话复用 unreadOf（服务端 last_read_seq 与本地游标取较大值），不使用未读消息明细。
   */
  const totalUnread = computed(() => {
    let total = 0
    for (const conversation of items.value) {
      total += unreadOf(conversation.conversationId)
    }
    return total
  })

  /**
   * 标记会话已读（打开会话/在会话内收到新消息时调用）：更新本地游标用于即时展示，
   * 并上报服务端 read.update（protocol.md §4.6）——服务端 last_read_seq 是事实源，
   * 重新登录/换设备后未读数以它为准（否则每次登录都会把历史消息算成未读）。
   */
  function markRead(conversationId: string, seq: number): void {
    if (!Number.isFinite(seq) || seq <= 0) return
    if (seq <= (lastReadSeqByConversation.value[conversationId] ?? 0)) return
    lastReadSeqByConversation.value = {
      ...lastReadSeqByConversation.value,
      [conversationId]: seq,
    }
    persistReadSeq(lastReadSeqByConversation.value)
    reportReadSeq(conversationId, seq)
  }

  /**
   * 上报已读游标到服务端（M3-05 read.update）：连接未认证时静默跳过，
   * 认证成功后的 flushReadSeq 会补报，不阻塞页面交互。
   */
  function reportReadSeq(conversationId: string, seq: number): void {
    if (chatWs.state !== 'authed') return
    chatWs.send({ type: 'read.update', conversationId, lastReadSeq: seq })
  }

  /**
   * 认证成功（首次/重连）后补报：把本地领先服务端快照的已读游标逐个上报一次，
   * 覆盖「上报时连接尚未认证 / 已读后断线重连」的场景。
   */
  function flushReadSeq(): void {
    if (chatWs.state !== 'authed') return
    for (const [conversationId, seq] of Object.entries(lastReadSeqByConversation.value)) {
      const conversation = getById(conversationId)
      if (!conversation) continue
      if (seq > (conversation.lastReadSeq ?? 0)) {
        chatWs.send({ type: 'read.update', conversationId, lastReadSeq: seq })
      }
    }
  }

  /** 记录对端已读游标（单聊回执展示用）：忽略自己，只增不减。 */
  function handleReadUpdated(conversationId: string, userId: string, lastReadSeq: number): void {
    if (!conversationId || !userId || !Number.isFinite(lastReadSeq)) return
    if (userId === useAuthStore().user?.userId) return
    const current = peerReadSeq.value[conversationId]?.[userId] ?? 0
    if (lastReadSeq <= current) return
    peerReadSeq.value = {
      ...peerReadSeq.value,
      [conversationId]: { ...peerReadSeq.value[conversationId], [userId]: lastReadSeq },
    }
  }

  /** 读取对端已读游标（无记录返回 undefined，展示为「未读」）。 */
  function peerReadSeqOf(conversationId: string, userId: string): number | undefined {
    return peerReadSeq.value[conversationId]?.[userId]
  }

  /** 本地已读游标（判断是否值得再次上报，避免重复 read.update）。 */
  function readSeqOf(conversationId: string): number {
    return lastReadSeqByConversation.value[conversationId] ?? 0
  }

  /**
   * 收到一条新消息后更新会话摘要（列表预览/排序用）：
   * 仅更新已在列表中的会话；只有 seq 更大的消息才更新 lastMessage（补拉到的旧消息不覆盖最新消息，M3-00 N5）。
   */
  function applyMessage(conversationId: string, message: Message): void {
    const index = items.value.findIndex((item) => item.conversationId === conversationId)
    if (index < 0) return
    const current = items.value[index]
    const nextLastSeq = Math.max(current.lastSeq, message.seq)
    if (message.seq > current.lastSeq) {
      const next: Conversation = { ...current, lastSeq: nextLastSeq, lastMessage: message }
      items.value = [
        ...items.value.slice(0, index),
        next,
        ...items.value.slice(index + 1),
      ]
      persistConversations([next])
      return
    }
    if (nextLastSeq !== current.lastSeq) {
      const next: Conversation = { ...current, lastSeq: nextLastSeq }
      items.value = [
        ...items.value.slice(0, index),
        next,
        ...items.value.slice(index + 1),
      ]
    }
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

  /** 注册「会话被移出/退出」的缓存清理回调（messages store 在初始化时注册一次）。 */
  function onConversationRemoved(handler: ConversationRemovedHandler): void {
    if (!removedHandlers.includes(handler)) removedHandlers.push(handler)
  }

  /**
   * 处理 conv.updated.removedConversationIds（protocol.md §4.5）：
   * 从列表移除、清本地已读游标与 IndexedDB 会话缓存，并通知 messages store 清消息缓存；
   * 记录 lastRemovedConversationId 供 Chat 页跳转/提示。
   */
  function handleRemovedConversations(conversationIds: readonly string[]): void {
    const removed = conversationIds.filter(
      (conversationId) => typeof conversationId === 'string' && conversationId.length > 0,
    )
    if (removed.length === 0) return

    const removedSet = new Set(removed)
    items.value = items.value.filter((item) => !removedSet.has(item.conversationId))

    const nextReadSeq = { ...lastReadSeqByConversation.value }
    for (const conversationId of removed) delete nextReadSeq[conversationId]
    lastReadSeqByConversation.value = nextReadSeq
    persistReadSeq(nextReadSeq)

    for (const conversationId of removed) {
      void deleteRecord(STORE_CONVERSATIONS, conversationId).catch(() => {
        // 缓存删除失败不影响主流程（下次全量加载会覆盖）。
      })
    }

    removedConversationIds.value = removed
    lastRemovedConversationId.value = removed[removed.length - 1] ?? null

    for (const conversationId of removed) {
      for (const handler of removedHandlers) {
        try {
          handler(conversationId)
        } catch (error) {
          console.warn('[conversations] 清理被移除会话缓存失败', error)
        }
      }
    }
  }

  /** 清除「被移出/退出」提示状态（Chat 页处理完跳转后调用）。 */
  function clearRemovedNotice(): void {
    lastRemovedConversationId.value = null
    removedConversationIds.value = []
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

  /** 创建群聊（M3-08）：成功后写入列表并返回会话（调用方负责跳转）。 */
  async function createGroup(title: string, memberIds: string[]): Promise<Conversation> {
    const conversation = await conversationsApi.createGroup(title, memberIds)
    upsert([conversation])
    return conversation
  }

  /**
   * 订阅 WS `conv.updated`：会话信息变化时 upsert；被移出/退出时按 removedConversationIds 清理
   * （protocol.md §4.5；重复调用只注册一次）。
   */
  function subscribeRealtime(): () => void {
    if (unsubscribeFrame !== null) return unsubscribeFrame
    unsubscribeFrame = chatWs.on('frame', (frame) => {
      // 单聊已读回执（M3-09）：服务端 read.updated 只推给对方，记录对端已读游标。
      if (frame.type === 'read.updated') {
        handleReadUpdated(frame.conversationId, frame.userId, frame.lastReadSeq)
        return
      }
      if (frame.type !== 'conv.updated') return
      if (frame.removedConversationIds && frame.removedConversationIds.length > 0) {
        handleRemovedConversations(frame.removedConversationIds)
      }
      upsert(frame.conversations)
    })
    // 认证成功（首次/重连）后补报已读游标，避免服务端 last_read_seq 长期滞后导致重新登录误报未读。
    unsubscribeState = chatWs.on('statechange', (state) => {
      if (state === 'authed') flushReadSeq()
    })
    return unsubscribeFrame
  }

  function clear(): void {
    items.value = []
    loaded.value = false
    cacheRestored = false
    // 清除本地已读游标（含 localStorage），避免账号切换后沿用上一位用户的已读状态（M3-00）。
    lastReadSeqByConversation.value = {}
    peerReadSeq.value = {}
    lastRemovedConversationId.value = null
    removedConversationIds.value = []
    activeConversationId.value = null
    try {
      window.localStorage.removeItem(READ_SEQ_STORAGE_KEY)
    } catch {
      // localStorage 不可用时本来也没有持久化。
    }
    if (unsubscribeFrame) {
      unsubscribeFrame()
      unsubscribeFrame = null
    }
    if (unsubscribeState) {
      unsubscribeState()
      unsubscribeState = null
    }
  }

  /** 当前用户是否对该会话免打扰（F-10）；未加载/未设置时为 false。 */
  function isMuted(conversationId: string): boolean {
    return getById(conversationId)?.muted === true
  }

  /**
   * 更新当前用户的会话成员设置（F-10）：PATCH 服务端后以响应更新 store。
   * 服务端不广播该设置；失败时抛出 ApiError 由调用方提示。
   */
  async function setMemberSettings(
    conversationId: string,
    settings: { pinned?: boolean; muted?: boolean },
  ): Promise<Conversation> {
    const updated = await conversationsApi.updateMemberSettings(conversationId, settings)
    upsert([updated])
    return updated
  }

  /** Chat 页监听路由变化时写入，供 messages store 判断提示音/通知是否需要提醒。 */
  function setActiveConversation(conversationId: string | null): void {
    activeConversationId.value = conversationId
  }

  return {
    items,
    sorted,
    loading,
    loaded,
    activeConversationId,
    lastRemovedConversationId,
    removedConversationIds,
    upsert,
    applyMessage,
    getById,
    unreadOf,
    isMuted,
    setMemberSettings,
    totalUnread,
    markRead,
    readSeqOf,
    peerReadSeqOf,
    restoreFromCache,
    onConversationRemoved,
    handleRemovedConversations,
    clearRemovedNotice,
    setActiveConversation,
    load,
    createDirect,
    createGroup,
    subscribeRealtime,
    clear,
  }
})
