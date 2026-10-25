<script setup lang="ts">
import { computed } from 'vue'
import type { Message } from '../types/frames'
import type { PendingMessage, PendingStatus } from '../stores/messages'

// 消息气泡（纯展示）。文本只用插值渲染（自动转义），禁止 v-html（protocol.md §4.3 安全要求）。
// pending 消息显示「发送中/失败重发」状态；失败时点击气泡上的按钮重发（复用 clientMsgId）。
const props = withDefaults(
  defineProps<{
    message: Message | PendingMessage
    self?: boolean
    status?: PendingStatus
  }>(),
  { self: false },
)

const emit = defineEmits<{
  retry: []
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
  <div
    class="message-bubble"
    :class="{
      'message-bubble--self': self,
      'message-bubble--sending': status === 'sending',
      'message-bubble--failed': status === 'failed',
    }"
  >
    <p class="message-bubble__text">{{ contentText }}</p>
    <time class="message-bubble__time">{{ timeText }}</time>
    <span v-if="status === 'sending'" class="message-bubble__pending">发送中…</span>
    <button
      v-else-if="status === 'failed'"
      class="message-bubble__retry"
      type="button"
      @click="emit('retry')"
    >
      发送失败，点击重发
    </button>
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

.message-bubble--sending {
  opacity: 0.75;
}

.message-bubble--failed {
  border: 1px solid var(--color-danger);
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

.message-bubble__pending {
  display: block;
  color: var(--color-text-secondary);
  font-size: 11px;
  text-align: right;
}

.message-bubble__retry {
  display: block;
  margin-left: auto;
  padding: 0;
  border: none;
  background: none;
  color: var(--color-danger);
  font-size: 11px;
  cursor: pointer;
}
</style>
