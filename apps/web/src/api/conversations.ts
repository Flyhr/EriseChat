// 会话接口（对应 server/Chat.Server/Api/ConversationEndpoints.cs；M1 实现后按实际契约校准）。
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
