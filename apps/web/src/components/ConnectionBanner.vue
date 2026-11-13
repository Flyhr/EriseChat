<script setup lang="ts">
import { computed, onBeforeUnmount, ref, watch } from 'vue'
import { PhWarningCircle } from '@phosphor-icons/vue'
import type { WsState } from '../core/ws'

// 连接状态条（UI-D P2）：参考图要求浅色非遮挡提示、历史消息继续可见。
// 去掉原先 fixed 全宽深色条，改为布局流内的一条浅橙横幅；只在「已断开」提供真实可点的重连。
const props = defineProps<{
  state: WsState
}>()

const emit = defineEmits<{ reconnect: [] }>()

/** 短暂重连延迟 2 秒再显示，避免切网抖动时横幅闪烁；明确断开（closed）立即提示。 */
const RECONNECT_BANNER_DELAY_MS = 2000
const reconnectElapsed = ref(false)
let reconnectTimer: ReturnType<typeof setTimeout> | null = null

function clearReconnectTimer(): void {
  if (reconnectTimer !== null) {
    clearTimeout(reconnectTimer)
    reconnectTimer = null
  }
}

watch(
  () => props.state,
  (state) => {
    clearReconnectTimer()
    reconnectElapsed.value = false
    if (state !== 'reconnecting') return
    reconnectTimer = setTimeout(() => {
      reconnectTimer = null
      reconnectElapsed.value = true
    }, RECONNECT_BANNER_DELAY_MS)
  },
  { immediate: true },
)

onBeforeUnmount(clearReconnectTimer)

const text = computed(() => {
  switch (props.state) {
    case 'connecting':
      return '正在连接服务器…'
    case 'reconnecting':
      return reconnectElapsed.value ? '正在重新连接…' : ''
    case 'closed':
      return '连接已断开，消息将在恢复后发送'
    default:
      return ''
  }
})

const hint = computed(() =>
  props.state === 'connecting'
    ? '可以继续查看历史消息'
    : '消息已在本机排队，恢复后自动发送',
)

/** 仅「已断开」状态可点重连；重连中由退避策略自动进行，不放假按钮。 */
const clickable = computed(() => props.state === 'closed')

function onClick(): void {
  if (clickable.value) emit('reconnect')
}
</script>

<template>
  <div v-if="text" class="connection-banner" role="status">
    <PhWarningCircle :size="16" weight="regular" class="connection-banner__icon" aria-hidden="true" />
    <span class="connection-banner__text">{{ text }}</span>
    <span class="connection-banner__hint">{{ hint }}</span>
    <button
      v-if="clickable"
      type="button"
      class="connection-banner__action ec-state-retry"
      @click="onClick"
    >
      重试连接
    </button>
  </div>
</template>

<style scoped>
.connection-banner {
  display: flex;
  flex: none;
  gap: var(--ec-space-2);
  align-items: center;
  min-height: 36px;
  padding: 6px var(--ec-space-4);
  border-bottom: 1px solid var(--ec-warning-border);
  background: var(--ec-warning-surface);
  color: var(--ec-warning);
  font-size: var(--ec-font-size-secondary);
}

.connection-banner__icon {
  flex: none;
}

.connection-banner__text {
  font-weight: var(--ec-font-weight-medium);
}

.connection-banner__hint {
  color: var(--ec-text-secondary);
}

/* U-09：尺寸、圆角、hover 与焦点环统一由 .ec-state-retry 提供，这里只保留警示色语义。 */
.connection-banner__action {
  margin-left: auto;
  border-color: var(--ec-warning);
  color: var(--ec-warning);
}

@media (max-width: 800px) {
  .connection-banner__hint {
    display: none;
  }
}
</style>
