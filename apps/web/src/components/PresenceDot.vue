<script setup lang="ts">
import { computed } from 'vue'

// 在线状态圆点（M3-09）：在线绿色、离线灰色、未知浅灰。
// v1 无初始查询接口，未知状态不要显示成离线（protocol.md §4.7）。
const props = defineProps<{
  online?: boolean
  title?: string
}>()

const modifier = computed(() =>
  props.online === true ? 'online' : props.online === false ? 'offline' : 'unknown',
)

const defaultTitle = computed(() =>
  props.online === true ? '在线' : props.online === false ? '离线' : '未知',
)
</script>

<template>
  <span
    class="presence-dot"
    :class="`presence-dot--${modifier}`"
    :title="title ?? defaultTitle"
    aria-hidden="true"
  />
</template>

<style scoped>
.presence-dot {
  display: inline-block;
  flex: none;
  width: 8px;
  height: 8px;
  border-radius: var(--ec-radius-full);
}

.presence-dot--online {
  background: var(--ec-success);
}

.presence-dot--offline {
  background: var(--ec-text-secondary);
}

.presence-dot--unknown {
  background: var(--ec-border);
}
</style>
