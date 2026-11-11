<script setup lang="ts">
import { onBeforeUnmount } from 'vue'
import { useMessage } from 'naive-ui'
import { setToastHandler, type ToastKind } from '../core/toast'

// UI-D：把 core/toast.ts 的全局 showToast 桥接到 Naive UI message，替换自绘 AppToast 渲染层。
// 必须挂在 NMessageProvider 内部；卸载时注销，使 showToast 回退到模块内队列。
// 所有调用方仍使用 showToast(message, kind)，无需改动。
const message = useMessage()

function notify(text: string, kind: ToastKind): void {
  if (kind === 'error') message.error(text)
  else if (kind === 'success') message.success(text)
  else message.info(text)
}

setToastHandler(notify)
onBeforeUnmount(() => setToastHandler(null))
</script>

<template>
  <span hidden aria-hidden="true" />
</template>
