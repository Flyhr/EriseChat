// 会话展示排序（F-10）：置顶优先，组内按最近消息时间倒序（无消息按创建时间），同时间按会话 ID 稳定排序。
// 服务端 conv.list 仍是最近消息倒序（不感知置顶），排序只在前端做，纯函数便于单测。
import type { Conversation } from '../types/frames'

/** 是否置顶（服务端默认不下发该字段，视为未置顶）。 */
export function isConversationPinned(
  conversation: Pick<Conversation, 'pinned'>,
): boolean {
  return conversation.pinned === true
}

/** 最近活动时间：有消息用消息时间，否则用创建时间；非法值按 0 兜底。 */
export function lastActivityAt(
  conversation: Pick<Conversation, 'lastMessage' | 'createdAt'>,
): number {
  const value = conversation.lastMessage?.createdAt ?? conversation.createdAt
  return typeof value === 'number' && Number.isFinite(value) ? value : 0
}

/**
 * 会话列表展示顺序：置顶组在前，组内最近活动时间倒序；时间相同按 conversationId 升序，保证顺序稳定。
 * 不修改入参。
 */
export function sortConversations(conversations: readonly Conversation[]): Conversation[] {
  return [...conversations].sort((a, b) => {
    const pinnedA = isConversationPinned(a) ? 1 : 0
    const pinnedB = isConversationPinned(b) ? 1 : 0
    if (pinnedA !== pinnedB) return pinnedB - pinnedA

    const diff = lastActivityAt(b) - lastActivityAt(a)
    if (diff !== 0) return diff

    if (a.conversationId === b.conversationId) return 0
    return a.conversationId < b.conversationId ? -1 : 1
  })
}
