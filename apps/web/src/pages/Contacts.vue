<script setup lang="ts">
import { ref } from 'vue'
import { useRouter } from 'vue-router'
import { usersApi, type UserProfile } from '../api/users'
import Avatar from '../components/Avatar.vue'
import { ApiError } from '../core/http'
import { useConversationsStore } from '../stores/conversations'

// 通讯录页（M2-04）：按邮箱/昵称搜索用户 → 发起单聊 → 进入 /chat/:conversationId。
const router = useRouter()
const conversations = useConversationsStore()

const keyword = ref('')
const results = ref<UserProfile[]>([])
const searched = ref(false)
const searching = ref(false)
const creatingUserId = ref<string | null>(null)
const errorMessage = ref('')

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
  if (creatingUserId.value !== null) return
  creatingUserId.value = user.userId
  errorMessage.value = ''
  try {
    const conversation = await conversations.createDirect(user.userId)
    await router.push({ name: 'chat', params: { conversationId: conversation.conversationId } })
  } catch (error) {
    errorMessage.value = toMessage(error)
  } finally {
    creatingUserId.value = null
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
      <h2 class="contacts-page__title">通讯录</h2>
    </header>

    <form class="contacts-page__search" @submit.prevent="search">
      <input
        v-model="keyword"
        type="search"
        maxlength="64"
        placeholder="输入邮箱或昵称搜索用户"
        aria-label="搜索用户"
      />
      <button type="submit" :disabled="searching">{{ searching ? '搜索中…' : '搜索' }}</button>
    </form>

    <p v-if="errorMessage" class="contacts-page__error" role="alert">{{ errorMessage }}</p>

    <ul v-if="results.length > 0" class="contacts-page__list">
      <li v-for="user in results" :key="user.userId" class="contacts-page__item">
        <Avatar :display-name="user.displayName" :avatar-file-id="user.avatarFileId" :size="36" />
        <span class="contacts-page__info">
          <span class="contacts-page__name">{{ user.displayName }}</span>
          <span class="contacts-page__email">{{ user.email }}</span>
        </span>
        <button
          type="button"
          class="contacts-page__action"
          :disabled="creatingUserId !== null"
          @click="startDirectChat(user)"
        >
          {{ creatingUserId === user.userId ? '创建中…' : '发消息' }}
        </button>
      </li>
    </ul>
    <p v-else-if="searched && !searching" class="contacts-page__hint">没有找到匹配的用户。</p>
    <p v-else class="contacts-page__hint">搜索同事的邮箱或昵称，发起单聊。</p>
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
  margin: 0;
  font-size: 20px;
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
  padding: var(--space-2) var(--space-3);
  border: 1px solid var(--color-primary);
  border-radius: var(--radius-sm);
  background: transparent;
  color: var(--color-primary);
  cursor: pointer;
}

.contacts-page__action:disabled {
  border-color: var(--color-border);
  color: var(--color-text-secondary);
  cursor: not-allowed;
}
</style>
