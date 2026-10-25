<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref } from 'vue'
import { RouterView } from 'vue-router'
import ConnectionBanner from './components/ConnectionBanner.vue'
import { chatWs, type WsState } from './core/ws'
import { useAuthStore } from './stores/auth'
import { usePresenceStore } from './stores/presence'

// 根组件：路由出口 + 全局连接横幅（未登录不显示；登录后按 WS 状态提示断线/重连）。
// 在线状态（M3-09）全局订阅：通讯录、单聊头部、群成员面板共用。
const auth = useAuthStore()
const presence = usePresenceStore()
const wsState = ref<WsState>(chatWs.state)
let unsubscribe: (() => void) | null = null

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
  <ConnectionBanner v-if="auth.isLoggedIn" :state="wsState" @reconnect="reconnect" />
  <RouterView />
</template>
