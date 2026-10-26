<script setup lang="ts">
import AppIcon from './AppIcon.vue'
import type { AppIconName } from '../core/icons'

// 空状态占位（UI-01 token 化；UI-03 批次4 增加可选图标与操作区）。
// 纯展示组件：不发起请求；默认插槽放操作按钮，无操作时不渲染额外区域。
defineProps<{
  title?: string
  description?: string
  /** 可选图标（与 AppIcon 图标名一致）；不传则只显示文字。 */
  icon?: AppIconName
}>()
</script>

<template>
  <div class="empty-state">
    <span v-if="icon" class="empty-state__icon" aria-hidden="true">
      <AppIcon :name="icon" :size="26" />
    </span>
    <p class="empty-state__title">{{ title ?? '暂无内容' }}</p>
    <p v-if="description" class="empty-state__description">{{ description }}</p>
    <div v-if="$slots.default" class="empty-state__actions">
      <slot />
    </div>
  </div>
</template>

<style scoped>
.empty-state {
  display: flex;
  flex-direction: column;
  gap: var(--ec-space-2);
  align-items: center;
  justify-content: center;
  max-width: 460px;
  margin: 0 auto;
  padding: var(--ec-space-8) var(--ec-space-6);
  color: var(--ec-text-secondary);
  text-align: center;
}

.empty-state__icon {
  display: grid;
  width: 48px;
  height: 48px;
  margin-bottom: var(--ec-space-1);
  place-items: center;
  border: 1px solid var(--ec-border);
  border-radius: var(--ec-radius-full);
  background: var(--ec-nav-bg);
  color: var(--ec-text-secondary);
}

.empty-state__title {
  margin: 0;
  color: var(--ec-text-primary);
  font-size: 15px;
  font-weight: var(--ec-font-weight-medium);
}

.empty-state__description {
  margin: 0;
  color: var(--ec-text-secondary);
  font-size: var(--ec-font-size-secondary);
  line-height: 1.6;
}

.empty-state__actions {
  display: flex;
  gap: var(--ec-space-2);
  margin-top: var(--ec-space-2);
}
</style>
