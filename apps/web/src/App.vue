<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref } from 'vue'
import { RouterView } from 'vue-router'
import ConnectionBanner from './components/ConnectionBanner.vue'
import { chatWs, type WsState } from './core/ws'
import { useAuthStore } from './stores/auth'

// 根组件：路由出口 + 全局连接横幅（未登录不显示；登录后按 WS 状态提示断线/重连）。
const auth = useAuthStore()
const wsState = ref<WsState>(chatWs.state)
let unsubscribe: (() => void) | null = null

onMounted(() => {
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
