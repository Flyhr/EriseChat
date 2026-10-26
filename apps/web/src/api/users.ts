// 用户接口（对应 server/Chat.Server/Api/UserEndpoints.cs；M2-01 已按服务端实际契约校准）。
import { http } from '../core/http'
import type { AuthUser } from './auth'

export interface UserProfile extends AuthUser {
  status: 'active' | 'disabled'
  createdAt: number
  /** F-17：与当前用户的关系（仅搜索结果返回；/api/me 不返回） */
  relation?: 'friend' | 'outgoing' | 'incoming'
}

export interface SearchUsersResponse {
  users: UserProfile[]
}

export const usersApi = {
  /** 当前登录用户资料（GET /api/me） */
  me: () => http.get<UserProfile>('/me'),

  /**
   * 按邮箱/昵称模糊搜索用户（GET /api/users?search=，需登录；M2 建单聊/群用）。
   * 服务端规则：search 去空白后 1~64 字符，排除当前用户，最多 20 条，按昵称排序；
   * http.get 的 query 会序列化为 `?search=...`（见 core/http.ts buildUrl）。
   */
  search: (search: string) => http.get<SearchUsersResponse>('/users', { query: { search } }),
}
