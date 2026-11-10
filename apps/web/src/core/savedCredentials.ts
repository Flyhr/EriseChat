/**
 * F-30：登录页「记住密码」的本机凭证存储。
 *
 * 只在浏览器 localStorage 中保存邮箱与密码明文字段（内部信任模型，不做加密）；
 * 仅用于登录页回填，不参与认证、不写日志、不上传服务端。读取失败/数据损坏一律静默回退为 null。
 */
export const SAVED_LOGIN_KEY = 'erisechat.savedLogin'

export interface SavedLogin {
  email: string
  password: string
}

/** 取本机存储；隐私模式/禁用 localStorage 时返回 null（记住密码自动降级为不可用）。 */
function getStorage(): Storage | null {
  try {
    return typeof localStorage === 'undefined' ? null : localStorage
  } catch {
    return null
  }
}

/** 读取已保存凭证；无记录、损坏 JSON、缺字段或空值时返回 null。 */
export function loadSavedLogin(): SavedLogin | null {
  const storage = getStorage()
  if (!storage) return null
  try {
    const raw = storage.getItem(SAVED_LOGIN_KEY)
    if (!raw) return null
    const parsed: unknown = JSON.parse(raw)
    if (typeof parsed !== 'object' || parsed === null) return null
    const record = parsed as Record<string, unknown>
    const email = typeof record.email === 'string' ? record.email.trim() : ''
    const password = typeof record.password === 'string' ? record.password : ''
    if (email.length === 0 || password.length === 0) return null
    return { email, password }
  } catch {
    return null
  }
}

/** 保存凭证（只写 email/password 两个字段）；邮箱去首尾空格，空值时不写入。 */
export function saveSavedLogin(login: SavedLogin): void {
  const storage = getStorage()
  if (!storage) return
  const email = login.email.trim()
  if (email.length === 0 || login.password.length === 0) return
  try {
    storage.setItem(SAVED_LOGIN_KEY, JSON.stringify({ email, password: login.password }))
  } catch {
    // 配额满/隐私模式写失败时静默：记住密码只是便利功能，不影响登录。
  }
}

/** 清除已保存凭证（取消勾选/登录时未勾选）。 */
export function clearSavedLogin(): void {
  const storage = getStorage()
  if (!storage) return
  try {
    storage.removeItem(SAVED_LOGIN_KEY)
  } catch {
    // 同上，静默。
  }
}
