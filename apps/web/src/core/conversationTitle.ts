import type { Conversation } from '../types/frames'
import { conversationLabel } from './groupChat'

// 会话展示名（P2 清理：原先在 4 处各写一遍「群聊取标签 / 单聊取对方昵称」）。
// 纯函数，无状态，便于单测与复用。

/** 单聊对方成员（群聊或无会话时返回 undefined）。 */
export function otherMemberOf(
  conversation: Pick<Conversation, 'type' | 'members'> | null | undefined,
  selfUserId: string | null | undefined,
): Conversation['members'][number] | undefined {
  if (!conversation || conversation.type === 'group' || !selfUserId) return undefined
  return conversation.members.find((member) => member.userId !== selfUserId)
}

/**
 * 会话展示名：
 * - 群聊：默认「群名（人数）」（groupStyle='label'）；'plain' 只取群名（用于聊天头部/通知标题）；
 * - 单聊：本人设置的好友备注 → 对方昵称 → 会话标题 → fallback。
 * 备注只是本人的标记（不是账号资料），所以由调用方通过 remarkOf 注入，纯函数不碰 store。
 */
export function conversationTitle(
  conversation: Pick<Conversation, 'type' | 'title' | 'members'> | null | undefined,
  selfUserId: string | null | undefined,
  options: {
    groupStyle?: 'label' | 'plain'
    fallback?: string
    /** 好友备注取值（userId → 备注）；返回空串表示未设置。 */
    remarkOf?: (userId: string) => string | undefined
  } = {},
): string {
  const { groupStyle = 'label', fallback = '会话', remarkOf } = options
  if (!conversation) return fallback
  if (conversation.type === 'group') {
    return groupStyle === 'plain'
      ? conversation.title.trim() || '群聊'
      : conversationLabel(conversation)
  }
  const peer = otherMemberOf(conversation, selfUserId)
  const remark = peer && remarkOf ? remarkOf(peer.userId)?.trim() : ''
  return remark || peer?.displayName || conversation.title.trim() || fallback
}
