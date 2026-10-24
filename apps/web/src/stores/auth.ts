import { computed, ref } from 'vue'
import { defineStore } from 'pinia'
import { setAccessTokenProvider } from '../core/http'
import type { AuthUser } from '../api/auth'

// 登录态（M1 接入登录/注册/验证码流程；token 不写日志、本地持久化方案 M1 定）。
export const useAuthStore = defineStore('auth', () => {
  const accessToken = ref<string | null>(null)
  const user = ref<AuthUser | null>(null)

  const isLoggedIn = computed(() => accessToken.value !== null)
  const isAdmin = computed(() => user.value?.role === 'admin')

  function setSession(token: string, nextUser: AuthUser): void {
    accessToken.value = token
    user.value = nextUser
  }

  function clearSession(): void {
    accessToken.value = null
    user.value = null
  }

  // core/http 通过 provider 读取 token，避免 core 反向依赖 store（project-overview.md §10）。
  setAccessTokenProvider(() => accessToken.value)

  return {
    accessToken,
    user,
    isLoggedIn,
    isAdmin,
    setSession,
    clearSession,
  }
})
