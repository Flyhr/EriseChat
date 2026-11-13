<script setup lang="ts">
import { computed, onBeforeUnmount, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { authApi, type LoginResponse } from '../api/auth'
import { chatWs } from '../core/ws'
import {
  getServerUrl,
  isPackagedShell,
  matchServerPreset,
  normalizeServerUrl,
  probeServerHealth,
  resetServerUrl,
  serverPresets,
  setServerUrl,
} from '../core/config'
import { isConversationIdLike, syncServerUrlToDesktop } from '../core/desktop'
import { friendlyError } from '../core/errorMessages'
import { validateNewPassword } from '../core/passwordRules'
import { showToast } from '../core/toast'
import { useAuthStore } from '../stores/auth'
import { useConversationsStore } from '../stores/conversations'
import { useMessagesStore } from '../stores/messages'

// 登录/注册页（M2-03）：验证码注册 / 验证码登录 / 密码登录三种模式。
// F-13：新增「找回密码」reset 模式（不进顶部三页签，从密码登录页进入）。
// 成功后统一走 applySession：写登录态 → 注入 WS 认证 → 建连 → 进聊天页（protocol.md §4.1）。
type Mode = 'register' | 'code' | 'password' | 'reset'
type TabMode = 'register' | 'code' | 'password'

const MODE_LABELS: Record<TabMode, string> = {
  code: '验证码登录',
  register: '注册',
  password: '密码登录',
}
const modes = Object.keys(MODE_LABELS) as TabMode[]

/** 服务端错误码 → 中文文案统一由 core/errorMessages 维护（UI-03 批次1）。 */

const router = useRouter()
const route = useRoute()
const auth = useAuthStore()

/** 致命关闭（被踢/登录失效）跳转登录页时携带的提示（M3-00）。 */
const noticeMessage = ref(typeof route.query.notice === 'string' ? route.query.notice : '')

/** Electron/APK 打包态（file:// 或 Capacitor http://localhost）：登录页需提供首启服务器地址配置，Web 同源部署不显示。 */
const packagedShell = isPackagedShell()
/** 「服务器地址」区块：默认折叠，未配置时自动展开提示首次填写。 */
const serverEntryOpen = ref(packagedShell && getServerUrl() === '')
const serverUrlInput = ref(getServerUrl())
const serverConfigured = ref(getServerUrl().length > 0)
const serverEntryTone = ref<'info' | 'success' | 'error'>('info')
const serverEntryStatus = ref(serverConfigured.value ? '' : '首次使用请先填写服务器地址')
/**
 * 已保存的是回环地址时提示不能用：127.0.0.1/localhost 只在本机或 USB 端口映射（adb reverse）时有效，
 * 手机经 Tailscale/局域网访问必须填服务器的可达地址。
 */
const serverAddressIsLoopback = computed(() => {
  if (!packagedShell || !serverConfigured.value) return false
  try {
    const host = new URL(serverUrlInput.value).hostname
    return host === '127.0.0.1' || host === 'localhost' || host === '[::1]'
  } catch {
    return false
  }
})
const serverSaving = ref(false)
/**
 * F-31：服务器地址下拉预设（仅打包壳）——选中后立即保存并检测，便于「服务器 / 本机调试」快速切换；
 * 选「自定义」时保留手动输入，「保存并检测」按钮照旧可用。
 */
const serverPresetKey = ref('custom')

function syncServerPresetKey(): void {
  if (!packagedShell) return
  serverPresetKey.value = matchServerPreset(serverUrlInput.value)?.url ?? 'custom'
}

watch(serverUrlInput, syncServerPresetKey, { immediate: true })

async function onServerPresetChange(): Promise<void> {
  if (serverPresetKey.value === 'custom') return
  serverUrlInput.value = serverPresetKey.value
  await saveServerEntry()
}

/** UI-03 批次3：密码显示/隐藏（只影响输入框 type，不记录任何日志）。 */
const passwordVisible = ref(false)
/** F-13：找回密码的新密码显示/隐藏。 */
const resetPasswordVisible = ref(false)

const mode = ref<Mode>('code')
const email = ref('')
const code = ref('')
const displayName = ref('')
const password = ref('')
/** F-13：找回密码（reset 模式）的新密码与确认。 */
const newPassword = ref('')
const confirmPassword = ref('')
const submitting = ref(false)
const sending = ref(false)
const errorMessage = ref('')
const devCodeHint = ref('')
const cooldown = ref(0)
let cooldownTimer: ReturnType<typeof setInterval> | null = null

const purpose = computed<'register' | 'login' | 'reset'>(() =>
  mode.value === 'register' ? 'register' : mode.value === 'reset' ? 'reset' : 'login',
)
const submitLabel = computed(() =>
  mode.value === 'register' ? '注册并登录' : mode.value === 'reset' ? '重置密码' : '登录',
)
const canSubmit = computed(() => !submitting.value && email.value.trim().length > 0)
const sendButtonLabel = computed(() => {
  if (sending.value) return '发送中…'
  if (cooldown.value > 0) return `${cooldown.value}s 后重发`
  return '发送验证码'
})

function toErrorMessage(error: unknown): string {
  // 打包壳未配置/配错地址时网络失败：自动展开地址区块并引导检查（其余错误统一走 friendlyError）。
  if (packagedShell && error instanceof TypeError) {
    serverEntryOpen.value = true
    return '无法连接服务器：请先确认服务器地址'
  }
  return friendlyError(error)
}

/** 保存并检测服务器地址（仅打包壳）：先探测可达性（失败不阻断保存），再同步 localStorage 与桌面壳设置。 */
async function saveServerEntry(): Promise<void> {
  if (serverSaving.value) return
  const normalized = normalizeServerUrl(serverUrlInput.value)
  if (!normalized) {
    serverEntryTone.value = 'error'
    serverEntryStatus.value = '请输入合法的 http(s) 地址，例如 https://chat.example.com'
    return
  }
  serverSaving.value = true
  serverEntryTone.value = 'info'
  serverEntryStatus.value = '正在检测服务…'
  const probe = await probeServerHealth(normalized)
  const saved = setServerUrl(normalized)
  await syncServerUrlToDesktop(normalized)
  serverConfigured.value = saved
  serverUrlInput.value = normalized
  serverSaving.value = false
  if (probe.ok) {
    serverEntryTone.value = 'success'
    serverEntryStatus.value = `服务可达${probe.version ? `（v${probe.version}）` : ''}，已保存，请登录`
  } else {
    serverEntryTone.value = 'error'
    serverEntryStatus.value = `${probe.message}；地址已保存，可稍后重试`
  }
}

/** 清除服务器地址（仅打包壳）：回退同源/构建期默认，并同步清空桌面壳设置。 */
async function clearServerEntry(): Promise<void> {
  resetServerUrl()
  await syncServerUrlToDesktop('')
  serverUrlInput.value = ''
  serverConfigured.value = false
  serverEntryTone.value = 'info'
  serverEntryStatus.value = '首次使用请先填写服务器地址'
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
  if (next !== 'reset') {
    newPassword.value = ''
    confirmPassword.value = ''
  }
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

/** F-13：找回密码——邮箱 + 验证码 + 新密码，成功切回验证码登录。 */
async function submitReset(): Promise<void> {
  const target = email.value.trim()
  if (!target) {
    errorMessage.value = '请先输入邮箱'
    return
  }
  if (code.value.trim().length === 0) {
    errorMessage.value = '请输入验证码'
    return
  }
  const passwordError = validateNewPassword(newPassword.value)
  if (passwordError) {
    errorMessage.value = passwordError
    return
  }
  if (newPassword.value !== confirmPassword.value) {
    errorMessage.value = '两次输入的新密码不一致'
    return
  }

  submitting.value = true
  errorMessage.value = ''
  try {
    await authApi.resetPassword({
      email: target,
      code: code.value.trim(),
      newPassword: newPassword.value,
    })
    showToast('密码已重置，请使用新密码登录', 'success')
    newPassword.value = ''
    confirmPassword.value = ''
    code.value = ''
    switchMode('code')
    stopCooldown()
    cooldown.value = 0
  } catch (error) {
    errorMessage.value = toErrorMessage(error)
  } finally {
    submitting.value = false
  }
}

async function submit(): Promise<void> {
  if (!canSubmit.value) return
  if (mode.value === 'reset') {
    await submitReset()
    return
  }
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
    // 未登录直达会话链接时，守卫只留下格式合法的会话 ID；这里再次校验后按命名路由恢复目标。
    // 不接受任意重定向 URL；缺少有效目标时进消息首页。会话访问权仍由服务端校验。
    const targetConversationId = route.query.conversationId
    await router.push(
      isConversationIdLike(targetConversationId)
        ? { name: 'chat', params: { conversationId: targetConversationId } }
        : { name: 'chat' },
    )
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
</script>

<template>
  <main class="login-page">
    <section class="login-brand" aria-hidden="true">
      <div class="login-brand__inner">
        <span class="login-brand__logo">E</span>
        <h1 class="login-brand__title">EriseChat</h1>
      </div>
    </section>

    <section class="login-panel">
      <!-- 移动端/窄屏：品牌区放到卡片上方（≥1200 用左侧品牌说明区）。 -->
      <div class="login-mobile-brand" aria-hidden="true">
        <span class="login-mobile-brand__logo">E</span>
        <span class="login-mobile-brand__name">EriseChat</span>
      </div>
      <div class="login-card">
        <h2 class="login-card__title">
          {{ mode === 'reset' ? '找回密码' : mode === 'register' ? '创建账号' : '欢迎回来' }}
        </h2>

        <!-- 打包壳（桌面/APK）首启配置服务器地址；Web 同源部署不显示。 -->
        <div v-if="packagedShell" class="login-server">
          <button
            class="login-server__toggle"
            type="button"
            :aria-expanded="serverEntryOpen"
            @click="serverEntryOpen = !serverEntryOpen"
          >
            <span class="login-server__title">服务器地址</span>
            <span class="login-server__current">{{ serverConfigured ? serverUrlInput : '未配置' }}</span>
            <span class="login-server__action">{{ serverEntryOpen ? '收起' : '设置' }}</span>
          </button>
          <p v-if="serverAddressIsLoopback" class="login-server__hint" role="note">
            当前是本机回环地址，仅在本机 USB 调试（adb reverse）时可用；手机经 Tailscale/局域网访问时，
            请改填服务器的可达地址（如 <code>https://chat-server.&lt;tailnet&gt;.ts.net</code> 或
            <code>http://&lt;电脑IPv4&gt;:6580</code>）。
          </p>
          <div v-if="serverEntryOpen" class="login-server__panel">
            <label class="login-server__field" for="login-server-preset">
              <span class="login-form__label">快速选择</span>
              <select
                id="login-server-preset"
                v-model="serverPresetKey"
                class="login-server__select"
                @change="onServerPresetChange"
              >
                <option v-for="preset in serverPresets" :key="preset.url" :value="preset.url">
                  {{ preset.label }}
                </option>
                <option value="custom">自定义地址…</option>
              </select>
            </label>
            <label class="login-server__field" for="login-server-url">
              <span class="login-form__label">服务器地址</span>
              <input
                id="login-server-url"
                v-model="serverUrlInput"
                type="url"
                inputmode="url"
                autocomplete="url"
                placeholder="https://chat-server.xxx.ts.net"
                @keydown.enter.prevent="saveServerEntry"
              />
            </label>
            <p
              v-if="serverEntryStatus"
              class="login-server__status"
              :class="`login-server__status--${serverEntryTone}`"
              role="status"
            >
              {{ serverEntryStatus }}
            </p>
            <div class="login-server__actions">
              <button
                class="login-server__save"
                type="button"
                :disabled="serverSaving"
                @click="saveServerEntry"
              >
                {{ serverSaving ? '检测中…' : '保存并检测' }}
              </button>
              <button
                v-if="serverConfigured"
                class="login-server__clear"
                type="button"
                @click="clearServerEntry"
              >
                清除
              </button>
            </div>
          </div>
        </div>

        <div v-if="mode !== 'reset'" class="login-modes" role="tablist" aria-label="登录方式">
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

        <button
          v-else
          class="login-form__back"
          type="button"
          @click="switchMode('code')"
        >
          ← 返回登录
        </button>

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

          <div v-if="mode === 'password' || mode === 'register'" class="login-form__field">
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
            <button
              v-if="mode === 'password'"
              class="login-form__forgot"
              type="button"
              @click="switchMode('reset')"
            >
              忘记密码？
            </button>
          </div>

          <template v-if="mode === 'reset'">
            <div class="login-form__field">
              <label class="login-form__label" for="login-new-password">新密码（8~128 个字符）</label>
              <div class="login-form__password">
                <input
                  id="login-new-password"
                  v-model="newPassword"
                  :type="resetPasswordVisible ? 'text' : 'password'"
                  autocomplete="new-password"
                  minlength="8"
                  maxlength="128"
                  placeholder="8~128 个字符"
                />
                <button
                  class="login-form__toggle-password"
                  type="button"
                  :aria-label="resetPasswordVisible ? '隐藏密码' : '显示密码'"
                  :aria-pressed="resetPasswordVisible"
                  @click="resetPasswordVisible = !resetPasswordVisible"
                >
                  {{ resetPasswordVisible ? '隐藏' : '显示' }}
                </button>
              </div>
            </div>
            <div class="login-form__field">
              <label class="login-form__label" for="login-confirm-password">确认新密码</label>
              <input
                id="login-confirm-password"
                v-model="confirmPassword"
                :type="resetPasswordVisible ? 'text' : 'password'"
                autocomplete="new-password"
                maxlength="128"
                placeholder="再次输入新密码"
              />
            </div>
          </template>

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

/* 桌面端（≥1200px）：左侧品牌说明区 + 右侧登录卡片（计划 §5.7）。 */
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

.login-panel {
  display: flex;
  flex: 1 1 0;
  flex-direction: column;
  gap: var(--ec-space-4);
  align-items: center;
  justify-content: center;
  padding: var(--ec-space-6);
}

/* 移动端/窄屏品牌头（默认隐藏，≤1199 显示在卡片上方）。 */
.login-mobile-brand {
  display: none;
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

/* 打包壳首启服务器地址区块（默认折叠；未配置时自动展开）。 */
.login-server {
  margin: var(--ec-space-3) 0 var(--ec-space-4);
  overflow: hidden;
  background: var(--ec-nav-bg);
  border: 1px solid var(--ec-border);
  border-radius: var(--ec-radius-input);
}

.login-server__toggle {
  display: flex;
  gap: var(--ec-space-2);
  align-items: center;
  width: 100%;
  padding: 8px var(--ec-space-3);
  border: none;
  background: transparent;
  color: var(--ec-text-secondary);
  font-size: var(--ec-font-size-small);
  text-align: left;
  cursor: pointer;
}

.login-server__title {
  flex: none;
  font-weight: var(--ec-font-weight-semibold);
}

.login-server__current {
  flex: 1 1 auto;
  overflow: hidden;
  color: var(--ec-text-tertiary);
  text-overflow: ellipsis;
  white-space: nowrap;
}

.login-server__action {
  flex: none;
  color: var(--ec-brand-600);
}

.login-server__hint {
  margin: 0;
  padding: 0 var(--ec-space-3) var(--ec-space-2);
  color: var(--ec-text-secondary);
  font-size: var(--ec-font-size-small);
  line-height: 1.5;
}

.login-server__hint code {
  font-size: inherit;
  word-break: break-all;
}

.login-server__panel {
  display: flex;
  flex-direction: column;
  gap: var(--ec-space-2);
  padding: 0 var(--ec-space-3) var(--ec-space-3);
  border-top: 1px solid var(--ec-border);
}

.login-server__field {
  display: flex;
  flex-direction: column;
  gap: 6px;
  padding-top: var(--ec-space-3);
}

.login-server__field input {
  width: 100%;
  padding: 10px var(--ec-space-3);
  border: 1px solid var(--ec-border);
  border-radius: var(--ec-radius-input);
  outline: none;
  color: var(--ec-text-primary);
  font-size: var(--ec-font-size-body);
  box-sizing: border-box;
  transition: border-color var(--ec-transition-fast) var(--ec-ease);
}

.login-server__field input:focus {
  border-color: var(--ec-brand-600);
}

/* F-31：服务器地址下拉预设，外观与输入框一致。 */
.login-server__select {
  width: 100%;
  padding: 10px var(--ec-space-3);
  border: 1px solid var(--ec-border);
  border-radius: var(--ec-radius-input);
  outline: none;
  background: var(--ec-surface);
  color: var(--ec-text-primary);
  font-size: var(--ec-font-size-body);
  box-sizing: border-box;
  cursor: pointer;
}

.login-server__select:focus {
  border-color: var(--ec-brand-600);
}

.login-server__status {
  margin: 0;
  font-size: var(--ec-font-size-small);
  line-height: 1.5;
}

.login-server__status--info {
  color: var(--ec-text-secondary);
}

.login-server__status--success {
  color: var(--ec-success);
}

.login-server__status--error {
  color: var(--ec-danger);
}

.login-server__actions {
  display: flex;
  gap: var(--ec-space-2);
}

.login-server__save {
  padding: 8px var(--ec-space-4);
  border: 1px solid var(--ec-brand-600);
  border-radius: var(--ec-radius-input);
  background: var(--ec-brand-600);
  color: var(--ec-text-inverse);
  font-size: var(--ec-font-size-secondary);
  cursor: pointer;
}

.login-server__save:disabled {
  opacity: 0.6;
  cursor: not-allowed;
}

.login-server__clear {
  padding: 8px var(--ec-space-4);
  border: 1px solid var(--ec-border);
  border-radius: var(--ec-radius-input);
  background: var(--ec-surface);
  color: var(--ec-text-secondary);
  font-size: var(--ec-font-size-secondary);
  cursor: pointer;
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
  transition: border-color var(--ec-transition-fast) var(--ec-ease);
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

/* 悬停效果仅在鼠标精确指针设备生效，触屏不产生粘滞 hover。 */
@media (hover: hover) and (pointer: fine) {
.login-form__toggle-password:hover {
  background: var(--ec-brand-050);
}
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

/* 悬停效果仅在鼠标精确指针设备生效，触屏不产生粘滞 hover。 */
@media (hover: hover) and (pointer: fine) {
.login-form__submit:hover:not(:disabled) {
  background: var(--ec-brand-700);
}
}

.login-form__submit:disabled {
  opacity: 0.65;
  cursor: not-allowed;
}

/* F-13：找回密码模式——返回登录、忘记密码入口。 */
.login-form__back {
  align-self: flex-start;
  margin-bottom: var(--ec-space-2);
  padding: 0;
  border: none;
  background: transparent;
  color: var(--ec-text-secondary);
  font-size: var(--ec-font-size-secondary);
  cursor: pointer;
}

.login-form__forgot {
  align-self: flex-end;
  margin-top: calc(var(--ec-space-1) * -1);
  padding: 0;
  border: none;
  background: transparent;
  color: var(--ec-brand-600);
  font-size: var(--ec-font-size-small);
  cursor: pointer;
}

/* F-30：记住密码——本机保存账号密码，取消勾选即清除。 */
/* 悬停效果仅在鼠标精确指针设备生效，触屏不产生粘滞 hover。 */
@media (hover: hover) and (pointer: fine) {
  .login-form__back:hover,
  .login-form__forgot:hover {
    text-decoration: underline;
  }
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

/* 平板/窄屏（≤1199px）：隐藏品牌区，单栏居中卡片。 */
@media (max-width: 1199px) {
  .login-panel {
    padding: var(--ec-space-4);
  }

  .login-mobile-brand {
    display: flex;
    gap: var(--ec-space-2);
    align-items: center;
    justify-content: center;
  }

  .login-mobile-brand__logo {
    display: grid;
    width: 36px;
    height: 36px;
    place-items: center;
    border-radius: var(--ec-radius-md);
    background: var(--ec-brand-600);
    color: var(--ec-text-inverse);
    font-size: 18px;
    font-weight: var(--ec-font-weight-semibold);
  }

  .login-mobile-brand__name {
    color: var(--ec-text-primary);
    font-size: 18px;
    font-weight: var(--ec-font-weight-semibold);
    letter-spacing: 0.2px;
  }
}

@media (min-width: 1200px) {
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

/* UI-05 移动端登录页：卡片大圆角 + 16px 外边距、模式切换/输入/发送/登录按钮全部 ≥44px，
   让登录页在手机上呈现为一张居中的完整表单卡片。 */
@media (max-width: 800px) {
  .login-page {
    padding: var(--ec-space-4);
  }

  .login-card {
    width: 100%;
    padding: var(--ec-space-6) var(--ec-space-4);
    border-radius: var(--ec-radius-mobile-card);
    box-shadow: var(--ec-shadow-md);
  }

  .login-card__title {
    font-size: var(--ec-font-size-mobile-page-title);
  }

  .login-modes {
    gap: 2px;
  }

  .login-modes__item {
    min-height: var(--ec-touch-target);
    font-size: var(--ec-font-size-mobile-list-sub);
  }

  .login-form__field input {
    min-height: var(--ec-touch-target);
    border-radius: var(--ec-radius-mobile-input);
  }

  .login-form__send {
    min-height: var(--ec-touch-target);
    border-radius: var(--ec-radius-mobile-input);
  }

  .login-form__toggle-password {
    min-width: var(--ec-touch-target);
    min-height: var(--ec-touch-target);
  }

  .login-form__submit {
    min-height: 48px;
    border-radius: var(--ec-radius-mobile-input);
    font-size: var(--ec-font-size-body);
  }
}
</style>
