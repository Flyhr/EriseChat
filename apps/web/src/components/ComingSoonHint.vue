<script setup lang="ts">
// 占位入口（UI-01，计划 §5.1 / 附录 A）：灰显禁用 + 「即将上线」。
// 不绑定点击行为、不发请求、不返回假数据；接入位置需在本地计划附录 A 登记。
withDefaults(
  defineProps<{
    label?: string
    hint?: string
    layout?: 'item' | 'inline'
    /** false 时只保留 disabled 语义与 title 提示（用于窄导航栏等空间有限处） */
    showHint?: boolean
  }>(),
  { hint: '即将上线', layout: 'item', showHint: true },
)
</script>

<template>
  <span
    class="ec-coming-soon"
    :class="[`ec-coming-soon--${layout}`, { 'ec-coming-soon--quiet': !showHint }]"
    aria-disabled="true"
    :title="`${label ? `${label}：` : ''}${hint}`"
  >
    <slot />
    <span v-if="showHint" class="ec-coming-soon__hint">{{ hint }}</span>
  </span>
</template>

<style scoped>
.ec-coming-soon {
  display: flex;
  gap: var(--ec-space-2);
  align-items: center;
  color: var(--ec-disabled);
  cursor: not-allowed;
  user-select: none;
}

.ec-coming-soon--inline {
  justify-content: space-between;
  width: 100%;
}

.ec-coming-soon__hint {
  flex: none;
  padding: 1px 6px;
  border: 1px solid var(--ec-border);
  border-radius: var(--ec-radius-full);
  color: var(--ec-text-tertiary);
  font-size: var(--ec-font-size-small);
}
</style>
