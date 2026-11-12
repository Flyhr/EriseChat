<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import AppDialog from '../components/AppDialog.vue'
import AppIcon from '../components/AppIcon.vue'
import ChatHeader from '../components/ChatHeader.vue'
import ConversationList from '../components/ConversationList.vue'
import EmojiPanel from '../components/EmojiPanel.vue'
import ForwardDialog from '../components/ForwardDialog.vue'
import EmptyState from '../components/EmptyState.vue'
import GroupMembersPanel from '../components/GroupMembersPanel.vue'
import ConversationFilesPanel from '../components/ConversationFilesPanel.vue'
import ImagePreview from '../components/ImagePreview.vue'
import MessageActionMenu from '../components/MessageActionMenu.vue'
import MessageList from '../components/MessageList.vue'
import NoticeBar from '../components/NoticeBar.vue'
import SendBox from '../components/SendBox.vue'
import { conversationsApi } from '../api/conversations'
import { filesApi } from '../api/files'
import { isImageFile, validateAttachment } from '../core/attachments'
import { createUploadItem, type UploadItem } from '../core/uploadQueue'
import type { FileContent } from '../types/frames'
import type { ConversationFilter } from '../core/chatLayout'
import { getOrLoadBlobUrl } from '../core/blobCache'
import { conversationTitle, otherMemberOf } from '../core/conversationTitle'
import { MAX_TEXT_LENGTH } from '../core/config'
import { MAX_SEND_FILES, filesOf, hasFiles, normalizeDroppedFile, truncateFiles } from '../core/fileDrop'
import { friendlyError } from '../core/errorMessages'
import { ApiError } from '../core/http'
import { compressImageFile } from '../core/imageCompress'
import { imageItemsOf } from '../core/imageGallery'
import type { MentionCandidateMember } from '../core/mentions'
import type { MessageActionId } from '../core/messageActions'
import { createPanelHistoryGuard } from '../core/historyGuard'
import { shouldAdvanceRead } from '../core/readReceipt'
import { showToast } from '../core/toast'
import { TypingThrottle } from '../core/typingThrottle'
import type { ReplyPreviewLike } from '../core/replyPreview'
import { chatWs, type WsState } from '../core/ws'
import { useAuthStore } from '../stores/auth'
import { useConversationsStore } from '../stores/conversations'
import { useMessagesStore } from '../stores/messages'
import { usePresenceStore } from '../stores/presence'
import { useTypingStore } from '../stores/typing'
import { useDraftsStore } from '../stores/drafts'
import { useFavoritesStore } from '../stores/favorites'
import { useSearchStore } from '../stores/search'
import type { Conversation, Message } from '../types/frames'

// 聊天主页面（M2-04 会话列表 + M2-05 消息流 + M3-08 群聊）：左侧会话列表，右侧消息流与发送框。
// 规则：页面只做编排，状态走 stores/，连接走 core/ws.ts，不直接 new WebSocket / fetch。
const route = useRoute()
const router = useRouter()
const auth = useAuthStore()
const conversations = useConversationsStore()
const messages = useMessagesStore()
const typing = useTypingStore()
const presence = usePresenceStore()
const drafts = useDraftsStore()
const favorites = useFavoritesStore()
/** typing 帧的客户端节流（同一会话 3 秒最多一次，protocol.md §4.7）。 */
const typingThrottle = new TypingThrottle()

const selfUserId = computed(() => auth.user?.userId ?? '')
const activeId = computed(() =>
  typeof route.params.conversationId === 'string' ? route.params.conversationId : null,
)
// 告知 conversations store 当前打开的会话：messages store 的提示音/系统通知据此判断是否正在查看。
// 切换会话前先 flush 上一个会话的草稿，避免 300ms 防抖窗口内的内容丢失（F-04）。
watch(
  activeId,
  (conversationId, previousId) => {
    if (previousId && previousId !== conversationId) drafts.flush(previousId)
    conversations.setActiveConversation(conversationId)
  },
  { immediate: true },
)
const activeConversation = computed(() =>
  activeId.value ? conversations.getById(activeId.value) : undefined,
)
const isGroup = computed(() => activeConversation.value?.type === 'group')
/** F-25：群聊 @ 成员候选（单聊为空 = 不启用候选面板）；角色用于候选行小字。 */
const mentionCandidates = computed<MentionCandidateMember[]>(() => {
  const conversation = activeConversation.value
  if (!conversation || conversation.type !== 'group') return []
  return conversation.members
    .filter((member) => member.userId !== selfUserId.value)
    .map((member) => ({ userId: member.userId, displayName: member.displayName, role: member.role }))
})
/** F-25：气泡内 @ 昵称高亮用的成员昵称表（单聊只有双方，但单聊不触发 @ 选择）。 */
const mentionNames = computed(() => activeConversation.value?.members.map((member) => member.displayName) ?? [])
/** F-25：当前用户昵称，用于「提到你」标记（与消息里书写时的昵称一致）。 */
const selfDisplayName = computed(() => auth.user?.displayName ?? '')
const memberCount = computed(() => activeConversation.value?.members.length ?? 0)

/** F-36：仅群主可设置/取消置顶（服务端仍是最终判定）。 */
const canManagePinned = computed(
  () =>
    isGroup.value &&
    activeConversation.value?.members.some(
      (member) => member.userId === selfUserId.value && member.role === 'owner',
    ) === true,
)

/** F-36：当前会话的置顶消息（服务端对已撤回的置顶消息置空，不再下发）。 */
const pinnedMessage = computed(() => activeConversation.value?.pinnedMessage ?? null)

/** F-36：置顶条摘要——发送人 + 内容前 60 字（文件/语音用占位文案）。 */
const pinnedText = computed(() => {
  const pinned = pinnedMessage.value
  if (!pinned) return ''
  const content = pinned.content
  let text = ''
  if (typeof content === 'string') text = content
  else if ('name' in content) text = `[文件] ${content.name}`
  else if ('durationMs' in content) text = '[语音]'
  return `${senderNameOf(pinned)}：${text.slice(0, 60)}`
})
const activeTitle = computed(() =>
  activeConversation.value
    ? conversationTitle(activeConversation.value, selfUserId.value, {
        groupStyle: 'plain',
        fallback: '单聊会话',
      })
    : '',
)

// 页内提示（M3-08：被移出/退出群聊等）；不引第三方 toast。
const pageNotice = ref('')
// 右侧面板（UI-02，计划 §5.6）：同一时刻最多一个；emoji 由输入框表情按钮触发，aside 由群聊「群资料」或顶栏「文件」按钮触发。
const activeDrawer = ref<'emoji' | 'group' | null>(null)
/** aside 当前页签（F-15）：群聊可在「群资料与成员」「文件」间切换；单聊仅有「文件」。 */
const asideTab = ref<'members' | 'files'>('members')
/** 面板打开时压入 history 哨兵：Android 返回/浏览器后退先关面板，不直接退出会话（UI-04）。 */
const panelGuard = createPanelHistoryGuard(() => {
  activeDrawer.value = null
})
/** 右侧 aside（群资料与成员 / 共享文件）：与聊天窗口同层的 grid 列，打开时聊天区收窄。 */
const asideOpen = computed(() => Boolean(activeId.value) && activeDrawer.value === 'group')
/** 发送框实例：Emoji 面板选择后调用其 insertEmoji，让插入落在当前光标处（草稿不丢）。 */
const sendBoxRef = ref<{ insertEmoji: (emoji: string) => void; focusInput: () => void } | null>(null)

// 连接状态页内提示（UI-02 批次4）：断线/重连就地提示，恢复后短暂显示 3 秒；
// 只读取 core/ws.ts 的真实状态，不替代全局 ConnectionBanner，也不伪造连接状态。
const wsState = ref<WsState>(chatWs.state)
const recovered = ref(false)
let recoveredTimer: ReturnType<typeof setTimeout> | null = null
let offWsState: (() => void) | null = null

const connectionNotice = computed<{ text: string; tone: 'warning' | 'success' } | null>(() => {
  if (wsState.value === 'closed') return { text: '网络已断开，消息可能延迟', tone: 'warning' }
  if (wsState.value === 'reconnecting') return { text: '正在重新连接…', tone: 'warning' }
  if (recovered.value) return { text: '已恢复连接', tone: 'success' }
  return null
})

function onWsStateChange(next: WsState): void {
  const previous = wsState.value
  wsState.value = next
  if (recoveredTimer) {
    clearTimeout(recoveredTimer)
    recoveredTimer = null
  }
  // 仅「断线/重连 → 已认证」才算恢复；首次登录 connecting→authed 不提示。
  if (next === 'authed' && (previous === 'reconnecting' || previous === 'closed')) {
    recovered.value = true
    recoveredTimer = setTimeout(() => {
      recovered.value = false
      recoveredTimer = null
    }, 3000)
  }
  // F-38 前置修复：断线重连成功后刷新历史定位窗口（保持查看位置）。
  if (next === 'authed' && previous !== 'authed' && locateMode.value) {
    void refreshLocateWindow()
  }
  if (next !== 'authed') recovered.value = false
}

// 会话列表筛选（UI-02）：全部/未读/群聊。群聊由左导航「群聊」(?group=all) 驱动；
// 未读为本地筛选状态，不写入 URL；切换筛选只改展示，不产生任何请求。
const listFilter = ref<ConversationFilter>(route.query.group === 'all' ? 'group' : 'all')
watch(
  () => route.query.group,
  (group) => {
    if (group === 'all') listFilter.value = 'group'
    else if (listFilter.value === 'group') listFilter.value = 'all'
  },
)

function changeListFilter(filter: ConversationFilter): void {
  listFilter.value = filter
  const query = { ...route.query }
  if (filter === 'group') query.group = 'all'
  else delete query.group
  void router.replace({ query })
}

function openContacts(): void {
  void router.push('/contacts')
}

const search = useSearchStore()

/** F-11：聊天顶栏搜索为当前会话内搜索（列表搜索框已在列表内联展示结果）。 */
function openConversationSearch(): void {
  if (activeId.value) search.openInConversation(activeId.value)
}

// 当前会话的消息缓存（已落库事实消息 + 乐观发送中的本地消息）与历史分页状态。
const activeMessages = computed(() =>
  activeId.value ? messages.messagesOf(activeId.value) : [],
)
const activePending = computed(() =>
  activeId.value ? messages.pendingOf(activeId.value) : [],
)
const activeHasMore = computed(() => (activeId.value ? messages.hasMoreOf(activeId.value) : false))
const activeLoadingHistory = computed(() =>
  activeId.value ? messages.isLoadingMore(activeId.value) : false,
)
const activeLoaded = computed(() => (activeId.value ? messages.isLoaded(activeId.value) : true))

// U-09：首屏加载超过 1 秒才补一行文字，避免快速加载时提示闪烁；骨架本身立即显示。
const slowLoading = ref(false)
let slowLoadingTimer: number | null = null
watch(
  activeLoaded,
  (loaded) => {
    if (slowLoadingTimer !== null) {
      window.clearTimeout(slowLoadingTimer)
      slowLoadingTimer = null
    }
    slowLoading.value = false
    if (!loaded) {
      slowLoadingTimer = window.setTimeout(() => {
        slowLoading.value = true
        slowLoadingTimer = null
      }, 1000)
    }
  },
  { immediate: true },
)

// F-37：定位窗口与正常时间线分离。置顶/引用/搜索跳转只展示目标附近的上下文窗口，
// 不合并进会话缓存、不推进 lastSeq / 同步水位 / 历史分页起点；跳转后提供「回到最新」。
const locateWindow = ref<{ messages: Message[]; hasOlder: boolean; hasNewer: boolean } | null>(null)
/** 当前定位窗口的锚点（重连后按同一目标刷新窗口，保持用户查看的历史位置）。 */
const locateAnchor = ref<{ conversationId: string; messageId?: string; seq?: number } | null>(null)
/** 定位窗口代数：每次打开/替换/关闭窗口 +1；迟到的刷新响应据此校验是否仍属同一个窗口。 */
const locateWindowVersion = ref(0)
const locateLoading = ref(false)
/** F-37：定位请求进行中（从发起定位到上下文接口返回），期间同样不能上报已读。 */
const locateRequestingCount = ref(0)
const locateRequesting = computed(() => locateRequestingCount.value > 0)
const locateMode = computed(() => locateWindow.value !== null)
const displayMessages = computed(() => locateWindow.value?.messages ?? activeMessages.value)
const displayPending = computed(() => (locateMode.value ? [] : activePending.value))
const displayHasMore = computed(() => (locateMode.value ? false : activeHasMore.value))

function exitLocateWindow(): void {
  locateWindowVersion.value += 1
  locateWindow.value = null
  locateAnchor.value = null
  locateLoading.value = false
}

/** 回到最新：退出定位窗口并滚回正常时间线底部（定位期间未改动分页与同步水位）。 */
function backToLatest(): void {
  if (!locateMode.value) return
  exitLocateWindow()
  atBottom.value = true
  void nextTick(() => {
    messageListRef.value?.scrollToBottom()
    markActiveConversationRead()
  })
}

// 回复引用（F-09）：replyTarget 记录当前回复目标与本地引用条摘要，发送成功或取消后清空；
// 点击引用条先在本窗口定位，未加载时循环补拉历史（≤8 页）。
const replyTarget = ref<{ messageId: string; preview: ReplyPreviewLike } | null>(null)
const messageListRef = ref<{
  scrollToMessage: (messageId: string) => boolean
  scrollToBottom: () => void
  getScrollTop?: () => number
  setScrollTop?: (value: number) => void
} | null>(null)
const JUMP_CONTEXT_BEFORE = 25
const JUMP_CONTEXT_AFTER = 25

function replyPreviewOf(message: Message): ReplyPreviewLike {
  const content = message.content
  if (typeof content === 'string') return { msgType: 'text', text: content }
  if ('durationMs' in content) return { msgType: 'voice' }
  if ('fileId' in content) {
    return { msgType: message.msgType === 'image' ? 'image' : 'file', name: content.name }
  }
  // system 消息不提供回复入口，这里仅做类型兜底（按文本摘要展示）。
  return { msgType: 'text', text: content.text ?? '' }
}

function onReplyMessage(messageId: string): void {
  // F-37：从当前展示的数据取目标（历史定位窗口中的旧消息也可回复）。
  const message = displayMessages.value.find((item) => item.messageId === messageId)
  if (!message) return
  replyTarget.value = { messageId, preview: replyPreviewOf(message) }
  void nextTick(() => sendBoxRef.value?.focusInput())
}

function cancelReply(): void {
  replyTarget.value = null
}

// F-18：消息操作菜单（回复/撤回/编辑）。撤回需二次确认；编辑用 wide AppDialog 表单。
const actionMenu = ref<{ messageId: string; x: number; y: number } | null>(null)
const actionMenuMessage = computed<Message | null>(() => {
  const target = actionMenu.value
  if (!target) return null
  // F-37：从当前展示的数据取目标（历史定位窗口中的旧消息也可操作）。
  return displayMessages.value.find((message) => message.messageId === target.messageId) ?? null
})
const actionMenuSenderName = computed(() => {
  const message = actionMenuMessage.value
  if (!message) return ''
  return activeConversation.value?.members.find((member) => member.userId === message.senderId)?.displayName ?? ''
})

function onOpenActions(messageId: string, position: { x: number; y: number }): void {
  if (!activeId.value) return
  actionMenu.value = { messageId, x: position.x, y: position.y }
}

/** F-26：resolve 消息发送者昵称（收藏快照用；找不到时回退「我 / 未知成员」）。 */
function senderNameOf(message: Message): string {
  const member = activeConversation.value?.members.find((item) => item.userId === message.senderId)
  if (member) return member.displayName
  return message.senderId === selfUserId.value ? '我' : '未知成员'
}

/** F-26/F-40：转发弹窗要转发的消息；关闭弹窗后仅在「部分失败待重试」时保留。 */
const forwardMessages = ref<Message[]>([])
/** F-40：弹窗显隐；与 forwardMessages 分开，关闭后仍可保留同一批的幂等键与失败项。 */
const forwardOpen = ref(false)
/** F-40：开始新一批时递增，驱动弹窗重新生成 clientMsgId 并解锁目标。 */
const forwardBatchId = ref(0)

/** F-40：多选状态：选择模式、已选消息 ID（按展示顺序过滤后即为转发顺序）。 */
const MAX_FORWARD_MESSAGES = 20
const selecting = ref(false)
const selectedMessageIds = ref<string[]>([])
const selectedMessages = computed(() =>
  displayMessages.value.filter((message) => selectedMessageIds.value.includes(message.messageId)),
)
const canForwardSelection = computed(() => selectedMessages.value.length > 0)

/** 只有已落库、非 system、未撤回的消息可选（待发送消息在 pending 列表，不在 displayMessages）。 */
function isSelectableMessage(message: Message): boolean {
  return message.msgType !== 'system' && typeof message.deletedAt !== 'number' && Boolean(message.messageId)
}

function startSelecting(messageId: string): void {
  const message = displayMessages.value.find((item) => item.messageId === messageId)
  if (!message || !isSelectableMessage(message)) {
    showToast('该消息不能选择', 'error')
    return
  }
  if (activeDrawer.value === 'emoji') activeDrawer.value = null
  // 重新开始选择即放弃上一批的失败项与幂等键。
  forwardOpen.value = false
  forwardMessages.value = []
  selecting.value = true
  selectedMessageIds.value = [messageId]
}

function toggleSelected(messageId: string): void {
  const message = displayMessages.value.find((item) => item.messageId === messageId)
  if (!message || !isSelectableMessage(message)) return
  // 选择变化后旧批次的目标/结果不再适用，丢弃后按新选择重新开始一批。
  if (forwardMessages.value.length > 0) {
    forwardOpen.value = false
    forwardMessages.value = []
  }
  if (selectedMessageIds.value.includes(messageId)) {
    selectedMessageIds.value = selectedMessageIds.value.filter((id) => id !== messageId)
    if (selectedMessageIds.value.length === 0) selecting.value = false
    return
  }
  if (selectedMessageIds.value.length >= MAX_FORWARD_MESSAGES) {
    showToast(`最多选择 ${MAX_FORWARD_MESSAGES} 条`, 'error')
    return
  }
  selectedMessageIds.value = [...selectedMessageIds.value, messageId]
}

function exitSelecting(): void {
  selecting.value = false
  selectedMessageIds.value = []
  forwardOpen.value = false
  forwardMessages.value = []
}

/** 多选转发：按原消息在会话中的顺序逐条转发（顺序由 selectedMessages 的计算保证）。 */
function openMultiForward(): void {
  if (selectedMessages.value.length === 0) return
  // 部分失败后关闭再点「转发」：恢复同一批（保留目标、clientMsgId 与失败项），不重新生成幂等键。
  const sameBatch =
    forwardMessages.value.length > 0 &&
    forwardMessages.value.length === selectedMessages.value.length &&
    forwardMessages.value.every((message, index) => message.messageId === selectedMessages.value[index]?.messageId)
  if (!sameBatch) {
    forwardMessages.value = [...selectedMessages.value]
    forwardBatchId.value += 1
  }
  forwardOpen.value = true
}

function onForwarded(result: { success: number; failed: number }): void {
  // 全部成功才退出多选；部分失败保留选择与失败项，用户可只重试失败消息。
  if (result.failed === 0) {
    forwardOpen.value = false
    forwardMessages.value = []
    exitSelecting()
  }
}

/** 关闭转发弹窗：部分失败时保留本批，便于重新打开只重试失败项。 */
function onForwardClose(keepBatch: boolean): void {
  forwardOpen.value = false
  if (!keepBatch) forwardMessages.value = []
}

/** F-38：收藏/取消收藏（服务端为事实源）；失败用明确错误提示，不显示成功。 */
async function toggleFavorite(message: Message): Promise<void> {
  if (!selfUserId.value) {
    showToast('请先登录后再收藏', 'error')
    return
  }
  try {
    const result = await favorites.toggle(message.messageId)
    if (result === 'stale') {
      showToast('登录状态或服务器已切换，请重试', 'error')
      return
    }
    showToast(result === 'added' ? '已收藏' : '已取消收藏')
  } catch (error) {
    showToast(friendlyError(error), 'error')
  }
}

/** F-36：设置/替换会话置顶消息（仅群主；其他成员通过 conv.updated 刷新）。 */
async function setPinned(message: Message): Promise<void> {
  const conversationId = activeId.value
  if (!conversationId) return
  try {
    const updated = await conversationsApi.setPinnedMessage(conversationId, message.messageId)
    conversations.upsert([updated])
    showToast('已置顶')
  } catch (cause) {
    showToast(friendlyError(cause), 'error')
  }
}

/** F-36：取消会话置顶消息（仅群主）。 */
async function clearPinnedMessage(): Promise<void> {
  const conversationId = activeId.value
  if (!conversationId) return
  try {
    const updated = await conversationsApi.clearPinnedMessage(conversationId)
    conversations.upsert([updated])
    showToast('已取消置顶')
  } catch (cause) {
    showToast(friendlyError(cause), 'error')
  }
}

function onActionSelect(action: MessageActionId): void {
  const target = actionMenu.value
  const message = actionMenuMessage.value
  actionMenu.value = null
  if (!target || !message) return
  if (action === 'reply') {
    onReplyMessage(target.messageId)
    return
  }
  if (action === 'recall') {
    recallTarget.value = message
    return
  }
  if (action === 'edit') {
    openEditDialog(message)
    return
  }
  if (action === 'forward') {
    forwardMessages.value = [message]
    forwardBatchId.value += 1
    forwardOpen.value = true
    return
  }
  if (action === 'select') {
    startSelecting(message.messageId)
    return
  }
  if (action === 'favorite' || action === 'unfavorite') {
    void toggleFavorite(message)
  }
  if (action === 'pin') {
    void setPinned(message)
    return
  }
  if (action === 'unpin') {
    void clearPinnedMessage()
  }
}

const recallTarget = ref<Message | null>(null)
const recallSubmitting = ref(false)

async function confirmRecall(): Promise<void> {
  const conversationId = activeId.value
  const target = recallTarget.value
  if (!conversationId || !target) return
  recallSubmitting.value = true
  const sent = messages.sendRecall(conversationId, target.messageId)
  recallSubmitting.value = false
  recallTarget.value = null
  if (!sent) showToast('撤回请求发送失败，请检查网络后重试', 'error')
}

const editTarget = ref<Message | null>(null)
const editContent = ref('')
const editError = ref('')
const editSubmitting = ref(false)

function openEditDialog(message: Message): void {
  editTarget.value = message
  editContent.value = typeof message.content === 'string' ? message.content : ''
  editError.value = ''
}

function closeEditDialog(): void {
  if (editSubmitting.value) return
  editTarget.value = null
  editError.value = ''
}

function confirmEdit(): void {
  const conversationId = activeId.value
  const target = editTarget.value
  if (!conversationId || !target) return
  const content = editContent.value.trim()
  if (content.length === 0) {
    editError.value = '编辑内容不能为空'
    return
  }
  if (content.length > MAX_TEXT_LENGTH) {
    editError.value = `文本内容不能超过 ${MAX_TEXT_LENGTH} 字符`
    return
  }
  editSubmitting.value = true
  const sent = messages.sendEdit(conversationId, target.messageId, content)
  editSubmitting.value = false
  if (!sent) {
    editError.value = '编辑请求发送失败，请检查网络后重试'
    return
  }
  editTarget.value = null
}

// 切换会话/退出登录时关闭残留菜单与弹窗，避免操作到旧会话的消息。
// 进入会话时清除「标为未读」个人标记：等该会话资料可用后只处理一次，只清标记、不推进已读游标。
// 停留在当前会话期间主动标为未读、或收到同账号其他设备的标记，不在这里清除（留待下次进入）。
const pendingEnteredMarkClear = ref<string | null>(null)

function tryClearEnteredMark(): void {
  const conversationId = activeId.value
  const enteringId = pendingEnteredMarkClear.value
  if (!conversationId || enteringId !== conversationId) return
  const conversation = conversations.getById(conversationId)
  // 缓存快照不算服务端事实：等该会话收到首个服务端结果（会话列表或 conv.updated）后再决定是否清除，
  // 避免冷启动时用 IndexedDB 旧快照误判「未标记」而提前消费本次进入。
  if (!conversation || !conversations.isServerSynced(conversationId)) return
  pendingEnteredMarkClear.value = null
  if (conversation.markedUnread) void conversations.clearMarkedUnread(conversationId)
}

watch(
  activeId,
  (conversationId) => {
    actionMenu.value = null
    recallTarget.value = null
    editTarget.value = null
    forwardOpen.value = false
    forwardMessages.value = []
    exitSelecting()
    pendingEnteredMarkClear.value = conversationId
    tryClearEnteredMark()
  },
  { immediate: true },
)

// 会话资料异步到达（冷启动直达、重新连接刷新）时再尝试一次；已处理过则 enteringId 为 null，不再清除。
// 依赖里同时带上「是否已收到服务端数据」：缓存快照未标记时不会提前消费本次进入。
watch(
  () => {
    const conversationId = activeId.value
    if (!conversationId) return ''
    const serverSynced = conversations.isServerSynced(conversationId) ? '1' : '0'
    const marked = conversations.getById(conversationId)?.markedUnread ? '1' : '0'
    return `${serverSynced}:${marked}`
  },
  () => tryClearEnteredMark(),
)

/** 在已加载消息中定位；不在缓存时按目标一次性加载上下文窗口（不扫最新页、不改正常时间线分页）。 */
async function ensureMessageVisible(
  conversationId: string,
  target: { messageId?: string; seq?: number },
  options: {
    scroll?: boolean
    forceContext?: boolean
    /** 写回窗口前校验：调用方身份/窗口是否仍有效（迟到的重连刷新不得重开旧窗口）。 */
    guard?: () => boolean
  } = {},
): Promise<boolean> {
  const findLoadedId = (): string | undefined =>
    target.messageId ??
    activeMessages.value.find((message) => message.seq === target.seq)?.messageId

  // 重连刷新必须走服务端上下文接口，不能因本地仍有旧快照而跳过。
  const existingId = options.forceContext ? undefined : findLoadedId()
  if (existingId && messageListRef.value?.scrollToMessage(existingId)) return true

  // 需要请求上下文窗口时才算「定位请求进行中」；期间所有已读上报都要暂停。
  locateRequestingCount.value += 1
  locateLoading.value = true
  try {
    // F-37：按目标取前后窗口，只用于定位展示；不合并进会话缓存、不推进同步水位或历史分页。
    const page = await messages.loadContextAround(conversationId, {
      messageId: target.messageId,
      seq: target.messageId ? undefined : target.seq,
      before: JUMP_CONTEXT_BEFORE,
      after: JUMP_CONTEXT_AFTER,
    })
    await nextTick()
    if (activeId.value !== conversationId) return false
    if (options.guard && !options.guard()) return false
    const targetId =
      target.messageId ??
      page.messages.find((message) => message.seq === target.seq)?.messageId
    if (!targetId || !page.messages.some((message) => message.messageId === targetId)) {
      return false
    }
    locateWindowVersion.value += 1
    locateWindow.value = {
      messages: page.messages,
      hasOlder: page.hasOlder,
      hasNewer: page.hasNewer,
    }
    locateAnchor.value = target.messageId
      ? { conversationId, messageId: target.messageId }
      : { conversationId, seq: target.seq }
    await nextTick()
    if (options.scroll === false) return true
    return messageListRef.value?.scrollToMessage(targetId) ?? false
  } catch {
    return false
  } finally {
    locateLoading.value = false
    locateRequestingCount.value = Math.max(0, locateRequestingCount.value - 1)
  }
}

/**
 * F-38 前置修复：重连成功后按锚点重新拉取定位窗口，让断线期间的撤回/编辑反映到窗口内。
 * 保持用户查看位置（恢复滚动偏移），窗口数据仍只用于展示，不并入缓存/不动游标。
 */
async function refreshLocateWindow(): Promise<void> {
  const conversationId = activeId.value
  const anchor = locateAnchor.value
  // 只刷新仍打开、仍是同一会话的历史窗口；正常时间线不触发。
  if (!conversationId || !anchor || !locateMode.value) return
  if (anchor.conversationId !== conversationId) return
  const capturedVersion = locateWindowVersion.value
  // 写回窗口（打开/替换）前校验：期间未「回到最新」、未切会话、未跳到另一条历史消息。
  const isSameWindowBeforeApply = (): boolean =>
    activeId.value === conversationId &&
    locateMode.value &&
    locateWindowVersion.value === capturedVersion &&
    locateAnchor.value?.conversationId === conversationId &&
    locateAnchor.value?.messageId === anchor.messageId &&
    locateAnchor.value?.seq === anchor.seq
  const list = messageListRef.value
  const scrollTop = list?.getScrollTop?.() ?? 0
  const applied = await ensureMessageVisible(conversationId, anchor, {
    scroll: false,
    forceContext: true,
    guard: isSameWindowBeforeApply,
  })
  if (!applied) return
  await nextTick()
  // 恢复滚动前再次校验：窗口仍是同一会话、同一目标的定位窗口。
  if (
    activeId.value !== conversationId ||
    !locateMode.value ||
    locateAnchor.value?.conversationId !== conversationId ||
    locateAnchor.value?.messageId !== anchor.messageId ||
    locateAnchor.value?.seq !== anchor.seq
  ) {
    return
  }
  list?.setScrollTop?.(scrollTop)
}

// F-37：定位窗口打开期间，同会话的撤回/编辑帧也要反映到窗口内（只更新临时窗口，不落缓存）。
watch(
  () => messages.lastMessagePatch,
  (event) => {
    const window = locateWindow.value
    if (!event || !window || event.conversationId !== activeId.value) return
    const index = window.messages.findIndex((message) => message.messageId === event.messageId)
    if (index < 0) return
    const next = window.messages.slice()
    next[index] = { ...next[index], ...event.patch }
    locateWindow.value = { ...window, messages: next }
  },
)

async function onJumpToMessage(messageId: string): Promise<void> {
  const conversationId = activeId.value
  if (!conversationId) return
  if (!(await ensureMessageVisible(conversationId, { messageId }))) {
    pageNotice.value = '消息已不存在或无法定位'
  }
}

// F-11：搜索结果跳转（?jumpSeq= / ?jumpMessageId=）：按目标加载上下文窗口后定位，
// 完成后清掉查询参数，避免刷新页面重复跳转。
let pendingJumpSeq: number | null = null
let pendingJumpMessageId: string | null = null

function clearJumpQuery(): void {
  if (!('jumpSeq' in route.query) && !('jumpMessageId' in route.query)) return
  const query = { ...route.query }
  delete query.jumpSeq
  delete query.jumpMessageId
  void router.replace({ query })
}

async function runPendingJump(): Promise<void> {
  const conversationId = activeId.value
  const messageId = pendingJumpMessageId
  const seq = pendingJumpSeq
  if (!conversationId || (messageId === null && seq === null)) return

  const located = await ensureMessageVisible(conversationId, {
    ...(messageId ? { messageId } : {}),
    ...(messageId ? {} : { seq: seq ?? undefined }),
  })
  if (pendingJumpMessageId !== messageId || pendingJumpSeq !== seq) return
  if (located) {
    pendingJumpMessageId = null
    pendingJumpSeq = null
    clearJumpQuery()
    return
  }
  pendingJumpMessageId = null
  pendingJumpSeq = null
  clearJumpQuery()
  pageNotice.value = '消息不在当前加载范围，可手动加载更早消息查看'
  // 定位失败时已回到正常时间线，按原有可见/贴底规则恢复已读上报。
  void nextTick(markActiveConversationRead)
}

watch(
  () => [route.query.jumpSeq, route.query.jumpMessageId] as const,
  ([seqValue, messageIdValue]) => {
    const seq = typeof seqValue === 'string' ? Number(seqValue) : Number.NaN
    pendingJumpMessageId =
      typeof messageIdValue === 'string' && messageIdValue.length > 0 ? messageIdValue : null
    pendingJumpSeq = Number.isFinite(seq) && seq > 0 ? seq : null
    if (pendingJumpMessageId !== null || pendingJumpSeq !== null) void runPendingJump()
  },
  { immediate: true },
)

// 目标会话的消息可能还在异步加载：加载完成后再尝试一次定位。
watch(activeLoaded, (loaded) => {
  if (loaded && (pendingJumpSeq !== null || pendingJumpMessageId !== null)) void runPendingJump()
})

// 切换会话时清空回复目标，避免把引用带到另一个会话（F-09）。
watch(activeId, () => {
  replyTarget.value = null
})

// 图片预览（F-08）：会话内图片列表 = 已加载消息 + 乐观待发图片；blob URL 复用 core/blobCache
// （按 fileId 去重、上限 100、退出登录时整体回收），避免与缩略图/下载重复请求，也不把 URL 写进 store。
interface PreviewSource {
  messageId: string
  name: string
  fileId: string
}

const previewOpen = ref(false)
const previewIndex = ref(0)
const previewLoading = ref(false)
const previewUrls = ref<Record<string, string>>({})
let previewRequestSeq = 0

const previewSources = computed<PreviewSource[]>(() => {
  if (!activeId.value) return []
  const fileIds = new Map<string, string>()
  for (const message of displayMessages.value) {
    // F-18：已撤回的图片不再出现在预览序列里。
    if (typeof message.deletedAt === 'number') continue
    const content = message.content
    if (message.msgType !== 'image' || typeof content !== 'object' || content === null) continue
    const fileId = 'fileId' in content ? content.fileId : null
    if (typeof fileId === 'string' && fileId) fileIds.set(message.messageId, fileId)
  }
  const sources: PreviewSource[] = imageItemsOf(displayMessages.value)
    .map((item) => ({ messageId: item.messageId, name: item.name, fileId: fileIds.get(item.messageId) ?? '' }))
    .filter((item) => Boolean(item.fileId))
  for (const pending of displayPending.value) {
    const content = pending.content
    if (pending.msgType !== 'image' || typeof content !== 'object' || content === null) continue
    const fileId = 'fileId' in content ? content.fileId : null
    if (typeof fileId !== 'string' || !fileId) continue
    const name = 'name' in content && typeof content.name === 'string' && content.name ? content.name : '图片'
    sources.push({ messageId: pending.clientMsgId, name, fileId })
  }
  return sources
})

const previewItems = computed(() =>
  previewSources.value.map((source) => ({
    messageId: source.messageId,
    name: source.name,
    url: previewUrls.value[source.messageId] ?? null,
  })),
)

function onPreviewImage(messageId: string): void {
  const index = previewSources.value.findIndex((source) => source.messageId === messageId)
  if (index < 0) return
  previewIndex.value = index
  previewOpen.value = true
  prefetchPreviewAround(index)
}

function onPreviewIndex(nextIndex: number): void {
  if (nextIndex < 0 || nextIndex >= previewSources.value.length) return
  previewIndex.value = nextIndex
  prefetchPreviewAround(nextIndex)
}

/** 仅预取当前与左右各一张，控制并发与内存。 */
function prefetchPreviewAround(index: number): void {
  void ensurePreviewUrl(index)
  void ensurePreviewUrl(index - 1)
  void ensurePreviewUrl(index + 1)
}

async function ensurePreviewUrl(index: number): Promise<void> {
  const source = previewSources.value[index]
  if (!source || previewUrls.value[source.messageId]) return
  const sequence = ++previewRequestSeq
  previewLoading.value = true
  try {
    const url = await getOrLoadBlobUrl(source.fileId, () => filesApi.downloadBlob(source.fileId))
    previewUrls.value = { ...previewUrls.value, [source.messageId]: url }
  } catch {
    // 加载失败保持空 url，预览面板显示「图片加载失败」。
  } finally {
    if (sequence === previewRequestSeq) previewLoading.value = false
  }
}

function closePreview(): void {
  previewOpen.value = false
  previewLoading.value = false
  previewRequestSeq += 1
}

watch(activeId, () => {
  closePreview()
  previewUrls.value = {}
})

// 已读回执（M3-09）：只有停留在底部（已看到最新消息）才推进已读；
// 单聊展示对端已读状态，群聊 v1 无已读详情（协议 §4.6）。
/** 消息列表是否停留在底部（由 MessageList 的 near-bottom-change 驱动）。 */
const atBottom = ref(true)
/** 单聊对端用户 ID（群聊为空，不展示已读回执）。 */
const peerUserId = computed(
  () => otherMemberOf(activeConversation.value, selfUserId.value)?.userId,
)
/** 单聊对端已上报的已读 seq（来自 read.updated；无记录时 undefined，展示「未读」）。 */
const peerReadSeqForDisplay = computed(() =>
  activeId.value && peerUserId.value
    ? conversations.peerReadSeqOf(activeId.value, peerUserId.value)
    : undefined,
)

/** 单聊对端在线状态（M3-09）：undefined 表示未知（v1 无初始查询，不显示成离线）。 */
const peerPresence = computed(() => presence.stateOf(peerUserId.value))

/**
 * 顶栏头像：群聊用群头像（conversations.avatar_file_id），单聊用对端成员头像。
 * 此前单聊误传群头像字段（恒为 null），导致顶栏永远是首字母占位。
 */
const headerAvatarFileId = computed(() => {
  const conversation = activeConversation.value
  if (!conversation) return null
  if (conversation.type === 'group') return conversation.avatarFileId ?? null
  const peerId = peerUserId.value
  if (!peerId) return null
  return conversation.members.find((member) => member.userId === peerId)?.avatarFileId ?? null
})

/** 当前会话「输入中」提示（M3-09）：单聊「对方正在输入…」、群聊「X 正在输入…」/多人。 */
const typingText = computed(() => {
  const conversation = activeConversation.value
  if (!conversation || !activeId.value) return ''
  const nameById = new Map(conversation.members.map((member) => [member.userId, member.displayName]))
  return typing.typingLabel(activeId.value, conversation.type, (userId) => nameById.get(userId) ?? '')
})

// 切换会话：首次进入拉最新一页历史（M2-05）；已加载过直接用缓存，后续新消息由 message.new 合并。
watch(
  activeId,
  (conversationId, previousId) => {
    activeDrawer.value = null
    exitLocateWindow()
    atBottom.value = true
    // 离开会话即清除其「输入中」状态（M3-09，避免残留旧会话提示）。
    if (previousId) typing.clearConversation(previousId)
    if (!conversationId) return
    void messages.loadInitial(conversationId).then(() => {
      // 首次加载并滚到底后上报一次已读（M3-09）。
      void nextTick(markActiveConversationRead)
    })
  },
  { immediate: true },
)

// 打开会话（或会话内收到新消息）时推进已读游标并上报服务端（read.update，M3-05 服务端已支持）：
// 未读角标随之清除；服务端 last_read_seq 是重新登录/换设备后未读计算的依据。
// M3-00 N6：仅当页面可见且当前会话就是 activeId 时才推进；滚到底判定留 M3-09。
function markActiveConversationRead(): void {
  // F-37：定位窗口不是最新位置，滚动/可见性变化不能推进已读游标。
  if (locateMode.value || locateRequesting.value) return
  const conversationId = activeId.value
  const lastSeq = activeConversation.value?.lastSeq ?? 0
  const advance = shouldAdvanceRead({
    visible: document.visibilityState === 'visible',
    atBottom: atBottom.value,
    loaded: activeLoaded.value,
    conversationId,
    lastSeq,
    reportedSeq: conversationId ? conversations.readSeqOf(conversationId) : 0,
  })
  if (!advance || !conversationId) return
  conversations.markRead(conversationId, lastSeq)
}

/** 消息列表滚动/自动贴底事件：回到或停在底部时推进已读（M3-09）。 */
function onNearBottomChange(value: boolean): void {
  atBottom.value = value
  if (locateMode.value || locateRequesting.value) return
  if (value) markActiveConversationRead()
}

watch(
  () => [activeId.value, activeConversation.value?.lastSeq ?? 0] as const,
  markActiveConversationRead,
  { immediate: true },
)

// 被移出/主动退出群聊（conv.updated.removedConversationIds）：会话与消息缓存清理由 stores 完成，
// 页面负责提示与跳转（若正是当前打开的会话）。
function handleRemovedNotice(): void {
  const removedId = conversations.lastRemovedConversationId
  if (!removedId) return
  activeDrawer.value = null
  if (activeId.value === removedId) {
    pageNotice.value = '你已被移出群聊或已退出群聊'
    void router.replace({ name: 'chat' })
  }
  conversations.clearRemovedNotice()
}

watch(() => conversations.lastRemovedConversationId, handleRemovedNotice)

onMounted(async () => {
  // 启动/刷新：先恢复 IndexedDB 缓存渲染（会话列表 + 各会话最近消息）；
  // 会话列表刷新与增量补拉统一由 messages store 在 authed（首次/重连成功）时触发（M3-00 B3）。
  conversations.subscribeRealtime()
  messages.subscribeRealtime()
  typing.subscribeRealtime()
  offWsState = chatWs.on('statechange', onWsStateChange)
  document.addEventListener('visibilitychange', markActiveConversationRead)
  window.addEventListener('dragover', onWindowDragOver)
  window.addEventListener('drop', onWindowDrop)
  await conversations.restoreFromCache()
  await messages.restoreFromCache()
})

onBeforeUnmount(() => {
  if (slowLoadingTimer !== null) window.clearTimeout(slowLoadingTimer)
  // 离开聊天页（去设置/退出登录/切换账号或服务器）：中止上传并丢弃队列，
  // 晚到的压缩/上传结果因找不到队列项而不会再发送；仅聊天页内切换会话不触发卸载。
  cancelAllUploads()
  clearSentCleanupTimers()
  uploadItems.value = []
  offWsState?.()
  offWsState = null
  conversations.setActiveConversation(null)
  if (recoveredTimer) {
    clearTimeout(recoveredTimer)
    recoveredTimer = null
  }
  document.removeEventListener('visibilitychange', markActiveConversationRead)
  window.removeEventListener('dragover', onWindowDragOver)
  window.removeEventListener('drop', onWindowDrop)
  document.removeEventListener('mousedown', onEmojiOutsidePointer)
  document.removeEventListener('keydown', onEmojiEscape)
  panelGuard.dispose()
  closePreview()
})

function selectConversation(conversationId: string): void {
  void router.push({ name: 'chat', params: { conversationId } })
}

/** ≤800 移动布局：从聊天详情返回——有站内历史则后退（保留列表滚动/筛选），否则回会话列表。 */
function backToList(): void {
  const historyState = window.history.state as { back?: string | null } | null
  if (historyState && typeof historyState.back === 'string' && historyState.back) {
    router.back()
    return
  }
  void router.replace({ name: 'chat' })
}

function sendMessage(text: string): void {
  const conversationId = activeId.value
  if (!conversationId || !selfUserId.value) return
  const reply = replyTarget.value
  const sent = messages.sendText(
    conversationId,
    text,
    selfUserId.value,
    reply?.messageId,
    reply?.preview,
  )
  if (!sent) return
  replyTarget.value = null
  // 发送成功即清空该会话草稿：发送框随后会把空串回传，这里先按语义删除并立即写盘（F-04）。
  drafts.removeDraft(conversationId)
}

/** 输入框内容变化 → 写入草稿（F-04）；空串表示已清空，由 store 删除对应条目。 */
function onDraftChange(text: string): void {
  const conversationId = activeId.value
  if (!conversationId) return
  drafts.setDraft(conversationId, text)
}

/** 输入中上报（M3-09）：客户端 3 秒节流 + 连接已认证才发送；服务端对「连接×会话」另有 1 秒静默节流。 */
function sendTyping(): void {
  const conversationId = activeId.value
  if (!conversationId || chatWs.state !== 'authed') return
  if (!typingThrottle.shouldSend(conversationId)) return
  chatWs.send({ type: 'typing', conversationId })
}

function retryMessage(clientMsgId: string): void {
  if (!activeId.value) return
  messages.retrySend(activeId.value, clientMsgId)
}

function loadMoreMessages(): void {
  if (!activeId.value) return
  void messages.loadMore(activeId.value)
}

// 群成员面板操作结果：邀请/移除后服务端返回最新 Conversation，写回 store（成员数即时更新）。
function onGroupUpdated(conversation: Conversation): void {
  conversations.upsert([conversation])
}

// 主动退出群聊成功：移出列表并清缓存（与 removedConversationIds 推送路径一致），回到空聊天页。
function onLeftGroup(conversationId: string): void {
  activeDrawer.value = null
  conversations.handleRemovedConversations([conversationId])
}

// F-41：群成员资料浮窗「发送消息」——收起群资料侧栏后跳到与对方的单聊。
function onOpenMemberChat(conversationId: string): void {
  activeDrawer.value = null
  selectConversation(conversationId)
}

// 表情面板悬浮在发送框上方（UI-02）：绝对定位不参与文档流，开合不挤动聊天内容；
// 点击面板外任意位置或按 Esc 收起（不需要面板内关闭按钮）。
function toggleEmojiDrawer(): void {
  activeDrawer.value = activeDrawer.value === 'emoji' ? null : 'emoji'
}

/** 点击表情面板内部或触发按钮不算「点击外部」（触发按钮自身负责开合切换）。
 * F-21：面板内的删除/清空确认弹窗由 AppDialog Teleport 到 body（`.ec-dialog`），同样视为面板内部，
 * 否则点击确认框会先关掉面板导致弹窗随之卸载。 */
function isEmojiLayerTarget(target: EventTarget | null): boolean {
  return target instanceof Element && Boolean(target.closest('.ec-emoji-panel, [data-emoji-trigger], .ec-dialog'))
}

function onEmojiOutsidePointer(event: MouseEvent): void {
  if (!isEmojiLayerTarget(event.target)) activeDrawer.value = null
}

function onEmojiEscape(event: KeyboardEvent): void {
  if (event.key !== 'Escape') return
  // F-21：面板内弹窗打开时，Esc 应先交给 AppDialog 关闭确认框，不连带收起面板。
  // F-41：成员资料浮窗同理，Esc 只关浮窗，不连带收起整个侧栏。
  if (document.querySelector('.ec-dialog, .member-card')) return
  activeDrawer.value = null
}

watch(activeDrawer, (drawer) => {
  document.removeEventListener('mousedown', onEmojiOutsidePointer)
  document.removeEventListener('keydown', onEmojiEscape)
  if (drawer) panelGuard.push()
  else panelGuard.dispose()
  if (drawer === 'emoji') {
    document.addEventListener('mousedown', onEmojiOutsidePointer)
    document.addEventListener('keydown', onEmojiEscape)
    return
  }
  // 群成员是内嵌侧栏，不监听外部点击；保留 Esc 收起与顶栏/关闭按钮切换。
  if (drawer === 'group') {
    document.addEventListener('keydown', onEmojiEscape)
  }
})

function toggleGroupDrawer(): void {
  if (!isGroup.value) return
  if (activeDrawer.value === 'group' && asideTab.value === 'members') {
    activeDrawer.value = null
    return
  }
  asideTab.value = 'members'
  activeDrawer.value = 'group'
}

/** F-15：顶栏「文件」按钮——打开 aside 并定位文件页签；再次点击收起。 */
function toggleFilesPanel(): void {
  if (!activeId.value) return
  if (activeDrawer.value === 'group' && asideTab.value === 'files') {
    activeDrawer.value = null
    return
  }
  asideTab.value = 'files'
  activeDrawer.value = 'group'
}

/** F-15：aside 内页签切换（群聊：群资料与成员 / 文件）。 */
function switchAsideTab(tab: 'members' | 'files'): void {
  if (tab === 'members' && !isGroup.value) return
  asideTab.value = tab
  activeDrawer.value = 'group'
}

// Emoji 面板选择：插入到发送框光标处，不发送、不关闭面板（可连续选多个）。
function onEmojiPick(emoji: string): void {
  sendBoxRef.value?.insertEmoji(emoji)
}

/**
 * F-21：自定义表情以 image 消息发送（复用现有上传/发送链路，不改协议）。
 * 面板已把图片准备成 128px PNG 或原样 GIF，这里不再二次压缩（避免透明通道丢失）。
 */
async function onPickCustomEmoji(file: File): Promise<void> {
  const conversationId = activeId.value
  const senderId = selfUserId.value
  if (!conversationId || !senderId) return
  // 只拦截同一会话内的重复提交；其他会话的表情/语音上传互不影响。
  if (singleShotUploads.value.has(conversationId)) return

  const validation = validateAttachment(file)
  if (!validation.ok) {
    uploadError.value = validation.reason
    return
  }

  singleShotUploads.value.add(conversationId)
  uploadError.value = null
  try {
    const uploaded = await filesApi.upload(file)
    const sent = messages.sendFileMessage(
      conversationId,
      'image',
      {
        fileId: uploaded.fileId,
        name: uploaded.name,
        size: uploaded.size,
        mime: uploaded.mime,
      },
      senderId,
    )
    if (!sent) showToast('表情发送失败', 'error')
  } catch {
    showToast('表情发送失败', 'error')
  } finally {
    singleShotUploads.value.delete(conversationId)
  }
}

// F-28：录音完成 → 上传音频文件 → 以 voice 消息发送（复用现有上传/发送链路，不改协议）。
async function handleVoiceRecorded(payload: { blob: Blob; durationMs: number }): Promise<void> {
  const conversationId = activeId.value
  const senderId = selfUserId.value
  if (!conversationId || !senderId) return
  // 同上：只拦截同一会话内的重复提交，跨会话互不影响。
  if (singleShotUploads.value.has(conversationId)) return

  singleShotUploads.value.add(conversationId)
  uploadError.value = null
  try {
    const uploaded = await filesApi.uploadVoice(payload.blob)
    const sent = messages.sendVoiceMessage(
      conversationId,
      uploaded.fileId,
      payload.durationMs,
      senderId,
    )
    if (!sent) showToast('语音发送失败', 'error')
  } catch (error) {
    const message = error instanceof Error && error.message ? error.message : '语音发送失败'
    showToast(message, 'error')
  } finally {
    singleShotUploads.value.delete(conversationId)
  }
}

function onVoiceError(message: string): void {
  showToast(message, 'error')
}

// 附件上传队列（F-35）：选择后入队并逐项展示真实进度；取消/失败不影响队列其他项。
// 并发策略：不同会话之间并行上传，同一会话内仍按入队顺序串行；全局并发上限见 MAX_CONCURRENT_UPLOADS。
// 说明：表情/语音等单发上传走 singleShotUploads（按会话），与附件队列互不阻塞。
const uploadItems = ref<UploadItem[]>([])
const uploadControllers = new Map<string, AbortController>()
/** 全局并发上限：跨会话并行，但不超过该值，避免内存与带宽峰值。 */
const MAX_CONCURRENT_UPLOADS = 3
/** 单会话并发上限：同一会话内保持串行，避免多张大图同时压缩占内存。 */
const MAX_CONCURRENT_UPLOADS_PER_CONVERSATION = 1
let uploadPumping = false
/** 单发上传（自定义表情/语音）进行中的会话集合：同一会话内互斥，跨会话互不影响。 */
const singleShotUploads = ref<Set<string>>(new Set())
const uploadError = ref<string | null>(null)
/** 输入区只展示当前会话的附件项：切换会话不把其他会话的上传中/失败项带过来。 */
const visibleUploadItems = computed(() =>
  uploadItems.value.filter((item) => item.conversationId === activeId.value),
)
/** 当前会话是否有待上传/上传中的附件：只影响本会话的输入区状态，不再跨会话互相禁用。 */
const visibleQueueBusy = computed(() =>
  visibleUploadItems.value.some((item) => item.status === 'uploading' || item.status === 'pending'),
)
/** 当前会话是否有表情/语音正在上传。 */
const singleShotBusy = computed(() => {
  const conversationId = activeId.value
  return Boolean(conversationId) && singleShotUploads.value.has(conversationId as string)
})

// 退出登录/切换账号：关闭群成员面板、清空附件队列与页内提示（附件属于原账号）。
watch(selfUserId, () => {
  activeDrawer.value = null
  pageNotice.value = ''
  uploadError.value = null
  cancelAllUploads()
  clearSentCleanupTimers()
  singleShotUploads.value.clear()
  uploadItems.value = []
  typing.clear()
  typingThrottle.reset()
})

function patchUploadItem(id: string, patch: Partial<UploadItem>): void {
  uploadItems.value = uploadItems.value.map((item) =>
    item.id === id ? { ...item, ...patch } : item,
  )
  if (patch.status === 'sent') scheduleSentCleanup(id)
}

// 已发送项保留片刻后自动移出队列，避免长期堆积撑高输入区；失败项保留，供重试或手动移出。
const SENT_UPLOAD_KEEP_MS = 4000
const sentCleanupTimers = new Map<string, number>()

function scheduleSentCleanup(id: string): void {
  if (sentCleanupTimers.has(id)) return
  const timer = window.setTimeout(() => {
    sentCleanupTimers.delete(id)
    uploadItems.value = uploadItems.value.filter((entry) => entry.id !== id)
  }, SENT_UPLOAD_KEEP_MS)
  sentCleanupTimers.set(id, timer)
}

function clearSentCleanupTimers(): void {
  for (const timer of sentCleanupTimers.values()) window.clearTimeout(timer)
  sentCleanupTimers.clear()
}

function findUploadItem(id: string): UploadItem | undefined {
  return uploadItems.value.find((item) => item.id === id)
}

/**
 * 「发送中」对账：附件项与消息 store 用同一 clientMsgId 关联。
 * pending 存在 → 仍发送中（其中 failed 表示发送失败）；pending 消失且消息已存在（ack 或补拉确认）→ 已发送。
 */
watch(
  () => {
    // 待发记录状态是原地更新的（重试耗尽→failed），显式依赖版本号。
    void messages.pendingRevision
    return uploadItems.value
      .filter(
        (item) =>
          item.clientMsgId !== undefined &&
          (item.status === 'sending' || (item.status === 'failed' && Boolean(item.fileId))),
      )
      .map((item) => {
        const clientMsgId = item.clientMsgId ?? ''
        const pending = messages
          .pendingOf(item.conversationId)
          .find((entry) => entry.clientMsgId === clientMsgId)
        if (pending) return `${item.id}:${pending.status === 'failed' ? 'failed' : 'sending'}`
        const confirmed = messages
          .messagesOf(item.conversationId)
          .some((message) => message.clientMsgId === clientMsgId)
        return `${item.id}:${confirmed ? 'sent' : item.status === 'failed' ? 'failed' : 'sending'}`
      })
      .join(',')
  },
  (signature) => {
    if (!signature) return
    for (const entry of signature.split(',')) {
      const [id, state] = entry.split(':')
      const item = findUploadItem(id)
      if (!item) continue
      if (state === 'sent') {
        // 迟到的 ack / 补拉确认也覆盖「已标记失败」的附件。
        patchUploadItem(id, { status: 'sent', progress: 100, error: undefined })
      } else if (state === 'failed' && item.status === 'sending') {
        patchUploadItem(id, { status: 'failed', error: '消息发送失败，请重试' })
      }
    }
  },
  { immediate: true },
)

/** 取消所有进行中的上传（切换账号/退出登录时调用）。 */
function cancelAllUploads(): void {
  for (const controller of uploadControllers.values()) controller.abort()
  uploadControllers.clear()
}

/** 选择文件：只入队并固定当时的会话/账号/回复目标，实际上传由队列串行处理。 */
function handleFileSelected(files: File[]): void {
  if (files.length === 0) return
  const conversationId = activeId.value
  const senderId = selfUserId.value
  if (!conversationId || !senderId) {
    uploadError.value = '请先选择会话'
    return
  }
  const { files: capped, rejectedCount } = truncateFiles(files)
  if (capped.length === 0) return
  uploadError.value = null
  if (rejectedCount > 0) {
    uploadError.value = `一次最多发送 ${MAX_SEND_FILES} 个文件，已忽略多余的 ${rejectedCount} 个`
  }

  // 入队前先做本地校验：不支持的类型/空文件/超限文件直接忽略并提示，
  // 不生成「上传失败」队列项——这类项重试也不会成功，还会长期占住输入区。
  const accepted: File[] = []
  const ignored: Array<{ name: string; reason: string }> = []
  for (const file of capped) {
    const normalized = normalizeDroppedFile(file, Date.now())
    const validation = validateAttachment(normalized)
    if (validation.ok) accepted.push(normalized)
    else ignored.push({ name: normalized.name, reason: validation.reason })
  }
  if (ignored.length === 1) {
    showToast(`已忽略「${ignored[0].name}」：${ignored[0].reason}`, 'error')
  } else if (ignored.length > 1) {
    const names = ignored.slice(0, 2).map((entry) => entry.name).join('、')
    showToast(`已忽略 ${ignored.length} 个文件（${names}${ignored.length > 2 ? ' 等' : ''}）`, 'error')
  }
  if (accepted.length === 0) return

  const reply = replyTarget.value
  const created = accepted.map((file) =>
    createUploadItem(file, conversationId, senderId, {
      replyTo: reply?.messageId,
      replyPreview: reply?.preview,
    }),
  )
  uploadItems.value = [...uploadItems.value, ...created]
  void pumpUploads()
}

/**
 * 调度 pending 项：跨会话并行，受「全局上限 3 + 单会话上限 1」约束。
 * 每项结束（成功/失败/取消）后再次调用本函数续跑，避免一次性拉起全部队列。
 */
function pumpUploads(): void {
  if (uploadPumping) return
  uploadPumping = true
  try {
    for (;;) {
      const active = uploadItems.value.filter((item) => item.status === 'uploading')
      if (active.length >= MAX_CONCURRENT_UPLOADS) return
      const perConversation = new Map<string, number>()
      for (const item of active) {
        perConversation.set(item.conversationId, (perConversation.get(item.conversationId) ?? 0) + 1)
      }
      const next = uploadItems.value.find(
        (item) =>
          item.status === 'pending' &&
          (perConversation.get(item.conversationId) ?? 0) < MAX_CONCURRENT_UPLOADS_PER_CONVERSATION,
      )
      if (!next) return
      // processUploadItem 在首个 await 前已把状态置为 uploading，循环下一轮据此计数。
      void processUploadItem(next.id)
    }
  } finally {
    uploadPumping = false
  }
}

/**
 * 单文件链路：校验 → 图片压缩 → 带真实进度上传 → 交给消息发送链路。
 * 取消后即使上传响应晚到，也直接丢弃，不生成聊天消息。
 */
async function processUploadItem(id: string): Promise<void> {
  const item = findUploadItem(id)
  if (!item || item.status !== 'pending') return
  const { conversationId, senderId } = item
  if (!conversationId || !senderId) {
    patchUploadItem(id, { status: 'failed', error: '请先选择会话' })
    return
  }

  patchUploadItem(id, { status: 'uploading', progress: 0, error: undefined })
  const controller = new AbortController()
  uploadControllers.set(id, controller)
  try {
    const validation = validateAttachment(item.file)
    if (!validation.ok) {
      patchUploadItem(id, { status: 'failed', error: validation.reason })
      return
    }
    // 图片发送前压缩（最长边 1280、约 ≤300KB）；gif 由 compressImageFile 原样返回。
    const payload = isImageFile(item.file) ? await compressImageFile(item.file) : item.file
    const recheck = validateAttachment(payload)
    if (!recheck.ok) {
      patchUploadItem(id, { status: 'failed', error: recheck.reason })
      return
    }
    // 压缩期间可能已离开聊天页/取消：丢弃结果，不再上传。
    if (findUploadItem(id)?.status !== 'uploading') return

    const uploaded = await filesApi.uploadWithProgress(payload, {
      signal: controller.signal,
      onProgress: (percent) => patchUploadItem(id, { progress: percent }),
    })
    const current = findUploadItem(id)
    // 取消或离开聊天页后队列项已被清理：丢弃晚到结果，不再发送。
    if (!current || current.status !== 'uploading') return

    const msgType = isImageFile(payload) ? 'image' : 'file'
    const content: FileContent = {
      fileId: uploaded.fileId,
      name: uploaded.name,
      size: uploaded.size,
      mime: uploaded.mime,
    }
    const clientMsgId = crypto.randomUUID()
    const sent = messages.sendFileMessage(
      conversationId,
      msgType,
      content,
      senderId,
      item.replyTo,
      item.replyPreview,
      clientMsgId,
    )
    if (!sent) {
      patchUploadItem(id, {
        status: 'failed',
        progress: 100,
        fileId: uploaded.fileId,
        clientMsgId,
        msgType,
        content,
        error: '消息发送失败，请重试',
      })
      return
    }
    // 上传完成只代表已进入消息发送链路；显示「已发送」要等 ack 或补拉确认（见下方 watch）。
    patchUploadItem(id, {
      status: 'sending',
      progress: 100,
      fileId: uploaded.fileId,
      clientMsgId,
      msgType,
      content,
      error: undefined,
    })
    if (item.replyTo && replyTarget.value?.messageId === item.replyTo) replyTarget.value = null
  } catch (error) {
    const current = findUploadItem(id)
    if (!current || current.status === 'cancelled') return
    const message =
      error instanceof ApiError
        ? error.message
        : error instanceof Error && error.message === '上传已取消'
          ? '已取消'
          : '文件上传失败，请重试'
    patchUploadItem(id, { status: 'failed', error: message })
  } finally {
    uploadControllers.delete(id)
    // 腾出并发槽位后续跑同会话/其他会话的 pending 项。
    pumpUploads()
  }
}

/** 取消上传中的附件：中断请求并标记已取消，响应晚到也不会触发发送。 */
function cancelUpload(id: string): void {
  const item = findUploadItem(id)
  if (!item || item.status !== 'uploading') return
  uploadControllers.get(id)?.abort()
  uploadControllers.delete(id)
  patchUploadItem(id, { status: 'cancelled', error: undefined })
  pumpUploads()
}

/** 移出等待/失败/已取消的附件项（上传中请先取消）；失败项可先重试，也可直接移出。 */
function dismissUpload(id: string): void {
  const item = findUploadItem(id)
  if (!item) return
  if (item.status !== 'pending' && item.status !== 'failed' && item.status !== 'cancelled') return
  uploadControllers.get(id)?.abort()
  uploadControllers.delete(id)
  uploadItems.value = uploadItems.value.filter((entry) => entry.id !== id)
}

/** 单项重试：仅重试失败的那一项。 */
function retryUpload(id: string): void {
  const item = findUploadItem(id)
  if (!item || item.status !== 'failed') return
  // 迟到的 ack / 补拉可能已确认该消息：已确认就直接置为已发送，不再进入待发队列。
  if (item.clientMsgId && item.fileId) {
    const confirmed = messages
      .messagesOf(item.conversationId)
      .some((message) => message.clientMsgId === item.clientMsgId)
    const pending = messages
      .pendingOf(item.conversationId)
      .find((entry) => entry.clientMsgId === item.clientMsgId)
    if (confirmed && !pending) {
      patchUploadItem(id, { status: 'sent', progress: 100, error: undefined })
      return
    }
  }
  // 文件已上传、仅消息发送失败：走消息 store 的既有重发入口，复用原 fileId 与 clientMsgId，
  // 不重新上传、不创建第二条待发消息。
  if (item.fileId && item.clientMsgId && item.content && item.msgType) {
    const clientMsgId = item.clientMsgId
    const existing = messages
      .pendingOf(item.conversationId)
      .find((entry) => entry.clientMsgId === clientMsgId)
    if (existing) {
      // 已存在同 clientMsgId 的待发消息：走既有重发入口，不新建待发记录。
      messages.retrySend(item.conversationId, clientMsgId)
    } else {
      // 极端情况（乐观发送在创建待发记录前被拦截）：用原 clientMsgId 重新进入发送链路，保证仍只有一条消息。
      messages.sendFileMessage(
        item.conversationId,
        item.msgType,
        item.content,
        item.senderId,
        item.replyTo,
        item.replyPreview,
        clientMsgId,
      )
    }
    const pending = messages
      .pendingOf(item.conversationId)
      .find((entry) => entry.clientMsgId === clientMsgId)
    patchUploadItem(id, {
      status: pending ? 'sending' : 'failed',
      error: pending ? undefined : '消息发送失败，请重试',
    })
    return
  }
  patchUploadItem(id, { status: 'pending', progress: 0, error: undefined })
  void pumpUploads()
}

// 拖拽上传（F-06）：根容器显示投放提示；window 兜底阻止浏览器/Electron 直接打开文件。
const dragActive = ref(false)
/** 进入/离开计数，避免在子元素间移动时覆盖层闪烁。 */
let dragDepth = 0

function onDragEnter(event: DragEvent): void {
  if (!hasFiles(event.dataTransfer) || !activeId.value) return
  event.preventDefault()
  dragDepth += 1
  dragActive.value = true
}

function onDragOver(event: DragEvent): void {
  if (!hasFiles(event.dataTransfer)) return
  event.preventDefault()
  // 上传按会话独立：任何会话上传中都不阻止在当前会话投放，文件进入各自队列等待。
  if (event.dataTransfer) event.dataTransfer.dropEffect = 'copy'
}

function onDragLeave(event: DragEvent): void {
  if (!hasFiles(event.dataTransfer)) return
  dragDepth = Math.max(0, dragDepth - 1)
  if (dragDepth === 0) dragActive.value = false
}

function onDrop(event: DragEvent): void {
  if (!hasFiles(event.dataTransfer)) return
  event.preventDefault()
  dragDepth = 0
  dragActive.value = false
  if (!activeId.value) {
    uploadError.value = '请先选择会话'
    return
  }
  const files = filesOf(event.dataTransfer)
  if (files.length === 0) return
  void handleFileSelected(files)
}

/** window 兜底：拖拽含文件时一律 preventDefault（即使落在侧栏/页面空白处也不打开文件）。 */
function onWindowDragOver(event: DragEvent): void {
  if (hasFiles(event.dataTransfer)) event.preventDefault()
}

function onWindowDrop(event: DragEvent): void {
  if (!hasFiles(event.dataTransfer)) return
  event.preventDefault()
  dragDepth = 0
  dragActive.value = false
}
</script>

<template>
  <main
    class="chat-page"
    :class="{ 'chat-page--conversation': Boolean(activeId), 'chat-page--members': asideOpen }"
  >
    <ConversationList
      class="chat-page__list"
      :conversations="conversations.sorted"
      :self-user-id="selfUserId"
      :active-id="activeId"
      :loading="conversations.loading"
      :filter="listFilter"
      @select="selectConversation"
      @update:filter="changeListFilter"
      @create="openContacts"
    />

    <section
      class="chat-page__main"
      @dragenter="onDragEnter"
      @dragover="onDragOver"
      @dragleave="onDragLeave"
      @drop="onDrop"
    >
      <NoticeBar v-if="pageNotice" :text="pageNotice" action-label="知道了" @action="pageNotice = ''" />
      <NoticeBar
        v-if="connectionNotice"
        :text="connectionNotice.text"
        :tone="connectionNotice.tone"
      />

      <template v-if="activeConversation">
        <ChatHeader
          :title="activeTitle"
          :is-group="isGroup"
          :member-count="memberCount"
          :peer-presence="peerPresence"
          :avatar-file-id="headerAvatarFileId"
          :show-back="true"
          @open-group="toggleGroupDrawer"
          @open-files="toggleFilesPanel"
          @back="backToList"
          @search="openConversationSearch"
        />
        <!-- F-36：群置顶消息条——全员可见，点击定位原消息；仅群主可取消置顶。 -->
        <div v-if="isGroup && pinnedMessage" class="chat-page__pinned ec-enter">
          <button
            type="button"
            class="chat-page__pinned-main"
            :aria-label="`查看置顶消息：${pinnedText}`"
            @click="onJumpToMessage(pinnedMessage.messageId)"
          >
            <AppIcon name="pin" :size="16" />
            <span class="chat-page__pinned-text">{{ pinnedText }}</span>
          </button>
          <button
            v-if="canManagePinned"
            type="button"
            class="chat-page__pinned-clear"
            title="取消置顶"
            aria-label="取消置顶"
            @click="clearPinnedMessage"
          >
            <AppIcon name="close" :size="14" />
          </button>
        </div>
        <div class="chat-page__content" :class="{ 'chat-page__content--members': asideOpen }">
          <div class="chat-page__chat" :aria-busy="!activeLoaded">
            <div
              v-if="!activeLoaded"
              class="chat-page__loading"
              role="status"
              aria-label="消息加载中"
              aria-busy="true"
            >
              <div
                v-for="index in 3"
                :key="index"
                class="chat-page__loading-row"
                :class="{ 'chat-page__loading-row--self': index === 2 }"
              >
                <div class="ec-skeleton chat-page__loading-avatar" />
                <div class="chat-page__loading-lines">
                  <div class="ec-skeleton chat-page__loading-line" />
                  <div class="ec-skeleton chat-page__loading-line chat-page__loading-line--short" />
                </div>
              </div>
              <p v-if="slowLoading" class="ec-state-text ec-state-slow" role="status">仍在加载消息…</p>
            </div>
            <p v-if="activeId" class="chat-page__typing" aria-live="polite">{{ typingText }}</p>
            <div v-if="locateMode" class="chat-page__locate">
              <span>{{ locateLoading ? '正在定位历史消息…' : '正在查看历史位置（不是最新消息）' }}</span>
              <button type="button" class="chat-page__locate-back" @click="backToLatest">回到最新</button>
            </div>
            <MessageList
              ref="messageListRef"
              :messages="displayMessages"
              :pending="displayPending"
              :self-user-id="selfUserId"
              :conversation-id="activeId"
              :members="activeConversation.members"
              :is-group="isGroup"
              :peer-read-seq="peerReadSeqForDisplay"
              :show-read-receipt="!isGroup"
              :has-more="displayHasMore"
              :loading="activeLoadingHistory"
              :mention-names="mentionNames"
              :self-display-name="selfDisplayName"
              :locate-mode="locateMode"
              :locate-has-older="locateWindow?.hasOlder ?? false"
              :locate-has-newer="locateWindow?.hasNewer ?? false"
              :selecting="selecting"
              :selected-ids="selectedMessageIds"
              @load-more="loadMoreMessages"
              @retry="retryMessage"
              @near-bottom-change="onNearBottomChange"
              @preview-image="onPreviewImage"
              @open-actions="onOpenActions"
              @jump-to-message="onJumpToMessage"
              @toggle-select="toggleSelected"
            />
            <div v-if="selecting" class="chat-page__selection-bar" role="region" aria-label="多选操作">
              <span class="chat-page__selection-count">
                已选 {{ selectedMessageIds.length }}/{{ MAX_FORWARD_MESSAGES }} 条
              </span>
              <div class="chat-page__selection-actions">
                <button
                  type="button"
                  class="chat-page__selection-btn"
                  :disabled="!canForwardSelection"
                  @click="openMultiForward"
                >
                  转发
                </button>
                <button
                  type="button"
                  class="chat-page__selection-btn chat-page__selection-btn--ghost"
                  @click="exitSelecting"
                >
                  取消
                </button>
              </div>
            </div>
            <div v-if="!selecting" class="chat-page__composer">
              <!-- 悬浮表情面板：不占文档流，开合不会让聊天内容整体上移。 -->
              <EmojiPanel
                v-if="activeDrawer === 'emoji'"
                class="chat-page__emoji"
                @pick="onEmojiPick"
                @pick-custom="onPickCustomEmoji"
              />
              <SendBox
                ref="sendBoxRef"
                :conversation-id="activeId ?? ''"
                :initial-draft="activeId ? drafts.draftOf(activeId) : ''"
                :uploading="singleShotBusy || visibleQueueBusy"
                :upload-error="uploadError"
                :upload-items="visibleUploadItems"
                :emoji-open="activeDrawer === 'emoji'"
                :reply-to="replyTarget?.preview ?? null"
                :mention-candidates="mentionCandidates"
                @send="sendMessage"
                @draft-change="onDraftChange"
                @file-selected="handleFileSelected"
                @cancel-upload="cancelUpload"
                @retry-upload="retryUpload"
                @dismiss-upload="dismissUpload"
                @send-voice="handleVoiceRecorded"
                @voice-error="onVoiceError"
                @typing="sendTyping"
                @pick-emoji="toggleEmojiDrawer"
                @cancel-reply="cancelReply"
              />
            </div>
          </div>
          <!-- 801~1199 覆盖式成员面板的遮罩；≥1200 推挤与 ≤800 全宽时由 CSS 隐藏。 -->
          <div
            v-if="asideOpen"
            class="chat-page__members-mask"
            aria-hidden="true"
            @click="activeDrawer = null"
          />
          <!-- 会话侧栏（F-15 页签）：群聊「群资料与成员」「文件」，单聊仅「文件」；与聊天窗口同层。 -->
          <aside v-if="asideOpen && activeConversation" class="chat-page__members" aria-label="会话侧栏">
            <header class="chat-page__members-header">
              <div v-if="isGroup" class="chat-page__members-tabs" role="tablist" aria-label="侧栏页签">
                <button
                  type="button"
                  role="tab"
                  :aria-selected="asideTab === 'members'"
                  :class="[
                    'chat-page__members-tab',
                    { 'chat-page__members-tab--active': asideTab === 'members' },
                  ]"
                  @click="switchAsideTab('members')"
                >
                  群资料与成员
                </button>
                <button
                  type="button"
                  role="tab"
                  :aria-selected="asideTab === 'files'"
                  :class="[
                    'chat-page__members-tab',
                    { 'chat-page__members-tab--active': asideTab === 'files' },
                  ]"
                  @click="switchAsideTab('files')"
                >
                  文件
                </button>
              </div>
              <h2 v-else class="chat-page__members-title">文件</h2>
              <button
                type="button"
                class="chat-page__members-close"
                aria-label="关闭侧栏"
                @click="activeDrawer = null"
              >
                <AppIcon name="close" :size="18" />
              </button>
            </header>
            <div class="chat-page__members-body">
              <GroupMembersPanel
                v-if="asideTab === 'members' && isGroup"
                :conversation="activeConversation"
                :self-user-id="selfUserId"
                @updated="onGroupUpdated"
                @left="onLeftGroup"
                @open-chat="onOpenMemberChat"
              />
              <ConversationFilesPanel
                v-else-if="asideTab === 'files' && activeId"
                :conversation-id="activeId"
              />
            </div>
          </aside>
        </div>
      </template>
      <EmptyState
        v-else-if="activeId && conversations.loaded"
        icon="chat"
        title="会话不存在或无权访问"
        description="请从左侧选择已有会话，或到「联系人」重新发起聊天。"
      />
      <EmptyState
        v-else
        icon="chat"
        title="选择会话开始聊天"
        description="选择一个会话开始聊天"
      />
      <!-- 拖拽投放提示（F-06）：仅拖拽含文件且已选择会话时出现在聊天主区；移动端由 CSS 隐藏。 -->
      <div v-if="dragActive" class="chat-page__drop" aria-hidden="true">
        <div class="chat-page__drop-card">
          <AppIcon name="paperclip" :size="28" />
          <p class="chat-page__drop-text">松开以发送到当前会话</p>
          <p class="chat-page__drop-hint">支持图片、PDF、TXT、Office 文档、ZIP（单次最多 10 个）</p>
        </div>
      </div>
    </section>
    <ImagePreview
      :open="previewOpen"
      :items="previewItems"
      :index="previewIndex"
      :loading="previewLoading"
      @close="closePreview"
      @update:index="onPreviewIndex"
    />
    <MessageActionMenu
      :open="Boolean(actionMenu)"
      :message="actionMenuMessage"
      :self-user-id="selfUserId"
      :is-group="isGroup"
      :sender-name="actionMenuSenderName"
      :favorited="actionMenuMessage ? favorites.isFavorited(actionMenuMessage.messageId) : false"
      :can-manage-pinned="canManagePinned"
      :pinned="Boolean(
        actionMenuMessage &&
          pinnedMessage &&
          actionMenuMessage.messageId === pinnedMessage.messageId,
      )"
      :position="actionMenu ? { x: actionMenu.x, y: actionMenu.y } : { x: 0, y: 0 }"
      @select="onActionSelect"
      @close="actionMenu = null"
    />
    <AppDialog
      :open="Boolean(recallTarget)"
      title="撤回消息"
      message="撤回后双方都会看到「已撤回」且不可恢复，确定撤回吗？"
      tone="danger"
      confirm-text="撤回"
      :busy="recallSubmitting"
      @confirm="confirmRecall"
      @cancel="recallTarget = null"
    />
    <AppDialog
      :open="Boolean(editTarget)"
      title="编辑消息"
      confirm-text="保存"
      :busy="editSubmitting"
      wide
      @confirm="confirmEdit"
      @cancel="closeEditDialog"
    >
      <div class="chat-page__edit-form">
        <textarea
          v-model="editContent"
          class="chat-page__edit-input"
          :maxlength="MAX_TEXT_LENGTH"
          rows="4"
          aria-label="编辑消息内容"
        />
        <div class="chat-page__edit-meta">
          <span>{{ editContent.length }}/{{ MAX_TEXT_LENGTH }}</span>
        </div>
        <p v-if="editError" class="chat-page__edit-error" role="alert">{{ editError }}</p>
      </div>
    </AppDialog>
    <ForwardDialog
      :open="forwardOpen"
      :messages="forwardMessages"
      :self-user-id="selfUserId"
      :batch-id="forwardBatchId"
      @close="onForwardClose"
      @forwarded="onForwarded"
    />
  </main>
</template>

<style scoped>
.chat-page {
  display: grid;
  grid-template-columns: var(--ec-list-width) minmax(0, 1fr);
  height: 100%;
  min-height: 0;
  background: var(--ec-bg-canvas);
}

/* 拖拽投放覆盖层（F-06）：绝对定位覆盖整个聊天页，pointer-events:none 让事件继续落到根容器。 */
.chat-page__drop {
  position: absolute;
  inset: 0;
  z-index: 30;
  display: flex;
  align-items: center;
  justify-content: center;
  padding: var(--ec-space-6);
  background: var(--ec-drop-overlay);
  border: 2px dashed var(--ec-brand-500);
  border-radius: var(--ec-radius-panel);
  pointer-events: none;
}

.chat-page__drop-card {
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: var(--ec-space-2);
  max-width: 360px;
  padding: var(--ec-space-5) var(--ec-space-6);
  text-align: center;
  color: var(--ec-brand-600);
  background: var(--ec-surface);
  border-radius: var(--ec-radius-panel);
  box-shadow: var(--ec-shadow-lg);
}

.chat-page__drop-text {
  margin: 0;
  font-size: var(--ec-font-size-page-title);
  font-weight: var(--ec-font-weight-semibold);
}

.chat-page__drop-hint {
  margin: 0;
  color: var(--ec-text-secondary);
  font-size: var(--ec-font-size-secondary);
}

/* 移动端没有拖拽操作，不做投放提示。 */
@media (max-width: 800px) {
  .chat-page__drop {
    display: none;
  }
}

/* UI-01/UI-02/UI-04：801~1199 窗口（含 Electron 最小 960×640）会话列表收窄，避免挤压主区域。 */
@media (max-width: 1199px) {
  .chat-page {
    grid-template-columns: 260px minmax(0, 1fr);
  }

  .chat-page__members {
    width: 260px;
  }
}

/* F-36：群置顶消息条（位于顶栏与消息区之间，不参与消息滚动）。 */
.chat-page__pinned {
  display: flex;
  flex: none;
  align-items: center;
  gap: var(--ec-space-2);
  padding: var(--ec-space-1) var(--ec-space-2) var(--ec-space-1) var(--ec-space-3);
  background: var(--ec-brand-050);
  border-bottom: 1px solid var(--ec-border);
}

.chat-page__pinned-main {
  display: flex;
  flex: 1;
  align-items: center;
  gap: var(--ec-space-2);
  min-width: 0;
  padding: var(--ec-space-1) 0;
  color: var(--ec-brand-600);
  font-size: var(--ec-font-size-small);
  text-align: left;
  background: none;
  border: 0;
  cursor: pointer;
}

.chat-page__pinned-text {
  overflow: hidden;
  color: var(--ec-text-primary);
  text-overflow: ellipsis;
  white-space: nowrap;
}

.chat-page__pinned-clear {
  display: grid;
  flex: none;
  place-items: center;
  width: 24px;
  height: 24px;
  color: var(--ec-text-secondary);
  background: none;
  border: 0;
  border-radius: var(--ec-radius-sm, 6px);
  cursor: pointer;
}

.chat-page__pinned-clear:hover {
  color: var(--ec-text-primary);
  background: var(--ec-surface-hover);
}

.chat-page__main {
  position: relative;
  display: flex;
  flex-direction: column;
  min-width: 0;
  min-height: 0;
}

/* 聊天主区内容行：消息/输入区占剩余宽度，群成员面板固定宽度并列在右侧。 */
.chat-page__content {
  position: relative;
  display: flex;
  flex: 1;
  min-width: 0;
  min-height: 0;
}

.chat-page__chat {
  display: flex;
  flex: 1;
  flex-direction: column;
  min-width: 0;
  min-height: 0;
  /* 渐变上移到顶栏以下的整块聊天容器：typing 预留区、消息区、输入区共用同一背景，
     消除原来因 .message-list 独占渐变而在顶栏下方产生的浅色缝隙。 */
  background: var(--ec-chat-gradient);
}

/* 群成员面板（UI-03 追加）：与聊天窗口同层，位于聊天顶栏下方紧邻右侧，
   打开时聊天区收窄而非被遮罩覆盖；高度与内容行一致，不盖住顶栏「群资料」。 */
.chat-page__members {
  display: flex;
  flex: none;
  flex-direction: column;
  width: var(--ec-drawer-width);
  min-width: 0;
  min-height: 0;
  border-left: 1px solid var(--ec-border);
  background: var(--ec-surface);
}

.chat-page__members-header {
  display: flex;
  flex: none;
  gap: var(--ec-space-2);
  align-items: center;
  justify-content: space-between;
  padding: var(--ec-space-3) var(--ec-space-4);
  border-bottom: 1px solid var(--ec-border);
}

.chat-page__members-title {
  margin: 0;
  color: var(--ec-text-primary);
  font-size: 15px;
  font-weight: var(--ec-font-weight-semibold);
}

/* F-15：aside 页签（群聊：群资料与成员 / 文件）。 */
.chat-page__members-tabs {
  display: flex;
  gap: var(--ec-space-1);
  min-width: 0;
  padding: 3px;
  border-radius: var(--ec-radius-full);
  background: var(--ec-surface-hover);
}

.chat-page__members-tab {
  min-width: 0;
  min-height: 28px;
  padding: 0 var(--ec-space-3);
  border: none;
  border-radius: var(--ec-radius-full);
  background: transparent;
  color: var(--ec-text-secondary);
  font-size: var(--ec-font-size-secondary);
  white-space: nowrap;
  cursor: pointer;
}

.chat-page__members-tab--active {
  background: var(--ec-surface);
  color: var(--ec-text-primary);
  font-weight: var(--ec-font-weight-semibold);
  box-shadow: var(--ec-shadow-sm);
}

.chat-page__members-close {
  display: grid;
  flex: none;
  width: 28px;
  height: 28px;
  place-items: center;
  border: none;
  border-radius: var(--ec-radius-sm);
  background: transparent;
  color: var(--ec-text-secondary);
  cursor: pointer;
}

/* 悬停效果仅在鼠标精确指针设备生效，触屏不产生粘滞 hover。 */
@media (hover: hover) and (pointer: fine) {
.chat-page__members-close:hover {
  background: var(--ec-surface-hover);
  color: var(--ec-text-primary);
}
}

.chat-page__members-body {
  flex: 1;
  min-height: 0;
  padding: var(--ec-space-4);
  overflow-y: auto;
}

/* 801~1199 覆盖式成员面板的遮罩；默认隐藏（仅该档显示）。 */
.chat-page__members-mask {
  display: none;
}

/* 801~1199（窄桌面/小窗口）：成员面板改为覆盖式——绝对定位在聊天区右侧并加遮罩，
   聊天列保持全宽，避免四栏硬挤导致输入区过窄；点遮罩、Esc 或关闭按钮均可关闭。 */
@media (min-width: 801px) and (max-width: 1199px) {
  .chat-page__members-mask {
    position: absolute;
    inset: 0;
    z-index: 14;
    display: block;
    background: var(--ec-scrim);
  }

  .chat-page__members {
    position: absolute;
    top: 0;
    right: 0;
    bottom: 0;
    z-index: 15;
    width: var(--ec-drawer-width);
    box-shadow: var(--ec-shadow-lg);
  }
}

/* 底部输入区容器：为悬浮 Emoji 面板提供定位基准（面板绝对定位，不占文档流高度）。 */
.chat-page__composer {
  position: relative;
  flex: none;
}

/* F-40：多选操作条（选择时替换输入区；固定在消息区底部，不覆盖消息）。 */
.chat-page__selection-bar {
  display: flex;
  flex: none;
  gap: var(--ec-space-3);
  align-items: center;
  justify-content: space-between;
  padding: var(--ec-space-3) var(--ec-space-4);
  padding-bottom: calc(var(--ec-space-3) + env(safe-area-inset-bottom, 0px));
  background: var(--ec-surface);
  border-top: 1px solid var(--ec-border);
}

.chat-page__selection-count {
  color: var(--ec-text-secondary);
  font-size: var(--ec-font-size-secondary);
}

.chat-page__selection-actions {
  display: flex;
  gap: var(--ec-space-2);
}

.chat-page__selection-btn {
  min-width: 72px;
  height: 34px;
  padding: 0 var(--ec-space-4);
  color: var(--ec-text-inverse);
  font-weight: 600;
  background: var(--ec-brand-600);
  border: none;
  border-radius: var(--ec-radius-input);
  cursor: pointer;
}

.chat-page__selection-btn:disabled {
  cursor: not-allowed;
  opacity: 0.5;
}

.chat-page__selection-btn--ghost {
  color: var(--ec-text-secondary);
  font-weight: 400;
  background: transparent;
  border: 1px solid var(--ec-border);
}

/* 悬浮表情面板：锚定在发送框上方，开合不会改变消息区高度、不挤动聊天内容。 */
.chat-page__emoji {
  position: absolute;
  bottom: calc(100% + var(--ec-space-2));
  left: var(--ec-space-4);
  z-index: 20;
}

/* U-09：慢反馈提示走全局 .ec-state-text / .ec-state-slow，不再单独定义。 */

/* U-01：切会话/首屏的伪消息骨架，行高与真实消息行接近，避免加载完成时跳动。 */
.chat-page__loading {
  display: flex;
  flex-direction: column;
  gap: var(--ec-space-5);
  padding: var(--ec-space-6) var(--ec-space-4);
}

.chat-page__loading-row {
  display: flex;
  gap: var(--ec-space-3);
  align-items: flex-start;
}

.chat-page__loading-row--self {
  flex-direction: row-reverse;
  margin-left: auto;
}

.chat-page__loading-avatar {
  width: 40px;
  height: 40px;
  border-radius: var(--ec-radius-full);
}

.chat-page__loading-lines {
  display: flex;
  flex-direction: column;
  gap: var(--ec-space-2);
  width: min(280px, 60%);
}

.chat-page__loading-line {
  height: 14px;
}

.chat-page__loading-line--short {
  width: 60%;
}

/* 预留固定高度，避免「输入中」提示出现/消失时消息列表明显跳动（M3-09）。 */
.chat-page__typing {
  margin: 0;
  min-height: 18px;
  padding: 0 var(--ec-space-4);
  color: var(--ec-text-secondary);
  font-size: var(--ec-font-size-secondary);
  line-height: 18px;
}

/* ≤800：移动单栏布局——无会话显示列表，有会话显示聊天详情（返回按钮回到列表）。 */
@media (max-width: 800px) {
  .chat-page {
    display: flex;
    flex-direction: column;
  }

  .chat-page__list {
    width: 100%;
    border-right: none;
  }

  .chat-page--conversation .chat-page__list {
    display: none;
  }

  .chat-page__main {
    flex: 1;
    min-height: 0;
  }

  .chat-page:not(.chat-page--conversation) .chat-page__main {
    display: none;
  }

  /* 群成员面板在窄屏占满全宽：顶栏保留（可关闭面板/返回），聊天列让位。 */
  .chat-page--members .chat-page__list {
    display: none;
  }

  .chat-page__content--members .chat-page__chat {
    display: none;
  }

  .chat-page__content--members .chat-page__members {
    flex: 1;
    width: 100%;
    border-left: none;
  }

  .chat-page__emoji {
    right: var(--ec-space-3);
    left: var(--ec-space-3);
    width: auto;
  }
}

/* UI-04 批次4 / F-15：移动端侧栏关闭按钮与页签触控热区。 */
@media (max-width: 800px) {
  .chat-page__members-close {
    min-width: 44px;
    min-height: 44px;
  }

  .chat-page__members-tab {
    min-height: 44px;
  }
}

/* F-18：编辑消息弹窗内的多行输入与计数。 */
.chat-page__edit-form {
  display: flex;
  flex-direction: column;
  gap: var(--ec-space-2);
}

.chat-page__edit-input {
  width: 100%;
  min-height: 120px;
  padding: var(--ec-space-2) var(--ec-space-3);
  border: 1px solid var(--ec-border);
  border-radius: var(--ec-radius-input);
  background: var(--ec-surface);
  color: var(--ec-text-primary);
  font: inherit;
  line-height: 1.5;
  resize: vertical;
}

.chat-page__edit-input:focus-visible {
  border-color: var(--ec-brand-500);
  outline: var(--ec-focus-width) solid var(--ec-focus-ring);
  outline-offset: 1px;
}

.chat-page__edit-meta {
  display: flex;
  justify-content: flex-end;
  color: var(--ec-text-tertiary);
  font-size: var(--ec-font-size-small);
}

.chat-page__edit-error {
  margin: 0;
  color: var(--ec-danger);
  font-size: var(--ec-font-size-secondary);
}
.chat-page__locate {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: var(--ec-space-3);
  margin: 0 var(--ec-space-4);
  padding: var(--ec-space-2) var(--ec-space-3);
  background: var(--ec-brand-050);
  color: var(--ec-brand-600);
  border-radius: var(--ec-radius-panel);
  font-size: 12px;
}

.chat-page__locate-back {
  flex: none;
  padding: 4px var(--ec-space-3);
  border: 1px solid var(--ec-brand-600);
  border-radius: var(--ec-radius-input);
  background: var(--ec-surface);
  color: var(--ec-brand-600);
  font-size: 12px;
  cursor: pointer;
}

.chat-page__locate-back:hover {
  background: var(--ec-brand-600);
  color: var(--ec-text-inverse);
}
</style>
