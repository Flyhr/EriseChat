// 消息接口（对应 server/Chat.Server/Api/MessageEndpoints.cs；M1 已实现，M2-05 接入历史分页）。
// 正常收发走 WebSocket（core/ws.ts）；这里的 REST 只做历史分页兜底（project-overview.md §7.5）。
// F-11：新增消息搜索（REST 形状，不属于协议帧；Message 结构沿用 protocol.md §4.3）。
import { http } from '../core/http'
import type { Message } from '../types/frames'

export interface MessagePage {
  /** 按 seq 升序 */
  messages: Message[]
  hasMore: boolean
}

/**
 * F-11/F-34：会话内结果按 seq 降序，全局结果按 createdAt/id 倒序；
 * hasMore 表示还有更早命中；nextCursor 仅全局搜索返回（与排序一致、跨会话不重不漏）。
 */
export interface MessageSearchPage {
  messages: Message[]
  hasMore: boolean
  nextCursor?: { createdAt: number; messageId: string }
}

export interface MessageContextPage {
  /** 按 seq 升序 */
  messages: Message[]
  hasOlder: boolean
  hasNewer: boolean
}

export interface MessageSearchOptions {
  /** 传入即只搜该会话；不传则跨当前用户全部会话。 */
  conversationId?: string
  /** 会话内翻页游标：返回 seq < beforeSeq 的命中（保留 F-11 行为）。 */
  beforeSeq?: number
  /** 全局翻页游标：返回发送时间早于 beforeCreatedAt，或同时间且 id < beforeMessageId 的命中。 */
  beforeCreatedAt?: number
  beforeMessageId?: string
  /** F-36：发送人筛选（用户 ID）。 */
  senderId?: string
  /** F-36：发送时间下界（毫秒时间戳，含）。 */
  from?: number
  /** F-36：发送时间上界（毫秒时间戳，含）。 */
  to?: number
  /** F-36：全局搜索显式启用发送时间游标（新客户端固定传 true）。 */
  timeCursor?: boolean
  /** F-37：显式链接模式——只匹配消息中识别出的 HTTP/HTTPS URL。 */
  linkOnly?: boolean
  /** 每页条数 1~50，默认 20。 */
  limit?: number
}

export const messagesApi = {
  history: (conversationId: string, beforeSeq?: number, limit = 50) =>
    http.get<MessagePage>(`/conversations/${conversationId}/messages`, {
      query: { beforeSeq, limit },
    }),
  /** F-11 消息搜索：仅本人所在会话、仅 text 且未删除，按 seq 降序。 */
  search: (q: string, options: MessageSearchOptions = {}) =>
    http.get<MessageSearchPage>('/messages/search', {
      query: {
        q,
        conversationId: options.conversationId,
        beforeSeq: options.beforeSeq,
        beforeCreatedAt: options.beforeCreatedAt,
        beforeMessageId: options.beforeMessageId,
        senderId: options.senderId,
        from: options.from,
        to: options.to,
        timeCursor: options.timeCursor,
        linkOnly: options.linkOnly,
        limit: options.limit,
      },
    }),
  /** F-36：按目标消息（messageId 或 seq）加载上下文窗口；只读历史，不推进同步游标。 */
  getContext: (
    conversationId: string,
    target: { messageId?: string; seq?: number; before?: number; after?: number },
  ) =>
    http.get<MessageContextPage>(`/conversations/${conversationId}/messages/context`, {
      query: {
        messageId: target.messageId,
        seq: target.seq,
        before: target.before,
        after: target.after,
      },
    }),
}
