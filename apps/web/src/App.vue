<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { RouterView, useRoute, useRouter } from 'vue-router'
import ConnectionBanner from './components/ConnectionBanner.vue'
import AppToast from './components/AppToast.vue'
import MobileTabBar from './components/MobileTabBar.vue'
import SearchDialog from './components/SearchDialog.vue'
import SideNav from './components/SideNav.vue'
import { chatWs, type WsState } from './core/ws'
import { setDesktopUnreadTotal, subscribeNotificationClick } from './core/desktop'
import { resolveApiBase, resolveWsUrl } from './core/config'
import {
  checkBackgroundNotificationsAvailability,
  consumePendingBackgroundClick,
  getBackgroundNotifyStatus,
  isAndroidBackgroundAvailable,
  setBackgroundConversation,
  startBackgroundNotifications,
  stopBackgroundNotifications,
  subscribeBackgroundNotificationClick,
  type BackgroundNotifyConfig,
  type PendingBackgroundClick,
} from './core/backgroundNotifications'
import { useAuthStore } from './stores/auth'
import { useConversationsStore } from './stores/conversations'
import { useFavoritesStore } from './stores/favorites'
import { useFriendsStore } from './stores/friends'
import { usePresenceStore } from './stores/presence'
import { useSettingsStore } from './stores/settings'
import { useVisualViewport } from './core/useVisualViewport'
import { NConfigProvider } from 'naive-ui'
import { ecThemeOverrides } from './core/theme'

// 根组件（UI-01 布局壳）：/login 使用 blank 布局（全屏，无导航）；
// 其余路由为「左侧一级导航 + 内容区」外壳，页面自身负责内部滚动。
// 全局连接横幅按 WS 状态提示断线/重连；在线状态（M3-09）全局订阅。
// F-01：系统通知点击后按会话 ID 打开对应会话；F-02：全局未读数同步到桌面壳（托盘/任务栏）。
const route = useRoute()
const router = useRouter()
const auth = useAuthStore()
const conversations = useConversationsStore()
const favorites = useFavoritesStore()
const friends = useFriendsStore()
const presence = usePresenceStore()
const settings = useSettingsStore()
// UI-04 批次3：软键盘适配（只读可视高度，键盘弹出时隐藏底部导航并收紧外壳高度）。
const { keyboardOpen } = useVisualViewport()
const wsState = ref<WsState>(chatWs.state)
let unsubscribe: (() => void) | null = null
let unsubscribeNotificationClick: (() => void) | null = null
let unsubscribeBackgroundClick: (() => void) | null = null

const isBlankLayout = computed(() => route.meta.layout === 'blank')

// UI-04：移动端底部导航仅在 ≤800 显示（组件内 CSS 控制），进入会话详情时隐藏，
// 把完整高度留给聊天区；登录页（blank 布局）不显示。
const isConversationDetail = computed(() => {
  const conversationId = route.params.conversationId
  return route.name === 'chat' && typeof conversationId === 'string' && conversationId.length > 0
})
const showMobileTabBar = computed(() => auth.isLoggedIn && !isBlankLayout.value && !isConversationDetail.value)

/** F-01：点击系统通知打开对应会话；已在该会话时不重复跳转。 */
function openConversationFromNotification(conversationId: string): void {
  if (!auth.isLoggedIn) return
  if (route.name === 'chat' && route.params.conversationId === conversationId) return
  void router.push({ name: 'chat', params: { conversationId } })
}

function sameServerOrigin(serverUrl: string): boolean {
  try {
    return new URL(serverUrl).origin === new URL(resolveApiBase()).origin
  } catch {
    return false
  }
}

/**
 * 通知点击：来源账号与服务器必须完整且仍属当前登录，否则丢弃（升级前缺少来源的旧通知直接失效）。
 * 原生侧冷/热启动统一走缓存，消费一次后清除。
 */
function handleBackgroundClick(click: PendingBackgroundClick): void {
  if (!auth.isLoggedIn) return
  if (!click.userId || !click.serverUrl) return
  if (click.userId !== auth.user?.userId) return
  if (!sameServerOrigin(click.serverUrl)) return
  openConversationFromNotification(click.conversationId)
}

/** 冷启动点击先由原生缓存；登录与路由就绪后消费一次。 */
async function consumeAndroidPendingClick(): Promise<void> {
  if (!isAndroidBackgroundAvailable() || !auth.isLoggedIn) return
  const click = await consumePendingBackgroundClick()
  if (click) handleBackgroundClick(click)
}

/** Android 后台消息提醒：登录态与服务器地址就绪后启动原生前台服务，否则停止。 */
function backgroundNotifyConfig(): BackgroundNotifyConfig | null {
  if (!auth.isLoggedIn || !auth.accessToken || !auth.user) return null
  return {
    apiBase: resolveApiBase(),
    wsUrl: resolveWsUrl(),
    token: auth.accessToken,
    deviceId: auth.deviceId,
    userId: auth.user.userId,
    // 登录/切账号触发的自动同步不是用户主动开启：服务被终止后不得自动重建。
    userInitiated: false,
  }
}

async function syncAndroidBackgroundNotifications(): Promise<void> {
  if (!isAndroidBackgroundAvailable()) return
  const config = backgroundNotifyConfig()
  if (settings.androidBackgroundNotificationsEnabled && config) {
    const result = await startBackgroundNotifications(config)
    if (!result.started && result.permission !== false) {
      // 系统通知/渠道被关闭等导致未启动：偏好回到关闭，避免设置页误显示为正在接收。
      settings.setAndroidBackgroundNotificationsEnabled(false)
    }
  } else {
    await stopBackgroundNotifications()
  }
}

/**
 * 恢复前台时复核通知权限、渠道与实际服务状态（覆盖聊天页、设置页等所有已登录页面）。
 * 原生停止不影响 Web 登录态；只有确认服务已不可用时才回退用户偏好。
 */
async function reconcileAndroidBackgroundNotifications(): Promise<void> {
  if (!isAndroidBackgroundAvailable() || !auth.isLoggedIn) return
  if (!settings.androidBackgroundNotificationsEnabled) return
  const availability = await checkBackgroundNotificationsAvailability()
  if (availability && !availability.available) {
    await stopBackgroundNotifications()
    settings.setAndroidBackgroundNotificationsEnabled(false)
    return
  }
  const status = await getBackgroundNotifyStatus()
  if (status !== null && !status.running) {
    settings.setAndroidBackgroundNotificationsEnabled(false)
  }
}

function onVisibilityChange(): void {
  if (document.visibilityState === 'visible') {
    void reconcileAndroidBackgroundNotifications()
  }
}

onMounted(() => {
  presence.subscribeRealtime()
  // F-17：好友的 REST 全量拉取 + friend.changed 增量推送（登录后/重连 authed 时拉取）。
  friends.subscribeRealtime()
  if (chatWs.state === 'authed') {
    void friends.loadAll().catch(() => undefined)
  }
  unsubscribe = chatWs.on('statechange', (state) => {
    wsState.value = state
    if (state === 'authed') {
      void friends.loadAll().catch(() => undefined)
    }
  })
  // F-01：桌面壳走 preload 回传，Web 版走浏览器通知点击自定义事件。
  unsubscribeNotificationClick = subscribeNotificationClick(openConversationFromNotification)
  // Android：原生前台服务通知点击回传（WebView 内无浏览器 Notification API）。
  // 冷热启动统一走消费入口：事件只通知去消费，避免热启动点击后 pending 残留再次导航。
  unsubscribeBackgroundClick = subscribeBackgroundNotificationClick(() => {
    void consumeAndroidPendingClick()
  })
  void consumeAndroidPendingClick()
  void syncAndroidBackgroundNotifications()
  document.addEventListener('visibilitychange', onVisibilityChange)
})

// Android 后台消息提醒：账号、令牌或服务器地址变化时同步原生前台服务状态。
watch(
  () =>
    [
      auth.user?.userId ?? '',
      auth.accessToken ?? '',
      auth.isLoggedIn ? resolveApiBase() : '',
    ].join('|'),
  () => {
    void syncAndroidBackgroundNotifications()
  },
)

// Android 后台消息提醒：记录当前打开的会话，供原生服务在前台时抑制通知。
watch(
  () => auth.isLoggedIn,
  (loggedIn) => {
    if (loggedIn) void consumeAndroidPendingClick()
  },
  { immediate: true },
)
watch(
  () => (route.name === 'chat' && typeof route.params.conversationId === 'string'
    ? route.params.conversationId
    : null),
  (conversationId) => {
    setBackgroundConversation(conversationId)
  },
  { immediate: true },
)

// F-26：登录后加载当前账号本地收藏；登出/切账号只清内存（IndexedDB 保留，换账号可见性由 ownerId 过滤）。
watch(
  () => auth.user?.userId ?? '',
  (userId) => {
    if (userId && auth.isLoggedIn) void favorites.loadForUser(userId)
    else favorites.clear()
  },
  { immediate: true },
)

// F-02：会话未读变化时同步到桌面壳（Web/移动端无桌面桥时静默跳过）。
watch(
  () => conversations.totalUnread,
  (count) => setDesktopUnreadTotal(count),
  { immediate: true },
)

onBeforeUnmount(() => {
  document.removeEventListener('visibilitychange', onVisibilityChange)
  unsubscribe?.()
  unsubscribeNotificationClick?.()
  unsubscribeBackgroundClick?.()
})

function reconnect(): void {
  chatWs.connect()
}
</script>

<template>
  <!-- Naive UI 主题入口：abstract 不渲染额外 DOM，避免影响 scoped 布局与 file:// 加载。 -->
  <NConfigProvider abstract :theme-overrides="ecThemeOverrides">
    <div class="ec-app-root" :class="{ 'ec-app-root--keyboard': keyboardOpen }">
      <ConnectionBanner v-if="auth.isLoggedIn" :state="wsState" @reconnect="reconnect" />
      <RouterView v-if="isBlankLayout" />
      <div v-else class="ec-app-shell">
        <SideNav />
        <main class="ec-app-main">
          <RouterView />
        </main>
        <MobileTabBar v-if="showMobileTabBar && !keyboardOpen" />
      </div>
      <!-- UI-03：全局轻提示（core/toast.ts 队列），全应用只挂载一次。 -->
      <AppToast />
      <!-- F-11：消息搜索面板（全局单例，左导航/聊天顶栏/移动端列表共用）。 -->
      <SearchDialog />
    </div>
  </NConfigProvider>
</template>

<style scoped>
.ec-app-root {
  display: flex;
  flex-direction: column;
  height: 100vh;
  height: 100dvh;
  /* UI-04 批次3：Android 软键盘弹出时用 visualViewport 实时高度，保证输入区可见。 */
  height: var(--ec-vvh, 100dvh);
  overflow: hidden;
}

.ec-app-shell {
  display: grid;
  flex: 1;
  grid-template-columns: auto minmax(0, 1fr);
  min-height: 0;
}

.ec-app-main {
  min-width: 0;
  min-height: 0;
  overflow: hidden;
}

/* ≤800（UI-04）：单列外壳 + 底部导航行；主导航 SideNav 由自身媒体查询隐藏。 */
@media (max-width: 800px) {
  .ec-app-root {
    /* UI-05 修复：Android 15+ edge-to-edge 下 WebView 绘制到状态栏区域，
       为系统状态栏（时间/信号）留出安全区，避免页面标题被遮挡。 */
    padding-top: var(--ec-safe-top);
  }

  .ec-app-shell {
    grid-template-columns: minmax(0, 1fr);
    grid-template-rows: minmax(0, 1fr) auto;
  }
}

/* UI-D P3：≥1200 桌面把应用收为一张浮起卡片（参考图 01~07 的外层画布）。
   1200 以下保持全屏，避免影响窄桌面、Electron 最小窗口与移动端安全区。 */
@media (min-width: 1200px) {
  .ec-app-root {
    height: calc(var(--ec-vvh, 100dvh) - 24px);
    margin: 12px;
    border: 1px solid var(--ec-border);
    border-radius: var(--ec-radius-lg);
    background: var(--ec-bg-canvas);
    box-shadow: var(--ec-shadow-lg);
  }
}

/* blank 布局（登录页）铺满外壳高度；页面自身仍在内容区居中（计划 §2.2 / §5.7）。 */
.ec-app-root > .login-page {
  flex: 1;
  min-height: 0;
}
</style>
