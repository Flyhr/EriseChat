<script setup lang="ts">
// 空状态占位（纯展示，UI-01 token 化）：默认插槽放操作按钮。
defineProps<{
  title?: string
  description?: string
}>()
</script>

<template>
  <div class="empty-state">
    <p class="empty-state__title">{{ title ?? '暂无内容' }}</p>
    <p v-if="description" class="empty-state__description">{{ description }}</p>
    <slot />
  </div>
</template>

<style scoped>
.empty-state {
  display: flex;
  flex-direction: column;
  gap: var(--ec-space-2);
  align-items: center;
  justify-content: center;
  padding: var(--ec-space-6);
  color: var(--ec-text-secondary);
  text-align: center;
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
  line-height: var(--ec-line-height-body);
}
</style>
