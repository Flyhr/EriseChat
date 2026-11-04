// 消息搜索 store（F-11 批次2，计划 §F-11）：全局/会话内两种范围共用一个面板与一份结果。
// 搜索走 REST（GET /api/messages/search），与 WS 消息链路无关；输入 250ms 防抖，
// 空关键词立即清空结果（不发请求）；打开面板时自动聚焦输入框由组件负责。
// F-19（批次2）：会话内模式先同步过滤「已加载消息」得到本地即时结果（0 延迟、离线可用），
// 再并行走服务端搜索；服务端结果到达后作为权威列表，失败且有本地命中时降级为本地结果。
import { defineStore } from 'pinia'
import { computed, ref } from 'vue'
import { messagesApi } from '../api/messages'
import type { Message } from '../types/frames'
import { friendlyError } from '../core/errorMessages'
import { countLocalMatches, searchLocalMessages } from '../core/localSearch'
import { useMessagesStore } from './messages'

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
  /** F-34：全局搜索翻页游标（服务端返回，跨会话不重不漏）。 */
  const nextCursor = ref<{ createdAt: number; messageId: string } | null>(null)
  /** F-19：会话内本地已加载消息的即时命中（0 延迟展示；全局搜索恒为空）。 */
  const localResults = ref<Message[]>([])
  /** F-19：本地结果对应的已提交关键词（用于高亮，避免与输入中间态不一致）。 */
  const localQuery = ref('')
  /** F-19：本地命中总数（不受 level 截断影响，用于「本地 N 条」提示）。 */
  const localTotal = ref(0)
  /** F-19：服务端搜索失败（含离线）但本地有命中时置 true，UI 提示「仅显示本地已加载消息」。 */
  const offlineFallback = ref(false)

  const isGlobal = computed(() => scopeConversationId.value === null)

  let debounceTimer: number | null = null
  let requestSeq = 0

  function clearTimer(): void {
    if (debounceTimer !== null) {
      window.clearTimeout(debounceTimer)
      debounceTimer = null
    }
  }

  /** F-19：清空本地结果与降级标记。 */
  function clearLocalResults(): void {
    localResults.value = []
    localQuery.value = ''
    localTotal.value = 0
    offlineFallback.value = false
  }

  /**
   * F-19：仅会话内模式同步计算本地结果——直接读取 messages store 中该会话已加载消息，
   * 不做防抖、不发请求、不写 IndexedDB / 不改 facts。全局搜索不启用本地过滤。
   */
  function computeLocalResults(): void {
    const conversationId = scopeConversationId.value
    const keyword = query.value.trim()
    if (!conversationId || !keyword) {
      clearLocalResults()
      return
    }
    const loaded = useMessagesStore().messagesOf(conversationId)
    localResults.value = searchLocalMessages(loaded, keyword)
    localQuery.value = keyword
    localTotal.value = countLocalMatches(loaded, keyword)
    offlineFallback.value = false
  }

  function resetResults(): void {
    requestSeq += 1
    clearTimer()
    results.value = []
    hasMore.value = false
    nextCursor.value = null
    error.value = ''
    loading.value = false
    loadingMore.value = false
    searchedQuery.value = ''
    clearLocalResults()
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
    clearLocalResults()
  }

  /** 立刻按当前 query 发起搜索（loadMore 复用）；before 为翻页游标。 */
  async function runSearch(
    keyword: string,
    before?: { beforeSeq?: number; beforeCreatedAt?: number; beforeMessageId?: string },
  ): Promise<void> {
    const seq = ++requestSeq
    if (before === undefined) {
      loading.value = true
      results.value = []
      hasMore.value = false
      nextCursor.value = null
    } else {
      loadingMore.value = true
    }
    error.value = ''

    try {
      const page = await messagesApi.search(keyword, {
        conversationId: scopeConversationId.value ?? undefined,
        beforeSeq: before?.beforeSeq,
        beforeCreatedAt: before?.beforeCreatedAt,
        beforeMessageId: before?.beforeMessageId,
        limit: SEARCH_PAGE_SIZE,
      })
      if (seq !== requestSeq) return

      if (before === undefined) {
        results.value = page.messages
        searchedQuery.value = keyword
        offlineFallback.value = false
      } else {
        results.value = [...results.value, ...page.messages]
      }
      nextCursor.value = page.nextCursor ?? null
      hasMore.value = page.hasMore
    } catch (cause) {
      if (seq === requestSeq) {
        if (before === undefined && localResults.value.length > 0) {
          // F-19：服务端不可用（离线/失败）时保留本地即时结果并明确降级，不显示网络错误抢走结果。
          offlineFallback.value = true
          error.value = ''
          results.value = []
          hasMore.value = false
        } else {
          error.value = friendlyError(cause)
          if (before === undefined) {
            results.value = []
            hasMore.value = false
          }
        }
      }
    } finally {
      if (seq === requestSeq) {
        loading.value = false
        loadingMore.value = false
      }
    }
  }

  /** 输入变化时调用：会话内先同步出本地即时结果，再 250ms 防抖发服务端搜索；空关键词立即清空。 */
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
      clearLocalResults()
      return
    }

    // F-19：不防抖，立即用已加载消息渲染本地结果（全局搜索不启用）。
    computeLocalResults()

    debounceTimer = window.setTimeout(() => {
      debounceTimer = null
      void runSearch(keyword)
    }, SEARCH_DEBOUNCE_MS)
  }

  /**
   * 加载更早的命中：全局搜索用服务端 (createdAt, messageId) 游标（seq 只在单会话内可比，
   * 不能用最后一条的 seq 过滤所有会话）；会话内搜索保留原 seq 游标行为。
   */
  async function loadMore(): Promise<void> {
    if (loading.value || loadingMore.value || !hasMore.value) return
    const keyword = searchedQuery.value
    if (!keyword) return
    if (isGlobal.value && nextCursor.value) {
      const cursor = nextCursor.value
      await runSearch(keyword, {
        beforeCreatedAt: cursor.createdAt,
        beforeMessageId: cursor.messageId,
      })
      return
    }
    const last = results.value[results.value.length - 1]
    if (!last) return
    await runSearch(keyword, { beforeSeq: last.seq })
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
    nextCursor,
    localResults,
    localQuery,
    localTotal,
    offlineFallback,
    openGlobal,
    openInConversation,
    close,
    search,
    loadMore,
    computeLocalResults,
  }
})
