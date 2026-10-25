<script setup lang="ts">
import { computed, ref } from 'vue'
import type { Message } from '../types/frames'
import type { PendingMessage, PendingStatus } from '../stores/messages'
import MessageAttachment from './MessageAttachment.vue'
import ImagePreview from './ImagePreview.vue'
import { systemMessageText } from '../core/groupChat'

// 消息气泡（纯展示）。文本只用插值渲染（自动转义），禁止 v-html（protocol.md §4.3 安全要求）。
// pending 消息显示「发送中/失败重发」状态；失败时点击气泡上的按钮重发（复用 clientMsgId）。
// M3-07：image/file 消息改用 MessageAttachment（鉴权 blob 渲染），点击图片打开 ImagePreview 大图。
// M3-08：system 消息（群事件）不带气泡、居中灰色小字；文案优先 content.text，缺失时按事件名兜底。
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
  /** 图片加载完成（父级在用户停留底部时重新滚到底）。 */
  imageLoaded: []
}>()

const contentText = computed(() => {
  const content = props.message.content
  if (typeof content === 'string') return content
  if ('fileId' in content) return content.name
  return content.text ?? '[系统消息]'
})

/** system 消息：居中灰字、无气泡/头像，不参与附件/时间/状态渲染。 */
const isSystem = computed(() => props.message.msgType === 'system')

const systemText = computed(() => systemMessageText(props.message.content))

/** 仅 image/file 且 content 为 FileContent 时用附件组件渲染；异常数据回退文本。 */
const showAttachment = computed(() => {
  if (props.message.msgType !== 'image' && props.message.msgType !== 'file') return false
  const content = props.message.content
  return typeof content === 'object' && content !== null && 'fileId' in content
})

const previewOpen = ref(false)
const previewUrl = ref('')
const previewName = ref('')

function onPreview(payload: { url: string; name: string }): void {
  previewUrl.value = payload.url
  previewName.value = payload.name
  previewOpen.value = true
}

function closePreview(): void {
  previewOpen.value = false
}

const timeText = computed(() =>
  new Date(props.message.createdAt).toLocaleTimeString('zh-CN', {
    hour: '2-digit',
    minute: '2-digit',
  }),
)
</script>

<template>
  <div v-if="isSystem" class="message-system">
    <span class="message-system__text">{{ systemText }}</span>
  </div>
  <div
    v-else
    class="message-bubble"
    :class="{
      'message-bubble--self': self,
      'message-bubble--sending': status === 'sending',
      'message-bubble--failed': status === 'failed',
    }"
  >
    <MessageAttachment
      v-if="showAttachment"
      :message="message"
      @preview="onPreview"
      @loaded="emit('imageLoaded')"
    />
    <p v-else class="message-bubble__text">{{ contentText }}</p>
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
    <ImagePreview
      :open="previewOpen"
      :url="previewUrl"
      :name="previewName"
      @close="closePreview"
    />
  </div>
</template>

<style scoped>
.message-system {
  display: flex;
  justify-content: center;
  padding: 0 var(--space-2);
}

.message-system__text {
  color: var(--color-text-secondary);
  font-size: 12px;
  text-align: center;
  word-break: break-word;
}

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
