// 补拉同步的纯逻辑：去重、排序、缺口检测、未读计算（无副作用、无网络/存储依赖）。
// 协议约定见 protocol/protocol.md §4.4 / §4.6；硬要求见 AGENTS.md §5.4/§5.5/§5.6。

import type { Message, SyncCursor } from '../types/frames'
import { config } from './config'

/** 按 messageId 去重（同一 id 以后出现者为准）并按 seq 升序排序。 */
export function dedupeAndSort(messages: readonly Message[]): Message[] {
  const byId = new Map<string, Message>()
  for (const message of messages) byId.set(message.messageId, message)
  return [...byId.values()].sort((a, b) => a.seq - b.seq)
}

/** 合并本地缓存与收到的消息（实时 message.new / 补拉 sync.response），去重后按 seq 排序。 */
export function mergeMessages(existing: readonly Message[], incoming: readonly Message[]): Message[] {
  return dedupeAndSort([...existing, ...incoming])
}

export interface GapCheckResult {
  /** 按 seq 连续推进后可确认的最大序号 */
  lastSeq: number
  /** 是否存在缺口（需要立即发 sync.request） */
  hasGap: boolean
  /** 缺口起始序号（hasGap=true 时有效） */
  missingFrom?: number
}

/**
 * 检测 seq 缺口：从 lastSeq 起按升序扫描，遇到 seq > cursor+1 即视为缺口。
 * 存在缺口时调用方应暂存已收到的消息、立即用 lastSeq 发起 sync.request（protocol.md §6.3）。
 */
export function detectGap(lastSeq: number, messages: readonly Message[]): GapCheckResult {
  const sorted = dedupeAndSort(messages)
  let cursor = Math.max(0, lastSeq)

  for (const message of sorted) {
    if (message.seq <= cursor) continue
    if (message.seq > cursor + 1) {
      return { lastSeq: cursor, hasGap: true, missingFrom: cursor + 1 }
    }
    cursor = message.seq
  }

  return { lastSeq: cursor, hasGap: false }
}

/**
 * 生成下一批 sync.request 的 cursors（每帧 1~100 项，protocol.md §4.4）。
 * 传入 Map 的迭代顺序即请求顺序；lastSeq 非法的会话会被跳过。
 */
export function buildSyncCursors(
  lastSeqByConversation: ReadonlyMap<string, number>,
  limit = 100,
): SyncCursor[] {
  const cursors: SyncCursor[] = []
  const max = Math.min(Math.max(limit, 1), 100)

  for (const [conversationId, lastSeq] of lastSeqByConversation) {
    if (cursors.length >= max) break
    if (!conversationId || !Number.isInteger(lastSeq) || lastSeq < 0) continue
    cursors.push({ conversationId, lastSeq })
  }

  return cursors
}

/** 未读数 = 会话最新 seq − 我的已读游标；不单独下发 unread（AGENTS.md §5.6）。 */
export function unreadCount(lastSeq: number, lastReadSeq: number): number {
  return Math.max(0, lastSeq - Math.max(0, lastReadSeq))
}

/** 单批每个会话的最大条数（协议 §4.4 固定 200 条）。 */
export function syncBatchLimit(): number {
  return config.syncBatchSize
}
