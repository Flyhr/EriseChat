// F-03 待发消息队列的纯逻辑（无网络、无 IndexedDB 依赖，便于单测）。
// 记录结构见 core/db.ts 的 PendingRecord；这里只做序列化校验、分组与「服务端已落库」的对账。

import type { FileContent, Message, MessageSendType } from '../types/frames'
import type { PendingRecord } from './db'

/** 与 stores/messages.ts 的 PendingMessage 结构兼容的最小输入类型（避免 store ↔ core 反向依赖）。 */
export interface PendingLike {
  clientMsgId: string
  conversationId: string
  senderId: string
  msgType: MessageSendType
  content: string | FileContent
  createdAt: number
  status: 'sending' | 'failed'
  retries: number
}

const MESSAGE_SEND_TYPES: readonly MessageSendType[] = ['text', 'image', 'file']

function isNonEmptyString(value: unknown): value is string {
  return typeof value === 'string' && value.length > 0
}

function isFileContent(value: unknown): value is FileContent {
  if (typeof value !== 'object' || value === null || Array.isArray(value)) return false
  const record = value as Record<string, unknown>
  return (
    isNonEmptyString(record.fileId) &&
    isNonEmptyString(record.name) &&
    isNonEmptyString(record.mime) &&
    typeof record.size === 'number' &&
    Number.isFinite(record.size) &&
    record.size > 0
  )
}

/** PendingMessage → PendingRecord（浅拷贝文件内容，避免把响应式对象直接写进 IndexedDB）。 */
export function toPendingRecord(pending: PendingLike): PendingRecord {
  return {
    clientMsgId: pending.clientMsgId,
    conversationId: pending.conversationId,
    senderId: pending.senderId,
    msgType: pending.msgType,
    content:
      typeof pending.content === 'string' ? pending.content : { ...pending.content },
    createdAt: pending.createdAt,
    status: pending.status,
    retries: pending.retries,
  }
}

/**
 * PendingRecord → PendingRecord（校验后的规范值）；非法记录返回 null。
 * IndexedDB 可能残留旧版本/被外部改写的脏数据，恢复时必须先校验，避免把坏对象塞进发送队列。
 */
export function fromPendingRecord(record: unknown): PendingRecord | null {
  if (typeof record !== 'object' || record === null || Array.isArray(record)) return null
  const value = record as Record<string, unknown>
  if (!isNonEmptyString(value.clientMsgId)) return null
  if (!isNonEmptyString(value.conversationId)) return null
  if (!isNonEmptyString(value.senderId)) return null
  if (!MESSAGE_SEND_TYPES.includes(value.msgType as MessageSendType)) return null
  const msgType = value.msgType as MessageSendType
  const content = value.content
  if (msgType === 'text') {
    if (!isNonEmptyString(content)) return null
  } else if (!isFileContent(content)) {
    return null
  }
  if (typeof value.createdAt !== 'number' || !Number.isFinite(value.createdAt) || value.createdAt <= 0) {
    return null
  }
  if (value.status !== 'sending' && value.status !== 'failed') return null
  if (typeof value.retries !== 'number' || !Number.isInteger(value.retries) || value.retries < 0) {
    return null
  }
  return {
    clientMsgId: value.clientMsgId,
    conversationId: value.conversationId,
    senderId: value.senderId,
    msgType,
    content:
      msgType === 'text' ? (content as string) : { ...(content as FileContent) },
    createdAt: value.createdAt,
    status: value.status,
    retries: value.retries,
  }
}

/** 按会话分组（恢复时先过滤掉会话已不存在的记录，再按会话塞回 pendingByConversation）。 */
export function groupPendingByConversation(
  records: readonly PendingRecord[],
): Record<string, PendingRecord[]> {
  const grouped: Record<string, PendingRecord[]> = {}
  for (const record of records) {
    const list = grouped[record.conversationId] ?? (grouped[record.conversationId] = [])
    list.push(record)
  }
  return grouped
}

/**
 * 「服务端已落库但 ack 丢失」对账：返回 incoming 中已带 clientMsgId 的待发消息 id 集合。
 * 调用方应清理这些 pending（否则刷新后补拉/历史会与本地发送中消息重复展示）。
 */
export function reconcileClientMsgIds(
  incomingMessages: readonly Message[],
  pending: readonly PendingRecord[],
): Set<string> {
  const pendingIds = new Set<string>()
  for (const record of pending) pendingIds.add(record.clientMsgId)
  const matched = new Set<string>()
  if (pendingIds.size === 0) return matched
  for (const message of incomingMessages) {
    const clientMsgId = message.clientMsgId
    if (clientMsgId && pendingIds.has(clientMsgId)) matched.add(clientMsgId)
  }
  return matched
}
