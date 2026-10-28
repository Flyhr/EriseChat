// F-09 回复引用的纯展示逻辑（无 DOM、无 store 依赖，便于单测）。
// 服务端 Reply 摘要把 text 截断到 120 字符；这里再做前端 60 字符折叠，避免引用条撑高气泡。
// 详见 protocol/protocol.md §4.3「Reply 对象」。

import { truncateText } from './text'
import type { MessageReply, MessageSendType, MessageType } from '../types/frames'

/** 引用条/回复条展示文本的前端上限（与 protocol 服务端 120 字符上限独立）。 */
export const REPLY_PREVIEW_MAX_LENGTH = 60

/** 本地待发消息可提供的引用摘要最小信息（pending 还没有服务端 reply 对象）。 */
export interface ReplyPreviewLike {
  msgType: MessageSendType
  text?: string
  name?: string
}

/** 引用条文本截断：复用统一的码点安全截断，非法上限回退到引用条默认上限。 */
export function truncateReplyPreview(text: string, max = REPLY_PREVIEW_MAX_LENGTH): string {
  return truncateText(text, Number.isFinite(max) && max > 0 ? Math.floor(max) : REPLY_PREVIEW_MAX_LENGTH)
}

function summaryOf(msgType: MessageType, text: string | undefined, name: string | undefined): string {
  if (msgType === 'image') {
    return name ? `[图片] ${name}` : '[图片]'
  }
  if (msgType === 'file') {
    return name ? `[文件] ${name}` : '[文件]'
  }
  if (msgType === 'system') {
    const value = text?.trim()
    return value ? truncateReplyPreview(value) : '[系统消息]'
  }
  const value = text?.trim()
  return value ? truncateReplyPreview(value) : '[消息]'
}

/** 服务端 Reply 摘要 → 引用条展示文本。 */
export function replySummaryOf(reply: MessageReply | null | undefined): string {
  if (!reply) return ''
  return summaryOf(reply.msgType, reply.text, reply.name)
}

/** 本地待发消息（乐观发送）→ 引用条展示文本。 */
export function pendingReplyPreview(preview: ReplyPreviewLike | null | undefined): string {
  if (!preview) return ''
  return summaryOf(preview.msgType, preview.text, preview.name)
}

/** 引用条标题：有发送者昵称时拼「昵称：摘要」，否则只显示摘要。 */
export function replyCardText(
  reply: MessageReply | ReplyPreviewLike | null | undefined,
  senderName?: string,
): string {
  const summary =
    reply && 'messageId' in reply
      ? replySummaryOf(reply as MessageReply)
      : pendingReplyPreview(reply as ReplyPreviewLike | null | undefined)
  if (!summary) return ''
  const name = senderName?.trim()
  return name ? `${name}：${summary}` : summary
}
