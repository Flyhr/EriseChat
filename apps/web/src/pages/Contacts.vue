<script setup lang="ts">
import { computed, ref } from 'vue'
import { useRouter } from 'vue-router'
import { usersApi, type UserProfile } from '../api/users'
import Avatar from '../components/Avatar.vue'
import PresenceDot from '../components/PresenceDot.vue'
import { ApiError } from '../core/http'
import { useConversationsStore } from '../stores/conversations'
import { usePresenceStore } from '../stores/presence'

// 通讯录页（M2-04 单聊 / M3-08 建群 / M3-09 在线状态）：搜索用户 → 发起单聊或勾选成员创建群聊 → 进入 /chat/:conversationId。
const router = useRouter()
const conversations = useConversationsStore()
const presence = usePresenceStore()

const mode = ref<'direct' | 'group'>('direct')
const keyword = ref('')
const results = ref<UserProfile[]>([])
const searched = ref(false)
const searching = ref(false)
const busyUserId = ref<string | null>(null)
const creatingGroup = ref(false)
const errorMessage = ref('')

// 建群状态：群名称必填（服务端 1~100 字符）+ 至少 1 位被邀请成员（不含群主）。
const groupTitle = ref('')
const selectedMembers = ref<UserProfile[]>([])

const isGroupMode = computed(() => mode.value === 'group')

function switchMode(next: 'direct' | 'group'): void {
  if (mode.value === next) return
  mode.value = next
  errorMessage.value = ''
  results.value = []
  searched.value = false
  keyword.value = ''
  if (next === 'direct') {
    groupTitle.value = ''
    selectedMembers.value = []
  }
}

async function search(): Promise<void> {
  const trimmed = keyword.value.trim()
  if (trimmed.length === 0) {
    errorMessage.value = '请输入邮箱或昵称'
    return
  }
  searching.value = true
  errorMessage.value = ''
  try {
    const response = await usersApi.search(trimmed)
    results.value = response.users
    searched.value = true
  } catch (error) {
    errorMessage.value = toMessage(error)
  } finally {
    searching.value = false
  }
}

async function startDirectChat(user: UserProfile): Promise<void> {
  if (busyUserId.value !== null) return
  busyUserId.value = user.userId
  errorMessage.value = ''
  try {
    const conversation = await conversations.createDirect(user.userId)
    await router.push({ name: 'chat', params: { conversationId: conversation.conversationId } })
  } catch (error) {
    errorMessage.value = toMessage(error)
  } finally {
    busyUserId.value = null
  }
}

function isSelected(userId: string): boolean {
  return selectedMembers.value.some((member) => member.userId === userId)
}

function toggleMember(user: UserProfile): void {
  selectedMembers.value = isSelected(user.userId)
    ? selectedMembers.value.filter((member) => member.userId !== user.userId)
    : [...selectedMembers.value, user]
}

function removeSelected(userId: string): void {
  selectedMembers.value = selectedMembers.value.filter((member) => member.userId !== userId)
}

async function createGroup(): Promise<void> {
  if (creatingGroup.value) return
  const title = groupTitle.value.trim()
  if (title.length === 0) {
    errorMessage.value = '请输入群名称'
    return
  }
  if (selectedMembers.value.length === 0) {
    errorMessage.value = '请至少选择 1 位成员'
    return
  }

  creatingGroup.value = true
  errorMessage.value = ''
  try {
    const conversation = await conversations.createGroup(
      title,
      selectedMembers.value.map((member) => member.userId),
    )
    await router.push({ name: 'chat', params: { conversationId: conversation.conversationId } })
  } catch (error) {
    // 服务端 VALIDATION_FAILED 的具体文案（人数上限/成员不存在等）原样展示，不自行编造。
    errorMessage.value = toMessage(error)
  } finally {
    creatingGroup.value = false
  }
}

function toMessage(error: unknown): string {
  if (error instanceof ApiError) {
    if (error.code === 'UNAUTHORIZED') return '登录状态已失效，请重新登录'
    return error.message
  }
  if (error instanceof Error && error.message) return error.message
  return '网络异常，请稍后重试'
}
</script>

<template>
  <main class="contacts-page">
    <header class="contacts-page__header">
      <RouterLink class="contacts-page__back" to="/chat">返回消息</RouterLink>
      <h2 class="contacts-page__title">{{ isGroupMode ? '新建群聊' : '通讯录' }}</h2>
      <button
        v-if="!isGroupMode"
        type="button"
        class="contacts-page__mode"
        @click="switchMode('group')"
      >
        新建群聊
      </button>
      <button
        v-else
        type="button"
        class="contacts-page__mode contacts-page__mode--ghost"
        @click="switchMode('direct')"
      >
        返回单聊
      </button>
    </header>

    <form class="contacts-page__search" @submit.prevent="search">
      <input
        v-model="keyword"
        type="search"
        maxlength="64"
        :placeholder="isGroupMode ? '输入邮箱或昵称搜索并勾选成员' : '输入邮箱或昵称搜索用户'"
        aria-label="搜索用户"
      />
      <button type="submit" :disabled="searching">{{ searching ? '搜索中…' : '搜索' }}</button>
    </form>

    <p v-if="errorMessage" class="contacts-page__error" role="alert">{{ errorMessage }}</p>

    <section v-if="isGroupMode" class="contacts-page__group">
      <label class="contacts-page__label" for="group-title">群名称（必填，1~100 字符）</label>
      <input
        id="group-title"
        v-model="groupTitle"
        type="text"
        maxlength="100"
        class="contacts-page__title-input"
        placeholder="例如：研发群"
      />

      <p class="contacts-page__label">已选成员（{{ selectedMembers.length }}）</p>
      <p v-if="selectedMembers.length === 0" class="contacts-page__hint">
        在下方搜索结果中勾选成员，至少 1 人（不含自己）。
      </p>
      <ul v-else class="contacts-page__chips">
        <li v-for="member in selectedMembers" :key="member.userId" class="contacts-page__chip">
          <span>{{ member.displayName }}</span>
          <button type="button" class="contacts-page__chip-remove" @click="removeSelected(member.userId)">
            移除
          </button>
        </li>
      </ul>

      <button
        type="button"
        class="contacts-page__create"
        :disabled="creatingGroup"
        @click="createGroup"
      >
        {{ creatingGroup ? '创建中…' : '创建群聊' }}
      </button>
    </section>

    <ul v-if="results.length > 0" class="contacts-page__list">
      <li v-for="user in results" :key="user.userId" class="contacts-page__item">
        <Avatar :display-name="user.displayName" :avatar-file-id="user.avatarFileId" :size="36" />
        <span class="contacts-page__info">
          <span class="contacts-page__name">
            <PresenceDot :online="presence.stateOf(user.userId)" />
            {{ user.displayName }}
          </span>
          <span class="contacts-page__email">{{ user.email }}</span>
        </span>
        <button
          v-if="isGroupMode"
          type="button"
          class="contacts-page__action"
          :class="{ 'contacts-page__action--selected': isSelected(user.userId) }"
          @click="toggleMember(user)"
        >
          {{ isSelected(user.userId) ? '取消选择' : '选择' }}
        </button>
        <button
          v-else
          type="button"
          class="contacts-page__action"
          :disabled="busyUserId !== null"
          @click="startDirectChat(user)"
        >
          {{ busyUserId === user.userId ? '创建中…' : '发消息' }}
        </button>
      </li>
    </ul>
    <p v-else-if="searched && !searching" class="contacts-page__hint">没有找到匹配的用户。</p>
    <p v-else class="contacts-page__hint">
      {{ isGroupMode ? '搜索同事，勾选后创建群聊。' : '搜索同事的邮箱或昵称，发起单聊。' }}
    </p>
  </main>
</template>

<style scoped>
.contacts-page {
  max-width: 640px;
  margin: 0 auto;
  padding: var(--space-5);
}

.contacts-page__header {
  display: flex;
  align-items: center;
  gap: var(--space-3);
  margin-bottom: var(--space-4);
}

.contacts-page__back {
  color: var(--color-primary);
  font-size: 13px;
  text-decoration: none;
}

.contacts-page__title {
  flex: 1;
  margin: 0;
  font-size: 20px;
}

.contacts-page__mode {
  padding: 6px 12px;
  border: none;
  border-radius: var(--radius-sm);
  background: var(--color-primary);
  color: var(--color-text-inverse);
  font-size: 13px;
  cursor: pointer;
}

.contacts-page__mode--ghost {
  background: transparent;
  color: var(--color-primary);
}

.contacts-page__search {
  display: flex;
  gap: var(--space-2);
  margin-bottom: var(--space-4);
}

.contacts-page__search input {
  flex: 1;
  min-width: 0;
  padding: var(--space-2) var(--space-3);
  border: 1px solid var(--color-border);
  border-radius: var(--radius-sm);
  outline: none;
}

.contacts-page__search input:focus {
  border-color: var(--color-primary);
}

.contacts-page__search button {
  padding: 0 var(--space-4);
  border: none;
  border-radius: var(--radius-sm);
  background: var(--color-primary);
  color: var(--color-text-inverse);
  cursor: pointer;
}

.contacts-page__search button:disabled {
  opacity: 0.6;
  cursor: not-allowed;
}

.contacts-page__error {
  margin: 0 0 var(--space-3);
  padding: var(--space-2) var(--space-3);
  border-radius: var(--radius-sm);
  background: var(--color-danger);
  color: var(--color-text-inverse);
  font-size: 13px;
}

.contacts-page__hint {
  color: var(--color-text-secondary);
  font-size: 13px;
}

.contacts-page__group {
  display: flex;
  flex-direction: column;
  gap: var(--space-2);
  margin-bottom: var(--space-4);
  padding: var(--space-4);
  border: 1px solid var(--color-border);
  border-radius: var(--radius-md);
}

.contacts-page__group .contacts-page__hint {
  margin: 0;
}

.contacts-page__label {
  margin: 0;
  color: var(--color-text-secondary);
  font-size: 12px;
}

.contacts-page__title-input {
  padding: var(--space-2) var(--space-3);
  border: 1px solid var(--color-border);
  border-radius: var(--radius-sm);
  outline: none;
}

.contacts-page__title-input:focus {
  border-color: var(--color-primary);
}

.contacts-page__chips {
  display: flex;
  flex-wrap: wrap;
  gap: var(--space-2);
  margin: 0;
  padding: 0;
  list-style: none;
}

.contacts-page__chip {
  display: inline-flex;
  gap: var(--space-2);
  align-items: center;
  padding: 2px 8px;
  border-radius: var(--radius-full);
  background: var(--color-surface-hover);
  font-size: 12px;
}

.contacts-page__chip-remove {
  border: none;
  background: transparent;
  color: var(--color-danger);
  font-size: 11px;
  cursor: pointer;
}

.contacts-page__create {
  align-self: flex-start;
  padding: 8px 16px;
  border: none;
  border-radius: var(--radius-sm);
  background: var(--color-primary);
  color: var(--color-text-inverse);
  font-size: 13px;
  cursor: pointer;
}

.contacts-page__create:disabled {
  opacity: 0.6;
  cursor: not-allowed;
}

.contacts-page__list {
  margin: 0;
  padding: 0;
  list-style: none;
}

.contacts-page__item {
  display: flex;
  gap: var(--space-3);
  align-items: center;
  padding: var(--space-3) 0;
  border-bottom: 1px solid var(--color-border);
}

.contacts-page__info {
  display: flex;
  flex: 1;
  flex-direction: column;
  gap: 2px;
  min-width: 0;
}

.contacts-page__name {
  font-weight: 600;
}

.contacts-page__email {
  overflow: hidden;
  color: var(--color-text-secondary);
  font-size: 12px;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.contacts-page__action {
  flex: none;
  padding: 6px 12px;
  border: 1px solid var(--color-border);
  border-radius: var(--radius-sm);
  background: transparent;
  color: var(--color-primary);
  font-size: 12px;
  cursor: pointer;
}

.contacts-page__action:disabled {
  opacity: 0.6;
  cursor: not-allowed;
}

.contacts-page__action--selected {
  border-color: var(--color-primary);
  background: var(--color-primary-soft);
}
</style>
