// 列表内联搜索（消息页 / 联系人页共用）：直接在搜索框内查「会话 / 联系人 / 聊天信息」，
// 不再打开全局 SearchDialog（Ctrl/Cmd+K 的全局面板仍保留，含发送人、日期与链接筛选）。
// - 会话命中在本地按会话名与成员昵称过滤（沿用 conversationTitle，不额外请求）；
// - 联系人命中来自 GET /api/users 的模糊搜索结果：已有好友关系（relation 不为空）或属于任一会话成员；
// - 陌生人（无关系且不是任何会话成员）只在「陌生人」分类里展示，默认分组不出现；
// - 聊天信息来自 GET /api/messages/search（跨会话，limit 20，按时间倒序）。
// 输入 250ms 防抖并行请求两类接口；按请求序号丢弃过期响应，卸载时清定时器。
import { computed, onBeforeUnmount, ref, watch } from 'vue'
import { messagesApi } from '../api/messages'
import { usersApi, type UserProfile } from '../api/users'
import type { Message } from '../types/frames'
import { useAuthStore } from '../stores/auth'
import { useConversationsStore } from '../stores/conversations'
import { conversationTitle } from './conversationTitle'
import { friendlyError } from './errorMessages'

/** 内联搜索分类：全部 / 联系人 / 聊天信息 / 陌生人。 */
export type InlineSearchCategory = 'all' | 'contacts' | 'messages' | 'strangers'

export const INLINE_SEARCH_DEBOUNCE_MS = 250
export const INLINE_SEARCH_MESSAGE_LIMIT = 20

export function useInlineSearch() {
  const auth = useAuthStore()
  const conversations = useConversationsStore()

  const query = ref('')
  const category = ref<InlineSearchCategory>('all')
  const users = ref<UserProfile[]>([])
  const messages = ref<Message[]>([])
  const loading = ref(false)
  const error = ref('')

  let timer: ReturnType<typeof setTimeout> | null = null
  let requestSeq = 0

  const selfUserId = computed(() => auth.user?.userId ?? '')
  const keyword = computed(() => query.value.trim())
  const hasKeyword = computed(() => keyword.value.length > 0)

  /** 当前用户所在全部会话的成员 ID（用于把搜索结果分到「联系人」或「陌生人」）。 */
  const memberIds = computed(() => {
    const ids = new Set<string>()
    for (const conversation of conversations.sorted) {
      for (const member of conversation.members) ids.add(member.userId)
    }
    return ids
  })

  const conversationMatches = computed(() => {
    const text = keyword.value.toLowerCase()
    if (!text) return []
    return conversations.sorted.filter((conversation) => {
      if (conversationTitle(conversation, selfUserId.value).toLowerCase().includes(text)) return true
      return conversation.members.some((member) => member.displayName.toLowerCase().includes(text))
    })
  })

  const contactMatches = computed(() =>
    users.value.filter(
      (user) =>
        user.userId !== selfUserId.value &&
        (user.relation !== undefined || memberIds.value.has(user.userId)),
    ),
  )

  const strangerMatches = computed(() =>
    users.value.filter(
      (user) =>
        user.userId !== selfUserId.value &&
        user.relation === undefined &&
        !memberIds.value.has(user.userId),
    ),
  )

  const messageMatches = computed(() => messages.value)

  function resetResults(): void {
    users.value = []
    messages.value = []
    loading.value = false
    error.value = ''
  }

  /** 清空关键词、分类与结果（Esc 或点「清空」时恢复原列表）。 */
  function clear(): void {
    if (timer) {
      clearTimeout(timer)
      timer = null
    }
    requestSeq += 1
    query.value = ''
    category.value = 'all'
    resetResults()
  }

  function setCategory(next: InlineSearchCategory): void {
    category.value = next
  }

  /** 加好友/自动成为好友后，把最新关系写回当前结果，避免行内按钮停留在旧状态。 */
  function updateUserRelation(userId: string, relation: 'friend' | 'outgoing'): void {
    users.value = users.value.map((user) =>
      user.userId === userId ? { ...user, relation } : user,
    )
  }

  async function run(text: string): Promise<void> {
    const seq = ++requestSeq
    loading.value = true
    error.value = ''
    try {
      const [userPage, messagePage] = await Promise.all([
        usersApi.search(text),
        messagesApi.search(text, { limit: INLINE_SEARCH_MESSAGE_LIMIT, timeCursor: true }),
      ])
      if (seq !== requestSeq) return
      users.value = userPage.users
      messages.value = messagePage.messages
    } catch (cause) {
      if (seq !== requestSeq) return
      users.value = []
      messages.value = []
      error.value = friendlyError(cause)
    } finally {
      if (seq === requestSeq) loading.value = false
    }
  }

  watch(query, () => {
    if (timer) {
      clearTimeout(timer)
      timer = null
    }
    const text = keyword.value
    if (!text) {
      requestSeq += 1
      resetResults()
      return
    }
    // 防抖窗口内先显示加载态，避免旧结果与新关键词错配。
    loading.value = true
    timer = setTimeout(() => {
      timer = null
      void run(text)
    }, INLINE_SEARCH_DEBOUNCE_MS)
  })

  onBeforeUnmount(() => {
    if (timer) {
      clearTimeout(timer)
      timer = null
    }
    requestSeq += 1
  })

  return {
    query,
    category,
    keyword,
    hasKeyword,
    selfUserId,
    conversationMatches,
    contactMatches,
    strangerMatches,
    messageMatches,
    loading,
    error,
    clear,
    setCategory,
    updateUserRelation,
  }
}
