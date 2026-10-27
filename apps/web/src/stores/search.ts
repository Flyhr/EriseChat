// 消息搜索 store（F-11 批次2，计划 §F-11）：全局/会话内两种范围共用一个面板与一份结果。
// 搜索走 REST（GET /api/messages/search），与 WS 消息链路无关；输入 250ms 防抖，
// 空关键词立即清空结果（不发请求）；打开面板时自动聚焦输入框由组件负责。
import { defineStore } from 'pinia'
import { computed, ref } from 'vue'
import { messagesApi } from '../api/messages'
import type { Message } from '../types/frames'
import { friendlyError } from '../core/errorMessages'

/** 输入防抖时长（与计划 §F-11 一致）。 */
export const SEARCH_DEBOUNCE_MS = 250

/** 每页条数（服务端允许 1~50，默认 20）。 */
export const SEARCH_PAGE_SIZE = 20

export const useSearchStore = defineStore('search', () => {
  const isOpen = ref(false)
  /** null = 全局搜索；否则为会话内搜索。 */
  const scopeConversationId = ref<string | null>(null)
  const query = ref('')
  /** 用于高亮的已提交关键词（与服务端实际检索的关键词一致）。 */
  const searchedQuery = ref('')
  const loading = ref(false)
  const loadingMore = ref(false)
  const error = ref('')
  const results = ref<Message[]>([])
  const hasMore = ref(false)

  const isGlobal = computed(() => scopeConversationId.value === null)

  let debounceTimer: number | null = null
  let requestSeq = 0

  function clearTimer(): void {
    if (debounceTimer !== null) {
      window.clearTimeout(debounceTimer)
      debounceTimer = null
    }
  }

  function resetResults(): void {
    requestSeq += 1
    clearTimer()
    results.value = []
    hasMore.value = false
    error.value = ''
    loading.value = false
    loadingMore.value = false
    searchedQuery.value = ''
  }

  function openGlobal(): void {
    scopeConversationId.value = null
    query.value = ''
    resetResults()
    isOpen.value = true
  }

  function openInConversation(conversationId: string): void {
    scopeConversationId.value = conversationId
    query.value = ''
    resetResults()
    isOpen.value = true
  }

  function close(): void {
    isOpen.value = false
    clearTimer()
    requestSeq += 1
  }

  /** 立刻按当前 query 发起搜索（loadMore 复用）。 */
  async function runSearch(keyword: string, beforeSeq?: number): Promise<void> {
    const seq = ++requestSeq
    if (beforeSeq === undefined) {
      loading.value = true
      results.value = []
      hasMore.value = false
    } else {
      loadingMore.value = true
    }
    error.value = ''

    try {
      const page = await messagesApi.search(keyword, {
        conversationId: scopeConversationId.value ?? undefined,
        beforeSeq,
        limit: SEARCH_PAGE_SIZE,
      })
      if (seq !== requestSeq) return

      if (beforeSeq === undefined) {
        results.value = page.messages
        searchedQuery.value = keyword
      } else {
        results.value = [...results.value, ...page.messages]
      }
      hasMore.value = page.hasMore
    } catch (cause) {
      if (seq === requestSeq) {
        error.value = friendlyError(cause)
        if (beforeSeq === undefined) {
          results.value = []
          hasMore.value = false
        }
      }
    } finally {
      if (seq === requestSeq) {
        loading.value = false
        loadingMore.value = false
      }
    }
  }

  /** 输入变化时调用：250ms 防抖；空关键词立即清空（不发请求）。 */
  function search(): void {
    clearTimer()
    const keyword = query.value.trim()
    if (!keyword) {
      requestSeq += 1
      results.value = []
      hasMore.value = false
      error.value = ''
      loading.value = false
      loadingMore.value = false
      searchedQuery.value = ''
      return
    }

    debounceTimer = window.setTimeout(() => {
      debounceTimer = null
      void runSearch(keyword)
    }, SEARCH_DEBOUNCE_MS)
  }

  /** 加载更早的命中（结果按 seq 降序，游标取最后一条的 seq）。 */
  async function loadMore(): Promise<void> {
    if (loading.value || loadingMore.value || !hasMore.value) return
    const keyword = searchedQuery.value
    if (!keyword) return
    const last = results.value[results.value.length - 1]
    if (!last) return
    await runSearch(keyword, last.seq)
  }

  return {
    isOpen,
    scopeConversationId,
    isGlobal,
    query,
    searchedQuery,
    loading,
    loadingMore,
    error,
    results,
    hasMore,
    openGlobal,
    openInConversation,
    close,
    search,
    loadMore,
  }
})
