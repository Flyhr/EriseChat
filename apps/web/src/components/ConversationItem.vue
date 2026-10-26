<script setup lang="ts">
import { computed } from 'vue'
import type { Conversation } from '../types/frames'
import { conversationLabel, systemMessageText } from '../core/groupChat'
import Avatar from './Avatar.vue'
import UnreadBadge from './UnreadBadge.vue'

// 会话列表项（UI-02 批次1，计划 §5.2）：行高桌面 72 / 紧凑 64 / 移动 44；头像 44；
// 选中浅蓝底 + 左侧 3px 品牌色指示条；hover 浅灰蓝；标题单行、摘要一行、时间与未读角标保留。
const props = defineProps<{
  conversation: Conversation
  /** 当前登录用户 ID：用于单聊时找出对方昵称 */
  selfUserId?: string
  active?: boolean
  /** 未读条数（0 或未传时不展示角标；会话被选中时由父组件传 0） */
  unread?: number
}>()

const emit = defineEmits<{
  select: [conversationId: string]
}>()

const otherMember = computed(() => {
  if (props.conversation.type === 'group') return undefined
  return props.conversation.members.find((member) => member.userId !== props.selfUserId)
})

const title = computed(() => {
  const conversation = props.conversation
  if (conversation.type === 'group') return conversationLabel(conversation)
  return otherMember.value?.displayName ?? conversation.title ?? '单聊会话'
})

const avatarFileId = computed(() => otherMember.value?.avatarFileId)

const preview = computed(() => {
  const message = props.conversation.lastMessage
  if (!message) return '暂无消息'

  switch (message.msgType) {
    case 'image':
      return '[图片]'
    case 'file':
      return typeof message.content === 'object' &&
        message.content !== null &&
        'name' in message.content
        ? `[文件] ${String(message.content.name)}`
        : '[文件]'
    case 'system':
      return truncate(systemMessageText(message.content))
    default:
      return typeof message.content === 'string' ? truncate(message.content) : '暂无消息'
  }
})

const timeText = computed(() => formatTime(props.conversation.lastMessage?.createdAt ?? props.conversation.createdAt))

function truncate(text: string, max = 80): string {
  const normalized = text.replace(/\\s+/g, ' ').trim()
  return normalized.length > max ? `${normalized.slice(0, max)}…` : normalized
}

function formatTime(timestamp: number): string {
  const date = new Date(timestamp)
  if (Number.isNaN(date.getTime())) return ''
  const now = new Date()
  const sameDay =
    date.getFullYear() === now.getFullYear() &&
    date.getMonth() === now.getMonth() &&
    date.getDate() === now.getDate()
  const hh = String(date.getHours()).padStart(2, '0')
  const mm = String(date.getMinutes()).padStart(2, '0')
  if (sameDay) return `${hh}:${mm}`
  const month = String(date.getMonth() + 1).padStart(2, '0')
  const day = String(date.getDate()).padStart(2, '0')
  return `${month}-${day} ${hh}:${mm}`
}
</script>

<template>
  <button
    type="button"
    class="conversation-item"
    :class="{ 'conversation-item--active': active }"
    :aria-current="active ? 'true' : undefined"
    @click="emit('select', conversation.conversationId)"
  >
    <Avatar :display-name="title" :avatar-file-id="avatarFileId" :size="44" />
    <span class="conversation-item__body">
      <span class="conversation-item__title">{{ title }}</span>
      <span class="conversation-item__preview">{{ preview }}</span>
    </span>
    <span class="conversation-item__meta">
      <span class="conversation-item__time">{{ timeText }}</span>
      <UnreadBadge :count="unread" />
    </span>
  </button>
</template>

<style scoped>
.conversation-item {
  position: relative;
  display: flex;
  gap: var(--ec-space-3);
  align-items: center;
  width: 100%;
  min-height: 72px;
  padding: var(--ec-space-2) var(--ec-space-4);
  border: none;
  background: transparent;
  text-align: left;
  cursor: pointer;
  transition: background var(--ec-transition-fast) var(--ec-ease);
}

.conversation-item:hover:not(.conversation-item--active) {
  background: var(--ec-surface-hover);
}

.conversation-item--active {
  background: var(--ec-brand-050);
}

/* 选中会话左侧 3px 品牌色指示条（计划 §5.2）。 */
.conversation-item--active::before {
  position: absolute;
  top: var(--ec-space-2);
  bottom: var(--ec-space-2);
  left: 0;
  width: 3px;
  border-radius: var(--ec-radius-full);
  background: var(--ec-brand-600);
  content: '';
}

.conversation-item__body {
  display: flex;
  flex: 1;
  flex-direction: column;
  gap: 2px;
  min-width: 0;
  overflow: hidden;
}

.conversation-item__title {
  overflow: hidden;
  color: var(--ec-text-primary);
  font-size: var(--ec-font-size-body);
  font-weight: var(--ec-font-weight-semibold);
  text-overflow: ellipsis;
  white-space: nowrap;
}

.conversation-item__preview {
  overflow: hidden;
  color: var(--ec-text-secondary);
  font-size: var(--ec-font-size-secondary);
  text-overflow: ellipsis;
  white-space: nowrap;
}

.conversation-item__meta {
  display: flex;
  flex: none;
  flex-direction: column;
  gap: var(--ec-space-1);
  align-items: flex-end;
  align-self: stretch;
  justify-content: center;
}

.conversation-item__time {
  color: var(--ec-text-secondary);
  font-size: var(--ec-font-size-small);
}

/* 801~1099：紧凑行高 64（Electron 最小 960×640 仍可用）。 */
@media (max-width: 1099px) {
  .conversation-item {
    min-height: 64px;
  }
}

/* ≤800：移动单栏列表行高 44，压缩内边距。 */
@media (max-width: 800px) {
  .conversation-item {
    min-height: 44px;
    padding: var(--ec-space-1) var(--ec-space-3);
  }
}
</style>
