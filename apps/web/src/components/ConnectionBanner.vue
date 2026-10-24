<script setup lang="ts">
import { computed } from 'vue'
import type { WsState } from '../core/ws'

// 断网/重连横幅（全局组件，M1 挂到 App 布局）：只在非就绪状态提示。
const props = defineProps<{
  state: WsState
}>()

const text = computed(() => {
  switch (props.state) {
    case 'connecting':
      return '正在连接服务器…'
    case 'reconnecting':
      return '连接已断开，正在重连…'
    case 'closed':
      return '连接已关闭'
    default:
      return ''
  }
})
</script>

<template>
  <div v-if="text" class="connection-banner" role="status">{{ text }}</div>
</template>

<style scoped>
.connection-banner {
  position: fixed;
  top: 0;
  right: 0;
  left: 0;
  z-index: 100;
  padding: var(--space-2);
  background: var(--color-warning);
  color: var(--color-text-inverse);
  font-size: 13px;
  text-align: center;
}
</style>
