import { ref } from 'vue'
import { defineStore } from 'pinia'
import type {
  FileContent,
  Message,
  MessageAckFrame,
  MessageEditFrame,
  MessageEditedFrame,
  MessageReactionsFrame,
  MessageRecallFrame,
  MessageRecalledFrame,
  MessageSendFrame,
  MessageSendType,
  ReactionSummary,
  ServerFrame,
  SyncRequestFrame,
  SyncResponseFrame,
  VoiceContent,
} from '../types/frames'
import { type GapCheckResult, buildSyncCursors, detectGap, mergeMessages } from '../core/sync'
import { messagesApi, type MessageContextPage } from '../api/messages'
import { chatWs, type WsState } from '../core/ws'
import { MAX_TEXT_LENGTH, config, resolveApiOrigin } from '../core/config'
import {
  deletePending,
  deleteRecord,
  getAllPending,
  getMessagesByConversation,
  getMeta,
  saveMessages,
  savePending,
  setMeta,
  clearPending,
  STORE_CONVERSATIONS,
  STORE_MESSAGES,
} from '../core/db'
import {
  fromPendingRecord,
  reconcileClientMsgIds,
  toPendingRecord,
} from '../core/pendingQueue'
import {
  getDesktopNotifier,
  playIncomingSound,
  shouldPlayIncomingSound,
  shouldShowDesktopNotification,
  showWebNotification,
} from '../core/notify'
import type { ReplyPreviewLike } from '../core/replyPreview'
import { buildVoiceContent, isVoiceContent, voicePreviewText } from '../core/voice'
import { useConversationsStore } from './conversations'
import { useAuthStore } from './auth'
import { useSettingsStore } from './settings'
import { isAndroidBackgroundAvailable } from '../core/backgroundNotifications'

/** 历史分页大小（服务端默认 50、上限 200；AGENTS.md §5.5 每会话每批 ≤200）。 */
const PAGE_SIZE = 50
// 文本上限常量下沉到 core/config（F-04）；此处保留 re-export 兼容既有 import。
export { MAX_TEXT_LENGTH }
/** 首次发送后等待 ack 的时长；超时进入重试（2s/5s/10s，同一 clientMsgId 幂等）。 */
const ACK_TIMEOUT_MS = 8000
const RETRY_DELAYS_MS = [2000, 5000, 10000] as const
/** 本会话已知最大 seq 的 IndexedDB 持久化 key（补拉游标）。 */
const META_LAST_SEQ_KEY = 'lastSeqByConversation'
/** F-34：各会话状态同步水位（撤回/编辑补齐游标）的 IndexedDB 持久化 key。 */
const META_SYNC_STATE_KEY = 'stateSyncedAtByConversation'
/** 一次 syncAll 最多发多少轮 sync.request（防止服务端异常时死循环）。 */
const SYNC_MAX_ROUNDS = 20
/** 同一会话检测到 seq 缺口时，最多用同一游标重试补拉的次数。 */
const SYNC_GAP_RETRIES = 1

export type PendingStatus = 'sending' | 'failed'

/** 本地乐观消息：尚未收到 message.ack，只用于 UI 展示，不属于服务端事实源。 */
export interface PendingMessage {
  clientMsgId: string
  conversationId: string
  senderId: string
  /** text / image / file / voice（system 仅服务端生成）。 */
  msgType: MessageSendType
  /** 文本为字符串；图片/文件为 FileContent；语音为 VoiceContent。 */
  content: string | FileContent | VoiceContent
  createdAt: number
  status: PendingStatus
  /** 已重试次数（0 = 首次发送，最多 RETRY_DELAYS_MS.length 次重试）。 */
  retries: number
  /** F-09：回复引用目标 messageId（可选；重试/恢复重发都会带上）。 */
  replyTo?: string
  /** F-09：本地引用条预览（仅内存；ack/message.new 后由服务端 reply 摘要取代）。 */
  replyPreview?: ReplyPreviewLike
}

/** message.send 的 reqId（error 帧据此把失败落到具体待发消息）。 */
function reqIdOf(clientMsgId: string): string {
  return `msg-${clientMsgId}`
}

// 消息缓存、历史分页、发送状态机与断线补拉（M2-06）。事实源在服务端（AGENTS.md §2.8），
// 本地按会话缓存 Message[]（已落库）与 PendingMessage[]（乐观发送中），
// IndexedDB 只做缓存（core/db.ts）；收发统一走 core/ws.ts 的 chatWs。
export const useMessagesStore = defineStore('messages', () => {
  const byConversation = ref<Record<string, Message[]>>({})
  const lastSeqByConversation = ref<Record<string, number>>({})
  /** F-34：各会话已同步到的服务端时间（撤回/编辑补齐游标）。 */
  const stateSyncedAtByConversation = ref<Record<string, number>>({})
  const pendingByConversation = ref<Record<string, PendingMessage[]>>({})
  /** 待发记录状态/重试次数变更的版本号：组件 watcher 依赖它感知原地状态变更（如重试耗尽→failed）。 */
  const pendingRevision = ref(0)
  const hasMoreByConversation = ref<Record<string, boolean>>({})
  const loadedConversations = ref<Record<string, boolean>>({})
  const loadingInitialByConversation = ref<Record<string, boolean>>({})
  const loadingMoreByConversation = ref<Record<string, boolean>>({})
  /** 是否正在补拉（UI 可显示同步状态；同一时刻只允许一轮 syncAll）。 */
  const syncing = ref(false)
  /** 最近一次补拉完成时间（UTC 毫秒）。 */
  const lastSyncedAt = ref(0)

  // 定时器/订阅/游标等非响应式状态放在闭包里，避免 UI 重新渲染抖动。
  const ackTimers = new Map<string, ReturnType<typeof setTimeout>>()
  const retryTimers = new Map<string, ReturnType<typeof setTimeout>>()
  const unsubscribers: Array<() => void> = []
  let visibilityHandler: (() => void) | null = null
  let realtimeReady = false
  let syncInFlight = false
  let syncQueued = false
  let cacheRestored = false

  function messagesOf(conversationId: string): Message[] {
    return byConversation.value[conversationId] ?? []
  }

  function pendingOf(conversationId: string): PendingMessage[] {
    return pendingByConversation.value[conversationId] ?? []
  }

  function hasMoreOf(conversationId: string): boolean {
    return hasMoreByConversation.value[conversationId] === true
  }

  function isLoaded(conversationId: string): boolean {
    return loadedConversations.value[conversationId] === true
  }

  function isLoadingMore(conversationId: string): boolean {
    return loadingMoreByConversation.value[conversationId] === true
  }

  function lastSeqOf(conversationId: string): number {
    return lastSeqByConversation.value[conversationId] ?? 0
  }

  function setLastSeq(conversationId: string, seq: number): void {
    if (seq <= lastSeqOf(conversationId)) return
    lastSeqByConversation.value = { ...lastSeqByConversation.value, [conversationId]: seq }
  }

  /** 持久化各会话的补拉游标（IndexedDB meta）。 */
  function persistLastSeqMap(): void {
    void setMeta(META_LAST_SEQ_KEY, { ...lastSeqByConversation.value }).catch(() => {
      // 缓存写入失败不影响主流程。
    })
  }

  /** F-34：持久化各会话状态同步水位。 */
  function persistStateSyncMap(): void {
    void setMeta(META_SYNC_STATE_KEY, { ...stateSyncedAtByConversation.value }).catch(() => {
      // 缓存写入失败不影响主流程。
    })
  }

  /**
   * F-34：把 sync.response 的 updates 覆盖到已缓存消息（仅按 messageId 定位，不新增/重排）。
   * 未缓存的消息（更早历史或已被淘汰）忽略，等用户加载历史时自然拿到最新状态。
   */
  function applySyncUpdates(conversationId: string, updates: readonly Message[]): void {
    if (updates.length === 0) return
    const list = messagesOf(conversationId)
    if (list.length === 0) return
    const byId = new Map(updates.map((message) => [message.messageId, message]))
    const changed: Message[] = []
    const next = list.map((message) => {
      const fresh = byId.get(message.messageId)
      if (!fresh) return message
      if (
        fresh.deletedAt === message.deletedAt &&
        fresh.editedAt === message.editedAt &&
        fresh.content === message.content &&
        isSameReactions(fresh.reactions, message.reactions)
      ) {
        return message
      }
      const updated: Message = { ...message, content: fresh.content }
      if (fresh.deletedAt === undefined) delete updated.deletedAt
      else updated.deletedAt = fresh.deletedAt
      if (fresh.editedAt === undefined) delete updated.editedAt
      else updated.editedAt = fresh.editedAt
      const reactionsChanged = !isSameReactions(fresh.reactions, message.reactions)
      if (fresh.reactions === undefined) delete updated.reactions
      else updated.reactions = fresh.reactions.map((item) => ({ ...item }))
      if (reactionsChanged) bumpReactionVersion(conversationId, message.messageId, updated.reactions)
      changed.push(updated)
      return updated
    })
    if (changed.length === 0) return
    byConversation.value = { ...byConversation.value, [conversationId]: next }
    persistMessages(changed)
  }

  /** 把服务端事实消息写入 IndexedDB（按 messageId 覆盖，天然幂等）。 */
  function persistMessages(messages: readonly Message[]): void {
    if (messages.length === 0) return
    void saveMessages(messages).catch(() => {
      // 缓存写入失败不影响主流程。
    })
  }

  /** F-18：用服务端推送的撤回/编辑结果覆盖本地消息（按 messageId 定位），并持久化。 */
  function patchStoredMessage(
    conversationId: string,
    messageId: string,
    patch: Partial<Pick<Message, 'content' | 'deletedAt' | 'editedAt' | 'reactions'>>,
  ): void {
    const list = messagesOf(conversationId)
    const index = list.findIndex((message) => message.messageId === messageId)
    if (index < 0) return
    const updated: Message = { ...list[index], ...patch }
    const next = list.slice()
    next[index] = updated
    byConversation.value = { ...byConversation.value, [conversationId]: next }
    persistMessages([updated])
  }

  /**
   * C3：每条消息当前回应摘要的本地版本号。实时帧/补拉/历史刷新都会推进它，
   * 界面发出 REST 请求后可用 {@link reactionVersionOf} 判断响应是否已过期。
   */
  const reactionVersionByMessage = new Map<string, number>()

  function bumpReactionVersion(
    conversationId: string,
    messageId: string,
    reactions: readonly ReactionSummary[] | undefined,
  ): void {
    const key = `${conversationId}:${messageId}`
    reactionVersionByMessage.set(key, (reactionVersionByMessage.get(key) ?? 0) + 1)
    // F-37：定位窗口等临时视图靠原始帧补丁更新，不能依赖消息是否已在缓存中。
    lastMessagePatch.value = {
      conversationId,
      messageId,
      patch: { reactions: reactions === undefined ? undefined : reactions.map((item) => ({ ...item })) },
    }
  }

  /** C3：当前消息回应摘要的版本号（0 表示本端还没收到过服务端摘要）。 */
  function reactionVersionOf(conversationId: string, messageId: string): number {
    return reactionVersionByMessage.get(`${conversationId}:${messageId}`) ?? 0
  }

  /** C3：应用服务端权威回应摘要（实时帧/补拉/历史刷新），并推进本地版本号。 */
  function applyReactionSummary(
    conversationId: string,
    messageId: string,
    reactions: readonly ReactionSummary[],
  ): void {
    const normalized = reactions.map((item) => ({ ...item }))
    bumpReactionVersion(conversationId, messageId, normalized)
    patchStoredMessage(conversationId, messageId, { reactions: normalized })
  }

  /** C3：回应摘要是否等价（避免无变化时重渲染与落库）。 */
  function isSameReactions(
    left: readonly ReactionSummary[] | undefined,
    right: readonly ReactionSummary[] | undefined,
  ): boolean {
    const a = left ?? []
    const b = right ?? []
    if (a.length !== b.length) return false
    return a.every(
      (item, index) =>
        item.emoji === b[index].emoji && item.count === b[index].count && item.mine === b[index].mine,
    )
  }

  /** F-37：最近一次实时撤回/编辑帧的补丁（供定位窗口等非缓存视图同步）。 */
  const lastMessagePatch = ref<{
    conversationId: string
    messageId: string
    patch: Partial<Message>
  } | null>(null)

  function handleRecalled(frame: MessageRecalledFrame): void {
    // 撤回后内容为空文本、不再带文件信息；deletedAt 非空即视为已撤回。
    const patch = { content: '', deletedAt: frame.deletedAt }
    // F-37：定位窗口等临时视图要靠原始帧补丁更新，不能依赖消息是否已在缓存中。
    lastMessagePatch.value = { conversationId: frame.conversationId, messageId: frame.messageId, patch }
    patchStoredMessage(frame.conversationId, frame.messageId, patch)
  }

  function handleEdited(frame: MessageEditedFrame): void {
    const patch = { content: frame.content, editedAt: frame.editedAt }
    // F-37：定位窗口等临时视图要靠原始帧补丁更新，不能依赖消息是否已在缓存中。
    lastMessagePatch.value = { conversationId: frame.conversationId, messageId: frame.messageId, patch }
    patchStoredMessage(frame.conversationId, frame.messageId, patch)
  }

  /** 合并服务端事实消息：去重 + 按 seq 升序，并持久化消息与游标。 */
  function merge(conversationId: string, incoming: readonly Message[]): GapCheckResult {
    // F-03：「服务端已落库但 ack 丢失」时，补拉/历史里会出现同一 clientMsgId 的消息；
    // 命中后清掉本地待发消息（含重试定时器与缓存记录），避免重复展示。
    const pendingRecords = pendingOf(conversationId).map(toPendingRecord)
    if (pendingRecords.length > 0) {
      const resolved = reconcileClientMsgIds(incoming, pendingRecords)
      for (const clientMsgId of resolved) {
        const pending = findPending(conversationId, clientMsgId)
        if (!pending) continue
        clearTimers(clientMsgId)
        removePending(conversationId, clientMsgId)
      }
    }

    const result = detectGap(lastSeqOf(conversationId), incoming)
    const merged = mergeMessages(messagesOf(conversationId), incoming)
    byConversation.value = { ...byConversation.value, [conversationId]: merged }
    setLastSeq(conversationId, result.lastSeq)
    persistMessages(incoming)
    persistLastSeqMap()
    return result
  }

  /** 用补拉结果直接推进游标（调用方已按批处理时使用）。 */
  function advanceLastSeq(conversationId: string, lastSeq: number): void {
    setLastSeq(conversationId, lastSeq)
    persistLastSeqMap()
  }

  /**
   * 启动/刷新时从 IndexedDB 恢复缓存（先渲染本地记录，再走 syncAll 补拉增量）。
   * 需要先调用 conversations.restoreFromCache()，以便知道要恢复哪些会话的消息。
   */
  async function restoreFromCache(): Promise<void> {
    if (cacheRestored) return
    cacheRestored = true
    try {
      const stored = await getMeta<Record<string, number>>(META_LAST_SEQ_KEY)
      if (stored && typeof stored === 'object') {
        const restored: Record<string, number> = {}
        for (const [conversationId, seq] of Object.entries(stored)) {
          if (typeof seq === 'number' && Number.isInteger(seq) && seq > 0) {
            restored[conversationId] = seq
          }
        }
        lastSeqByConversation.value = { ...lastSeqByConversation.value, ...restored }
      }

      const storedState = await getMeta<Record<string, number>>(META_SYNC_STATE_KEY)
      if (storedState && typeof storedState === 'object') {
        const restoredState: Record<string, number> = {}
        for (const [conversationId, syncedAt] of Object.entries(storedState)) {
          if (typeof syncedAt === 'number' && Number.isInteger(syncedAt) && syncedAt > 0) {
            restoredState[conversationId] = syncedAt
          }
        }
        stateSyncedAtByConversation.value = {
          ...stateSyncedAtByConversation.value,
          ...restoredState,
        }
      }

      const conversations = useConversationsStore()
      for (const conversation of conversations.items) {
        const cached = await getMessagesByConversation(conversation.conversationId)
        if (cached.length === 0) continue
        // 与内存中已有消息合并（不覆盖已拉到的新消息），再按 seq 去重排序（M3-00 N4）。
        const merged = mergeMessages(messagesOf(conversation.conversationId), cached)
        byConversation.value = { ...byConversation.value, [conversation.conversationId]: merged }
        loadedConversations.value = {
          ...loadedConversations.value,
          [conversation.conversationId]: true,
        }
        // 缓存可能是截断的历史：允许继续向上加载更早消息。
        hasMoreByConversation.value = {
          ...hasMoreByConversation.value,
          [conversation.conversationId]: true,
        }
        const maxSeq = merged[merged.length - 1].seq
        setLastSeq(conversation.conversationId, maxSeq)
      }
      persistLastSeqMap()
      await restorePendingFromCache(conversations.items.map((item) => item.conversationId))
      // 恢复完成后再触发重发：此时 WS 可能已认证（刷新场景），也可能还没连上（首次进入由重连事件触发）。
      resumePending()
    } catch (error) {
      console.warn('[messages] 读取本地缓存失败', error)
    }
  }

  /**
   * F-03：从 IndexedDB 恢复待发消息。
   * - 脏数据 / 会话已不存在的孤儿记录直接删除；
   * - status='sending' 保持 sending、'failed' 保持 failed；
   * - 只恢复进内存，由 resumePending() 在 WS 已认证时重发，避免未连接时耗光重试次数。
   */
  async function restorePendingFromCache(conversationIds: readonly string[]): Promise<void> {
    const stored = await getAllPending()
    if (stored.length === 0) return
    const aliveConversations = new Set(conversationIds)
    const grouped: Record<string, PendingMessage[]> = {}
    for (const raw of stored) {
      const record = fromPendingRecord(raw)
      if (!record) {
        // 旧版本/被外部破坏的记录：能取到 clientMsgId 就顺手清掉，避免反复读取。
        const maybe = raw as unknown as { clientMsgId?: unknown }
        if (typeof maybe?.clientMsgId === 'string') forgetPendingRecord(maybe.clientMsgId)
        continue
      }
      if (!aliveConversations.has(record.conversationId)) {
        forgetPendingRecord(record.clientMsgId)
        continue
      }
      const list = grouped[record.conversationId] ?? (grouped[record.conversationId] = [])
      list.push({
        clientMsgId: record.clientMsgId,
        conversationId: record.conversationId,
        senderId: record.senderId,
        msgType: record.msgType,
        content: record.content,
        createdAt: record.createdAt,
        status: record.status,
        retries: record.retries,
        ...(record.replyTo ? { replyTo: record.replyTo } : {}),
      })
    }

    const merged: Record<string, PendingMessage[]> = {}
    for (const [conversationId, restored] of Object.entries(grouped)) {
      const existing = pendingOf(conversationId)
      const known = new Set(existing.map((item) => item.clientMsgId))
      const list = [...existing]
      for (const pending of restored) {
        if (known.has(pending.clientMsgId)) continue
        list.push(pending)
      }
      if (list.length > 0) merged[conversationId] = list
    }
    if (Object.keys(merged).length > 0) {
      pendingByConversation.value = { ...pendingByConversation.value, ...merged }
    }
  }

  /**
   * F-03：把恢复出来、仍处于 sending 的待发消息按原 clientMsgId 重新发出。
   * 仅在 WS 已认证时调用；有在途 ack / 重试定时器的消息跳过，避免重复发送。
   * 不重置 retries：补发是「恢复投递」而非新一轮重试，短暂重连不应抹掉已执行的
   * 在线重试次数，也不应额外消耗预算。
   */
  function resumePending(): void {
    if (chatWs.state !== 'authed') return
    for (const list of Object.values(pendingByConversation.value)) {
      for (const pending of list) {
        if (pending.status !== 'sending') continue
        if (ackTimers.has(pending.clientMsgId) || retryTimers.has(pending.clientMsgId)) continue
        transmit(pending)
      }
    }
  }

  /**
   * 首次进入会话：优先使用本地缓存（已由 restoreFromCache 载入），
   * 否则 REST 拉最新一页历史（limit=50）；缓存存在时由 syncAll 补齐增量。
   */
  async function loadInitial(conversationId: string): Promise<void> {
    if (isLoaded(conversationId) || loadingInitialByConversation.value[conversationId]) return
    if (messagesOf(conversationId).length > 0) {
      loadedConversations.value = {
        ...loadedConversations.value,
        [conversationId]: true,
      }
      // F-18：缓存可能落后于服务端（离线期间的撤回/编辑），打开会话后拉最新一页校正。
      void refreshRecent(conversationId)
      return
    }
    loadingInitialByConversation.value = {
      ...loadingInitialByConversation.value,
      [conversationId]: true,
    }
    try {
      const page = await messagesApi.history(conversationId, undefined, PAGE_SIZE)
      const before = lastSeqOf(conversationId)
      const result = merge(conversationId, page.messages)
      const pageMaxSeq = page.messages.reduce((max, item) => Math.max(max, item.seq), before)
      // 首屏基线（本地无游标）直接以本页最大 seq 为游标；已有游标且检测到缺口时保持游标不动，
      // 交给 syncAll 从缺口处补拉，避免把缺口吞掉（M3-00 N1）。
      if (before === 0 || !result.hasGap) {
        advanceLastSeq(conversationId, Math.max(pageMaxSeq, result.lastSeq))
      }
      hasMoreByConversation.value = {
        ...hasMoreByConversation.value,
        [conversationId]: page.hasMore,
      }
      loadedConversations.value = { ...loadedConversations.value, [conversationId]: true }
      void refreshRecent(conversationId)
    } finally {
      loadingInitialByConversation.value = {
        ...loadingInitialByConversation.value,
        [conversationId]: false,
      }
    }
  }

  /** 向上加载更早消息：以当前最早 seq 作为 beforeSeq 再拉一页。 */
  async function loadMore(conversationId: string): Promise<void> {
    const list = messagesOf(conversationId)
    if (!hasMoreOf(conversationId) || isLoadingMore(conversationId) || list.length === 0) return
    loadingMoreByConversation.value = {
      ...loadingMoreByConversation.value,
      [conversationId]: true,
    }
    try {
      const beforeSeq = list[0].seq
      const page = await messagesApi.history(conversationId, beforeSeq, PAGE_SIZE)
      merge(conversationId, page.messages)
      hasMoreByConversation.value = {
        ...hasMoreByConversation.value,
        [conversationId]: page.hasMore,
      }
    } finally {
      loadingMoreByConversation.value = {
        ...loadingMoreByConversation.value,
        [conversationId]: false,
      }
    }
  }

  /**
   * F-36：加载目标消息附近的上下文窗口并合并进缓存。
   * 只补充历史消息，不推进 lastSeq / 同步水位，避免历史跳转被当成实时补拉。
   */
  /**
   * F-37：按目标读取定位上下文窗口。定位窗口只用于展示，不合并进会话时间线、
   * 不写 IndexedDB，也不推进 lastSeq / 同步水位 / 历史分页起点。
   */
  async function loadContextAround(
    conversationId: string,
    target: { messageId?: string; seq?: number; before?: number; after?: number },
  ): Promise<MessageContextPage> {
    return messagesApi.getContext(conversationId, target)
  }

  function isSameContent(a: Message['content'], b: Message['content']): boolean {
    if (typeof a === 'string' || typeof b === 'string') return a === b
    return JSON.stringify(a) === JSON.stringify(b)
  }

  /**
   * F-18：打开会话后用 REST 拉最新一页历史，按 messageId 覆盖本地 content/deletedAt/editedAt/reply。
   * 已知限制：离线期间最近 50 条以外的撤回/编辑不会自动回放（需手动加载历史或刷新）。
   */
  async function refreshRecent(conversationId: string): Promise<void> {
    try {
      const page = await messagesApi.history(conversationId, undefined, PAGE_SIZE)
      const freshById = new Map(page.messages.map((message) => [message.messageId, message]))
      const changed: Message[] = []
      const next = messagesOf(conversationId).map((message) => {
        const fresh = freshById.get(message.messageId)
        if (!fresh) return message
        if (
          fresh.deletedAt === message.deletedAt &&
          fresh.editedAt === message.editedAt &&
          isSameContent(fresh.content, message.content) &&
          isSameReactions(fresh.reactions, message.reactions)
        ) {
          return message
        }
        const updated: Message = { ...message, content: fresh.content }
        if (fresh.deletedAt === undefined) delete updated.deletedAt
        else updated.deletedAt = fresh.deletedAt
        if (fresh.editedAt === undefined) delete updated.editedAt
        else updated.editedAt = fresh.editedAt
        if (fresh.reply) updated.reply = fresh.reply
        const reactionsChanged = !isSameReactions(fresh.reactions, message.reactions)
        if (fresh.reactions === undefined) delete updated.reactions
        else updated.reactions = fresh.reactions.map((item) => ({ ...item }))
        if (reactionsChanged) bumpReactionVersion(conversationId, message.messageId, updated.reactions)
        changed.push(updated)
        return updated
      })
      if (changed.length === 0) return
      byConversation.value = { ...byConversation.value, [conversationId]: next }
      persistMessages(changed)
    } catch (error) {
      console.warn('[messages] 刷新最近消息状态失败（将以补拉为准）', error)
    }
  }

  /**
   * 补拉所有会话的增量（协议 §4.4）：
   * 以本地 lastSeq 生成 cursors → sync.request → 合并并持久化；
   * 每轮最多 100 个 cursors，取满一批（200 条）且有更多时继续下一轮；
   * 合并后检测到 seq 缺口则用同一游标立即再补一次（有限次）。
   */
  async function syncAll(): Promise<void> {
    if (chatWs.state !== 'authed') return
    if (syncInFlight) {
      syncQueued = true
      return
    }
    const conversations = useConversationsStore()
    if (conversations.items.length === 0) return

    syncInFlight = true
    syncing.value = true
    try {
      let pending = new Map<string, number>()
      for (const conversation of conversations.items) {
        pending.set(conversation.conversationId, lastSeqOf(conversation.conversationId))
      }
      const gapRetries = new Map<string, number>()
      let rounds = 0

      while (rounds < SYNC_MAX_ROUNDS) {
        rounds += 1
        const updatedAfterByConversation = new Map<string, number>()
        for (const conversationId of pending.keys()) {
          const syncedAt = stateSyncedAtByConversation.value[conversationId]
          if (syncedAt !== undefined && syncedAt > 0) {
            updatedAfterByConversation.set(conversationId, syncedAt)
          }
        }
        const cursors = buildSyncCursors(pending, 100, updatedAfterByConversation)
        if (cursors.length === 0) break

        const sentSeq = new Map(cursors.map((cursor) => [cursor.conversationId, cursor.lastSeq]))
        const frame: SyncRequestFrame = { type: 'sync.request', cursors }
        let response: SyncResponseFrame
        try {
          response = await chatWs.request<SyncResponseFrame>(frame)
        } catch (error) {
          console.warn('[messages] sync.request 失败，稍后重试', error)
          break
        }

        const next = new Map<string, number>()
        for (const batch of response.batches ?? []) {
          if (!sentSeq.has(batch.conversationId)) continue
          const conversationId = batch.conversationId
          const messages = batch.messages ?? []
          // F-34：updates 可能在没有新消息时单独出现（断线期间仅发生撤回/编辑）。
          const updates = batch.updates ?? []
          const cursor = sentSeq.get(conversationId) ?? lastSeqOf(conversationId)
          const gap = messages.length > 0 ? detectGap(cursor, messages) : { hasGap: false, lastSeq: cursor }
          if (messages.length > 0) {
            merge(conversationId, messages)
            conversations.applyMessage(conversationId, messages[messages.length - 1])
          }
          applySyncUpdates(conversationId, updates)

          if (batch.updatedThrough !== undefined) {
            stateSyncedAtByConversation.value = {
              ...stateSyncedAtByConversation.value,
              [conversationId]: batch.updatedThrough,
            }
            persistStateSyncMap()
          } else if (updates.length === 0 && messages.length === 0) {
            // 空批次（理论上服务端不返回）不占用游标。
            continue
          }

          if (gap.hasGap) {
            const retried = gapRetries.get(conversationId) ?? 0
            if (retried < SYNC_GAP_RETRIES) {
              gapRetries.set(conversationId, retried + 1)
              next.set(conversationId, cursor)
            }
            continue
          }

          if (messages.length >= config.syncBatchSize) {
            next.set(conversationId, lastSeqOf(conversationId))
          }
        }

        // 本轮请求未覆盖的会话（一次最多 100 个 cursors）保留到下一轮，不能丢（M3-00 N2）。
        for (const [conversationId, seq] of pending) {
          if (!sentSeq.has(conversationId)) next.set(conversationId, seq)
        }
        pending = next
        // 没有待补会话且服务端无更多数据时才结束，避免把未覆盖会话丢掉（M3-00 N3）。
        if (pending.size === 0 && !response.hasMore) break
      }

      lastSyncedAt.value = Date.now()
    } finally {
      syncInFlight = false
      syncing.value = false
      if (syncQueued) {
        syncQueued = false
        void syncAll()
      }
    }
  }

  function findPending(conversationId: string, clientMsgId: string): PendingMessage | undefined {
    return pendingOf(conversationId).find((item) => item.clientMsgId === clientMsgId)
  }

  function findPendingAnywhere(clientMsgId: string): PendingMessage | undefined {
    for (const list of Object.values(pendingByConversation.value)) {
      const found = list.find((item) => item.clientMsgId === clientMsgId)
      if (found) return found
    }
    return undefined
  }

  function removePending(conversationId: string, clientMsgId: string): void {
    const rest = pendingOf(conversationId).filter((item) => item.clientMsgId !== clientMsgId)
    pendingByConversation.value = { ...pendingByConversation.value, [conversationId]: rest }
    forgetPendingRecord(clientMsgId)
  }

  /** F-03：把待发消息写入 IndexedDB（失败静默，缓存只用于刷新后恢复，不影响发送主流程）。 */
  function persistPending(pending: PendingMessage): void {
    void savePending(toPendingRecord(pending)).catch(() => {
      // 缓存写入失败不影响发送。
    })
  }

  /** F-03：删除单条待发消息缓存。 */
  function forgetPendingRecord(clientMsgId: string): void {
    if (!clientMsgId) return
    void deletePending(clientMsgId).catch(() => {
      // 缓存写入失败不影响发送。
    })
  }

  function clearTimers(clientMsgId: string): void {
    const ackTimer = ackTimers.get(clientMsgId)
    if (ackTimer) {
      clearTimeout(ackTimer)
      ackTimers.delete(clientMsgId)
    }
    const retryTimer = retryTimers.get(clientMsgId)
    if (retryTimer) {
      clearTimeout(retryTimer)
      retryTimers.delete(clientMsgId)
    }
  }

  /**
   * 连接离开 authed：取消当前连接遗留的全部 ack / 重试定时器。
   * 旧连接的 ack 永远不会到，定时重试也会在离线期间白跑并消耗预算；
   * pending 保持 sending 与原 clientMsgId，等重新认证后由 resumePending() 补发。
   */
  function clearAllTimers(): void {
    for (const clientMsgId of new Set([...ackTimers.keys(), ...retryTimers.keys()])) {
      clearTimers(clientMsgId)
    }
  }

  function scheduleAckTimeout(pending: PendingMessage): void {
    clearTimers(pending.clientMsgId)
    const timer = setTimeout(() => {
      ackTimers.delete(pending.clientMsgId)
      handleAckTimeout(pending.conversationId, pending.clientMsgId)
    }, ACK_TIMEOUT_MS)
    ackTimers.set(pending.clientMsgId, timer)
  }

  /**
   * 发送一帧 message.send（带 reqId；幂等键 clientMsgId 由服务端去重）。
   * countRetry=true 表示这是定时重试：仅在 WS 成功受理该帧后才计入 retries 并写回缓存，
   * 未受理（未认证 / send 失败）不扣次数，刷新后也不会重置已消耗的预算。
   */
  function transmit(pending: PendingMessage, countRetry = false): void {
    if (pending.status !== 'sending') return
    // 弱网 / 认证未完成：挂起等待网络，不消耗 2s/5s/10s 重试预算。
    // 保持 sending 与原 clientMsgId，由 subscribeRealtime 的 statechange('authed')
    // → resumePending() 原键补发，再由 ack 或补拉对账；未认证的 socket 上发帧也会丢。
    if (chatWs.state !== 'authed') {
      clearTimers(pending.clientMsgId)
      return
    }
    const frame: MessageSendFrame = {
      type: 'message.send',
      reqId: reqIdOf(pending.clientMsgId),
      clientMsgId: pending.clientMsgId,
      conversationId: pending.conversationId,
      // M3-07：图片/文件消息必须沿用原 msgType，否则服务端会按纯文本拒绝或落错类型。
      msgType: pending.msgType,
      content: pending.content,
      ...(pending.replyTo ? { replyTo: pending.replyTo } : {}),
    }
    if (chatWs.send(frame)) {
      if (countRetry) {
        pending.retries += 1
        // 计入已消耗预算并写回 IndexedDB：刷新恢复时读到的是实际重发次数，不会重跑整轮。
        persistPending(pending)
      }
      scheduleAckTimeout(pending)
      return
    }
    // 状态仍显示 authed 但 socket 已关闭的极短窗口：同样挂起，等 onclose→重连→authed
    // 重新触发 resumePending，不在这里把离线问题算进重试次数。
    clearTimers(pending.clientMsgId)
  }

  function handleAckTimeout(conversationId: string, clientMsgId: string): void {
    const pending = findPending(conversationId, clientMsgId)
    if (!pending || pending.status !== 'sending') return
    scheduleRetry(pending)
  }

  /**
   * 按 2s/5s/10s 间隔重发同一 clientMsgId；3 次重发仍失败则标记 failed（可手动重发）。
   * 计数由 transmit(pending, true) 在 WS 成功受理重发帧后才递增并落库：
   * 仅排定未执行、断线取消、或帧未被受理都不占预算，刷新后次数也不会回退。
   */
  function scheduleRetry(pending: PendingMessage): void {
    clearTimers(pending.clientMsgId)
    if (pending.retries >= RETRY_DELAYS_MS.length) {
      pending.status = 'failed'
      // 原地变更不会触发依赖 pending 数组的 watcher，显式通知（附件面板据此显示发送失败）。
      pendingRevision.value += 1
      // F-03：落库「失败」状态，刷新后仍显示可重发。
      persistPending(pending)
      return
    }
    const delay = RETRY_DELAYS_MS[pending.retries]
    const timer = setTimeout(() => {
      retryTimers.delete(pending.clientMsgId)
      transmit(pending, true)
    }, delay)
    retryTimers.set(pending.clientMsgId, timer)
  }

  /** 乐观发送通用入口（text / image / file / voice）：本地立即展示（sending），ack 到达后转为服务端事实消息。 */
  function sendContent(
    conversationId: string,
    msgType: MessageSendType,
    content: string | FileContent | VoiceContent,
    senderId: string,
    replyTo?: string,
    replyPreview?: ReplyPreviewLike,
    clientMsgId?: string,
  ): boolean {
    if (!conversationId || !senderId) return false
    const pending: PendingMessage = {
      clientMsgId: clientMsgId || crypto.randomUUID(),
      conversationId,
      senderId,
      msgType,
      content,
      createdAt: Date.now(),
      status: 'sending',
      retries: 0,
      ...(replyTo ? { replyTo } : {}),
      ...(replyPreview ? { replyPreview } : {}),
    }
    pendingByConversation.value = {
      ...pendingByConversation.value,
      [conversationId]: [...pendingOf(conversationId), pending],
    }
    // F-03：先落库再发送，刷新/崩溃后能恢复这条待发消息。
    persistPending(pending)
    transmit(pending)
    return true
  }

  /** 乐观发送文本消息：本地立即展示（sending），ack 到达后转为服务端事实消息。 */
  function sendText(
    conversationId: string,
    text: string,
    senderId: string,
    replyTo?: string,
    replyPreview?: ReplyPreviewLike,
  ): boolean {
    const content = text.trim()
    if (!content) return false
    if (content.length > MAX_TEXT_LENGTH) {
      // 输入框已有 maxlength 拦截，这里兜底防止程序化调用超长文本（M3-00 N10）。
      console.warn(`[messages] 文本超过 ${MAX_TEXT_LENGTH} 字符上限，已拦截`)
      return false
    }
    return sendContent(conversationId, 'text', content, senderId, replyTo, replyPreview)
  }

  /** 乐观发送图片/文件消息（M3-07）：content 必须是已上传成功的 FileContent（服务端会按 files 记录再归一）。 */
  function sendFileMessage(
    conversationId: string,
    msgType: 'image' | 'file',
    content: FileContent,
    senderId: string,
    replyTo?: string,
    replyPreview?: ReplyPreviewLike,
    clientMsgId?: string,
  ): boolean {
    if (
      !content.fileId ||
      !content.name ||
      !content.mime ||
      !Number.isFinite(content.size) ||
      content.size <= 0
    ) {
      console.warn('[messages] 文件消息 content 不完整，已拦截')
      return false
    }
    return sendContent(
      conversationId,
      msgType,
      { ...content },
      senderId,
      replyTo,
      replyPreview,
      clientMsgId,
    )
  }

  /** 乐观发送语音消息（F-28）：fileId 必须来自已上传的 webm 文件，时长 1000~60000 毫秒。 */
  function sendVoiceMessage(
    conversationId: string,
    fileId: string,
    durationMs: number,
    senderId: string,
  ): boolean {
    const content = buildVoiceContent(fileId, durationMs)
    if (!content) {
      console.warn('[messages] 语音消息参数不合法（fileId 或时长），已拦截')
      return false
    }
    return sendContent(conversationId, 'voice', content, senderId)
  }

  /** 重发失败消息：复用同一 clientMsgId（服务端幂等，不会重复落库）。 */
  function retrySend(conversationId: string, clientMsgId: string): void {
    const pending = findPending(conversationId, clientMsgId)
    if (!pending || pending.status !== 'failed') return
    pending.status = 'sending'
    pending.retries = 0
    pendingRevision.value += 1
    // F-03：状态回到 sending 也要同步缓存，避免刷新后仍显示失败。
    persistPending(pending)
    transmit(pending)
  }

  /** F-18：撤回自己的消息（结果通过 message.recalled 广播/错误帧返回，不在本地乐观更新）。 */
  function sendRecall(conversationId: string, messageId: string): boolean {
    const frame: MessageRecallFrame = { type: 'message.recall', conversationId, messageId }
    return chatWs.send(frame)
  }

  /** F-18：编辑自己的文本消息（结果通过 message.edited 广播返回）。 */
  function sendEdit(conversationId: string, messageId: string, content: string): boolean {
    const frame: MessageEditFrame = { type: 'message.edit', conversationId, messageId, content }
    return chatWs.send(frame)
  }

  function handleAck(frame: MessageAckFrame): void {
    const pending = findPending(frame.conversationId, frame.clientMsgId)
    if (!pending) return
    clearTimers(frame.clientMsgId)
    removePending(frame.conversationId, frame.clientMsgId)

    // ack 只含 messageId/seq/createdAt，本地用待发消息补全 Message 后并入缓存。
    const message: Message = {
      messageId: frame.messageId,
      conversationId: frame.conversationId,
      senderId: pending.senderId,
      clientMsgId: frame.clientMsgId,
      seq: frame.seq,
      msgType: pending.msgType,
      content: pending.content,
      createdAt: frame.createdAt,
      ...(frame.reply ? { reply: frame.reply } : {}),
    }
    merge(frame.conversationId, [message])
    useConversationsStore().applyMessage(frame.conversationId, message)
  }

  /**
   * 收到 message.new：并入对应会话（按 messageId 去重、按 seq 排序）并持久化；
   * 未打开的会话只更新会话摘要（conversations.applyMessage）；发现缺口时提示补拉。
   */
  /**
   * 新消息提醒（本机）：提示音按设置与焦点状态播放；系统通知仅窗口不可见时弹出。
   * 桌面壳（Electron preload 的 notify）优先，Web 版退回浏览器 Notification。
   * 不是自己的消息才提醒；任何异常静默，不影响消息入库/展示。
   */
  function notifyIncoming(message: Message): void {
    if (typeof document === 'undefined') return
    const selfUserId = useAuthStore().user?.userId ?? ''
    if (message.senderId === selfUserId) return

    const settings = useSettingsStore()
    const documentVisible = document.visibilityState === 'visible'
    if (
      !documentVisible &&
      settings.androidBackgroundNotificationsEnabled &&
      isAndroidBackgroundAvailable()
    ) {
      // Android 后台由原生前台服务负责系统通知；WebView 内不再播放声音或重复弹通知。
      return
    }
    if (!settings.soundEnabled && !settings.desktopNotificationsEnabled) return

    const conversationStore = useConversationsStore()
    const state = {
      isSelf: false,
      documentVisible,
      activeConversationId: conversationStore.activeConversationId,
      conversationId: message.conversationId,
      // F-10：免打扰会话不提示音、不弹系统通知（未读数不受影响）。
      muted: conversationStore.isMuted(message.conversationId),
      soundEnabled: settings.soundEnabled,
      desktopNotificationsEnabled: settings.desktopNotificationsEnabled,
    } as const

    if (shouldPlayIncomingSound(state)) playIncomingSound()
    if (!shouldShowDesktopNotification(state)) return

    const conversation = conversationStore.getById(message.conversationId)
    const title = conversation ? conversationTitleOf(conversation, selfUserId) : 'EriseChat 新消息'
    const body = previewOf(message)
    const notifier = getDesktopNotifier()
    if (notifier) {
      // F-01：带上会话 ID 与来源账号/服务器，点击后由渲染层校验归属再跳转；
      // 旧账号或旧服务器的目标会被丢弃，不会把渲染层带到错误的会话。
      // 必须传绝对 origin：开发态 / 同源部署的 resolveApiBase() 是相对 /api，
      // 主进程用单参数 new URL 解析会失败并丢弃整个点击目标。
      void notifier.notify({
        title,
        body,
        conversationId: message.conversationId,
        userId: selfUserId,
        serverUrl: resolveApiOrigin(),
      })
    } else {
      showWebNotification(title, body, message.conversationId)
    }
  }

  function conversationTitleOf(
    conversation: {
      type: string
      title: string
      members: readonly { userId: string; displayName: string }[]
    },
    selfUserId: string,
  ): string {
    if (conversation.type === 'group') return conversation.title.trim() || '群聊'
    const other = conversation.members.find((member) => member.userId !== selfUserId)
    return other?.displayName ?? '新消息'
  }

  function previewOf(message: Message): string {
    if (message.msgType === 'image') return '[图片]'
    if (message.msgType === 'voice') {
      return isVoiceContent(message.content)
        ? voicePreviewText(message.content.durationMs)
        : '[语音]'
    }
    if (message.msgType === 'file') {
      const content = message.content
      return typeof content === 'object' && content !== null && 'name' in content
        ? `[文件] ${String(content.name)}`
        : '[文件]'
    }
    if (typeof message.content === 'string') {
      const text = message.content.trim()
      return text.length > 80 ? `${text.slice(0, 80)}…` : text
    }
    return '新消息'
  }

  function handleIncoming(message: Message): GapCheckResult {
    // 自己的消息经多端/补拉回到本地时，清掉同 clientMsgId 的待发消息，避免重复展示。
    if (message.clientMsgId) {
      const pending = findPendingAnywhere(message.clientMsgId)
      if (pending) {
        clearTimers(pending.clientMsgId)
        removePending(pending.conversationId, pending.clientMsgId)
      }
    }

    const result = merge(message.conversationId, [message])
    useConversationsStore().applyMessage(message.conversationId, message)
    notifyIncoming(message)
    if (result.hasGap) {
      // 缺口立即补拉（协议 §6.3）：syncAll 会以本地游标重新拉取缺失区间。
      void syncAll()
    }
    return result
  }

  function handleFrame(frame: ServerFrame): void {
    switch (frame.type) {
      case 'message.ack':
        handleAck(frame)
        break
      case 'message.new':
        handleIncoming(frame.message)
        break
      case 'message.recalled':
        handleRecalled(frame)
        break
      case 'message.edited':
        handleEdited(frame)
        break
      case 'message.reactions':
        applyReactionSummary(frame.conversationId, frame.messageId, frame.reactions)
        break
      case 'error': {
        const match = /^msg-(.+)$/.exec(frame.reqId ?? '')
        if (!match) break
        const pending = findPendingAnywhere(match[1])
        if (pending) {
          clearTimers(pending.clientMsgId)
          pending.status = 'failed'
          pendingRevision.value += 1
          // F-03：服务端明确拒绝，也要把失败状态落库。
          persistPending(pending)
        }
        break
      }
      default:
        break
    }
  }

  /**
   * 认证成功（首次登录/断线重连/刷新后建连）：先强制刷新会话列表拿最新 lastSeq，
   * 再统一补拉增量——只在此一处触发，避免页面与 store 双拉（M3-00 B3）。
   */
  async function handleAuthed(): Promise<void> {
    try {
      await useConversationsStore().load(true)
    } catch (error) {
      console.warn('[messages] 刷新会话列表失败，使用本地缓存继续', error)
    }
    await syncAll()
  }

  /**
   * 订阅 WS 帧与连接状态（只注册一次）：
   * - message.ack / message.new 实时合并；
   * - authed（hello 后）触发会话列表刷新 + 全量补拉（首次进入/断线重连成功）；
   * - visibilitychange 回前台时补拉增量。
   */
  function subscribeRealtime(): () => void {
    if (realtimeReady) return disposeRealtime
    realtimeReady = true

    unsubscribers.push(chatWs.on('frame', handleFrame))
    unsubscribers.push(
      chatWs.on('statechange', (state: WsState) => {
        if (state !== 'authed') {
          // 断线：旧连接的 ack / 定时重试全部作废，待发保持可恢复状态，不标失败、不耗预算。
          clearAllTimers()
          return
        }
        void handleAuthed()
        // F-03：认证成功（刷新/重连）后按原 clientMsgId 补发，由 ack 或补拉对账。
        resumePending()
      }),
    )

    visibilityHandler = () => {
      if (document.visibilityState === 'visible' && chatWs.state === 'authed') void syncAll()
    }
    document.addEventListener('visibilitychange', visibilityHandler)

    // 订阅时若已认证（如从登录页跳转后路由已建连），立即刷新列表并补拉一次。
    if (chatWs.state === 'authed') {
      void handleAuthed()
      resumePending()
    }

    return disposeRealtime
  }

  function disposeRealtime(): void {
    for (const unsubscribe of unsubscribers.splice(0)) unsubscribe()
    if (visibilityHandler) {
      document.removeEventListener('visibilitychange', visibilityHandler)
      visibilityHandler = null
    }
    realtimeReady = false
  }

  /** 从 key→value 映射中移除某个 key（不存在时原样返回，避免无意义的新对象）。 */
  function omitKey<T>(record: Record<string, T>, key: string): Record<string, T> {
    if (!(key in record)) return record
    const next = { ...record }
    delete next[key]
    return next
  }

  /**
   * 会话已被移出/主动退出（conv.updated.removedConversationIds）：
   * 清理该会话的本地消息、补拉游标、待发消息与重试定时器，并删除 IndexedDB 缓存。
   * 事实源在服务端；若以后重新入群，会重新拉取，不会丢数据。
   */
  function removeConversation(conversationId: string): void {
    for (const pending of pendingByConversation.value[conversationId] ?? []) {
      clearTimers(pending.clientMsgId)
      // F-03：会话被移出/退出时，一并删除其待发消息缓存（避免下次恢复出已不存在的会话）。
      forgetPendingRecord(pending.clientMsgId)
    }

    byConversation.value = omitKey(byConversation.value, conversationId)
    lastSeqByConversation.value = omitKey(lastSeqByConversation.value, conversationId)
    stateSyncedAtByConversation.value = omitKey(stateSyncedAtByConversation.value, conversationId)
    pendingByConversation.value = omitKey(pendingByConversation.value, conversationId)
    hasMoreByConversation.value = omitKey(hasMoreByConversation.value, conversationId)
    loadedConversations.value = omitKey(loadedConversations.value, conversationId)
    loadingInitialByConversation.value = omitKey(loadingInitialByConversation.value, conversationId)
    loadingMoreByConversation.value = omitKey(loadingMoreByConversation.value, conversationId)

    void (async () => {
      try {
        const cached = await getMessagesByConversation(conversationId)
        await Promise.all(
          cached.map((message) => deleteRecord(STORE_MESSAGES, message.messageId)),
        )
        const lastSeqMap = (await getMeta<Record<string, number>>(META_LAST_SEQ_KEY)) ?? {}
        if (conversationId in lastSeqMap) {
          await setMeta(META_LAST_SEQ_KEY, omitKey(lastSeqMap, conversationId))
        }
        const stateMap = (await getMeta<Record<string, number>>(META_SYNC_STATE_KEY)) ?? {}
        if (conversationId in stateMap) {
          await setMeta(META_SYNC_STATE_KEY, omitKey(stateMap, conversationId))
        }
        await deleteRecord(STORE_CONVERSATIONS, conversationId)
      } catch (error) {
        console.warn('[messages] 清理被移除会话缓存失败', error)
      }
    })()
  }

  // 会话被移出/退出时，由 conversations store 回调清理本 store 的消息缓存
  // （反过来让 conversations import messages 会形成循环依赖，故用回调注册）。
  useConversationsStore().onConversationRemoved(removeConversation)

  function clear(): void {
    clearAllTimers()
    disposeRealtime()
    // F-03：登出/切账号清空待发消息缓存，避免把上一个账号的发送中消息带到下一个账号。
    void clearPending()
    byConversation.value = {}
    lastSeqByConversation.value = {}
    stateSyncedAtByConversation.value = {}
    pendingByConversation.value = {}
    hasMoreByConversation.value = {}
    loadedConversations.value = {}
    loadingInitialByConversation.value = {}
    loadingMoreByConversation.value = {}
    syncing.value = false
    lastSyncedAt.value = 0
    syncInFlight = false
    syncQueued = false
    cacheRestored = false
  }

  return {
    byConversation,
    lastSeqByConversation,
    pendingByConversation,
    syncing,
    lastSyncedAt,
    messagesOf,
    pendingOf,
    pendingRevision,
    lastMessagePatch,
    hasMoreOf,
    isLoaded,
    isLoadingMore,
    lastSeqOf,
    setLastSeq,
    advanceLastSeq,
    merge,
    restoreFromCache,
    loadInitial,
    loadMore,
    loadContextAround,
    syncAll,
    sendText,
    sendFileMessage,
    sendVoiceMessage,
    sendRecall,
    sendEdit,
    refreshRecent,
    applyReactionSummary,
    reactionVersionOf,
    retrySend,
    removeConversation,
    subscribeRealtime,
    clear,
  }
})
