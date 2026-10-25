<script setup lang="ts">
import { computed, onMounted, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import ConversationItem from '../components/ConversationItem.vue'
import EmptyState from '../components/EmptyState.vue'
import MessageList from '../components/MessageList.vue'
import SendBox from '../components/SendBox.vue'
import { useAuthStore } from '../stores/auth'
import { useConversationsStore } from '../stores/conversations'
import { useMessagesStore } from '../stores/messages'

// 聊天主页面（M2-04 会话列表 + M2-05 消息流）：左侧会话列表，右侧消息流与发送框。
// 规则：页面只做编排，状态走 stores/，连接走 core/ws.ts，不直接 new WebSocket / fetch。
const route = useRoute()
const router = useRouter()
const auth = useAuthStore()
const conversations = useConversationsStore()
const messages = useMessagesStore()

const selfUserId = computed(() => auth.user?.userId ?? '')
const activeId = computed(() =>
  typeof route.params.conversationId === 'string' ? route.params.conversationId : null,
)
const activeConversation = computed(() =>
  activeId.value ? conversations.getById(activeId.value) : undefined,
)
const activeTitle = computed(() => {
  const conversation = activeConversation.value
  if (!conversation) return ''
  if (conversation.type === 'group') return conversation.title || '群聊'
  const other = conversation.members.find((member) => member.userId !== selfUserId.value)
  return other?.displayName ?? conversation.title ?? '单聊会话'
})

// 当前会话的消息缓存（已落库事实消息 + 乐观发送中的本地消息）与历史分页状态。
const activeMessages = computed(() =>
  activeId.value ? messages.messagesOf(activeId.value) : [],
)
const activePending = computed(() =>
  activeId.value ? messages.pendingOf(activeId.value) : [],
)
const activeHasMore = computed(() => (activeId.value ? messages.hasMoreOf(activeId.value) : false))
const activeLoadingHistory = computed(() =>
  activeId.value ? messages.isLoadingMore(activeId.value) : false,
)
const activeLoaded = computed(() => (activeId.value ? messages.isLoaded(activeId.value) : true))

// 切换会话：首次进入拉最新一页历史（M2-05）；已加载过直接用缓存，后续新消息由 message.new 合并。
watch(
  activeId,
  (conversationId) => {
    if (!conversationId) return
    void messages.loadInitial(conversationId)
  },
  { immediate: true },
)

// 打开会话（或会话内收到新消息）时推进本地已读游标：未读角标随之清除。
// M3 接入 read.update 上报服务端后，改由服务端 read.updated 驱动。
watch(
  () => [activeId.value, activeConversation.value?.lastSeq ?? 0] as const,
  ([conversationId, lastSeq]) => {
    if (!conversationId || lastSeq <= 0) return
    conversations.markRead(conversationId, lastSeq)
  },
  { immediate: true },
)

onMounted(() => {
  void conversations.load()
  // 服务端推送：conv.updated 更新会话列表、message.ack/message.new 更新消息流（各自只订阅一次）。
  conversations.subscribeRealtime()
  messages.subscribeRealtime()
})

function selectConversation(conversationId: string): void {
  void router.push({ name: 'chat', params: { conversationId } })
}

function sendMessage(text: string): void {
  if (!activeId.value || !selfUserId.value) return
  messages.sendText(activeId.value, text, selfUserId.value)
}

function retryMessage(clientMsgId: string): void {
  if (!activeId.value) return
  messages.retrySend(activeId.value, clientMsgId)
}

function loadMoreMessages(): void {
  if (!activeId.value) return
  void messages.loadMore(activeId.value)
}
</script>

<template>
  <main class="chat-page">
    <aside class="chat-page__sidebar">
      <header class="chat-page__sidebar-header">
        <span>消息</span>
        <RouterLink class="chat-page__new" to="/contacts">发起聊天</RouterLink>
      </header>

      <p v-if="conversations.loading" class="chat-page__hint">加载中…</p>
      <p v-else-if="conversations.sorted.length === 0" class="chat-page__hint">
        还没有会话，去「通讯录」搜索用户发起单聊。
      </p>
      <nav v-else class="chat-page__list" aria-label="会话列表">
        <ConversationItem
          v-for="conversation in conversations.sorted"
          :key="conversation.conversationId"
          :conversation="conversation"
          :self-user-id="selfUserId"
          :active="conversation.conversationId === activeId"
          :unread="conversation.conversationId === activeId ? 0 : conversations.unreadOf(conversation.conversationId)"
          @select="selectConversation"
        />
      </nav>
    </aside>

    <section class="chat-page__main">
      <template v-if="activeConversation">
        <header class="chat-page__main-header">{{ activeTitle }}</header>
        <p v-if="!activeLoaded" class="chat-page__main-hint">消息加载中…</p>
        <MessageList
          :messages="activeMessages"
          :pending="activePending"
          :self-user-id="selfUserId"
          :has-more="activeHasMore"
          :loading="activeLoadingHistory"
          @load-more="loadMoreMessages"
          @retry="retryMessage"
        />
        <SendBox @send="sendMessage" />
      </template>
      <EmptyState
        v-else-if="activeId && conversations.loaded"
        title="会话不存在或无权访问"
        description="请从左侧选择已有会话，或到「通讯录」重新发起聊天。"
      />
      <EmptyState
        v-else
        title="选择会话开始聊天"
        description="从左侧选择会话，或到「通讯录」搜索用户发起单聊。"
      />
    </section>
  </main>
</template>

<style scoped>
.chat-page {
  display: grid;
  grid-template-columns: 280px 1fr;
  height: 100%;
  background: var(--color-surface);
}

.chat-page__sidebar {
  display: flex;
  flex-direction: column;
  overflow: hidden;
  border-right: 1px solid var(--color-border);
}

.chat-page__sidebar-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  height: var(--header-height);
  padding: 0 var(--space-4);
  font-weight: 600;
  border-bottom: 1px solid var(--color-border);
}

.chat-page__new {
  color: var(--color-primary);
  font-size: 13px;
  font-weight: 500;
  text-decoration: none;
}

.chat-page__list {
  flex: 1;
  overflow-y: auto;
}

.chat-page__hint {
  margin: var(--space-4);
  color: var(--color-text-secondary);
  font-size: 13px;
}

.chat-page__main {
  display: flex;
  flex-direction: column;
  min-height: 0;
}

.chat-page__main-header {
  display: flex;
  align-items: center;
  height: var(--header-height);
  padding: 0 var(--space-4);
  font-weight: 600;
  border-bottom: 1px solid var(--color-border);
}

.chat-page__main-hint {
  margin: var(--space-4);
  color: var(--color-text-secondary);
  font-size: 13px;
  text-align: center;
}
</style>
