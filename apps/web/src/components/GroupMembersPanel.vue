<script setup lang="ts">
import { computed, ref } from 'vue'
import { conversationsApi } from '../api/conversations'
import { usersApi, type UserProfile } from '../api/users'
import Avatar from './Avatar.vue'
import AppDialog from './AppDialog.vue'
import PresenceDot from './PresenceDot.vue'
import { friendlyError } from '../core/errorMessages'
import { sortMembers } from '../core/groupChat'
import { showToast } from '../core/toast'
import { usePresenceStore } from '../stores/presence'
import type { Conversation, ConversationMember } from '../types/frames'

// 群成员管理面板（M3-08）：成员列表（群主优先）、邀请成员、移除成员（仅群主）、退出群聊（非群主）。
// 所有操作以服务端为准：成功后重新 GET 会话并 emit updated，由 Chat 页写回 store；
// 被移出/退出由服务端 conv.updated.removedConversationIds 推送（本面板主动退出时也直接 emit left）。
// M3-09：成员行展示在线状态圆点，头部展示在线人数。
// UI-02（计划 §5.6 group 抽屉）：本组件改为抽屉内容，遮罩/Esc/焦点返回统一由 DrawerShell 负责。
const props = defineProps<{
  conversation: Conversation
  selfUserId: string
}>()

const emit = defineEmits<{
  updated: [conversation: Conversation]
  left: [conversationId: string]
}>()

const presence = usePresenceStore()

const busy = ref(false)
const errorMessage = ref('')
/** 待确认的危险操作（移除成员 / 退出群聊），由自绘 AppDialog 二次确认。 */
const confirmState = ref<{ kind: 'remove'; member: ConversationMember } | { kind: 'leave' } | null>(null)
const inviteOpen = ref(false)
const inviteKeyword = ref('')
const inviteResults = ref<UserProfile[]>([])
const selectedInviteIds = ref<string[]>([])
const searching = ref(false)

const members = computed(() => sortMembers(props.conversation.members))
/** 已知在线的成员数（未知状态不计入，v1 无初始查询，M3-09）。 */
const onlineCount = computed(
  () => members.value.filter((member) => presence.stateOf(member.userId) === true).length,
)

const selfMember = computed(() =>
  props.conversation.members.find((member) => member.userId === props.selfUserId),
)
const isOwner = computed(() => selfMember.value?.role === 'owner')
const memberIds = computed(() => new Set(props.conversation.members.map((member) => member.userId)))

function roleLabel(member: ConversationMember): string {
  return member.role === 'owner' ? '群主' : '成员'
}

const dialogTitle = computed(() =>
  confirmState.value?.kind === 'remove' ? '移除群成员' : '退出群聊',
)
const dialogMessage = computed(() => {
  const state = confirmState.value
  if (!state) return ''
  if (state.kind === 'remove') {
    return `确定将「${state.member.displayName}」移出群聊吗？移除后对方将无法继续接收群消息。`
  }
  return '确定退出该群聊吗？退出后将不再接收群消息。'
})
const dialogConfirmText = computed(() => (confirmState.value?.kind === 'remove' ? '移除' : '退出'))


async function refreshConversation(): Promise<void> {
  const latest = await conversationsApi.get(props.conversation.conversationId)
  emit('updated', latest)
}

async function searchUsers(): Promise<void> {
  const keyword = inviteKeyword.value.trim()
  if (keyword.length === 0) {
    errorMessage.value = '请输入邮箱或昵称'
    return
  }
  searching.value = true
  errorMessage.value = ''
  try {
    const response = await usersApi.search(keyword)
    inviteResults.value = response.users.filter((user) => !memberIds.value.has(user.userId))
  } catch (error) {
    errorMessage.value = friendlyError(error)
  } finally {
    searching.value = false
  }
}

function isSelected(userId: string): boolean {
  return selectedInviteIds.value.includes(userId)
}

function toggleInvite(userId: string): void {
  selectedInviteIds.value = isSelected(userId)
    ? selectedInviteIds.value.filter((item) => item !== userId)
    : [...selectedInviteIds.value, userId]
}

function resetInvite(): void {
  inviteOpen.value = false
  inviteKeyword.value = ''
  inviteResults.value = []
  selectedInviteIds.value = []
  searching.value = false
}

async function invite(): Promise<void> {
  if (busy.value || selectedInviteIds.value.length === 0) return
  busy.value = true
  errorMessage.value = ''
  try {
    const updated = await conversationsApi.addMembers(
      props.conversation.conversationId,
      selectedInviteIds.value,
    )
    emit('updated', updated)
    showToast(`已邀请 ${selectedInviteIds.value.length} 位成员加入群聊`, 'success')
    resetInvite()
  } catch (error) {
    errorMessage.value = friendlyError(error)
  } finally {
    busy.value = false
  }
}

/** 移除成员：打开自绘确认弹窗，真正执行在 confirmDialog()。 */
function askRemoveMember(member: ConversationMember): void {
  if (busy.value || !isOwner.value) return
  confirmState.value = { kind: 'remove', member }
}

/** 退出群聊：打开自绘确认弹窗，真正执行在 confirmDialog()。 */
function askLeaveGroup(): void {
  if (busy.value || isOwner.value) return
  confirmState.value = { kind: 'leave' }
}

async function confirmDialog(): Promise<void> {
  const state = confirmState.value
  if (!state || busy.value) return
  busy.value = true
  errorMessage.value = ''
  try {
    if (state.kind === 'remove') {
      await conversationsApi.removeMember(props.conversation.conversationId, state.member.userId)
      await refreshConversation()
      showToast('已移出成员', 'success')
    } else {
      await conversationsApi.removeMember(props.conversation.conversationId, props.selfUserId)
      emit('left', props.conversation.conversationId)
    }
    confirmState.value = null
  } catch (error) {
    errorMessage.value = friendlyError(error)
    confirmState.value = null
  } finally {
    busy.value = false
  }
}
</script>

<template>
  <div class="group-panel">
    <p class="group-panel__subtitle">{{ members.length }} 位成员 · 在线 {{ onlineCount }} 人</p>

    <p v-if="errorMessage" class="group-panel__error" role="alert">{{ errorMessage }}</p>

    <ul class="group-panel__members">
      <li v-for="member in members" :key="member.userId" class="group-panel__member">
        <Avatar :display-name="member.displayName" :avatar-file-id="member.avatarFileId" :size="32" />
        <span class="group-panel__member-info">
          <span class="group-panel__member-name">
            <PresenceDot :online="presence.stateOf(member.userId)" />
            {{ member.displayName }}
            <em v-if="member.userId === selfUserId" class="group-panel__me">（我）</em>
          </span>
          <span class="group-panel__member-email">{{ member.email }}</span>
        </span>
        <span class="group-panel__role">{{ roleLabel(member) }}</span>
        <button
          v-if="isOwner && member.userId !== selfUserId"
          type="button"
          class="group-panel__remove"
          :disabled="busy"
          @click="askRemoveMember(member)"
        >
          移除
        </button>
      </li>
    </ul>

    <section class="group-panel__section">
      <button v-if="!inviteOpen" type="button" class="group-panel__primary" :disabled="busy" @click="inviteOpen = true">
        邀请成员
      </button>
      <template v-else>
        <form class="group-panel__search" @submit.prevent="searchUsers">
          <input
            v-model="inviteKeyword"
            type="search"
            maxlength="64"
            placeholder="输入邮箱或昵称搜索"
            aria-label="搜索待邀请用户"
          />
          <button type="submit" :disabled="searching">{{ searching ? '搜索中…' : '搜索' }}</button>
        </form>
        <p v-if="selectedInviteIds.length > 0" class="group-panel__selected">
          已选 {{ selectedInviteIds.length }} 人
        </p>
        <ul v-if="inviteResults.length > 0" class="group-panel__candidates">
          <li v-for="user in inviteResults" :key="user.userId" class="group-panel__candidate">
            <Avatar :display-name="user.displayName" :avatar-file-id="user.avatarFileId" :size="28" />
            <span class="group-panel__candidate-info">
              <span>{{ user.displayName }}</span>
              <span class="group-panel__member-email">{{ user.email }}</span>
            </span>
            <button type="button" class="group-panel__toggle" @click="toggleInvite(user.userId)">
              {{ isSelected(user.userId) ? '取消选择' : '选择' }}
            </button>
          </li>
        </ul>
        <p v-else-if="searching === false && inviteKeyword.trim().length > 0" class="group-panel__hint">
          没有找到可邀请的用户。
        </p>
        <div class="group-panel__actions">
          <button
            type="button"
            class="group-panel__primary"
            :disabled="busy || selectedInviteIds.length === 0"
            @click="invite"
          >
            确认邀请
          </button>
          <button type="button" class="group-panel__secondary" :disabled="busy" @click="resetInvite">取消</button>
        </div>
      </template>
    </section>

    <section class="group-panel__section group-panel__leave">
      <button v-if="!isOwner" type="button" class="group-panel__danger" :disabled="busy" @click="askLeaveGroup">
        退出群聊
      </button>
      <p v-else class="group-panel__hint">群主不能退出群聊（v1 不支持转让群主或解散群）。</p>
    </section>

    <AppDialog
      :open="confirmState !== null"
      :title="dialogTitle"
      :message="dialogMessage"
      :confirm-text="dialogConfirmText"
      tone="danger"
      :busy="busy"
      @confirm="confirmDialog"
      @cancel="confirmState = null"
    />
  </div>
</template>

<style scoped>
.group-panel {
  display: flex;
  flex-direction: column;
  gap: var(--ec-space-3);
}

.group-panel__subtitle {
  margin: 0;
  color: var(--ec-text-secondary);
  font-size: var(--ec-font-size-secondary);
}

.group-panel__toggle,
.group-panel__remove,
.group-panel__secondary {
  padding: 4px 10px;
  border: 1px solid var(--ec-border);
  border-radius: var(--ec-radius-sm);
  background: transparent;
  color: var(--ec-text-secondary);
  font-size: var(--ec-font-size-small);
  cursor: pointer;
}

.group-panel__primary {
  padding: 6px 12px;
  border: none;
  border-radius: var(--ec-radius-sm);
  background: var(--ec-brand-600);
  color: var(--ec-text-inverse);
  font-size: 13px;
  cursor: pointer;
}

.group-panel__primary:disabled,
.group-panel__danger:disabled {
  opacity: 0.6;
  cursor: not-allowed;
}

.group-panel__danger {
  padding: 6px 12px;
  border: 1px solid var(--ec-danger);
  border-radius: var(--ec-radius-sm);
  background: transparent;
  color: var(--ec-danger);
  font-size: 13px;
  cursor: pointer;
}

.group-panel__error {
  margin: 0;
  padding: var(--ec-space-2) var(--ec-space-3);
  border-radius: var(--ec-radius-sm);
  background: var(--ec-danger);
  color: var(--ec-text-inverse);
  font-size: var(--ec-font-size-secondary);
}

.group-panel__members,
.group-panel__candidates {
  margin: 0;
  padding: 0;
  list-style: none;
}

.group-panel__member,
.group-panel__candidate {
  display: flex;
  gap: var(--ec-space-2);
  align-items: center;
  padding: var(--ec-space-2) 0;
  border-bottom: 1px solid var(--ec-border);
}

.group-panel__member-info,
.group-panel__candidate-info {
  display: flex;
  flex: 1;
  flex-direction: column;
  gap: 2px;
  min-width: 0;
}

.group-panel__member-name {
  font-weight: var(--ec-font-weight-semibold);
}

.group-panel__me {
  color: var(--ec-text-secondary);
  font-size: var(--ec-font-size-small);
  font-style: normal;
}

.group-panel__member-email {
  overflow: hidden;
  color: var(--ec-text-secondary);
  font-size: var(--ec-font-size-small);
  text-overflow: ellipsis;
  white-space: nowrap;
}

.group-panel__role {
  flex: none;
  padding: 2px 8px;
  border-radius: var(--ec-radius-full);
  background: var(--ec-surface-hover);
  color: var(--ec-text-secondary);
  font-size: var(--ec-font-size-small);
}

.group-panel__section {
  display: flex;
  flex-direction: column;
  gap: var(--ec-space-2);
  padding-top: var(--ec-space-3);
  border-top: 1px solid var(--ec-border);
}

.group-panel__search {
  display: flex;
  gap: var(--ec-space-2);
}

.group-panel__search input {
  flex: 1;
  min-width: 0;
  padding: var(--ec-space-2) var(--ec-space-3);
  border: 1px solid var(--ec-border);
  border-radius: var(--ec-radius-sm);
  outline: none;
}

.group-panel__search input:focus {
  border-color: var(--ec-brand-600);
}

.group-panel__search button {
  padding: 0 var(--ec-space-3);
  border: none;
  border-radius: var(--ec-radius-sm);
  background: var(--ec-brand-600);
  color: var(--ec-text-inverse);
  cursor: pointer;
}

.group-panel__selected {
  margin: 0;
  color: var(--ec-brand-600);
  font-size: var(--ec-font-size-secondary);
}

.group-panel__actions {
  display: flex;
  gap: var(--ec-space-2);
}

.group-panel__hint {
  margin: 0;
  color: var(--ec-text-secondary);
  font-size: var(--ec-font-size-secondary);
}

.group-panel__leave {
  margin-top: var(--ec-space-2);
}
</style>
