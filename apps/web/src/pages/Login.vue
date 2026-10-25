<script setup lang="ts">
import { computed, onBeforeUnmount, ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { authApi, type LoginResponse } from '../api/auth'
import { ApiError } from '../core/http'
import { chatWs } from '../core/ws'
import { getServerUrl, resetServerUrl, setServerUrl } from '../core/config'
import { useAuthStore } from '../stores/auth'
import { useConversationsStore } from '../stores/conversations'
import { useMessagesStore } from '../stores/messages'

// 登录/注册页（M2-03）：验证码注册 / 验证码登录 / 密码登录三种模式。
// 成功后统一走 applySession：写登录态 → 注入 WS 认证 → 建连 → 进聊天页（protocol.md §4.1）。
type Mode = 'register' | 'code' | 'password'

const MODE_LABELS: Record<Mode, string> = {
  code: '验证码登录',
  register: '注册',
  password: '密码登录',
}
const modes = Object.keys(MODE_LABELS) as Mode[]

/** 服务端错误码 → 中文文案（protocol.md §5；未列出的错误码用服务端 message 兜底）。 */
const ERROR_MESSAGES: Record<string, string> = {
  CODE_INVALID: '验证码错误，请检查后重试',
  CODE_EXPIRED: '验证码已过期，请重新获取',
  CODE_ATTEMPTS_EXCEEDED: '验证码错误次数过多，请重新获取',
  EMAIL_ALREADY_REGISTERED: '该邮箱已注册，请直接登录',
  EMAIL_RATE_LIMITED: '发送过于频繁，请稍后再试',
  PASSWORD_INVALID: '邮箱或密码错误',
  ACCOUNT_LOCKED: '登录失败次数过多，账号已临时锁定，请稍后再试',
  UNAUTHORIZED: '登录状态已失效，请重新登录',
}

const router = useRouter()
const route = useRoute()
const auth = useAuthStore()

/** 致命关闭（被踢/登录失效）跳转登录页时携带的提示（M3-00）。 */
const noticeMessage = ref(typeof route.query.notice === 'string' ? route.query.notice : '')

/** M4-01：服务器地址配置（桌面/APK 首次使用需填写；Web 同源可留空）。 */
const serverUrlOpen = ref(false)
const serverUrl = ref(getServerUrl())
const serverUrlError = ref('')
const serverUrlLabel = computed(() => serverUrl.value.trim() || '与当前页面同源')

const mode = ref<Mode>('code')
const email = ref('')
const code = ref('')
const displayName = ref('')
const password = ref('')
const submitting = ref(false)
const sending = ref(false)
const errorMessage = ref('')
const devCodeHint = ref('')
const cooldown = ref(0)
let cooldownTimer: ReturnType<typeof setInterval> | null = null

const purpose = computed<'register' | 'login'>(() => (mode.value === 'register' ? 'register' : 'login'))
const submitLabel = computed(() => (mode.value === 'register' ? '注册并登录' : '登录'))
const canSubmit = computed(() => !submitting.value && email.value.trim().length > 0)
const sendButtonLabel = computed(() => {
  if (sending.value) return '发送中…'
  if (cooldown.value > 0) return `${cooldown.value}s 后重发`
  return '发送验证码'
})

function toErrorMessage(error: unknown): string {
  if (error instanceof ApiError) return ERROR_MESSAGES[error.code] ?? error.message
  if (error instanceof Error && error.message) return error.message
  return '网络异常，请稍后重试'
}

function startCooldown(seconds: number): void {
  stopCooldown()
  cooldown.value = seconds
  cooldownTimer = setInterval(() => {
    cooldown.value -= 1
    if (cooldown.value <= 0) stopCooldown()
  }, 1000)
}

function stopCooldown(): void {
  if (cooldownTimer !== null) {
    clearInterval(cooldownTimer)
    cooldownTimer = null
  }
}

onBeforeUnmount(stopCooldown)

function switchMode(next: Mode): void {
  mode.value = next
  errorMessage.value = ''
  devCodeHint.value = ''
}

async function sendCode(): Promise<void> {
  if (sending.value || cooldown.value > 0) return
  const target = email.value.trim()
  if (!target) {
    errorMessage.value = '请先输入邮箱'
    return
  }
  sending.value = true
  errorMessage.value = ''
  try {
    const result = await authApi.sendEmailCode({ email: target, purpose: purpose.value })
    devCodeHint.value = result.devCode ?? ''
    // 冷却时间以服务端返回为准（同邮箱 60 秒/次，AGENTS.md §6）。
    startCooldown(result.cooldownSec > 0 ? result.cooldownSec : 60)
  } catch (error) {
    errorMessage.value = toErrorMessage(error)
  } finally {
    sending.value = false
  }
}

async function submit(): Promise<void> {
  if (!canSubmit.value) return
  const target = email.value.trim()
  if (mode.value === 'register' && displayName.value.trim().length === 0) {
    errorMessage.value = '请输入昵称'
    return
  }
  if (mode.value !== 'password' && code.value.trim().length === 0) {
    errorMessage.value = '请输入验证码'
    return
  }
  if (mode.value !== 'code' && password.value.length === 0) {
    errorMessage.value = '请输入密码'
    return
  }

  submitting.value = true
  errorMessage.value = ''
  try {
    let response: LoginResponse
    if (mode.value === 'register') {
      response = await authApi.register({
        email: target,
        code: code.value.trim(),
        displayName: displayName.value.trim(),
        password: password.value.length > 0 ? password.value : undefined,
        deviceId: auth.deviceId,
      })
    } else if (mode.value === 'code') {
      response = await authApi.loginWithCode({
        email: target,
        code: code.value.trim(),
        deviceId: auth.deviceId,
      })
    } else {
      response = await authApi.loginWithPassword({
        email: target,
        password: password.value,
        deviceId: auth.deviceId,
      })
    }
    applySession(response)
    await router.push('/chat')
  } catch (error) {
    errorMessage.value = toErrorMessage(error)
  } finally {
    submitting.value = false
    stopCooldown()
  }
}

/** 登录成功：持久化登录态 → 注入 WS 认证信息并建连（WS auth 必须是连接后首帧）。 */
function applySession(response: LoginResponse): void {
  // 切换账号/重新登录前先断开旧连接、清空上一位用户的会话与消息状态（M3-00 修 B4）。
  if (chatWs.state !== 'idle' && chatWs.state !== 'closed') {
    chatWs.disconnect(1000, 'switch account')
  }
  useConversationsStore().clear()
  useMessagesStore().clear()
  auth.setSession(response.accessToken, response.user, response.expiresAt)
  chatWs.setAuth(response.accessToken, auth.deviceId)
  chatWs.connect()
}

/** M4-01：保存服务器地址（空字符串 = 同源默认）；Web 端校验 http(s) origin 后持久化。 */
function saveServerUrl(): void {
  const value = serverUrl.value.trim()
  if (value.length > 0 && !setServerUrl(value)) {
    serverUrlError.value = '地址无效：需以 http:// 或 https:// 开头'
    return
  }
  if (value.length === 0) {
    resetServerUrl()
  }
  serverUrl.value = getServerUrl()
  serverUrlError.value = ''
  serverUrlOpen.value = false
}

/** 恢复同源默认地址（清除 localStorage 配置）。 */
function clearServerUrl(): void {
  resetServerUrl()
  serverUrl.value = ''
  serverUrlError.value = ''
}
</script>

<template>
  <main class="login-page">
    <section class="login-card">
      <h1 class="login-card__title">EiseChat</h1>
      <p class="login-card__subtitle">内部聊天工具 · 邮箱验证码登录</p>

      <div class="login-server">
        <button class="login-server__toggle" type="button" @click="serverUrlOpen = !serverUrlOpen">
          服务器地址：{{ serverUrlLabel }}{{ serverUrlOpen ? '（收起）' : '（修改）' }}
        </button>
        <div v-if="serverUrlOpen" class="login-server__panel">
          <input
            v-model="serverUrl"
            type="url"
            placeholder="https://chat.example.com 或 http://127.0.0.1:8080（留空 = 同源）"
          />
          <div class="login-server__actions">
            <button class="login-server__save" type="button" @click="saveServerUrl">保存</button>
            <button class="login-server__reset" type="button" @click="clearServerUrl">恢复默认</button>
          </div>
          <p v-if="serverUrlError" class="login-server__error" role="alert">{{ serverUrlError }}</p>
        </div>
      </div>

      <div class="login-modes" role="tablist" aria-label="登录方式">
        <button
          v-for="item in modes"
          :key="item"
          class="login-modes__item"
          :class="{ 'login-modes__item--active': mode === item }"
          type="button"
          role="tab"
          :aria-selected="mode === item"
          @click="switchMode(item)"
        >
          {{ MODE_LABELS[item] }}
        </button>
      </div>

      <form class="login-form" @submit.prevent="submit">
        <label class="login-form__field">
          <span class="login-form__label">邮箱</span>
          <input v-model="email" type="email" autocomplete="email" placeholder="you@company.com" />
        </label>

        <label v-if="mode === 'register'" class="login-form__field">
          <span class="login-form__label">昵称</span>
          <input
            v-model="displayName"
            type="text"
            maxlength="32"
            autocomplete="nickname"
            placeholder="聊天中显示的名字"
          />
        </label>

        <label v-if="mode !== 'password'" class="login-form__field">
          <span class="login-form__label">验证码</span>
          <div class="login-form__code-row">
            <input v-model="code" inputmode="numeric" maxlength="6" placeholder="6 位数字" />
            <button
              class="login-form__send"
              type="button"
              :disabled="sending || cooldown > 0"
              @click="sendCode"
            >
              {{ sendButtonLabel }}
            </button>
          </div>
        </label>

        <label v-if="mode !== 'code'" class="login-form__field">
          <span class="login-form__label">
            {{ mode === 'register' ? '密码（可选，设置后可用密码登录）' : '密码' }}
          </span>
          <input
            v-model="password"
            type="password"
            autocomplete="current-password"
            minlength="8"
            maxlength="128"
            placeholder="8~128 个字符"
          />
        </label>

        <p v-if="noticeMessage" class="login-form__notice" role="status">{{ noticeMessage }}</p>
        <p v-if="devCodeHint" class="login-form__hint">
          Development 测试码：<strong>{{ devCodeHint }}</strong>（生产环境不返回）
        </p>
        <p v-if="errorMessage" class="login-form__error" role="alert">{{ errorMessage }}</p>

        <button class="login-form__submit" type="submit" :disabled="submitting">
          {{ submitting ? '处理中…' : submitLabel }}
        </button>
      </form>
    </section>
  </main>
</template>

<style scoped>
.login-page {
  display: flex;
  align-items: center;
  justify-content: center;
  min-height: 100%;
  padding: var(--space-5);
}

.login-card {
  width: 100%;
  max-width: 360px;
  padding: var(--space-6);
  background: var(--color-surface);
  border-radius: var(--radius-lg);
  box-shadow: var(--shadow-md);
}

.login-card__title {
  margin: 0 0 var(--space-2);
  font-size: 24px;
  color: var(--color-primary);
}

.login-card__subtitle {
  margin: 0 0 var(--space-5);
  font-size: 13px;
  color: var(--color-text-secondary);
}

.login-modes {
  display: flex;
  gap: var(--space-1);
  margin-bottom: var(--space-4);
  padding: var(--space-1);
  background: var(--color-bg);
  border-radius: var(--radius-sm);
}

.login-modes__item {
  flex: 1;
  padding: var(--space-2) var(--space-1);
  border: none;
  border-radius: var(--radius-sm);
  background: transparent;
  color: var(--color-text-secondary);
  font-size: 13px;
  cursor: pointer;
}

.login-modes__item--active {
  background: var(--color-surface);
  color: var(--color-primary);
  font-weight: 600;
  box-shadow: var(--shadow-sm);
}

.login-server {
  margin: 0 0 var(--space-3);
}

.login-server__toggle {
  padding: 0;
  border: none;
  background: none;
  color: var(--color-text-secondary);
  font-size: 12px;
  cursor: pointer;
}

.login-server__panel {
  display: grid;
  gap: var(--space-2);
  margin-top: var(--space-2);
}

.login-server__panel input {
  width: 100%;
  padding: var(--space-2) var(--space-3);
  border: 1px solid var(--color-border);
  border-radius: var(--radius-sm);
  outline: none;
  box-sizing: border-box;
}

.login-server__panel input:focus {
  border-color: var(--color-primary);
}

.login-server__actions {
  display: flex;
  gap: var(--space-2);
}

.login-server__save {
  padding: var(--space-1) var(--space-3);
  border: 1px solid var(--color-primary);
  border-radius: var(--radius-sm);
  background: var(--color-primary);
  color: var(--color-text-inverse);
  font-size: 12px;
  cursor: pointer;
}

.login-server__reset {
  padding: var(--space-1) var(--space-3);
  border: 1px solid var(--color-border);
  border-radius: var(--radius-sm);
  background: transparent;
  color: var(--color-text-secondary);
  font-size: 12px;
  cursor: pointer;
}

.login-server__error {
  margin: 0;
  color: var(--color-danger);
  font-size: 12px;
}

.login-form {
  display: grid;
  gap: var(--space-4);
}

.login-form__field {
  display: grid;
  gap: var(--space-2);
}

.login-form__label {
  font-size: 13px;
  color: var(--color-text-secondary);
}

.login-form input {
  width: 100%;
  padding: var(--space-2) var(--space-3);
  border: 1px solid var(--color-border);
  border-radius: var(--radius-sm);
  outline: none;
  box-sizing: border-box;
}

.login-form input:focus {
  border-color: var(--color-primary);
}

.login-form__code-row {
  display: flex;
  gap: var(--space-2);
}

.login-form__code-row input {
  flex: 1;
  min-width: 0;
}

.login-form__send {
  flex: none;
  padding: 0 var(--space-3);
  border: 1px solid var(--color-primary);
  border-radius: var(--radius-sm);
  background: transparent;
  color: var(--color-primary);
  font-size: 13px;
  white-space: nowrap;
  cursor: pointer;
}

.login-form__send:disabled {
  border-color: var(--color-border);
  color: var(--color-text-secondary);
  cursor: not-allowed;
}

.login-form__hint {
  margin: 0;
  padding: var(--space-2) var(--space-3);
  border-radius: var(--radius-sm);
  background: var(--color-primary-soft);
  color: var(--color-primary);
  font-size: 13px;
}

.login-form__notice {
  margin: 0;
  padding: var(--space-2) var(--space-3);
  border-radius: var(--radius-sm);
  background: var(--color-primary-soft);
  color: var(--color-primary);
  font-size: 13px;
}

.login-form__error {
  margin: 0;
  padding: var(--space-2) var(--space-3);
  border-radius: var(--radius-sm);
  background: var(--color-danger);
  color: var(--color-text-inverse);
  font-size: 13px;
}

.login-form__submit {
  padding: var(--space-3);
  border: none;
  border-radius: var(--radius-sm);
  background: var(--color-primary);
  color: var(--color-text-inverse);
  cursor: pointer;
}

.login-form__submit:disabled {
  opacity: 0.6;
  cursor: not-allowed;
}
</style>
