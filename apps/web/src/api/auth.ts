// 认证 REST 接口（对应 server/Chat.Server/Api/AuthEndpoints.cs；M1-11 已按服务端实际契约校准）。
// 路径相对 config.apiBase（默认 /api）；错误统一抛 ApiError，code 见 protocol/protocol.md §5。
import { http } from '../core/http'

export type EmailCodePurpose = 'register' | 'login'

export interface SendEmailCodeRequest {
  email: string
  purpose: EmailCodePurpose
}

export interface SendEmailCodeResponse {
  /** 冷却秒数（同邮箱 60 秒/次，AGENTS.md §6） */
  cooldownSec: number
  /** 仅 Development 的 mock 邮件通道返回固定测试码；生产环境不返回 */
  devCode?: string
}

export interface RegisterRequest {
  email: string
  code: string
  displayName: string
  /** 可选密码；设置后可用密码登录（PBKDF2-SHA256，8~128 字符） */
  password?: string
  /** 可选：缺省时服务端生成；正式客户端应持久化稳定 deviceId */
  deviceId?: string
}

export interface LoginWithCodeRequest {
  email: string
  code: string
  deviceId: string
}

export interface LoginWithPasswordRequest {
  email: string
  password: string
  deviceId: string
}

export interface AuthUser {
  userId: string
  email: string
  displayName: string
  /** 头像文件 ID；未设置时不返回该字段 */
  avatarFileId?: string
  role: 'user' | 'admin'
}

/** 注册/登录响应（与 server 的 AuthSessionResponse 对齐） */
export interface LoginResponse {
  accessToken: string
  /** 过期时间（UTC 毫秒） */
  expiresAt: number
  user: AuthUser
}

export const authApi = {
  /** 发送邮箱验证码（purpose = register | login） */
  sendEmailCode: (body: SendEmailCodeRequest) =>
    http.post<SendEmailCodeResponse>('/auth/email/send', body),

  /** 邮箱验证码注册（成功后直接返回登录态） */
  register: (body: RegisterRequest) => http.post<LoginResponse>('/auth/register', body),

  /** 邮箱验证码登录 */
  loginWithCode: (body: LoginWithCodeRequest) =>
    http.post<LoginResponse>('/auth/login/code', body),

  /** 密码登录（可选密码；登录失败 5 次锁定 5 分钟） */
  loginWithPassword: (body: LoginWithPasswordRequest) =>
    http.post<LoginResponse>('/auth/login/password', body),

  /** 注销当前会话（服务端写 sessions.revoked_at，token 立即失效） */
  logout: () => http.post<void>('/auth/logout'),
}
