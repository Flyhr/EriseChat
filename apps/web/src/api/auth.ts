// 认证 REST 接口（对应 server/Chat.Server/Api/AuthEndpoints.cs；M1-11 已按服务端实际契约校准）。
// 路径相对 config.apiBase（默认 /api）；错误统一抛 ApiError，code 见 protocol/protocol.md §5。
import { http } from '../core/http'

export type EmailCodePurpose = 'register' | 'login' | 'reset'

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

/** F-12：修改个人资料请求（两项至少一项；不传=不修改，暂不支持清除头像）。 */
export interface UpdateProfileRequest {
  /** 新昵称：trim 后 1~32 字符，禁控制字符 */
  displayName?: string
  /** 头像文件 ID：须为本人上传的 png/jpeg/gif/webp */
  avatarFileId?: string
}

/** F-12：PATCH /api/me/profile 响应（`/api/me` 同形状；前端只取资料字段）。 */
export interface ProfileResponse extends AuthUser {
  status: 'active' | 'disabled'
  createdAt: number
  /** F-13：是否已设置密码（决定设置页展示「修改密码」还是「设置密码」） */
  hasPassword?: boolean
}

/** F-13：修改/设置密码请求（已有密码时 oldPassword 必填）。 */
export interface ChangePasswordRequest {
  oldPassword?: string
  newPassword: string
}

/** F-13：邮箱验证码重置密码请求。 */
export interface ResetPasswordRequest {
  email: string
  code: string
  newPassword: string
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

  /** F-12：修改昵称/头像引用（两项至少一项），返回更新后的资料 */
  updateProfile: (patch: UpdateProfileRequest) =>
    http.patch<ProfileResponse>('/me/profile', patch),

  /** F-13：修改/设置密码（已有密码须传 oldPassword；返回 204） */
  changePassword: (body: ChangePasswordRequest) => http.post<void>('/me/password', body),

  /** F-13：请求密码重置验证码（purpose=reset；未注册邮箱同样返回冷却，不泄漏） */
  requestPasswordReset: (email: string) =>
    http.post<SendEmailCodeResponse>('/auth/password/reset/request', { email }),

  /** F-13：用邮箱验证码重置密码（返回 204） */
  resetPassword: (body: ResetPasswordRequest) =>
    http.post<void>('/auth/password/reset', body),
}
