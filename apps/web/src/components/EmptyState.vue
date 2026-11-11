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

/* UI-D P3：空状态视觉锚点放大并引入品牌色，避免空页面过于单薄。 */
.empty-state__icon {
  display: grid;
  width: 56px;
  height: 56px;
  margin-bottom: var(--ec-space-2);
  place-items: center;
  border: 1px solid var(--ec-brand-100);
  border-radius: var(--ec-radius-panel);
  background: var(--ec-brand-050);
  box-shadow: 0 0 0 5px var(--ec-surface);
  color: var(--ec-brand-600);
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

/* UI-05 移动端：空态图标更大、留白收紧，避免小屏内容显得零碎。 */
@media (max-width: 800px) {
  .empty-state {
    padding: var(--ec-space-6) var(--ec-space-4);
  }

  .empty-state__icon {
    width: 56px;
    height: 56px;
  }

  .empty-state__title {
    font-size: var(--ec-font-size-mobile-header);
  }
}
</style>
