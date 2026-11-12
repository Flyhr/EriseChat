<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref } from 'vue'
import { loop, prefersReducedMotion, type MotionHandle } from '../core/motion'

// 三点加载反馈（U-18）：循环动画走 core/motion.ts，卸载时取消在途动画。
// reduced-motion 下不播放动画，改为静态三点 + 文案（AGENTS.md §14.9）。
withDefaults(defineProps<{ label?: string }>(), { label: '加载中…' })

const reduced = prefersReducedMotion()
const dots = ref<HTMLElement[]>([])
let handle: MotionHandle | null = null

onMounted(() => {
  if (reduced) return
  handle = loop(dots.value)
})

onBeforeUnmount(() => {
  handle?.cancel()
  handle = null
})
</script>

<template>
  <span class="ec-loading-dots" role="status" :aria-label="label">
    <span
      v-for="index in 3"
      :key="index"
      ref="dots"
      class="ec-loading-dots__dot"
      aria-hidden="true"
    />
    <span v-if="reduced" class="ec-state-text ec-loading-dots__label" aria-hidden="true">
      {{ label }}
    </span>
  </span>
</template>
