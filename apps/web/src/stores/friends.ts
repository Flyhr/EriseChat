import { computed, ref } from 'vue'
import { defineStore } from 'pinia'
import { friendsApi } from '../api/friends'
import type { Friend, FriendChangedFrame, FriendRequest, ServerFrame } from '../types/frames'
import { chatWs, type WsState } from '../core/ws'
import { showToast } from '../core/toast'

/** 按分组聚合后的好友列表（groupName 为 null 表示「未分组」，永远排在最后）。 */
export interface FriendGroup {
  groupName: string | null
  friends: Friend[]
}

/**
 * 好友关系（F-17）：好友列表、好友申请（收到/发出）、备注与分组。
 * - REST 全量拉取（登录/重连后）；WS friend.changed 帧增量更新；
 * - 动作方法失败时抛错，由页面用 friendlyError 展示；实时推送中的提示用 toast；
 * - 未知 kind 一律忽略（protocol.md §8 兼容规则）。
 */
export const useFriendsStore = defineStore('friends', () => {
  const friends = ref<Friend[]>([])
  const incoming = ref<FriendRequest[]>([])
  const outgoing = ref<FriendRequest[]>([])
  const loading = ref(false)

  let loaded = false
  let loadingPromise: Promise<void> | null = null
  let subscribed = false
  const unsubscribers: Array<() => void> = []

  const incomingCount = computed(() => incoming.value.length)
  const friendIds = computed(() => new Set(friends.value.map((friend) => friend.userId)))

  /** 收到/发出的待处理申请数量（侧栏徽标用 incomingCount）。 */
  const pendingCount = computed(() => incoming.value.length + outgoing.value.length)

  /** 按分组聚合（未分组排最后，组内、组间沿用服务端排序）。 */
  const groupedFriends = computed<FriendGroup[]>(() => {
    const groups: FriendGroup[] = []
    for (const friend of friends.value) {
      const groupName = friend.groupName?.trim() || null
      const group = groups.find((item) => item.groupName === groupName)
      if (group) {
        group.friends.push(friend)
      } else {
        groups.push({ groupName, friends: [friend] })
      }
    }

    return groups
  })

  /** 好友展示名：备注优先，其次昵称；非好友返回空串。 */
  function displayNameOf(userId: string | undefined): string {
    if (!userId) return ''
    const friend = friends.value.find((item) => item.userId === userId)
    return friend?.remark?.trim() || friend?.displayName || ''
  }

  function isFriend(userId: string | undefined): boolean {
    return Boolean(userId) && friendIds.value.has(userId as string)
  }

  function sortFriends(list: Friend[]): Friend[] {
    const displayOf = (friend: Friend): string => friend.remark?.trim() || friend.displayName
    const groupRank = (friend: Friend): string => friend.groupName?.trim() || '\uffff'
    return [...list].sort((left, right) => {
      const byGroup = groupRank(left).localeCompare(groupRank(right), 'zh-Hans-CN')
      if (byGroup !== 0) return byGroup
      const byName = displayOf(left).localeCompare(displayOf(right), 'zh-Hans-CN')
      if (byName !== 0) return byName
      return left.userId.localeCompare(right.userId)
    })
  }

  function upsertFriend(friend: Friend): void {
    const index = friends.value.findIndex((item) => item.userId === friend.userId)
    friends.value = index >= 0
      ? sortFriends(friends.value.map((item) => (item.userId === friend.userId ? friend : item)))
      : sortFriends([...friends.value, friend])
  }

  function removeFriendLocal(userId: string): void {
    friends.value = friends.value.filter((item) => item.userId !== userId)
  }

  function upsertRequest(list: FriendRequest[], request: FriendRequest): FriendRequest[] {
    const index = list.findIndex((item) => item.requestId === request.requestId)
    if (index < 0) return [request, ...list]
    return list.map((item) => (item.requestId === request.requestId ? request : item))
  }

  function removeIncoming(requestId: string): void {
    incoming.value = incoming.value.filter((item) => item.requestId !== requestId)
  }

  function removeOutgoing(requestId: string): void {
    outgoing.value = outgoing.value.filter((item) => item.requestId !== requestId)
  }

  function removeRequestsWithUser(userId: string): void {
    incoming.value = incoming.value.filter((item) => item.fromUserId !== userId)
    outgoing.value = outgoing.value.filter((item) => item.toUserId !== userId)
  }

  /** 全量拉取好友与申请（登录/重连后调用；并发调用合并为一次）。 */
  async function loadAll(): Promise<void> {
    if (loadingPromise) return loadingPromise
    loading.value = true
    loadingPromise = (async () => {
      try {
        const [friendResult, requestResult] = await Promise.all([
          friendsApi.list(),
          friendsApi.requests(),
        ])
        friends.value = sortFriends(friendResult.friends)
        incoming.value = requestResult.incoming
        outgoing.value = requestResult.outgoing
        loaded = true
      } finally {
        loading.value = false
        loadingPromise = null
      }
    })()
    return loadingPromise
  }

  async function ensureLoaded(): Promise<void> {
    if (loaded) return
    await loadAll()
  }

  /** 发起好友申请（对方此前也申请过时服务端自动接受）。 */
  async function sendRequest(toUserId: string, message?: string) {
    const body: { toUserId: string; message?: string } = { toUserId }
    const trimmed = message?.trim()
    if (trimmed) body.message = trimmed
    const result = await friendsApi.sendRequest(body)

    if (result.autoAccepted && result.friend) {
      removeRequestsWithUser(result.friend.userId)
      upsertFriend(result.friend)
      showToast('对方也向你发过申请，已直接成为好友', 'success')
    } else {
      outgoing.value = upsertRequest(outgoing.value, result.request)
      showToast('好友申请已发送', 'success')
    }

    return result
  }

  async function accept(requestId: string): Promise<Friend> {
    const { friend } = await friendsApi.accept(requestId)
    removeIncoming(requestId)
    upsertFriend(friend)
    showToast('已添加为好友', 'success')
    return friend
  }

  async function reject(requestId: string): Promise<void> {
    await friendsApi.reject(requestId)
    removeIncoming(requestId)
    showToast('已拒绝好友申请', 'info')
  }

  async function cancel(requestId: string): Promise<void> {
    await friendsApi.cancel(requestId)
    removeOutgoing(requestId)
    showToast('已撤销好友申请', 'info')
  }

  async function updateFriend(friendUserId: string, patch: { remark?: string; groupName?: string }): Promise<Friend> {
    const { friend } = await friendsApi.update(friendUserId, patch)
    upsertFriend(friend)
    return friend
  }

  async function removeFriend(friendUserId: string): Promise<void> {
    await friendsApi.remove(friendUserId)
    removeFriendLocal(friendUserId)
    showToast('已删除好友', 'info')
  }

  function handleChanged(frame: FriendChangedFrame): void {
    switch (frame.kind) {
      case 'request.received':
        if (frame.request) {
          incoming.value = upsertRequest(incoming.value, frame.request)
          showToast(`${frame.request.fromDisplayName} 请求添加你为好友`, 'info')
        }
        break
      case 'request.accepted':
        if (frame.request) removeOutgoing(frame.request.requestId)
        break
      case 'request.rejected':
        if (frame.request) removeOutgoing(frame.request.requestId)
        showToast('好友申请被拒绝', 'info')
        break
      case 'request.cancelled':
        if (frame.request) {
          removeIncoming(frame.request.requestId)
          showToast('对方撤销了好友申请', 'info')
        }
        break
      case 'friend.added':
        if (frame.friend) {
          removeRequestsWithUser(frame.friend.userId)
          upsertFriend(frame.friend)
        }
        break
      case 'friend.removed':
        if (frame.friend) removeFriendLocal(frame.friend.userId)
        break
      case 'friend.updated':
        if (frame.friend) upsertFriend(frame.friend)
        break
      default:
        // 未知 kind：忽略（protocol.md §8 向后兼容）
        break
    }
  }

  /** 订阅 friend.changed（只注册一次）；连接恢复 authed 时重新全量拉取兜底。 */
  function subscribeRealtime(): () => void {
    if (subscribed) return dispose
    subscribed = true
    unsubscribers.push(
      chatWs.on('frame', (frame: ServerFrame) => {
        if (frame.type !== 'friend.changed') return
        handleChanged(frame)
      }),
    )
    unsubscribers.push(
      chatWs.on('statechange', (state: WsState) => {
        if (state === 'authed') {
          void loadAll().catch(() => undefined)
        }
      }),
    )
    return dispose
  }

  function clear(): void {
    friends.value = []
    incoming.value = []
    outgoing.value = []
    loading.value = false
    loaded = false
    loadingPromise = null
  }

  function dispose(): void {
    for (const unsubscribe of unsubscribers.splice(0)) unsubscribe()
    subscribed = false
    clear()
  }

  return {
    friends,
    incoming,
    outgoing,
    loading,
    incomingCount,
    pendingCount,
    friendIds,
    groupedFriends,
    displayNameOf,
    isFriend,
    loadAll,
    ensureLoaded,
    sendRequest,
    accept,
    reject,
    cancel,
    updateFriend,
    removeFriend,
    subscribeRealtime,
    clear,
    dispose,
  }
})
