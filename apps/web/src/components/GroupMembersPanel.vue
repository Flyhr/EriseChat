<script setup lang="ts">
import { computed, ref } from 'vue'
import { conversationsApi } from '../api/conversations'
import { usersApi, type UserProfile } from '../api/users'
import Avatar from './Avatar.vue'
import { ApiError } from '../core/http'
import type { Conversation, ConversationMember } from '../types/frames'

// 群成员管理面板（M3-08）：成员列表（群主优先）、邀请成员、移除成员（仅群主）、退出群聊（非群主）。
// 所有操作以服务端为准：成功后重新 GET 会话并 emit updated，由 Chat 页写回 store；
// 被移出/退出由服务端 conv.updated.removedConversationIds 推送（本面板主动退出时也直接 emit left）。
const props = defineProps<{
  conversation: Conversation
  selfUserId: string
}>()

const emit = defineEmits<{
  close: []
  updated: [conversation: Conversation]
  left: [conversationId: string]
}>()

const busy = ref(false)
const errorMessage = ref('')
const successMessage = ref('')
const inviteOpen = ref(false)
const inviteKeyword = ref('')
const inviteResults = ref<UserProfile[]>([])
const selectedInviteIds = ref<string[]>([])
const searching = ref(false)

const members = computed(() =>
  [...props.conversation.members].sort((a, b) => {
    if (a.role !== b.role) return a.role === 'owner' ? -1 : 1
    return a.displayName.localeCompare(b.displayName, 'zh-CN')
  }),
)

const selfMember = computed(() =>
  props.conversation.members.find((member) => member.userId === props.selfUserId),
)
const isOwner = computed(() => selfMember.value?.role === 'owner')
const memberIds = computed(() => new Set(props.conversation.members.map((member) => member.userId)))

function roleLabel(member: ConversationMember): string {
  return member.role === 'owner' ? '群主' : '成员'
}

function toMessage(error: unknown): string {
  if (error instanceof ApiError) {
    if (error.code === 'UNAUTHORIZED') return '登录状态已失效，请重新登录'
    return error.message
  }
  if (error instanceof Error && error.message) return error.message
  return '网络异常，请稍后重试'
}

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
    errorMessage.value = toMessage(error)
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
  successMessage.value = ''
  try {
    const updated = await conversationsApi.addMembers(
      props.conversation.conversationId,
      selectedInviteIds.value,
    )
    emit('updated', updated)
    successMessage.value = '已邀请成员加入群聊'
    resetInvite()
  } catch (error) {
    errorMessage.value = toMessage(error)
  } finally {
    busy.value = false
  }
}

async function removeMember(member: ConversationMember): Promise<void> {
  if (busy.value || !isOwner.value) return
  if (!window.confirm(`确定将「${member.displayName}」移出群聊吗？`)) return
  busy.value = true
  errorMessage.value = ''
  successMessage.value = ''
  try {
    await conversationsApi.removeMember(props.conversation.conversationId, member.userId)
    await refreshConversation()
    successMessage.value = '已移出成员'
  } catch (error) {
    errorMessage.value = toMessage(error)
  } finally {
    busy.value = false
  }
}

async function leaveGroup(): Promise<void> {
  if (busy.value || isOwner.value) return
  if (!window.confirm('确定退出该群聊吗？退出后将不再接收群消息。')) return
  busy.value = true
  errorMessage.value = ''
  try {
    await conversationsApi.removeMember(props.conversation.conversationId, props.selfUserId)
    emit('left', props.conversation.conversationId)
  } catch (error) {
    errorMessage.value = toMessage(error)
  } finally {
    busy.value = false
  }
}
</script>

<template>
  <Teleport to="body">
    <div class="group-panel__backdrop" @click.self="emit('close')">
      <aside class="group-panel" role="dialog" aria-label="群成员">
        <header class="group-panel__header">
          <div>
            <h3 class="group-panel__title">{{ conversation.title || '群聊' }}</h3>
            <p class="group-panel__subtitle">{{ members.length }} 位成员</p>
          </div>
          <button type="button" class="group-panel__close" @click="emit('close')">关闭</button>
        </header>

        <p v-if="errorMessage" class="group-panel__error" role="alert">{{ errorMessage }}</p>
        <p v-if="successMessage" class="group-panel__success" role="status">{{ successMessage }}</p>

        <ul class="group-panel__members">
          <li v-for="member in members" :key="member.userId" class="group-panel__member">
            <Avatar :display-name="member.displayName" :avatar-file-id="member.avatarFileId" :size="32" />
            <span class="group-panel__member-info">
              <span class="group-panel__member-name">
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
              @click="removeMember(member)"
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
          <button v-if="!isOwner" type="button" class="group-panel__danger" :disabled="busy" @click="leaveGroup">
            退出群聊
          </button>
          <p v-else class="group-panel__hint">群主不能退出群聊（v1 不支持转让群主或解散群）。</p>
        </section>
      </aside>
    </div>
  </Teleport>
</template>

<style scoped>
.group-panel__backdrop {
  position: fixed;
  inset: 0;
  z-index: 40;
  display: flex;
  justify-content: flex-end;
  background: rgb(0 0 0 / 32%);
}

.group-panel {
  display: flex;
  flex-direction: column;
  gap: var(--space-3);
  width: min(380px, 100%);
  height: 100%;
  padding: var(--space-4);
  overflow-y: auto;
  background: var(--color-surface);
  box-shadow: var(--shadow-md);
}

.group-panel__header {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: var(--space-3);
}

.group-panel__title {
  margin: 0;
  font-size: 18px;
}

.group-panel__subtitle {
  margin: 4px 0 0;
  color: var(--color-text-secondary);
  font-size: 12px;
}

.group-panel__close,
.group-panel__toggle,
.group-panel__remove,
.group-panel__secondary {
  padding: 4px 10px;
  border: 1px solid var(--color-border);
  border-radius: var(--radius-sm);
  background: transparent;
  color: var(--color-text-secondary);
  font-size: 12px;
  cursor: pointer;
}

.group-panel__primary {
  padding: 6px 12px;
  border: none;
  border-radius: var(--radius-sm);
  background: var(--color-primary);
  color: var(--color-text-inverse);
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
  border: 1px solid var(--color-danger);
  border-radius: var(--radius-sm);
  background: transparent;
  color: var(--color-danger);
  font-size: 13px;
  cursor: pointer;
}

.group-panel__error,
.group-panel__success {
  margin: 0;
  padding: var(--space-2) var(--space-3);
  border-radius: var(--radius-sm);
  font-size: 12px;
}

.group-panel__error {
  background: var(--color-danger);
  color: var(--color-text-inverse);
}

.group-panel__success {
  background: var(--color-primary-soft);
  color: var(--color-primary);
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
  gap: var(--space-2);
  align-items: center;
  padding: var(--space-2) 0;
  border-bottom: 1px solid var(--color-border);
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
  font-weight: 600;
}

.group-panel__me {
  color: var(--color-text-secondary);
  font-size: 11px;
  font-style: normal;
}

.group-panel__member-email {
  overflow: hidden;
  color: var(--color-text-secondary);
  font-size: 11px;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.group-panel__role {
  flex: none;
  padding: 2px 8px;
  border-radius: var(--radius-full);
  background: var(--color-surface-hover);
  color: var(--color-text-secondary);
  font-size: 11px;
}

.group-panel__section {
  display: flex;
  flex-direction: column;
  gap: var(--space-2);
  padding-top: var(--space-3);
  border-top: 1px solid var(--color-border);
}

.group-panel__search {
  display: flex;
  gap: var(--space-2);
}

.group-panel__search input {
  flex: 1;
  min-width: 0;
  padding: var(--space-2) var(--space-3);
  border: 1px solid var(--color-border);
  border-radius: var(--radius-sm);
  outline: none;
}

.group-panel__search input:focus {
  border-color: var(--color-primary);
}

.group-panel__search button {
  padding: 0 var(--space-3);
  border: none;
  border-radius: var(--radius-sm);
  background: var(--color-primary);
  color: var(--color-text-inverse);
  cursor: pointer;
}

.group-panel__selected {
  margin: 0;
  color: var(--color-primary);
  font-size: 12px;
}

.group-panel__actions {
  display: flex;
  gap: var(--space-2);
}

.group-panel__hint {
  margin: 0;
  color: var(--color-text-secondary);
  font-size: 12px;
}

.group-panel__leave {
  margin-top: auto;
}
</style>
