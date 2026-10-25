<script setup lang="ts">
import { computed, nextTick, onMounted, ref, watch } from 'vue'
import type { Message } from '../types/frames'
import type { PendingMessage } from '../stores/messages'
import { lastOwnMessageOf, readStateOf, type ReadReceiptState } from '../core/readReceipt'
import MessageBubble from './MessageBubble.vue'

// 消息列表（纯展示）：按 seq 升序渲染，自身消息靠右；pending 为尚未收到 ack 的本地乐观消息。
// M3-09：向上 emit near-bottom-change（父组件据此推进已读）；单聊中自己发出的最新一条显示对端「已读/未读」。
const props = defineProps<{
  messages: readonly Message[]
  pending?: readonly PendingMessage[]
  selfUserId?: string
  conversationId?: string | null
  /** 单聊对端已上报的已读 seq（无记录时 undefined，展示「未读」）。 */
  peerReadSeq?: number
  /** 是否展示已读回执（仅单聊；群聊 v1 无已读详情）。 */
  showReadReceipt?: boolean
  hasMore?: boolean
  loading?: boolean
}>()

const emit = defineEmits<{
  loadMore: []
  retry: [clientMsgId: string]
  'near-bottom-change': [atBottom: boolean]
}>()

const container = ref<HTMLDivElement | null>(null)
/** 用户是否停留在底部附近（新消息到达/图片撑高列表时只在此状态自动滚到底，不打断向上翻阅）。 */
const stickToBottom = ref(true)

/** 自己发出的最新一条消息 ID（单聊只在该条后展示已读/未读）。 */
const lastOwnMessageId = computed(
  () => lastOwnMessageOf(props.messages, props.selfUserId)?.messageId ?? '',
)

function receiptOf(message: Message): ReadReceiptState | undefined {
  if (!props.showReadReceipt || message.messageId !== lastOwnMessageId.value) return undefined
  return readStateOf(props.peerReadSeq, message.seq)
}

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

function emitNearBottom(atBottom: boolean): void {
  emit('near-bottom-change', atBottom)
}

function onScroll(): void {
  stickToBottom.value = isNearBottom()
  emitNearBottom(stickToBottom.value)
}

/** 图片缩略图加载完成后：用户原本在底部则跟随滚到底，否则保持当前位置。 */
function onImageLoaded(): void {
  if (!stickToBottom.value) return
  void nextTick(scrollToBottom)
}

// 切换会话：重新贴底并上报，父组件据此推进新会话的已读。
watch(
  () => props.conversationId ?? '',
  () => {
    stickToBottom.value = true
    void nextTick(() => {
      scrollToBottom()
      emitNearBottom(true)
    })
  },
)

// 收到新消息/加载历史：只有用户原本在底部附近才自动滚到底，向上翻阅旧消息时不打断。
watch(
  () => props.messages.length,
  () => {
    if (!stickToBottom.value) return
    void nextTick(scrollToBottom)
  },
)

// 自己发送新消息（pending 增加）时总是贴底。
watch(
  () => props.pending?.length ?? 0,
  (next, previous) => {
    if (next <= previous) return
    stickToBottom.value = true
    void nextTick(() => {
      scrollToBottom()
      emitNearBottom(true)
    })
  },
)

onMounted(() => {
  void nextTick(() => {
    scrollToBottom()
    emitNearBottom(true)
  })
})
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
      :receipt="receiptOf(message)"
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
