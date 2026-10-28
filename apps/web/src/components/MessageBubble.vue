<script setup lang="ts">
import { computed, onBeforeUnmount } from 'vue'
import type { Message } from '../types/frames'
import type { PendingMessage, PendingStatus } from '../stores/messages'
import { systemMessageText } from '../core/groupChat'
import { hasSelfMention, splitRichText } from '../core/mentions'
import { replyCardText } from '../core/replyPreview'
import Avatar from './Avatar.vue'
import MessageAttachment from './MessageAttachment.vue'

// 消息气泡（纯展示，UI-02 批次2 按 D-05 调整）：自己蓝底白字、对方白底描边；时间与状态移到气泡下方；
// 群聊他人消息左侧 32px 字母头像 + 昵称。文本只用插值渲染（自动转义），禁止 v-html（protocol.md §4.3）。
// pending 消息显示「发送中/失败重发」；system 消息居中灰字，不带气泡。
// F-09/F-18：桌面右键 / 移动端长按打开消息操作菜单（回复/撤回/编辑）；气泡内展示引用摘要，点击引用条定位。
// F-18：已撤回消息只显示灰色占位文案，不渲染内容/附件；编辑过的消息在时间旁标「已编辑」。
const props = withDefaults(
  defineProps<{
    message: Message | PendingMessage
    self?: boolean
    status?: PendingStatus
    /** 单聊已读回执（M3-09）：仅自己发出的最新一条展示；群聊不展示。 */
    receipt?: 'read' | 'unread'
    /** 群聊中展示发送者头像与昵称（非自己消息）。 */
    showSender?: boolean
    senderName?: string
    /** 是否群聊（F-18：撤回占位文案区分「对方」/「{昵称}」）。 */
    isGroup?: boolean
    /** F-09：被引用消息发送者昵称（群聊展示「昵称：摘要」；单聊可留空）。 */
    replySenderName?: string
    /** F-25：会话成员昵称（用于把文本中的「@昵称」识别为提及并高亮）。 */
    mentionNames?: readonly string[]
    /** F-25：当前用户昵称（用于「提到你」标记；空串不标记）。 */
    selfDisplayName?: string
  }>(),
  {
    self: false,
    showSender: false,
    senderName: '',
    replySenderName: '',
    isGroup: false,
    mentionNames: () => [],
    selfDisplayName: '',
  },
)

const emit = defineEmits<{
  retry: []
  /** 图片加载完成（父级在用户停留底部时重新滚到底）。 */
  imageLoaded: []
  /** F-08：请求打开图片预览（由会话页统一管理多图切换与 blob 加载）。 */
  'preview-image': [messageId: string]
  /** F-18：请求打开消息操作菜单（桌面右键 / 移动端长按 500ms），position 为视口坐标。 */
  'open-actions': [messageId: string, position: { x: number; y: number }]
  /** F-09：点击引用条，请求定位到被引用的消息。 */
  'jump-to-message': [messageId: string]
}>()

const contentText = computed(() => {
  const content = props.message.content
  if (typeof content === 'string') return content
  if ('fileId' in content) return content.name
  return content.text ?? '[系统消息]'
})

/**
 * 文本消息富文本分段（F-07 链接 + F-25 提及；仅字符串内容参与，附件名与 system 消息不参与）。
 * 链接与提及均以文本节点渲染，禁止 v-html（protocol.md §4.3）。
 */
const richSegments = computed(() =>
  typeof props.message.content === 'string'
    ? splitRichText(props.message.content, props.mentionNames, props.selfDisplayName)
    : [],
)

/** F-25：当前消息是否提到了自己（仅文本消息，用于气泡角标「提到你」）。 */
const selfMentioned = computed(
  () =>
    props.selfDisplayName !== '' &&
    typeof props.message.content === 'string' &&
    hasSelfMention(props.message.content, props.selfDisplayName),
)

/** system 消息：居中灰字、无气泡/头像，不参与附件/时间/状态渲染。 */
const isSystem = computed(() => props.message.msgType === 'system')

const systemText = computed(() => systemMessageText(props.message.content))

/** F-18：已撤回（deletedAt 非空）只渲染占位文案。 */
const recalled = computed(
  () => 'deletedAt' in props.message && typeof props.message.deletedAt === 'number',
)

const recalledText = computed(() => {
  if (!recalled.value) return ''
  if (props.self) return '你撤回了一条消息'
  if (props.showSender && props.senderName) return `${props.senderName} 撤回了一条消息`
  return '对方撤回了一条消息'
})

/** F-18：编辑过的消息在时间旁标「已编辑」。 */
const edited = computed(
  () => 'editedAt' in props.message && typeof props.message.editedAt === 'number',
)

/** 仅 image/file 且 content 为 FileContent 时用附件组件渲染；异常数据回退文本。 */
const showAttachment = computed(() => {
  if (props.message.msgType !== 'image' && props.message.msgType !== 'file') return false
  const content = props.message.content
  return typeof content === 'object' && content !== null && 'fileId' in content
})

/** pending 消息没有 messageId，用 clientMsgId 兜底，作为预览条目的稳定键。 */
function messageKeyOf(message: Message | PendingMessage): string {
  return 'messageId' in message ? message.messageId : message.clientMsgId
}

/** F-08 批次3：预览状态提升到 Chat.vue，气泡只上报「需要预览哪条消息」；blob URL 由会话页加载/缓存。 */
function onPreview(): void {
  // F-18：已撤回的图片不再提供预览。
  if (recalled.value) return
  emit('preview-image', messageKeyOf(props.message))
}

/** F-09：引用摘要（已落库消息取服务端 reply；pending 用本地 replyPreview，不可跳转）。 */
const replyInfo = computed<{ messageId: string; text: string } | null>(() => {
  if ('messageId' in props.message) {
    const reply = props.message.reply
    if (!reply) return null
    return {
      messageId: reply.messageId,
      text: replyCardText(reply, props.replySenderName || undefined),
    }
  }
  const preview = props.message.replyPreview
  return preview ? { messageId: '', text: replyCardText(preview) } : null
})

/** 可回复的消息 ID：pending 尚未落库（无 messageId），不提供回复入口。 */
function repliableMessageId(): string {
  return 'messageId' in props.message ? props.message.messageId : ''
}

const LONG_PRESS_MS = 500
let longPressTimer: number | null = null

function clearLongPress(): void {
  if (longPressTimer !== null) {
    window.clearTimeout(longPressTimer)
    longPressTimer = null
  }
}

/** F-18：打开操作菜单（回复/撤回/编辑由父级按窗口与身份过滤）。 */
function openActions(position: { x: number; y: number }): void {
  const messageId = repliableMessageId()
  if (!messageId || recalled.value) return
  emit('open-actions', messageId, position)
}

/** 桌面：右键气泡打开操作菜单（system 消息不提供）。 */
function onContextMenu(event: MouseEvent): void {
  if (isSystem.value || !repliableMessageId()) return
  event.preventDefault()
  openActions({ x: event.clientX, y: event.clientY })
}

/** 移动端：长按 500ms 打开操作菜单；滚动/抬起/取消时清除计时器。 */
function onPointerDown(event: PointerEvent): void {
  if (event.button !== 0 || isSystem.value || !repliableMessageId()) return
  clearLongPress()
  const target = event.currentTarget instanceof HTMLElement ? event.currentTarget : null
  longPressTimer = window.setTimeout(() => {
    longPressTimer = null
    const rect = target?.getBoundingClientRect()
    openActions(rect ? { x: rect.left + rect.width / 2, y: rect.top } : { x: 0, y: 0 })
  }, LONG_PRESS_MS)
}

function onPointerEnd(): void {
  clearLongPress()
}

onBeforeUnmount(clearLongPress)

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
    class="message-row"
    :class="{ 'message-row--self': self, 'message-row--sender': showSender }"
  >
    <Avatar v-if="showSender" class="message-row__avatar" :display-name="senderName || '?'" :size="32" />
    <div class="message-row__body">
      <span v-if="showSender && senderName" class="message-row__sender">{{ senderName }}</span>
      <div
        class="message-bubble"
        :class="{
          'message-bubble--self': self,
          'message-bubble--sending': status === 'sending',
          'message-bubble--failed': status === 'failed',
          'message-bubble--recalled': recalled,
        }"
        @contextmenu="onContextMenu"
        @pointerdown="onPointerDown"
        @pointerup="onPointerEnd"
        @pointerleave="onPointerEnd"
        @pointercancel="onPointerEnd"
      >
        <div v-if="replyInfo && !recalled" class="message-bubble__reply">
          <button
            v-if="replyInfo.messageId"
            class="message-bubble__reply-button"
            type="button"
            @click="emit('jump-to-message', replyInfo.messageId)"
          >
            {{ replyInfo.text }}
          </button>
          <span v-else class="message-bubble__reply-text">{{ replyInfo.text }}</span>
        </div>
        <p v-if="recalled" class="message-bubble__recalled">{{ recalledText }}</p>
        <MessageAttachment
          v-else-if="showAttachment"
          :message="message"
          @preview="onPreview"
          @loaded="emit('imageLoaded')"
        />
        <p v-else class="message-bubble__text">
          <template v-if="richSegments.length > 0">
            <template v-for="(segment, index) in richSegments" :key="index">
              <a
                v-if="segment.kind === 'link'"
                class="message-bubble__link"
                :href="segment.href"
                target="_blank"
                rel="noopener noreferrer"
                >{{ segment.value }}</a
              >
              <span
                v-else-if="segment.kind === 'mention'"
                class="message-bubble__mention"
                :class="{ 'message-bubble__mention--self': segment.self }"
                >{{ segment.value }}</span
              >
              <template v-else>{{ segment.value }}</template>
            </template>
          </template>
          <template v-else>{{ contentText }}</template>
        </p>
      </div>
      <div class="message-row__meta">
        <span v-if="selfMentioned" class="message-row__mention-badge">提到你</span>
        <span v-if="edited" class="message-row__edited">已编辑</span>
        <time class="message-row__time">{{ timeText }}</time>
        <span
          v-if="receipt"
          class="message-row__receipt"
          :class="`message-row__receipt--${receipt}`"
        >
          {{ receipt === 'read' ? '已读' : '未读' }}
        </span>
        <span v-if="status === 'sending'" class="message-row__pending">发送中…</span>
        <button
          v-else-if="status === 'failed'"
          class="message-row__retry"
          type="button"
          @click="emit('retry')"
        >
          发送失败，点击重发
        </button>
      </div>
    </div>
  </div>
</template>

<style scoped>
.message-system {
  display: flex;
  justify-content: center;
  padding: 0 var(--ec-space-2);
}

.message-system__text {
  color: var(--ec-text-secondary);
  font-size: var(--ec-font-size-secondary);
  text-align: center;
  word-break: break-word;
}

.message-row {
  display: flex;
  gap: var(--ec-space-2);
  align-items: flex-start;
}

/* 自己消息靠右：行方向反转，meta 右对齐。 */
.message-row--self {
  flex-direction: row-reverse;
}

.message-row__avatar {
  margin-top: 2px;
}

.message-row__body {
  display: flex;
  flex-direction: column;
  gap: 4px;
  max-width: min(520px, 78%);
  min-width: 0;
}

.message-row--self .message-row__body {
  align-items: flex-end;
}

.message-row__sender {
  color: var(--ec-text-secondary);
  font-size: var(--ec-font-size-small);
}

/* 「澄蓝」主题：气泡 18px 大圆角 + 单侧 6px 尾角；自己为蓝色渐变白字，对方为白底冷蓝描边。 */
.message-bubble {
  max-width: 100%;
  padding: 10px 14px;
  border: 1px solid var(--ec-bubble-other-border);
  border-radius: var(--ec-radius-bubble) var(--ec-radius-bubble) var(--ec-radius-bubble)
    var(--ec-radius-bubble-tail);
  background: var(--ec-bubble-other);
  color: var(--ec-text-primary);
  box-shadow: var(--ec-shadow-bubble);
}

.message-bubble--self {
  border-color: var(--ec-bubble-self-border);
  border-radius: var(--ec-radius-bubble) var(--ec-radius-bubble) var(--ec-radius-bubble-tail)
    var(--ec-radius-bubble);
  background: var(--ec-bubble-self-bg);
  color: var(--ec-text-inverse);
  box-shadow: var(--ec-shadow-bubble-self);
}

.message-bubble--sending {
  opacity: 0.75;
}

.message-bubble--failed {
  border-color: var(--ec-danger);
}

.message-bubble__text {
  margin: 0;
  white-space: pre-wrap;
  overflow-wrap: anywhere;
  word-break: break-word;
}

/* 链接：对方气泡用品牌色，自己蓝底白字气泡继承文字色；下划线补偿 offset 保证可辨识。 */
.message-bubble__link {
  color: var(--ec-brand-600);
  text-decoration: underline;
  text-underline-offset: 2px;
}

.message-bubble--self .message-bubble__link {
  color: inherit;
}

/* F-25 提及：普通 @ 用品牌色轻底；提到自己时用品牌主色加重高亮。 */
.message-bubble__mention {
  padding: 0 2px;
  border-radius: 4px;
  color: var(--ec-brand-600);
  background: var(--ec-brand-050);
}

.message-bubble__mention--self {
  color: var(--ec-text-inverse);
  background: var(--ec-brand-600);
  font-weight: 600;
}

.message-bubble--self .message-bubble__mention {
  color: inherit;
  background: rgba(255, 255, 255, 0.22);
}

.message-bubble--self .message-bubble__mention--self {
  color: var(--ec-brand-600);
  background: var(--ec-surface);
}

/* 「提到你」角标：与「已编辑」同处 meta 行，仅文本消息且确实提到自己时出现。 */
.message-row__mention-badge {
  padding: 0 6px;
  border-radius: 999px;
  font-size: 11px;
  line-height: 16px;
  color: var(--ec-brand-600);
  background: var(--ec-brand-050);
}

.message-row__meta {
  display: flex;
  gap: var(--ec-space-2);
  align-items: center;
  justify-content: flex-end;
  color: var(--ec-text-secondary);
  font-size: var(--ec-font-size-small);
}

.message-row__receipt--read {
  color: var(--ec-brand-600);
}

.message-row__retry {
  padding: 0;
  border: none;
  background: none;
  color: var(--ec-danger);
  font-size: var(--ec-font-size-small);
  cursor: pointer;
}

/* F-09：引用条（气泡内上方），点击跳转被引用消息；pending 本地预览不可点击。 */
.message-bubble__reply {
  margin-bottom: 6px;
  padding: 5px 10px;
  border-left: 3px solid var(--ec-brand-500);
  border-radius: var(--ec-radius-sm);
  background: var(--ec-surface-hover);
  color: var(--ec-text-secondary);
  font-size: var(--ec-font-size-secondary);
  text-align: left;
}

.message-bubble--self .message-bubble__reply {
  border-left-color: var(--ec-text-inverse);
  background: rgba(255, 255, 255, 0.18);
  color: inherit;
}

.message-bubble__reply-button,
.message-bubble__reply-text {
  display: block;
  width: 100%;
  overflow: hidden;
  padding: 0;
  border: none;
  background: none;
  color: inherit;
  font: inherit;
  text-align: left;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.message-bubble__reply-button {
  cursor: pointer;
}

.message-bubble__reply-button:focus-visible {
  border-radius: 4px;
  outline: var(--ec-focus-width) solid var(--ec-focus-ring);
  outline-offset: 2px;
}

/* F-09：点击引用条定位后短暂高亮被引用的消息气泡。 */
.message-row--highlight .message-bubble {
  outline: 2px solid var(--ec-brand-500);
  outline-offset: 2px;
}

/* F-18：已撤回消息占位（灰色虚线，不渲染原文/附件）。 */
.message-bubble--recalled {
  border-style: dashed;
  border-color: var(--ec-border);
  background: var(--ec-surface-hover);
  color: var(--ec-text-secondary);
  box-shadow: none;
}

.message-bubble--self.message-bubble--recalled {
  background: var(--ec-surface-hover);
  color: var(--ec-text-secondary);
}

.message-bubble__recalled {
  margin: 0;
  font-style: italic;
}

.message-row__edited {
  color: var(--ec-text-tertiary);
}

/* ≤800：窄屏下气泡最大宽放宽，圆角同步收一档。 */
@media (max-width: 800px) {
  .message-row__body {
    max-width: 84%;
  }

  .message-bubble {
    border-radius: 16px 16px 16px 5px;
  }

  .message-bubble--self {
    border-radius: 16px 16px 5px 16px;
  }
}
</style>
