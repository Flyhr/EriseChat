<script setup lang="ts">
import { computed } from 'vue'
import { conversationTitle } from '../core/conversationTitle'
import { highlightSegments, snippetOf } from '../core/searchHighlight'
import { useAuthStore } from '../stores/auth'
import { useConversationsStore } from '../stores/conversations'
import { useFriendsStore } from '../stores/friends'
import type { Message } from '../types/frames'

// 搜索结果行（F-11 体验调整）：搜索面板（SearchDialog）与会话列表内联搜索共用同一展示，
// 保证两处样式与信息一致。只展示 text 消息的摘要 + 命中高亮，不做任何请求。
const props = defineProps<{
  message: Message
  query: string
  active?: boolean
}>()

const emit = defineEmits<{
  select: [message: Message]
}>()

const auth = useAuthStore()
const conversations = useConversationsStore()
const friends = useFriendsStore()

/** 会话名：群聊用「群名（人数）」，单聊取对方昵称，兜底会话 ID 前 8 位。 */
const conversationName = computed(() =>
  conversationTitle(conversations.getById(props.message.conversationId), auth.user?.userId, {
    fallback: props.message.conversationId.slice(0, 8),
  }),
)

/** 发送人：优先会话成员昵称，其次好友列表，最后兜底文案。 */
const senderName = computed(() => {
  const conversation = conversations.getById(props.message.conversationId)
  const member = conversation?.members.find((item) => item.userId === props.message.senderId)
  if (member?.displayName) return member.displayName
  return friends.displayNameOf(props.message.senderId) || '未知用户'
})

const timeText = computed(() => formatTime(props.message.createdAt))

/** 摘要：文本取正文、图片/文件取文件名；以命中为中心截取，再按关键词切高亮片段。 */
const segments = computed(() => {
  const content = props.message.content
  const text =
    typeof content === 'string'
      ? content
      : content && typeof content === 'object' && 'name' in content && typeof content.name === 'string'
        ? content.name
        : ''
  const snippet = snippetOf(text, props.query)
  return highlightSegments(snippet, props.query)
})

function formatTime(timestamp: number): string {
  const date = new Date(timestamp)
  if (Number.isNaN(date.getTime())) return ''
  const now = new Date()
  const pad = (value: number): string => String(value).padStart(2, '0')
  const startOfToday = new Date(now.getFullYear(), now.getMonth(), now.getDate()).getTime()
  if (timestamp >= startOfToday) return `${pad(date.getHours())}:${pad(date.getMinutes())}`
  if (timestamp >= startOfToday - 24 * 60 * 60 * 1000) return '昨天'
  if (date.getFullYear() === now.getFullYear()) {
    return `${pad(date.getMonth() + 1)}-${pad(date.getDate())}`
  }
  return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}`
}

function onSelect(): void {
  emit('select', props.message)
}
</script>

<template>
  <button
    type="button"
    class="ec-search-result"
    :class="{ 'ec-search-result--active': active }"
    role="option"
    :aria-selected="active"
    @click="onSelect"
  >
    <span class="ec-search-result__top">
      <span class="ec-search-result__conversation">{{ conversationName }}</span>
      <span class="ec-search-result__time">{{ timeText }}</span>
    </span>
    <span class="ec-search-result__sender">{{ senderName }}</span>
    <span class="ec-search-result__snippet">
      <template v-for="(segment, index) in segments" :key="index">
        <mark v-if="segment.hit" class="ec-search-result__hit">{{ segment.text }}</mark>
        <template v-else>{{ segment.text }}</template>
      </template>
    </span>
  </button>
</template>

<style scoped>
.ec-search-result {
  display: flex;
  flex-direction: column;
  gap: 2px;
  width: 100%;
  padding: var(--ec-space-2) var(--ec-space-3);
  border: none;
  border-radius: var(--ec-radius-sm);
  background: transparent;
  color: var(--ec-text-primary);
  text-align: left;
  cursor: pointer;
}

.ec-search-result--active,
.ec-search-result:hover {
  background: var(--ec-brand-050);
}

.ec-search-result:focus-visible {
  outline: var(--ec-focus-width) solid var(--ec-focus-ring);
  outline-offset: calc(var(--ec-focus-offset) * -1);
}

.ec-search-result__top {
  display: flex;
  gap: var(--ec-space-2);
  align-items: baseline;
  justify-content: space-between;
}

.ec-search-result__conversation {
  overflow: hidden;
  font-size: var(--ec-font-size-body);
  font-weight: var(--ec-font-weight-medium);
  text-overflow: ellipsis;
  white-space: nowrap;
}

.ec-search-result__time {
  flex: none;
  color: var(--ec-text-tertiary);
  font-size: var(--ec-font-size-small);
}

.ec-search-result__sender {
  color: var(--ec-text-secondary);
  font-size: var(--ec-font-size-small);
}

.ec-search-result__snippet {
  display: -webkit-box;
  overflow: hidden;
  color: var(--ec-text-secondary);
  font-size: var(--ec-font-size-secondary);
  word-break: break-word;
  -webkit-box-orient: vertical;
  -webkit-line-clamp: 2;
}

.ec-search-result__hit {
  border-radius: 2px;
  background: var(--ec-warning-soft);
  color: var(--ec-text-primary);
  text-decoration: underline;
  text-underline-offset: 2px;
}

@media (max-width: 800px) {
  .ec-search-result {
    min-height: 44px;
  }
}

@media (prefers-reduced-motion: reduce) {
  .ec-search-result {
    transition: none;
  }
}
</style>
