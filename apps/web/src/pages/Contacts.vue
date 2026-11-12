// 联系人页（UI-03 批次2 双栏 + F-17 批次5 好友体系）：
// 左栏三块切换「好友 / 申请 / 搜索」，右栏资料区（ContactProfile）。
// - 好友：按分组展示（备注优先显示），选中可查看资料、维护备注/分组、删除好友；
// - 申请：收到的申请可接受/拒绝，发出的可撤销（实时推送由 friends store 接收）；
// - 搜索：按邮箱/昵称搜索同事，按 relation 显示加为好友 / 已发送 / 待处理。
<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { useRouter } from 'vue-router'
import { usersApi, type UserProfile } from '../api/users'
import type { Conversation } from '../types/frames'
import type { Friend, FriendRequest } from '../types/frames'
import AppDialog from '../components/AppDialog.vue'
import SearchInput from '../components/SearchInput.vue'
import AppIcon from '../components/AppIcon.vue'
import Avatar from '../components/Avatar.vue'
import ContactProfile from '../components/ContactProfile.vue'
import EmptyState from '../components/EmptyState.vue'
import NewGroupDialog from '../components/NewGroupDialog.vue'
import PresenceDot from '../components/PresenceDot.vue'
import { friendlyError } from '../core/errorMessages'
import { enter, type MotionHandle } from '../core/motion'
import { showToast } from '../core/toast'
import { useConversationsStore } from '../stores/conversations'
import { useFriendsStore } from '../stores/friends'
import { usePresenceStore } from '../stores/presence'

type ContactsTab = 'friends' | 'requests' | 'search'

/** 资料区所需的最小用户信息（UserProfile 结构兼容）。 */
interface ContactUser {
  userId: string
  displayName: string
  email: string
  avatarFileId?: string | null
}

const SEARCH_DEBOUNCE_MS = 300
const MESSAGE_MAX_LENGTH = 100

const router = useRouter()
const conversations = useConversationsStore()
const presence = usePresenceStore()
const friends = useFriendsStore()

const tab = ref<ContactsTab>('friends')

// 搜索态
const keyword = ref('')
const results = ref<UserProfile[]>([])
const searched = ref(false)
const searching = ref(false)
const searchError = ref('')

// 选中与操作态
const selectedUser = ref<ContactUser | null>(null)
const busyUserId = ref('')
const newGroupOpen = ref(false)

// 好友加载 / 编辑
const friendLoadError = ref('')
const savingFriendId = ref('')
const removingFriend = ref<Friend | null>(null)
const removingFriendBusy = ref(false)

// 申请处理
const requestBusyId = ref('')
const requestError = ref('')

// 加好友弹窗
const requestTarget = ref<ContactUser | null>(null)
const requestMessage = ref('')
const requestSubmitting = ref(false)
const requestDialogError = ref('')

let searchTimer: ReturnType<typeof setTimeout> | null = null

const incomingCount = computed(() => friends.incomingCount)

const selectedFriend = computed<Friend | null>(() => {
  const userId = selectedUser.value?.userId
  if (!userId) return null
  return friends.friends.find((friend) => friend.userId === userId) ?? null
})

const selectedPresence = computed(() => presence.stateOf(selectedUser.value?.userId))

const selectedRelation = computed<'friend' | 'outgoing' | 'incoming' | null>(() => {
  if (!selectedUser.value) return null
  if (selectedFriend.value) return 'friend'
  const result = results.value.find((item) => item.userId === selectedUser.value?.userId)
  return result?.relation ?? null
})

// ---------------------------------------------------------------------------
// 好友列表
// ---------------------------------------------------------------------------

function selectFriend(friend: Friend): void {
  selectedUser.value = {
    userId: friend.userId,
    displayName: friend.displayName,
    email: friend.email,
    avatarFileId: friend.avatarFileId ?? null,
  }
}

function clearSelection(): void {
  selectedUser.value = null
}

async function ensureFriendsLoaded(): Promise<void> {
  friendLoadError.value = ''
  try {
    await friends.ensureLoaded()
  } catch (error) {
    friendLoadError.value = friendlyError(error)
  }
}

function switchTab(next: ContactsTab): void {
  tab.value = next
  clearSelection()
  requestError.value = ''
  if (next === 'friends') {
    void ensureFriendsLoaded()
  }
}

// U-17：好友首屏骨架一次性入场（5 行，受控 stagger）；真实好友行不加 stagger
// （AGENTS.md §14.9 消息流禁令），切换页签重新出现时重播。
const loadingRef = ref<HTMLElement | null>(null)
let loadingMotion: MotionHandle | null = null

const showFriendsSkeleton = computed(
  () => tab.value === 'friends' && friends.loading && friends.friends.length === 0,
)

function playSkeletonEntrance(): void {
  const container = loadingRef.value
  if (!container) return
  loadingMotion?.cancel()
  loadingMotion = enter(Array.from(container.children), { stagger: true })
}

onMounted(playSkeletonEntrance)
onBeforeUnmount(() => {
  loadingMotion?.cancel()
  loadingMotion = null
})

watch(
  showFriendsSkeleton,
  (show) => {
    if (show) playSkeletonEntrance()
    else loadingMotion?.cancel()
  },
  { flush: 'post' },
)

// ---------------------------------------------------------------------------
// 搜索
// ---------------------------------------------------------------------------

/** 头部搜索框获得焦点时切到「搜索」页签（输入直接触发防抖搜索）。 */
function onHeaderSearchFocus(): void {
  if (tab.value !== 'search') switchTab('search')
}

function clearKeyword(): void {
  keyword.value = ''
}

async function search(immediate = false): Promise<void> {
  if (searchTimer) {
    clearTimeout(searchTimer)
    searchTimer = null
  }

  const text = keyword.value.trim()
  if (!text) {
    results.value = []
    searched.value = false
    searchError.value = ''
    return
  }

  if (!immediate) {
    searchTimer = setTimeout(() => {
      searchTimer = null
      void search(true)
    }, SEARCH_DEBOUNCE_MS)
    return
  }

  searching.value = true
  searchError.value = ''
  try {
    const response = await usersApi.search(text)
    results.value = response.users
    searched.value = true
  } catch (error) {
    results.value = []
    searched.value = true
    searchError.value = friendlyError(error)
  } finally {
    searching.value = false
  }
}

watch(keyword, () => {
  // 头部搜索框输入即切到「搜索」页签（不依赖 focus 事件，避免键盘/程序化输入时漏切）。
  if (keyword.value.trim() && tab.value !== 'search') switchTab('search')
  if (tab.value === 'search') void search()
})

function selectResult(user: UserProfile): void {
  selectedUser.value = user
}

function updateRelationLocal(userId: string, relation: 'friend' | 'outgoing'): void {
  results.value = results.value.map((user) => (user.userId === userId ? { ...user, relation } : user))
}

// ---------------------------------------------------------------------------
// 好友申请
// ---------------------------------------------------------------------------

function askRequest(user: ContactUser): void {
  requestTarget.value = user
  requestMessage.value = ''
  requestDialogError.value = ''
}

async function confirmRequest(): Promise<void> {
  const target = requestTarget.value
  if (!target) return
  const message = requestMessage.value.trim()
  if (message.length > MESSAGE_MAX_LENGTH) {
    requestDialogError.value = `附言不能超过 ${MESSAGE_MAX_LENGTH} 个字符`
    return
  }

  requestSubmitting.value = true
  requestDialogError.value = ''
  try {
    const result = await friends.sendRequest(target.userId, message || undefined)
    updateRelationLocal(target.userId, result.autoAccepted ? 'friend' : 'outgoing')
    requestTarget.value = null
  } catch (error) {
    requestDialogError.value = friendlyError(error)
  } finally {
    requestSubmitting.value = false
  }
}

async function acceptRequest(request: FriendRequest): Promise<void> {
  requestBusyId.value = request.requestId
  requestError.value = ''
  try {
    await friends.accept(request.requestId)
  } catch (error) {
    requestError.value = friendlyError(error)
  } finally {
    requestBusyId.value = ''
  }
}

async function rejectRequest(request: FriendRequest): Promise<void> {
  requestBusyId.value = request.requestId
  requestError.value = ''
  try {
    await friends.reject(request.requestId)
  } catch (error) {
    requestError.value = friendlyError(error)
  } finally {
    requestBusyId.value = ''
  }
}

async function cancelRequest(request: FriendRequest): Promise<void> {
  requestBusyId.value = request.requestId
  requestError.value = ''
  try {
    await friends.cancel(request.requestId)
  } catch (error) {
    requestError.value = friendlyError(error)
  } finally {
    requestBusyId.value = ''
  }
}

// ---------------------------------------------------------------------------
// 好友备注 / 删除
// ---------------------------------------------------------------------------

async function saveFriend(payload: { userId: string; remark?: string; groupName?: string }): Promise<void> {
  savingFriendId.value = payload.userId
  try {
    await friends.updateFriend(payload.userId, {
      remark: payload.remark,
      groupName: payload.groupName,
    })
    showToast('好友备注已保存', 'success')
  } catch (error) {
    showToast(friendlyError(error), 'error')
  } finally {
    savingFriendId.value = ''
  }
}

function askRemoveFriend(friend: Friend): void {
  removingFriend.value = friend
}

async function confirmRemoveFriend(): Promise<void> {
  const friend = removingFriend.value
  if (!friend) return
  removingFriendBusy.value = true
  try {
    await friends.removeFriend(friend.userId)
    if (selectedUser.value?.userId === friend.userId) {
      clearSelection()
    }
    removingFriend.value = null
  } catch (error) {
    showToast(friendlyError(error), 'error')
  } finally {
    removingFriendBusy.value = false
  }
}

// ---------------------------------------------------------------------------
// 会话与建群
// ---------------------------------------------------------------------------

async function startDirectChat(user: ContactUser): Promise<void> {
  if (busyUserId.value) return
  busyUserId.value = user.userId
  try {
    const conversation = await conversations.createDirect(user.userId)
    await router.push({ name: 'chat', params: { conversationId: conversation.conversationId } })
  } catch (error) {
    showToast(friendlyError(error), 'error')
  } finally {
    busyUserId.value = ''
  }
}

async function onGroupCreated(conversation: Conversation): Promise<void> {
  await router.push({ name: 'chat', params: { conversationId: conversation.conversationId } })
}

void ensureFriendsLoaded()

onBeforeUnmount(() => {
  if (searchTimer) {
    clearTimeout(searchTimer)
    searchTimer = null
  }
})
</script>

<template>
  <main class="contacts-page" :class="{ 'contacts-page--profile': selectedUser }">
    <aside class="contacts-page__list">
      <header class="contacts-page__header">
        <SearchInput
          v-model="keyword"
          class="contacts-page__searchbox"
          placeholder="搜索联系人"
          aria-label="搜索联系人"
          :maxlength="64"
          @focus="onHeaderSearchFocus"
          @escape="clearKeyword"
        />
        <button
          type="button"
          class="contacts-page__new-group"
          aria-label="新建群聊"
          @click="newGroupOpen = true"
        >
          <AppIcon name="plus" :size="16" />
          <span class="contacts-page__new-group-label">新建群聊</span>
        </button>
      </header>

      <div class="contacts-page__tabs" role="tablist" aria-label="联系人视图">
        <button
          type="button"
          role="tab"
          class="contacts-page__tab"
          :class="{ 'contacts-page__tab--active': tab === 'friends' }"
          :aria-selected="tab === 'friends'"
          @click="switchTab('friends')"
        >
          好友
        </button>
        <button
          type="button"
          role="tab"
          class="contacts-page__tab"
          :class="{ 'contacts-page__tab--active': tab === 'requests' }"
          :aria-selected="tab === 'requests'"
          @click="switchTab('requests')"
        >
          申请
          <span v-if="incomingCount > 0" class="contacts-page__tab-badge">{{ incomingCount }}</span>
        </button>
        <button
          type="button"
          role="tab"
          class="contacts-page__tab"
          :class="{ 'contacts-page__tab--active': tab === 'search' }"
          :aria-selected="tab === 'search'"
          @click="switchTab('search')"
        >
          搜索
        </button>
      </div>

      <!-- 好友 -->
      <div v-if="tab === 'friends'" class="contacts-page__section">
        <p v-if="friendLoadError" class="contacts-page__error" role="alert">{{ friendLoadError }}</p>
        <div
          v-if="friends.loading && friends.friends.length === 0"
          ref="loadingRef"
          class="contacts-page__loading"
          role="status"
          aria-label="联系人加载中"
          aria-busy="true"
        >
          <div v-for="index in 5" :key="index" class="contacts-page__loading-row">
            <div class="ec-skeleton contacts-page__loading-avatar" />
            <div class="ec-skeleton contacts-page__loading-line" />
          </div>
        </div>
        <template v-for="group in friends.groupedFriends" :key="group.groupName ?? '__ungrouped'">
          <h3 class="contacts-page__group">{{ group.groupName ?? '未分组' }}</h3>
          <ul class="contacts-page__results">
            <li v-for="friend in group.friends" :key="friend.userId">
              <button
                type="button"
                class="contacts-page__result"
                :class="{ 'contacts-page__result--active': selectedUser?.userId === friend.userId }"
                @click="selectFriend(friend)"
              >
                <Avatar
                  :display-name="friends.displayNameOf(friend.userId)"
                  :size="36"
                  :avatar-file-id="friend.avatarFileId ?? undefined"
                />
                <span class="contacts-page__result-info">
                  <span class="contacts-page__result-name">
                    <PresenceDot :online="presence.stateOf(friend.userId)" />
                    {{ friends.displayNameOf(friend.userId) }}
                  </span>
                  <span class="contacts-page__result-email">{{ friend.email }}</span>
                </span>
              </button>
            </li>
          </ul>
        </template>
        <EmptyState
          v-if="!friends.loading && friends.friends.length === 0"
          icon="contacts"
          title="还没有好友"
          description="切到「搜索」按邮箱或昵称查找同事，发送好友申请。"
        />
      </div>

      <!-- 申请 -->
      <div v-else-if="tab === 'requests'" class="contacts-page__section">
        <p v-if="requestError" class="contacts-page__error" role="alert">{{ requestError }}</p>
        <h3 class="contacts-page__group">收到的申请</h3>
        <p v-if="friends.incoming.length === 0" class="contacts-page__hint">暂无收到的好友申请。</p>
        <ul v-else class="contacts-page__requests">
          <li v-for="request in friends.incoming" :key="request.requestId" class="contacts-page__request">
            <span class="contacts-page__request-info">
              <strong class="contacts-page__request-name">{{ request.fromDisplayName }}</strong>
              <span v-if="request.message" class="contacts-page__request-message">{{ request.message }}</span>
            </span>
            <span class="contacts-page__request-actions">
              <button
                type="button"
                class="contacts-page__request-btn contacts-page__request-btn--primary"
                :disabled="requestBusyId === request.requestId"
                @click="acceptRequest(request)"
              >
                接受
              </button>
              <button
                type="button"
                class="contacts-page__request-btn"
                :disabled="requestBusyId === request.requestId"
                @click="rejectRequest(request)"
              >
                拒绝
              </button>
            </span>
          </li>
        </ul>

        <h3 class="contacts-page__group">发出的申请</h3>
        <p v-if="friends.outgoing.length === 0" class="contacts-page__hint">暂无发出的好友申请。</p>
        <ul v-else class="contacts-page__requests">
          <li v-for="request in friends.outgoing" :key="request.requestId" class="contacts-page__request">
            <span class="contacts-page__request-info">
              <strong class="contacts-page__request-name">{{ request.toDisplayName }}</strong>
              <span class="contacts-page__request-message">等待对方处理</span>
            </span>
            <span class="contacts-page__request-actions">
              <button
                type="button"
                class="contacts-page__request-btn"
                :disabled="requestBusyId === request.requestId"
                @click="cancelRequest(request)"
              >
                撤销
              </button>
            </span>
          </li>
        </ul>
      </div>

      <!-- 搜索 -->
      <div v-else class="contacts-page__section">
        <p v-if="!keyword.trim()" class="contacts-page__hint">输入邮箱或昵称搜索同事。</p>
        <p v-if="searchError" class="contacts-page__error" role="alert">{{ searchError }}</p>
        <p v-if="searching" class="contacts-page__hint">搜索中…</p>
        <ul v-else-if="results.length" class="contacts-page__results">
          <li v-for="user in results" :key="user.userId" class="contacts-page__row">
            <button
              type="button"
              class="contacts-page__row-main"
              :class="{ 'contacts-page__row-main--active': selectedUser?.userId === user.userId }"
              @click="selectResult(user)"
            >
              <Avatar
                :display-name="user.displayName"
                :size="36"
                :avatar-file-id="user.avatarFileId ?? undefined"
              />
              <span class="contacts-page__result-info">
                <span class="contacts-page__result-name">
                  <PresenceDot :online="presence.stateOf(user.userId)" />
                  {{ user.displayName }}
                </span>
                <span class="contacts-page__result-email">{{ user.email }}</span>
              </span>
            </button>
            <button
              v-if="user.relation === 'friend'"
              type="button"
              class="contacts-page__relation-btn"
              @click="selectResult(user)"
            >
              已好友
            </button>
            <button
              v-else-if="user.relation === 'outgoing'"
              type="button"
              class="contacts-page__relation-btn"
              disabled
            >
              已发送
            </button>
            <button
              v-else-if="user.relation === 'incoming'"
              type="button"
              class="contacts-page__relation-btn"
              @click="switchTab('requests')"
            >
              待处理
            </button>
            <button
              v-else
              type="button"
              class="contacts-page__relation-btn contacts-page__relation-btn--primary"
              @click="askRequest(user)"
            >
              加好友
            </button>
          </li>
        </ul>
        <EmptyState
          v-else-if="searched"
          icon="search"
          title="未找到匹配用户"
          description="换个昵称或邮箱再试试。"
        />
        <p v-else class="contacts-page__hint">输入邮箱或昵称搜索同事。</p>
      </div>
    </aside>

    <section class="contacts-page__profile">
      <div class="contacts-page__profile-header">
        <button type="button" class="contacts-page__back-list" @click="clearSelection">
          <AppIcon name="back" :size="18" />
          返回
        </button>
        <h3 class="contacts-page__profile-title">联系人资料</h3>
      </div>
      <ContactProfile
        :user="selectedUser"
        :presence="selectedPresence"
        :busy="Boolean(selectedUser && busyUserId === selectedUser.userId)"
        :friend="selectedFriend"
        :relation="selectedRelation"
        :saving="savingFriendId === selectedUser?.userId"
        :removing="Boolean(
          removingFriendBusy && removingFriend && selectedFriend && removingFriend.userId === selectedFriend.userId,
        )"
        @start-chat="startDirectChat"
        @send-request="askRequest"
        @update-friend="saveFriend"
        @remove-friend="askRemoveFriend"
      />
    </section>

    <NewGroupDialog :open="newGroupOpen" @close="newGroupOpen = false" @created="onGroupCreated" />

    <AppDialog
      :open="Boolean(requestTarget)"
      :title="`添加 ${requestTarget?.displayName ?? ''} 为好友`"
      confirm-text="发送申请"
      :busy="requestSubmitting"
      @cancel="requestTarget = null"
      @confirm="confirmRequest"
    >
      <label class="contacts-page__dialog-field">
        <span>附言（可选，最多 {{ MESSAGE_MAX_LENGTH }} 字）</span>
        <textarea
          v-model="requestMessage"
          maxlength="100"
          rows="3"
          placeholder="介绍一下自己或说明来意"
          aria-label="好友申请附言"
        />
      </label>
      <p v-if="requestDialogError" class="contacts-page__error" role="alert">{{ requestDialogError }}</p>
    </AppDialog>

    <AppDialog
      :open="Boolean(removingFriend)"
      :title="`删除好友 ${removingFriend ? friends.displayNameOf(removingFriend.userId) : ''}`"
      message="删除后将解除好友关系，聊天记录仍会保留。确定删除？"
      tone="danger"
      confirm-text="删除"
      :busy="removingFriendBusy"
      @cancel="removingFriend = null"
      @confirm="confirmRemoveFriend"
    />
  </main>
</template>

<style scoped>
.contacts-page {
  display: grid;
  /* 二级列表宽度与「消息/群聊」页共用同一 token，三处宽度保持一致。 */
  grid-template-columns: var(--ec-list-width) minmax(0, 1fr);
  min-height: 0;
  height: 100%;
  background: var(--ec-bg-canvas);
}

.contacts-page__list {
  display: flex;
  flex-direction: column;
  min-width: 0;
  min-height: 0;
  background: var(--ec-list-bg);
  border-right: 1px solid var(--ec-border);
}

.contacts-page__header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: var(--ec-space-2);
  height: var(--ec-header-height);
  padding: 0 var(--ec-space-4);
  border-bottom: 1px solid var(--ec-border-soft);
}

/* 搜索框组件（SearchInput）统一外观，这里只限制头部占用宽度。 */
.contacts-page__searchbox {
  flex: 1;
  max-width: 280px;
}

.contacts-page__new-group {
  display: inline-flex;
  gap: var(--ec-space-1);
  align-items: center;
  padding: 4px var(--ec-space-2);
  border: 1px solid var(--ec-border);
  border-radius: var(--ec-radius-full);
  background: var(--ec-surface);
  color: var(--ec-brand-600);
  font-size: var(--ec-font-size-secondary);
  font-weight: var(--ec-font-weight-medium);
  cursor: pointer;
}

.contacts-page__tabs {
  display: flex;
  gap: var(--ec-space-1);
  padding: var(--ec-space-3) var(--ec-space-4) 0;
}

.contacts-page__tab {
  display: inline-flex;
  gap: 4px;
  align-items: center;
  padding: 5px var(--ec-space-3);
  border: none;
  border-radius: var(--ec-radius-full);
  background: transparent;
  color: var(--ec-text-secondary);
  font-size: var(--ec-font-size-secondary);
  font-weight: var(--ec-font-weight-medium);
  cursor: pointer;
}

.contacts-page__tab--active {
  background: var(--ec-brand-050);
  color: var(--ec-brand-600);
  font-weight: var(--ec-font-weight-semibold);
}

.contacts-page__tab-badge {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  min-width: 16px;
  height: 16px;
  padding: 0 4px;
  border-radius: var(--ec-radius-full);
  background: var(--ec-brand-600);
  color: var(--ec-text-inverse);
  font-size: 10px;
  font-weight: var(--ec-font-weight-semibold);
  line-height: 1;
}

.contacts-page__section {
  display: flex;
  flex-direction: column;
  gap: var(--ec-space-2);
  flex: 1;
  min-height: 0;
  padding: var(--ec-space-3) var(--ec-space-4) var(--ec-space-4);
  overflow-y: auto;
}

.contacts-page__group {
  margin: var(--ec-space-2) 0 0;
  color: var(--ec-text-tertiary);
  font-size: var(--ec-font-size-small);
  font-weight: var(--ec-font-weight-medium);
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
  color: var(--ec-text-tertiary);
  font-size: var(--ec-font-size-secondary);
}

/* U-01：好友列表首屏骨架，行高对齐真实好友行。 */
.contacts-page__loading-row {
  display: flex;
  gap: var(--ec-space-3);
  align-items: center;
  min-height: 56px;
  padding: var(--ec-space-2) var(--ec-space-3);
}

.contacts-page__loading-avatar {
  width: 40px;
  height: 40px;
  border-radius: var(--ec-radius-full);
}

.contacts-page__loading-line {
  width: min(220px, 55%);
  height: 12px;
}

.contacts-page__results,
.contacts-page__requests {
  display: flex;
  flex-direction: column;
  gap: 2px;
  margin: 0;
  padding: 0;
  list-style: none;
}

.contacts-page__result,
.contacts-page__row-main {
  display: flex;
  gap: var(--ec-space-2);
  align-items: center;
  width: 100%;
  min-width: 0;
  padding: var(--ec-space-2);
  border: none;
  border-radius: var(--ec-radius-md);
  background: transparent;
  text-align: left;
  cursor: pointer;
}

.contacts-page__result--active,
.contacts-page__row-main--active {
  background: var(--ec-brand-050);
}

.contacts-page__row {
  display: flex;
  gap: var(--ec-space-2);
  align-items: center;
}

.contacts-page__row-main {
  flex: 1;
}

.contacts-page__result-info {
  display: flex;
  flex-direction: column;
  gap: 2px;
  min-width: 0;
}

.contacts-page__result-name {
  display: inline-flex;
  gap: 6px;
  align-items: center;
  color: var(--ec-text-primary);
  font-size: var(--ec-font-size-body);
  font-weight: var(--ec-font-weight-medium);
}

.contacts-page__result-email {
  overflow: hidden;
  color: var(--ec-text-tertiary);
  font-size: var(--ec-font-size-small);
  text-overflow: ellipsis;
  white-space: nowrap;
}

.contacts-page__relation-btn {
  flex: none;
  padding: 4px var(--ec-space-2);
  border: 1px solid var(--ec-border);
  border-radius: var(--ec-radius-full);
  background: var(--ec-surface);
  color: var(--ec-text-secondary);
  font-size: var(--ec-font-size-small);
  cursor: pointer;
}

.contacts-page__relation-btn--primary {
  border-color: var(--ec-brand-600);
  color: var(--ec-brand-600);
  font-weight: var(--ec-font-weight-medium);
}

.contacts-page__relation-btn:disabled {
  opacity: 0.6;
  cursor: not-allowed;
}

.contacts-page__request {
  display: flex;
  gap: var(--ec-space-2);
  align-items: center;
  justify-content: space-between;
  padding: var(--ec-space-2);
  border-radius: var(--ec-radius-md);
}

.contacts-page__request-info {
  display: flex;
  flex-direction: column;
  gap: 2px;
  min-width: 0;
}

.contacts-page__request-name {
  color: var(--ec-text-primary);
  font-size: var(--ec-font-size-body);
}

.contacts-page__request-message {
  overflow: hidden;
  color: var(--ec-text-tertiary);
  font-size: var(--ec-font-size-small);
  text-overflow: ellipsis;
  white-space: nowrap;
}

.contacts-page__request-actions {
  display: flex;
  flex: none;
  gap: var(--ec-space-1);
}

.contacts-page__request-btn {
  padding: 4px var(--ec-space-2);
  border: 1px solid var(--ec-border);
  border-radius: var(--ec-radius-full);
  background: var(--ec-surface);
  color: var(--ec-text-secondary);
  font-size: var(--ec-font-size-small);
  cursor: pointer;
}

.contacts-page__request-btn--primary {
  border-color: var(--ec-brand-600);
  background: var(--ec-brand-600);
  color: var(--ec-text-inverse);
}

.contacts-page__request-btn:disabled {
  opacity: 0.6;
  cursor: not-allowed;
}

.contacts-page__dialog-field {
  display: flex;
  flex-direction: column;
  gap: var(--ec-space-1);
  color: var(--ec-text-secondary);
  font-size: var(--ec-font-size-secondary);
}

.contacts-page__dialog-field textarea {
  width: 100%;
  padding: var(--ec-space-2) var(--ec-space-3);
  border: 1px solid var(--ec-border);
  border-radius: var(--ec-radius-input);
  background: var(--ec-surface);
  color: var(--ec-text-primary);
  font-family: inherit;
  font-size: var(--ec-font-size-body);
  resize: vertical;
}

.contacts-page__dialog-field textarea:focus-visible {
  border-color: var(--ec-brand-500);
  outline: none;
  box-shadow: var(--ec-focus-shadow);
}

.contacts-page__profile {
  display: flex;
  flex-direction: column;
  min-width: 0;
  min-height: 0;
  overflow: hidden;
}

.contacts-page__profile-header {
  display: none;
}

.contacts-page__back-list {
  display: inline-flex;
  gap: 4px;
  align-items: center;
  padding: 4px var(--ec-space-2);
  border: none;
  border-radius: var(--ec-radius-full);
  background: transparent;
  color: var(--ec-brand-600);
  font-size: var(--ec-font-size-secondary);
  cursor: pointer;
}

/* 悬停效果仅在鼠标精确指针设备生效，触屏不产生粘滞 hover。 */
@media (hover: hover) and (pointer: fine) {
  .contacts-page__new-group:hover {
    background: var(--ec-brand-050);
  }

  .contacts-page__tab:hover:not(.contacts-page__tab--active) {
    background: var(--ec-surface-hover);
    color: var(--ec-text-primary);
  }

  .contacts-page__result:hover,
  .contacts-page__row-main:hover {
    background: var(--ec-surface-hover);
  }

  .contacts-page__result--active:hover,
  .contacts-page__row-main--active:hover {
    background: var(--ec-brand-050);
  }

  .contacts-page__relation-btn:hover:not(:disabled),
  .contacts-page__request-btn:hover:not(:disabled) {
    background: var(--ec-brand-050);
  }

}

/* 801~1199：与聊天页一致，列表收窄到 260px。 */
@media (max-width: 1199px) {
  .contacts-page {
    grid-template-columns: 260px minmax(0, 1fr);
  }
}

/* ≤800：单列；选中资料后隐藏列表，顶部提供返回条。 */
@media (max-width: 800px) {
  .contacts-page {
    grid-template-columns: minmax(0, 1fr);
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

  .contacts-page__header {
    height: var(--ec-header-height-mobile);
    padding: 0 var(--ec-space-3) 0 var(--ec-space-4);
  }

  .contacts-page__searchbox {
    max-width: none;
  }

  .contacts-page__new-group {
    min-height: 36px;
    border-radius: var(--ec-radius-full);
  }

  .contacts-page__new-group-label {
    display: none;
  }

  .contacts-page__tabs {
    gap: var(--ec-space-2);
    padding: var(--ec-space-3) var(--ec-space-4) 0;
  }

  .contacts-page__tab {
    min-height: 40px;
    padding: 0 var(--ec-space-3);
  }

  .contacts-page__section {
    padding: var(--ec-space-2) var(--ec-space-3) var(--ec-space-4);
  }

  .contacts-page__result,
  .contacts-page__row-main {
    min-height: var(--ec-row-min-height-mobile);
    border-radius: var(--ec-radius-md);
  }

  .contacts-page__result + .contacts-page__result,
  .contacts-page__row + .contacts-page__row {
    border-top: 1px solid var(--ec-border-soft);
  }

  .contacts-page__relation-btn {
    min-height: 36px;
    padding: 0 var(--ec-space-3);
  }

  .contacts-page__request-btn {
    min-height: 36px;
    padding: 0 var(--ec-space-3);
  }

  .contacts-page__profile-header {
    display: flex;
    position: sticky;
    top: 0;
    z-index: 2;
    gap: var(--ec-space-2);
    align-items: center;
    height: var(--ec-header-height-mobile);
    padding: 0 var(--ec-space-3) 0 var(--ec-space-2);
    background: var(--ec-surface);
    border-bottom: 1px solid var(--ec-border-soft);
  }

  .contacts-page__profile-title {
    margin: 0;
    color: var(--ec-text-primary);
    font-size: var(--ec-font-size-mobile-header);
    font-weight: var(--ec-font-weight-semibold);
  }

  .contacts-page__back-list {
    min-height: var(--ec-touch-target);
  }
}
</style>
