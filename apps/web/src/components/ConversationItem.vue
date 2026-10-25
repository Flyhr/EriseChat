<script setup lang="ts">
import { computed } from 'vue'
import type { Conversation } from '../types/frames'
import Avatar from './Avatar.vue'

// 会话列表项（纯展示，M2-04 接线）：标题（单聊显示对方昵称/群聊标题）、最后一条消息摘要、
// 时间、未读角标（M2-05 起本地已读游标计算；M3 接入服务端 read.* 后改用服务端未读）。
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
  if (props.conversation.type === 'group') return props.conversation.title || '群聊'
  return otherMember.value?.displayName ?? props.conversation.title ?? '单聊会话'
})

const avatarFileId = computed(() => otherMember.value?.avatarFileId)

const preview = computed(() => {
  const message = props.conversation.lastMessage
  if (!message) return '暂无消息'
  if (typeof message.content === 'string') return truncate(message.content)
  if ('fileId' in message.content) return `[文件] ${message.content.name}`
  return message.content.text ? truncate(message.content.text) : '[系统消息]'
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
      <span v-if="unread && unread > 0" class="conversation-item__unread">{{ unread > 99 ? '99+' : unread }}</span>
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

.conversation-item__unread {
  min-width: 18px;
  padding: 0 5px;
  border-radius: var(--radius-full);
  background: var(--color-danger);
  color: var(--color-text-inverse);
  font-size: 11px;
  line-height: 18px;
  text-align: center;
}
</style>
