<script setup lang="ts">
import { nextTick, ref, watch } from 'vue'
import type { Message } from '../types/frames'
import type { PendingMessage } from '../stores/messages'
import MessageBubble from './MessageBubble.vue'

// 消息列表（纯展示）：按 seq 升序渲染，自身消息靠右；
// pending 为尚未收到 ack 的本地乐观消息（含发送中/失败状态）。
// 向上翻页由父组件接 load-more（REST 历史分页）后追加，列表自动滚到底部。
const props = defineProps<{
  messages: readonly Message[]
  pending?: readonly PendingMessage[]
  selfUserId?: string
  hasMore?: boolean
  loading?: boolean
}>()

const emit = defineEmits<{
  loadMore: []
  retry: [clientMsgId: string]
}>()

const container = ref<HTMLDivElement | null>(null)
/** 用户是否停留在底部附近（图片异步加载撑高列表时，只有此时才自动滚到底）。 */
const stickToBottom = ref(true)

function scrollToBottom(): void {
  const element = container.value
  if (!element) return
  element.scrollTop = element.scrollHeight
}

function isNearBottom(): boolean {
  const element = container.value
  if (!element) return true
  return element.scrollHeight - element.scrollTop - element.clientHeight < 80
}

function onScroll(): void {
  stickToBottom.value = isNearBottom()
}

/** 图片缩略图加载完成后：用户原本在底部则跟随滚到底，否则保持当前位置（避免打断向上翻阅）。 */
function onImageLoaded(): void {
  if (!stickToBottom.value) return
  void nextTick(() => {
    scrollToBottom()
  })
}

// 消息数量变化（收到/发送/加载历史）或切换会话时滚到底部。
watch(
  () => [
    props.messages.length,
    props.pending?.length ?? 0,
    props.messages[0]?.messageId ?? '',
  ],
  () => {
    stickToBottom.value = true
    void nextTick(scrollToBottom)
  },
  { immediate: true },
)
</script>

<template>
  <div ref="container" class="message-list" @scroll="onScroll">
    <button
      v-if="hasMore"
      class="message-list__more"
      type="button"
      :disabled="loading"
      @click="emit('loadMore')"
    >
      {{ loading ? '加载中…' : '加载更早消息' }}
    </button>
    <p v-else-if="messages.length > 0" class="message-list__top">没有更早的消息了</p>

    <MessageBubble
      v-for="message in messages"
      :key="message.messageId"
      :message="message"
      :self="message.senderId === selfUserId"
      @image-loaded="onImageLoaded"
    />
    <MessageBubble
      v-for="item in pending ?? []"
      :key="item.clientMsgId"
      :message="item"
      :self="true"
      :status="item.status"
      @retry="emit('retry', item.clientMsgId)"
      @image-loaded="onImageLoaded"
    />
  </div>
</template>

<style scoped>
.message-list {
  display: flex;
  flex: 1;
  flex-direction: column;
  gap: var(--space-3);
  min-height: 0;
  padding: var(--space-4);
  overflow-y: auto;
}

.message-list__more {
  align-self: center;
  padding: var(--space-1) var(--space-3);
  border: 1px solid var(--color-border);
  border-radius: var(--radius-full);
  background: var(--color-surface);
  color: var(--color-text-secondary);
  font-size: 12px;
}

.message-list__more:disabled {
  opacity: 0.6;
  cursor: not-allowed;
}

.message-list__top {
  margin: 0 auto;
  color: var(--color-text-secondary);
  font-size: 12px;
}
</style>
