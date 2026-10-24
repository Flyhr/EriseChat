<script setup lang="ts">
import { computed } from 'vue'
import type { Message } from '../types/frames'

// 消息气泡（纯展示）。文本只用插值渲染（自动转义），禁止 v-html（protocol.md §4.3 安全要求）。
const props = defineProps<{
  message: Message
  self?: boolean
}>()

const contentText = computed(() => {
  const content = props.message.content
  if (typeof content === 'string') return content
  if ('fileId' in content) return `[文件] ${content.name}（${Math.ceil(content.size / 1024)} KB）`
  return content.text ?? '[系统消息]'
})

const timeText = computed(() =>
  new Date(props.message.createdAt).toLocaleTimeString('zh-CN', {
    hour: '2-digit',
    minute: '2-digit',
  }),
)
</script>

<template>
  <div class="message-bubble" :class="{ 'message-bubble--self': self }">
    <p class="message-bubble__text">{{ contentText }}</p>
    <time class="message-bubble__time">{{ timeText }}</time>
  </div>
</template>

<style scoped>
.message-bubble {
  max-width: 72%;
  padding: var(--space-2) var(--space-3);
  border-radius: var(--radius-md);
  background: var(--color-bubble-other);
  box-shadow: var(--shadow-sm);
}

.message-bubble--self {
  margin-left: auto;
  background: var(--color-bubble-self);
}

.message-bubble__text {
  margin: 0;
  white-space: pre-wrap;
  word-break: break-word;
}

.message-bubble__time {
  display: block;
  margin-top: var(--space-1);
  color: var(--color-text-secondary);
  font-size: 11px;
  text-align: right;
}
</style>
