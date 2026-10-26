<script setup lang="ts">
import { computed } from 'vue'
import type { Conversation } from '../types/frames'
import { conversationLabel, systemMessageText } from '../core/groupChat'
import Avatar from './Avatar.vue'
import UnreadBadge from './UnreadBadge.vue'

// 会话列表项（纯展示）：标题（单聊显示对方昵称/群聊显示群名与成员数）、最后一条消息摘要、
// 时间、未读角标。群聊消息摘要区分 system/image/file（M3-08）。
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
  const normalized = text.replace(/\s+/g, ' ').trim()
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
    @click="emit('select', conversation.conversationId)"
  >
    <Avatar :display-name="title" :avatar-file-id="avatarFileId" :size="40" />
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
  display: flex;
  gap: var(--space-3);
  align-items: center;
  width: 100%;
  padding: var(--space-3) var(--space-4);
  border: none;
  background: transparent;
  text-align: left;
  cursor: pointer;
}

.conversation-item:hover,
.conversation-item--active {
  background: var(--color-surface-hover);
}

.conversation-item__body {
  display: flex;
  flex-direction: column;
  flex: 1;
  gap: 2px;
  overflow: hidden;
}

.conversation-item__title {
  font-weight: 600;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.conversation-item__preview {
  overflow: hidden;
  color: var(--color-text-secondary);
  font-size: 12px;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.conversation-item__meta {
  display: flex;
  flex-direction: column;
  align-items: flex-end;
  gap: 4px;
  align-self: flex-start;
  flex: none;
}

.conversation-item__time {
  color: var(--color-text-secondary);
  font-size: 11px;
}
</style>
