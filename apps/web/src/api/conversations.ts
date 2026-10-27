// 会话接口（对应 server/Chat.Server/Api/ConversationsEndpoints.cs；M3-08 按服务端实际契约校准）。
// 群成员信息包含在 Conversation.members 中返回；服务端没有独立的 GET members / PATCH 标题路由，
// 群成员变更走 POST /conversations/{id}/members 与 DELETE /conversations/{id}/members/{userId}。
import { http } from '../core/http'
import type { Conversation } from '../types/frames'

export interface CreateConversationRequest {
  type: 'direct' | 'group'
  /** 群聊标题；单聊可省略（服务端按对方昵称展示） */
  title?: string
  memberIds: string[]
}

export interface AddConversationMembersRequest {
  userIds: string[]
}

/** 成员设置请求（F-10）：至少提供 pinned/muted 之一，服务端校验。 */
export interface MemberSettingsRequest {
  pinned?: boolean
  muted?: boolean
}

export const conversationsApi = {
  list: () => http.get<Conversation[]>('/conversations'),

  create: (body: CreateConversationRequest) => http.post<Conversation>('/conversations', body),

  get: (conversationId: string) => http.get<Conversation>(`/conversations/${conversationId}`),

  /**
   * 创建群聊（M3-08）：当前用户为群主，memberIds 为被邀请成员（不含自己，至少 1 人）。
   * 校验失败（群名/人数/用户不存在/超上限）由服务端返回 VALIDATION_FAILED 与具体文案。
   */
  createGroup: (title: string, memberIds: string[]) =>
    http.post<Conversation>('/conversations', {
      type: 'group',
      title,
      memberIds,
    } satisfies CreateConversationRequest),

  /** 邀请成员（M3-08）：返回更新后的 Conversation；已在群内的用户由服务端幂等跳过。 */
  addMembers: (conversationId: string, userIds: string[]) =>
    http.post<Conversation>(`/conversations/${conversationId}/members`, {
      userIds,
    } satisfies AddConversationMembersRequest),

  /** 移除成员 / 退出群聊（M3-08）：userId 传自己即退出；成功返回 204（http 层映射为 undefined）。 */
  removeMember: (conversationId: string, userId: string) =>
    http.delete<void>(`/conversations/${conversationId}/members/${encodeURIComponent(userId)}`),

  /**
   * 更新当前用户在该会话的置顶/免打扰（F-10）：只改本人成员行，返回更新后的 Conversation。
   * 私密性：服务端不广播，客户端以本响应更新 store。
   */
  updateMemberSettings: (conversationId: string, settings: MemberSettingsRequest) =>
    http.patch<Conversation>(
      `/conversations/${conversationId}/member-settings`,
      settings satisfies MemberSettingsRequest,
    ),
}
