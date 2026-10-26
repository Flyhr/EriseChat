<script setup lang="ts">
import { computed } from 'vue'
import type { WsState } from '../core/ws'

// 全局连接横幅（App.vue 挂在路由出口上方，UI-01 token 化）：按 chatWs 状态提示，断开可点击重连。
const props = defineProps<{
  state: WsState
}>()

const emit = defineEmits<{ reconnect: [] }>()

const text = computed(() => {
  switch (props.state) {
    case 'connecting':
      return '正在连接服务器…'
    case 'reconnecting':
      return '连接已断开，正在重连…'
    case 'closed':
      return '连接已断开'
    default:
      return ''
  }
})

/** 仅「已断开」状态提供点击重连（重连中由退避策略自动进行）。 */
const clickable = computed(() => props.state === 'closed')

function onClick(): void {
  if (clickable.value) emit('reconnect')
}
</script>

<template>
  <div
    v-if="text"
    class="connection-banner"
    :class="{ 'connection-banner--clickable': clickable }"
    role="status"
    @click="onClick"
  >
    <span>{{ text }}</span>
    <span v-if="clickable" class="connection-banner__action">点击重连</span>
  </div>
</template>

<style scoped>
.connection-banner {
  position: fixed;
  top: 0;
  right: 0;
  left: 0;
  z-index: 100;
  display: flex;
  gap: var(--ec-space-2);
  align-items: center;
  justify-content: center;
  padding: var(--ec-space-2);
  background: var(--ec-warning);
  color: var(--ec-text-inverse);
  font-size: var(--ec-font-size-secondary);
  text-align: center;
}

.connection-banner--clickable {
  cursor: pointer;
}

.connection-banner__action {
  text-decoration: underline;
  text-underline-offset: 2px;
}
</style>
