// 认证 REST 接口（对应 server/Chat.Server/Api/AuthEndpoints.cs；M1 实现后按服务端实际契约校准）。
// M0 只固化薄封装形态，路径/字段为骨架占位，不做业务判断（project-overview.md §9）。
import { http } from '../core/http'

export interface SendEmailCodeRequest {
  email: string
  purpose: 'register' | 'login'
}

export interface SendEmailCodeResponse {
  /** 冷却秒数（同邮箱 60 秒/次，AGENTS.md §6） */
  cooldownSec: number
  /** 仅 Development 的 mock 邮件通道可能返回固定测试码，生产环境不返回 */
  devCode?: string
}

export interface LoginRequest {
  email: string
  code?: string
  password?: string
  deviceId: string
}

export interface AuthUser {
  userId: string
  email: string
  displayName: string
  avatarFileId?: string
  role: 'user' | 'admin'
}

export interface LoginResponse {
  accessToken: string
  expiresAt: number
  user: AuthUser
}

export const authApi = {
  sendEmailCode: (body: SendEmailCodeRequest) =>
    http.post<SendEmailCodeResponse>('/auth/email-code', body),

  login: (body: LoginRequest) => http.post<LoginResponse>('/auth/login', body),

  logout: () => http.post<void>('/auth/logout'),
}
