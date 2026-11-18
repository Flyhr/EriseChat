<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { useRouter } from 'vue-router'
import type { Conversation, Message } from '../types/frames'
import type { UserProfile } from '../api/users'
import { filterConversations, type ConversationFilter } from '../core/chatLayout'
import { friendlyError } from '../core/errorMessages'
import { useInlineSearch } from '../core/inlineSearch'
import { enter, type MotionHandle } from '../core/motion'
import { showToast } from '../core/toast'
import { useConversationsStore } from '../stores/conversations'
import AppIcon from './AppIcon.vue'
import ConversationItem from './ConversationItem.vue'
import InlineSearchResults from './InlineSearchResults.vue'
import SearchInput from './SearchInput.vue'

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
  /** 好友备注取值：非空时优先作为单聊会话标题。 */
  remarkOf?: (userId: string) => string | undefined
}>()

const emit = defineEmits<{
  select: [conversationId: string]
  'select-user': [user: UserProfile]
  'update:filter': [filter: ConversationFilter]
  create: []
}>()

const conversationsStore = useConversationsStore()

// 批次 B2：离开列表（去设置/联系人）再回来时恢复滚动位置；手机单栏下列表只是隐藏，位置天然保留。
// 用 sessionStorage：只服务于本次会话内的返回恢复，不跨浏览器重启。
const LIST_SCROLL_KEY = 'erisechat.conversationListScroll'
const listRef = ref<HTMLElement | null>(null)

function readListScroll(): number {
  try {
    const value = Number(sessionStorage.getItem(LIST_SCROLL_KEY) ?? '0')
    return Number.isFinite(value) && value > 0 ? value : 0
  } catch {
    return 0
  }
}

function saveListScroll(): void {
  const element = listRef.value
  if (!element) return
  try {
    sessionStorage.setItem(LIST_SCROLL_KEY, String(element.scrollTop))
  } catch {
    // 隐私模式/存储不可用：只影响返回恢复，不影响列表使用。
  }
}

const filters: { key: ConversationFilter; label: string }[] = [
  { key: 'all', label: '全部' },
  { key: 'unread', label: '未读' },
  { key: 'group', label: '群聊' },
]

const visible = computed(() =>
  filterConversations(props.conversations, props.filter, conversationsStore.unreadOf),
)

// 首次渲染出会话后恢复上次滚动位置（骨架/空态期间容器不存在，不能提前恢复）。
let listScrollRestored = false
watch(
  () => visible.value.length,
  (count) => {
    if (listScrollRestored || count === 0) return
    listScrollRestored = true
    const value = readListScroll()
    if (value === 0) return
    void nextTick(() => {
      if (listRef.value) listRef.value.scrollTop = value
    })
  },
  { immediate: true },
)

// 列表内联搜索（F-11 体验调整）：直接在本页搜索框内完成「会话 / 联系人 / 聊天信息」搜索，
// 不再打开全局 SearchDialog；Ctrl/Cmd+K 的全局面板保持不变。
const router = useRouter()
const {
  query,
  category,
  keyword,
  hasKeyword,
  conversationMatches,
  contactMatches,
  strangerMatches,
  messageMatches,
  loading: searchLoading,
  error: searchError,
  clear: clearSearch,
  setCategory,
} = useInlineSearch()

function selectSearchConversation(conversationId: string): void {
  emit('select', conversationId)
}

async function selectSearchMessage(message: Message): Promise<void> {
  await router.push({
    name: 'chat',
    params: { conversationId: message.conversationId },
    query: { jumpSeq: String(message.seq), jumpMessageId: message.messageId },
  })
}

function selectSearchUser(user: UserProfile): void {
  emit('select-user', user)
}

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

// U-17：首屏骨架一次性入场。骨架行按 --ec-motion-stagger 错峰，总跨度受
// --ec-motion-stagger-max 约束；真实会话行不加 stagger（AGENTS.md §14.9 消息流禁令）。
const loadingRef = ref<HTMLElement | null>(null)
let loadingMotion: MotionHandle | null = null

function playSkeletonEntrance(): void {
  const container = loadingRef.value
  if (!container) return
  loadingMotion?.cancel()
  // 逐行错峰而不是整块淡入：骨架行数量固定，跨度由 staggerStep 压到上限内。
  loadingMotion = enter(Array.from(container.children), { stagger: true })
}

onMounted(playSkeletonEntrance)
onBeforeUnmount(() => {
  loadingMotion?.cancel()
  loadingMotion = null
})

watch(
  () => props.loading && visible.value.length === 0,
  (show) => {
    if (show) playSkeletonEntrance()
    else loadingMotion?.cancel()
  },
  { flush: 'post' },
)

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
    <header class="ec-conversation-list__header">
      <SearchInput
        v-model="query"
        class="ec-conversation-list__searchbox"
        placeholder="搜索会话、联系人或消息"
        aria-label="搜索会话、联系人或消息"
        :maxlength="64"
        @escape="clearSearch"
      />
      <button
        type="button"
        class="ec-conversation-list__create"
        aria-label="发起聊天"
        title="发起聊天"
        @click="emit('create')"
      >
        <AppIcon name="plus" :size="18" />
      </button>
    </header>

    <div v-if="!hasKeyword" class="ec-conversation-list__filters" role="tablist" aria-label="会话筛选">
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

    <template v-if="!hasKeyword">
      <div
        v-if="loading && visible.length === 0"
        ref="loadingRef"
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
      <nav
        v-else
        ref="listRef"
        class="ec-conversation-list__items ec-fade"
        @scroll.passive="saveListScroll"
      >
        <ConversationItem
          v-for="conversation in visible"
          :key="conversation.conversationId"
          :conversation="conversation"
          :self-user-id="selfUserId"
          :active="conversation.conversationId === activeId"
          :unread="unreadOf(conversation.conversationId)"
          :remark-of="remarkOf"
          @select="emit('select', $event)"
          @update-settings="onUpdateSettings"
        />
      </nav>
    </template>

    <InlineSearchResults
      v-else
      class="ec-conversation-list__search-results"
      :query="keyword"
      :category="category"
      :loading="searchLoading"
      :error="searchError"
      :conversations="conversationMatches"
      :contacts="contactMatches"
      :strangers="strangerMatches"
      :messages="messageMatches"
      :self-user-id="selfUserId"
      :remark-of="remarkOf"
      show-conversations
      @update:category="setCategory"
      @select-conversation="selectSearchConversation"
      @select-message="selectSearchMessage"
      @select-user="selectSearchUser"
    />
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
  border-bottom: 1px solid var(--ec-border-soft);
}

/* 与联系人页 SearchInput 同一外观（胶囊、导航底、右侧搜索图标），输入即内联搜索。 */
.ec-conversation-list__searchbox {
  flex: 1;
  max-width: 280px;
}

/* 内联搜索结果占满列表区（芯片 + 分组结果 + 滚动）。 */
.ec-conversation-list__search-results {
  flex: 1;
  min-height: 0;
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
  flex: none;
  align-items: center;
  justify-content: center;
  width: 36px;
  height: 36px;
  padding: 0;
  border: none;
  border-radius: var(--ec-radius-sm);
  background: transparent;
  color: var(--ec-brand-600);
  cursor: pointer;
  transition: background var(--ec-transition-fast) var(--ec-ease);
}

/* 悬停效果仅在鼠标精确指针设备生效，触屏不产生粘滞 hover。 */
@media (hover: hover) and (pointer: fine) {
.ec-conversation-list__create:hover {
  background: var(--ec-brand-050);
}
}

.ec-conversation-list__create:focus-visible {
  outline: var(--ec-focus-width) solid var(--ec-focus-ring);
  outline-offset: var(--ec-focus-offset);
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
  color: var(--ec-brand-700);
  font-weight: var(--ec-font-weight-semibold);
}

.ec-conversation-list__chip:focus-visible {
  outline: var(--ec-focus-width) solid var(--ec-focus-ring);
  outline-offset: var(--ec-focus-offset);
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
