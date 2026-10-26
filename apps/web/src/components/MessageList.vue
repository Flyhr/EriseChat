<script setup lang="ts">
import { computed, nextTick, onMounted, ref, watch } from 'vue'
import type { ConversationMember, Message } from '../types/frames'
import type { PendingMessage } from '../stores/messages'
import { buildMessageItems, shouldShowNewMessageBadge } from '../core/chatLayout'
import { lastOwnMessageOf, readStateOf, type ReadReceiptState } from '../core/readReceipt'
import MessageBubble from './MessageBubble.vue'

// 消息列表（纯展示，UI-02 批次2）：按 seq 升序渲染（输入即有序），跨天插入日期分隔；
// 群聊他人消息展示头像+昵称；用户向上翻阅时收到新消息只累加「有新消息 ↓」浮标，不强制滚底。
// 会话切换/自己发送/pending 增加/图片加载仍按既有规则贴底（M2-05/M3-09 行为保留）。
const props = defineProps<{
  messages: readonly Message[]
  pending?: readonly PendingMessage[]
  selfUserId?: string
  conversationId?: string | null
  /** 会话成员（群聊用于发送者昵称；单聊可不传）。 */
  members?: readonly ConversationMember[]
  /** 是否群聊：群聊才展示发送者头像/昵称。 */
  isGroup?: boolean
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
/** 未贴底期间累计收到的新消息条数（>0 时显示「有新消息 ↓」浮标）。 */
const newMessageCount = ref(0)

const items = computed(() => buildMessageItems(props.messages))

/** 自己发出的最新一条消息 ID（单聊只在该条后展示已读/未读）。 */
const lastOwnMessageId = computed(
  () => lastOwnMessageOf(props.messages, props.selfUserId)?.messageId ?? '',
)

const memberNameById = computed(() => {
  const map = new Map<string, string>()
  for (const member of props.members ?? []) {
    map.set(member.userId, member.displayName)
  }
  return map
})

function receiptOf(message: Message): ReadReceiptState | undefined {
  if (!props.showReadReceipt || message.messageId !== lastOwnMessageId.value) return undefined
  return readStateOf(props.peerReadSeq, message.seq)
}

function showSenderOf(message: Message): boolean {
  return Boolean(props.isGroup) && message.senderId !== props.selfUserId
}

function senderNameOf(message: Message): string {
  return memberNameById.value.get(message.senderId) ?? ''
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
  if (stickToBottom.value) newMessageCount.value = 0
  emitNearBottom(stickToBottom.value)
}

/** 点击「有新消息 ↓」：回到最新消息并清零浮标。 */
function jumpToBottom(): void {
  stickToBottom.value = true
  newMessageCount.value = 0
  void nextTick(() => {
    scrollToBottom()
    emitNearBottom(true)
  })
}

/** 图片缩略图加载完成后：用户原本在底部则跟随滚到底，否则保持当前位置。 */
function onImageLoaded(): void {
  if (!stickToBottom.value) return
  void nextTick(scrollToBottom)
}

// 切换会话：重新贴底、清浮标并上报，父组件据此推进新会话的已读。
watch(
  () => props.conversationId ?? '',
  () => {
    stickToBottom.value = true
    newMessageCount.value = 0
    void nextTick(() => {
      scrollToBottom()
      emitNearBottom(true)
    })
  },
)

// 消息数量变化：贴底时跟随滚到底；向上翻阅时累计「有新消息」浮标（不强制滚动，计划 §4.3）。
watch(
  () => props.messages.length,
  (next, previous) => {
    const delta = next - previous
    if (delta <= 0) return
    if (stickToBottom.value) {
      void nextTick(scrollToBottom)
      return
    }
    newMessageCount.value += delta
  },
)

// 自己发送新消息（pending 增加）时总是贴底并清浮标。
watch(
  () => props.pending?.length ?? 0,
  (next, previous) => {
    if (next <= previous) return
    stickToBottom.value = true
    newMessageCount.value = 0
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
  <div class="message-list-wrap">
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

      <template v-for="item in items" :key="item.key">
        <div v-if="item.kind === 'separator'" class="message-list__date">
          <span>{{ item.label }}</span>
        </div>
        <MessageBubble
          v-else
          :message="item.message"
          :self="item.message.senderId === selfUserId"
          :show-sender="showSenderOf(item.message)"
          :sender-name="senderNameOf(item.message)"
          :receipt="receiptOf(item.message)"
          @image-loaded="onImageLoaded"
        />
      </template>

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

    <button
      v-if="shouldShowNewMessageBadge(stickToBottom, newMessageCount)"
      type="button"
      class="message-list__badge"
      @click="jumpToBottom"
    >
      有新消息（{{ newMessageCount }}）↓
    </button>
  </div>
</template>

<style scoped>
.message-list-wrap {
  position: relative;
  display: flex;
  flex: 1;
  flex-direction: column;
  min-height: 0;
}

.message-list {
  display: flex;
  flex: 1;
  flex-direction: column;
  gap: var(--ec-space-4);
  min-height: 0;
  padding: 18px var(--ec-space-5) var(--ec-space-3);
  overflow-y: auto;
  /* UI-04 批次3：移动端触摸滚动保持原生惯性，滚动链不传给页面。 */
  -webkit-overflow-scrolling: touch;
  overscroll-behavior: contain;
  /* 「澄蓝」主题：左上 brand 光晕 + 右下 indigo 光晕 + 纵向浅蓝渐变。 */
  background: var(--ec-chat-gradient);
}

.message-list__more {
  align-self: center;
  padding: var(--ec-space-1) var(--ec-space-3);
  border: 1px solid var(--ec-border);
  border-radius: var(--ec-radius-full);
  background: var(--ec-surface);
  color: var(--ec-text-secondary);
  font-size: var(--ec-font-size-secondary);
  box-shadow: var(--ec-shadow-sm);
  cursor: pointer;
}

.message-list__more:disabled {
  opacity: 0.6;
  cursor: not-allowed;
}

.message-list__top {
  margin: 0 auto;
  color: var(--ec-text-secondary);
  font-size: var(--ec-font-size-secondary);
}

/* 日期分隔：居中胶囊（替换原先的左右细线，视觉更轻）。 */
.message-list__date {
  align-self: center;
  margin: var(--ec-space-1) 0;
  padding: 3px var(--ec-space-3);
  border-radius: var(--ec-radius-full);
  background: rgba(241, 245, 253, 0.92);
  color: var(--ec-text-secondary);
  font-size: var(--ec-font-size-small);
}

/* 「有新消息 ↓」浮标：绝对定位在列表底部（不遮挡输入区），品牌蓝实底。 */
.message-list__badge {
  position: absolute;
  bottom: var(--ec-space-3);
  left: 50%;
  padding: var(--ec-space-1) var(--ec-space-3);
  border: none;
  border-radius: var(--ec-radius-full);
  background: var(--ec-brand-600);
  box-shadow: 0 6px 16px rgba(37, 99, 235, 0.28);
  color: var(--ec-text-inverse);
  font-size: var(--ec-font-size-secondary);
  font-weight: var(--ec-font-weight-medium);
  white-space: nowrap;
  cursor: pointer;
  transform: translateX(-50%);
}

/* UI-05 移动端：消息区左右留白收紧、行距略增，日期分隔与浮标保持可点。 */
@media (max-width: 800px) {
  .message-list {
    gap: 14px;
    padding: var(--ec-space-3) var(--ec-space-3) var(--ec-space-2);
  }

  .message-list__badge {
    bottom: var(--ec-space-2);
    min-height: 36px;
    padding: 0 var(--ec-space-4);
  }

  .message-list__date {
    font-size: var(--ec-font-size-small);
  }
}
</style>
