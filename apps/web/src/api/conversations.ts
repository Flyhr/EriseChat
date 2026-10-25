// 会话接口（对应 server/Chat.Server/Api/ConversationsEndpoints.cs；M2-04 已按服务端实际契约校准 list/create）。
// members/updateTitle 服务端尚未实现，保留占位给后续里程碑。
import { http } from '../core/http'
import type { Conversation, ConversationMember } from '../types/frames'

export interface CreateConversationRequest {
  type: 'direct' | 'group'
  /** 群聊标题；单聊可省略（服务端按对方昵称展示） */
  title?: string
  memberIds: string[]
}

export const conversationsApi = {
  list: () => http.get<Conversation[]>('/conversations'),

  create: (body: CreateConversationRequest) => http.post<Conversation>('/conversations', body),

  get: (conversationId: string) => http.get<Conversation>(`/conversations/${conversationId}`),

  members: (conversationId: string) =>
    http.get<ConversationMember[]>(`/conversations/${conversationId}/members`),

  updateTitle: (conversationId: string, title: string) =>
    http.patch<Conversation>(`/conversations/${conversationId}`, { title }),
}
