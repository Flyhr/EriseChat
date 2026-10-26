// 好友关系接口（F-17，对应 server/Chat.Server/Api/FriendEndpoints.cs；
// 服务端契约见 protocol/protocol.md §4.9 与计划 §7；变更通过 friend.changed 帧推送）。
import { http } from '../core/http'
import type { Friend, FriendRequest } from '../types/frames'

export interface FriendsResponse {
  friends: Friend[]
}

export interface FriendResponse {
  friend: Friend
}

export interface FriendRequestsResponse {
  incoming: FriendRequest[]
  outgoing: FriendRequest[]
}

export interface SendFriendRequestRequest {
  toUserId: string
  /** 附言（可选，≤100 字符） */
  message?: string
}

export interface SendFriendRequestResponse {
  request: FriendRequest
  /** 对方此前也向我发过申请时为 true（服务端自动接受并建立好友） */
  autoAccepted: boolean
  friend?: Friend
}

export interface UpdateFriendRequest {
  /** 备注（空串表示清空；不传表示不修改） */
  remark?: string
  /** 分组名（空串表示清空；不传表示不修改） */
  groupName?: string
}

export const friendsApi = {
  /** 我的好友列表（GET /api/friends） */
  list: () => http.get<FriendsResponse>('/friends'),

  /** 我的好友申请：收到的 / 发出的，仅 pending（GET /api/friends/requests） */
  requests: () => http.get<FriendRequestsResponse>('/friends/requests'),

  /** 发起好友申请（POST /api/friends/requests） */
  sendRequest: (body: SendFriendRequestRequest) =>
    http.post<SendFriendRequestResponse>('/friends/requests', body),

  /** 接受收到的申请（POST /api/friends/requests/{requestId}/accept） */
  accept: (requestId: string) =>
    http.post<FriendResponse>(`/friends/requests/${encodeURIComponent(requestId)}/accept`),

  /** 拒绝收到的申请（POST /api/friends/requests/{requestId}/reject） */
  reject: (requestId: string) =>
    http.post<void>(`/friends/requests/${encodeURIComponent(requestId)}/reject`),

  /** 撤销我发出的申请（DELETE /api/friends/requests/{requestId}） */
  cancel: (requestId: string) =>
    http.delete<void>(`/friends/requests/${encodeURIComponent(requestId)}`),

  /** 修改备注/分组（PATCH /api/friends/{friendUserId}） */
  update: (friendUserId: string, body: UpdateFriendRequest) =>
    http.patch<FriendResponse>(`/friends/${encodeURIComponent(friendUserId)}`, body),

  /** 删除好友（DELETE /api/friends/{friendUserId}，双向删除） */
  remove: (friendUserId: string) =>
    http.delete<void>(`/friends/${encodeURIComponent(friendUserId)}`),
}
