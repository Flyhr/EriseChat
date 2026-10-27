<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, ref } from 'vue'
import { RouterLink, useRouter } from 'vue-router'
import { authApi } from '../api/auth'
import { filesApi } from '../api/files'
import AppDialog from '../components/AppDialog.vue'
import AppIcon from '../components/AppIcon.vue'
import Avatar from '../components/Avatar.vue'
import ComingSoonHint from '../components/ComingSoonHint.vue'
import SettingsGroup from '../components/settings/SettingsGroup.vue'
import SettingsRow from '../components/settings/SettingsRow.vue'
import SettingsSwitch from '../components/settings/SettingsSwitch.vue'
import type { AppIconName } from '../core/icons'
import { chatWs, type WsState } from '../core/ws'
import { clearAllData } from '../core/db'
import { getServerUrl, resetServerUrl, setServerUrl } from '../core/config'
import { friendlyError } from '../core/errorMessages'
import { compressImageFile } from '../core/imageCompress'
import { showToast } from '../core/toast'
import { APP_VERSION } from '../core/version'
import { ensureNotificationPermission, playIncomingSound } from '../core/notify'
import { useAuthStore } from '../stores/auth'
import { useConversationsStore } from '../stores/conversations'
import { useMessagesStore } from '../stores/messages'
import { useSettingsStore } from '../stores/settings'

// 设置中心（SET-01 重构）：按「个人资料 / 账号与安全 / 通知与提醒 / 外观 / 连接与关于」分节。
// 桌面为左侧分类导航 + 右侧内容面板；移动端为分组列表 → 进入分节详情（带返回），
// 与 Slack / Telegram / 微信 / iOS 设置的分组列表形态一致。只展示真实可用的能力：
// 未上线的深色主题、消息密度等一律 ComingSoonHint 灰显，不提供假开关、不发假请求。
const router = useRouter()
const auth = useAuthStore()
const conversations = useConversationsStore()
const messages = useMessagesStore()
const settings = useSettingsStore()

type SectionId = 'profile' | 'account' | 'notifications' | 'appearance' | 'connection'

interface SettingsSection {
  id: SectionId
  label: string
  title: string
  description: string
  icon: AppIconName
}

// 分类元数据是导航、移动端列表与内容标题的唯一来源：增删分类只改这一处。
const SECTIONS: SettingsSection[] = [
  {
    id: 'profile',
    label: '个人资料',
    title: '个人资料',
    description: '头像和昵称会显示在你的消息、会话列表与成员列表中。',
    icon: 'user',
  },
  {
    id: 'account',
    label: '账号与安全',
    title: '账号与安全',
    description: '查看当前登录的邮箱账号，退出当前设备上的登录。',
    icon: 'shield',
  },
  {
    id: 'notifications',
    label: '通知与提醒',
    title: '通知与提醒',
    description: '控制新消息的提示方式。',    icon: 'bell',
  },
  {
    id: 'appearance',
    label: '外观',
    title: '外观',
    description: '主题与消息展示样式。',
    icon: 'palette',
  },
  {
    id: 'connection',
    label: '连接与关于',
    title: '连接与关于',
    description: '服务器连接、客户端版本与下载入口。',
    icon: 'link',
  },
]

const WS_STATE_LABELS: Record<WsState, string> = {
  idle: '未连接',
  connecting: '连接中…',
  authed: '已连接',
  reconnecting: '重连中…',
  closed: '已断开',
}

/** ≤800 首次进入显示分类列表（移动端设置首页），桌面默认选中个人资料；不做 resize 布局监听。 */
const isNarrowAtMount =
  typeof window !== 'undefined' && window.matchMedia('(max-width: 800px)').matches
const activeSectionId = ref<SectionId | null>(isNarrowAtMount ? null : 'profile')
const currentSection = computed<SettingsSection>(
  () => SECTIONS.find((section) => section.id === activeSectionId.value) ?? SECTIONS[0],
)
const contentEl = ref<HTMLElement | null>(null)

function openSection(id: SectionId): void {
  activeSectionId.value = id
  void nextTick(() => contentEl.value?.scrollTo({ top: 0 }))
}

function goBackToIndex(): void {
  activeSectionId.value = null
}

/** 移动端分类列表的右侧摘要：只展示真实状态，不展示未保存的编辑值。 */
function sectionSummaryOf(id: SectionId): string {
  switch (id) {
    case 'profile':
      return '头像与昵称'
    case 'account':
      return auth.user?.email ?? '未登录'
    case 'notifications':
      return `桌面通知${settings.desktopNotificationsEnabled ? '开' : '关'} · 提示音${settings.soundEnabled ? '开' : '关'}`
    case 'appearance':
      return '浅色主题'
    case 'connection':
      return savedServerUrl.value || '与页面同源'
  }
}

// 桌面通知开关请求进行中（防止重复点击/重复申请权限）。
const notificationBusy = ref(false)

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

/** 断线后手动重连（与全局 ConnectionBanner 的「重试」能力一致）。 */
function reconnect(): void {
  chatWs.connect()
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

// F-12：个人资料（头像 + 昵称）。头像先压缩（最长边 512、≤200KB）再上传，暂存 fileId；
// 「保存资料」一次 PATCH 提交变更（昵称/头像任一变化），成功即刷新本机登录用户与会话成员展示。
const PROFILE_AVATAR_MAX_EDGE = 512
const PROFILE_AVATAR_MAX_BYTES = 200 * 1024
const displayNameInput = ref(auth.user?.displayName ?? '')
const avatarFileIdInput = ref<string | null>(auth.user?.avatarFileId ?? null)
const avatarUploading = ref(false)
const profileSaving = ref(false)
const profileError = ref('')
const avatarInputEl = ref<HTMLInputElement | null>(null)

const displayNameLength = ref(displayNameInput.value.trim().length)
/** 是否有未保存的资料改动（昵称 trim 后比较，头像按 fileId 比较）。 */
const profileDirty = ref(false)

function refreshProfileDirty(): void {
  const nameChanged = displayNameInput.value.trim() !== (auth.user?.displayName ?? '')
  const avatarChanged = (avatarFileIdInput.value ?? null) !== (auth.user?.avatarFileId ?? null)
  profileDirty.value = nameChanged || avatarChanged
}

displayNameInput.value = auth.user?.displayName ?? ''
avatarFileIdInput.value = auth.user?.avatarFileId ?? null
displayNameLength.value = displayNameInput.value.trim().length

function onDisplayNameInput(): void {
  displayNameLength.value = displayNameInput.value.trim().length
  refreshProfileDirty()
}

function triggerAvatarPick(): void {
  if (avatarUploading.value || profileSaving.value) return
  avatarInputEl.value?.click()
}

/** 选择头像：校验图片类型 → 压缩 → 上传 → 暂存 fileId（点「保存资料」才写回服务端）。 */
async function onAvatarPicked(event: Event): Promise<void> {
  const input = event.target as HTMLInputElement
  const file = input.files?.[0]
  input.value = ''
  if (!file) return
  if (!file.type.startsWith('image/')) {
    showToast('头像仅支持图片文件（png/jpeg/gif/webp）', 'error')
    return
  }

  avatarUploading.value = true
  profileError.value = ''
  try {
    const compressed = await compressImageFile(file, {
      maxEdge: PROFILE_AVATAR_MAX_EDGE,
      targetBytes: PROFILE_AVATAR_MAX_BYTES,
    })
    const uploaded = await filesApi.upload(compressed)
    avatarFileIdInput.value = uploaded.fileId
    refreshProfileDirty()
    showToast('头像已上传，点击「保存资料」生效', 'info')
  } catch (error) {
    profileError.value = friendlyError(error)
  } finally {
    avatarUploading.value = false
  }
}

/** 保存资料：按变更字段组请求（两项都无变化时不发请求）。 */
async function saveProfile(): Promise<void> {
  if (profileSaving.value || avatarUploading.value) return
  const trimmedName = displayNameInput.value.trim()
  if (trimmedName.length < 1 || trimmedName.length > 32) {
    profileError.value = '昵称长度应为 1~32 个字符'
    return
  }

  const patch: { displayName?: string; avatarFileId?: string } = {}
  if (trimmedName !== (auth.user?.displayName ?? '')) patch.displayName = trimmedName
  const nextAvatarFileId = avatarFileIdInput.value ?? undefined
  if (nextAvatarFileId !== (auth.user?.avatarFileId ?? undefined)) {
    if (nextAvatarFileId) patch.avatarFileId = nextAvatarFileId
  }
  if (patch.displayName === undefined && patch.avatarFileId === undefined) {
    profileDirty.value = false
    return
  }

  profileSaving.value = true
  profileError.value = ''
  try {
    const profile = await authApi.updateProfile(patch)
    auth.applyProfile({
      userId: profile.userId,
      email: profile.email,
      displayName: profile.displayName,
      avatarFileId: profile.avatarFileId,
      role: profile.role,
    })
    conversations.patchSelfProfile({
      displayName: profile.displayName,
      avatarFileId: profile.avatarFileId,
    })
    displayNameInput.value = profile.displayName
    avatarFileIdInput.value = profile.avatarFileId ?? null
    displayNameLength.value = profile.displayName.trim().length
    refreshProfileDirty()
    showToast('资料已更新', 'success')
  } catch (error) {
    profileError.value = friendlyError(error)
  } finally {
    profileSaving.value = false
  }
}
</script>

<template>
  <main
    class="settings-page"
    :class="{ 'settings-page--detail': activeSectionId !== null }"
  >
    <!-- 移动端首页标题栏（桌面隐藏；进入分节详情后由内容头部的返回栏替代） -->
    <header class="settings-mobile-bar">
      <h1 class="settings-mobile-bar__title">设置</h1>
      <p class="settings-mobile-bar__subtitle">账号、通知与连接</p>
    </header>

    <div class="settings-layout">
      <!-- 桌面：分类导航 -->
      <aside class="settings-nav">
        <div class="settings-nav__head">
          <h1 class="settings-nav__heading">设置</h1>
        </div>
        <nav class="settings-nav__items" aria-label="设置分类">
          <button
            v-for="section in SECTIONS"
            :key="section.id"
            type="button"
            class="settings-nav__item"
            :class="{ 'settings-nav__item--active': section.id === activeSectionId }"
            :aria-current="section.id === activeSectionId ? 'page' : undefined"
            @click="openSection(section.id)"
          >
            <AppIcon :name="section.icon" :size="18" />
            <span class="settings-nav__label">{{ section.label }}</span>
          </button>
        </nav>
        <div class="settings-nav__foot">
          <span>EiseChat {{ APP_VERSION }}</span>
          <RouterLink class="settings-nav__link" to="/download">下载客户端</RouterLink>
        </div>
      </aside>

      <!-- 移动端：分组分类列表（点击进入分节详情） -->
      <section class="settings-index" aria-label="设置分类">
        <p class="settings-index__eyebrow">账号</p>
        <button type="button" class="settings-index__account" @click="openSection('account')">
          <Avatar
            :display-name="auth.user?.displayName ?? ''"
            :avatar-file-id="auth.user?.avatarFileId"
            :size="52"
          />
          <span class="settings-index__account-text">
            <span class="settings-index__account-name">{{ auth.user?.displayName ?? '未登录' }}</span>
            <span class="settings-index__account-email">{{ auth.user?.email }}</span>
          </span>
          <AppIcon class="settings-index__chevron" name="back" :size="18" />
        </button>

        <p class="settings-index__eyebrow">设置</p>
        <nav class="settings-index__group" aria-label="设置项">
          <button
            v-for="section in SECTIONS"
            :key="section.id"
            type="button"
            class="settings-index__item"
            @click="openSection(section.id)"
          >
            <span class="settings-index__icon" aria-hidden="true">
              <AppIcon :name="section.icon" :size="18" />
            </span>
            <span class="settings-index__item-text">
              <span class="settings-index__label">{{ section.label }}</span>
              <span class="settings-index__value">{{ sectionSummaryOf(section.id) }}</span>
            </span>
            <AppIcon class="settings-index__chevron" name="back" :size="18" />
          </button>
        </nav>

        <p class="settings-index__foot">
          版本 {{ APP_VERSION }} ·
          <RouterLink class="settings-nav__link" to="/download">下载客户端</RouterLink>
        </p>
      </section>

      <!-- 桌面右侧 / 移动端详情：当前分节内容 -->
      <section ref="contentEl" class="settings-content">
        <header class="settings-content__header">
          <button
            type="button"
            class="settings-content__back"
            aria-label="返回设置列表"
            @click="goBackToIndex"
          >
            <AppIcon name="back" :size="20" />
          </button>
          <div class="settings-content__heading">
            <h2 class="settings-content__title">{{ currentSection.title }}</h2>
            <p class="settings-content__desc">{{ currentSection.description }}</p>
          </div>
        </header>

        <!-- 个人资料 -->
        <div v-if="activeSectionId === 'profile'" class="settings-content__body">
          <SettingsGroup title="资料">
            <SettingsRow label="头像" hint="显示在消息、会话列表与成员列表">
              <Avatar
                :display-name="displayNameInput || auth.user?.displayName || ''"
                :avatar-file-id="avatarFileIdInput ?? undefined"
                :size="40"
              />
              <button
                type="button"
                class="settings-btn settings-btn--secondary"
                :disabled="avatarUploading || profileSaving"
                @click="triggerAvatarPick"
              >
                {{ avatarUploading ? '上传中…' : '更换头像' }}
              </button>
              <input
                ref="avatarInputEl"
                type="file"
                class="settings-avatar-input"
                accept="image/png,image/jpeg,image/gif,image/webp"
                aria-label="选择头像图片"
                @change="onAvatarPicked"
              />
            </SettingsRow>
            <SettingsRow label="昵称" hint="1~32 个字符，显示给会话中的其他成员" vertical>
              <div class="settings-field">
                <input
                  v-model="displayNameInput"
                  class="settings-input"
                  type="text"
                  maxlength="32"
                  placeholder="1~32 个字符"
                  aria-label="昵称"
                  @input="onDisplayNameInput"
                />
                <span class="settings-field__count">{{ displayNameLength }}/32</span>
              </div>
              <p v-if="profileError" class="settings-field__error" role="alert">{{ profileError }}</p>
            </SettingsRow>
          </SettingsGroup>

          <div class="settings-actions">
            <span v-if="profileDirty" class="settings-actions__hint" role="status">有未保存的修改</span>
            <button
              type="button"
              class="settings-btn settings-btn--primary"
              :disabled="!profileDirty || profileSaving || avatarUploading"
              @click="saveProfile"
            >
              {{ profileSaving ? '保存中…' : '保存资料' }}
            </button>
          </div>
        </div>

        <!-- 账号与安全 -->
        <div v-else-if="activeSectionId === 'account'" class="settings-content__body">
          <SettingsGroup title="当前账号">
            <div class="settings-account">
              <Avatar
                :display-name="auth.user?.displayName ?? ''"
                :avatar-file-id="auth.user?.avatarFileId"
                :size="48"
              />
              <span class="settings-account__text">
                <span class="settings-account__name">{{ auth.user?.displayName ?? '未登录' }}</span>
                <span class="settings-account__email">{{ auth.user?.email }}</span>
              </span>
            </div>
          </SettingsGroup>

          <SettingsGroup title="账号信息">
            <SettingsRow label="邮箱" hint="用于接收验证码登录">
              <span class="settings-value">{{ auth.user?.email }}</span>
            </SettingsRow>
            <SettingsRow label="账号角色">
              <span class="settings-value">{{ auth.user?.role === 'admin' ? '管理员' : '成员' }}</span>
            </SettingsRow>
          </SettingsGroup>

          <SettingsGroup title="登录" description="同一账号同一时间只保留一个活动连接；在新设备登录会替代旧连接。">
            <SettingsRow label="退出登录" hint="退出后本机将清除登录态与本地缓存" tone="danger">
              <button
                type="button"
                class="settings-btn settings-btn--danger"
                :disabled="loggingOut"
                @click="logoutConfirmOpen = true"
              >
                {{ loggingOut ? '退出中…' : '退出登录' }}
              </button>
            </SettingsRow>
          </SettingsGroup>
        </div>

        <!-- 通知与提醒 -->
        <div v-else-if="activeSectionId === 'notifications'" class="settings-content__body">
          <SettingsGroup title="消息提醒">
            <SettingsRow
              label="桌面通知"
              :hint="
                isDesktopShell()
                  ? '窗口最小化或不在前台时，通过系统通知提醒新消息'
                  : '需要在浏览器中允许通知权限'
              "
            >
              <SettingsSwitch
                :model-value="settings.desktopNotificationsEnabled"
                label="桌面通知"
                :disabled="notificationBusy"
                @update:model-value="toggleDesktopNotifications"
              />
            </SettingsRow>
            <SettingsRow label="提示音" hint="收到新消息且当前不在该会话时播放短提示音">
              <button type="button" class="settings-text-btn" @click="previewSound">试听</button>
              <SettingsSwitch
                :model-value="settings.soundEnabled"
                label="提示音"
                @update:model-value="toggleSound"
              />
            </SettingsRow>
          </SettingsGroup>
          <p class="settings-note">
            设置只保存在本机（localStorage），不同步到其他设备；清除浏览器数据后恢复默认值。
          </p>
        </div>

        <!-- 外观 -->
        <div v-else-if="activeSectionId === 'appearance'" class="settings-content__body">
          <SettingsGroup title="主题">
            <div class="settings-themes">
              <div class="settings-theme settings-theme--active" role="status">
                <span class="settings-theme__preview settings-theme__preview--light" aria-hidden="true" />
                <span class="settings-theme__meta">
                  <span class="settings-theme__name">浅色（当前）</span>
                  <span class="settings-theme__hint">澄蓝聊天主题</span>
                </span>
                <AppIcon class="settings-theme__check" name="check" :size="18" />
              </div>
              <div class="settings-theme settings-theme--disabled" aria-disabled="true">
                <span class="settings-theme__preview settings-theme__preview--dark" aria-hidden="true" />
                <span class="settings-theme__meta">
                  <span class="settings-theme__name">深色</span>
                  <span class="settings-theme__badge">即将上线</span>
                </span>
              </div>
            </div>
            <SettingsRow label="消息字号与密度" hint="默认标准字号与行距" vertical>
              <ComingSoonHint label="消息字号与密度">标准</ComingSoonHint>
            </SettingsRow>
          </SettingsGroup>
        </div>

        <!-- 连接与关于 -->
        <div v-else class="settings-content__body">
          <SettingsGroup title="连接">
            <SettingsRow label="连接状态" hint="消息通过 WebSocket 实时收发">
              <span class="settings-conn" :class="`settings-conn--${wsState}`">
                <span class="settings-conn__dot" aria-hidden="true" />
                <span role="status">{{ WS_STATE_LABELS[wsState] }}</span>
              </span>
              <button
                v-if="wsState !== 'authed'"
                type="button"
                class="settings-text-btn"
                @click="reconnect"
              >
                重新连接
              </button>
            </SettingsRow>
            <SettingsRow
              label="服务器地址"
              hint="桌面端与 Android 端首次使用时需填写；Web 端留空表示与页面同源"
              vertical
            >
              <div class="settings-field">
                <input
                  v-model="serverUrlInput"
                  class="settings-input"
                  type="url"
                  placeholder="https://chat.example.com 或 http://127.0.0.1:8080"
                  aria-label="服务器地址"
                />
                <button
                  type="button"
                  class="settings-btn settings-btn--primary"
                  :disabled="savingServer"
                  @click="saveServer"
                >
                  保存
                </button>
              </div>
              <p class="settings-field__hint">当前：{{ currentServerLabel }}</p>
              <p v-if="serverMessage" class="settings-field__error" role="alert">{{ serverMessage }}</p>
            </SettingsRow>
          </SettingsGroup>

          <SettingsGroup title="关于">
            <SettingsRow label="版本" :hint="isDesktopShell() ? 'Windows 桌面客户端' : 'Web 客户端'">
              <span class="settings-value">{{ APP_VERSION }}</span>
            </SettingsRow>
            <SettingsRow label="下载客户端" hint="Windows 安装包与 Android APK">
              <RouterLink class="settings-link" to="/download">前往下载页</RouterLink>
            </SettingsRow>
          </SettingsGroup>
        </div>
      </section>
    </div>

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
/* ==================== 页面骨架 ====================
   桌面：左侧 232px 分类导航 + 右侧内容（自身滚动）；
   移动 ≤800：分类列表 → 分节详情（.settings-page--detail 切换显示）。 */
.settings-page {
  display: flex;
  flex-direction: column;
  height: 100%;
  min-height: 0;
  background: var(--ec-bg-canvas);
}

.settings-layout {
  display: grid;
  flex: 1;
  grid-template-columns: 232px minmax(0, 1fr);
  min-height: 0;
}

/* 窄桌面（801~1199）：一级导航已收为 72px 图标栏，设置内导航同步收窄、内容边距减小。 */
@media (min-width: 801px) and (max-width: 1199px) {
  .settings-layout {
    grid-template-columns: 200px minmax(0, 1fr);
  }

  .settings-content {
    padding: var(--ec-space-5) var(--ec-space-6) var(--ec-space-8);
  }
}

/* ==================== 桌面分类导航 ==================== */
.settings-nav {
  display: flex;
  flex-direction: column;
  min-height: 0;
  padding: var(--ec-space-4) var(--ec-space-3);
  background: var(--ec-surface);
  border-right: 1px solid var(--ec-border-soft);
}

.settings-nav__head {
  padding: 0 var(--ec-space-2) var(--ec-space-3);
}

.settings-nav__heading {
  margin: 0;
  color: var(--ec-text-primary);
  font-size: var(--ec-font-size-page-title);
  font-weight: var(--ec-font-weight-semibold);
}

.settings-nav__items {
  display: flex;
  flex: 1;
  flex-direction: column;
  gap: 2px;
  min-height: 0;
  overflow-y: auto;
}

.settings-nav__item {
  display: flex;
  gap: var(--ec-space-3);
  align-items: center;
  width: 100%;
  min-height: 40px;
  padding: 0 var(--ec-space-3);
  border: none;
  border-radius: var(--ec-radius-sm);
  background: transparent;
  color: var(--ec-text-secondary);
  font-size: var(--ec-font-size-nav);
  text-align: left;
  cursor: pointer;
  transition:
    background-color var(--ec-transition-fast) var(--ec-ease),
    color var(--ec-transition-fast) var(--ec-ease);
}

.settings-nav__item--active {
  background: var(--ec-brand-050);
  color: var(--ec-brand-600);
  font-weight: var(--ec-font-weight-medium);
}

@media (hover: hover) and (pointer: fine) {
  .settings-nav__item:not(.settings-nav__item--active):hover {
    background: var(--ec-surface-hover);
    color: var(--ec-text-primary);
  }
}

.settings-nav__item:focus-visible {
  outline: var(--ec-focus-width) solid var(--ec-focus-ring);
  outline-offset: calc(-1 * var(--ec-focus-width));
}

.settings-nav__label {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.settings-nav__foot {
  display: flex;
  flex-direction: column;
  gap: 2px;
  padding: var(--ec-space-3) var(--ec-space-3) 0;
  border-top: 1px solid var(--ec-border-soft);
  color: var(--ec-text-tertiary);
  font-size: var(--ec-font-size-small);
}

.settings-nav__link {
  align-self: flex-start;
  color: var(--ec-brand-600);
  font-size: var(--ec-font-size-secondary);
  text-decoration: none;
}

@media (hover: hover) and (pointer: fine) {
  .settings-nav__link:hover {
    text-decoration: underline;
  }
}

/* ==================== 内容区 ==================== */
.settings-content {
  min-height: 0;
  padding: var(--ec-space-6) var(--ec-space-8) var(--ec-space-8);
  overflow-y: auto;
}

.settings-content__header {
  display: flex;
  gap: var(--ec-space-2);
  align-items: flex-start;
  max-width: 720px;
  margin: 0 auto var(--ec-space-6);
}

.settings-content__back {
  display: none;
}

.settings-content__heading {
  display: flex;
  flex-direction: column;
  gap: var(--ec-space-1);
}

.settings-content__title {
  margin: 0;
  color: var(--ec-text-primary);
  font-size: 20px;
  font-weight: var(--ec-font-weight-semibold);
  line-height: 1.3;
}

.settings-content__desc {
  margin: 0;
  max-width: 60ch;
  color: var(--ec-text-secondary);
  font-size: var(--ec-font-size-secondary);
  line-height: 1.5;
}

.settings-content__body {
  display: flex;
  flex-direction: column;
  gap: var(--ec-space-6);
  max-width: 720px;
  margin: 0 auto;
}

/* 分组卡内的行分割线（行根节点来自 SettingsRow，父级作用域统一画，避免每个分组重复配置）。 */
.settings-content__body :deep(.ec-set-row + .ec-set-row) {
  border-top: 1px solid var(--ec-border-soft);
}

/* 主题选择块与后续行之间同样画分割线（主题块是自定义块，不在 .ec-set-row 链上）。 */
.settings-content__body :deep(.settings-themes + .ec-set-row) {
  border-top: 1px solid var(--ec-border-soft);
}

/* ==================== 通用控件 ==================== */
.settings-btn {
  flex: none;
  padding: 0 var(--ec-space-4);
  min-height: 36px;
  border: 1px solid transparent;
  border-radius: var(--ec-radius-sm);
  font-size: var(--ec-font-size-secondary);
  font-weight: var(--ec-font-weight-medium);
  cursor: pointer;
  transition: background-color var(--ec-transition-fast) var(--ec-ease);
}

.settings-btn:disabled {
  opacity: 0.55;
  cursor: not-allowed;
}

.settings-btn:focus-visible {
  outline: var(--ec-focus-width) solid var(--ec-focus-ring);
  outline-offset: var(--ec-focus-offset);
}

.settings-btn--primary {
  background: var(--ec-brand-600);
  color: var(--ec-text-inverse);
}

@media (hover: hover) and (pointer: fine) {
  .settings-btn--primary:not(:disabled):hover {
    background: var(--ec-brand-700);
  }
}

.settings-btn--secondary {
  background: transparent;
  border-color: var(--ec-border);
  color: var(--ec-text-primary);
}

@media (hover: hover) and (pointer: fine) {
  .settings-btn--secondary:not(:disabled):hover {
    background: var(--ec-surface-hover);
    border-color: var(--ec-border-strong);
  }
}

.settings-btn--danger {
  background: transparent;
  border-color: var(--ec-danger);
  color: var(--ec-danger);
}

@media (hover: hover) and (pointer: fine) {
  .settings-btn--danger:not(:disabled):hover {
    background: rgb(220 38 38 / 8%);
  }
}

.settings-text-btn {
  padding: 0 var(--ec-space-2);
  border: none;
  border-radius: var(--ec-radius-sm);
  background: transparent;
  color: var(--ec-brand-600);
  font-size: var(--ec-font-size-secondary);
  cursor: pointer;
}

.settings-text-btn:focus-visible {
  outline: var(--ec-focus-width) solid var(--ec-focus-ring);
  outline-offset: var(--ec-focus-offset);
}

.settings-avatar-input {
  display: none;
}

.settings-input {
  flex: 1;
  min-width: 0;
  min-height: 36px;
  padding: 0 var(--ec-space-3);
  border: 1px solid var(--ec-border);
  border-radius: var(--ec-radius-sm);
  background: var(--ec-surface);
  color: var(--ec-text-primary);
  font: inherit;
  font-size: var(--ec-font-size-body);
  outline: none;
  box-sizing: border-box;
  transition: border-color var(--ec-transition-fast) var(--ec-ease);
}

.settings-input:focus {
  border-color: var(--ec-brand-600);
  box-shadow: 0 0 0 3px rgb(37 99 235 / 12%);
}

.settings-field {
  display: flex;
  gap: var(--ec-space-2);
  align-items: center;
  width: 100%;
}

.settings-field__count {
  flex: none;
  color: var(--ec-text-tertiary);
  font-size: var(--ec-font-size-small);
  font-variant-numeric: tabular-nums;
}

.settings-field__hint {
  margin: 0;
  color: var(--ec-text-tertiary);
  font-size: var(--ec-font-size-small);
}

.settings-field__error {
  margin: 0;
  color: var(--ec-danger);
  font-size: var(--ec-font-size-secondary);
}

.settings-value {
  color: var(--ec-text-secondary);
  font-size: var(--ec-font-size-secondary);
  font-variant-numeric: tabular-nums;
}

.settings-link {
  color: var(--ec-brand-600);
  font-size: var(--ec-font-size-secondary);
  text-decoration: none;
}

@media (hover: hover) and (pointer: fine) {
  .settings-link:hover {
    text-decoration: underline;
  }
}

.settings-note {
  margin: 0;
  color: var(--ec-text-tertiary);
  font-size: var(--ec-font-size-small);
  line-height: 1.5;
}

.settings-actions {
  display: flex;
  gap: var(--ec-space-3);
  align-items: center;
  justify-content: flex-end;
  padding-top: var(--ec-space-1);
}

.settings-actions__hint {
  color: var(--ec-text-secondary);
  font-size: var(--ec-font-size-small);
}

/* ==================== 账号概览 ==================== */
.settings-account {
  display: flex;
  gap: var(--ec-space-3);
  align-items: center;
  padding: var(--ec-space-4);
}

.settings-account__text {
  display: flex;
  flex-direction: column;
  gap: 2px;
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

/* ==================== 连接状态 ==================== */
.settings-conn {
  display: inline-flex;
  gap: var(--ec-space-2);
  align-items: center;
  color: var(--ec-text-secondary);
  font-size: var(--ec-font-size-secondary);
}

.settings-conn__dot {
  width: 8px;
  height: 8px;
  border-radius: 50%;
  background: var(--ec-disabled);
}

.settings-conn--authed .settings-conn__dot {
  background: var(--ec-online);
}

.settings-conn--connecting .settings-conn__dot,
.settings-conn--reconnecting .settings-conn__dot {
  background: var(--ec-warning);
}

/* ==================== 主题选择 ==================== */
.settings-themes {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(200px, 1fr));
  gap: var(--ec-space-3);
  padding: var(--ec-space-4);
}

.settings-theme {
  display: flex;
  gap: var(--ec-space-3);
  align-items: center;
  padding: var(--ec-space-2);
  border: 1px solid var(--ec-border);
  border-radius: var(--ec-radius-md);
  background: var(--ec-surface);
}

.settings-theme--active {
  border-color: var(--ec-brand-600);
  box-shadow: 0 0 0 1px var(--ec-brand-600);
}

.settings-theme--disabled {
  opacity: 0.7;
}

.settings-theme__preview {
  flex: none;
  width: 56px;
  height: 40px;
  border-radius: var(--ec-radius-sm);
  border: 1px solid var(--ec-border);
}

.settings-theme__preview--light {
  background: linear-gradient(135deg, #ffffff 0%, #dbeafe 100%);
}

.settings-theme__preview--dark {
  background: linear-gradient(135deg, #1e293b 0%, #0f172a 100%);
}

.settings-theme__meta {
  display: flex;
  flex: 1;
  flex-direction: column;
  gap: 2px;
  min-width: 0;
}

.settings-theme__name {
  color: var(--ec-text-primary);
  font-size: var(--ec-font-size-secondary);
  font-weight: var(--ec-font-weight-medium);
}

.settings-theme__hint {
  color: var(--ec-text-secondary);
  font-size: var(--ec-font-size-small);
}

.settings-theme__badge {
  align-self: flex-start;
  padding: 1px 6px;
  border: 1px solid var(--ec-border);
  border-radius: var(--ec-radius-full);
  color: var(--ec-text-tertiary);
  font-size: var(--ec-font-size-small);
}

.settings-theme__check {
  flex: none;
  color: var(--ec-brand-600);
}

/* ==================== 移动端 ==================== */
.settings-mobile-bar {
  display: none;
}

.settings-index {
  display: none;
}

@media (max-width: 800px) {
  .settings-layout {
    display: block;
    height: 100%;
    overflow-y: auto;
  }

  .settings-nav {
    display: none;
  }

  .settings-mobile-bar {
    display: flex;
    flex-direction: column;
    gap: 2px;
    padding: var(--ec-space-3) var(--ec-space-4) var(--ec-space-2);
    background: var(--ec-bg-canvas);
  }

  .settings-page--detail .settings-mobile-bar {
    display: none;
  }

  .settings-mobile-bar__title {
    margin: 0;
    color: var(--ec-text-primary);
    font-size: var(--ec-font-size-mobile-page-title, 22px);
    font-weight: var(--ec-font-weight-semibold);
  }

  .settings-mobile-bar__subtitle {
    margin: 0;
    color: var(--ec-text-secondary);
    font-size: var(--ec-font-size-mobile-list-sub, 13px);
  }

  .settings-index {
    display: flex;
    flex-direction: column;
    gap: var(--ec-space-2);
    padding: 0 var(--ec-space-4) var(--ec-space-8);
  }

  .settings-page--detail .settings-index {
    display: none;
  }

  .settings-index__eyebrow {
    margin: var(--ec-space-2) 2px 0;
    color: var(--ec-text-tertiary);
    font-size: var(--ec-font-size-small);
    letter-spacing: 0.04em;
  }

  .settings-index__account {
    display: flex;
    gap: var(--ec-space-3);
    align-items: center;
    width: 100%;
    padding: var(--ec-space-3) var(--ec-space-4);
    border: 1px solid var(--ec-border);
    border-radius: var(--ec-radius-mobile-card, var(--ec-radius-lg));
    background: var(--ec-surface);
    text-align: left;
    cursor: pointer;
  }

  .settings-index__account-text {
    display: flex;
    flex: 1;
    flex-direction: column;
    gap: 2px;
    min-width: 0;
  }

  .settings-index__account-name {
    color: var(--ec-text-primary);
    font-size: 16px;
    font-weight: var(--ec-font-weight-semibold);
  }

  .settings-index__account-email {
    overflow: hidden;
    color: var(--ec-text-secondary);
    font-size: var(--ec-font-size-mobile-list-sub, 13px);
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  .settings-index__group {
    display: flex;
    flex-direction: column;
    overflow: hidden;
    background: var(--ec-surface);
    border: 1px solid var(--ec-border);
    border-radius: var(--ec-radius-mobile-card, var(--ec-radius-lg));
  }

  .settings-index__item {
    display: flex;
    gap: var(--ec-space-3);
    align-items: center;
    width: 100%;
    min-height: 64px;
    padding: var(--ec-space-2) var(--ec-space-4);
    border: none;
    background: transparent;
    text-align: left;
    cursor: pointer;
  }

  .settings-index__item + .settings-index__item {
    border-top: 1px solid var(--ec-border-soft);
  }

  .settings-index__item:active {
    background: var(--ec-surface-hover);
  }

  .settings-index__item:focus-visible {
    outline: var(--ec-focus-width) solid var(--ec-focus-ring);
    outline-offset: calc(-1 * var(--ec-focus-width));
  }

  .settings-index__icon {
    display: flex;
    flex: none;
    align-items: center;
    justify-content: center;
    width: 34px;
    height: 34px;
    border-radius: var(--ec-radius-sm);
    background: var(--ec-brand-050);
    color: var(--ec-brand-600);
  }

  .settings-index__item-text {
    display: flex;
    flex: 1;
    flex-direction: column;
    gap: 2px;
    min-width: 0;
  }

  .settings-index__label {
    color: var(--ec-text-primary);
    font-size: 15px;
  }

  .settings-index__value {
    overflow: hidden;
    color: var(--ec-text-secondary);
    font-size: var(--ec-font-size-mobile-list-sub, 13px);
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  .settings-index__chevron {
    flex: none;
    color: var(--ec-text-tertiary);
    transform: rotate(180deg);
  }

  .settings-index__foot {
    margin: var(--ec-space-2) 0 0;
    padding: 0 2px;
    color: var(--ec-text-tertiary);
    font-size: var(--ec-font-size-small);
  }

  .settings-content {
    display: none;
    padding: 0 0 var(--ec-space-8);
  }

  .settings-page--detail .settings-content {
    display: block;
  }

  .settings-content__header {
    position: sticky;
    top: 0;
    z-index: 2;
    gap: var(--ec-space-2);
    align-items: center;
    margin: 0;
    padding: var(--ec-space-2) var(--ec-space-3) var(--ec-space-2);
    background: var(--ec-bg-canvas);
    border-bottom: 1px solid var(--ec-border-soft);
  }

  .settings-content__back {
    display: inline-flex;
    flex: none;
    align-items: center;
    justify-content: center;
    width: 40px;
    height: 40px;
    border: none;
    border-radius: var(--ec-radius-full);
    background: transparent;
    color: var(--ec-text-primary);
    cursor: pointer;
  }

  .settings-content__back:active {
    background: var(--ec-surface-hover);
  }

  .settings-content__title {
    font-size: var(--ec-font-size-mobile-header, 17px);
  }

  .settings-content__desc {
    display: none;
  }

  .settings-content__body {
    gap: var(--ec-space-4);
    padding: var(--ec-space-3) var(--ec-space-4) 0;
  }

  .settings-btn {
    min-height: var(--ec-touch-target, 44px);
    padding: 0 var(--ec-space-4);
    border-radius: var(--ec-radius-mobile-input, 16px);
  }

  .settings-text-btn {
    min-height: var(--ec-touch-target, 44px);
  }

  .settings-input {
    min-height: var(--ec-touch-target, 44px);
    border-radius: var(--ec-radius-mobile-input, 16px);
  }

  .settings-field {
    width: 100%;
  }

  .settings-themes {
    grid-template-columns: 1fr;
    padding: var(--ec-space-3) var(--ec-space-4);
  }

  .settings-theme__preview {
    width: 48px;
    height: 36px;
  }

  .settings-actions {
    padding: var(--ec-space-1) 0 0;
  }

  .settings-actions .settings-btn--primary {
    width: 100%;
  }
}

/* 触屏不产生粘滞 hover；键盘焦点始终可见。 */
@media (prefers-reduced-motion: reduce) {
  .settings-nav__item,
  .settings-btn,
  .settings-input {
    transition: none;
  }
}
</style>
