<script setup lang="ts">
import { computed } from 'vue'
import type { Conversation } from '../types/frames'
import { unreadCount } from '../core/sync'
import Avatar from './Avatar.vue'

// 会话列表项（纯展示）：标题、最后一条消息预览、未读角标（components 不写全局状态）。
const props = defineProps<{
  conversation: Conversation
  active?: boolean
}>()

const title = computed(() => props.conversation.title || '单聊会话')

const unread = computed(() =>
  unreadCount(props.conversation.lastSeq, props.conversation.lastReadSeq),
)

const preview = computed(() => {
  const message = props.conversation.lastMessage
  if (!message) return '暂无消息'
  if (typeof message.content === 'string') return message.content
  if ('fileId' in message.content) return `[文件] ${message.content.name}`
  return message.content.text ?? '[系统消息]'
})
</script>

<template>
  <button type="button" class="conversation-item" :class="{ 'conversation-item--active': active }">
    <Avatar :display-name="title" :size="40" />
    <span class="conversation-item__body">
      <span class="conversation-item__title">{{ title }}</span>
      <span class="conversation-item__preview">{{ preview }}</span>
    </span>
    <span v-if="unread > 0" class="conversation-item__badge">{{ unread > 99 ? '99+' : unread }}</span>
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

.conversation-item__badge {
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
