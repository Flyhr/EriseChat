<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, ref, watch } from 'vue'
import { useRouter } from 'vue-router'
import { conversationTitle } from '../core/conversationTitle'
import { mergeLocalSearchResults } from '../core/localSearch'
import NoticeBar from './NoticeBar.vue'
import SearchResultItem from './SearchResultItem.vue'
import { useAuthStore } from '../stores/auth'
import { useConversationsStore } from '../stores/conversations'
import { useSearchStore } from '../stores/search'
import type { Message } from '../types/frames'

// 消息搜索面板（F-11 批次2，计划 §F-11）：全局/会话内共用，App.vue 挂载一次。
// 只展示 REST 返回的 Message（text 消息），点击结果跳转到对应会话并定位（jumpSeq，复用 F-09 机制）。
// 渲染一律文本节点 + 高亮 span，禁止 v-html。

const router = useRouter()
const auth = useAuthStore()
const conversations = useConversationsStore()
const search = useSearchStore()

const inputRef = ref<HTMLInputElement | null>(null)
const panelRef = ref<HTMLElement | null>(null)

/** F-36：筛选条件（发送人用户 ID + 本地日期范围）；日期按浏览器时区换算为毫秒时间戳。 */
const senderInput = ref('')
const fromDate = ref('')
const toDate = ref('')

const filtersActive = computed(
  () =>
    senderInput.value.trim().length > 0 ||
    fromDate.value.length > 0 ||
    toDate.value.length > 0,
)

function toEpochMillis(dateText: string, endOfDay: boolean): number | null {
  const match = /^(\d{4})-(\d{2})-(\d{2})$/.exec(dateText.trim())
  if (!match) return null
  const date = new Date(Number(match[1]), Number(match[2]) - 1, Number(match[3]))
  const time = date.getTime()
  if (Number.isNaN(time)) return null
  return endOfDay ? time + 24 * 60 * 60 * 1000 - 1 : time
}

function applyFilters(): void {
  search.setFilters({
    senderId: senderInput.value,
    from: toEpochMillis(fromDate.value, false),
    to: toEpochMillis(toDate.value, true),
  })
}

function clearFilters(): void {
  senderInput.value = ''
  fromDate.value = ''
  toDate.value = ''
  search.setFilters({ senderId: '', from: null, to: null })
}

/** F-37：显式链接模式——只匹配识别出的 HTTP/HTTPS URL。 */
function toggleLinkOnly(event: Event): void {
  search.setLinkOnly((event.target as HTMLInputElement).checked)
}
const activeIndex = ref(0)
let lastFocused: HTMLElement | null = null

const placeholder = computed(() =>
  search.isGlobal ? '搜索全部会话的消息' : '搜索当前会话的消息',
)

const title = computed(() =>
  search.isGlobal || !search.scopeConversationId
    ? '搜索消息'
    : `搜索：${conversationName(search.scopeConversationId)}`,
)

/** F-19：当前展示的列表——服务端结果优先；会话内尚未返回时展示本地即时结果。 */
const displayList = computed<Message[]>(() => {
  if (search.results.length > 0) {
    // F-19-05：服务端只检索 text 消息，本地还会命中文件名；合并后保留本地独有命中（去重按服务端优先）。
    return search.isGlobal
      ? search.results
      : mergeLocalSearchResults(search.results, search.localResults)
  }
  if (!search.isGlobal) return search.localResults
  return []
})

/** 当前是否为「本地已加载」列表（服务端结果未到/为空且本地有命中）。 */
const isLocalList = computed(
  () => !search.isGlobal && search.results.length === 0 && search.localResults.length > 0,
)

/** 高亮关键词：本地列表用本地已提交关键词，服务端列表用服务端检索关键词。 */
const displayQuery = computed(() =>
  isLocalList.value ? search.localQuery : search.searchedQuery,
)

const hasKeyword = computed(() => Boolean(search.query.trim()))

/** F-19：会话内命中数提示（区分搜索中/离线/服务端无结果等状态）。 */
const countsText = computed(() => {
  if (!hasKeyword.value) return ''
  // 全局搜索：补齐原先缺失的结果计数（会话内模式仍走下面的本地命中口径）。
  if (search.isGlobal) {
    if (search.loading && search.results.length === 0) return '正在搜索…'
    if (search.results.length > 0) {
      return `共 ${displayList.value.length} 条${search.hasMore ? '（可继续加载更多）' : ''}`
    }
    return ''
  }
  if (search.localTotal === 0) return ''
  if (search.offlineFallback) return `本地 ${search.localTotal} 条（网络不可用，仅显示本地已加载）`
  if (search.loading && search.results.length === 0) {
    return `本地 ${search.localTotal} 条，正在搜索更早历史…`
  }
  if (search.results.length > 0) {
    return `共 ${displayList.value.length} 条（本地命中 ${search.localTotal} 条）`
  }
  if (search.localResults.length > 0) return `本地 ${search.localTotal} 条（更早历史无匹配）`
  return ''
})

const emptyText = computed(() => {
  if (search.error) return search.error
  if (search.loading && displayList.value.length === 0) return '搜索中…'
  if (!hasKeyword.value) return '输入关键词搜索历史消息'
  if (search.offlineFallback) return '网络不可用，本地已加载的消息中也没有匹配'
  if (!search.isGlobal) return '没有找到匹配的消息（本地已加载 + 服务端历史）'
  return '没有找到匹配的消息'
})

/** 会话名：群聊用「群名（人数）」，单聊取对方昵称，兜底会话 ID 前 8 位。 */
function conversationName(conversationId: string): string {
  return conversationTitle(conversations.getById(conversationId), auth.user?.userId, {
    fallback: conversationId.slice(0, 8),
  })
}

function close(): void {
  search.close()
}

async function selectResult(message: Message): Promise<void> {
  if (!Number.isFinite(message.seq)) return
  search.close()
  await router.push({
    name: 'chat',
    params: { conversationId: message.conversationId },
    query: { jumpSeq: String(message.seq), jumpMessageId: message.messageId },
  })
}

function moveSelection(delta: number): void {
  const length = displayList.value.length
  if (length === 0) return
  const next = (activeIndex.value + delta + length) % length
  activeIndex.value = next
  void scrollActiveIntoView()
}

async function scrollActiveIntoView(): Promise<void> {
  await nextTick()
  panelRef.value
    ?.querySelectorAll<HTMLElement>('.ec-search__result')
    [activeIndex.value]?.scrollIntoView({ block: 'nearest' })
}

function onInputKeydown(event: KeyboardEvent): void {
  if (event.key === 'ArrowDown') {
    event.preventDefault()
    moveSelection(1)
  } else if (event.key === 'ArrowUp') {
    event.preventDefault()
    moveSelection(-1)
  } else if (event.key === 'Enter') {
    event.preventDefault()
    const message = displayList.value[activeIndex.value]
    if (message) void selectResult(message)
  }
}

function onPanelKeydown(event: KeyboardEvent): void {
  if (event.key === 'Escape') {
    event.preventDefault()
    close()
  }
}

function onOverlayMousedown(event: MouseEvent): void {
  if (event.target === event.currentTarget) close()
}

watch(
  () => search.query,
  () => {
    activeIndex.value = 0
    search.search()
  },
)

watch(
  () => displayList.value.length,
  () => {
    activeIndex.value = 0
  },
)

watch(
  () => search.isOpen,
  async (open) => {
    if (open) {
      lastFocused = document.activeElement instanceof HTMLElement ? document.activeElement : null
      senderInput.value = ''
      fromDate.value = ''
      toDate.value = ''
      await nextTick()
      inputRef.value?.focus()
    } else if (lastFocused) {
      lastFocused.focus()
      lastFocused = null
    }
  },
)

onBeforeUnmount(() => {
  search.close()
})
</script>

<template>
  <Teleport to="body">
    <div
      v-if="search.isOpen"
      class="ec-search-mask"
      @mousedown="onOverlayMousedown"
    >
      <section
        ref="panelRef"
        class="ec-search"
        role="dialog"
        aria-modal="true"
        :aria-label="title"
        @keydown="onPanelKeydown"
      >
        <header class="ec-search__header">
          <h2 class="ec-search__title">{{ title }}</h2>
          <button
            type="button"
            class="ec-search__close"
            aria-label="关闭搜索"
            title="关闭搜索（Esc）"
            @click="close"
          >
            ✕
          </button>
        </header>

        <div class="ec-search__input-row">
          <input
            ref="inputRef"
            v-model="search.query"
            type="search"
            class="ec-search__input"
            :placeholder="placeholder"
            aria-label="搜索关键词"
            @keydown="onInputKeydown"
          />
        </div>

        <div class="ec-search__filters">
          <input
            v-model="senderInput"
            type="search"
            class="ec-search__filter-input"
            placeholder="发送人用户 ID"
            aria-label="按发送人筛选"
            @change="applyFilters"
          />
          <label class="ec-search__filter-date">
            <span>起始</span>
            <input v-model="fromDate" type="date" :max="toDate || undefined" @change="applyFilters" />
          </label>
          <label class="ec-search__filter-date">
            <span>结束</span>
            <input v-model="toDate" type="date" :min="fromDate || undefined" @change="applyFilters" />
          </label>
          <label class="ec-search__filter-check">
            <input type="checkbox" :checked="search.linkOnly" @change="toggleLinkOnly" />
            <span>仅链接</span>
          </label>
          <button
            v-if="filtersActive"
            type="button"
            class="ec-search__filter-clear"
            @click="clearFilters"
          >
            清除筛选
          </button>
        </div>

        <div class="ec-search__body">
          <NoticeBar
            v-if="search.offlineFallback"
            class="ec-search__notice"
            tone="warning"
            text="网络不可用，仅显示本地已加载的消息"
          />

          <p v-if="countsText" class="ec-search__counts">{{ countsText }}</p>

          <p v-if="emptyText && displayList.length === 0 && !search.loading" class="ec-search__hint">
            {{ emptyText }}
          </p>

          <template v-else-if="displayList.length > 0">
            <p v-if="isLocalList" class="ec-search__source">本地已加载</p>
            <ul class="ec-search__results" role="listbox" aria-label="搜索结果">
              <li
                v-for="(message, index) in displayList"
                :key="`${message.conversationId}-${message.messageId}`"
              >
                <SearchResultItem
                  :message="message"
                  :query="displayQuery"
                  :active="index === activeIndex"
                  @select="selectResult"
                  @mouseenter="activeIndex = index"
                />
              </li>
            </ul>
          </template>

          <button
            v-if="search.hasMore && search.results.length > 0"
            type="button"
            class="ec-search__more"
            :disabled="search.loadingMore"
            @click="search.loadMore()"
          >
            {{ search.loadingMore ? '加载中…' : '加载更多' }}
          </button>
        </div>
      </section>
    </div>
  </Teleport>
</template>

<style scoped>
.ec-search-mask {
  position: fixed;
  inset: 0;
  z-index: 70;
  display: flex;
  align-items: flex-start;
  justify-content: center;
  padding: calc(12vh + env(safe-area-inset-top, 0px)) var(--ec-space-4)
    calc(var(--ec-space-4) + env(safe-area-inset-bottom, 0px));
  background: var(--ec-scrim);
}

.ec-search {
  display: flex;
  flex-direction: column;
  width: min(640px, 100%);
  max-height: min(72vh, 720px);
  overflow: hidden;
  border: 1px solid var(--ec-border);
  border-radius: var(--ec-radius-panel);
  background: var(--ec-surface);
  box-shadow: var(--ec-shadow-lg);
}

.ec-search__header {
  display: flex;
  flex: none;
  align-items: center;
  justify-content: space-between;
  padding: var(--ec-space-3) var(--ec-space-4);
  border-bottom: 1px solid var(--ec-border-soft);
}

.ec-search__title {
  margin: 0;
  overflow: hidden;
  color: var(--ec-text-primary);
  font-size: var(--ec-font-size-body);
  font-weight: var(--ec-font-weight-semibold);
  text-overflow: ellipsis;
  white-space: nowrap;
}

.ec-search__close {
  display: grid;
  flex: none;
  place-items: center;
  width: 32px;
  height: 32px;
  border: none;
  border-radius: var(--ec-radius-sm);
  background: transparent;
  color: var(--ec-text-secondary);
  font-size: 16px;
  cursor: pointer;
}

.ec-search__close:hover {
  background: var(--ec-surface-hover);
  color: var(--ec-text-primary);
}

.ec-search__close:focus-visible {
  outline: var(--ec-focus-width) solid var(--ec-focus-ring);
  outline-offset: var(--ec-focus-offset);
}

.ec-search__input-row {
  flex: none;
  padding: var(--ec-space-3) var(--ec-space-4);
  border-bottom: 1px solid var(--ec-border-soft);
}

.ec-search__input {
  width: 100%;
  min-height: 40px;
  padding: 0 var(--ec-space-3);
  border: 1px solid var(--ec-border);
  border-radius: var(--ec-radius-input);
  background: var(--ec-bg-canvas);
  color: var(--ec-text-primary);
  font-size: var(--ec-font-size-body);
}

.ec-search__input:focus-visible {
  border-color: var(--ec-brand-600);
  outline: var(--ec-focus-width) solid var(--ec-focus-ring);
  outline-offset: var(--ec-focus-offset);
}

.ec-search__filters {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: var(--ec-space-2);
  flex: none;
  padding: 0 var(--ec-space-4) var(--ec-space-2);
}

.ec-search__filter-input {
  flex: 1 1 160px;
  min-width: 0;
  min-height: 32px;
  padding: 0 var(--ec-space-2);
  border: 1px solid var(--ec-border);
  border-radius: var(--ec-radius-input);
  background: var(--ec-bg-canvas);
  color: var(--ec-text-primary);
  font-size: var(--ec-font-size-small);
}

.ec-search__filter-date {
  display: inline-flex;
  align-items: center;
  gap: var(--ec-space-1);
  color: var(--ec-text-secondary);
  font-size: var(--ec-font-size-small);
}

.ec-search__filter-date input {
  min-height: 32px;
  padding: 0 var(--ec-space-1);
  border: 1px solid var(--ec-border);
  border-radius: var(--ec-radius-input);
  background: var(--ec-bg-canvas);
  color: var(--ec-text-primary);
  font-size: var(--ec-font-size-small);
}

.ec-search__filter-clear {
  min-height: 32px;
  padding: 0 var(--ec-space-2);
  border: 0;
  background: none;
  color: var(--ec-brand-600);
  font-size: var(--ec-font-size-small);
  cursor: pointer;
}

.ec-search__body {
  flex: 1;
  min-height: 0;
  overflow-y: auto;
  padding: var(--ec-space-2) var(--ec-space-2) var(--ec-space-3);
}

.ec-search__hint {
  margin: var(--ec-space-5) 0;
  color: var(--ec-text-tertiary);
  font-size: var(--ec-font-size-secondary);
  text-align: center;
}

/* F-19：离线降级提示、命中数、本地已加载角标。 */
.ec-search__notice {
  margin: 0 var(--ec-space-2) var(--ec-space-2);
}

.ec-search__counts {
  margin: 0 var(--ec-space-3) var(--ec-space-1);
  color: var(--ec-text-secondary);
  font-size: var(--ec-font-size-small);
}

.ec-search__source {
  display: inline-flex;
  margin: var(--ec-space-1) var(--ec-space-3);
  padding: 1px var(--ec-space-2);
  border-radius: var(--ec-radius-sm);
  background: var(--ec-brand-050);
  color: var(--ec-brand-600);
  font-size: var(--ec-font-size-small);
  font-weight: var(--ec-font-weight-medium);
}

.ec-search__results {
  display: flex;
  flex-direction: column;
  gap: 2px;
  margin: 0;
  padding: 0;
  list-style: none;
}

.ec-search__result {
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

.ec-search__result--active,
.ec-search__result:hover {
  background: var(--ec-brand-050);
}

.ec-search__result:focus-visible {
  outline: var(--ec-focus-width) solid var(--ec-focus-ring);
  outline-offset: calc(var(--ec-focus-offset) * -1);
}

.ec-search__result-top {
  display: flex;
  gap: var(--ec-space-2);
  align-items: baseline;
  justify-content: space-between;
}

.ec-search__result-conversation {
  overflow: hidden;
  font-size: var(--ec-font-size-body);
  font-weight: var(--ec-font-weight-medium);
  text-overflow: ellipsis;
  white-space: nowrap;
}

.ec-search__result-time {
  flex: none;
  color: var(--ec-text-tertiary);
  font-size: var(--ec-font-size-small);
}

.ec-search__result-sender {
  color: var(--ec-text-secondary);
  font-size: var(--ec-font-size-small);
}

.ec-search__result-snippet {
  display: -webkit-box;
  overflow: hidden;
  color: var(--ec-text-secondary);
  font-size: var(--ec-font-size-secondary);
  word-break: break-word;
  -webkit-box-orient: vertical;
  -webkit-line-clamp: 2;
}

.ec-search__hit {
  border-radius: 2px;
  background: var(--ec-warning-soft);
  color: var(--ec-text-primary);
  text-decoration: underline;
  text-underline-offset: 2px;
}

.ec-search__more {
  display: block;
  margin: var(--ec-space-3) auto 0;
  padding: var(--ec-space-2) var(--ec-space-4);
  border: 1px solid var(--ec-border);
  border-radius: var(--ec-radius-full);
  background: var(--ec-surface);
  color: var(--ec-brand-600);
  font-size: var(--ec-font-size-secondary);
  cursor: pointer;
}

.ec-search__more:disabled {
  color: var(--ec-text-tertiary);
  cursor: default;
}

.ec-search__more:focus-visible {
  outline: var(--ec-focus-width) solid var(--ec-focus-ring);
  outline-offset: var(--ec-focus-offset);
}

@media (max-width: 800px) {
  .ec-search-mask {
    padding: calc(6vh + env(safe-area-inset-top, 0px)) var(--ec-space-2)
      var(--ec-space-2);
  }

  .ec-search {
    width: 100%;
    max-height: calc(100% - 2vh);
  }

  .ec-search__close,
  .ec-search__result {
    min-height: 44px;
  }
}

@media (prefers-reduced-motion: reduce) {
  .ec-search__result {
    transition: none;
  }
}
.ec-search__filter-check {
  display: inline-flex;
  align-items: center;
  gap: var(--ec-space-1);
  color: var(--ec-text-secondary);
  font-size: 12px;
  white-space: nowrap;
  cursor: pointer;
}

.ec-search__filter-check input {
  accent-color: var(--ec-brand-600);
}
</style>
