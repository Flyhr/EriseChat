<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import { RouterView, useRoute } from 'vue-router'
import ConnectionBanner from './components/ConnectionBanner.vue'
import AppToast from './components/AppToast.vue'
import MobileTabBar from './components/MobileTabBar.vue'
import SideNav from './components/SideNav.vue'
import { chatWs, type WsState } from './core/ws'
import { useAuthStore } from './stores/auth'
import { usePresenceStore } from './stores/presence'

// 根组件（UI-01 布局壳）：/login 使用 blank 布局（全屏，无导航）；
// 其余路由为「左侧一级导航 + 内容区」外壳，页面自身负责内部滚动。
// 全局连接横幅按 WS 状态提示断线/重连；在线状态（M3-09）全局订阅。
const route = useRoute()
const auth = useAuthStore()
const presence = usePresenceStore()
const wsState = ref<WsState>(chatWs.state)
let unsubscribe: (() => void) | null = null

const isBlankLayout = computed(() => route.meta.layout === 'blank')

// UI-04：移动端底部导航仅在 ≤800 显示（组件内 CSS 控制），进入会话详情时隐藏，
// 把完整高度留给聊天区；登录页（blank 布局）不显示。
const isConversationDetail = computed(() => {
  const conversationId = route.params.conversationId
  return route.name === 'chat' && typeof conversationId === 'string' && conversationId.length > 0
})
const showMobileTabBar = computed(() => auth.isLoggedIn && !isBlankLayout.value && !isConversationDetail.value)

onMounted(() => {
  presence.subscribeRealtime()
  unsubscribe = chatWs.on('statechange', (state) => {
    wsState.value = state
  })
})

onBeforeUnmount(() => {
  unsubscribe?.()
})

function reconnect(): void {
  chatWs.connect()
}
</script>

<template>
  <div class="ec-app-root">
    <ConnectionBanner v-if="auth.isLoggedIn" :state="wsState" @reconnect="reconnect" />
    <RouterView v-if="isBlankLayout" />
    <div v-else class="ec-app-shell">
      <SideNav />
      <main class="ec-app-main">
        <RouterView />
      </main>
      <MobileTabBar v-if="showMobileTabBar" />
    </div>
    <!-- UI-03：全局轻提示（core/toast.ts 队列），全应用只挂载一次。 -->
    <AppToast />
  </div>
</template>

<style scoped>
.ec-app-root {
  display: flex;
  flex-direction: column;
  height: 100vh;
  height: 100dvh;
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
  .ec-app-shell {
    grid-template-columns: minmax(0, 1fr);
    grid-template-rows: minmax(0, 1fr) auto;
  }
}

/* blank 布局（登录页）铺满外壳高度；页面自身仍在内容区居中（计划 §2.2 / §5.7）。 */
.ec-app-root > .login-page {
  flex: 1;
  min-height: 0;
}
</style>
