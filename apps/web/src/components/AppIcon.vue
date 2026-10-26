<script setup lang="ts">
import { computed } from 'vue'
import { APP_ICONS, type AppIconName } from '../core/icons'

// 统一内联 SVG 图标（UI-01，计划 §3.2 / §16.4）：18px 基准、1.8 描边、currentColor；
// 不引入图标库；装饰性图标 aria-hidden，语义由外层按钮/链路的 aria-label、文本提供。
const props = withDefaults(defineProps<{ name: AppIconName; size?: number }>(), { size: 18 })
const icon = computed(() => APP_ICONS[props.name])
</script>

<template>
  <svg
    class="ec-icon"
    :width="size"
    :height="size"
    viewBox="0 0 24 24"
    fill="none"
    stroke="currentColor"
    stroke-width="1.8"
    stroke-linecap="round"
    stroke-linejoin="round"
    aria-hidden="true"
    focusable="false"
  >
    <path v-for="path in icon.paths ?? []" :key="path" :d="path" />
    <circle
      v-for="circle in icon.circles ?? []"
      :key="`${circle.cx}-${circle.cy}-${circle.r}`"
      :cx="circle.cx"
      :cy="circle.cy"
      :r="circle.r"
    />
  </svg>
</template>

<style scoped>
.ec-icon {
  display: block;
  flex: none;
}
</style>
