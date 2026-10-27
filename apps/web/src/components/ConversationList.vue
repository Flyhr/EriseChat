<script setup lang="ts">
import { computed } from 'vue'
import type { Conversation } from '../types/frames'
import { filterConversations, type ConversationFilter } from '../core/chatLayout'
import { friendlyError } from '../core/errorMessages'
import { showToast } from '../core/toast'
import { useConversationsStore } from '../stores/conversations'
import AppIcon from './AppIcon.vue'
import ConversationItem from './ConversationItem.vue'

// 会话列表（UI-02 批次1，计划 §5.2）：头部搜索框 + 发起聊天、全部/未读/群聊本地筛选、复用 ConversationItem。
// F-11 体验调整：搜索以输入框形式放在「发起聊天」左侧（点击打开全局搜索面板），不再显示列表标题。
// 只消费现有 store 数据（不新增请求）；未读数以 unreadOf 为唯一口径（当前打开的会话传 0）。
// 筛选规则在 core/chatLayout.ts 的 filterConversations（纯函数，单测覆盖）。
// F-10：子组件「⋯」菜单发出 update-settings，这里统一调 store 更新并给轻提示。

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
  /** F-11：头部搜索框（点击打开全局搜索面板）。 */
  search: []
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

const searchPlaceholder = computed(() =>
  props.filter === 'group' ? '搜索群聊消息' : '搜索消息',
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

/** F-10：置顶/免打扰成功后的轻提示文案，按本次改动的字段决定。 */
function settingsToastText(
  settings: { pinned?: boolean; muted?: boolean },
  updated: Conversation,
): string {
  if (settings.pinned !== undefined) return updated.pinned ? '会话已置顶' : '已取消置顶'
  if (settings.muted !== undefined) return updated.muted ? '已开启消息免打扰' : '已关闭消息免打扰'
  return '设置已更新'
}

async function onUpdateSettings(payload: {
  conversationId: string
  settings: { pinned?: boolean; muted?: boolean }
}): Promise<void> {
  try {
    const updated = await conversationsStore.setMemberSettings(
      payload.conversationId,
      payload.settings,
    )
    showToast(settingsToastText(payload.settings, updated), 'success')
  } catch (error) {
    // 失败时 store 未写入，保持原状并给出可读原因（协议错误码已由 friendlyError 映射）。
    showToast(friendlyError(error), 'error')
  }
}
</script>

<template>
  <section class="ec-conversation-list" aria-label="会话列表">
    <header class="ec-conversation-list__header">
      <button
        type="button"
        class="ec-conversation-list__searchbox"
        :aria-label="searchPlaceholder"
        :title="searchPlaceholder"
        @click="emit('search')"
      >
        <AppIcon name="search" :size="16" />
        <span class="ec-conversation-list__searchbox-label">{{ searchPlaceholder }}</span>
      </button>
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
        @update-settings="onUpdateSettings"
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
  height: var(--ec-header-height);
  padding: 0 var(--ec-space-4);
  border-bottom: 1px solid var(--ec-border-soft);
}

/* F-11 体验调整：搜索以输入框形式放在「发起聊天」左侧，点击打开全局搜索面板。 */
.ec-conversation-list__searchbox {
  display: inline-flex;
  flex: 1;
  gap: var(--ec-space-2);
  align-items: center;
  min-width: 0;
  max-width: 280px;
  height: 32px;
  padding: 0 var(--ec-space-3);
  border: 1px solid transparent;
  border-radius: var(--ec-radius-full);
  background: var(--ec-nav-bg);
  color: var(--ec-text-secondary);
  font-size: var(--ec-font-size-secondary);
  text-align: left;
  cursor: pointer;
  transition:
    background var(--ec-transition-fast) var(--ec-ease),
    border-color var(--ec-transition-fast) var(--ec-ease),
    color var(--ec-transition-fast) var(--ec-ease);
}

.ec-conversation-list__searchbox-label {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

/* 悬停效果仅在鼠标精确指针设备生效，触屏不产生粘滞 hover。 */
@media (hover: hover) and (pointer: fine) {
.ec-conversation-list__searchbox:hover {
  border-color: var(--ec-border);
  background: var(--ec-surface-hover);
  color: var(--ec-text-primary);
}
}

.ec-conversation-list__searchbox:focus-visible {
  outline: var(--ec-focus-width) solid var(--ec-focus-ring);
  outline-offset: var(--ec-focus-offset);
}

.ec-conversation-list__create {
  display: inline-flex;
  gap: var(--ec-space-1);
  align-items: center;
  flex: none;
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
  padding: 5px var(--ec-space-3);
  border: 1px solid transparent;
  border-radius: var(--ec-radius-full);
  background: var(--ec-nav-bg);
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

/* 选中态必须常驻（原先写在 hover 媒体里导致触屏看不到）；卡片/列表筛选共用。 */
.ec-conversation-list__chip--active,
.ec-conversation-list__chip--active:hover {
  background: var(--ec-brand-050);
  color: var(--ec-brand-600);
  font-weight: var(--ec-font-weight-semibold);
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

  .ec-conversation-list__searchbox {
    max-width: none;
    height: 36px;
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
