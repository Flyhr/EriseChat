<script setup lang="ts">
import { computed, onBeforeUnmount, ref, watch } from 'vue'
import { useRouter } from 'vue-router'
import { messagesApi } from '../api/messages'
import type { Conversation, Message } from '../types/frames'
import { filterConversations, type ConversationFilter } from '../core/chatLayout'
import { friendlyError } from '../core/errorMessages'
import { showToast } from '../core/toast'
import { useConversationsStore } from '../stores/conversations'
import { SEARCH_DEBOUNCE_MS, SEARCH_PAGE_SIZE } from '../stores/search'
import AppIcon from './AppIcon.vue'
import ConversationItem from './ConversationItem.vue'
import SearchInput from './SearchInput.vue'
import SearchResultItem from './SearchResultItem.vue'

// 会话列表（UI-02 批次1，计划 §5.2）：头部搜索框 + 发起聊天、全部/未读/群聊本地筛选、复用 ConversationItem。
// F-11 体验调整：搜索框放在「发起聊天」左侧，可直接输入（250ms 防抖）在列表区内联展示命中消息，
// 点击结果跳到对应会话并定位；清空关键词恢复会话列表。不再显示列表标题。
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

const router = useRouter()

const searchKeyword = ref('')
const searchResults = ref<Message[]>([])
const searchLoading = ref(false)
const searchLoadingMore = ref(false)
const searchError = ref('')
const searchHasMore = ref(false)
const searchedKeyword = ref('')

let searchTimer: ReturnType<typeof setTimeout> | null = null
let searchRequestSeq = 0

const isSearchMode = computed(() => searchKeyword.value.trim().length > 0)

const searchPlaceholder = computed(() =>
  props.filter === 'group' ? '搜索群聊消息' : '搜索消息',
)

/** 群聊筛选下只展示群会话的命中（服务端全局搜索，这里按当前筛选本地过滤）。 */
const visibleSearchResults = computed(() =>
  props.filter === 'group'
    ? searchResults.value.filter(
        (message) => conversationsStore.getById(message.conversationId)?.type === 'group',
      )
    : searchResults.value,
)

const searchHint = computed(() => {
  if (searchError.value) return searchError.value
  if (searchLoading.value) return '搜索中…'
  return '没有找到匹配的消息'
})

function clearSearchTimer(): void {
  if (searchTimer !== null) {
    clearTimeout(searchTimer)
    searchTimer = null
  }
}

function resetSearchResults(): void {
  searchRequestSeq += 1
  clearSearchTimer()
  searchResults.value = []
  searchHasMore.value = false
  searchError.value = ''
  searchLoading.value = false
  searchLoadingMore.value = false
  searchedKeyword.value = ''
}

async function runSearch(keyword: string, beforeSeq?: number): Promise<void> {
  const seq = ++searchRequestSeq
  if (beforeSeq === undefined) {
    searchLoading.value = true
    searchResults.value = []
    searchHasMore.value = false
  } else {
    searchLoadingMore.value = true
  }
  searchError.value = ''
  try {
    const page = await messagesApi.search(keyword, {
      beforeSeq,
      limit: SEARCH_PAGE_SIZE,
    })
    if (seq !== searchRequestSeq) return
    if (beforeSeq === undefined) {
      searchResults.value = page.messages
      searchedKeyword.value = keyword
    } else {
      searchResults.value = [...searchResults.value, ...page.messages]
    }
    searchHasMore.value = page.hasMore
  } catch (error) {
    if (seq === searchRequestSeq) {
      searchError.value = friendlyError(error)
      if (beforeSeq === undefined) {
        searchResults.value = []
        searchHasMore.value = false
      }
    }
  } finally {
    if (seq === searchRequestSeq) {
      searchLoading.value = false
      searchLoadingMore.value = false
    }
  }
}

function scheduleSearch(): void {
  clearSearchTimer()
  const keyword = searchKeyword.value.trim()
  if (!keyword) {
    resetSearchResults()
    return
  }
  searchTimer = setTimeout(() => {
    searchTimer = null
    void runSearch(keyword)
  }, SEARCH_DEBOUNCE_MS)
}

function clearSearch(): void {
  searchKeyword.value = ''
}

function loadMoreSearch(): void {
  if (searchLoading.value || searchLoadingMore.value || !searchHasMore.value) return
  const last = searchResults.value[searchResults.value.length - 1]
  if (!last || !Number.isFinite(last.seq)) return
  void runSearch(searchedKeyword.value, last.seq)
}

function selectSearchResult(message: Message): void {
  if (!Number.isFinite(message.seq)) return
  clearSearch()
  void router.push({
    name: 'chat',
    params: { conversationId: message.conversationId },
    query: { jumpSeq: String(message.seq), jumpMessageId: message.messageId },
  })
}

watch(searchKeyword, scheduleSearch)

onBeforeUnmount(clearSearchTimer)

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
  settings: { pinned?: boolean; muted?: boolean; markedUnread?: boolean },
  updated: Conversation,
): string {
  if (settings.pinned !== undefined) return updated.pinned ? '会话已置顶' : '已取消置顶'
  if (settings.muted !== undefined) return updated.muted ? '已开启消息免打扰' : '已关闭消息免打扰'
  if (settings.markedUnread !== undefined) {
    return updated.markedUnread ? '已标为未读' : '已取消未读标记'
  }
  return '设置已更新'
}

async function onUpdateSettings(payload: {
  conversationId: string
  settings: { pinned?: boolean; muted?: boolean; markedUnread?: boolean }
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
    <!-- UI-D P2：补回参考图的列表标题（F-11 移除后信息层级偏弱）。 -->
    <div class="ec-conversation-list__titlebar">
      <div class="ec-conversation-list__greeting">
        <p class="ec-conversation-list__eyebrow">欢迎回来</p>
        <h1 class="ec-conversation-list__title">消息</h1>
      </div>
    </div>
    <header class="ec-conversation-list__header">
      <SearchInput
        v-model="searchKeyword"
        class="ec-conversation-list__searchbox"
        :placeholder="searchPlaceholder"
        :aria-label="searchPlaceholder"
        @escape="clearSearch"
      />
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

    <template v-if="isSearchMode">
      <p v-if="visibleSearchResults.length === 0" class="ec-conversation-list__hint">{{ searchHint }}</p>
      <nav v-else class="ec-conversation-list__items ec-fade" aria-label="搜索结果">
        <SearchResultItem
          v-for="message in visibleSearchResults"
          :key="`${message.conversationId}-${message.messageId}`"
          :message="message"
          :query="searchedKeyword"
          @select="selectSearchResult"
        />
        <button
          v-if="searchHasMore"
          type="button"
          class="ec-conversation-list__more"
          :disabled="searchLoadingMore"
          @click="loadMoreSearch"
        >
          {{ searchLoadingMore ? '加载中…' : '加载更多' }}
        </button>
      </nav>
    </template>
    <template v-else>
      <div
        v-if="loading && visible.length === 0"
        class="ec-conversation-list__loading"
        role="status"
        aria-label="会话加载中"
        aria-busy="true"
      >
        <div v-for="index in 6" :key="index" class="ec-conversation-list__loading-row">
          <div class="ec-skeleton ec-conversation-list__loading-avatar" />
          <div class="ec-conversation-list__loading-lines">
            <div class="ec-skeleton ec-conversation-list__loading-line" />
            <div class="ec-skeleton ec-conversation-list__loading-line ec-conversation-list__loading-line--short" />
          </div>
        </div>
      </div>
      <p v-else-if="visible.length === 0" class="ec-conversation-list__hint">{{ emptyText }}</p>
      <nav v-else class="ec-conversation-list__items ec-fade">
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
    </template>
  </section>
</template>

<style scoped>
/* UI-D P2：列表标题行，与聊天顶栏形成明确的信息层级。 */
.ec-conversation-list__titlebar {
  display: flex;
  flex: none;
  align-items: center;
  height: 44px;
  padding: 0 var(--ec-space-4);
}

.ec-conversation-list__greeting {
  display: flex;
  flex-direction: column;
  min-width: 0;
}

/* UI-D P3：参考图 08 的欢迎语只在移动端出现，桌面保持单行标题。 */
.ec-conversation-list__eyebrow {
  display: none;
  margin: 0;
  color: var(--ec-text-secondary);
  font-size: 11px;
  line-height: 1.3;
}

@media (max-width: 800px) {
  .ec-conversation-list__titlebar {
    height: 56px;
  }

  .ec-conversation-list__eyebrow {
    display: block;
  }
}

.ec-conversation-list__title {
  margin: 0;
  color: var(--ec-text-primary);
  font-size: 18px;
  font-weight: var(--ec-font-weight-semibold);
  line-height: 1.3;
}

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

/* 搜索框组件（SearchInput）只负责外观，这里限制它在头部占用的宽度。 */
.ec-conversation-list__searchbox {
  flex: 1;
  max-width: 280px;
}

.ec-conversation-list__more {
  display: block;
  margin: var(--ec-space-2) auto var(--ec-space-3);
  padding: var(--ec-space-2) var(--ec-space-4);
  border: 1px solid var(--ec-border);
  border-radius: var(--ec-radius-full);
  background: var(--ec-surface);
  color: var(--ec-brand-600);
  font-size: var(--ec-font-size-secondary);
  cursor: pointer;
}

.ec-conversation-list__more:disabled {
  color: var(--ec-text-tertiary);
  cursor: default;
}

.ec-conversation-list__more:focus-visible {
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

/* U-01：会话列表骨架，行高对齐 ConversationItem 的 72px（移动端 68px）。 */
.ec-conversation-list__loading-row {
  display: flex;
  gap: var(--ec-space-3);
  align-items: center;
  min-height: 72px;
  padding: var(--ec-space-3) var(--ec-space-4);
  border-bottom: 1px solid var(--ec-border-soft);
}

.ec-conversation-list__loading-avatar {
  width: 44px;
  height: 44px;
  border-radius: var(--ec-radius-full);
}

.ec-conversation-list__loading-lines {
  display: flex;
  flex: 1;
  flex-direction: column;
  gap: var(--ec-space-2);
}

.ec-conversation-list__loading-line {
  height: 12px;
}

.ec-conversation-list__loading-line--short {
  width: 55%;
}

@media (max-width: 800px) {
  .ec-conversation-list__loading-row {
    min-height: var(--ec-row-min-height-mobile, 68px);
  }
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
