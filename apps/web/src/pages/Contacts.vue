// 联系人页（UI-03 批次2 双栏 + F-17 批次5 好友体系）：
// 左栏「好友 / 申请」两块切换，右栏资料区（ContactProfile）。
// - 好友：按分组展示（备注优先显示），选中可查看资料、维护备注/分组、删除好友；
// - 申请：收到的申请可接受/拒绝，发出的可撤销（实时推送由 friends store 接收）；
// - 搜索：头部搜索框输入即内联搜索「联系人 + 聊天信息」，好友与会话成员归「联系人」，陌生人单独分类；
//   与消息页共用同一输入框样式与结果区，输入期间隐藏页签、结果直接替换列表；清空后恢复原页签。
//   点聊天信息直接跳到对应会话并定位（与全局搜索面板同一跳转方式）。搜索结果不含他人邮箱。
<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { useRouter } from 'vue-router'
import { blocksApi } from '../api/blocks'
import type { UserProfile } from '../api/users'
import type { Conversation, Message } from '../types/frames'
import type { Friend, FriendRequest } from '../types/frames'
import AppDialog from '../components/AppDialog.vue'
import SearchInput from '../components/SearchInput.vue'
import AppIcon from '../components/AppIcon.vue'
import Avatar from '../components/Avatar.vue'
import ContactProfile from '../components/ContactProfile.vue'
import EmptyState from '../components/EmptyState.vue'
import InlineSearchResults from '../components/InlineSearchResults.vue'
import NewGroupDialog from '../components/NewGroupDialog.vue'
import PresenceDot from '../components/PresenceDot.vue'
import { friendlyError } from '../core/errorMessages'
import { useInlineSearch } from '../core/inlineSearch'
import { enter, type MotionHandle } from '../core/motion'
import { showToast } from '../core/toast'
import { useConversationsStore } from '../stores/conversations'
import { useFriendsStore } from '../stores/friends'
import { usePresenceStore } from '../stores/presence'

type ContactsTab = 'friends' | 'requests'

/** 资料区所需的最小用户信息（UserProfile 结构兼容；邮箱仅好友与本人有值）。 */
interface ContactUser {
  userId: string
  displayName: string
  email?: string
  avatarFileId?: string | null
}

const MESSAGE_MAX_LENGTH = 100

const router = useRouter()
const conversations = useConversationsStore()
const presence = usePresenceStore()
const friends = useFriendsStore()

const tab = ref<ContactsTab>('friends')

// 搜索态（内联搜索：联系人 + 聊天信息；陌生人单独分类，默认不展示）
const {
  query: searchQuery,
  keyword: searchKeyword,
  category: searchCategory,
  loading: searchLoading,
  error: searchError,
  selfUserId,
  contactMatches,
  strangerMatches,
  messageMatches,
  clear: clearSearch,
  setCategory: setSearchCategory,
  updateUserRelation,
} = useInlineSearch()

// 选中与操作态
const selectedUser = ref<ContactUser | null>(null)
const busyUserId = ref('')
const newGroupOpen = ref(false)

// 拉黑（C1b）：拉黑是有方向的关系，页面只维护「被我拉黑」的用户 ID 集合。
const blockedUserIds = ref<string[]>([])
const blockBusyUserId = ref('')

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
  const userId = selectedUser.value.userId
  const result =
    contactMatches.value.find((item) => item.userId === userId) ??
    strangerMatches.value.find((item) => item.userId === userId)
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
// 内联搜索（联系人 + 聊天信息，陌生人单独分类）
// ---------------------------------------------------------------------------

function clearKeyword(): void {
  clearSearch()
}

function searchUserOf(userId: string): UserProfile | undefined {
  return (
    contactMatches.value.find((item) => item.userId === userId) ??
    strangerMatches.value.find((item) => item.userId === userId)
  )
}

function selectSearchUser(user: UserProfile): void {
  selectedUser.value = user
}

function requestSearchFriend(userId: string): void {
  const user = searchUserOf(userId)
  if (user) askRequest(user)
}

/** 点聊天信息：跳到对应会话并定位到该消息（与全局搜索面板同一跳转方式）。 */
async function selectSearchMessage(message: Message): Promise<void> {
  await router.push({
    name: 'chat',
    params: { conversationId: message.conversationId },
    query: { jumpSeq: String(message.seq), jumpMessageId: message.messageId },
  })
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
    updateUserRelation(target.userId, result.autoAccepted ? 'friend' : 'outgoing')
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
// 拉黑（C1b）
// ---------------------------------------------------------------------------

async function ensureBlocksLoaded(): Promise<void> {
  try {
    const result = await blocksApi.list()
    blockedUserIds.value = result.blocks.map((item) => item.userId)
  } catch {
    // 拉黑列表加载失败不阻塞联系人页：资料卡按未拉黑展示，真正操作时服务端仍会校验。
  }
}

async function toggleBlock(user: ContactUser): Promise<void> {
  if (blockBusyUserId.value) return
  const blocked = blockedUserIds.value.includes(user.userId)
  blockBusyUserId.value = user.userId
  try {
    if (blocked) {
      await blocksApi.unblock(user.userId)
      blockedUserIds.value = blockedUserIds.value.filter((userId) => userId !== user.userId)
      showToast('已解除拉黑', 'success')
    } else {
      await blocksApi.block(user.userId)
      blockedUserIds.value = [...blockedUserIds.value, user.userId]
      showToast('已拉黑：双方不能私聊、申请好友或邀请入群', 'success')
    }
  } catch (error) {
    showToast(friendlyError(error), 'error')
  } finally {
    blockBusyUserId.value = ''
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
void ensureBlocksLoaded()
// 内联搜索的聊天信息行要用会话标题与成员昵称（SearchResultItem 从会话 store 取），
// 联系人页不加载会话列表时会退化成会话 ID 与「未知用户」，这里幂等补齐。
void conversations.load().catch(() => undefined)
</script>

<template>
  <main class="contacts-page" :class="{ 'contacts-page--profile': selectedUser }">
    <aside class="contacts-page__list">
      <header class="contacts-page__header">
        <SearchInput
          v-model="searchQuery"
          class="contacts-page__searchbox"
          placeholder="搜索联系人或聊天信息"
          aria-label="搜索联系人或聊天信息"
          :maxlength="64"
          @escape="clearKeyword"
        />
        <button
          type="button"
          class="contacts-page__new-group"
          aria-label="新建群聊"
          title="新建群聊"
          @click="newGroupOpen = true"
        >
          <AppIcon name="plus" :size="18" />
        </button>
      </header>

      <div
        v-if="!searchKeyword"
        class="contacts-page__tabs"
        role="tablist"
        aria-label="联系人视图"
      >
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
      </div>

      <!-- 好友 -->
      <div v-if="!searchKeyword && tab === 'friends'" class="contacts-page__section">
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
          description="在顶部搜索框按昵称或完整邮箱查找同事，发送好友申请。"
        />
      </div>

      <!-- 申请 -->
      <div v-else-if="!searchKeyword && tab === 'requests'" class="contacts-page__section">
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

      <!-- 搜索：联系人 + 聊天信息（陌生人单独分类，默认不展示）；与消息页结果区同样占满列表区 -->
      <div v-else class="contacts-page__section contacts-page__section--search">
        <InlineSearchResults
          :query="searchKeyword"
          :category="searchCategory"
          :loading="searchLoading"
          :error="searchError"
          :conversations="[]"
          :contacts="contactMatches"
          :strangers="strangerMatches"
          :messages="messageMatches"
          :self-user-id="selfUserId"
          :active-user-id="selectedUser?.userId ?? ''"
          show-relation-actions
          @update:category="setSearchCategory"
          @select-user="selectSearchUser"
          @select-message="selectSearchMessage"
          @request-friend="requestSearchFriend"
        />
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
        :blocked="Boolean(selectedUser && blockedUserIds.includes(selectedUser.userId))"
        :block-busy="blockBusyUserId === selectedUser?.userId"
        @start-chat="startDirectChat"
        @send-request="askRequest"
        @update-friend="saveFriend"
        @remove-friend="askRemoveFriend"
        @toggle-block="toggleBlock"
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
  flex: none;
  align-items: center;
  justify-content: center;
  width: 36px;
  height: 36px;
  padding: 0;
  /* 必要控件轮廓走 --ec-border-strong（≥3:1）。 */
  border: 1px solid var(--ec-border-strong);
  border-radius: var(--ec-radius-full);
  background: var(--ec-surface);
  color: var(--ec-brand-600);
  cursor: pointer;
}

.contacts-page__new-group:focus-visible {
  outline: var(--ec-focus-width) solid var(--ec-focus-ring);
  outline-offset: var(--ec-focus-offset);
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

/* 内联搜索结果自带滚动与内边距（与消息页同构），这里只负责占满列表区。 */
.contacts-page__section--search {
  gap: 0;
  padding: 0;
  overflow: hidden;
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

.contacts-page__result {
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

.contacts-page__result--active {
  background: var(--ec-brand-050);
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
    width: var(--ec-touch-target);
    height: var(--ec-touch-target);
    border-radius: var(--ec-radius-full);
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

  .contacts-page__section--search {
    padding: 0;
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
