// 已读回执纯逻辑（M3-09）：
// 1) 滚动位置驱动的已读推进判定（页面可见 + 停留在底部 + 历史已加载 + 有新进度）；
// 2) 单聊「已读/未读」状态计算（只展示自己发出的最新一条）。
// 抽成纯函数便于单测（M3-09 提交 4），不依赖 Vue/DOM。
import type { Message } from '../types/frames'

export interface ReadAdvanceContext {
  /** 当前页面是否可见（document.visibilityState === 'visible'）。 */
  visible: boolean
  /** 消息列表是否停留在底部（用户已看到最新消息）。 */
  atBottom: boolean
  /** 当前会话历史是否已加载（避免首屏加载前误推进）。 */
  loaded: boolean
  /** 当前打开的会话 ID；为空表示未进入任何会话。 */
  conversationId: string | null
  /** 会话最新 seq（推进目标）。 */
  lastSeq: number
  /** 已记录/已上报的最大已读 seq（避免重复上报）。 */
  reportedSeq: number
}

/** 是否应推进已读游标：可见、贴底、已加载且有新进度时返回 true。 */
export function shouldAdvanceRead(context: ReadAdvanceContext): boolean {
  if (!context.visible || !context.atBottom || !context.loaded) return false
  if (context.conversationId === null || context.conversationId.length === 0) return false
  if (!Number.isFinite(context.lastSeq) || context.lastSeq <= 0) return false
  return context.lastSeq > context.reportedSeq
}

/** 单聊回执状态：对端已读游标 >= 消息 seq 视为已读。 */
export type ReadReceiptState = 'read' | 'unread'

export function readStateOf(
  peerReadSeq: number | null | undefined,
  seq: number,
): ReadReceiptState {
  const peer = typeof peerReadSeq === 'number' && Number.isFinite(peerReadSeq) ? peerReadSeq : 0
  const target = Number.isFinite(seq) ? seq : 0
  return peer >= target ? 'read' : 'unread'
}

/** 自己发出的最新一条消息（单聊只在该条展示已读/未读）；无自身消息返回 undefined。 */
export function lastOwnMessageOf(
  messages: readonly Message[],
  selfUserId: string | undefined,
): Message | undefined {
  if (!selfUserId) return undefined
  for (let index = messages.length - 1; index >= 0; index -= 1) {
    const message = messages[index]
    if (message?.senderId === selfUserId) return message
  }
  return undefined
}
