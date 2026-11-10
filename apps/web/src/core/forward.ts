// F-26/F-40：消息转发的纯逻辑。
//
// 转发 = 在目标会话里按原消息内容生成一条普通消息（新 clientMsgId、不 chain 原消息、不带 replyTo）。
// 文本/图片/文件/语音统一走服务端 `/api/messages/forward`：服务端校验转发者仍可访问原消息与目标会话，
// 复用原 `files.id` 建立新消息引用，客户端不重复下载/上传附件。
import type { Conversation, FileContent, Message, VoiceContent } from '../types/frames'

const FORWARDABLE_TYPES = new Set(['text', 'image', 'file', 'voice'])

function fileContentOf(message: Message): FileContent | null {
  const content = message.content
  if (typeof content !== 'object' || content === null) return null
  if (!('name' in content) || !('fileId' in content)) return null
  return content as FileContent
}

function voiceContentOf(message: Message): VoiceContent | null {
  const content = message.content
  if (typeof content !== 'object' || content === null) return null
  if (!('durationMs' in content) || !('fileId' in content)) return null
  return content as VoiceContent
}

/** 是否可转发：text/image/file/voice、未撤回、非 system、附件引用完整。 */
export function canForward(message: Message | null | undefined): boolean {
  if (!message) return false
  if (!FORWARDABLE_TYPES.has(message.msgType)) return false
  if (typeof message.deletedAt === 'number') return false
  if (!message.messageId) return false
  if (message.msgType === 'text') {
    return typeof message.content === 'string' && message.content.length > 0
  }
  if (message.msgType === 'voice') {
    const voice = voiceContentOf(message)
    return Boolean(voice?.fileId)
  }
  const file = fileContentOf(message)
  return Boolean(file?.fileId && file?.name)
}

/**
 * 转发列表/预览的一行摘要：文本截 60 字，附件只显示类型（不展示文件名以外内容）。
 * 不可转发的消息返回空串。
 */
export function forwardPreviewOf(message: Message | null | undefined): string {
  if (!canForward(message) || !message) return ''
  if (message.msgType === 'text') {
    const text = typeof message.content === 'string' ? message.content : ''
    return text.length > 60 ? `${text.slice(0, 60)}…` : text
  }
  if (message.msgType === 'voice') return '[语音]'
  if (message.msgType === 'image') return '[图片]'
  const file = fileContentOf(message)
  return file ? `[文件] ${file.name}` : ''
}

/** 会话活跃时间：优先最后一条消息时间，回退创建时间。 */
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
