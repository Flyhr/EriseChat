<script setup lang="ts">
import { NInput } from 'naive-ui'
import AppIcon from './AppIcon.vue'

// 会话列表与联系人页共用的搜索输入框（UI-D：内部改用 Naive UI NInput，保留原 props/emit API）。
// 胶囊外观由 :deep() 覆盖组件库样式，继续由 --ec-* token 控制视觉。
const props = withDefaults(
  defineProps<{
    modelValue: string
    placeholder?: string
    ariaLabel?: string
    maxlength?: number
  }>(),
  { placeholder: '搜索', ariaLabel: '搜索', maxlength: undefined },
)

const emit = defineEmits<{
  'update:modelValue': [value: string]
  focus: []
  escape: []
}>()

function onUpdate(value: string): void {
  emit('update:modelValue', value)
}
</script>

<template>
  <div class="ec-search-input">
    <NInput
      :value="props.modelValue"
      :placeholder="props.placeholder"
      :aria-label="props.ariaLabel"
      :maxlength="props.maxlength"
      @update:value="onUpdate"
      @focusin="emit('focus')"
      @keydown.esc.prevent="emit('escape')"
    >
      <template #suffix>
        <AppIcon name="search" :size="16" class="ec-search-input__icon" />
      </template>
    </NInput>
  </div>
</template>

<style scoped>
.ec-search-input {
  min-width: 0;
}

.ec-search-input :deep(.n-input) {
  height: 32px;
  border: 1px solid transparent;
  border-radius: var(--ec-radius-full);
  background: var(--ec-nav-bg);
  transition:
    background var(--ec-transition-fast) var(--ec-ease),
    border-color var(--ec-transition-fast) var(--ec-ease);
}

/* 悬停效果仅在鼠标精确指针设备生效，触屏不产生粘滞 hover。 */
@media (hover: hover) and (pointer: fine) {
  .ec-search-input:hover :deep(.n-input) {
    background: var(--ec-surface-hover);
  }
}

/* 聚焦态：只换细描边 + 白底，不使用全局 focus-visible 外环。 */
.ec-search-input:focus-within :deep(.n-input) {
  border-color: var(--ec-brand-500);
  background: var(--ec-surface);
}

.ec-search-input :deep(.n-input__input-el) {
  font-size: var(--ec-font-size-secondary);
}

.ec-search-input :deep(.n-input__input-el:focus-visible) {
  outline: none;
}

.ec-search-input__icon {
  flex: none;
  color: var(--ec-text-tertiary);
}

.ec-search-input:focus-within .ec-search-input__icon {
  color: var(--ec-brand-600);
}

@media (max-width: 800px) {
  .ec-search-input :deep(.n-input) {
    height: 36px;
  }
}
</style>
