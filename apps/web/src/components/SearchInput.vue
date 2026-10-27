<script setup lang="ts">
import AppIcon from './AppIcon.vue'

// F-11 体验调整：会话列表与联系人页共用的搜索输入框（统一样式）。
// 放大镜放在输入框右侧内部；聚焦只给细描边（不是全局 focus 环），避免默认焦点框。
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

function onInput(event: Event): void {
  emit('update:modelValue', (event.target as HTMLInputElement).value)
}
</script>

<template>
  <div class="ec-search-input">
    <input
      :value="props.modelValue"
      type="search"
      class="ec-search-input__field"
      :placeholder="props.placeholder"
      :aria-label="props.ariaLabel"
      :maxlength="props.maxlength"
      @input="onInput"
      @focus="emit('focus')"
      @keydown.esc.prevent="emit('escape')"
    />
    <AppIcon name="search" :size="16" class="ec-search-input__icon" />
  </div>
</template>

<style scoped>
.ec-search-input {
  display: flex;
  align-items: center;
  min-width: 0;
  height: 32px;
  padding: 0 var(--ec-space-3);
  border: 1px solid transparent;
  border-radius: var(--ec-radius-full);
  background: var(--ec-nav-bg);
  transition:
    background var(--ec-transition-fast) var(--ec-ease),
    border-color var(--ec-transition-fast) var(--ec-ease);
}

/* 悬停效果仅在鼠标精确指针设备生效，触屏不产生粘滞 hover。 */
@media (hover: hover) and (pointer: fine) {
  .ec-search-input:hover {
    background: var(--ec-surface-hover);
  }
}

/* 聚焦态：只换细描边 + 白底，不使用全局 focus-visible 外环。 */
.ec-search-input:focus-within {
  border-color: var(--ec-brand-500);
  background: var(--ec-surface);
}

.ec-search-input__field {
  flex: 1;
  min-width: 0;
  padding: 0;
  border: none;
  outline: none;
  background: transparent;
  color: var(--ec-text-primary);
  font-family: inherit;
  font-size: var(--ec-font-size-secondary);
}

.ec-search-input__field::placeholder {
  color: var(--ec-text-tertiary);
}

/* 文本输入框在 Chrome 下始终命中 :focus-visible，这里显式取消外环。 */
.ec-search-input__field:focus-visible {
  outline: none;
}

.ec-search-input__field::-webkit-search-cancel-button {
  display: none;
}

.ec-search-input__icon {
  flex: none;
  margin-left: var(--ec-space-2);
  color: var(--ec-text-tertiary);
}

.ec-search-input:focus-within .ec-search-input__icon {
  color: var(--ec-brand-600);
}

@media (max-width: 800px) {
  .ec-search-input {
    height: 36px;
  }
}
</style>
