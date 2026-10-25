import { ref } from 'vue'
import { defineStore } from 'pinia'
import type { Message, MessageAckFrame, MessageSendFrame } from '../types/frames'
import { type GapCheckResult, detectGap, mergeMessages } from '../core/sync'
import { messagesApi } from '../api/messages'
import { chatWs } from '../core/ws'
import { useConversationsStore } from './conversations'

/** 历史分页大小（服务端默认 50、上限 200；AGENTS.md §5.5 每会话每批 ≤200）。 */
const PAGE_SIZE = 50
/** 首次发送后等待 ack 的时长；超时进入重试（2s/5s/10s，同一 clientMsgId 幂等）。 */
const ACK_TIMEOUT_MS = 8000
const RETRY_DELAYS_MS = [2000, 5000, 10000] as const

export type PendingStatus = 'sending' | 'failed'

/** 本地乐观消息：尚未收到 message.ack，只用于 UI 展示，不属于服务端事实源。 */
export interface PendingMessage {
  clientMsgId: string
  conversationId: string
  senderId: string
  msgType: 'text'
  content: string
  createdAt: number
  status: PendingStatus
  /** 已重试次数（0 = 首次发送，最多 RETRY_DELAYS_MS.length 次重试）。 */
  retries: number
}

/** message.send 的 reqId（error 帧据此把失败落到具体待发消息）。 */
function reqIdOf(clientMsgId: string): string {
  return `msg-${clientMsgId}`
}

// 消息缓存、历史分页与发送状态机。事实源在服务端（AGENTS.md §2.8），
// 本地按会话缓存 Message[]（已落库）与 PendingMessage[]（乐观发送中）；
// 收发统一走 core/ws.ts 的 chatWs，不直接 new WebSocket / fetch（project-overview.md §10）。
export const useMessagesStore = defineStore('messages', () => {
  const byConversation = ref<Record<string, Message[]>>({})
  const lastSeqByConversation = ref<Record<string, number>>({})
  const pendingByConversation = ref<Record<string, PendingMessage[]>>({})
  const hasMoreByConversation = ref<Record<string, boolean>>({})
  const loadedConversations = ref<Record<string, boolean>>({})
  const loadingInitialByConversation = ref<Record<string, boolean>>({})
  const loadingMoreByConversation = ref<Record<string, boolean>>({})

  // 定时器不进响应式状态；按 clientMsgId 管理，避免 UI 重新渲染抖动。
  const ackTimers = new Map<string, ReturnType<typeof setTimeout>>()
  const retryTimers = new Map<string, ReturnType<typeof setTimeout>>()
  let unsubscribeFrame: (() => void) | null = null

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

  /** 合并服务端事实消息：去重 + 按 seq 升序；返回缺口检测结果（补拉在 M2-06 接入）。 */
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

  /**
   * 首次进入会话：REST 拉最新一页历史（limit=50），与本地缓存合并。
   * 幂等：已加载过或正在加载时直接返回。
   */
  async function loadInitial(conversationId: string): Promise<void> {
    if (isLoaded(conversationId) || loadingInitialByConversation.value[conversationId]) return
    loadingInitialByConversation.value = {
      ...loadingInitialByConversation.value,
      [conversationId]: true,
    }
    try {
      const page = await messagesApi.history(conversationId, undefined, PAGE_SIZE)
      merge(conversationId, page.messages)
      hasMoreByConversation.value = {
        ...hasMoreByConversation.value,
        [conversationId]: page.hasMore,
      }
      loadedConversations.value = { ...loadedConversations.value, [conversationId]: true }
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
      msgType: 'text',
      content: pending.content,
    }
    if (!chatWs.send(frame)) {
      // 断线时 chatWs.send 返回 false；ack 超时后按重试间隔继续尝试，最终失败可手动重发。
      console.warn('[messages] WebSocket 未连接，消息将在重试窗口内继续尝试')
    }
    scheduleAckTimeout(pending)
  }

  function handleAckTimeout(conversationId: string, clientMsgId: string): void {
    const pending = findPending(conversationId, clientMsgId)
    if (!pending || pending.status !== 'sending') return
    if (pending.retries >= RETRY_DELAYS_MS.length) {
      pending.status = 'failed'
      return
    }
    const delay = RETRY_DELAYS_MS[pending.retries]
    pending.retries += 1
    const timer = setTimeout(() => {
      retryTimers.delete(clientMsgId)
      transmit(pending)
    }, delay)
    retryTimers.set(clientMsgId, timer)
  }

  /** 乐观发送文本消息：本地立即展示（sending），ack 到达后转为服务端事实消息。 */
  function sendText(conversationId: string, text: string, senderId: string): void {
    const content = text.trim()
    if (!content) return
    const pending: PendingMessage = {
      clientMsgId: crypto.randomUUID(),
      conversationId,
      senderId,
      msgType: 'text',
      content,
      createdAt: Date.now(),
      status: 'sending',
      retries: 0,
    }
    pendingByConversation.value = {
      ...pendingByConversation.value,
      [conversationId]: [...pendingOf(conversationId), pending],
    }
    transmit(pending)
  }

  /** 重发失败消息：复用同一 clientMsgId（服务端幂等，不会重复落库）。 */
  function retrySend(conversationId: string, clientMsgId: string): void {
    const pending = findPending(conversationId, clientMsgId)
    if (!pending || pending.status !== 'failed') return
    pending.status = 'sending'
    pending.retries = 0
    transmit(pending)
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
      msgType: 'text',
      content: pending.content,
      createdAt: frame.createdAt,
    }
    merge(frame.conversationId, [message])
    useConversationsStore().applyMessage(frame.conversationId, message)
  }

  /**
   * 收到 message.new：并入对应会话（按 messageId 去重、按 seq 排序）；
   * 未打开的会话只更新会话摘要（conversations.applyMessage）。
   */
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
    if (result.hasGap) {
      // 缺口补拉统一在 M2-06（sync.request）接入；此处仅提示，不丢消息。
      console.warn(`[messages] 会话 ${message.conversationId} 出现 seq 缺口`)
    }
    return result
  }

  /** 订阅 WS 帧：message.ack 落库确认、message.new 实时合并、error 标记发送失败（只注册一次）。 */
  function subscribeRealtime(): () => void {
    if (unsubscribeFrame !== null) return unsubscribeFrame
    unsubscribeFrame = chatWs.on('frame', (frame) => {
      switch (frame.type) {
        case 'message.ack':
          handleAck(frame)
          break
        case 'message.new':
          handleIncoming(frame.message)
          break
        case 'error': {
          const match = /^msg-(.+)$/.exec(frame.reqId ?? '')
          if (!match) break
          const pending = findPendingAnywhere(match[1])
          if (pending) {
            clearTimers(pending.clientMsgId)
            pending.status = 'failed'
          }
          break
        }
        default:
          break
      }
    })
    return unsubscribeFrame
  }

  function clear(): void {
    const tracked = new Set([...ackTimers.keys(), ...retryTimers.keys()])
    for (const clientMsgId of tracked) clearTimers(clientMsgId)
    byConversation.value = {}
    lastSeqByConversation.value = {}
    pendingByConversation.value = {}
    hasMoreByConversation.value = {}
    loadedConversations.value = {}
    loadingInitialByConversation.value = {}
    loadingMoreByConversation.value = {}
    if (unsubscribeFrame) {
      unsubscribeFrame()
      unsubscribeFrame = null
    }
  }

  return {
    byConversation,
    lastSeqByConversation,
    pendingByConversation,
    messagesOf,
    pendingOf,
    hasMoreOf,
    isLoaded,
    isLoadingMore,
    lastSeqOf,
    setLastSeq,
    advanceLastSeq,
    merge,
    loadInitial,
    loadMore,
    sendText,
    retrySend,
    subscribeRealtime,
    clear,
  }
})
