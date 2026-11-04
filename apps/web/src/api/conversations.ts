// 会话接口（对应 server/Chat.Server/Api/ConversationsEndpoints.cs；M3-08 按服务端实际契约校准）。
// 群成员信息包含在 Conversation.members 中返回；群成员变更走 POST /conversations/{id}/members 与
// DELETE /conversations/{id}/members/{userId}；群资料（标题/公告）走 PATCH /conversations/{id}（仅群主，F-14）。
// 会话共享文件走 GET /conversations/{id}/files（F-15：只读汇总 image/file 消息附件）。
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

/** 成员设置请求（F-10 / 标为未读）：至少提供 pinned/muted/markedUnread 之一，服务端校验。 */
export interface MemberSettingsRequest {
  pinned?: boolean
  muted?: boolean
  /** 手动标为未读：只影响未读筛选，不改已读游标与真实未读数；变更会同步到同账号其他在线设备。 */
  markedUnread?: boolean
}

/** 群主转让请求（F-24）：目标成员用户 ID。 */
export interface TransferOwnerRequest {
  userId: string
}

/**
 * 群资料更新请求（F-14 / F-23）：字段缺省 = 不修改；`announcement: ''` = 清空公告；
 * `avatarFileId: ''` = 清空群头像；title 必须为非空字符串（不传表示不改）。仅群聊群主可用。
 */
export interface GroupProfileUpdate {
  title?: string
  announcement?: string
  avatarFileId?: string
}

/** 共享文件类型（F-15）：与消息 msgType 对应。 */
export type SharedFileType = 'image' | 'file'

/** 共享文件条目（F-15）：每个文件取其在会话中最新引用的消息（messageSeq 最大）。 */
export interface SharedFileItem {
  fileId: string
  name: string
  size: number
  mime: string
  messageId: string
  messageSeq: number
  senderId: string
  createdAt: number
}

/** 共享文件分页（F-15）：items 按 messageSeq 降序；beforeSeq 为排他游标。 */
export interface SharedFilesPage {
  items: SharedFileItem[]
  hasMore: boolean
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

  /**
   * 修改群资料（F-14 / F-23）：仅群聊群主可用；字段缺省 = 不修改，`announcement: ''` / `avatarFileId: ''` = 清空。
   * 返回当前用户视角的完整 Conversation；其他成员通过服务端按收件人下发的 conv.updated 刷新。
   */
  updateGroupProfile: (conversationId: string, body: GroupProfileUpdate) =>
    http.patch<Conversation>(
      `/conversations/${conversationId}`,
      body satisfies GroupProfileUpdate,
    ),

  /**
   * 转让群主（F-24）：仅当前群主可用，目标须为本群成员且非自己。
   * 成功后服务端互换两条成员行的角色、写 owner.transferred 系统消息并广播 conv.updated；
   * 返回当前用户视角的完整 Conversation（群主身份已转出）。
   */
  transferOwner: (conversationId: string, userId: string) =>
    http.post<Conversation>(
      `/conversations/${conversationId}/owner`,
      { userId } satisfies TransferOwnerRequest,
    ),

  /**
   * 会话共享文件分页（F-15）：需登录且为会话成员；type 必填，limit 1~100（默认 50），
   * beforeSeq 为排他游标（传上一页最后一项的 messageSeq 继续向前翻）。
   */
  listFiles: (
    conversationId: string,
    options: { type: SharedFileType; beforeSeq?: number; limit?: number },
  ) => {
    const params = new URLSearchParams({ type: options.type })
    if (options.beforeSeq !== undefined) params.set('beforeSeq', String(options.beforeSeq))
    if (options.limit !== undefined) params.set('limit', String(options.limit))
    return http.get<SharedFilesPage>(
      `/conversations/${conversationId}/files?${params.toString()}`,
    )
  },
}
