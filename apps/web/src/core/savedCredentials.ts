/**
 * 遗留「记住密码」凭证清理。
 *
 * 旧版本把邮箱与密码明文写进 localStorage（erisechat.savedLogin，以及 1.2.0 命名迁移前的
 * eisechat.savedLogin）。该功能已移除：登录页不再回填、不再写入密码，交给浏览器密码管理器
 * （登录框保留 autocomplete）。这里只保留一次兼容清理入口，由应用初始化调用，覆盖
 * 「升级后仍是登录态、直接进入聊天」的用户 —— 他们不会再经过登录页，旧明文会一直留在本机。
 *
 * 清理过程不读取、不解析、不打印、不重新保存旧值，只按键名删除。
 */
import { removeStorage } from './safeStorage'

export const SAVED_LOGIN_KEY = 'erisechat.savedLogin'

/** 1.2.0 命名迁移前的历史键。 */
const LEGACY_SAVED_LOGIN_KEY = 'eisechat.savedLogin'

/** 幂等：清除新旧两个历史键；存储不可用时静默（本来也没有持久化内容）。 */
export function clearLegacySavedLogin(): void {
  removeStorage(SAVED_LOGIN_KEY)
  removeStorage(LEGACY_SAVED_LOGIN_KEY)
}

// 兼容导出：既有入库的 savedCredentials.test.ts 仍按旧名字导入，删掉会让 vue-tsc 失败。
// 这里只保留名字与类型形状，实现不再读取或写入任何明文凭证；测试期望需另行同步。
/** @deprecated 历史契约占位，不再使用。 */
export interface SavedLogin {
  email: string
  password: string
}

/** @deprecated 本机不再保存凭证，恒返回 null。 */
export function loadSavedLogin(): SavedLogin | null {
  return null
}

/** @deprecated 不写入任何内容；调用只会清理历史遗留键。 */
export function saveSavedLogin(_login: SavedLogin): void {
  clearLegacySavedLogin()
}

/** @deprecated 等价于 clearLegacySavedLogin()。 */
export function clearSavedLogin(): void {
  clearLegacySavedLogin()
}
