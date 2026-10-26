<script setup lang="ts">
import { computed } from 'vue'
import type { Conversation } from '../types/frames'
import { filterConversations, type ConversationFilter } from '../core/chatLayout'
import { useConversationsStore } from '../stores/conversations'
import AppIcon from './AppIcon.vue'
import ConversationItem from './ConversationItem.vue'

// 会话列表（UI-02 批次1，计划 §5.2）：头部标题 + 发起聊天、全部/未读/群聊本地筛选、复用 ConversationItem。
// 只消费现有 store 数据（不新增请求）；未读数以 unreadOf 为唯一口径（当前打开的会话传 0）。
// 筛选规则在 core/chatLayout.ts 的 filterConversations（纯函数，单测覆盖）。

const props = defineProps<{
  conversations: readonly Conversation[]
  selfUserId: string
  activeId: string | null
  loading: boolean
  filter: ConversationFilter
}>()

const emit = defineEmits<{
  select: [conversationId: string]
  'update:filter': [filter: ConversationFilter]
  create: []
}>()

const conversationsStore = useConversationsStore()

const filters: { key: ConversationFilter; label: string }[] = [
  { key: 'all', label: '全部' },
  { key: 'unread', label: '未读' },
  { key: 'group', label: '群聊' },
]

const visible = computed(() =>
  filterConversations(props.conversations, props.filter, conversationsStore.unreadOf),
)

const emptyText = computed(() => {
  if (props.filter === 'unread') return '没有未读会话'
  if (props.filter === 'group') return '还没有群聊，到「联系人」创建群聊'
  return '还没有会话，去「联系人」搜索用户发起单聊或创建群聊'
})

/** 当前打开的会话不展示未读角标（进入会话即视为已读，由 Chat 页上报）。 */
function unreadOf(conversationId: string): number {
  if (conversationId === props.activeId) return 0
  return conversationsStore.unreadOf(conversationId)
}
</script>

<template>
  <section class="ec-conversation-list" aria-label="会话列表">
    <header class="ec-conversation-list__header">
      <h2 class="ec-conversation-list__title">消息</h2>
      <button
        type="button"
        class="ec-conversation-list__create"
        aria-label="发起聊天"
        @click="emit('create')"
      >
        <AppIcon name="plus" :size="16" />
        <span class="ec-conversation-list__create-label">发起聊天</span>
      </button>
    </header>

    <div class="ec-conversation-list__filters" role="tablist" aria-label="会话筛选">
      <button
        v-for="item in filters"
        :key="item.key"
        type="button"
        role="tab"
        class="ec-conversation-list__chip"
        :class="{ 'ec-conversation-list__chip--active': filter === item.key }"
        :aria-selected="filter === item.key"
        @click="emit('update:filter', item.key)"
      >
        {{ item.label }}
      </button>
    </div>

    <p v-if="loading && visible.length === 0" class="ec-conversation-list__hint">加载中…</p>
    <p v-else-if="visible.length === 0" class="ec-conversation-list__hint">{{ emptyText }}</p>
    <nav v-else class="ec-conversation-list__items">
      <ConversationItem
        v-for="conversation in visible"
        :key="conversation.conversationId"
        :conversation="conversation"
        :self-user-id="selfUserId"
        :active="conversation.conversationId === activeId"
        :unread="unreadOf(conversation.conversationId)"
        @select="emit('select', $event)"
      />
    </nav>
  </section>
</template>

<style scoped>
.ec-conversation-list {
  display: flex;
  flex-direction: column;
  min-width: 0;
  min-height: 0;
  overflow: hidden;
  background: var(--ec-list-bg);
  border-right: 1px solid var(--ec-border);
}

.ec-conversation-list__header {
  display: flex;
  flex: none;
  gap: var(--ec-space-2);
  align-items: center;
  justify-content: space-between;
  height: var(--ec-header-height);
  padding: 0 var(--ec-space-4);
  border-bottom: 1px solid var(--ec-border);
}

.ec-conversation-list__title {
  margin: 0;
  color: var(--ec-text-primary);
  font-size: 16px;
  font-weight: var(--ec-font-weight-semibold);
}

.ec-conversation-list__create {
  display: inline-flex;
  gap: var(--ec-space-1);
  align-items: center;
  padding: 4px var(--ec-space-2);
  border: none;
  border-radius: var(--ec-radius-sm);
  background: transparent;
  color: var(--ec-brand-600);
  font-size: var(--ec-font-size-secondary);
  font-weight: var(--ec-font-weight-medium);
  cursor: pointer;
  transition: background var(--ec-transition-fast) var(--ec-ease);
}

/* 悬停效果仅在鼠标精确指针设备生效，触屏不产生粘滞 hover。 */
@media (hover: hover) and (pointer: fine) {
.ec-conversation-list__create:hover {
  background: var(--ec-brand-050);
}
}

.ec-conversation-list__filters {
  display: flex;
  flex: none;
  gap: var(--ec-space-1);
  padding: var(--ec-space-3) var(--ec-space-4);
}

.ec-conversation-list__chip {
  padding: 4px var(--ec-space-3);
  border: 1px solid transparent;
  border-radius: var(--ec-radius-full);
  background: transparent;
  color: var(--ec-text-secondary);
  font-size: var(--ec-font-size-secondary);
  cursor: pointer;
  transition:
    background var(--ec-transition-fast) var(--ec-ease),
    color var(--ec-transition-fast) var(--ec-ease);
}

/* 悬停效果仅在鼠标精确指针设备生效，触屏不产生粘滞 hover。 */
@media (hover: hover) and (pointer: fine) {
.ec-conversation-list__chip:hover {
  background: var(--ec-surface-hover);
  color: var(--ec-text-primary);
}
}

/* 悬停效果仅在鼠标精确指针设备生效，触屏不产生粘滞 hover。 */
@media (hover: hover) and (pointer: fine) {
.ec-conversation-list__chip--active,
.ec-conversation-list__chip--active:hover {
  background: var(--ec-brand-050);
  color: var(--ec-brand-600);
  font-weight: var(--ec-font-weight-semibold);
}
}

.ec-conversation-list__items {
  flex: 1;
  min-height: 0;
  overflow-y: auto;
}

.ec-conversation-list__hint {
  margin: var(--ec-space-4);
  color: var(--ec-text-secondary);
  font-size: 13px;
  line-height: var(--ec-line-height-body);
}

/* UI-05 移动端：页面标题放大到 22px、发起聊天收成 44×44 图标按钮、
   筛选条吸顶并改为浅灰底胶囊，列表滚动时仍可切换；行间分隔由 ConversationItem 绘制。 */
@media (max-width: 800px) {
  .ec-conversation-list {
    width: 100%;
    border-right: none;
  }

  .ec-conversation-list__header {
    height: var(--ec-header-height-mobile);
    padding: 0 var(--ec-space-3) 0 var(--ec-space-4);
  }

  .ec-conversation-list__title {
    font-size: var(--ec-font-size-mobile-page-title);
  }

  .ec-conversation-list__create {
    justify-content: center;
    min-width: var(--ec-touch-target);
    min-height: var(--ec-touch-target);
    padding: 0;
    border-radius: var(--ec-radius-full);
  }

  .ec-conversation-list__create-label {
    display: none;
  }

  .ec-conversation-list__filters {
    position: sticky;
    top: 0;
    z-index: 2;
    gap: var(--ec-space-2);
    padding: var(--ec-space-2) var(--ec-space-4) var(--ec-space-3);
    background: var(--ec-list-bg);
  }

  .ec-conversation-list__chip {
    display: inline-flex;
    align-items: center;
    justify-content: center;
    min-height: var(--ec-touch-target);
    padding: 0 var(--ec-space-4);
    background: var(--ec-nav-bg);
    font-size: var(--ec-font-size-mobile-list-sub);
  }

  /* 触屏设备没有 hover，选中态必须常驻（桌面规则在 hover 媒体查询内）。 */
  .ec-conversation-list__chip--active {
    background: var(--ec-brand-050);
    color: var(--ec-brand-600);
    font-weight: var(--ec-font-weight-semibold);
  }

  .ec-conversation-list__hint {
    margin: var(--ec-space-5) var(--ec-space-4);
    text-align: center;
  }
}
</style>
