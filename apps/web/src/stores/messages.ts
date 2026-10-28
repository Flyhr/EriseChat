import { ref } from 'vue'
import { defineStore } from 'pinia'
import type {
  FileContent,
  Message,
  MessageAckFrame,
  MessageEditFrame,
  MessageEditedFrame,
  MessageRecallFrame,
  MessageRecalledFrame,
  MessageSendFrame,
  MessageSendType,
  ServerFrame,
  SyncRequestFrame,
  SyncResponseFrame,
} from '../types/frames'
import { type GapCheckResult, buildSyncCursors, detectGap, mergeMessages } from '../core/sync'
import { messagesApi } from '../api/messages'
import { chatWs, type WsState } from '../core/ws'
import { MAX_TEXT_LENGTH, config } from '../core/config'
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
import { useConversationsStore } from './conversations'
import { useAuthStore } from './auth'
import { useSettingsStore } from './settings'

/** 历史分页大小（服务端默认 50、上限 200；AGENTS.md §5.5 每会话每批 ≤200）。 */
const PAGE_SIZE = 50
// 文本上限常量下沉到 core/config（F-04）；此处保留 re-export 兼容既有 import。
export { MAX_TEXT_LENGTH }
/** 首次发送后等待 ack 的时长；超时进入重试（2s/5s/10s，同一 clientMsgId 幂等）。 */
const ACK_TIMEOUT_MS = 8000
const RETRY_DELAYS_MS = [2000, 5000, 10000] as const
/** 本会话已知最大 seq 的 IndexedDB 持久化 key（补拉游标）。 */
const META_LAST_SEQ_KEY = 'lastSeqByConversation'
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
  /** text / image / file（system 仅服务端生成）。 */
  msgType: MessageSendType
  /** 文本为字符串；图片/文件为 FileContent。 */
  content: string | FileContent
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
  const pendingByConversation = ref<Record<string, PendingMessage[]>>({})
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
    patch: Partial<Pick<Message, 'content' | 'deletedAt' | 'editedAt'>>,
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

  function handleRecalled(frame: MessageRecalledFrame): void {
    // 撤回后内容为空文本、不再带文件信息；deletedAt 非空即视为已撤回。
    patchStoredMessage(frame.conversationId, frame.messageId, {
      content: '',
      deletedAt: frame.deletedAt,
    })
  }

  function handleEdited(frame: MessageEditedFrame): void {
    patchStoredMessage(frame.conversationId, frame.messageId, {
      content: frame.content,
      editedAt: frame.editedAt,
    })
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
   * F-03：把恢复出来、仍处于 sending 的待发消息重新发出（retries 归零，复用原 clientMsgId）。
   * 仅在 WS 已认证时调用；已有 ack/重试定时器的消息跳过，避免重复排定时器。
   */
  function resumePending(): void {
    if (chatWs.state !== 'authed') return
    for (const list of Object.values(pendingByConversation.value)) {
      for (const pending of list) {
        if (pending.status !== 'sending') continue
        if (ackTimers.has(pending.clientMsgId) || retryTimers.has(pending.clientMsgId)) continue
        pending.retries = 0
        persistPending(pending)
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
          isSameContent(fresh.content, message.content)
        ) {
          return message
        }
        const updated: Message = { ...message, content: fresh.content }
        if (fresh.deletedAt === undefined) delete updated.deletedAt
        else updated.deletedAt = fresh.deletedAt
        if (fresh.editedAt === undefined) delete updated.editedAt
        else updated.editedAt = fresh.editedAt
        if (fresh.reply) updated.reply = fresh.reply
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
        const cursors = buildSyncCursors(pending, 100)
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
          const messages = batch.messages ?? []
          if (messages.length === 0) continue
          const cursor = sentSeq.get(batch.conversationId) ?? lastSeqOf(batch.conversationId)
          const gap = detectGap(cursor, messages)
          merge(batch.conversationId, messages)
          conversations.applyMessage(batch.conversationId, messages[messages.length - 1])

          if (gap.hasGap) {
            const retried = gapRetries.get(batch.conversationId) ?? 0
            if (retried < SYNC_GAP_RETRIES) {
              gapRetries.set(batch.conversationId, retried + 1)
              next.set(batch.conversationId, cursor)
            }
            continue
          }

          if (messages.length >= config.syncBatchSize) {
            next.set(batch.conversationId, lastSeqOf(batch.conversationId))
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

  function scheduleAckTimeout(pending: PendingMessage): void {
    clearTimers(pending.clientMsgId)
    const timer = setTimeout(() => {
      ackTimers.delete(pending.clientMsgId)
      handleAckTimeout(pending.conversationId, pending.clientMsgId)
    }, ACK_TIMEOUT_MS)
    ackTimers.set(pending.clientMsgId, timer)
  }

  /** 发送一帧 message.send（带 reqId；幂等键 clientMsgId 由服务端去重）。 */
  function transmit(pending: PendingMessage): void {
    if (pending.status !== 'sending') return
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
      scheduleAckTimeout(pending)
      return
    }
    // 未连接：不启动 8 秒 ack 计时，直接进入 2s/5s/10s 重试序列，避免几十秒后才标失败（M3-00 N8）。
    console.warn('[messages] WebSocket 未连接，消息进入重试序列')
    scheduleRetry(pending)
  }

  function handleAckTimeout(conversationId: string, clientMsgId: string): void {
    const pending = findPending(conversationId, clientMsgId)
    if (!pending || pending.status !== 'sending') return
    scheduleRetry(pending)
  }

  /** 按 2s/5s/10s 间隔重发同一 clientMsgId；3 次重试仍失败则标记 failed（可手动重发）。 */
  function scheduleRetry(pending: PendingMessage): void {
    clearTimers(pending.clientMsgId)
    if (pending.retries >= RETRY_DELAYS_MS.length) {
      pending.status = 'failed'
      // F-03：落库「失败」状态，刷新后仍显示可重发。
      persistPending(pending)
      return
    }
    const delay = RETRY_DELAYS_MS[pending.retries]
    pending.retries += 1
    const timer = setTimeout(() => {
      retryTimers.delete(pending.clientMsgId)
      transmit(pending)
    }, delay)
    retryTimers.set(pending.clientMsgId, timer)
  }

  /** 乐观发送通用入口（text / image / file）：本地立即展示（sending），ack 到达后转为服务端事实消息。 */
  function sendContent(
    conversationId: string,
    msgType: MessageSendType,
    content: string | FileContent,
    senderId: string,
    replyTo?: string,
    replyPreview?: ReplyPreviewLike,
  ): boolean {
    if (!conversationId || !senderId) return false
    const pending: PendingMessage = {
      clientMsgId: crypto.randomUUID(),
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
    return sendContent(conversationId, msgType, { ...content }, senderId, replyTo, replyPreview)
  }

  /** 重发失败消息：复用同一 clientMsgId（服务端幂等，不会重复落库）。 */
  function retrySend(conversationId: string, clientMsgId: string): void {
    const pending = findPending(conversationId, clientMsgId)
    if (!pending || pending.status !== 'failed') return
    pending.status = 'sending'
    pending.retries = 0
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
    if (!settings.soundEnabled && !settings.desktopNotificationsEnabled) return

    const conversationStore = useConversationsStore()
    const state = {
      isSelf: false,
      documentVisible: document.visibilityState === 'visible',
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
    const title = conversation ? conversationTitleOf(conversation, selfUserId) : 'EiseChat 新消息'
    const body = previewOf(message)
    const notifier = getDesktopNotifier()
    if (notifier) {
      // F-01：带上会话 ID，点击系统通知时由 App.vue 跳转到对应会话。
      void notifier.notify({ title, body, conversationId: message.conversationId })
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
      case 'error': {
        const match = /^msg-(.+)$/.exec(frame.reqId ?? '')
        if (!match) break
        const pending = findPendingAnywhere(match[1])
        if (pending) {
          clearTimers(pending.clientMsgId)
          pending.status = 'failed'
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
        if (state === 'authed') {
          void handleAuthed()
          // F-03：认证成功（刷新/重连）后恢复的待发消息才重发，避免提前耗光重试次数。
          resumePending()
        }
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
    const tracked = new Set([...ackTimers.keys(), ...retryTimers.keys()])
    for (const clientMsgId of tracked) clearTimers(clientMsgId)
    disposeRealtime()
    // F-03：登出/切账号清空待发消息缓存，避免把上一个账号的发送中消息带到下一个账号。
    void clearPending()
    byConversation.value = {}
    lastSeqByConversation.value = {}
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
    syncAll,
    sendText,
    sendFileMessage,
    sendRecall,
    sendEdit,
    refreshRecent,
    retrySend,
    removeConversation,
    subscribeRealtime,
    clear,
  }
})
