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
import { ensureNotificationPermission, playIncomingSound } from '../core/notify'
import { useAuthStore } from '../stores/auth'
import { useConversationsStore } from '../stores/conversations'
import { useMessagesStore } from '../stores/messages'
import { useSettingsStore } from '../stores/settings'

// 设置中心（UI-03 批次3，计划 §5.10）：分组卡片 = 账号与安全 / 外观与主题 / 通知 / 连接与关于。
// 只展示真实可用的能力：未实现的通知、深色主题等一律 ComingSoonHint 灰显，不提供假开关、不发假请求。
const router = useRouter()
const auth = useAuthStore()
const conversations = useConversationsStore()
const messages = useMessagesStore()
const settings = useSettingsStore()
/** 桌面通知开关请求进行中（防止重复点击/重复申请权限）。 */
const notificationBusy = ref(false)

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

/** 是否运行在桌面壳：Electron preload 提供原生通知，不依赖浏览器通知授权。 */
function isDesktopShell(): boolean {
  return (
    typeof window !== 'undefined' &&
    Boolean((window as Window & { eisechatDesktop?: unknown }).eisechatDesktop)
  )
}

/** 切换桌面通知：Web 版先申请浏览器通知权限；被拒绝/不支持时提示且保持关闭。 */
async function toggleDesktopNotifications(): Promise<void> {
  if (notificationBusy.value) return
  if (settings.desktopNotificationsEnabled) {
    settings.setDesktopNotificationsEnabled(false)
    return
  }
  notificationBusy.value = true
  try {
    if (!isDesktopShell()) {
      const permission = await ensureNotificationPermission()
      if (permission !== 'granted') {
        showToast(
          permission === 'denied'
            ? '浏览器已拒绝通知权限，请在站点设置中允许后重试'
            : '当前浏览器不支持系统通知',
          'error',
        )
        return
      }
    }
    settings.setDesktopNotificationsEnabled(true)
    showToast('桌面通知已开启', 'success')
  } finally {
    notificationBusy.value = false
  }
}

/** 切换提示音（即时生效并持久化）。 */
function toggleSound(): void {
  settings.setSoundEnabled(!settings.soundEnabled)
}

/** 试听提示音：必须在用户点击回调里调用，满足浏览器自动播放策略。 */
function previewSound(): void {
  playIncomingSound()
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
      <div class="settings-toggle">
        <span class="settings-toggle__text">
          <span class="settings-toggle__label">桌面通知</span>
          <span class="settings-toggle__hint">窗口最小化或不在前台时，弹出新消息提醒</span>
        </span>
        <button
          type="button"
          role="switch"
          class="settings-switch"
          :class="{ 'settings-switch--on': settings.desktopNotificationsEnabled }"
          :aria-checked="settings.desktopNotificationsEnabled"
          :aria-label="settings.desktopNotificationsEnabled ? '关闭桌面通知' : '开启桌面通知'"
          :disabled="notificationBusy"
          @click="toggleDesktopNotifications"
        >
          <span class="settings-switch__thumb" aria-hidden="true" />
        </button>
      </div>
      <div class="settings-toggle">
        <span class="settings-toggle__text">
          <span class="settings-toggle__label">提示音</span>
          <span class="settings-toggle__hint">收到新消息且当前不在该会话时播放短提示音</span>
        </span>
        <span class="settings-toggle__actions">
          <button type="button" class="settings-link-button" @click="previewSound">试听</button>
          <button
            type="button"
            role="switch"
            class="settings-switch"
            :class="{ 'settings-switch--on': settings.soundEnabled }"
            :aria-checked="settings.soundEnabled"
            :aria-label="settings.soundEnabled ? '关闭提示音' : '开启提示音'"
            @click="toggleSound"
          >
            <span class="settings-switch__thumb" aria-hidden="true" />
          </button>
        </span>
      </div>
      <p class="settings-card__hint">
        设置只保存在本机（localStorage），不同步到其他设备；清除浏览器数据后恢复默认值。
      </p>
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

/* 悬停效果仅在鼠标精确指针设备生效，触屏不产生粘滞 hover。 */
@media (hover: hover) and (pointer: fine) {
.settings-row__link:hover {
  text-decoration: underline;
}
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

/* UI-05 移动端设置页：页面标题 22px、卡片通栏大圆角、行高 44px 便于点按，
   服务器地址输入/保存按钮同步到 44px。 */
@media (max-width: 800px) {
  .settings-page {
    padding: var(--ec-space-2) var(--ec-space-4) var(--ec-space-6);
  }

  .settings-page__header {
    margin: var(--ec-space-2) 0 var(--ec-space-3);
  }

  .settings-page__title {
    font-size: var(--ec-font-size-mobile-page-title);
  }

  .settings-card {
    max-width: none;
    gap: var(--ec-space-1);
    margin-bottom: var(--ec-space-3);
    padding: var(--ec-space-4);
    border-radius: var(--ec-radius-mobile-card);
  }

  .settings-card__title {
    margin-bottom: var(--ec-space-2);
    font-size: var(--ec-font-size-mobile-list-sub);
  }

  .settings-account {
    gap: var(--ec-space-2);
  }

  .settings-account__logout {
    min-height: 40px;
    padding: 0 var(--ec-space-3);
    border-radius: var(--ec-radius-full);
  }

  .settings-theme__current {
    min-height: 40px;
    border-radius: var(--ec-radius-full);
  }

  .settings-row {
    gap: var(--ec-space-2);
    min-height: var(--ec-touch-target);
  }

  .settings-row__link {
    display: inline-flex;
    align-items: center;
    min-height: var(--ec-touch-target);
  }

  .settings-server {
    padding: var(--ec-space-3) 0;
  }

  .settings-server__row input {
    min-height: var(--ec-touch-target);
    border-radius: var(--ec-radius-mobile-input);
  }

  .settings-server__row button {
    min-height: var(--ec-touch-target);
    border-radius: var(--ec-radius-mobile-input);
  }
}

/* 通知开关（F-01）：自绘 switch；开启态用品牌色，关闭态用边框色，减小动态效果偏好时禁用过渡。 */
.settings-toggle {
  display: flex;
  gap: var(--ec-space-3);
  align-items: center;
  justify-content: space-between;
  padding: var(--ec-space-2) 0;
}

.settings-toggle__text {
  display: flex;
  flex-direction: column;
  gap: 2px;
  min-width: 0;
}

.settings-toggle__label {
  color: var(--ec-text-primary);
  font-size: var(--ec-font-size-secondary);
}

.settings-toggle__hint {
  color: var(--ec-text-secondary);
  font-size: var(--ec-font-size-small);
  line-height: 1.4;
}

.settings-toggle__actions {
  display: flex;
  gap: var(--ec-space-2);
  align-items: center;
  flex: none;
}

.settings-link-button {
  min-height: 32px;
  padding: 0 var(--ec-space-2);
  border: none;
  border-radius: var(--ec-radius-full);
  background: transparent;
  color: var(--ec-brand-600);
  font-size: var(--ec-font-size-small);
  cursor: pointer;
}

.settings-switch {
  position: relative;
  flex: none;
  width: 44px;
  height: 24px;
  padding: 0;
  border: none;
  border-radius: var(--ec-radius-full);
  background: var(--ec-border);
  cursor: pointer;
  transition: background-color 150ms ease;
}

.settings-switch--on {
  background: var(--ec-brand-600);
}

.settings-switch__thumb {
  position: absolute;
  top: 2px;
  left: 2px;
  width: 20px;
  height: 20px;
  border-radius: 50%;
  background: var(--ec-surface);
  box-shadow: 0 1px 2px rgb(15 23 42 / 18%);
  transition: transform 150ms ease;
}

.settings-switch--on .settings-switch__thumb {
  transform: translateX(20px);
}

.settings-switch:focus-visible {
  outline: 2px solid var(--ec-brand-600);
  outline-offset: 2px;
}

.settings-switch:disabled {
  opacity: 0.6;
  cursor: not-allowed;
}

@media (prefers-reduced-motion: reduce) {
  .settings-switch,
  .settings-switch__thumb {
    transition: none;
  }
}

@media (max-width: 800px) {
  .settings-toggle {
    min-height: var(--ec-touch-target);
  }

  .settings-link-button {
    min-height: var(--ec-touch-target);
  }
}
</style>
