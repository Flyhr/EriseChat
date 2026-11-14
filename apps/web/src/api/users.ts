// 用户接口（对应 server/Chat.Server/Api/UserEndpoints.cs；M2-01 已按服务端实际契约校准）。
import { http } from '../core/http'
import type { AuthUser } from './auth'

/**
 * 用户资料（/api/me 返回本人邮箱；搜索结果不下发他人邮箱，见 C1a 邮箱收敛）。
 * email 可选：仅本人接口有值，搜索结果的类型带不到该字段，UI 按缺失处理。
 */
export interface UserProfile extends Omit<AuthUser, 'email'> {
  email?: string
  status: 'active' | 'disabled'
  createdAt: number
  /** F-13：是否已设置密码（仅 /api/me 返回；搜索结果为 undefined） */
  hasPassword?: boolean
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
   * 按昵称或完整邮箱搜索用户（GET /api/users?search=，需登录；M2 建单聊/群用）。
   * 服务端规则：search 去空白后 1~64 字符，只按昵称模糊匹配；整串是合法邮箱时额外精确匹配邮箱；
   * 排除当前用户，最多 20 条，按昵称排序，结果不含他人邮箱；
   * http.get 的 query 会序列化为 `?search=...`（见 core/http.ts buildUrl）。
   */
  search: (search: string) => http.get<SearchUsersResponse>('/users', { query: { search } }),
}
