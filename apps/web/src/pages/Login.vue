<script setup lang="ts">
import { computed, onBeforeUnmount, ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { authApi, type LoginResponse } from '../api/auth'
import { chatWs } from '../core/ws'
import { getServerUrl, normalizeServerUrl, resetServerUrl, setServerUrl } from '../core/config'
import { friendlyError } from '../core/errorMessages'
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

/** 服务端错误码 → 中文文案统一由 core/errorMessages 维护（UI-03 批次1）。 */

const router = useRouter()
const route = useRoute()
const auth = useAuthStore()

/** 致命关闭（被踢/登录失效）跳转登录页时携带的提示（M3-00）。 */
const noticeMessage = ref(typeof route.query.notice === 'string' ? route.query.notice : '')

/** 桌面/APK 打包态：页面以 file:// 加载，「同源」不可用，必须显式配置服务器地址（M4-08）。 */
const isFileShell = typeof window !== 'undefined' && window.location.protocol === 'file:'
const DEFAULT_DESKTOP_SERVER = 'http://127.0.0.1:8080'

/** M4-01：服务器地址配置（桌面/APK 首次使用需填写；Web 同源可留空）。 */
const serverUrlOpen = ref(false)
const serverUrl = ref(getServerUrl())
const serverUrlError = ref('')
const serverUrlLabel = computed(
  () => serverUrl.value.trim() || (isFileShell ? '未配置，请点此填写' : '与当前页面同源'),
)

/** UI-03 批次3：桌面壳注入的服务器地址存在时，地址由桌面端配置，页面只读展示。 */
const injectedServerUrl = normalizeServerUrl(
  typeof window !== 'undefined'
    ? (window as Window & { eisechatDesktop?: { serverUrl?: string } }).eisechatDesktop?.serverUrl
    : null,
)

/** UI-03 批次3：密码显示/隐藏（只影响输入框 type，不记录任何日志）。 */
const passwordVisible = ref(false)

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
  // 桌面/APK 以 file:// 加载时，网络失败需额外提示先配置服务器地址（其余错误统一走 friendlyError）。
  if (isFileShell && error instanceof TypeError) {
    return '无法连接服务器：请先在“服务器地址”填写服务端地址（如 http://127.0.0.1:8080）后重试'
  }
  return friendlyError(error)
}

/** 展开服务器地址编辑区：桌面端未配置时预填本机默认地址，点「保存」即可生效。 */
function toggleServerEditor(): void {
  serverUrlOpen.value = !serverUrlOpen.value
  if (serverUrlOpen.value && serverUrl.value.trim().length === 0 && isFileShell) {
    serverUrl.value = DEFAULT_DESKTOP_SERVER
  }
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
  // 密码登录必填；注册模式下密码可选（服务端允许只凭验证码注册，AGENTS.md §6）。
  if (mode.value === 'password' && password.value.length === 0) {
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
    <section class="login-brand" aria-hidden="true">
      <div class="login-brand__inner">
        <span class="login-brand__logo">E</span>
        <h1 class="login-brand__title">EiseChat</h1>
        <p class="login-brand__desc">团队内部使用的轻量聊天工具：消息实时送达，历史随时可查。</p>
        <ul class="login-brand__list">
          <li>邮箱验证码登录，无需额外注册流程</li>
          <li>单聊与群聊，支持图片和文件</li>
          <li>断线自动重连，消息不丢失</li>
        </ul>
      </div>
    </section>

    <section class="login-panel">
      <div class="login-card">
        <h2 class="login-card__title">{{ mode === 'register' ? '创建账号' : '欢迎回来' }}</h2>
        <p class="login-card__subtitle">内部聊天工具 · 邮箱验证码登录</p>

        <div class="login-server">
          <p v-if="injectedServerUrl" class="login-server__locked">
            服务器地址：{{ injectedServerUrl }}（由桌面端配置）
          </p>
          <template v-else>
            <button class="login-server__toggle" type="button" @click="toggleServerEditor">
              服务器地址：{{ serverUrlLabel }}{{ serverUrlOpen ? '（收起）' : '（修改）' }}
            </button>
            <div v-if="serverUrlOpen" class="login-server__panel">
              <input
                v-model="serverUrl"
                type="url"
                aria-label="服务器地址"
                :placeholder="
                  isFileShell
                    ? 'http://127.0.0.1:8080（本机）或 https://你的域名'
                    : 'https://chat.example.com 或 http://127.0.0.1:8080（留空 = 同源）'
                "
              />
              <div class="login-server__actions">
                <button class="login-server__save" type="button" @click="saveServerUrl">保存</button>
                <button class="login-server__reset" type="button" @click="clearServerUrl">恢复默认</button>
              </div>
              <p v-if="serverUrlError" class="login-server__error" role="alert">{{ serverUrlError }}</p>
            </div>
          </template>
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

          <div v-if="mode !== 'code'" class="login-form__field">
            <label class="login-form__label" for="login-password">
              {{ mode === 'register' ? '密码（可选，设置后可用密码登录）' : '密码' }}
            </label>
            <div class="login-form__password">
              <input
                id="login-password"
                v-model="password"
                :type="passwordVisible ? 'text' : 'password'"
                autocomplete="current-password"
                minlength="8"
                maxlength="128"
                placeholder="8~128 个字符"
              />
              <button
                class="login-form__toggle-password"
                type="button"
                :aria-label="passwordVisible ? '隐藏密码' : '显示密码'"
                :aria-pressed="passwordVisible"
                @click="passwordVisible = !passwordVisible"
              >
                {{ passwordVisible ? '隐藏' : '显示' }}
              </button>
            </div>
          </div>

          <p v-if="noticeMessage" class="login-form__notice" role="status">{{ noticeMessage }}</p>
          <p v-if="devCodeHint" class="login-form__hint">
            Development 测试码：<strong>{{ devCodeHint }}</strong>（生产环境不返回）
          </p>
          <p v-if="errorMessage" class="login-form__error" role="alert">{{ errorMessage }}</p>

          <button class="login-form__submit" type="submit" :disabled="submitting">
            {{ submitting ? '处理中…' : submitLabel }}
          </button>
        </form>
      </div>
    </section>
  </main>
</template>

<style scoped>
.login-page {
  display: flex;
  min-height: 100%;
  background: var(--ec-bg-canvas);
}

/* 桌面端（≥1100px）：左侧品牌说明区 + 右侧登录卡片（计划 §5.7）。 */
.login-brand {
  display: none;
  flex: 1 1 0;
  align-items: center;
  justify-content: center;
  padding: var(--ec-space-8);
  background: var(--ec-chat-gradient);
  border-right: 1px solid var(--ec-border);
}

.login-brand__inner {
  width: 100%;
  max-width: 360px;
}

.login-brand__logo {
  display: grid;
  width: 48px;
  height: 48px;
  place-items: center;
  border-radius: var(--ec-radius-full);
  background: var(--ec-brand-600);
  color: var(--ec-text-inverse);
  font-size: 24px;
  font-weight: 700;
}

.login-brand__title {
  margin: var(--ec-space-4) 0 var(--ec-space-2);
  color: var(--ec-text-primary);
  font-size: 30px;
  font-weight: 700;
}

.login-brand__desc {
  margin: 0 0 var(--ec-space-5);
  color: var(--ec-text-secondary);
  font-size: var(--ec-font-size-body);
  line-height: 1.7;
}

.login-brand__list {
  display: flex;
  flex-direction: column;
  gap: var(--ec-space-2);
  margin: 0;
  padding: 0;
  list-style: none;
}

.login-brand__list li {
  position: relative;
  padding-left: var(--ec-space-4);
  color: var(--ec-text-secondary);
  font-size: var(--ec-font-size-secondary);
}

.login-brand__list li::before {
  position: absolute;
  top: 6px;
  left: 0;
  width: 6px;
  height: 6px;
  border-radius: var(--ec-radius-full);
  background: var(--ec-brand-600);
  content: '';
}

.login-panel {
  display: flex;
  flex: 1 1 0;
  align-items: center;
  justify-content: center;
  padding: var(--ec-space-6);
}

.login-card {
  width: 100%;
  max-width: 400px;
  padding: var(--ec-space-6);
  background: var(--ec-surface);
  border: 1px solid var(--ec-border);
  border-radius: var(--ec-radius-panel);
  box-shadow: var(--ec-shadow-md);
}

.login-card__title {
  margin: 0 0 var(--ec-space-1);
  color: var(--ec-text-primary);
  font-size: var(--ec-font-size-page-title);
  font-weight: var(--ec-font-weight-semibold);
}

.login-card__subtitle {
  margin: 0 0 var(--ec-space-5);
  color: var(--ec-text-secondary);
  font-size: var(--ec-font-size-secondary);
}

.login-server {
  margin-bottom: var(--ec-space-4);
}

.login-server__toggle {
  display: block;
  width: 100%;
  padding: var(--ec-space-2) var(--ec-space-3);
  overflow: hidden;
  border: 1px dashed var(--ec-border);
  border-radius: var(--ec-radius-input);
  background: var(--ec-surface-hover);
  color: var(--ec-text-secondary);
  font-size: var(--ec-font-size-small);
  text-align: left;
  text-overflow: ellipsis;
  white-space: nowrap;
  cursor: pointer;
}

.login-server__toggle:hover {
  border-color: var(--ec-brand-500);
  color: var(--ec-brand-600);
}

.login-server__locked {
  margin: 0;
  padding: var(--ec-space-2) var(--ec-space-3);
  border: 1px dashed var(--ec-border);
  border-radius: var(--ec-radius-input);
  background: var(--ec-surface-hover);
  color: var(--ec-text-secondary);
  font-size: var(--ec-font-size-small);
  word-break: break-all;
}

.login-server__panel {
  display: flex;
  flex-direction: column;
  gap: var(--ec-space-2);
  margin-top: var(--ec-space-2);
}

.login-server__panel input {
  width: 100%;
  padding: 8px var(--ec-space-3);
  border: 1px solid var(--ec-border);
  border-radius: var(--ec-radius-input);
  outline: none;
  font-size: var(--ec-font-size-secondary);
  box-sizing: border-box;
}

.login-server__panel input:focus {
  border-color: var(--ec-brand-600);
}

.login-server__actions {
  display: flex;
  gap: var(--ec-space-2);
}

.login-server__save,
.login-server__reset {
  padding: 6px var(--ec-space-3);
  border-radius: var(--ec-radius-sm);
  font-size: var(--ec-font-size-small);
  cursor: pointer;
}

.login-server__save {
  border: 1px solid var(--ec-brand-600);
  background: var(--ec-brand-050);
  color: var(--ec-brand-600);
}

.login-server__reset {
  border: 1px solid var(--ec-border);
  background: transparent;
  color: var(--ec-text-secondary);
}

.login-server__error {
  margin: 0;
  color: var(--ec-danger);
  font-size: var(--ec-font-size-small);
}

.login-modes {
  display: grid;
  grid-template-columns: repeat(3, minmax(0, 1fr));
  gap: 4px;
  margin-bottom: var(--ec-space-4);
  padding: 4px;
  border-radius: var(--ec-radius-input);
  background: var(--ec-nav-bg);
}

.login-modes__item {
  padding: 8px 4px;
  border: none;
  border-radius: var(--ec-radius-sm);
  background: transparent;
  color: var(--ec-text-secondary);
  font-size: var(--ec-font-size-secondary);
  cursor: pointer;
}

.login-modes__item--active {
  background: var(--ec-surface);
  color: var(--ec-brand-600);
  font-weight: var(--ec-font-weight-semibold);
  box-shadow: var(--ec-shadow-sm);
}

.login-form {
  display: flex;
  flex-direction: column;
  gap: var(--ec-space-3);
}

.login-form__field {
  display: flex;
  flex-direction: column;
  gap: 6px;
}

.login-form__label {
  color: var(--ec-text-secondary);
  font-size: var(--ec-font-size-small);
}

.login-form__field input {
  width: 100%;
  padding: 10px var(--ec-space-3);
  border: 1px solid var(--ec-border);
  border-radius: var(--ec-radius-input);
  outline: none;
  color: var(--ec-text-primary);
  font-size: var(--ec-font-size-body);
  box-sizing: border-box;
  transition: border-color var(--ec-transition-fast) var(--ec-transition-ease);
}

.login-form__field input::placeholder {
  color: var(--ec-text-tertiary);
}

.login-form__field input:focus {
  border-color: var(--ec-brand-600);
}

.login-form__code-row {
  display: flex;
  gap: var(--ec-space-2);
}

.login-form__code-row input {
  flex: 1;
  min-width: 0;
}

.login-form__send {
  flex: none;
  min-width: 108px;
  padding: 0 var(--ec-space-3);
  border: 1px solid var(--ec-brand-600);
  border-radius: var(--ec-radius-input);
  background: var(--ec-brand-050);
  color: var(--ec-brand-600);
  font-size: var(--ec-font-size-secondary);
  cursor: pointer;
}

.login-form__send:disabled {
  opacity: 0.6;
  cursor: not-allowed;
}

.login-form__password {
  position: relative;
}

.login-form__password input {
  padding-right: 64px;
}

.login-form__toggle-password {
  position: absolute;
  top: 50%;
  right: var(--ec-space-2);
  padding: 4px;
  border: none;
  border-radius: var(--ec-radius-sm);
  background: transparent;
  color: var(--ec-brand-600);
  font-size: var(--ec-font-size-small);
  transform: translateY(-50%);
  cursor: pointer;
}

.login-form__toggle-password:hover {
  background: var(--ec-brand-050);
}

.login-form__submit {
  margin-top: var(--ec-space-1);
  padding: 11px;
  border: none;
  border-radius: var(--ec-radius-input);
  background: var(--ec-brand-600);
  color: var(--ec-text-inverse);
  font-size: var(--ec-font-size-body);
  font-weight: var(--ec-font-weight-semibold);
  cursor: pointer;
}

.login-form__submit:hover:not(:disabled) {
  background: var(--ec-brand-700);
}

.login-form__submit:disabled {
  opacity: 0.65;
  cursor: not-allowed;
}

.login-form__notice {
  margin: 0;
  padding: var(--ec-space-2) var(--ec-space-3);
  border-radius: var(--ec-radius-input);
  background: var(--ec-brand-050);
  color: var(--ec-brand-600);
  font-size: var(--ec-font-size-small);
}

.login-form__hint {
  margin: 0;
  color: var(--ec-text-tertiary);
  font-size: var(--ec-font-size-small);
}

.login-form__error {
  margin: 0;
  color: var(--ec-danger);
  font-size: var(--ec-font-size-small);
}

/* 平板/窄屏（≤1099px）：隐藏品牌区，单栏居中卡片。 */
@media (max-width: 1099px) {
  .login-panel {
    padding: var(--ec-space-4);
  }
}

@media (min-width: 1100px) {
  .login-brand {
    display: flex;
  }

  .login-panel {
    flex: 0 0 46%;
    padding: var(--ec-space-8);
  }
}

@media (max-width: 480px) {
  .login-card {
    padding: var(--ec-space-5) var(--ec-space-4);
  }
}
</style>
