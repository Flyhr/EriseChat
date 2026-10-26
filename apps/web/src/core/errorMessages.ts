// 统一错误文案（UI-03 批次1）：把服务端错误码映射为用户可读中文，避免各页面维护重复映射。
// 依据 protocol/protocol.md §5 错误码表；未映射的码回退服务端 message，绝不泄露 token/密钥等敏感值。
import { ApiError } from './http'

/** 错误码 → 中文文案（协议 §5 全量）。 */
const CODE_MESSAGES: Record<string, string> = {
  // 通用 / WebSocket（REST 一般不会返回，保留兜底）
  BAD_FRAME: '通信数据格式有误，请刷新后重试',
  AUTH_REQUIRED: '请先登录',
  AUTH_FAILED: '登录状态已失效，请重新登录',
  AUTH_EXPIRED: '登录已过期，请重新登录',
  PROTOCOL_VERSION_MISMATCH: '客户端与服务端版本不兼容，请更新客户端',
  FORBIDDEN: '没有权限执行该操作',
  NOT_MEMBER: '你不是该会话成员，无法执行此操作',
  NOT_FOUND: '内容不存在或已被删除',
  VALIDATION_FAILED: '提交的内容不符合要求，请检查后重试',
  MESSAGE_TOO_LARGE: '消息内容过长，请精简后重试',
  RATE_LIMITED: '操作过于频繁，请稍后再试',
  INTERNAL_ERROR: '服务器开小差了，请稍后重试',
  // 认证 / 注册（REST）
  UNAUTHORIZED: '登录状态已失效，请重新登录',
  CODE_INVALID: '验证码错误，请检查后重试',
  CODE_EXPIRED: '验证码已过期，请重新获取',
  CODE_ATTEMPTS_EXCEEDED: '验证码错误次数过多，请重新获取',
  EMAIL_RATE_LIMITED: '发送过于频繁，请稍后再试',
  EMAIL_DOMAIN_NOT_ALLOWED: '该邮箱域名不允许注册，请联系管理员',
  REGISTRATION_DISABLED: '当前暂不开放注册',
  EMAIL_ALREADY_REGISTERED: '该邮箱已注册，请直接登录',
  INVITE_REQUIRED: '注册需要邀请码',
  INVITE_INVALID: '邀请码无效，请检查后重试',
  INVITE_EXPIRED: '邀请码已过期，请重新获取',
  PASSWORD_INVALID: '邮箱或密码错误',
  ACCOUNT_LOCKED: '登录失败次数过多，账号已临时锁定，请稍后再试',
  ACCOUNT_DISABLED: '账号已被停用，请联系管理员',
  // 文件
  FILE_TOO_LARGE: '文件超过大小限制',
  FILE_TYPE_NOT_ALLOWED: '不支持该文件类型',
  // 前端本地错误（core/http.ts 抛出）
  SERVER_URL_REQUIRED: '请先填写服务器地址后重试',
}

/** 网络层异常的通用提示（浏览器对网络/CORS 失败只抛 TypeError）。 */
const NETWORK_MESSAGE = '无法连接服务器，请检查网络或服务器地址'

/** 未知异常的保底提示。 */
const FALLBACK_MESSAGE = '操作失败，请稍后重试'

/**
 * 将任意异常转换为可直接展示的中文文案。
 * - ApiError：按 code 映射；未映射时用服务端 message 兜底；
 * - TypeError：按网络连接失败处理；
 * - 其它 Error：使用其 message；
 * - 非 Error：通用文案。
 */
export function friendlyError(error: unknown): string {
  if (error instanceof ApiError) {
    const mapped = CODE_MESSAGES[error.code]
    if (mapped) return mapped
    const message = error.message.trim()
    if (message.length > 0 && message !== `请求失败（HTTP ${error.status}）`) return message
    return FALLBACK_MESSAGE
  }
  if (error instanceof TypeError) return NETWORK_MESSAGE
  if (error instanceof Error) {
    const message = error.message.trim()
    return message.length > 0 ? message : FALLBACK_MESSAGE
  }
  if (typeof error === 'string' && error.trim().length > 0) return error.trim()
  return FALLBACK_MESSAGE
}
