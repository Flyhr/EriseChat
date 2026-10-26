<script setup lang="ts">
import { computed } from 'vue'

// 未读角标（UI-01：从 ConversationItem 内联样式抽为公共组件，视觉与逻辑不变）：
// 0/未传不渲染；1~99 原样；≥100 显示 99+。
const props = defineProps<{ count?: number }>()

const visible = computed(() => Boolean(props.count && props.count > 0))
const label = computed(() => (props.count && props.count > 99 ? '99+' : String(props.count ?? 0)))
</script>

<template>
  <span v-if="visible" class="ec-unread-badge" :aria-label="`${count} 条未读`">{{ label }}</span>
</template>

<style scoped>
.ec-unread-badge {
  flex: none;
  min-width: 18px;
  padding: 0 5px;
  border-radius: var(--ec-radius-full);
  /* 「澄蓝」主题：未读角标用品牌蓝实底，与列表选中色同一体系。 */
  background: var(--ec-brand-600);
  color: var(--ec-text-inverse);
  font-size: var(--ec-font-size-small);
  line-height: 18px;
  font-weight: var(--ec-font-weight-semibold);
  text-align: center;
}
</style>
