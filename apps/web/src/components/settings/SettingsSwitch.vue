<script setup lang="ts">
// 开关（SET-01，F-01 通知开关的通用化）：role=switch 受控组件。
// 视觉尺寸 44×24，移动端行高由父级行保证 ≥44px 触控区域；动效在 reduced-motion 下关闭。
defineProps<{
  modelValue: boolean
  /** 无障碍名称，随状态切换为「开启/关闭 xxx」。 */
  label: string
  disabled?: boolean
}>()

const emit = defineEmits<{ (event: 'update:modelValue', value: boolean): void }>()
</script>

<template>
  <button
    type="button"
    role="switch"
    class="ec-switch"
    :class="{ 'ec-switch--on': modelValue }"
    :aria-checked="modelValue"
    :aria-label="modelValue ? `关闭${label}` : `开启${label}`"
    :disabled="disabled"
    @click="emit('update:modelValue', !modelValue)"
  >
    <span class="ec-switch__thumb" aria-hidden="true" />
  </button>
</template>

<style scoped>
.ec-switch {
  position: relative;
  flex: none;
  width: 44px;
  height: 24px;
  padding: 0;
  border: none;
  border-radius: var(--ec-radius-full);
  background: var(--ec-border);
  cursor: pointer;
  transition: background-color var(--ec-transition-fast) var(--ec-ease);
}

.ec-switch--on {
  background: var(--ec-brand-600);
}

.ec-switch__thumb {
  position: absolute;
  top: 2px;
  left: 2px;
  width: 20px;
  height: 20px;
  border-radius: 50%;
  background: var(--ec-surface);
  box-shadow: 0 1px 2px rgb(15 23 42 / 18%);
  transition: transform var(--ec-transition-fast) var(--ec-ease);
}

.ec-switch--on .ec-switch__thumb {
  transform: translateX(20px);
}

.ec-switch:focus-visible {
  outline: var(--ec-focus-width) solid var(--ec-focus-ring);
  outline-offset: var(--ec-focus-offset);
}

.ec-switch:disabled {
  opacity: 0.6;
  cursor: not-allowed;
}

@media (prefers-reduced-motion: reduce) {
  .ec-switch,
  .ec-switch__thumb {
    transition: none;
  }
}
</style>
