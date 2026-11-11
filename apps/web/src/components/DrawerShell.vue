<script setup lang="ts">
import { NDrawer, NDrawerContent } from 'naive-ui'
import { watch } from 'vue'

// 右侧抽屉公共壳（UI-D：外壳改用 Naive UI NDrawer / NDrawerContent，保留 open / title / close 契约）。
// Esc、遮罩关闭与焦点 trap 交由组件库处理；关闭后焦点归还触发元素由下方 watch 保留。
const props = withDefaults(defineProps<{ open: boolean; title?: string }>(), { title: '' })
const emit = defineEmits<{ close: [] }>()

let previousFocus: HTMLElement | null = null

watch(
  () => props.open,
  (open) => {
    if (open) {
      previousFocus = document.activeElement instanceof HTMLElement ? document.activeElement : null
    } else if (previousFocus) {
      previousFocus.focus()
      previousFocus = null
    }
  },
  { immediate: true },
)

function onShowUpdate(show: boolean): void {
  if (!show) emit('close')
}
</script>

<template>
  <NDrawer
    :show="open"
    :width="288"
    placement="right"
    :trap-focus="true"
    :close-on-esc="true"
    @update:show="onShowUpdate"
    @mask-click="emit('close')"
  >
    <NDrawerContent :title="title || undefined" closable @close="emit('close')">
      <slot />
    </NDrawerContent>
  </NDrawer>
</template>
