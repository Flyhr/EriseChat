<script setup lang="ts">
import { computed, onBeforeUnmount, ref, watch } from 'vue'
import { useRouter } from 'vue-router'
import { usersApi, type UserProfile } from '../api/users'
import type { Conversation } from '../types/frames'
import Avatar from '../components/Avatar.vue'
import ContactProfile from '../components/ContactProfile.vue'
import EmptyState from '../components/EmptyState.vue'
import NewGroupDialog from '../components/NewGroupDialog.vue'
import PresenceDot from '../components/PresenceDot.vue'
import { friendlyError } from '../core/errorMessages'
import { useConversationsStore } from '../stores/conversations'
import { usePresenceStore } from '../stores/presence'

// 联系人页（UI-03 批次2，计划 §5.8）：左栏「搜索用户 + 结果列表」，右栏选中用户资料；
// 顶部提供「新建群聊」入口（自绘弹窗，沿用原 createGroup 流程，计划 §5.9）。
// 不出现「好友 / 好友申请」概念：服务端没有该关系模型，页面只说「同事 / 联系人」。
const router = useRouter()
const conversations = useConversationsStore()
const presence = usePresenceStore()

const keyword = ref('')
const results = ref<UserProfile[]>([])
const searched = ref(false)
const searching = ref(false)
const errorMessage = ref('')
const selectedUser = ref<UserProfile | null>(null)
const busyUserId = ref<string | null>(null)
const newGroupOpen = ref(false)

/** 搜索防抖：300ms 内连续输入只发一次请求；清空关键词时立即回到初始态。 */
const SEARCH_DEBOUNCE_MS = 300
let searchTimer: ReturnType<typeof setTimeout> | null = null

const selectedPresence = computed(() =>
  selectedUser.value ? presence.stateOf(selectedUser.value.userId) : undefined,
)

function clearSearchTimer(): void {
  if (searchTimer !== null) {
    clearTimeout(searchTimer)
    searchTimer = null
  }
}

async function search(immediate = false): Promise<void> {
  clearSearchTimer()
  const trimmed = keyword.value.trim()
  if (trimmed.length === 0) {
    results.value = []
    searched.value = false
    return
  }
  if (!immediate && searchTimer !== null) return
  searching.value = true
  errorMessage.value = ''
  try {
    const response = await usersApi.search(trimmed)
    results.value = response.users
    searched.value = true
  } catch (error) {
    errorMessage.value = friendlyError(error)
  } finally {
    searching.value = false
  }
}

watch(keyword, (value) => {
  clearSearchTimer()
  errorMessage.value = ''
  const trimmed = value.trim()
  if (trimmed.length === 0) {
    results.value = []
    searched.value = false
    return
  }
  searchTimer = setTimeout(() => {
    searchTimer = null
    void search(true)
  }, SEARCH_DEBOUNCE_MS)
})

onBeforeUnmount(clearSearchTimer)

function selectUser(user: UserProfile): void {
  selectedUser.value = user
}

function clearSelection(): void {
  selectedUser.value = null
}

/** 发起单聊：沿用 M2 的单聊创建/复用流程（首次创建、已有则复用），成功后进入聊天页。 */
async function startDirectChat(user: UserProfile): Promise<void> {
  if (busyUserId.value !== null) return
  busyUserId.value = user.userId
  errorMessage.value = ''
  try {
    const conversation = await conversations.createDirect(user.userId)
    await router.push({ name: 'chat', params: { conversationId: conversation.conversationId } })
  } catch (error) {
    errorMessage.value = friendlyError(error)
  } finally {
    busyUserId.value = null
  }
}

function onGroupCreated(conversation: Conversation): void {
  void router.push({ name: 'chat', params: { conversationId: conversation.conversationId } })
}
</script>

<template>
  <main class="contacts-page" :class="{ 'contacts-page--profile': selectedUser !== null }">
    <aside class="contacts-page__list">
      <header class="contacts-page__header">
        <h2 class="contacts-page__title">联系人</h2>
        <button type="button" class="contacts-page__new-group" @click="newGroupOpen = true">
          新建群聊
        </button>
      </header>

      <form class="contacts-page__search" @submit.prevent="search(true)">
        <input
          v-model="keyword"
          type="search"
          maxlength="64"
          placeholder="输入邮箱或昵称搜索同事"
          aria-label="搜索用户"
        />
        <button type="submit" :disabled="searching">{{ searching ? '搜索中…' : '搜索' }}</button>
      </form>

      <p v-if="errorMessage" class="contacts-page__error" role="alert">{{ errorMessage }}</p>

      <p v-if="searching && results.length === 0" class="contacts-page__hint">搜索中…</p>
      <ul v-else-if="results.length > 0" class="contacts-page__results">
        <li v-for="user in results" :key="user.userId">
          <button
            type="button"
            class="contacts-page__result"
            :class="{ 'contacts-page__result--active': selectedUser?.userId === user.userId }"
            @click="selectUser(user)"
          >
            <Avatar :display-name="user.displayName" :size="36" />
            <span class="contacts-page__result-info">
              <span class="contacts-page__result-name">
                <PresenceDot :online="presence.stateOf(user.userId)" />
                {{ user.displayName }}
              </span>
              <span class="contacts-page__result-email">{{ user.email }}</span>
            </span>
          </button>
        </li>
      </ul>
      <EmptyState
        v-else-if="searched"
        title="未找到匹配用户"
        description="换一个邮箱或昵称再试试。"
      />
      <p v-else class="contacts-page__hint">输入同事的邮箱或昵称开始搜索。</p>
    </aside>

    <section class="contacts-page__profile">
      <button
        v-if="selectedUser"
        type="button"
        class="contacts-page__back-list"
        @click="clearSelection"
      >
        返回列表
      </button>
      <ContactProfile
        :user="selectedUser"
        :presence="selectedPresence"
        :busy="busyUserId !== null"
        @start-chat="startDirectChat"
      />
    </section>

    <NewGroupDialog
      :open="newGroupOpen"
      @close="newGroupOpen = false"
      @created="onGroupCreated"
    />
  </main>
</template>

<style scoped>
.contacts-page {
  display: grid;
  grid-template-columns: 360px minmax(0, 1fr);
  height: 100%;
  min-height: 0;
  background: var(--ec-bg-canvas);
}

.contacts-page__list {
  display: flex;
  flex-direction: column;
  gap: var(--ec-space-3);
  min-height: 0;
  padding: var(--ec-space-4);
  overflow-y: auto;
  background: var(--ec-surface);
  border-right: 1px solid var(--ec-border);
}

.contacts-page__header {
  display: flex;
  align-items: center;
  gap: var(--ec-space-3);
}

.contacts-page__title {
  flex: 1;
  margin: 0;
  color: var(--ec-text-primary);
  font-size: var(--ec-font-size-page-title);
  font-weight: var(--ec-font-weight-semibold);
}

.contacts-page__new-group {
  flex: none;
  padding: 6px var(--ec-space-3);
  border: 1px solid var(--ec-brand-600);
  border-radius: var(--ec-radius-sm);
  background: transparent;
  color: var(--ec-brand-600);
  font-size: var(--ec-font-size-secondary);
  cursor: pointer;
  transition: background-color var(--ec-transition-fast) var(--ec-ease);
}

.contacts-page__new-group:hover {
  background: var(--ec-brand-050);
}

.contacts-page__search {
  display: flex;
  gap: var(--ec-space-2);
}

.contacts-page__search input {
  flex: 1;
  min-width: 0;
  padding: var(--ec-space-2) var(--ec-space-3);
  border: 1px solid var(--ec-border);
  border-radius: var(--ec-radius-input);
  outline: none;
  box-sizing: border-box;
}

.contacts-page__search input:focus {
  border-color: var(--ec-brand-600);
}

.contacts-page__search button {
  flex: none;
  padding: 0 var(--ec-space-4);
  border: none;
  border-radius: var(--ec-radius-input);
  background: var(--ec-brand-600);
  color: var(--ec-text-inverse);
  cursor: pointer;
}

.contacts-page__search button:disabled {
  opacity: 0.6;
  cursor: not-allowed;
}

.contacts-page__error {
  margin: 0;
  padding: var(--ec-space-2) var(--ec-space-3);
  border-radius: var(--ec-radius-sm);
  background: var(--ec-danger);
  color: var(--ec-text-inverse);
  font-size: var(--ec-font-size-secondary);
}

.contacts-page__hint {
  margin: 0;
  color: var(--ec-text-secondary);
  font-size: var(--ec-font-size-secondary);
}

.contacts-page__results {
  display: flex;
  flex-direction: column;
  gap: var(--ec-space-1);
  margin: 0;
  padding: 0;
  list-style: none;
}

.contacts-page__result {
  display: flex;
  gap: var(--ec-space-2);
  align-items: center;
  width: 100%;
  padding: var(--ec-space-2);
  border: none;
  border-radius: var(--ec-radius-md);
  background: transparent;
  text-align: left;
  cursor: pointer;
}

.contacts-page__result:hover {
  background: var(--ec-surface-hover);
}

.contacts-page__result--active {
  background: var(--ec-brand-050);
}

.contacts-page__result-info {
  display: flex;
  flex: 1;
  flex-direction: column;
  gap: 2px;
  min-width: 0;
}

.contacts-page__result-name {
  display: inline-flex;
  gap: var(--ec-space-1);
  align-items: center;
  color: var(--ec-text-primary);
  font-size: var(--ec-font-size-body);
  font-weight: var(--ec-font-weight-medium);
}

.contacts-page__result-email {
  overflow: hidden;
  color: var(--ec-text-secondary);
  font-size: var(--ec-font-size-small);
  text-overflow: ellipsis;
  white-space: nowrap;
}

.contacts-page__profile {
  display: flex;
  flex-direction: column;
  min-height: 0;
  overflow-y: auto;
}

.contacts-page__back-list {
  display: none;
  align-self: flex-start;
  margin: var(--ec-space-3) 0 0 var(--ec-space-3);
  padding: 4px var(--ec-space-2);
  border: none;
  background: transparent;
  color: var(--ec-brand-600);
  font-size: var(--ec-font-size-secondary);
  cursor: pointer;
}

.contacts-page__profile > .contact-profile {
  flex: 1;
}

/* ≤800：单列；列表与资料一次只显示一个（选中后显示资料，可用「返回列表」切换）。 */
@media (max-width: 800px) {
  .contacts-page {
    grid-template-columns: minmax(0, 1fr);
  }

  .contacts-page__list {
    border-right: none;
  }

  .contacts-page__profile {
    display: none;
  }

  .contacts-page--profile .contacts-page__list {
    display: none;
  }

  .contacts-page--profile .contacts-page__profile {
    display: flex;
  }

  .contacts-page__back-list {
    display: inline-block;
  }
}
</style>
