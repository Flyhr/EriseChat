<script setup lang="ts">
import { computed, nextTick, onMounted, ref, watch } from 'vue'
import type { ConversationMember, Message } from '../types/frames'
import type { PendingMessage } from '../stores/messages'
import { buildMessageItems, shouldShowNewMessageBadge } from '../core/chatLayout'
import {
  anchorScrollTop,
  clampWindow,
  expandOlderRange,
  initialWindowRange,
  isNearBottom as isNearBottomState,
  isNearTop,
  shouldAutoLoadOlder,
  type WindowRange,
} from '../core/messageWindow'
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
  /** F-25：会话成员昵称（用于把消息文本中的「@昵称」识别为提及）。 */
  mentionNames?: readonly string[]
  /** F-25：当前用户昵称（用于气泡「提到你」标记）。 */
  selfDisplayName?: string
}>()

const emit = defineEmits<{
  loadMore: []
  retry: [clientMsgId: string]
  'near-bottom-change': [atBottom: boolean]
  'preview-image': [messageId: string]
  /** F-18：右键/长按打开消息操作菜单（回复/撤回/编辑由会话页处理）。 */
  'open-actions': [messageId: string, position: { x: number; y: number }]
  /** F-09：请求定位到被引用的消息（由会话页处理未加载时的补拉）。 */
  'jump-to-message': [messageId: string]
}>()

const container = ref<HTMLDivElement | null>(null)
/** 用户是否停留在底部附近（新消息到达/图片撑高列表时只在此状态自动滚到底，不打断向上翻阅）。 */
const stickToBottom = ref(true)
/** 未贴底期间累计收到的新消息条数（>0 时显示「有新消息 ↓」浮标）。 */
const newMessageCount = ref(0)

// F-05 批次 2：分片渲染。只把窗口内的消息交给 DOM（默认 200 条、上限 400 行），
// 向上滚动先扩窗，窗口内无更早消息时再由父组件拉取历史；滚动位置用锚点补偿，
// 避免向上加载时视口内容跳变。贴底/浮标/已读上报语义保持 M2-05/M3-09 行为。
const INITIAL_WINDOW_SIZE = 200
const WINDOW_STEP = 200
const MAX_RENDERED_MESSAGES = 400

const windowRange = ref<WindowRange>(initialWindowRange(props.messages.length, INITIAL_WINDOW_SIZE))
/** 上一次渲染时的「首条消息 ID」，用于区分「前面插入了历史」与「末尾追加了新消息」。 */
const lastFirstMessageId = ref(props.messages[0]?.messageId ?? '')
/** 向上拉取历史前的滚动位置，历史返回后用它补偿 scrollTop。 */
const loadAnchor = ref<{ scrollTop: number; scrollHeight: number } | null>(null)

function totalMessages(): number {
  return props.messages.length
}

function currentRange(): WindowRange {
  return clampWindow(windowRange.value, totalMessages())
}

function firstMessageId(): string {
  return props.messages[0]?.messageId ?? ''
}

const renderedRange = computed(() => currentRange())

const renderedMessages = computed(() => {
  const range = renderedRange.value
  if (range.end <= range.start) return []
  return props.messages.slice(range.start, range.end)
})

const items = computed(() => {
  const range = renderedRange.value
  // 窗口首条消息的前一条（窗口外）用于初始化日期状态，避免同一天重复插入分隔。
  const previousMessage = range.start > 0 ? props.messages[range.start - 1] : null
  return buildMessageItems(renderedMessages.value, Date.now(), previousMessage)
})

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

/** F-09：被引用消息的发送者昵称（群聊展示「昵称：摘要」；取不到时留空）。 */
function replySenderNameOf(message: Message): string {
  const reply = message.reply
  if (!reply) return ''
  return memberNameById.value.get(reply.senderId) ?? ''
}

function scrollToBottom(): void {
  const element = container.value
  if (!element) return
  element.scrollTop = element.scrollHeight
}

function isPhysicalNearBottom(threshold = 80): boolean {
  const element = container.value
  if (!element) return true
  return isNearBottomState(element.scrollHeight, element.scrollTop, element.clientHeight, threshold)
}

/** 贴底判定：窗口必须包含最新消息且物理位置在底部附近。 */
function isAtRealBottom(): boolean {
  return renderedRange.value.end === totalMessages() && isPhysicalNearBottom()
}

function emitNearBottom(atBottom: boolean): void {
  emit('near-bottom-change', atBottom)
}

function updateStickState(): void {
  stickToBottom.value = isAtRealBottom()
  if (stickToBottom.value) newMessageCount.value = 0
}

/** 用户回到底部时把扩大的窗口收缩回默认大小，避免长会话累积 DOM。 */
function shrinkWindowAtBottom(): void {
  const range = currentRange()
  if (range.end - range.start <= INITIAL_WINDOW_SIZE) return
  windowRange.value = initialWindowRange(totalMessages(), INITIAL_WINDOW_SIZE)
  void nextTick(scrollToBottom)
}

/** 向上扩窗：窗口内还有更早消息时直接在内存中扩大渲染范围，并用锚点保持视口不动。 */
function expandOlder(): void {
  const element = container.value
  const range = currentRange()
  if (!element || range.start <= 0) return
  const anchor = { scrollTop: element.scrollTop, scrollHeight: element.scrollHeight }
  windowRange.value = expandOlderRange(range, WINDOW_STEP, totalMessages())
  void nextTick(() => {
    const node = container.value
    if (!node) return
    node.scrollTop = anchorScrollTop(anchor.scrollTop, anchor.scrollHeight, node.scrollHeight)
    // 用户此时在顶部：超过上限时从较新一侧收缩，即可见区域不受影响。
    const next = currentRange()
    if (next.end - next.start > MAX_RENDERED_MESSAGES) {
      windowRange.value = { start: next.start, end: next.start + MAX_RENDERED_MESSAGES }
    }
    updateStickState()
  })
}

/** 向下扩窗：翻阅到窗口底部但尚未到真实底部时，把之后的消息纳入窗口。 */
function expandNewer(): void {
  const range = currentRange()
  if (range.end >= totalMessages()) return
  windowRange.value = clampWindow(
    { start: range.start, end: range.end + WINDOW_STEP },
    totalMessages(),
  )
  void nextTick(() => {
    const node = container.value
    if (!node) return
    const next = currentRange()
    if (next.end - next.start > MAX_RENDERED_MESSAGES) {
      // 从较旧一侧收缩（在视口上方），按高度差补偿 scrollTop，保持可见内容不动。
      const prevTop = node.scrollTop
      const prevHeight = node.scrollHeight
      windowRange.value = { start: next.end - MAX_RENDERED_MESSAGES, end: next.end }
      void nextTick(() => {
        const shrunk = container.value
        if (!shrunk) return
        shrunk.scrollTop = Math.max(0, prevTop - (prevHeight - shrunk.scrollHeight))
        updateStickState()
      })
      return
    }
    updateStickState()
  })
}

/** 点击「加载更早消息」：先扩窗；窗口已到内存起点时才请求父组件拉历史。 */
function onLoadMoreClick(): void {
  const range = currentRange()
  if (range.start > 0) {
    expandOlder()
    return
  }
  const element = container.value
  if (element) {
    loadAnchor.value = { scrollTop: element.scrollTop, scrollHeight: element.scrollHeight }
  }
  emit('loadMore')
}

function onScroll(): void {
  const element = container.value
  if (!element) return
  const range = currentRange()
  const atTop = isNearTop(element.scrollTop)

  if (isAtRealBottom()) {
    stickToBottom.value = true
    newMessageCount.value = 0
    shrinkWindowAtBottom()
    emitNearBottom(true)
    return
  }

  stickToBottom.value = false

  if (atTop) {
    if (range.start > 0) {
      expandOlder()
      return
    }
    if (
      shouldAutoLoadOlder({
        atTop,
        hasMore: Boolean(props.hasMore),
        loading: Boolean(props.loading),
        rangeStart: range.start,
      })
    ) {
      loadAnchor.value = { scrollTop: element.scrollTop, scrollHeight: element.scrollHeight }
      emit('loadMore')
      return
    }
    emitNearBottom(false)
    return
  }

  if (range.end < totalMessages() && isPhysicalNearBottom()) {
    expandNewer()
    return
  }

  emitNearBottom(false)
}

/** 点击「有新消息 ↓」：回到最新消息并清零浮标。 */
function jumpToBottom(): void {
  stickToBottom.value = true
  newMessageCount.value = 0
  windowRange.value = initialWindowRange(totalMessages(), INITIAL_WINDOW_SIZE)
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

/** F-18：把气泡的操作菜单请求原样转发给会话页。 */
function forwardOpenActions(messageId: string, position: { x: number; y: number }): void {
  emit('open-actions', messageId, position)
}

/**
 * F-09：定位到指定消息（被引用消息跳转）。目标不在当前渲染窗口时先扩窗，
 * 滚动到视口中央并短暂高亮；消息不在本地缓存时返回 false，由父组件尝试补拉历史。
 */
function scrollToMessage(messageId: string): boolean {
  const index = props.messages.findIndex((message) => message.messageId === messageId)
  if (index < 0) return false

  const range = currentRange()
  if (index < range.start || index >= range.end) {
    windowRange.value = clampWindow(
      {
        start: Math.max(0, index - 20),
        end: Math.min(totalMessages(), index + INITIAL_WINDOW_SIZE),
      },
      totalMessages(),
    )
  }
  stickToBottom.value = false
  emitNearBottom(false)

  void nextTick(() => {
    const element = container.value?.querySelector<HTMLElement>(
      `[data-message-id="${CSS.escape(messageId)}"]`,
    )
    if (!element) return
    element.scrollIntoView({ block: 'center' })
    element.classList.add('message-row--highlight')
    window.setTimeout(() => element.classList.remove('message-row--highlight'), 1600)
  })
  return true
}

defineExpose({ scrollToMessage })

// 切换会话：窗口重置到末尾、重新贴底、清浮标并上报，父组件据此推进新会话的已读。
watch(
  () => props.conversationId ?? '',
  () => {
    windowRange.value = initialWindowRange(totalMessages(), INITIAL_WINDOW_SIZE)
    lastFirstMessageId.value = firstMessageId()
    loadAnchor.value = null
    stickToBottom.value = true
    newMessageCount.value = 0
    void nextTick(() => {
      scrollToBottom()
      emitNearBottom(true)
    })
  },
)

// 消息数量变化：区分「前面插入历史」与「末尾追加新消息」。
watch(
  () => props.messages.length,
  (next, previous) => {
    const delta = next - previous

    if (delta < 0) {
      windowRange.value = clampWindow(windowRange.value, next)
      lastFirstMessageId.value = firstMessageId()
      return
    }
    if (delta === 0) return

    const prepended = previous > 0 && firstMessageId() !== lastFirstMessageId.value
    lastFirstMessageId.value = firstMessageId()

    if (prepended) {
      // 窗口内保持原有内容，把新拉到的历史加在窗口上方；用锚点恢复滚动位置。
      windowRange.value = clampWindow(
        { start: windowRange.value.start, end: windowRange.value.end + delta },
        next,
      )
      const anchor = loadAnchor.value
      loadAnchor.value = null
      void nextTick(() => {
        const element = container.value
        if (!element) return
        if (anchor) {
          element.scrollTop = anchorScrollTop(
            anchor.scrollTop,
            anchor.scrollHeight,
            element.scrollHeight,
          )
        }
        updateStickState()
      })
      return
    }

    loadAnchor.value = null
    if (stickToBottom.value) {
      windowRange.value = initialWindowRange(next, INITIAL_WINDOW_SIZE)
      void nextTick(scrollToBottom)
      return
    }

    // 用户停留在窗口底部但未贴底：把新消息纳入窗口，便于继续下滑查看。
    if (currentRange().end === previous) {
      windowRange.value = clampWindow({ start: currentRange().start, end: next }, next)
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
    windowRange.value = initialWindowRange(totalMessages(), INITIAL_WINDOW_SIZE)
    void nextTick(() => {
      scrollToBottom()
      emitNearBottom(true)
    })
  },
)

onMounted(() => {
  windowRange.value = initialWindowRange(totalMessages(), INITIAL_WINDOW_SIZE)
  lastFirstMessageId.value = firstMessageId()
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
        v-if="hasMore || renderedRange.start > 0"
        class="message-list__more"
        type="button"
        :disabled="Boolean(loading) && renderedRange.start === 0"
        @click="onLoadMoreClick"
      >
        {{ loading && renderedRange.start === 0 ? '加载中…' : '加载更早消息' }}
      </button>
      <p v-else-if="messages.length > 0" class="message-list__top">没有更早的消息了</p>

      <template v-for="item in items" :key="item.key">
        <div v-if="item.kind === 'separator'" class="message-list__date">
          <span>{{ item.label }}</span>
        </div>
        <MessageBubble
          v-else
          :message="item.message"
          :data-message-id="item.message.messageId"
          :self="item.message.senderId === selfUserId"
          :show-sender="showSenderOf(item.message)"
          :sender-name="senderNameOf(item.message)"
          :reply-sender-name="replySenderNameOf(item.message)"
          :is-group="isGroup"
          :receipt="receiptOf(item.message)"
          :mention-names="mentionNames ?? []"
          :self-display-name="selfDisplayName ?? ''"
          @image-loaded="onImageLoaded"
          @preview-image="emit('preview-image', $event)"
          @open-actions="forwardOpenActions"
          @jump-to-message="emit('jump-to-message', $event)"
        />
      </template>

      <MessageBubble
        v-for="item in pending ?? []"
        :key="item.clientMsgId"
        :message="item"
        :self="true"
        :status="item.status"
        :mention-names="mentionNames ?? []"
        :self-display-name="selfDisplayName ?? ''"
        @retry="emit('retry', item.clientMsgId)"
        @image-loaded="onImageLoaded"
        @preview-image="emit('preview-image', $event)"
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
