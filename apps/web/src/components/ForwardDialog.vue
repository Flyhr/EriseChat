<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import AppDialog from './AppDialog.vue'
import Avatar from './Avatar.vue'
import EmptyState from './EmptyState.vue'
import { otherMemberOf } from '../core/conversationTitle'
import { filterForwardTargets, forwardPreviewOf } from '../core/forward'
import { resolveApiBase } from '../core/config'
import { friendlyError } from '../core/errorMessages'
import { getAccessToken } from '../core/http'
import { showToast } from '../core/toast'
import { messagesApi } from '../api/messages'
import { useConversationsStore } from '../stores/conversations'
import { useMessagesStore } from '../stores/messages'
import type { Conversation, Message } from '../types/frames'

// F-40：多选/单条消息转发弹窗（AppDialog wide）。
// 目标会话排除来源会话；文本/图片/文件/语音统一走服务端 `/api/messages/forward`，
// 服务端复用原 files.id 建立目标会话的新消息引用，客户端不重复上传附件。
// 每条目标消息固定一个 clientMsgId：重试只重跑未成功的项，幂等由服务端保证。
// 一次提交期间锁定目标会话与账号/服务器上下文；目标或上下文要变，必须显式「开始新的一批」。
const MAX_FORWARD_COUNT = 20
const CONTEXT_CHANGED_MESSAGE = '登录账号或服务器已变更，已停止本批转发。'

interface ForwardItem {
  message: Message
  clientMsgId: string
  status: 'pending' | 'sent' | 'failed'
  error?: string
}

/** 批次开始时的身份快照：用于阻止切换账号/服务器后剩余消息以新身份发出。 */
interface BatchIdentity {
  userId: string
  token: string | null
  apiBase: string
}

const props = defineProps<{
  open: boolean
  messages: readonly Message[]
  selfUserId: string
  /** 由父组件在开始新一批时递增；关闭/重新打开同一批不会重置幂等键与失败项。 */
  batchId: number
}>()

const emit = defineEmits<{
  close: [keepBatch: boolean]
  forwarded: [result: { success: number; failed: number }]
}>()

const conversations = useConversationsStore()

/** 目标会话头像：群聊用群头像，单聊用对端成员头像（与聊天列表/顶栏一致）。 */
function avatarOf(conversation: Conversation): string | undefined {
  if (conversation.type === 'group') return conversation.avatarFileId ?? undefined
  return otherMemberOf(conversation, props.selfUserId)?.avatarFileId
}
const messagesStore = useMessagesStore()

const query = ref('')
const selectedId = ref('')
const running = ref(false)
const items = ref<ForwardItem[]>([])
const result = ref<{ success: number; failed: number } | null>(null)
const lastError = ref('')
const lockedTargetId = ref<string | null>(null)
const lockedTargetTitle = ref('')
const batchIdentity = ref<BatchIdentity | null>(null)
const contextLost = ref(false)

const selected = computed(
  () => targets.value.find((conversation) => conversation.conversationId === selectedId.value) ?? null,
)
const targets = computed(() =>
  filterForwardTargets(
    conversations.sorted,
    props.messages[0]?.conversationId ?? '',
    query.value,
  ),
)
const chosenTargetId = computed(() => lockedTargetId.value ?? (selectedId.value || ''))
const targetLocked = computed(() => lockedTargetId.value !== null)
const pendingCount = computed(() => items.value.filter((item) => item.status === 'pending').length)
const sentCount = computed(() => items.value.filter((item) => item.status === 'sent').length)
const failedCount = computed(() => items.value.filter((item) => item.status === 'failed').length)
const confirmText = computed(() => {
  if (contextLost.value) return '开始新的一批'
  return result.value && result.value.failed > 0 ? '重试失败项' : '转发'
})
const keepBatchOnClose = computed(
  () => !contextLost.value && (result.value?.failed ?? 0) > 0,
)

/** 读取当前账号、令牌与服务器地址；每次请求前后都重新读取用于比对。 */
function captureIdentity(): BatchIdentity {
  return {
    userId: props.selfUserId,
    token: getAccessToken(),
    apiBase: resolveApiBase(),
  }
}

function identityChanged(): boolean {
  const initial = batchIdentity.value
  if (!initial) return false
  const current = captureIdentity()
  return (
    current.userId !== initial.userId ||
    current.token !== initial.token ||
    current.apiBase !== initial.apiBase
  )
}

function initBatch(): void {
  items.value = props.messages.slice(0, MAX_FORWARD_COUNT).map((message) => ({
    message,
    // 幂等键在批次开始时生成，关闭再打开、部分失败重试都复用它。
    clientMsgId: crypto.randomUUID(),
    status: 'pending',
  }))
  query.value = ''
  selectedId.value = ''
  running.value = false
  result.value = null
  lastError.value = ''
  lockedTargetId.value = null
  lockedTargetTitle.value = ''
  batchIdentity.value = null
  contextLost.value = false
}

watch(
  () => props.batchId,
  () => initBatch(),
  { immediate: true },
)

/** 显式开始新的一批：重新生成幂等键、解锁目标、清空本批结果。 */
function startNewBatch(): void {
  initBatch()
}

function markContextLost(): void {
  contextLost.value = true
  lastError.value = CONTEXT_CHANGED_MESSAGE
  for (const item of items.value) {
    if (item.status === 'pending') {
      item.status = 'failed'
      item.error = CONTEXT_CHANGED_MESSAGE
    }
  }
}

async function confirmForward(): Promise<void> {
  if (contextLost.value) {
    startNewBatch()
    return
  }
  if (running.value) return
  const targetId = chosenTargetId.value
  if (!targetId) {
    showToast('请选择转发目标会话', 'error')
    return
  }
  const waiting = items.value.filter((item) => item.status !== 'sent')
  if (waiting.length === 0) return

  if (!batchIdentity.value) {
    batchIdentity.value = captureIdentity()
  } else if (identityChanged()) {
    markContextLost()
    return
  }

  // 首次提交后锁定目标，避免部分失败重试把同一批消息分发到两个会话。
  if (!lockedTargetId.value) {
    lockedTargetId.value = targetId
    lockedTargetTitle.value =
      targets.value.find((conversation) => conversation.conversationId === targetId)?.title ??
      selected.value?.title ??
      '已选会话'
  }

  running.value = true
  lastError.value = ''
  for (const item of waiting) {
    if (contextLost.value) break
    // 发下一条前核对：账号/令牌/服务器任一变化就停止，不把剩余消息发到新上下文。
    if (identityChanged()) {
      item.error = CONTEXT_CHANGED_MESSAGE
      markContextLost()
      break
    }
    try {
      const forwarded = await messagesApi.forward({
        messageId: item.message.messageId,
        targetConversationId: targetId,
        clientMsgId: item.clientMsgId,
      })
      // 请求期间账号/令牌/服务器可能已变化；客户端无法保证服务端未提交，这里只停止后续请求、
      // 丢弃迟到结果，不把它合并进当前账号的消息 store。
      if (identityChanged()) {
        item.status = 'failed'
        item.error = CONTEXT_CHANGED_MESSAGE
        markContextLost()
        break
      }
      item.status = 'sent'
      item.error = undefined
      // 广播通常已经带回这条消息；这里兜底 WS 断开/重连场景，未加载的会话不写入以免打乱分页状态。
      if (messagesStore.isLoaded(targetId)) {
        messagesStore.merge(targetId, [forwarded])
      }
    } catch (cause) {
      if (identityChanged()) {
        item.status = 'failed'
        item.error = CONTEXT_CHANGED_MESSAGE
        markContextLost()
        break
      }
      item.status = 'failed'
      item.error = friendlyError(cause)
      lastError.value = item.error
    }
  }

  running.value = false
  result.value = { success: sentCount.value, failed: failedCount.value }
  if (result.value.failed === 0) {
    const success = result.value.success
    showToast(success > 1 ? `已转发 ${success} 条` : '已转发')
    emit('forwarded', { success, failed: 0 })
    emit('close', false)
  }
}
</script>

<template>
  <AppDialog
    :open="open"
    :title="items.length > 1 ? `转发 ${items.length} 条消息` : '转发消息'"
    :confirm-text="confirmText"
    :busy="running"
    wide
    @confirm="confirmForward"
    @cancel="emit('close', keepBatchOnClose)"
  >
    <div class="forward-dialog">
      <ul v-if="items.length > 0" class="forward-dialog__previews">
        <li v-for="item in items" :key="item.clientMsgId" class="forward-dialog__preview-row">
          <span class="forward-dialog__preview-text">{{ forwardPreviewOf(item.message) }}</span>
          <span
            class="forward-dialog__status"
            :class="{
              'forward-dialog__status--sent': item.status === 'sent',
              'forward-dialog__status--failed': item.status === 'failed',
            }"
          >
            {{ item.status === 'sent' ? '已发送' : item.status === 'failed' ? '失败' : '待发送' }}
          </span>
        </li>
      </ul>

      <p v-if="result" class="forward-dialog__result" role="status">
        <span v-if="result.failed === 0">已转发 {{ result.success }} 条。</span>
        <span v-else>成功 {{ result.success }} 条，失败 {{ result.failed }} 条。</span>
      </p>
      <p v-if="result && result.failed > 0" class="forward-dialog__error" role="alert">
        失败项会保留原消息顺序，点击「重试失败项」只重发未成功的消息。{{ lastError }}
      </p>
      <p v-if="contextLost" class="forward-dialog__error" role="alert">
        账号或服务器已变更，本批已停止，不会自动重发。请「开始新的一批」重新选择目标。
      </p>

      <div v-if="targetLocked" class="forward-dialog__batch">
        <span class="forward-dialog__hint">目标已锁定：{{ lockedTargetTitle }}</span>
        <button type="button" class="forward-dialog__new-batch" :disabled="running" @click="startNewBatch">
          开始新的一批
        </button>
      </div>

      <input
        v-model="query"
        class="forward-dialog__search"
        type="search"
        placeholder="搜索会话"
        aria-label="搜索会话"
      />
      <div class="forward-dialog__list" role="listbox" aria-label="转发目标会话" :aria-disabled="targetLocked">
        <button
          v-for="conversation in targets"
          :key="conversation.conversationId"
          type="button"
          role="option"
          :aria-selected="chosenTargetId === conversation.conversationId"
          class="forward-dialog__item"
          :class="{ 'forward-dialog__item--active': chosenTargetId === conversation.conversationId }"
          :disabled="targetLocked"
          @click="selectedId = conversation.conversationId"
        >
          <Avatar
            :display-name="conversation.title"
            :size="32"
            :avatar-file-id="avatarOf(conversation)"
          />
          <span class="forward-dialog__title">{{ conversation.title }}</span>
          <span class="forward-dialog__type">{{ conversation.type === 'group' ? '群聊' : '单聊' }}</span>
        </button>
      </div>
      <EmptyState
        v-if="targets.length === 0"
        title="没有可转发的会话"
        :description="query.trim() ? '没有匹配的会话，换个关键词试试' : '当前没有其他会话可转发'"
      />
      <p v-if="items.length > MAX_FORWARD_COUNT" class="forward-dialog__hint">
        单次最多转发 {{ MAX_FORWARD_COUNT }} 条，已保留前 {{ MAX_FORWARD_COUNT }} 条。
      </p>
      <p v-else-if="items.length > 0" class="forward-dialog__hint">
        已选 {{ items.length }} 条，成功 {{ sentCount }} 条，待转发 {{ pendingCount }} 条。
      </p>
    </div>
  </AppDialog>
</template>

<style scoped>
.forward-dialog {
  display: flex;
  flex-direction: column;
  gap: var(--ec-space-3);
  min-height: 0;
}

.forward-dialog__previews {
  display: flex;
  flex-direction: column;
  gap: 2px;
  max-height: 144px;
  padding: 0;
  margin: 0;
  overflow-y: auto;
  list-style: none;
}

.forward-dialog__preview-row {
  display: flex;
  gap: var(--ec-space-3);
  align-items: center;
  min-height: 28px;
  padding: var(--ec-space-1) var(--ec-space-3);
  background: var(--ec-surface-hover);
  border-radius: var(--ec-radius-input);
}

.forward-dialog__preview-text {
  flex: 1;
  min-width: 0;
  overflow: hidden;
  color: var(--ec-text-secondary);
  text-overflow: ellipsis;
  white-space: nowrap;
}

.forward-dialog__status {
  flex: none;
  font-size: var(--ec-font-size-small);
  color: var(--ec-text-tertiary);
}

.forward-dialog__status--sent {
  color: var(--ec-success);
}

.forward-dialog__status--failed {
  color: var(--ec-danger);
}

.forward-dialog__result {
  margin: 0;
  color: var(--ec-text-primary);
}

.forward-dialog__error {
  margin: 0;
  color: var(--ec-danger);
  font-size: var(--ec-font-size-small);
}

.forward-dialog__batch {
  display: flex;
  gap: var(--ec-space-3);
  align-items: center;
  justify-content: space-between;
  padding: var(--ec-space-2) var(--ec-space-3);
  background: var(--ec-brand-050);
  border-radius: var(--ec-radius-input);
}

.forward-dialog__new-batch {
  flex: none;
  padding: 4px 10px;
  font-size: var(--ec-font-size-small);
  color: var(--ec-brand-600);
  background: transparent;
  border: 1px solid var(--ec-brand-600);
  border-radius: var(--ec-radius-input);
  cursor: pointer;
}

.forward-dialog__new-batch:disabled {
  color: var(--ec-text-tertiary);
  cursor: not-allowed;
  border-color: var(--ec-border);
}

.forward-dialog__search {
  width: 100%;
  height: 36px;
  padding: 0 var(--ec-space-3);
  color: var(--ec-text-primary);
  background: var(--ec-surface);
  border: 1px solid var(--ec-border);
  border-radius: var(--ec-radius-input);
}

.forward-dialog__search:focus-visible {
  border-color: var(--ec-focus-ring);
  outline: var(--ec-focus-width) solid var(--ec-focus-ring);
  outline-offset: var(--ec-focus-offset);
}

.forward-dialog__list {
  display: flex;
  flex-direction: column;
  gap: 2px;
  max-height: 300px;
  overflow-y: auto;
}

.forward-dialog__item {
  display: flex;
  gap: var(--ec-space-3);
  align-items: center;
  min-height: 48px;
  padding: var(--ec-space-1) var(--ec-space-2);
  text-align: left;
  background: transparent;
  border: none;
  border-radius: var(--ec-radius-panel);
  cursor: pointer;
}

.forward-dialog__item:hover:not(:disabled) {
  background: var(--ec-surface-hover);
}

.forward-dialog__item--active {
  background: var(--ec-brand-050);
}

.forward-dialog__item:disabled {
  cursor: default;
  opacity: 0.72;
}

.forward-dialog__title {
  flex: 1;
  min-width: 0;
  overflow: hidden;
  font-size: var(--ec-font-size-base);
  font-weight: 600;
  color: var(--ec-text-primary);
  text-overflow: ellipsis;
  white-space: nowrap;
}

.forward-dialog__type {
  flex: none;
  font-size: var(--ec-font-size-small);
  color: var(--ec-text-tertiary);
}

.forward-dialog__hint {
  margin: 0;
  font-size: var(--ec-font-size-small);
  color: var(--ec-text-tertiary);
}

@media (max-width: 800px) {
  .forward-dialog__list {
    max-height: 40vh;
  }
}
</style>
