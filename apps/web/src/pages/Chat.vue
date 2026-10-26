<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import AppIcon from '../components/AppIcon.vue'
import ChatHeader from '../components/ChatHeader.vue'
import ConversationList from '../components/ConversationList.vue'
import EmojiPanel from '../components/EmojiPanel.vue'
import EmptyState from '../components/EmptyState.vue'
import GroupMembersPanel from '../components/GroupMembersPanel.vue'
import MessageList from '../components/MessageList.vue'
import NoticeBar from '../components/NoticeBar.vue'
import SendBox from '../components/SendBox.vue'
import { filesApi } from '../api/files'
import { isImageFile, validateAttachment } from '../core/attachments'
import type { ConversationFilter } from '../core/chatLayout'
import { ApiError } from '../core/http'
import { compressImageFile } from '../core/imageCompress'
import { shouldAdvanceRead } from '../core/readReceipt'
import { TypingThrottle } from '../core/typingThrottle'
import { chatWs, type WsState } from '../core/ws'
import { useAuthStore } from '../stores/auth'
import { useConversationsStore } from '../stores/conversations'
import { useMessagesStore } from '../stores/messages'
import { usePresenceStore } from '../stores/presence'
import { useTypingStore } from '../stores/typing'
import type { Conversation } from '../types/frames'

// 聊天主页面（M2-04 会话列表 + M2-05 消息流 + M3-08 群聊）：左侧会话列表，右侧消息流与发送框。
// 规则：页面只做编排，状态走 stores/，连接走 core/ws.ts，不直接 new WebSocket / fetch。
const route = useRoute()
const router = useRouter()
const auth = useAuthStore()
const conversations = useConversationsStore()
const messages = useMessagesStore()
const typing = useTypingStore()
const presence = usePresenceStore()
/** typing 帧的客户端节流（同一会话 3 秒最多一次，protocol.md §4.7）。 */
const typingThrottle = new TypingThrottle()

const selfUserId = computed(() => auth.user?.userId ?? '')
const activeId = computed(() =>
  typeof route.params.conversationId === 'string' ? route.params.conversationId : null,
)
const activeConversation = computed(() =>
  activeId.value ? conversations.getById(activeId.value) : undefined,
)
const isGroup = computed(() => activeConversation.value?.type === 'group')
const memberCount = computed(() => activeConversation.value?.members.length ?? 0)
const activeTitle = computed(() => {
  const conversation = activeConversation.value
  if (!conversation) return ''
  if (conversation.type === 'group') return conversation.title || '群聊'
  const other = conversation.members.find((member) => member.userId !== selfUserId.value)
  return other?.displayName ?? conversation.title ?? '单聊会话'
})

// 页内提示（M3-08：被移出/退出群聊等）；不引第三方 toast。
const pageNotice = ref('')
// 右侧面板（UI-02，计划 §5.6）：同一时刻最多一个；emoji 由输入框表情按钮触发，group 由群聊顶栏触发。
const activeDrawer = ref<'emoji' | 'group' | null>(null)
/** 群成员面板改为与聊天窗口同层的右侧栏（内嵌 grid 列，不再用遮罩抽屉覆盖聊天区）。 */
const membersOpen = computed(
  () =>
    Boolean(activeId.value) &&
    activeConversation.value?.type === 'group' &&
    activeDrawer.value === 'group',
)
/** 发送框实例：Emoji 面板选择后调用其 insertEmoji，让插入落在当前光标处（草稿不丢）。 */
const sendBoxRef = ref<{ insertEmoji: (emoji: string) => void } | null>(null)

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
    return
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

// 已读回执（M3-09）：只有停留在底部（已看到最新消息）才推进已读；
// 单聊展示对端已读状态，群聊 v1 无已读详情（协议 §4.6）。
/** 消息列表是否停留在底部（由 MessageList 的 near-bottom-change 驱动）。 */
const atBottom = ref(true)
/** 单聊对端用户 ID（群聊为空，不展示已读回执）。 */
const peerUserId = computed(() => {
  const conversation = activeConversation.value
  if (!conversation || conversation.type !== 'direct') return undefined
  return conversation.members.find((member) => member.userId !== selfUserId.value)?.userId
})
/** 单聊对端已上报的已读 seq（来自 read.updated；无记录时 undefined，展示「未读」）。 */
const peerReadSeqForDisplay = computed(() =>
  activeId.value && peerUserId.value
    ? conversations.peerReadSeqOf(activeId.value, peerUserId.value)
    : undefined,
)

/** 单聊对端在线状态（M3-09）：undefined 表示未知（v1 无初始查询，不显示成离线）。 */
const peerPresence = computed(() => presence.stateOf(peerUserId.value))

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
  await conversations.restoreFromCache()
  await messages.restoreFromCache()
})

onBeforeUnmount(() => {
  offWsState?.()
  offWsState = null
  if (recoveredTimer) {
    clearTimeout(recoveredTimer)
    recoveredTimer = null
  }
  document.removeEventListener('visibilitychange', markActiveConversationRead)
  document.removeEventListener('mousedown', onEmojiOutsidePointer)
  document.removeEventListener('keydown', onEmojiEscape)
})

function selectConversation(conversationId: string): void {
  void router.push({ name: 'chat', params: { conversationId } })
}

/** ≤800 移动布局：从聊天详情返回会话列表（不改变桌面端两栏行为）。 */
function backToList(): void {
  void router.push({ name: 'chat' })
}

function sendMessage(text: string): void {
  if (!activeId.value || !selfUserId.value) return
  messages.sendText(activeId.value, text, selfUserId.value)
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

// 表情面板悬浮在发送框上方（UI-02）：绝对定位不参与文档流，开合不挤动聊天内容；
// 点击面板外任意位置或按 Esc 收起（不需要面板内关闭按钮）。
function toggleEmojiDrawer(): void {
  activeDrawer.value = activeDrawer.value === 'emoji' ? null : 'emoji'
}

/** 点击表情面板内部或触发按钮不算「点击外部」（触发按钮自身负责开合切换）。 */
function isEmojiLayerTarget(target: EventTarget | null): boolean {
  return target instanceof Element && Boolean(target.closest('.ec-emoji-panel, [data-emoji-trigger]'))
}

function onEmojiOutsidePointer(event: MouseEvent): void {
  if (!isEmojiLayerTarget(event.target)) activeDrawer.value = null
}

function onEmojiEscape(event: KeyboardEvent): void {
  if (event.key === 'Escape') activeDrawer.value = null
}

watch(activeDrawer, (drawer) => {
  document.removeEventListener('mousedown', onEmojiOutsidePointer)
  document.removeEventListener('keydown', onEmojiEscape)
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
  activeDrawer.value = activeDrawer.value === 'group' ? null : 'group'
}

// Emoji 面板选择：插入到发送框光标处，不发送、不关闭面板（可连续选多个）。
function onEmojiPick(emoji: string): void {
  sendBoxRef.value?.insertEmoji(emoji)
}

// 附件上传（M3-07）：校验 → 图片压缩 → 上传 → 以 image/file 消息发送；上传期间禁止重复选择。
const uploadBusy = ref(false)
const uploadError = ref<string | null>(null)

// 退出登录/切换账号：关闭群成员面板并清空页内提示（面板内部状态随 v-if 卸载重置）；
// 同时清空「输入中」状态与节流记录，避免沿用上一位用户的数据（M3-09）。
watch(selfUserId, () => {
  activeDrawer.value = null
  pageNotice.value = ''
  uploadError.value = null
  typing.clear()
  typingThrottle.reset()
})

async function handleFileSelected(file: File): Promise<void> {
  const conversationId = activeId.value
  const senderId = selfUserId.value
  if (!conversationId || !senderId || uploadBusy.value) return

  const validation = validateAttachment(file)
  if (!validation.ok) {
    uploadError.value = validation.reason
    return
  }

  uploadBusy.value = true
  uploadError.value = null
  try {
    // 图片发送前压缩（最长边 1280、约 ≤300KB）；gif 由 compressImageFile 原样返回。
    const payload = isImageFile(file) ? await compressImageFile(file) : file
    const recheck = validateAttachment(payload)
    if (!recheck.ok) {
      uploadError.value = recheck.reason
      return
    }

    const uploaded = await filesApi.upload(payload)
    const sent = messages.sendFileMessage(
      conversationId,
      isImageFile(payload) ? 'image' : 'file',
      {
        fileId: uploaded.fileId,
        name: uploaded.name,
        size: uploaded.size,
        mime: uploaded.mime,
      },
      senderId,
    )
    if (!sent) uploadError.value = '消息发送失败，请重试'
  } catch (error) {
    uploadError.value = error instanceof ApiError ? error.message : '文件上传失败，请重试'
  } finally {
    uploadBusy.value = false
  }
}
</script>

<template>
  <main
    class="chat-page"
    :class="{ 'chat-page--conversation': Boolean(activeId), 'chat-page--members': membersOpen }"
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

    <section class="chat-page__main">
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
          :show-back="true"
          @open-group="toggleGroupDrawer"
          @back="backToList"
        />
        <p v-if="!activeLoaded" class="chat-page__main-hint">消息加载中…</p>
        <p v-if="activeId" class="chat-page__typing" aria-live="polite">{{ typingText }}</p>
        <MessageList
          :messages="activeMessages"
          :pending="activePending"
          :self-user-id="selfUserId"
          :conversation-id="activeId"
          :members="activeConversation.members"
          :is-group="isGroup"
          :peer-read-seq="peerReadSeqForDisplay"
          :show-read-receipt="!isGroup"
          :has-more="activeHasMore"
          :loading="activeLoadingHistory"
          @load-more="loadMoreMessages"
          @retry="retryMessage"
          @near-bottom-change="onNearBottomChange"
        />
        <div class="chat-page__composer">
          <!-- 悬浮表情面板：不占文档流，开合不会让聊天内容整体上移。 -->
          <EmojiPanel
            v-if="activeDrawer === 'emoji'"
            class="chat-page__emoji"
            @pick="onEmojiPick"
          />
          <SendBox
            ref="sendBoxRef"
            :uploading="uploadBusy"
            :upload-error="uploadError"
            :emoji-open="activeDrawer === 'emoji'"
            @send="sendMessage"
            @file-selected="handleFileSelected"
            @typing="sendTyping"
            @pick-emoji="toggleEmojiDrawer"
          />
        </div>
      </template>
      <EmptyState
        v-else-if="activeId && conversations.loaded"
        icon="chat"
        title="会话不存在或无权访问"
        description="请从左侧选择已有会话，或到「通讯录」重新发起聊天。"
      />
      <EmptyState
        v-else
        icon="chat"
        title="选择会话开始聊天"
        description="从左侧选择会话，或到「通讯录」搜索用户发起单聊、创建群聊。"
      />
    </section>

    <!-- 群成员面板：与聊天窗口同层的右侧栏（内嵌 grid 列，非遮罩抽屉），默认隐藏。 -->
    <aside v-if="membersOpen && activeConversation" class="chat-page__members" aria-label="群成员">
      <header class="chat-page__members-header">
        <h2 class="chat-page__members-title">群成员</h2>
        <button
          type="button"
          class="chat-page__members-close"
          aria-label="关闭群成员面板"
          @click="activeDrawer = null"
        >
          <AppIcon name="close" :size="18" />
        </button>
      </header>
      <div class="chat-page__members-body">
        <GroupMembersPanel
          :conversation="activeConversation"
          :self-user-id="selfUserId"
          @updated="onGroupUpdated"
          @left="onLeftGroup"
        />
      </div>
    </aside>
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

/* UI-01/UI-02：801~1099 窗口（含 Electron 最小 960×640）会话列表收窄，避免挤压主区域。 */
@media (max-width: 1099px) {
  .chat-page {
    grid-template-columns: 260px minmax(0, 1fr);
  }

  .chat-page--members {
    grid-template-columns: 260px minmax(0, 1fr) 260px;
  }
}

.chat-page__main {
  display: flex;
  flex-direction: column;
  min-width: 0;
  min-height: 0;
}

/* 群成员面板（UI-03 追加）：与聊天窗口同层的第三列，打开时聊天区收窄而非被遮罩覆盖。 */
.chat-page--members {
  grid-template-columns: var(--ec-list-width) minmax(0, 1fr) var(--ec-drawer-width);
}

.chat-page__members {
  display: flex;
  flex-direction: column;
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

.chat-page__members-close:hover {
  background: var(--ec-surface-hover);
  color: var(--ec-text-primary);
}

.chat-page__members-body {
  flex: 1;
  min-height: 0;
  padding: var(--ec-space-4);
  overflow-y: auto;
}

/* 底部输入区容器：为悬浮 Emoji 面板提供定位基准（面板绝对定位，不占文档流高度）。 */
.chat-page__composer {
  position: relative;
  flex: none;
}

/* 悬浮表情面板：锚定在发送框上方，开合不会改变消息区高度、不挤动聊天内容。 */
.chat-page__emoji {
  position: absolute;
  bottom: calc(100% + var(--ec-space-2));
  left: var(--ec-space-4);
  z-index: 20;
}

.chat-page__main-hint {
  margin: var(--ec-space-4);
  color: var(--ec-text-secondary);
  font-size: var(--ec-font-size-secondary);
  text-align: center;
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

  /* 群成员面板在窄屏占满全宽：聊天区让位，关闭后回到聊天；顶部返回按钮仍可回列表。 */
  .chat-page--members .chat-page__list,
  .chat-page--members .chat-page__main {
    display: none;
  }

  .chat-page__members {
    flex: 1;
    width: 100%;
    min-height: 0;
    border-left: none;
  }

  .chat-page__emoji {
    right: var(--ec-space-3);
    left: var(--ec-space-3);
    width: auto;
  }
}
</style>
