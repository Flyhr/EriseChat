<script setup lang="ts">
import { computed, onMounted } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import ConversationItem from '../components/ConversationItem.vue'
import EmptyState from '../components/EmptyState.vue'
import { useAuthStore } from '../stores/auth'
import { useConversationsStore } from '../stores/conversations'

// 聊天主页面（M2-04）：左侧会话列表 + 右侧选中会话占位；消息流与发送框在 M2-05 接入。
// 规则：页面只做编排，状态走 stores/，连接走 core/ws.ts，不直接 new WebSocket / fetch。
const route = useRoute()
const router = useRouter()
const auth = useAuthStore()
const conversations = useConversationsStore()

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

onMounted(() => {
  void conversations.load()
  // 订阅服务端 conv.updated：其他端新建/更新会话时刷新左侧列表（列表顺序由 store 统一计算）。
  conversations.subscribeRealtime()
})

function selectConversation(conversationId: string): void {
  void router.push({ name: 'chat', params: { conversationId } })
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
          @select="selectConversation"
        />
      </nav>
    </aside>

    <section class="chat-page__main">
      <template v-if="activeConversation">
        <header class="chat-page__main-header">{{ activeTitle }}</header>
        <div class="chat-page__main-body">
          <EmptyState title="消息流即将接入" description="M2-05 实现消息收发、发送状态机、seq 去重与断线补拉。" />
        </div>
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
}

.chat-page__main-header {
  display: flex;
  align-items: center;
  height: var(--header-height);
  padding: 0 var(--space-4);
  font-weight: 600;
  border-bottom: 1px solid var(--color-border);
}

.chat-page__main-body {
  display: flex;
  flex: 1;
  align-items: center;
  justify-content: center;
  padding: var(--space-5);
}
</style>
