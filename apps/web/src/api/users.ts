// 用户接口（对应 server/Chat.Server/Api/UserEndpoints.cs；M1 实现后按实际契约校准）。
import { http } from '../core/http'
import type { AuthUser } from './auth'

export interface UserProfile extends AuthUser {
  status: 'active' | 'disabled'
  createdAt: number
}

export interface SearchUsersResponse {
  users: UserProfile[]
}

export const usersApi = {
  /** 当前登录用户资料 */
  me: () => http.get<UserProfile>('/me'),

  /** 按昵称/邮箱搜索用户（建群、加好友用，M2 使用） */
  search: (keyword: string) => http.get<SearchUsersResponse>('/users', { query: { keyword } }),
}
