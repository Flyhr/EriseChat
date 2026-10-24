// 消息接口（对应 server/Chat.Server/Api/MessageEndpoints.cs；M1 实现后按实际契约校准）。
// 正常收发走 WebSocket（core/ws.ts）；这里的 REST 只做历史分页兜底（project-overview.md §7.5）。
import { http } from '../core/http'
import type { Message } from '../types/frames'

export interface MessagePage {
  /** 按 seq 升序 */
  messages: Message[]
  hasMore: boolean
}

export const messagesApi = {
  history: (conversationId: string, beforeSeq?: number, limit = 50) =>
    http.get<MessagePage>(`/conversations/${conversationId}/messages`, {
      query: { beforeSeq, limit },
    }),
}
