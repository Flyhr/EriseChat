// 拉黑关系接口（C1b，对应 server/Chat.Server/Api/BlockEndpoints.cs；
// 服务端契约见 protocol/protocol.md §7 第 15 条）。
// 拉黑是有方向的单向关系：任一方拉黑期间双方不能私聊、申请好友或邀请入群；
// 服务端在发送/申请/邀请入口统一返回 BLOCKED，客户端只做展示与操作反馈。
import { http } from '../core/http'

/** 拉黑列表条目（被拉黑用户的最小资料；不含邮箱）。 */
export interface BlockedUser {
  userId: string
  displayName: string
  avatarFileId?: string | null
  /** 拉黑时间（UTC 毫秒） */
  createdAt: number
}

export interface BlocksResponse {
  blocks: BlockedUser[]
}

export interface BlockResponse {
  block: BlockedUser
}

export const blocksApi = {
  /** 我拉黑的用户列表（GET /api/blocks，按拉黑时间倒序） */
  list: () => http.get<BlocksResponse>('/blocks'),

  /** 拉黑用户（PUT /api/blocks/{userId}，幂等） */
  block: (userId: string) => http.put<BlockResponse>(`/blocks/${encodeURIComponent(userId)}`),

  /** 解除拉黑（DELETE /api/blocks/{userId}，幂等） */
  unblock: (userId: string) => http.delete<void>(`/blocks/${encodeURIComponent(userId)}`),
}
