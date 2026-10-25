<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import ConversationItem from '../components/ConversationItem.vue'
import EmptyState from '../components/EmptyState.vue'
import GroupMembersPanel from '../components/GroupMembersPanel.vue'
import MessageList from '../components/MessageList.vue'
import SendBox from '../components/SendBox.vue'
import { filesApi } from '../api/files'
import { isImageFile, validateAttachment } from '../core/attachments'
import { ApiError } from '../core/http'
import { compressImageFile } from '../core/imageCompress'
import { shouldAdvanceRead } from '../core/readReceipt'
import { useAuthStore } from '../stores/auth'
import { useConversationsStore } from '../stores/conversations'
import { useMessagesStore } from '../stores/messages'
import type { Conversation } from '../types/frames'

// 聊天主页面（M2-04 会话列表 + M2-05 消息流 + M3-08 群聊）：左侧会话列表，右侧消息流与发送框。
// 规则：页面只做编排，状态走 stores/，连接走 core/ws.ts，不直接 new WebSocket / fetch。
const route = useRoute()
const router = useRouter()
const auth = useAuthStore()
const conversations = useConversationsStore()
const messages = useMessagesStore()

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
// 群成员管理面板（仅群聊可用）。
const showMembersPanel = ref(false)

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

// 切换会话：首次进入拉最新一页历史（M2-05）；已加载过直接用缓存，后续新消息由 message.new 合并。
watch(
  activeId,
  (conversationId) => {
    showMembersPanel.value = false
    atBottom.value = true
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
  showMembersPanel.value = false
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
  document.addEventListener('visibilitychange', markActiveConversationRead)
  await conversations.restoreFromCache()
  await messages.restoreFromCache()
})

onBeforeUnmount(() => {
  document.removeEventListener('visibilitychange', markActiveConversationRead)
})

function selectConversation(conversationId: string): void {
  void router.push({ name: 'chat', params: { conversationId } })
}

function sendMessage(text: string): void {
  if (!activeId.value || !selfUserId.value) return
  messages.sendText(activeId.value, text, selfUserId.value)
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
  showMembersPanel.value = false
  conversations.handleRemovedConversations([conversationId])
}

// 附件上传（M3-07）：校验 → 图片压缩 → 上传 → 以 image/file 消息发送；上传期间禁止重复选择。
const uploadBusy = ref(false)
const uploadError = ref<string | null>(null)

// 退出登录/切换账号：关闭群成员面板并清空页内提示（面板内部状态随 v-if 卸载重置）。
watch(selfUserId, () => {
  showMembersPanel.value = false
  pageNotice.value = ''
  uploadError.value = null
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
  <main class="chat-page">
    <aside class="chat-page__sidebar">
      <header class="chat-page__sidebar-header">
        <span>消息</span>
        <RouterLink class="chat-page__new" to="/contacts">发起聊天</RouterLink>
      </header>

      <p v-if="conversations.loading" class="chat-page__hint">加载中…</p>
      <p v-else-if="conversations.sorted.length === 0" class="chat-page__hint">
        还没有会话，去「通讯录」搜索用户发起单聊或创建群聊。
      </p>
      <nav v-else class="chat-page__list" aria-label="会话列表">
        <ConversationItem
          v-for="conversation in conversations.sorted"
          :key="conversation.conversationId"
          :conversation="conversation"
          :self-user-id="selfUserId"
          :active="conversation.conversationId === activeId"
          :unread="conversation.conversationId === activeId ? 0 : conversations.unreadOf(conversation.conversationId)"
          @select="selectConversation"
        />
      </nav>
    </aside>

    <section class="chat-page__main">
      <p v-if="pageNotice" class="chat-page__notice" role="status">
        <span>{{ pageNotice }}</span>
        <button type="button" class="chat-page__notice-close" @click="pageNotice = ''">知道了</button>
      </p>

      <template v-if="activeConversation">
        <header class="chat-page__main-header">
          <span class="chat-page__main-title" :title="activeTitle">{{ activeTitle }}</span>
          <span v-if="isGroup" class="chat-page__member-count">{{ memberCount }} 人</span>
          <button
            v-if="isGroup"
            type="button"
            class="chat-page__members-button"
            @click="showMembersPanel = true"
          >
            群成员
          </button>
        </header>
        <p v-if="!activeLoaded" class="chat-page__main-hint">消息加载中…</p>
        <MessageList
          :messages="activeMessages"
          :pending="activePending"
          :self-user-id="selfUserId"
          :conversation-id="activeId"
          :peer-read-seq="peerReadSeqForDisplay"
          :show-read-receipt="!isGroup"
          :has-more="activeHasMore"
          :loading="activeLoadingHistory"
          @load-more="loadMoreMessages"
          @retry="retryMessage"
          @near-bottom-change="onNearBottomChange"
        />
        <SendBox
          :uploading="uploadBusy"
          :upload-error="uploadError"
          @send="sendMessage"
          @file-selected="handleFileSelected"
        />
        <GroupMembersPanel
          v-if="showMembersPanel && activeConversation.type === 'group'"
          :conversation="activeConversation"
          :self-user-id="selfUserId"
          @close="showMembersPanel = false"
          @updated="onGroupUpdated"
          @left="onLeftGroup"
        />
      </template>
      <EmptyState
        v-else-if="activeId && conversations.loaded"
        title="会话不存在或无权访问"
        description="请从左侧选择已有会话，或到「通讯录」重新发起聊天。"
      />
      <EmptyState
        v-else
        title="选择会话开始聊天"
        description="从左侧选择会话，或到「通讯录」搜索用户发起单聊、创建群聊。"
      />
    </section>
  </main>
</template>

<style scoped>
.chat-page {
  display: grid;
  grid-template-columns: 280px 1fr;
  height: 100%;
  background: var(--color-surface);
}

.chat-page__sidebar {
  display: flex;
  flex-direction: column;
  overflow: hidden;
  border-right: 1px solid var(--color-border);
}

.chat-page__sidebar-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  height: var(--header-height);
  padding: 0 var(--space-4);
  font-weight: 600;
  border-bottom: 1px solid var(--color-border);
}

.chat-page__new {
  color: var(--color-primary);
  font-size: 13px;
  font-weight: 500;
  text-decoration: none;
}

.chat-page__list {
  flex: 1;
  overflow-y: auto;
}

.chat-page__hint {
  margin: var(--space-4);
  color: var(--color-text-secondary);
  font-size: 13px;
}

.chat-page__main {
  display: flex;
  flex-direction: column;
  min-height: 0;
}

.chat-page__notice {
  display: flex;
  gap: var(--space-2);
  align-items: center;
  justify-content: space-between;
  margin: 0;
  padding: var(--space-2) var(--space-4);
  background: var(--color-primary-soft);
  color: var(--color-primary);
  font-size: 13px;
}

.chat-page__notice-close {
  border: none;
  background: transparent;
  color: var(--color-primary);
  font-size: 12px;
  cursor: pointer;
}

.chat-page__main-header {
  display: flex;
  gap: var(--space-2);
  align-items: center;
  height: var(--header-height);
  padding: 0 var(--space-4);
  font-weight: 600;
  border-bottom: 1px solid var(--color-border);
}

.chat-page__main-title {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.chat-page__member-count {
  flex: none;
  padding: 2px 8px;
  border-radius: var(--radius-full);
  background: var(--color-surface-hover);
  color: var(--color-text-secondary);
  font-size: 12px;
  font-weight: 400;
}

.chat-page__members-button {
  flex: none;
  margin-left: auto;
  padding: 4px 10px;
  border: 1px solid var(--color-border);
  border-radius: var(--radius-sm);
  background: transparent;
  color: var(--color-primary);
  font-size: 12px;
  cursor: pointer;
}

.chat-page__main-hint {
  margin: var(--space-4);
  color: var(--color-text-secondary);
  font-size: 13px;
  text-align: center;
}
</style>
