<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { RouterLink, useRouter } from 'vue-router'
import { authApi } from '../api/auth'
import { filesApi } from '../api/files'
import { sessionsApi, type SessionInfo } from '../api/sessions'
import { usersApi } from '../api/users'
import { fetchVersionManifest, type VersionArtifact } from '../api/version'
import AppDialog from '../components/AppDialog.vue'
import AppIcon from '../components/AppIcon.vue'
import Avatar from '../components/Avatar.vue'
import ComingSoonHint from '../components/ComingSoonHint.vue'
import SettingsGroup from '../components/settings/SettingsGroup.vue'
import SettingsRow from '../components/settings/SettingsRow.vue'
import SettingsSwitch from '../components/settings/SettingsSwitch.vue'
import {
  checkDesktopUpdate,
  downloadDesktopUpdate,
  installDesktopUpdate,
  isDesktopShell,
  subscribeDesktopUpdateProgress,
} from '../core/desktop'
import type { AppIconName } from '../core/icons'
import { chatWs, type WsState } from '../core/ws'
import { clearAllData } from '../core/db'
import {
  getServerUrl,
  isPackagedShell,
  resetServerUrl,
  resolveApiBase,
  resolveServerOrigin,
  resolveWsUrl,
  setServerUrl,
} from '../core/config'
import { friendlyError } from '../core/errorMessages'
import { compressImageFile } from '../core/imageCompress'
import { changePasswordFormError } from '../core/passwordRules'
import { formatLastSeen, formatSessionTime, shortDeviceLabel } from '../core/sessionFormat'
import { showToast } from '../core/toast'
import { shouldOfferUpdate, platformArtifactOf, updatePromptOf } from '../core/update'
import { APP_VERSION } from '../core/version'
import { ensureNotificationPermission, playIncomingSound } from '../core/notify'
import { downloadAndInstallApk, isAndroidUpdaterAvailable, subscribeApkUpdateProgress } from '../core/updateAndroid'
import {
  getBackgroundNotifyStatus,
  isAndroidBackgroundAvailable,
  startBackgroundNotifications,
  stopBackgroundNotifications,
  subscribeBackgroundStateChange,
} from '../core/backgroundNotifications'
import type { BackgroundNotifyStatus } from '../core/backgroundNotifications'
import { useAuthStore } from '../stores/auth'
import { useConversationsStore } from '../stores/conversations'
import { useDraftsStore } from '../stores/drafts'
import { useFriendsStore } from '../stores/friends'
import { useMessagesStore } from '../stores/messages'
import { useSettingsStore } from '../stores/settings'

// 设置中心（SET-01 重构）：按「个人资料 / 账号与安全 / 通知与提醒 / 外观 / 连接与关于」分节。
// 桌面为左侧分类导航 + 右侧内容面板；移动端为分组列表 → 进入分节详情（带返回），
// 与 Slack / Telegram / 微信 / iOS 设置的分组列表形态一致。只展示真实可用的能力：
// 消息字号、会话密度与深浅色主题均已做成真设置，只展示真实可用的能力，不提供假开关、不发假请求。
const router = useRouter()
const auth = useAuthStore()
const conversations = useConversationsStore()
const messages = useMessagesStore()
const settings = useSettingsStore()
const friends = useFriendsStore()
const drafts = useDraftsStore()

// U-08：外观设置的可选值；as const 让 value 收窄为 store 的联合类型。
const fontSizeOptions = [
  { value: 'small', label: '小' },
  { value: 'medium', label: '标准' },
  { value: 'large', label: '大' },
] as const
const densityOptions = [
  { value: 'comfortable', label: '舒适' },
  { value: 'compact', label: '紧凑' },
] as const

type SectionId = 'profile' | 'account' | 'notifications' | 'appearance' | 'connection'

interface SettingsSection {
  id: SectionId
  label: string
  title: string
  icon: AppIconName
}

// 分类元数据是导航、移动端列表与内容标题的唯一来源：增删分类只改这一处。
const SECTIONS: SettingsSection[] = [
  {
    id: 'profile',
    label: '个人资料',
    title: '个人资料',
    icon: 'user',
  },
  {
    id: 'account',
    label: '账号与安全',
    title: '账号与安全',
    icon: 'shield',
  },
  {
    id: 'notifications',
    label: '通知与提醒',
    title: '通知与提醒',
    icon: 'bell',
  },
  {
    id: 'appearance',
    label: '外观',
    title: '外观',
    icon: 'palette',
  },
  {
    id: 'connection',
    label: '连接与关于',
    title: '连接与关于',
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

// Android 后台消息提醒：偏好（用户意图）与实际服务状态分开显示，避免「开关为开」被误认为正在接收。
const androidBackgroundAvailable = isAndroidBackgroundAvailable()
const backgroundStatus = ref<BackgroundNotifyStatus | null>(null)
const backgroundStopped = ref(false)
/** 读取原生状态失败：保留未知状态，不据此关闭用户偏好，允许重试。 */
const backgroundStatusUnknown = ref(false)
let backgroundStatusUnsubscribe: (() => void) | null = null
const backgroundStatusText = computed(() => {
  if (!androidBackgroundAvailable) return ''
  if (backgroundStatusUnknown.value) return '状态未知，请重试'
  const status = backgroundStatus.value
  if (status?.running) return status.connected ? '正在接收新消息' : '正在连接'
  if (backgroundStopped.value) return '后台消息提醒已停止'
  return settings.androidBackgroundNotificationsEnabled ? '正在连接' : '后台消息提醒已关闭'
})

/** 移动端分类列表的右侧摘要：只展示真实状态，不展示未保存的编辑值。 */
function sectionSummaryOf(id: SectionId): string {
  switch (id) {
    case 'profile':
      return '头像与昵称'
    case 'account':
      return auth.user?.email ?? '未登录'
    case 'notifications':
      if (androidBackgroundAvailable) {
        return `${backgroundStatusText.value} · 提示音${settings.soundEnabled ? '开' : '关'}`
      }
      return `桌面通知${settings.desktopNotificationsEnabled ? '开' : '关'} · 提示音${settings.soundEnabled ? '开' : '关'}`
    case 'appearance':
      return '浅色主题'
    case 'connection':
      return savedServerUrl.value || '与页面同源'
  }
}

// 桌面通知开关请求进行中（防止重复点击/重复申请权限）。
const notificationBusy = ref(false)
// Android 前台服务后台消息提醒：仅原生壳内可用。
const backgroundBusy = ref(false)

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
const switchingServer = ref(false)
const currentServerLabel = ref(savedServerUrl.value || '与当前页面同源')

const logoutConfirmOpen = ref(false)
const loggingOut = ref(false)

/**
 * 保存服务器地址：
 * - origin 未变化：只断开并按新地址重连，保留登录态；
 * - origin 变化：先隔离旧登录态（断连 → 清令牌/WS 认证 → 清会话/消息缓存 → 回登录页），
 *   新服务器的 HTTP/WS 请求不会带上旧令牌。
 */
function saveServer(): void {
  if (savingServer.value || switchingServer.value) return
  const previousOrigin = resolveServerOrigin()
  const value = serverUrlInput.value.trim()
  if (value.length > 0 && !setServerUrl(value)) {
    serverMessage.value = '地址无效：需以 http:// 或 https:// 开头'
    return
  }
  if (value.length === 0) {
    resetServerUrl()
  }
  serverMessage.value = ''
  savedServerUrl.value = getServerUrl()
  currentServerLabel.value = savedServerUrl.value || '与当前页面同源'
  serverUrlInput.value = savedServerUrl.value
  const nextOrigin = resolveServerOrigin()
  if (previousOrigin !== nextOrigin) {
    void switchServer()
    return
  }
  savingServer.value = true
  if (chatWs.state !== 'idle' && chatWs.state !== 'closed') {
    chatWs.disconnect(1000, 'server url changed')
  }
  chatWs.connect()
  savingServer.value = false
  showToast('服务器地址已保存，正在重新连接', 'success')
}

/** 服务器 origin 变化：断开旧连接、清空旧登录态与缓存，回登录页重新登录（F-34）。 */
async function switchServer(): Promise<void> {
  switchingServer.value = true
  try {
    chatWs.disconnect(1000, 'server switched')
    // 新服务器的 HTTP/WS 请求不得携带旧令牌：清内存令牌与 WS 认证。
    chatWs.setAuth(null, null)
    auth.clearSession()
    conversations.clear()
    friends.clear()
    messages.clear()
    drafts.clear()
    await clearAllData()
    showToast('服务器已切换，请重新登录', 'success')
    await router.replace({ name: 'login', query: { notice: '服务器地址已切换，请重新登录' } })
  } finally {
    switchingServer.value = false
  }
}

/** 断线后手动重连（与全局 ConnectionBanner 的「重试」能力一致）。 */
function reconnect(): void {
  chatWs.connect()
}

const updateChecking = ref(false)
const latestVersion = ref('')
const updateAvailable = ref(false)
const updateMessage = ref('')
const updateArtifact = ref<VersionArtifact | null>(null)
const updateDownloading = ref(false)
const updateProgress = ref(0)
let stopUpdateProgress: (() => void) | null = null

function clearUpdateProgressListener(): void {
  stopUpdateProgress?.()
  stopUpdateProgress = null
}

/**
 * 检查更新（F-29-03/04）：桌面壳交给主进程按当前服务器检查并可下载安装；
 * Web 浏览器只提示刷新页面；Android 壳暂提示去下载页更新（App 内安装见 F-29-05）。
 */
async function checkForUpdate(): Promise<void> {
  if (updateChecking.value || updateDownloading.value) return
  updateChecking.value = true
  updateMessage.value = ''
  updateArtifact.value = null
  try {
    if (isDesktopShell()) {
      const response = await checkDesktopUpdate()
      if (!response.ok || !response.result) {
        updateAvailable.value = false
        updateMessage.value = response.message ?? '检查更新失败，请稍后重试'
        return
      }
      latestVersion.value = response.result.latestVersion
      updateAvailable.value = response.result.available
      updateArtifact.value = response.result.available ? response.result.artifact : null
      updateMessage.value = response.result.available
        ? `发现新版本 v${response.result.latestVersion}，可下载并安装`
        : '已是最新版本'
      return
    }

    const manifest = await fetchVersionManifest()
    latestVersion.value = manifest.version
    if (!isPackagedShell()) {
      updateAvailable.value = shouldOfferUpdate(APP_VERSION, manifest.version)
      updateMessage.value = updateAvailable.value ? '服务端已更新，刷新页面即可获取最新版' : '已是最新版本'
      return
    }
    const prompt = updatePromptOf(manifest, APP_VERSION, 'android')
    updateAvailable.value = prompt.available
    const artifact = platformArtifactOf(manifest, 'android')
    if (prompt.available && artifact && isAndroidUpdaterAvailable()) {
      updateArtifact.value = artifact
      updateMessage.value = `发现新版本 v${prompt.latest}，可下载并安装`
    } else {
      updateMessage.value = prompt.available ? `发现新版本 v${prompt.latest}，请到下载页更新` : '已是最新版本'
    }
  } catch (error) {
    updateAvailable.value = false
    updateMessage.value = friendlyError(error)
  } finally {
    updateChecking.value = false
  }
}

/** 下载并安装桌面端更新（F-29-04）：主进程下载 → sha256 校验 → 静默安装并退出。 */
async function downloadAndInstallUpdate(): Promise<void> {
  const artifact = updateArtifact.value
  if (!artifact || updateDownloading.value) return
  updateDownloading.value = true
  updateProgress.value = 0
  updateMessage.value = '正在下载更新…'
  clearUpdateProgressListener()
  stopUpdateProgress = subscribeDesktopUpdateProgress((progress) => {
    updateProgress.value = progress.percent
  })
  try {
    const downloaded = await downloadDesktopUpdate(artifact)
    if (!downloaded.ok) {
      updateMessage.value = downloaded.message ?? '下载更新失败，请稍后重试'
      return
    }
    updateMessage.value = '下载完成，正在安装…'
    const installed = await installDesktopUpdate()
    if (!installed.ok) {
      updateMessage.value = installed.message ?? '安装更新失败，请手动运行下载的安装包'
    }
  } finally {
    updateDownloading.value = false
    clearUpdateProgressListener()
  }
}

onBeforeUnmount(() => {
  clearUpdateProgressListener()
})

/** 下载并安装 Android 更新（F-29-05）：插件下载校验后调起系统安装器。 */
async function downloadAndInstallAndroidUpdate(): Promise<void> {
  const artifact = updateArtifact.value
  if (!artifact || updateDownloading.value) return
  updateDownloading.value = true
  updateProgress.value = 0
  updateMessage.value = '正在下载更新，完成后将打开系统安装器…'
  clearUpdateProgressListener()
  stopUpdateProgress = subscribeApkUpdateProgress((progress) => {
    updateProgress.value = progress.percent
  })
  try {
    let absoluteUrl = artifact.url
    try {
      absoluteUrl = new URL(artifact.url, resolveApiBase()).toString()
    } catch {
      // 相对地址无法解析时保持原样，由插件报错。
    }
    const result = await downloadAndInstallApk({
      url: absoluteUrl,
      sha256: artifact.sha256,
      size: artifact.size,
    })
    updateMessage.value = result.ok
      ? '下载完成，请在系统安装器中确认安装（首次需允许「安装未知应用」）'
      : (result.message ?? '下载更新失败，请稍后重试')
  } finally {
    updateDownloading.value = false
    clearUpdateProgressListener()
  }
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

// ==================== F-13：账号密码与登录设备 ====================

/** 是否已设置密码（null=加载中/未知；来自 GET /api/me 的 hasPassword）。 */
const hasPassword = ref<boolean | null>(null)
const passwordDialogOpen = ref(false)
const passwordSaving = ref(false)
const passwordError = ref('')
const oldPassword = ref('')
const newPasswordInput = ref('')
const confirmPassword = ref('')
const passwordVisible = ref(false)

async function loadPasswordState(): Promise<void> {
  try {
    const profile = await usersApi.me()
    hasPassword.value = profile.hasPassword ?? false
  } catch {
    // 失败时保持未知：不阻塞其它设置项，改密入口仍可点（服务端会再校验）。
    hasPassword.value = null
  }
}

function openPasswordDialog(): void {
  passwordDialogOpen.value = true
  passwordError.value = ''
  oldPassword.value = ''
  newPasswordInput.value = ''
  confirmPassword.value = ''
  passwordVisible.value = false
}

function closePasswordDialog(): void {
  if (passwordSaving.value) return
  passwordDialogOpen.value = false
}

async function submitPasswordChange(): Promise<void> {
  if (passwordSaving.value) return
  const currentHasPassword = hasPassword.value === true
  const formError = changePasswordFormError({
    hasPassword: currentHasPassword,
    oldPassword: oldPassword.value,
    newPassword: newPasswordInput.value,
    confirmPassword: confirmPassword.value,
  })
  if (formError) {
    passwordError.value = formError
    return
  }

  passwordSaving.value = true
  passwordError.value = ''
  try {
    await authApi.changePassword({
      oldPassword: currentHasPassword ? oldPassword.value : undefined,
      newPassword: newPasswordInput.value,
    })
    hasPassword.value = true
    passwordDialogOpen.value = false
    showToast(currentHasPassword ? '密码已修改' : '密码已设置', 'success')
  } catch (error) {
    passwordError.value = friendlyError(error)
  } finally {
    passwordSaving.value = false
  }
}

/** 登录设备列表最多展示的条数（服务端已按最近活跃倒序）。 */
const MAX_VISIBLE_SESSIONS = 5

const sessions = ref<SessionInfo[]>([])
const visibleSessions = computed(() => sessions.value.slice(0, MAX_VISIBLE_SESSIONS))
const sessionsLoading = ref(false)
const sessionsError = ref('')
const sessionsLoaded = ref(false)
const kickingSessionId = ref<string | null>(null)
const kickTarget = ref<SessionInfo | null>(null)

async function loadSessions(force = false): Promise<void> {
  if (sessionsLoading.value || (sessionsLoaded.value && !force)) return
  sessionsLoading.value = true
  sessionsError.value = ''
  try {
    sessions.value = await sessionsApi.list()
    sessionsLoaded.value = true
  } catch (error) {
    sessionsError.value = friendlyError(error)
  } finally {
    sessionsLoading.value = false
  }
}

function askKickSession(session: SessionInfo): void {
  kickTarget.value = session
}

async function confirmKickSession(): Promise<void> {
  const target = kickTarget.value
  if (!target || kickingSessionId.value) return
  kickingSessionId.value = target.sessionId
  try {
    await sessionsApi.kick(target.sessionId)
    kickTarget.value = null
    showToast('已退出该设备', 'success')
    await loadSessions(true)
  } catch (error) {
    showToast(friendlyError(error), 'error')
  } finally {
    kickingSessionId.value = null
  }
}

// 进入「账号与安全」时懒加载密码状态与登录设备；进入「通知与提醒」时刷新后台提醒实际状态。
watch(activeSectionId, (next) => {
  if (next === 'notifications' && androidBackgroundAvailable) {
    void loadBackgroundStatus()
    return
  }
  if (next !== 'account') return
  void loadPasswordState()
  void loadSessions()
})

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

/**
 * 切换 Android 后台消息提醒：开启时先请求通知权限，再启动原生前台服务；
 * 授权失败保持关闭并提示，不写成功状态。
 */
async function toggleAndroidBackgroundNotifications(): Promise<void> {
  if (backgroundBusy.value) return
  if (settings.androidBackgroundNotificationsEnabled) {
    backgroundBusy.value = true
    try {
      await stopBackgroundNotifications()
      settings.setAndroidBackgroundNotificationsEnabled(false)
      backgroundStopped.value = false
      backgroundStatus.value = { running: false, connected: false }
      showToast('后台消息提醒已关闭', 'success')
    } finally {
      backgroundBusy.value = false
    }
    return
  }
  if (!auth.isLoggedIn || !auth.accessToken || !auth.user) {
    showToast('请先登录', 'error')
    return
  }
  backgroundBusy.value = true
  try {
    const result = await startBackgroundNotifications({
      apiBase: resolveApiBase(),
      wsUrl: resolveWsUrl(),
      token: auth.accessToken,
      deviceId: auth.deviceId,
      userId: auth.user.userId,
      // 设置页开关是用户明确操作：服务被终止后允许用户在下次操作时重新开启。
      userInitiated: true,
    })
    if (!result.started) {
      const message =
        result.permission === false
          ? '通知权限未开启'
          : result.reason === 'notifications_disabled'
            ? '系统通知已关闭，请在系统设置中开启'
            : result.reason === 'channel_disabled'
              ? '消息通知渠道已关闭，请在系统设置中开启'
              : '后台消息提醒开启失败'
      showToast(message, 'error')
      return
    }
    settings.setAndroidBackgroundNotificationsEnabled(true)
    backgroundStopped.value = false
    showToast('后台消息提醒已开启', 'success')
    // 状态以原生实际连接为准，避免页面写入与真实状态不一致。
    void loadBackgroundStatus()
  } finally {
    backgroundBusy.value = false
  }
}

/** 合并原生状态写入：运行中清除停止态；否则在偏好仍开启时回退，避免页面长期显示过期状态。 */
function applyBackgroundStatus(status: BackgroundNotifyStatus): void {
  backgroundStatusUnknown.value = false
  backgroundStatus.value = status
  if (status.running) {
    backgroundStopped.value = false
    return
  }
  if (settings.androidBackgroundNotificationsEnabled && !backgroundBusy.value) {
    // 服务未运行（被踢/认证失败/超时/系统回收）：偏好回到关闭，需用户在前台重新开启。
    settings.setAndroidBackgroundNotificationsEnabled(false)
    backgroundStopped.value = true
  }
}

/** 读取原生服务的实际状态；读取失败保留未知状态，不把用户偏好改成关闭。 */
async function loadBackgroundStatus(): Promise<void> {
  if (!androidBackgroundAvailable) return
  const status = await getBackgroundNotifyStatus()
  if (status === null) {
    backgroundStatusUnknown.value = true
    return
  }
  applyBackgroundStatus(status)
}

/** 状态未知时的手动重试入口。 */
function retryBackgroundStatus(): void {
  void loadBackgroundStatus()
}

onMounted(() => {
  if (androidBackgroundAvailable) {
    void loadBackgroundStatus()
    backgroundStatusUnsubscribe = subscribeBackgroundStateChange(applyBackgroundStatus)
  }
})

onBeforeUnmount(() => {
  backgroundStatusUnsubscribe?.()
  backgroundStatusUnsubscribe = null
})

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
          <span>EriseChat {{ APP_VERSION }}</span>
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
          </div>
        </header>

        <!-- 个人资料 -->
        <div v-if="activeSectionId === 'profile'" class="settings-content__body">
          <SettingsGroup title="资料">
            <SettingsRow label="头像">
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
            <SettingsRow label="昵称" vertical>
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
            <SettingsRow label="邮箱">
              <span class="settings-value">{{ auth.user?.email }}</span>
            </SettingsRow>
            <SettingsRow label="账号角色">
              <span class="settings-value">{{ auth.user?.role === 'admin' ? '管理员' : '成员' }}</span>
            </SettingsRow>
          </SettingsGroup>

          <SettingsGroup title="密码">
            <SettingsRow label="登录密码">
              <button
                type="button"
                class="settings-btn settings-btn--secondary"
                @click="openPasswordDialog"
              >
                {{ hasPassword ? '修改密码' : '设置密码' }}
              </button>
            </SettingsRow>
          </SettingsGroup>

          <SettingsGroup title="登录设备">
            <SettingsRow v-if="sessionsLoading" label="正在加载登录设备…">
              <span class="settings-value">请稍候</span>
            </SettingsRow>
            <SettingsRow v-else-if="sessionsError" label="登录设备加载失败" :hint="sessionsError">
              <button
                type="button"
                class="settings-btn settings-btn--secondary"
                @click="loadSessions(true)"
              >
                重试
              </button>
            </SettingsRow>
            <SettingsRow v-else-if="sessions.length === 0" label="暂无登录设备">
              <span class="settings-value">—</span>
            </SettingsRow>
            <template v-else>
              <SettingsRow
                v-for="session in visibleSessions"
                :key="session.sessionId"
                :label="shortDeviceLabel(session.deviceId)"
                :hint="`登录：${formatSessionTime(session.createdAt)} · 最近活跃：${formatLastSeen(session.lastSeenAt)}`"
              >
                <span v-if="session.current" class="settings-value settings-value--badge">
                  当前设备
                </span>
                <button
                  v-else
                  type="button"
                  class="settings-btn settings-btn--danger"
                  :disabled="kickingSessionId === session.sessionId"
                  @click="askKickSession(session)"
                >
                  {{ kickingSessionId === session.sessionId ? '退出中…' : '退出该设备' }}
                </button>
              </SettingsRow>
            </template>
          </SettingsGroup>

          <SettingsGroup title="登录">
            <SettingsRow label="退出登录" tone="danger">
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
            <SettingsRow v-if="!androidBackgroundAvailable" label="桌面通知">
              <SettingsSwitch
                :model-value="settings.desktopNotificationsEnabled"
                label="桌面通知"
                :disabled="notificationBusy"
                @update:model-value="toggleDesktopNotifications"
              />
            </SettingsRow>
            <SettingsRow v-else label="后台消息提醒" :hint="backgroundStatusText">
              <button
                v-if="backgroundStatusUnknown"
                type="button"
                class="settings-text-btn"
                @click="retryBackgroundStatus"
              >
                重试
              </button>
              <SettingsSwitch
                :model-value="settings.androidBackgroundNotificationsEnabled"
                label="后台消息提醒"
                :disabled="backgroundBusy"
                @update:model-value="toggleAndroidBackgroundNotifications"
              />
            </SettingsRow>
            <SettingsRow label="提示音">
              <button type="button" class="settings-text-btn" @click="previewSound">试听</button>
              <SettingsSwitch
                :model-value="settings.soundEnabled"
                label="提示音"
                @update:model-value="toggleSound"
              />
            </SettingsRow>
          </SettingsGroup>
        </div>

        <!-- 外观 -->
        <div v-else-if="activeSectionId === 'appearance'" class="settings-content__body">
          <SettingsGroup title="主题模式">
            <div class="settings-themes" role="group" aria-label="主题模式">
              <button
                type="button"
                class="settings-theme"
                :class="{ 'settings-theme--active': settings.theme === 'azure' }"
                :aria-pressed="settings.theme === 'azure'"
                @click="settings.setTheme('azure')"
              >
                <span class="settings-theme__preview settings-theme__preview--light" aria-hidden="true" />
                <span class="settings-theme__meta">
                  <span class="settings-theme__name">浅色</span>
                </span>
                <AppIcon v-if="settings.theme === 'azure'" class="settings-theme__check" name="check" :size="18" />
              </button>
              <button
                type="button"
                class="settings-theme"
                :class="{ 'settings-theme--active': settings.theme === 'dark' }"
                :aria-pressed="settings.theme === 'dark'"
                @click="settings.setTheme('dark')"
              >
                <span class="settings-theme__preview settings-theme__preview--dark" aria-hidden="true" />
                <span class="settings-theme__meta">
                  <span class="settings-theme__name">深色</span>
                </span>
                <AppIcon v-if="settings.theme === 'dark'" class="settings-theme__check" name="check" :size="18" />
              </button>
            </div>
          </SettingsGroup>
          <!-- UI-D P3：参考图 05 的其余外观信息卡。只读展示当前值，不提供无后端支持的假入口。 -->
          <section class="settings-appearance" aria-label="外观信息">
            <div class="settings-appearance__card">
              <div class="settings-appearance__info">
                <h3 class="settings-appearance__name">聊天背景</h3>
                <p class="settings-appearance__desc">柔和蓝白渐变（默认）</p>
              </div>
              <span class="settings-appearance__swatch" aria-hidden="true" />
            </div>
            <div class="settings-appearance__card">
              <div class="settings-appearance__info">
                <h3 class="settings-appearance__name">消息文字大小</h3>
                <p class="settings-appearance__desc">聊天正文与会话摘要</p>
              </div>
              <div class="settings-appearance__options" role="group" aria-label="消息文字大小">
                <button
                  v-for="option in fontSizeOptions"
                  :key="option.value"
                  type="button"
                  class="settings-appearance__option"
                  :class="{ 'settings-appearance__option--active': settings.messageFontSize === option.value }"
                  :aria-pressed="settings.messageFontSize === option.value"
                  @click="settings.setMessageFontSize(option.value)"
                >
                  {{ option.label }}
                </button>
              </div>
            </div>
            <div class="settings-appearance__card">
              <div class="settings-appearance__info">
                <h3 class="settings-appearance__name">会话列表密度</h3>
                <p class="settings-appearance__desc">会话行高与留白</p>
              </div>
              <div class="settings-appearance__options" role="group" aria-label="会话列表密度">
                <button
                  v-for="option in densityOptions"
                  :key="option.value"
                  type="button"
                  class="settings-appearance__option"
                  :class="{ 'settings-appearance__option--active': settings.listDensity === option.value }"
                  :aria-pressed="settings.listDensity === option.value"
                  @click="settings.setListDensity(option.value)"
                >
                  {{ option.label }}
                </button>
              </div>
            </div>
          </section>
        </div>

        <!-- 连接与关于 -->
        <div v-else class="settings-content__body">
          <SettingsGroup title="连接">
            <SettingsRow label="连接状态">
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
            <SettingsRow label="服务器地址" vertical>
              <div class="settings-field">
                <input
                  v-model="serverUrlInput"
                  class="settings-input"
                  type="url"
                  placeholder="https://chat.example.com 或 http://127.0.0.1:6580"
                  aria-label="服务器地址"
                />
                <button
                  type="button"
                  class="settings-btn settings-btn--primary"
                  :disabled="savingServer || switchingServer"
                  @click="saveServer"
                >
                  {{ switchingServer ? '切换中…' : '保存' }}
                </button>
              </div>
              <p class="settings-field__hint">当前：{{ currentServerLabel }}</p>
              <p class="settings-field__hint">切换服务器地址会退出当前登录，需要在新服务器重新登录。</p>
              <p v-if="serverMessage" class="settings-field__error" role="alert">{{ serverMessage }}</p>
            </SettingsRow>
          </SettingsGroup>

          <SettingsGroup title="关于">
            <SettingsRow label="版本" :hint="isDesktopShell() ? 'Windows 桌面客户端' : isPackagedShell() ? 'Android 客户端' : 'Web 客户端'">
              <span class="settings-value">{{ APP_VERSION }}</span>
            </SettingsRow>
            <SettingsRow label="检查更新" :hint="latestVersion ? `最新版本 v${latestVersion}` : '从当前服务器获取最新版本'">
              <button type="button" class="settings-btn" :disabled="updateChecking" @click="checkForUpdate">
                {{ updateChecking ? '检查中…' : '检查更新' }}
              </button>
            </SettingsRow>
            <p v-if="updateMessage" class="settings-field__hint" role="status">{{ updateMessage }}</p>
            <SettingsRow
              v-if="isDesktopShell() && updateAvailable && updateArtifact"
              label="安装更新"
              :hint="updateDownloading ? `下载中 ${updateProgress}%` : '下载完成后将自动安装并重启'"
            >
              <button
                type="button"
                class="settings-btn settings-btn--primary"
                :disabled="updateDownloading"
                @click="downloadAndInstallUpdate"
              >
                {{ updateDownloading ? `下载中 ${updateProgress}%` : '下载并安装' }}
              </button>
            </SettingsRow>
            <SettingsRow
              v-if="!isDesktopShell() && isPackagedShell() && updateAvailable && updateArtifact"
              label="安装更新"
              :hint="updateDownloading ? `下载中 ${updateProgress}%` : '下载完成后将打开系统安装器'"
            >
              <button
                type="button"
                class="settings-btn settings-btn--primary"
                :disabled="updateDownloading"
                @click="downloadAndInstallAndroidUpdate"
              >
                {{ updateDownloading ? `下载中 ${updateProgress}%` : '下载并安装' }}
              </button>
            </SettingsRow>
            <SettingsRow label="下载客户端">
              <RouterLink class="settings-link" to="/download">前往下载页</RouterLink>
            </SettingsRow>
          </SettingsGroup>
        </div>
      </section>
    </div>

    <AppDialog
      :open="passwordDialogOpen"
      :title="hasPassword ? '修改密码' : '设置密码'"
      confirm-text="保存"
      :busy="passwordSaving"
      @confirm="submitPasswordChange"
      @cancel="closePasswordDialog"
    >
      <div class="settings-password-form">
        <label v-if="hasPassword" class="settings-password-form__field">
          <span class="settings-password-form__label">当前密码</span>
          <input
            v-model="oldPassword"
            class="settings-input"
            type="password"
            autocomplete="current-password"
            aria-label="当前密码"
          />
        </label>
        <label class="settings-password-form__field">
          <span class="settings-password-form__label">新密码（8~128 个字符）</span>
          <div class="settings-password-form__row">
            <input
              v-model="newPasswordInput"
              class="settings-input"
              :type="passwordVisible ? 'text' : 'password'"
              autocomplete="new-password"
              minlength="8"
              maxlength="128"
              aria-label="新密码"
            />
            <button
              type="button"
              class="settings-password-form__toggle"
              :aria-label="passwordVisible ? '隐藏密码' : '显示密码'"
              :aria-pressed="passwordVisible"
              @click="passwordVisible = !passwordVisible"
            >
              {{ passwordVisible ? '隐藏' : '显示' }}
            </button>
          </div>
        </label>
        <label class="settings-password-form__field">
          <span class="settings-password-form__label">确认新密码</span>
          <input
            v-model="confirmPassword"
            class="settings-input"
            :type="passwordVisible ? 'text' : 'password'"
            autocomplete="new-password"
            maxlength="128"
            aria-label="确认新密码"
          />
        </label>
        <p v-if="passwordError" class="settings-field__error" role="alert">{{ passwordError }}</p>
      </div>
    </AppDialog>

    <AppDialog
      :open="Boolean(kickTarget)"
      title="退出该设备"
      :message="
        kickTarget
          ? `将退出 ${shortDeviceLabel(kickTarget.deviceId)} 的登录，该设备会被立即断开。`
          : ''
      "
      confirm-text="退出该设备"
      tone="danger"
      :busy="Boolean(kickingSessionId)"
      @confirm="confirmKickSession"
      @cancel="kickTarget = null"
    />

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

/* F-13：登录设备「当前设备」徽标 */
.settings-value--badge {
  padding: 2px var(--ec-space-2);
  border-radius: var(--ec-radius-full);
  background: var(--ec-brand-050);
  color: var(--ec-brand-600);
  font-size: var(--ec-font-size-small);
  font-weight: var(--ec-font-weight-medium);
  white-space: nowrap;
}

/* F-13：修改密码/设置密码弹窗表单 */
.settings-password-form {
  display: flex;
  flex-direction: column;
  gap: var(--ec-space-3);
}

.settings-password-form__field {
  display: flex;
  flex-direction: column;
  gap: var(--ec-space-2);
}

.settings-password-form__label {
  color: var(--ec-text-secondary);
  font-size: var(--ec-font-size-small);
}

.settings-password-form__row {
  display: flex;
  gap: var(--ec-space-2);
  align-items: center;
}

.settings-password-form__row .settings-input {
  flex: 1;
}

.settings-password-form__toggle {
  flex: none;
  min-width: 44px;
  min-height: 36px;
  padding: 0 var(--ec-space-2);
  border: 1px solid var(--ec-border);
  border-radius: var(--ec-radius-sm);
  background: var(--ec-surface);
  color: var(--ec-text-secondary);
  font: inherit;
  font-size: var(--ec-font-size-small);
  cursor: pointer;
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

/* 深色主题解冻后两张卡都可点击；选中态与 hover / focus 只改边框与底色，不产生布局位移。 */
.settings-theme {
  display: flex;
  gap: var(--ec-space-3);
  align-items: center;
  padding: var(--ec-space-2);
  border: 1px solid var(--ec-border);
  border-radius: var(--ec-radius-md);
  background: var(--ec-surface);
  text-align: left;
  cursor: pointer;
  transition:
    border-color var(--ec-transition-fast) var(--ec-ease),
    background var(--ec-transition-fast) var(--ec-ease);
}

.settings-theme:hover {
  border-color: var(--ec-brand-500);
  background: var(--ec-surface-hover);
}

.settings-theme--active {
  border-color: var(--ec-brand-600);
  box-shadow: 0 0 0 1px var(--ec-brand-600);
}

.settings-theme:focus-visible {
  outline: var(--ec-focus-width) solid var(--ec-focus-ring);
  outline-offset: var(--ec-focus-offset);
}

.settings-theme__preview {
  flex: none;
  width: 56px;
  height: 40px;
  border-radius: var(--ec-radius-sm);
  border: 1px solid var(--ec-border);
}

.settings-theme__preview--light {
  background: var(--ec-theme-preview-light);
}

.settings-theme__preview--dark {
  background: var(--ec-theme-preview-dark);
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

.settings-theme__check {
  flex: none;
  color: var(--ec-brand-600);
}

/* UI-D P3 外观信息卡：字号与密度已改为可操作设置，聊天背景仍为只读展示。 */
.settings-appearance {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(220px, 1fr));
  gap: var(--ec-space-3);
}

.settings-appearance__card {
  display: flex;
  gap: var(--ec-space-3);
  align-items: center;
  justify-content: space-between;
  min-height: 88px;
  padding: var(--ec-space-4);
  border: 1px solid var(--ec-border);
  border-radius: var(--ec-radius-panel);
  background: var(--ec-surface);
  box-shadow: var(--ec-shadow-sm);
  flex-wrap: wrap;
}

/* U-08：外观设置按钮组（原先是只读数值徽标）。 */
.settings-appearance__options {
  display: flex;
  flex: none;
  gap: var(--ec-space-1);
  padding: 3px;
  border: 1px solid var(--ec-border);
  border-radius: var(--ec-radius-full);
  background: var(--ec-surface-hover);
}

.settings-appearance__option {
  min-height: 28px;
  padding: 0 var(--ec-space-2);
  border: none;
  border-radius: var(--ec-radius-full);
  background: transparent;
  color: var(--ec-text-secondary);
  font-size: var(--ec-font-size-small);
  cursor: pointer;
  transition:
    background var(--ec-transition-fast) var(--ec-ease),
    color var(--ec-transition-fast) var(--ec-ease);
}

.settings-appearance__option--active {
  background: var(--ec-brand-600);
  color: var(--ec-text-inverse);
}

.settings-appearance__info {
  min-width: 0;
}

.settings-appearance__name {
  margin: 0 0 4px;
  color: var(--ec-text-primary);
  font-size: var(--ec-font-size-body);
  font-weight: var(--ec-font-weight-semibold);
}

.settings-appearance__desc {
  margin: 0;
  color: var(--ec-text-secondary);
  font-size: var(--ec-font-size-secondary);
  line-height: 1.4;
}

.settings-appearance__value {
  flex: none;
  padding: 4px 10px;
  border-radius: var(--ec-radius-full);
  background: var(--ec-brand-050);
  color: var(--ec-brand-600);
  font-size: 13px;
  font-weight: var(--ec-font-weight-medium);
}

.settings-appearance__swatch {
  flex: none;
  width: 56px;
  height: 40px;
  border: 1px solid var(--ec-border);
  border-radius: var(--ec-radius-sm);
  background: var(--ec-chat-gradient);
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
