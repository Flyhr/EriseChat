// 群邀请链接接口（C1c，对应 server/Chat.Server/Api/InviteLinkEndpoints.cs；
// 服务端契约见 protocol/protocol.md §7 第 16 条）。
// 只处理「加入群聊」：链接默认 7 天有效、可撤销，令牌不含邮箱，日志不记录令牌。
import { http } from '../core/http'
import type { Conversation } from '../types/frames'

/** 群邀请链接（创建者/群成员视角）。 */
export interface GroupInviteLink {
  token: string
  conversationId: string
  /** 过期时间（UTC 毫秒） */
  expiresAt: number
  /** 撤销时间（UTC 毫秒）；未撤销为 null/缺省 */
  revokedAt?: number | null
  /** 创建时间（UTC 毫秒） */
  createdAt: number
}

export interface InviteLinksResponse {
  links: GroupInviteLink[]
}

/** 打开链接的预览：不泄露成员名单，只给加入判断所需信息。 */
export interface GroupInvitePreview {
  conversationId: string
  title: string
  memberCount: number
  maxMembers: number
  creatorDisplayName: string
  expiresAt: number
  expired: boolean
  revoked: boolean
  alreadyMember: boolean
}

export const inviteLinksApi = {
  /** 该群未撤销的邀请链接（GET，按创建时间倒序） */
  list: (conversationId: string) =>
    http.get<InviteLinksResponse>(`/conversations/${encodeURIComponent(conversationId)}/invite-links`),

  /** 创建邀请链接（POST，须为群成员；配置仅群主可邀时仅群主） */
  create: (conversationId: string) =>
    http.post<GroupInviteLink>(`/conversations/${encodeURIComponent(conversationId)}/invite-links`),

  /** 撤销邀请链接（DELETE，群主或创建者；重复撤销幂等） */
  revoke: (conversationId: string, token: string) =>
    http.delete<void>(
      `/conversations/${encodeURIComponent(conversationId)}/invite-links/${encodeURIComponent(token)}`,
    ),

  /** 打开链接预览（GET） */
  preview: (token: string) => http.get<GroupInvitePreview>(`/invite-links/${encodeURIComponent(token)}`),

  /** 兑换加入群聊（POST，已是成员时幂等返回当前会话） */
  join: (token: string) => http.post<Conversation>(`/invite-links/${encodeURIComponent(token)}/join`),
}
