<script setup lang="ts">
// 设置分组卡（SET-01）：分组标题 + 可选说明 + 行列表。
// 行之间不在此处画分割线：由页面用 :deep(.ec-set-row + .ec-set-row) 统一处理，
// 保证嵌套自定义块（如账号概览、主题选择）也能按需保留自己的间距。
defineProps<{
  /** 分组标题，例如「消息提醒」；省略时不渲染标题行。 */
  title?: string
  /** 分组说明，用于解释分组的作用或限制（例如「设置只保存在本机」）。 */
  description?: string
}>()
</script>

<template>
  <section class="ec-set-group">
    <header v-if="title || description" class="ec-set-group__header">
      <h3 v-if="title" class="ec-set-group__title">{{ title }}</h3>
      <p v-if="description" class="ec-set-group__desc">{{ description }}</p>
    </header>
    <div class="ec-set-group__card">
      <slot />
    </div>
  </section>
</template>

<style scoped>
.ec-set-group {
  display: flex;
  flex-direction: column;
  gap: var(--ec-space-2);
}

.ec-set-group__header {
  display: flex;
  flex-direction: column;
  gap: 2px;
  padding: 0 2px;
}

.ec-set-group__title {
  margin: 0;
  color: var(--ec-text-primary);
  font-size: var(--ec-font-size-secondary);
  font-weight: var(--ec-font-weight-semibold);
  letter-spacing: 0.02em;
}

.ec-set-group__desc {
  margin: 0;
  color: var(--ec-text-secondary);
  font-size: var(--ec-font-size-small);
  line-height: 1.5;
}

.ec-set-group__card {
  overflow: hidden;
  background: var(--ec-surface);
  border: 1px solid var(--ec-border);
  border-radius: var(--ec-radius-panel);
  box-shadow: var(--ec-shadow-sm);
}

@media (max-width: 800px) {
  .ec-set-group__card {
    border-radius: var(--ec-radius-mobile-card, var(--ec-radius-lg));
  }
}
</style>
