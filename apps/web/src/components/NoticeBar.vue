<script setup lang="ts">
// 轻量页内提示条（UI-02 批次4，计划 §5.3 / §5.6 断线提示）：
// 断线/重连/恢复、被移出群聊等就地提示；不遮挡消息、不替代全局 ConnectionBanner。
// 纯展示：文案与关闭动作由页面决定，避免伪造连接状态。
withDefaults(
  defineProps<{
    text: string
    /** info=普通提示（浅蓝）、warning=网络异常（浅灰蓝+橙字）、success=连接恢复。 */
    tone?: 'info' | 'warning' | 'success'
    /** 右侧操作文案（如「知道了」）；为空不显示按钮。 */
    actionLabel?: string
  }>(),
  { tone: 'info', actionLabel: '' },
)

const emit = defineEmits<{ action: [] }>()
</script>

<template>
  <div class="ec-notice" :class="`ec-notice--${tone}`" role="status">
    <span class="ec-notice__dot" aria-hidden="true" />
    <span class="ec-notice__text">{{ text }}</span>
    <button
      v-if="actionLabel"
      type="button"
      class="ec-notice__action"
      @click="emit('action')"
    >
      {{ actionLabel }}
    </button>
  </div>
</template>

<style scoped>
.ec-notice {
  display: flex;
  flex-shrink: 0;
  gap: var(--ec-space-2);
  align-items: center;
  padding: 6px var(--ec-space-4);
  background: var(--ec-brand-050);
  color: var(--ec-brand-600);
  font-size: var(--ec-font-size-secondary);
}

.ec-notice--warning {
  background: var(--ec-surface-hover);
  color: var(--ec-warning);
}

.ec-notice--success {
  background: var(--ec-brand-050);
  color: var(--ec-brand-600);
}

.ec-notice__dot {
  flex: none;
  width: 6px;
  height: 6px;
  border-radius: var(--ec-radius-full);
  background: currentcolor;
}

.ec-notice__text {
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.ec-notice__action {
  flex: none;
  margin-left: auto;
  padding: 0;
  border: none;
  background: transparent;
  color: inherit;
  font-size: inherit;
  text-decoration: underline;
  text-underline-offset: 2px;
  cursor: pointer;
}
</style>
