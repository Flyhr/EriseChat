<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue'
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
import { chatWs } from '../core/ws'
import MessageBubble from './MessageBubble.vue'
import LoadingDots from './LoadingDots.vue'

// UI-D P2-B：断线状态只订阅一次，向下传给 pending 消息用于「消息待发送」提示。
const offline = ref(chatWs.state !== 'authed')
let unsubscribeWs: (() => void) | null = null
onMounted(() => {
  unsubscribeWs = chatWs.on('statechange', (state) => {
    offline.value = state !== 'authed'
  })
})
onBeforeUnmount(() => {
  unsubscribeWs?.()
  resizeObserver?.disconnect()
  resizeObserver = null
})

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
  /** F-37：定位窗口模式（独立于正常时间线，不提供加载更早入口）。 */
  locateMode?: boolean
  /** F-37：定位窗口上方是否还有更早消息（仅提示）。 */
  locateHasOlder?: boolean
  /** F-37：定位窗口下方是否还有更新消息。 */
  locateHasNewer?: boolean
  /** F-40：多选模式（消息行可点选，气泡内交互暂时禁用）。 */
  selecting?: boolean
  /** F-40：已选中的服务端消息 ID（回显勾选态）。 */
  selectedIds?: readonly string[]
  /**
   * 批次 B2：进入会话时的落点（首条可见消息 ID + 相对容器顶部的偏移）。
   * 为空表示贴底；目标不在已加载消息里时退回贴底，由页面决定是否另给入口。
   */
  initialAnchor?: { messageId: string; offset: number } | null
  /** 批次 B2：首条未读消息 ID，用于在时间线上插入「以下是未读消息」分隔。 */
  unreadStartId?: string | null
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
  /** F-40：多选模式下切换消息选中状态。 */
  'toggle-select': [messageId: string]
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
/** 批次 B2：最近一次滚动后的可见锚点，图片解码撑高列表时用它把视口拉回原位。 */
let lastVisibleAnchor: { messageId: string; offset: number } | null = null
/** 批次 B3：消息区尺寸变化（输入框增高 / 软键盘弹出）时保持贴底或阅读锚点。 */
let resizeObserver: ResizeObserver | null = null
let resizeObserverReady = false

function totalMessages(): number {
  return props.messages.length
}

/** 首条可见消息及其相对容器顶部的偏移（前插历史、图片解码、会话切换共用同一口径）。 */
function getVisibleAnchor(): { messageId: string; offset: number } | null {
  const element = container.value
  if (!element) return null
  const containerTop = element.getBoundingClientRect().top
  const rows = element.querySelectorAll<HTMLElement>('[data-message-id]')
  for (const row of rows) {
    const rect = row.getBoundingClientRect()
    if (rect.bottom <= containerTop + 1) continue
    const messageId = row.dataset.messageId
    if (!messageId) continue
    return { messageId, offset: Math.round(rect.top - containerTop) }
  }
  return null
}

/**
 * 按锚点恢复滚动位置：把该消息行顶部对齐到记录的偏移。
 * 会话已切换或该消息不在渲染窗口时返回 false，由调用方回退到高度差补偿或贴底。
 */
function restoreAnchor(anchor: { messageId: string; offset: number }, conversationId: string): boolean {
  const element = container.value
  if (!element) return false
  if ((props.conversationId ?? '') !== conversationId) return false
  const row = element.querySelector<HTMLElement>(`[data-message-id="${CSS.escape(anchor.messageId)}"]`)
  if (!row) return false
  const delta = row.getBoundingClientRect().top - element.getBoundingClientRect().top
  element.scrollTop += delta - anchor.offset
  // 程序化定位后同步记录锚点：后续图片解码仍按同一位置恢复。
  lastVisibleAnchor = anchor
  return true
}

/** 把目标消息纳入渲染窗口并定位到指定偏移（不在已加载消息里时返回 false）。 */
function scrollToAnchor(anchor: { messageId: string; offset: number }): boolean {
  const index = props.messages.findIndex((message) => message.messageId === anchor.messageId)
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
  newMessageCount.value = 0
  const conversationId = props.conversationId ?? ''
  void nextTick(() => {
    if (!restoreAnchor(anchor, conversationId)) {
      scrollToBottom()
      emitNearBottom(true)
      return
    }
    emitNearBottom(false)
  })
  return true
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

/** 发送者头像：直接取会话成员上的 avatarFileId（与群成员面板/会话列表同源）。 */
const memberAvatarById = computed(() => {
  const map = new Map<string, string>()
  for (const member of props.members ?? []) {
    if (member.avatarFileId) map.set(member.userId, member.avatarFileId)
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
  // 记录可见锚点：图片解码、窗口收缩等非用户滚动引起的高度变化都按它恢复。
  lastVisibleAnchor = getVisibleAnchor()
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

/** 图片缩略图加载完成后：贴底时跟随滚到底，浏览历史时按可见锚点把视口拉回原位。 */
function onImageLoaded(): void {
  if (stickToBottom.value) {
    void nextTick(scrollToBottom)
    return
  }
  const anchor = lastVisibleAnchor
  if (!anchor) return
  const conversationId = props.conversationId ?? ''
  void nextTick(() => restoreAnchor(anchor, conversationId))
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

function getScrollTop(): number {
  return container.value?.scrollTop ?? 0
}

function setScrollTop(value: number): void {
  if (container.value) container.value.scrollTop = Math.max(0, value)
}

defineExpose({ scrollToMessage, scrollToBottom, getScrollTop, setScrollTop, getVisibleAnchor })

// 切换会话：按页面给的落点恢复位置（指定消息 / 上次浏览 / 首条未读），没有落点才贴底。
// 窗口重置到末尾；落点目标不在已加载消息里时退回贴底，并由页面决定是否给「查看未读起点」入口。
watch(
  () => props.conversationId ?? '',
  () => {
    windowRange.value = initialWindowRange(totalMessages(), INITIAL_WINDOW_SIZE)
    lastFirstMessageId.value = firstMessageId()
    loadAnchor.value = null
    lastVisibleAnchor = null
    newMessageCount.value = 0
    const anchor = props.initialAnchor
    if (anchor && scrollToAnchor(anchor)) return
    stickToBottom.value = true
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
    if (props.locateMode) {
      windowRange.value = initialWindowRange(next, INITIAL_WINDOW_SIZE)
      lastFirstMessageId.value = firstMessageId()
      newMessageCount.value = 0
      return
    }

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
      // 渲染前读取可见锚点：历史插到上方后按「消息 ID + 偏移」还原，比纯高度差更稳；
      // 目标行缺失时（例如同时发生窗口收缩）退回原有高度差补偿。
      const visibleAnchor = getVisibleAnchor()
      const conversationId = props.conversationId ?? ''
      windowRange.value = clampWindow(
        { start: windowRange.value.start, end: windowRange.value.end + delta },
        next,
      )
      const anchor = loadAnchor.value
      loadAnchor.value = null
      void nextTick(() => {
        const element = container.value
        if (!element) return
        const restored = visibleAnchor ? restoreAnchor(visibleAnchor, conversationId) : false
        if (!restored && anchor) {
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
  // 批次 B3：输入框增高、软键盘弹出都会改变消息区高度——贴底时保持贴底，浏览历史时按锚点保持位置。
  if (typeof ResizeObserver !== 'undefined' && container.value) {
    resizeObserver = new ResizeObserver(() => {
      if (!resizeObserverReady) return
      if (stickToBottom.value) {
        scrollToBottom()
        return
      }
      if (lastVisibleAnchor) restoreAnchor(lastVisibleAnchor, props.conversationId ?? '')
    })
    resizeObserver.observe(container.value)
    window.requestAnimationFrame(() => {
      resizeObserverReady = true
    })
  }
  const anchor = props.initialAnchor
  if (anchor && scrollToAnchor(anchor)) return
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
        v-if="!locateMode && (hasMore || renderedRange.start > 0)"
        class="message-list__more"
        type="button"
        :disabled="Boolean(loading) && renderedRange.start === 0"
        @click="onLoadMoreClick"
      >
        <LoadingDots v-if="loading && renderedRange.start === 0" />
        <template v-else>加载更早消息</template>
      </button>
      <p v-else-if="!locateMode && messages.length > 0" class="message-list__top">没有更早的消息了</p>
      <p v-if="locateMode" class="message-list__gap">
        {{ locateHasOlder ? '上方还有更早的未加载消息，查看更早请点「回到最新」后上滑' : '已到这段历史的最早一条' }}
      </p>

      <template v-for="item in items" :key="item.key">
        <div v-if="item.kind === 'separator'" class="message-list__date ec-enter">
          <span>{{ item.label }}</span>
        </div>
        <template v-else>
          <!-- 批次 B2：首条未读分隔（只在进入会话的落点处出现，读到最新后由页面清除）。 -->
          <div
            v-if="unreadStartId && item.message.messageId === unreadStartId"
            class="message-list__unread"
            role="separator"
            aria-label="未读消息起点"
          >
            <span>以下是未读消息</span>
          </div>
          <MessageBubble
            :message="item.message"
            :data-message-id="item.message.messageId"
            :self="item.message.senderId === selfUserId"
            :show-sender="showSenderOf(item.message)"
            :sender-name="senderNameOf(item.message)"
            :sender-avatar-file-id="memberAvatarById.get(item.message.senderId)"
            :reply-sender-name="replySenderNameOf(item.message)"
            :is-group="isGroup"
            :receipt="receiptOf(item.message)"
            :mention-names="mentionNames ?? []"
            :self-display-name="selfDisplayName ?? ''"
            :selectable="selecting === true"
            :selected="selectedIds?.includes(item.message.messageId) ?? false"
            @image-loaded="onImageLoaded"
            @preview-image="emit('preview-image', $event)"
            @open-actions="forwardOpenActions"
            @jump-to-message="emit('jump-to-message', $event)"
            @toggle-select="emit('toggle-select', $event)"
          />
        </template>
      </template>

      <MessageBubble
        v-for="item in pending ?? []"
        :key="item.clientMsgId"
        class="ec-enter"
        :message="item"
        :self="true"
        :status="item.status"
        :offline="offline"
        :mention-names="mentionNames ?? []"
        :self-display-name="selfDisplayName ?? ''"
        @retry="emit('retry', item.clientMsgId)"
        @image-loaded="onImageLoaded"
        @preview-image="emit('preview-image', $event)"
      />
    </div>

    <p v-if="locateMode && locateHasNewer" class="message-list__gap">
      下方还有更新的消息，点「回到最新」查看
    </p>

    <button
      v-if="!locateMode && shouldShowNewMessageBadge(stickToBottom, newMessageCount)"
      type="button"
      class="message-list__badge"
      @click="jumpToBottom"
    >
      有新消息（{{ newMessageCount }}）↓
    </button>
  </div>
</template>

<style scoped>
/* UI-D P2-B：同一侧连续消息收紧间距形成分组；日期分隔与系统消息会打断相邻关系。 */
.message-list :deep(.message-row--self + .message-row--self),
.message-list :deep(.message-row:not(.message-row--self) + .message-row:not(.message-row--self)) {
  margin-top: -8px;
}

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
  /* 渐变由 .chat-page__chat 统一提供，这里保持透明，避免顶部出现背景断层。 */
  background: transparent;
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
  background: var(--ec-date-badge-bg);
  color: var(--ec-text-secondary);
  font-size: var(--ec-font-size-small);
}

/* 批次 B2：首条未读分隔——品牌色细线与胶囊文字，只在落点处出现一次。 */
.message-list__unread {
  display: flex;
  gap: var(--ec-space-2);
  align-items: center;
  align-self: stretch;
  margin: var(--ec-space-1) 0;
  color: var(--ec-brand-700);
  font-size: var(--ec-font-size-small);
}

.message-list__unread::before,
.message-list__unread::after {
  flex: 1;
  height: 1px;
  background: var(--ec-brand-100);
  content: '';
}

.message-list__unread span {
  flex: none;
  padding: 2px var(--ec-space-2);
  border-radius: var(--ec-radius-full);
  background: var(--ec-brand-050);
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
  box-shadow: var(--ec-shadow-badge);
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
.message-list__gap {
  margin: var(--ec-space-2) var(--ec-space-4);
  padding: var(--ec-space-2) var(--ec-space-3);
  border: 1px dashed var(--ec-border);
  border-radius: var(--ec-radius-panel);
  color: var(--ec-text-tertiary);
  font-size: 12px;
  line-height: 1.5;
  text-align: center;
}
</style>
