import { computed, ref } from 'vue'
import { defineStore } from 'pinia'
import { setAccessTokenProvider } from '../core/http'
import type { AuthUser } from '../api/auth'

/** deviceId localStorage key（与 core/ws.ts 共用同一 key，保证 REST 会话与 WS auth 设备标识一致）。 */
const DEVICE_ID_STORAGE_KEY = 'eisechat.deviceId'
/** 登录态持久化 key：内部信任模型下允许存 localStorage（token 不写日志、不打印）。 */
const SESSION_STORAGE_KEY = 'eisechat.session'

interface StoredSession {
  accessToken: string
  /** 过期时间（UTC 毫秒） */
  expiresAt: number
  user: AuthUser
}

/** 读取或生成稳定的设备标识（同一设备重连复用，protocol.md §4.1）。 */
function readOrCreateDeviceId(): string {
  try {
    const existing = window.localStorage.getItem(DEVICE_ID_STORAGE_KEY)
    if (existing) return existing
    const created = `web-${crypto.randomUUID()}`
    window.localStorage.setItem(DEVICE_ID_STORAGE_KEY, created)
    return created
  } catch {
    // 隐私模式等场景 localStorage 不可用：退化为固定值，不阻塞使用。
    return 'web-unknown'
  }
}

/** 清空持久化登录态（不抛异常）。 */
function clearStoredSession(): void {
  try {
    window.localStorage.removeItem(SESSION_STORAGE_KEY)
  } catch {
    // 忽略清理失败（localStorage 不可用时本来也没有持久化）。
  }
}

/** 从 localStorage 恢复登录态；结构非法或已过期时清空并返回 null。 */
function readStoredSession(): StoredSession | null {
  try {
    const raw = window.localStorage.getItem(SESSION_STORAGE_KEY)
    if (!raw) return null
    const parsed = JSON.parse(raw) as Record<string, unknown> | null
    if (typeof parsed !== 'object' || parsed === null) {
      clearStoredSession()
      return null
    }
    const token = parsed.accessToken
    const expiresAtMs = parsed.expiresAt
    const storedUser = parsed.user
    if (typeof token !== 'string' || token.length === 0 || typeof expiresAtMs !== 'number') {
      clearStoredSession()
      return null
    }
    if (
      typeof storedUser !== 'object' ||
      storedUser === null ||
      typeof (storedUser as { userId?: unknown }).userId !== 'string'
    ) {
      clearStoredSession()
      return null
    }
    if (expiresAtMs <= Date.now()) {
      clearStoredSession()
      return null
    }
    return { accessToken: token, expiresAt: expiresAtMs, user: storedUser as AuthUser }
  } catch {
    clearStoredSession()
    return null
  }
}

// 登录态（M2-02：token/user/expiresAt 持久化到 localStorage，启动时恢复并校验过期；
// 401 由 core/http 回调 → 应用入口清会话并跳 /login）。
export const useAuthStore = defineStore('auth', () => {
  const restored = readStoredSession()
  const accessToken = ref<string | null>(restored?.accessToken ?? null)
  const user = ref<AuthUser | null>(restored?.user ?? null)
  const expiresAt = ref<number>(restored?.expiresAt ?? 0)
  /** 本机稳定设备标识（注册/登录/WS auth 复用；已持久化）。 */
  const deviceId = ref<string>(readOrCreateDeviceId())

  const isLoggedIn = computed(() => accessToken.value !== null && expiresAt.value > Date.now())
  const isAdmin = computed(() => user.value?.role === 'admin')

  function setSession(token: string, nextUser: AuthUser, expiresAtMs: number): void {
    accessToken.value = token
    user.value = nextUser
    expiresAt.value = expiresAtMs
    try {
      window.localStorage.setItem(
        SESSION_STORAGE_KEY,
        JSON.stringify({ accessToken: token, expiresAt: expiresAtMs, user: nextUser }),
      )
    } catch {
      // localStorage 不可用时仅保留内存态；刷新后需重新登录。
    }
  }

  function clearSession(): void {
    accessToken.value = null
    user.value = null
    expiresAt.value = 0
    clearStoredSession()
  }

  // core/http 通过 provider 读取 token，避免 core 反向依赖 store（project-overview.md §10）。
  setAccessTokenProvider(() => accessToken.value)

  return {
    accessToken,
    user,
    expiresAt,
    deviceId,
    isLoggedIn,
    isAdmin,
    setSession,
    clearSession,
  }
})
