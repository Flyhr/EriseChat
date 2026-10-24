<script setup lang="ts">
// 空状态占位（纯展示）：默认插槽放操作按钮。
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
  gap: var(--space-2);
  align-items: center;
  justify-content: center;
  padding: var(--space-6);
  color: var(--color-text-secondary);
}

.empty-state__title {
  margin: 0;
  font-size: 15px;
}

.empty-state__description {
  margin: 0;
  font-size: 13px;
}
</style>
