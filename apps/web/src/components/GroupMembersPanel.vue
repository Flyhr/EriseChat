<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { conversationsApi } from '../api/conversations'
import { filesApi } from '../api/files'
import { usersApi, type UserProfile } from '../api/users'
import AppDialog from './AppDialog.vue'
import AppIcon from './AppIcon.vue'
import Avatar from './Avatar.vue'
import MemberProfileCard from './MemberProfileCard.vue'
import PresenceDot from './PresenceDot.vue'
import { friendlyError } from '../core/errorMessages'
import { compressImageFile } from '../core/imageCompress'
import { groupAvatarFileIdOf, isAcceptableAvatarFile } from '../core/groupAvatar'
import {
  ANNOUNCEMENT_MAX_LENGTH,
  GROUP_TITLE_MAX_LENGTH,
  announcementCountOf,
  announcementIsEmpty,
  announcementPreview,
  canEditGroupProfile,
  normalizeAnnouncementInput,
  validateGroupTitle,
} from '../core/groupProfile'
import { canTransferOwner, sortMembers } from '../core/groupChat'
import { showToast } from '../core/toast'
import { useConversationsStore } from '../stores/conversations'
import { useFriendsStore } from '../stores/friends'
import { usePresenceStore } from '../stores/presence'
import type { Conversation, ConversationMember } from '../types/frames'

// 群成员管理面板（M3-08）：成员列表（群主优先）、邀请成员、移除成员（仅群主）、退出群聊（非群主）。
// 所有操作以服务端为准：成功后重新 GET 会话并 emit updated，由 Chat 页写回 store；
// 被移出/退出由服务端 conv.updated.removedConversationIds 推送（本面板主动退出时也直接 emit left）。
// M3-09：成员行展示在线状态圆点，头部展示在线人数。
// UI-02（计划 §5.6 group 抽屉）：本组件改为抽屉内容，遮罩/Esc/焦点返回统一由 DrawerShell 负责。
// F-41：点击成员行打开资料浮窗，可发起单聊或好友申请（好友关系以 friends store 为准）。
const props = defineProps<{
  conversation: Conversation
  selfUserId: string
}>()

const emit = defineEmits<{
  updated: [conversation: Conversation]
  left: [conversationId: string]
  /** F-41：浮窗「发送消息」创建/复用单聊后，交由页面跳转。 */
  'open-chat': [conversationId: string]
}>()

const presence = usePresenceStore()
const friends = useFriendsStore()
const conversations = useConversationsStore()

const busy = ref(false)
const errorMessage = ref('')
/** 待确认的危险操作（移除成员 / 退出群聊 / 转让群主），由自绘 AppDialog 二次确认。 */
const confirmState = ref<{ kind: 'remove'; member: ConversationMember } | { kind: 'leave' } | null>(null)
/** 待确认的群主转让目标（F-24）。 */
const transferState = ref<ConversationMember | null>(null)
const transferSubmitting = ref(false)
const inviteOpen = ref(false)
const inviteKeyword = ref('')
const inviteResults = ref<UserProfile[]>([])
const selectedInviteIds = ref<string[]>([])
const searching = ref(false)

// 群资料（F-14）：群主可编辑群名与公告，其余成员只读。
const canEditProfile = computed(() => canEditGroupProfile(props.conversation, props.selfUserId))
const titleDialogOpen = ref(false)
const titleDraft = ref('')
const announcementDialogOpen = ref(false)
const announcementDraft = ref('')
const announcementExpanded = ref(false)
const clearAnnouncementOpen = ref(false)
const profileSaving = ref(false)
// 群头像（F-23）：群主可更换/移除，客户端先压到 512 长边、约 200KB 再上传。
const avatarUploading = ref(false)
const avatarFileInput = ref<HTMLInputElement | null>(null)
const removeAvatarOpen = ref(false)
const announcementText = computed(() => props.conversation.announcement ?? '')
const groupAvatarFileId = computed(() => groupAvatarFileIdOf(props.conversation))
const hasGroupAvatarImage = computed(() => groupAvatarFileId.value !== null)
const announcementPreviewText = computed(() => announcementPreview(announcementText.value))
const hasAnnouncement = computed(() => !announcementIsEmpty(announcementText.value))
const titleDraftLength = computed(() => Array.from(titleDraft.value).length)
const announcementDraftLength = computed(() => announcementCountOf(announcementDraft.value))

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

// F-41：成员资料浮窗。anchor 为被点击成员行的视口矩形，桌面据此把浮窗开在成员列表左侧；
// anchorEl 保留该行元素，视口变化或成员列表滚动后据此重算锚点，避免浮窗与行脱位。
const memberCard = ref<{
  member: ConversationMember
  anchorEl: HTMLElement | null
  anchor: { left: number; right: number; top: number }
} | null>(null)
const memberCardBusy = ref(false)
const memberCardRequesting = ref(false)
const memberCardError = ref('')
const cardMember = computed(() => memberCard.value?.member ?? null)
const cardDisplayName = computed(() => {
  const member = cardMember.value
  if (!member) return ''
  return friends.displayNameOf(member.userId) || member.displayName
})
/** 与当前用户的关系：好友列表与待处理申请均来自 friends store（登录/重连时全量拉取）。 */
const cardRelation = computed<'friend' | 'outgoing' | 'incoming' | 'none'>(() => {
  const userId = cardMember.value?.userId
  if (!userId) return 'none'
  if (friends.isFriend(userId)) return 'friend'
  if (friends.outgoing.some((request) => request.toUserId === userId)) return 'outgoing'
  if (friends.incoming.some((request) => request.fromUserId === userId)) return 'incoming'
  return 'none'
})

function openMemberCard(member: ConversationMember, event: MouseEvent): void {
  const target = event.currentTarget
  const rect = target instanceof HTMLElement ? target.getBoundingClientRect() : null
  memberCardError.value = ''
  memberCard.value = {
    member,
    anchorEl: target instanceof HTMLElement ? target : null,
    anchor: rect
      ? { left: rect.left, right: rect.right, top: rect.top }
      : { left: 0, right: 0, top: 0 },
  }
}

function closeMemberCard(): void {
  memberCard.value = null
  memberCardError.value = ''
}

// 视口缩放/旋转与成员列表滚动会让先前记录的矩形失效：同一帧内合并事件后按行元素重算。
let anchorFrame = 0
function syncMemberCardAnchor(): void {
  if (!memberCard.value || anchorFrame) return
  anchorFrame = requestAnimationFrame(() => {
    anchorFrame = 0
    const current = memberCard.value
    if (!current) return
    // 行元素已随列表重渲染卸载时直接关闭，避免浮窗停在旧位置。
    if (!current.anchorEl?.isConnected) {
      closeMemberCard()
      return
    }
    const rect = current.anchorEl.getBoundingClientRect()
    const { left, right, top } = current.anchor
    if (left === rect.left && right === rect.right && top === rect.top) return
    current.anchor = { left: rect.left, right: rect.right, top: rect.top }
  })
}

onMounted(() => {
  window.addEventListener('resize', syncMemberCardAnchor, { passive: true })
  window.addEventListener('scroll', syncMemberCardAnchor, { capture: true, passive: true })
})

onBeforeUnmount(() => {
  window.removeEventListener('resize', syncMemberCardAnchor)
  window.removeEventListener('scroll', syncMemberCardAnchor, true)
  if (anchorFrame) cancelAnimationFrame(anchorFrame)
  anchorFrame = 0
})

/** 浮窗「发送消息」：创建或复用与对方的单聊（服务端按 direct_key 去重），再交给页面跳转。 */
async function startChatWithCardMember(): Promise<void> {
  const member = cardMember.value
  if (!member || memberCardBusy.value) return
  memberCardBusy.value = true
  memberCardError.value = ''
  try {
    const conversation = await conversations.createDirect(member.userId)
    closeMemberCard()
    emit('open-chat', conversation.conversationId)
  } catch (error) {
    memberCardError.value = friendlyError(error)
  } finally {
    memberCardBusy.value = false
  }
}

/** 浮窗「添加好友」：发送好友申请；对方此前也申请过时服务端自动接受（store 内提示）。 */
async function addFriendFromCard(): Promise<void> {
  const member = cardMember.value
  if (!member || memberCardRequesting.value) return
  memberCardRequesting.value = true
  memberCardError.value = ''
  try {
    await friends.sendRequest(member.userId)
  } catch (error) {
    memberCardError.value = friendlyError(error)
  } finally {
    memberCardRequesting.value = false
  }
}

/** 浮窗「转让群主」：先收起浮窗，再交给既有二次确认弹窗（成员行内不再提供入口）。 */
function transferFromCard(): void {
  const member = cardMember.value
  if (!member) return
  closeMemberCard()
  askTransferOwner(member)
}

/** 浮窗「移出群聊」：先收起浮窗，再交给既有二次确认弹窗。 */
function removeFromCard(): void {
  const member = cardMember.value
  if (!member) return
  closeMemberCard()
  askRemoveMember(member)
}

// 好友数据由 App 在登录/重连时拉取；面板首次打开补一次兜底，避免把好友误显示为陌生人。
void friends.ensureLoaded().catch(() => undefined)

// 切换会话后浮窗仍指向上一会话的成员，关闭以免误操作。
watch(
  () => props.conversation.conversationId,
  () => closeMemberCard(),
)

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

/** 群主转让（F-24）：成员行「转让」入口是否可用；最终权限以服务端校验为准。 */
function canTransferTo(member: ConversationMember): boolean {
  return !busy.value && canTransferOwner(props.conversation, props.selfUserId, member.userId)
}

function askTransferOwner(member: ConversationMember): void {
  if (!canTransferTo(member)) return
  transferState.value = member
}

async function confirmTransferOwner(): Promise<void> {
  const target = transferState.value
  if (!target || transferSubmitting.value) return
  transferSubmitting.value = true
  try {
    const updated = await conversationsApi.transferOwner(
      props.conversation.conversationId,
      target.userId,
    )
    emit('updated', updated)
    // 角色以服务端广播的 conv.updated 为准；若推送稍晚，再 GET 一次兜底，保证徽标与操作按钮及时刷新。
    await refreshConversation()
    showToast('已转让群主', 'success')
  } catch (error) {
    showToast(friendlyError(error), 'error')
  } finally {
    transferState.value = null
    transferSubmitting.value = false
  }
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

function openTitleDialog(): void {
  if (!canEditProfile.value) return
  titleDraft.value = props.conversation.title
  titleDialogOpen.value = true
}

async function saveTitle(): Promise<void> {
  if (!canEditProfile.value || profileSaving.value) return
  const validation = validateGroupTitle(titleDraft.value)
  if (!validation.ok) {
    showToast(validation.message, 'error')
    return
  }

  profileSaving.value = true
  try {
    const updated = await conversationsApi.updateGroupProfile(props.conversation.conversationId, {
      title: validation.value,
    })
    emit('updated', updated)
    titleDialogOpen.value = false
    showToast('群名已更新', 'success')
  } catch (error) {
    showToast(friendlyError(error), 'error')
  } finally {
    profileSaving.value = false
  }
}

function openAnnouncementDialog(): void {
  if (!canEditProfile.value) return
  announcementDraft.value = announcementText.value
  announcementDialogOpen.value = true
}

async function saveAnnouncement(): Promise<void> {
  if (!canEditProfile.value || profileSaving.value) return
  const normalized = normalizeAnnouncementInput(announcementDraft.value)
  profileSaving.value = true
  try {
    const updated = await conversationsApi.updateGroupProfile(props.conversation.conversationId, {
      announcement: normalized,
    })
    emit('updated', updated)
    announcementDialogOpen.value = false
    announcementExpanded.value = false
    showToast('群公告已更新', 'success')
  } catch (error) {
    showToast(friendlyError(error), 'error')
  } finally {
    profileSaving.value = false
  }
}

function askClearAnnouncement(): void {
  if (!canEditProfile.value) return
  clearAnnouncementOpen.value = true
}

async function clearAnnouncement(): Promise<void> {
  if (!canEditProfile.value || profileSaving.value) return
  profileSaving.value = true
  try {
    const updated = await conversationsApi.updateGroupProfile(props.conversation.conversationId, {
      announcement: '',
    })
    emit('updated', updated)
    clearAnnouncementOpen.value = false
    announcementDialogOpen.value = false
    showToast('群公告已清空', 'success')
  } catch (error) {
    showToast(friendlyError(error), 'error')
    clearAnnouncementOpen.value = false
  } finally {
    profileSaving.value = false
  }
}

function triggerAvatarPick(): void {
  if (!canEditProfile.value || profileSaving.value || avatarUploading.value) return
  avatarFileInput.value?.click()
}

async function onAvatarPicked(event: Event): Promise<void> {
  const input = event.target as HTMLInputElement
  const file = input.files?.[0]
  input.value = ''
  if (!file || !canEditProfile.value) return
  if (!isAcceptableAvatarFile(file)) {
    showToast('群头像需为 5MB 以内的图片文件', 'error')
    return
  }

  avatarUploading.value = true
  try {
    const compressed = await compressImageFile(file, {
      maxEdge: 512,
      targetBytes: 200 * 1024,
    })
    const uploaded = await filesApi.upload(compressed)
    const updated = await conversationsApi.updateGroupProfile(props.conversation.conversationId, {
      avatarFileId: uploaded.fileId,
    })
    emit('updated', updated)
    showToast('群头像已更新', 'success')
  } catch (error) {
    showToast(friendlyError(error), 'error')
  } finally {
    avatarUploading.value = false
  }
}

function askRemoveAvatar(): void {
  if (!canEditProfile.value) return
  removeAvatarOpen.value = true
}

async function removeAvatar(): Promise<void> {
  if (!canEditProfile.value || profileSaving.value) return
  profileSaving.value = true
  try {
    const updated = await conversationsApi.updateGroupProfile(props.conversation.conversationId, {
      avatarFileId: '',
    })
    emit('updated', updated)
    removeAvatarOpen.value = false
    showToast('群头像已移除', 'success')
  } catch (error) {
    showToast(friendlyError(error), 'error')
    removeAvatarOpen.value = false
  } finally {
    profileSaving.value = false
  }
}
</script>

<template>
  <div class="group-panel">
    <p class="group-panel__subtitle">{{ members.length }} 位成员 · 在线 {{ onlineCount }} 人</p>

    <p v-if="errorMessage" class="group-panel__error" role="alert">{{ errorMessage }}</p>

    <section v-if="conversation.type === 'group'" class="group-panel__section group-panel__profile">
      <div class="group-panel__profile-avatar">
        <Avatar :display-name="conversation.title" :avatar-file-id="groupAvatarFileId ?? undefined" :size="56" />
        <div v-if="canEditProfile" class="group-panel__profile-avatar-actions">
          <input
            ref="avatarFileInput"
            type="file"
            class="group-panel__file-input"
            accept="image/*"
            @change="onAvatarPicked"
          />
          <button
            type="button"
            class="group-panel__link"
            :disabled="profileSaving || avatarUploading"
            @click="triggerAvatarPick"
          >
            <AppIcon name="edit" :size="14" /> {{ avatarUploading ? '上传中…' : '更换头像' }}
          </button>
          <button
            v-if="hasGroupAvatarImage"
            type="button"
            class="group-panel__link"
            :disabled="profileSaving || avatarUploading"
            @click="askRemoveAvatar"
          >
            移除头像
          </button>
        </div>
      </div>
      <div class="group-panel__profile-head">
        <h3 class="group-panel__profile-title">群资料</h3>
        <button
          v-if="canEditProfile"
          type="button"
          class="group-panel__link"
          :disabled="busy || profileSaving"
          @click="openTitleDialog"
        >
          <AppIcon name="edit" :size="14" /> 修改群名
        </button>
      </div>
      <p class="group-panel__profile-name" :title="conversation.title">{{ conversation.title }}</p>
      <div class="group-panel__announcement">
        <span class="group-panel__announcement-label">群公告</span>
        <template v-if="hasAnnouncement">
          <p class="group-panel__announcement-text">
            {{ announcementExpanded ? announcementText : announcementPreviewText.text }}
          </p>
          <button
            v-if="announcementPreviewText.truncated || announcementExpanded"
            type="button"
            class="group-panel__link"
            @click="announcementExpanded = !announcementExpanded"
          >
            {{ announcementExpanded ? '收起' : '展开' }}
          </button>
        </template>
        <p v-else class="group-panel__hint">暂无公告</p>
        <button
          v-if="canEditProfile"
          type="button"
          class="group-panel__link"
          :disabled="busy || profileSaving"
          @click="openAnnouncementDialog"
        >
          <AppIcon name="edit" :size="14" /> {{ hasAnnouncement ? '编辑公告' : '添加公告' }}
        </button>
      </div>
      <p v-if="!canEditProfile" class="group-panel__hint">仅群主可修改群资料</p>
    </section>

    <!-- UI-D P3：参考图 03 的分区标题与成员计数。 -->
    <h3 class="group-panel__section-title">群成员 · {{ members.length }}</h3>

    <ul class="group-panel__members">
      <li v-for="member in members" :key="member.userId" class="group-panel__member">
        <button
          type="button"
          class="group-panel__member-main"
          :aria-label="`查看 ${member.displayName} 的资料`"
          aria-haspopup="dialog"
          @click="openMemberCard(member, $event)"
        >
          <Avatar :display-name="member.displayName" :avatar-file-id="member.avatarFileId" :size="28" />
          <span class="group-panel__member-name">
            <PresenceDot :online="presence.stateOf(member.userId)" />
            <span class="group-panel__member-text">{{ member.displayName }}</span>
            <em v-if="member.userId === selfUserId" class="group-panel__me">（我）</em>
          </span>
          <span v-if="member.role === 'owner'" class="group-panel__role">群主</span>
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
      <p v-else class="group-panel__hint">群主不能退出群聊；如需退出，请先在成员列表把群主转让给其他成员。</p>
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
    <AppDialog
      :open="transferState !== null"
      title="转让群主"
      :message="`转让后你将变为普通成员，无法再管理群资料与成员。确定转让给「${transferState?.displayName ?? ''}」吗？`"
      confirm-text="转让"
      tone="danger"
      :busy="transferSubmitting"
      @confirm="confirmTransferOwner"
      @cancel="transferState = null"
    />
    <AppDialog
      :open="titleDialogOpen"
      title="修改群名"
      confirm-text="保存"
      :busy="profileSaving"
      wide
      @confirm="saveTitle"
      @cancel="titleDialogOpen = false"
    >
      <label class="group-panel__field">
        <span class="group-panel__field-label">群名称</span>
        <input v-model="titleDraft" type="text" :maxlength="GROUP_TITLE_MAX_LENGTH" />
      </label>
      <p class="group-panel__count">{{ titleDraftLength }}/{{ GROUP_TITLE_MAX_LENGTH }}</p>
    </AppDialog>

    <AppDialog
      :open="announcementDialogOpen"
      title="编辑群公告"
      confirm-text="保存"
      :busy="profileSaving"
      wide
      @confirm="saveAnnouncement"
      @cancel="announcementDialogOpen = false"
    >
      <label class="group-panel__field">
        <span class="group-panel__field-label">群公告（{{ ANNOUNCEMENT_MAX_LENGTH }} 字符以内，支持换行）</span>
        <textarea v-model="announcementDraft" rows="5" :maxlength="ANNOUNCEMENT_MAX_LENGTH" />
      </label>
      <p class="group-panel__count">{{ announcementDraftLength }}/{{ ANNOUNCEMENT_MAX_LENGTH }}</p>
      <button
        v-if="hasAnnouncement"
        type="button"
        class="group-panel__danger group-panel__clear"
        :disabled="profileSaving"
        @click="askClearAnnouncement"
      >
        清空公告
      </button>
    </AppDialog>

    <AppDialog
      :open="removeAvatarOpen"
      title="移除群头像"
      message="移除后所有成员将看不到当前群头像，确定移除吗？"
      confirm-text="移除"
      tone="danger"
      :busy="profileSaving"
      @confirm="removeAvatar"
      @cancel="removeAvatarOpen = false"
    />

    <AppDialog
      :open="clearAnnouncementOpen"
      title="清空群公告"
      message="清空后所有成员将看不到当前公告，确定清空吗？"
      confirm-text="清空"
      tone="danger"
      :busy="profileSaving"
      @confirm="clearAnnouncement"
      @cancel="clearAnnouncementOpen = false"
    />

    <MemberProfileCard
      :open="cardMember !== null"
      :member="cardMember"
      :display-name="cardDisplayName"
      :presence="cardMember ? presence.stateOf(cardMember.userId) : undefined"
      :relation="cardRelation"
      :is-self="cardMember?.userId === selfUserId"
      :anchor="memberCard?.anchor ?? null"
      :busy="memberCardBusy"
      :requesting="memberCardRequesting"
      :can-transfer="cardMember ? canTransferTo(cardMember) : false"
      :can-remove="cardMember ? isOwner && cardMember.userId !== selfUserId : false"
      :error="memberCardError"
      @close="closeMemberCard"
      @start-chat="startChatWithCardMember"
      @send-request="addFriendFromCard"
      @transfer="transferFromCard"
      @remove="removeFromCard"
    />
  </div>
</template>

<style scoped>
/* UI-D P3：参考图 03 的分区标题（群成员 + 计数）。 */
.group-panel__section-title {
  margin: var(--ec-space-2) 0 0;
  color: var(--ec-text-secondary);
  font-size: var(--ec-font-size-secondary);
  font-weight: var(--ec-font-weight-semibold);
}

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
.group-panel__secondary {
  padding: var(--ec-space-1) var(--ec-space-2);
  border: 1px solid var(--ec-border);
  border-radius: var(--ec-radius-sm);
  background: transparent;
  color: var(--ec-text-secondary);
  font-size: var(--ec-font-size-small);
  cursor: pointer;
}

.group-panel__primary {
  padding: var(--ec-space-1) var(--ec-space-2);
  border: none;
  border-radius: var(--ec-radius-sm);
  background: var(--ec-brand-600);
  color: var(--ec-text-inverse);
  font-size: var(--ec-font-size-secondary);
  cursor: pointer;
}

.group-panel__primary:disabled,
.group-panel__danger:disabled {
  opacity: 0.6;
  cursor: not-allowed;
}

.group-panel__danger {
  padding: var(--ec-space-1) var(--ec-space-2);
  border: 1px solid var(--ec-danger);
  border-radius: var(--ec-radius-sm);
  background: transparent;
  color: var(--ec-danger);
  font-size: var(--ec-font-size-secondary);
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

/* F-41：成员为单列紧凑行——去掉邮箱行与行内「转让/移除」按钮，行高约 37px；
   完整信息与成员管理动作集中在点击后的资料浮窗。 */
.group-panel__members {
  display: flex;
  flex-direction: column;
}

.group-panel__member {
  display: flex;
  min-width: 0;
  border-bottom: 1px solid var(--ec-border);
}

.group-panel__candidate {
  display: flex;
  gap: var(--ec-space-2);
  align-items: center;
  padding: var(--ec-space-2) 0;
  border-bottom: 1px solid var(--ec-border);
}

/* F-41：成员行主体为按钮（点击打开资料浮窗）；头像 + 昵称 + 群主徽标，邮箱只在浮窗展示。 */
.group-panel__member-main {
  display: flex;
  flex: 1;
  gap: var(--ec-space-1);
  align-items: center;
  min-width: 0;
  padding: var(--ec-space-1) var(--ec-space-2) var(--ec-space-1) var(--ec-space-1);
  border: none;
  border-radius: var(--ec-radius-sm);
  background: transparent;
  color: inherit;
  font: inherit;
  text-align: left;
  cursor: pointer;
}

.group-panel__member-main:focus-visible {
  outline: var(--ec-focus-width) solid var(--ec-focus-ring);
  outline-offset: var(--ec-focus-offset);
}

/* 悬停效果仅在鼠标精确指针设备生效，触屏不产生粘滞 hover。 */
@media (hover: hover) and (pointer: fine) {
  .group-panel__member-main:hover {
    background: var(--ec-surface-hover);
  }
}

.group-panel__candidate-info {
  display: flex;
  flex: 1;
  flex-direction: column;
  gap: 2px;
  min-width: 0;
}

.group-panel__member-name {
  display: flex;
  flex: 1;
  gap: 4px;
  align-items: center;
  min-width: 0;
  font-weight: var(--ec-font-weight-semibold);
}

.group-panel__member-text {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.group-panel__me {
  flex: none;
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
  padding: 0 5px;
  border-radius: var(--ec-radius-full);
  background: var(--ec-brand-050);
  color: var(--ec-brand-600);
  font-size: 10px;
  line-height: 16px;
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

/* F-14：群资料区（群名 + 公告，群主可编辑，其余成员只读）。 */
.group-panel__profile {
  margin-top: var(--ec-space-1);
  padding: var(--ec-space-3);
  border: 1px solid var(--ec-border);
  border-radius: var(--ec-radius-md);
  background: var(--ec-bg-canvas);
}

/* F-23：群头像行（头像 + 群主更换/移除操作）。 */
.group-panel__profile-avatar {
  display: flex;
  gap: var(--ec-space-3);
  align-items: center;
  margin-bottom: var(--ec-space-2);
}

.group-panel__profile-avatar-actions {
  display: flex;
  flex-direction: column;
  gap: var(--ec-space-1);
  align-items: flex-start;
}

.group-panel__file-input {
  display: none;
}

.group-panel__profile-head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: var(--ec-space-2);
}

.group-panel__profile-title {
  margin: 0;
  color: var(--ec-text-primary);
  font-size: var(--ec-font-size-secondary);
  font-weight: var(--ec-font-weight-semibold);
}

.group-panel__profile-name {
  margin: var(--ec-space-1) 0 0;
  overflow: hidden;
  color: var(--ec-text-primary);
  font-size: var(--ec-font-size-body);
  text-overflow: ellipsis;
  white-space: nowrap;
}

.group-panel__announcement {
  display: flex;
  flex-direction: column;
  gap: var(--ec-space-1);
  margin-top: var(--ec-space-2);
}

.group-panel__announcement-label {
  color: var(--ec-text-secondary);
  font-size: var(--ec-font-size-small);
}

.group-panel__announcement-text {
  margin: 0;
  color: var(--ec-text-secondary);
  font-size: var(--ec-font-size-secondary);
  line-height: 1.55;
  overflow-wrap: anywhere;
  white-space: pre-line;
}

.group-panel__link {
  display: inline-flex;
  gap: 4px;
  align-items: center;
  align-self: flex-start;
  padding: 0;
  border: none;
  background: transparent;
  color: var(--ec-brand-600);
  font-size: var(--ec-font-size-small);
  cursor: pointer;
}

.group-panel__link:disabled {
  opacity: 0.6;
  cursor: not-allowed;
}

@media (hover: hover) and (pointer: fine) {
  .group-panel__link:hover:not(:disabled) {
    text-decoration: underline;
  }
}

.group-panel__field {
  display: flex;
  flex-direction: column;
  gap: var(--ec-space-1);
}

.group-panel__field-label {
  color: var(--ec-text-secondary);
  font-size: var(--ec-font-size-small);
}

.group-panel__field input,
.group-panel__field textarea {
  width: 100%;
  padding: var(--ec-space-2) var(--ec-space-3);
  border: 1px solid var(--ec-border);
  border-radius: var(--ec-radius-sm);
  outline: none;
  font: inherit;
  resize: vertical;
}

.group-panel__field input:focus,
.group-panel__field textarea:focus {
  border-color: var(--ec-brand-600);
}

.group-panel__count {
  margin: var(--ec-space-1) 0 0;
  color: var(--ec-text-tertiary);
  font-size: var(--ec-font-size-small);
  text-align: right;
}

.group-panel__clear {
  margin-top: var(--ec-space-2);
}

@media (max-width: 800px) {
  .group-panel__link,
  .group-panel__clear {
    min-height: 44px;
  }

  .group-panel__member-main {
    min-height: 44px;
  }
}
</style>
