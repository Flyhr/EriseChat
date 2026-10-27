<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, ref, watch } from 'vue'
import { useRouter } from 'vue-router'
import { conversationLabel } from '../core/groupChat'
import { highlightSegments, snippetOf } from '../core/searchHighlight'
import { useAuthStore } from '../stores/auth'
import { useConversationsStore } from '../stores/conversations'
import { useFriendsStore } from '../stores/friends'
import { useSearchStore } from '../stores/search'
import type { Message } from '../types/frames'

// 消息搜索面板（F-11 批次2，计划 §F-11）：全局/会话内共用，App.vue 挂载一次。
// 只展示 REST 返回的 Message（text 消息），点击结果跳转到对应会话并定位（jumpSeq，复用 F-09 机制）。
// 渲染一律文本节点 + 高亮 span，禁止 v-html。

const router = useRouter()
const auth = useAuthStore()
const conversations = useConversationsStore()
const friends = useFriendsStore()
const search = useSearchStore()

const inputRef = ref<HTMLInputElement | null>(null)
const panelRef = ref<HTMLElement | null>(null)
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

const emptyText = computed(() => {
  if (search.loading) return '搜索中…'
  if (search.error) return search.error
  if (!search.query.trim()) return '输入关键词搜索历史消息'
  return '没有找到匹配的消息'
})

/** 会话名：群聊用 conversationLabel，单聊取对方昵称，兜底会话 ID 前 8 位。 */
function conversationName(conversationId: string): string {
  const conversation = conversations.getById(conversationId)
  if (!conversation) return conversationId.slice(0, 8)
  if (conversation.type === 'group') return conversationLabel(conversation)
  const other = conversation.members.find((member) => member.userId !== auth.user?.userId)
  return other?.displayName || conversation.title || conversationId.slice(0, 8)
}

/** 发送人：优先会话成员昵称，其次好友列表，最后兜底文案。 */
function senderNameOf(message: Message): string {
  const conversation = conversations.getById(message.conversationId)
  const member = conversation?.members.find((item) => item.userId === message.senderId)
  if (member?.displayName) return member.displayName
  return friends.displayNameOf(message.senderId) || '未知用户'
}

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

/** 摘要：以命中为中心截取，再按关键词切高亮片段。 */
function resultSnippet(message: Message): string {
  const content = message.content
  return typeof content === 'string' ? content : ''
}

function segmentsOf(message: Message) {
  const snippet = snippetOf(resultSnippet(message), search.searchedQuery)
  return highlightSegments(snippet, search.searchedQuery)
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
    query: { jumpSeq: String(message.seq) },
  })
}

function moveSelection(delta: number): void {
  if (search.results.length === 0) return
  const next = (activeIndex.value + delta + search.results.length) % search.results.length
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
    const message = search.results[activeIndex.value]
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
  () => search.results.length,
  () => {
    activeIndex.value = 0
  },
)

watch(
  () => search.isOpen,
  async (open) => {
    if (open) {
      lastFocused = document.activeElement instanceof HTMLElement ? document.activeElement : null
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

        <div class="ec-search__body">
          <p v-if="emptyText && !search.loading && search.results.length === 0" class="ec-search__hint">
            {{ emptyText }}
          </p>

          <ul v-else-if="search.results.length > 0" class="ec-search__results" role="listbox" :aria-label="'搜索结果'">
            <li v-for="(message, index) in search.results" :key="`${message.conversationId}-${message.messageId}`">
              <button
                type="button"
                class="ec-search__result"
                :class="{ 'ec-search__result--active': index === activeIndex }"
                role="option"
                :aria-selected="index === activeIndex"
                @click="selectResult(message)"
                @mouseenter="activeIndex = index"
              >
                <span class="ec-search__result-top">
                  <span class="ec-search__result-conversation">{{ conversationName(message.conversationId) }}</span>
                  <span class="ec-search__result-time">{{ formatTime(message.createdAt) }}</span>
                </span>
                <span class="ec-search__result-sender">{{ senderNameOf(message) }}</span>
                <span class="ec-search__result-snippet">
                  <template v-for="(segment, segmentIndex) in segmentsOf(message)" :key="segmentIndex">
                    <mark v-if="segment.hit" class="ec-search__hit">{{ segment.text }}</mark>
                    <template v-else>{{ segment.text }}</template>
                  </template>
                </span>
              </button>
            </li>
          </ul>

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
  background: var(--ec-warning-soft, #fff1d6);
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
</style>
