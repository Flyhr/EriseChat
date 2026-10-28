// F-26：消息转发（仅文本）的纯逻辑。
//
// 转发 = 把原文本作为一条新消息发送到目标会话（新 clientMsgId、不 chain 原消息、不带 replyTo）。
// 图片/文件因服务端按 fileId 归属校验，跨会话复用需另定方案，本批不提供转发入口。
import type { Conversation, Message } from '../types/frames'

/** 是否可转发：仅文本消息、未撤回、非 system。 */
export function canForward(message: Message | null | undefined): boolean {
  if (!message) return false
  if (message.msgType !== 'text') return false
  if (typeof message.deletedAt === 'number') return false
  return typeof message.content === 'string' && message.content.length > 0
}

/** 转发时的文本内容（不可转发返回空串，调用方据此兜底）。 */
export function forwardTextOf(message: Message | null | undefined): string {
  return canForward(message) && typeof message?.content === 'string' ? message.content : ''
}

function conversationActivityOf(conversation: Conversation): number {
  const last = conversation.lastMessage?.createdAt
  if (typeof last === 'number') return last
  return conversation.createdAt
}

/**
 * 转发目标会话：排除当前会话；按置顶优先、最近活跃倒序（同活跃时间按会话 ID 升序稳定）；
 * 关键词匹配会话标题（大小写不敏感）。
 */
export function filterForwardTargets(
  conversations: readonly Conversation[],
  currentConversationId: string,
  query: string,
): Conversation[] {
  const keyword = query.trim().toLowerCase()
  return conversations
    .filter((conversation) => conversation.conversationId !== currentConversationId)
    .filter((conversation) => keyword.length === 0 || conversation.title.toLowerCase().includes(keyword))
    .sort((a, b) => {
      const pinA = a.pinned ? 1 : 0
      const pinB = b.pinned ? 1 : 0
      if (pinA !== pinB) return pinB - pinA
      const activityDiff = conversationActivityOf(b) - conversationActivityOf(a)
      if (activityDiff !== 0) return activityDiff
      return a.conversationId < b.conversationId ? -1 : a.conversationId > b.conversationId ? 1 : 0
    })
}
