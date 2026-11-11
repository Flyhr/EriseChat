<script setup lang="ts">
import { NSwitch } from 'naive-ui'

// 开关（UI-D P4）：内部改用 Naive UI NSwitch，保留原 modelValue / label / disabled 与 emit API，
// 调用方无需改动即可完成自绘组件替换；品牌色由 NConfigProvider 的 themeOverrides 提供。
const props = defineProps<{
  modelValue: boolean
  /** 无障碍名称，随状态切换为「开启/关闭 xxx」。 */
  label: string
  disabled?: boolean
}>()

const emit = defineEmits<{ (event: 'update:modelValue', value: boolean): void }>()

function onUpdate(value: boolean): void {
  emit('update:modelValue', value)
}
</script>

<template>
  <NSwitch
    :value="props.modelValue"
    :disabled="props.disabled"
    :aria-label="props.modelValue ? `关闭${props.label}` : `开启${props.label}`"
    @update:value="onUpdate"
  />
</template>
