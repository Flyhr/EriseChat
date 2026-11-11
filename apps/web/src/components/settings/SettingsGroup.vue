<script setup lang="ts">
import { NCard } from 'naive-ui'

// 设置分组卡（SET-01）：分组标题 + 可选说明 + 行列表。
// 卡片容器改用 Naive UI NCard；:bordered=false + content-style 清零内边距，
// 视觉（表面色/边框/圆角/阴影）由下方 scoped 规则锚回 --ec-* token，避免组件库主题漂移。
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
    <NCard
      class="ec-set-group__card"
      :bordered="false"
      :content-style="{ padding: 0, fontSize: 'inherit', lineHeight: 'inherit' }"
    >
      <slot />
    </NCard>
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

/* NCard 自带圆角/表面色/阴影，此处统一锚回项目 token；
   font/color/word-break 复位为继承，避免组件库默认排版覆盖设置行文字。 */
.ec-set-group__card {
  overflow: hidden;
  color: inherit;
  font: inherit;
  word-break: normal;
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
