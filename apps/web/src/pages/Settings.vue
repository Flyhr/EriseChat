<script setup lang="ts">
import { onBeforeUnmount, ref } from 'vue'
import { RouterLink, useRouter } from 'vue-router'
import { authApi } from '../api/auth'
import AppDialog from '../components/AppDialog.vue'
import Avatar from '../components/Avatar.vue'
import ComingSoonHint from '../components/ComingSoonHint.vue'
import { chatWs, type WsState } from '../core/ws'
import { clearAllData } from '../core/db'
import { getServerUrl, resetServerUrl, setServerUrl } from '../core/config'
import { showToast } from '../core/toast'
import { APP_VERSION } from '../core/version'
import { useAuthStore } from '../stores/auth'
import { useConversationsStore } from '../stores/conversations'
import { useMessagesStore } from '../stores/messages'

// 设置中心（UI-03 批次3，计划 §5.10）：分组卡片 = 账号与安全 / 外观与主题 / 通知 / 连接与关于。
// 只展示真实可用的能力：未实现的通知、深色主题等一律 ComingSoonHint 灰显，不提供假开关、不发假请求。
const router = useRouter()
const auth = useAuthStore()
const conversations = useConversationsStore()
const messages = useMessagesStore()

const WS_STATE_LABELS: Record<WsState, string> = {
  idle: '未连接',
  connecting: '连接中…',
  authed: '已连接',
  reconnecting: '重连中…',
  closed: '已断开',
}

const wsState = ref<WsState>(chatWs.state)
let unsubscribeWs: (() => void) | null = null
unsubscribeWs = chatWs.on('statechange', (state) => {
  wsState.value = state
})
onBeforeUnmount(() => {
  unsubscribeWs?.()
})

const savedServerUrl = ref(getServerUrl())
const serverUrlInput = ref(savedServerUrl.value)
const serverMessage = ref('')
const savingServer = ref(false)
const currentServerLabel = ref(savedServerUrl.value || '与当前页面同源')

const logoutConfirmOpen = ref(false)
const loggingOut = ref(false)

/** 保存服务器地址：校验 → 持久化 → toast 提示 → 断开旧连接并按新地址重连。 */
function saveServer(): void {
  if (savingServer.value) return
  const value = serverUrlInput.value.trim()
  if (value.length > 0 && !setServerUrl(value)) {
    serverMessage.value = '地址无效：需以 http:// 或 https:// 开头'
    return
  }
  if (value.length === 0) {
    resetServerUrl()
  }
  savingServer.value = true
  serverMessage.value = ''
  savedServerUrl.value = getServerUrl()
  currentServerLabel.value = savedServerUrl.value || '与当前页面同源'
  serverUrlInput.value = savedServerUrl.value
  if (chatWs.state !== 'idle' && chatWs.state !== 'closed') {
    chatWs.disconnect(1000, 'server url changed')
  }
  chatWs.connect()
  savingServer.value = false
  showToast('服务器地址已保存，正在重新连接', 'success')
}

/** 退出登录：自绘弹窗二次确认 → 请求服务端注销（失败也继续）→ 断连清缓存回登录页。 */
async function confirmLogout(): Promise<void> {
  if (loggingOut.value) return
  loggingOut.value = true
  try {
    await authApi.logout()
  } catch {
    // token 已过期/网络异常时服务端会话可能已失效；本地清理照常进行。
  } finally {
    chatWs.disconnect(1000, 'logout')
    auth.clearSession()
    conversations.clear()
    messages.clear()
    void clearAllData()
    logoutConfirmOpen.value = false
    await router.replace({ name: 'login' })
    loggingOut.value = false
  }
}
</script>

<template>
  <main class="settings-page">
    <header class="settings-page__header">
      <h2 class="settings-page__title">设置</h2>
    </header>

    <section class="settings-card">
      <h3 class="settings-card__title">账号与安全</h3>
      <div class="settings-account">
        <Avatar :display-name="auth.user?.displayName ?? ''" :size="48" />
        <span class="settings-account__info">
          <span class="settings-account__name">{{ auth.user?.displayName ?? '未登录' }}</span>
          <span class="settings-account__email">{{ auth.user?.email }}</span>
        </span>
        <button
          type="button"
          class="settings-account__logout"
          :disabled="loggingOut"
          @click="logoutConfirmOpen = true"
        >
          {{ loggingOut ? '退出中…' : '退出登录' }}
        </button>
      </div>
    </section>

    <section class="settings-card">
      <h3 class="settings-card__title">外观与主题</h3>
      <div class="settings-theme">
        <div class="settings-theme__current" role="status">
          <span class="settings-theme__swatch" aria-hidden="true" />
          <span>浅色主题（当前）</span>
        </div>
        <p class="settings-row">
          <ComingSoonHint label="深色主题" layout="inline">深色主题</ComingSoonHint>
        </p>
        <p class="settings-row">
          <ComingSoonHint label="消息字号与密度" layout="inline">消息字号与密度</ComingSoonHint>
        </p>
      </div>
    </section>

    <section class="settings-card">
      <h3 class="settings-card__title">通知</h3>
      <p class="settings-row">
        <ComingSoonHint label="桌面通知" layout="inline">桌面通知</ComingSoonHint>
      </p>
      <p class="settings-row">
        <ComingSoonHint label="提示音" layout="inline">提示音</ComingSoonHint>
      </p>
      <p class="settings-card__hint">通知能力将在桌面客户端完善后提供，当前仅保留页面内提示。</p>
    </section>

    <section class="settings-card">
      <h3 class="settings-card__title">连接与关于</h3>
      <p class="settings-row">
        <span class="settings-row__label">连接状态</span>
        <span class="settings-row__value" role="status">{{ WS_STATE_LABELS[wsState] }}</span>
      </p>

      <div class="settings-server">
        <p class="settings-row__label">服务器地址</p>
        <p class="settings-server__current">当前：{{ currentServerLabel }}</p>
        <div class="settings-server__row">
          <input
            v-model="serverUrlInput"
            type="url"
            placeholder="https://chat.example.com 或 http://127.0.0.1:8080（留空 = 与页面同源）"
            aria-label="服务器地址"
          />
          <button type="button" :disabled="savingServer" @click="saveServer">保存</button>
        </div>
        <p v-if="serverMessage" class="settings-server__error" role="alert">{{ serverMessage }}</p>
        <p class="settings-card__hint">
          桌面 / APK 首次使用需填写服务器地址；保存后会断开旧连接并按新地址重连。
        </p>
      </div>

      <p class="settings-row">
        <span class="settings-row__label">版本</span>
        <span class="settings-row__value">{{ APP_VERSION }}</span>
      </p>
      <p class="settings-row">
        <RouterLink class="settings-row__link" to="/download">下载客户端</RouterLink>
      </p>
    </section>

    <AppDialog
      :open="logoutConfirmOpen"
      title="退出登录"
      message="退出后本机将清除登录态与本地缓存，需要重新登录才能继续聊天。"
      confirm-text="退出登录"
      tone="danger"
      :busy="loggingOut"
      @confirm="confirmLogout"
      @cancel="logoutConfirmOpen = false"
    />
  </main>
</template>

<style scoped>
.settings-page {
  height: 100%;
  padding: var(--ec-space-6);
  overflow-y: auto;
  background: var(--ec-bg-canvas);
}

.settings-page__header {
  margin-bottom: var(--ec-space-4);
}

.settings-page__title {
  margin: 0;
  color: var(--ec-text-primary);
  font-size: var(--ec-font-size-page-title);
  font-weight: var(--ec-font-weight-semibold);
}

.settings-card {
  display: flex;
  flex-direction: column;
  gap: var(--ec-space-2);
  max-width: 640px;
  margin-bottom: var(--ec-space-4);
  padding: var(--ec-space-4);
  background: var(--ec-surface);
  border: 1px solid var(--ec-border);
  border-radius: var(--ec-radius-panel);
  box-shadow: var(--ec-shadow-sm);
}

.settings-card__title {
  margin: 0 0 var(--ec-space-1);
  color: var(--ec-text-primary);
  font-size: var(--ec-font-size-body);
  font-weight: var(--ec-font-weight-semibold);
}

.settings-card__hint {
  margin: 0;
  color: var(--ec-text-tertiary);
  font-size: var(--ec-font-size-small);
}

.settings-account {
  display: flex;
  gap: var(--ec-space-3);
  align-items: center;
}

.settings-account__info {
  display: flex;
  flex: 1;
  flex-direction: column;
  min-width: 0;
}

.settings-account__name {
  color: var(--ec-text-primary);
  font-size: var(--ec-font-size-body);
  font-weight: var(--ec-font-weight-medium);
}

.settings-account__email {
  overflow: hidden;
  color: var(--ec-text-secondary);
  font-size: var(--ec-font-size-secondary);
  text-overflow: ellipsis;
  white-space: nowrap;
}

.settings-account__logout {
  flex: none;
  padding: 6px var(--ec-space-3);
  border: 1px solid var(--ec-danger);
  border-radius: var(--ec-radius-sm);
  background: transparent;
  color: var(--ec-danger);
  font-size: var(--ec-font-size-secondary);
  cursor: pointer;
}

.settings-account__logout:disabled {
  opacity: 0.6;
  cursor: not-allowed;
}

.settings-theme {
  display: flex;
  flex-direction: column;
  gap: var(--ec-space-2);
}

.settings-theme__current {
  display: inline-flex;
  gap: var(--ec-space-2);
  align-items: center;
  align-self: flex-start;
  padding: var(--ec-space-2) var(--ec-space-3);
  border: 1px solid var(--ec-brand-600);
  border-radius: var(--ec-radius-md);
  background: var(--ec-brand-050);
  color: var(--ec-brand-600);
  font-size: var(--ec-font-size-secondary);
}

.settings-theme__swatch {
  width: 14px;
  height: 14px;
  border: 1px solid var(--ec-brand-600);
  border-radius: var(--ec-radius-full);
  background: var(--ec-surface);
}

.settings-row {
  display: flex;
  gap: var(--ec-space-2);
  align-items: center;
  margin: 0;
  color: var(--ec-text-secondary);
  font-size: var(--ec-font-size-secondary);
}

.settings-row__label {
  flex: none;
  color: var(--ec-text-primary);
  font-size: var(--ec-font-size-secondary);
}

.settings-row__value {
  color: var(--ec-text-secondary);
}

.settings-row__link {
  color: var(--ec-brand-600);
  text-decoration: none;
  font-size: var(--ec-font-size-secondary);
}

.settings-row__link:hover {
  text-decoration: underline;
}

.settings-server {
  display: flex;
  flex-direction: column;
  gap: var(--ec-space-2);
  padding: var(--ec-space-3) 0;
  border-top: 1px solid var(--ec-border);
  border-bottom: 1px solid var(--ec-border);
}

.settings-server__current {
  margin: 0;
  color: var(--ec-text-secondary);
  font-size: var(--ec-font-size-secondary);
}

.settings-server__row {
  display: flex;
  gap: var(--ec-space-2);
}

.settings-server__row input {
  flex: 1;
  min-width: 0;
  padding: var(--ec-space-2) var(--ec-space-3);
  border: 1px solid var(--ec-border);
  border-radius: var(--ec-radius-input);
  outline: none;
  box-sizing: border-box;
}

.settings-server__row input:focus {
  border-color: var(--ec-brand-600);
}

.settings-server__row button {
  flex: none;
  padding: 0 var(--ec-space-4);
  border: 1px solid var(--ec-brand-600);
  border-radius: var(--ec-radius-input);
  background: var(--ec-brand-600);
  color: var(--ec-text-inverse);
  cursor: pointer;
}

.settings-server__row button:disabled {
  opacity: 0.6;
  cursor: not-allowed;
}

.settings-server__error {
  margin: 0;
  color: var(--ec-danger);
  font-size: var(--ec-font-size-small);
}
</style>
