// F-38：个人收藏状态（Pinia，服务端 SQLite 为事实源，跨设备同步）。
//
// 旧版本（F-26）的收藏只存在本机 IndexedDB（core/favorites.ts 的 FavoriteRecord 快照）。
// 本版本改为服务端数据源：登录后 GET /api/favorites 恢复；新增/取消走 REST（幂等，可安全重试）；
// 同账号其他在线设备通过 favorite.changed 帧触发刷新，WebSocket 重连认证成功后也会自动刷新。
// 旧本地记录默认保留，由用户在收藏页显式导入当前服务器；导入成功的记录写入 migratedTo，避免重复导入。
//
// 收尾（F-38）：所有请求与「发起时的登录会话 + 服务器」绑定（epoch + sessionKey）。
// 切换账号或服务器后，旧请求的响应直接丢弃，不覆盖新列表；旧批次导入在切换时停止，未确认记录保留在本机。
import { computed, ref } from 'vue'
import { defineStore } from 'pinia'
import { favoritesApi } from '../api/favorites'
import { resolveServerOrigin } from '../core/config'
import { getAllFavorites, putFavorite } from '../core/db'
import type { FavoriteRecord } from '../core/favorites'
import { chatWs, type WsState } from '../core/ws'
import type { FavoriteItem, ServerFrame } from '../types/frames'

export type FavoriteToggleAction = 'added' | 'removed' | 'stale'

export const useFavoritesStore = defineStore('favorites', () => {
  /** 服务端收藏列表（按收藏时间倒序，服务端已排序）。 */
  const items = ref<FavoriteItem[]>([])
  const loaded = ref(false)
  const loading = ref(false)
  const error = ref('')
  /** 当前账号（收藏按账号隔离；未登录为空串）。 */
  const ownerId = ref('')
  /** 旧版本地收藏中尚未导入当前服务器的记录。 */
  const legacyRecords = ref<FavoriteRecord[]>([])
  const importing = ref(false)

  let loadedFor = ''
  let subscribed = false
  /** 刷新序号：切换账号/服务器后丢弃旧请求的结果。 */
  let refreshSeq = 0
  /** 会话代数：账号/服务器切换时递增，用于丢弃旧请求的迟到响应。 */
  let epoch = 0
  /** 最近一次观察到的 WS 状态（识别「重连成功」）。 */
  let lastWsState: WsState = chatWs.state
  /** 列表版本：撤回/编辑/退群事件会递增，使事件之前发起的列表请求失效。 */
  let listRevision = 0

  const count = computed(() => items.value.length)
  const legacyCount = computed(() => legacyRecords.value.length)

  /** 当前会话标识：服务器 origin + 账号；请求发起时记录，响应回来后比对。 */
  function currentSessionKey(): string {
    return `${resolveServerOrigin()}|${ownerId.value}`
  }

  /** 响应是否仍属于发起请求时的会话。 */
  function isSameSession(capturedEpoch: number, capturedKey: string): boolean {
    return capturedEpoch === epoch && capturedKey === currentSessionKey()
  }

  /** 事件（撤回/编辑/退群）后使进行中的列表请求失效。 */
  function bumpListRevision(): void {
    listRevision += 1
  }

  /** 撤回/失去权限：立即隐藏旧内容（即使后续刷新失败也不继续展示）。 */
  function markUnavailable(messageId: string): void {
    bumpListRevision()
    const index = items.value.findIndex((item) => item.messageId === messageId)
    if (index < 0) return
    const next = items.value.slice()
    next[index] = { ...next[index], available: false, message: null }
    items.value = next
  }

  function markConversationsUnavailable(conversationIds: readonly string[]): void {
    if (conversationIds.length === 0) return
    bumpListRevision()
    const removed = new Set(conversationIds)
    items.value = items.value.map((item) =>
      removed.has(item.conversationId) ? { ...item, available: false, message: null } : item,
    )
  }

  /** 原消息编辑：更新已缓存的快照（不可用条目保持不可用）。 */
  function applyEdited(frame: Extract<ServerFrame, { type: 'message.edited' }>): void {
    bumpListRevision()
    const index = items.value.findIndex((item) => item.messageId === frame.messageId)
    if (index < 0) return
    const item = items.value[index]
    if (!item.available || !item.message) return
    const next = items.value.slice()
    next[index] = {
      ...item,
      message: { ...item.message, content: frame.content, editedAt: frame.editedAt },
    }
    items.value = next
  }

  /** 业务帧：撤回/编辑/会话移除 → 就地更新受影响条目；favorite.changed → 重新拉取列表。 */
  function applyRealtimeFrame(frame: ServerFrame): void {
    switch (frame.type) {
      case 'message.recalled':
        markUnavailable(frame.messageId)
        break
      case 'message.edited':
        applyEdited(frame)
        break
      case 'conv.updated':
        if (frame.removedConversationIds && frame.removedConversationIds.length > 0) {
          markConversationsUnavailable(frame.removedConversationIds)
        }
        break
      default:
        break
    }
  }

  /** 从服务端拉取收藏列表（登录、重连、favorite.changed 后调用）。 */
  async function refresh(): Promise<void> {
    const seq = ++refreshSeq
    const capturedEpoch = epoch
    const capturedKey = currentSessionKey()
    const revisionAtStart = listRevision
    loading.value = true
    try {
      const list = await favoritesApi.list()
      if (seq !== refreshSeq || !isSameSession(capturedEpoch, capturedKey)) return
      if (revisionAtStart !== listRevision) {
        // 请求期间发生撤回/编辑/退群：旧列表可能含已隐藏/已更新内容，丢弃并重新拉取。
        void refresh().catch(() => undefined)
        return
      }
      items.value = list
      error.value = ''
    } catch (cause) {
      if (seq !== refreshSeq || !isSameSession(capturedEpoch, capturedKey)) return
      if (revisionAtStart !== listRevision) {
        void refresh().catch(() => undefined)
        return
      }
      error.value = cause instanceof Error ? cause.message : '收藏加载失败'
      throw cause
    } finally {
      if (seq === refreshSeq) loading.value = false
    }
  }

  /** 加载指定账号的收藏；同一账号只初始化一次，切换账号时丢弃进行中的旧结果。 */
  async function loadForUser(userId: string): Promise<void> {
    const next = userId.trim()
    if (!next) {
      clear()
      return
    }
    if (loadedFor === next) return
    epoch += 1
    refreshSeq += 1
    ownerId.value = next
    loadedFor = next
    loaded.value = false
    items.value = []
    legacyRecords.value = []
    ensureRealtime()
    await Promise.all([refresh().catch(() => undefined), loadLegacy()])
    if (loadedFor === next) loaded.value = true
  }

  /** 读取本机旧收藏（F-26 快照）中当前账号且尚未迁移的部分。 */
  async function loadLegacy(): Promise<void> {
    const capturedEpoch = epoch
    try {
      const all = await getAllFavorites()
      if (capturedEpoch !== epoch) return
      const current = ownerId.value
      legacyRecords.value = all.filter((record) => record.ownerId === current && !record.migratedTo)
    } catch (cause) {
      console.warn('[favorites] 读取旧版本地收藏失败', cause)
      if (capturedEpoch === epoch) legacyRecords.value = []
    }
  }

  /** 该消息是否已被当前账号收藏（菜单状态用）。 */
  function isFavorited(messageId: string): boolean {
    return items.value.some((item) => item.messageId === messageId)
  }

  /**
   * 收藏/取消收藏；失败时抛错由调用方提示（不伪造成功）；会话已切换时返回 stale 且不更新列表。
   * 成功后不直接用接口响应增删列表，而是统一按服务端最新列表刷新，
   * 避免迟到的 POST/DELETE 响应覆盖其他设备/后续事件造成的新状态。
   */
  async function toggle(messageId: string): Promise<FavoriteToggleAction> {
    const capturedEpoch = epoch
    const capturedKey = currentSessionKey()
    const wasFavorited = isFavorited(messageId)
    if (wasFavorited) {
      await favoritesApi.remove(messageId)
    } else {
      await favoritesApi.add(messageId)
    }
    if (!isSameSession(capturedEpoch, capturedKey)) return 'stale'
    await refresh().catch(() => undefined)
    return wasFavorited ? 'removed' : 'added'
  }

  /** 收藏页删除（与 toggle 取消等价，单独命名便于页面语义）；成功后统一刷新列表。 */
  async function remove(messageId: string): Promise<void> {
    const capturedEpoch = epoch
    const capturedKey = currentSessionKey()
    await favoritesApi.remove(messageId)
    if (!isSameSession(capturedEpoch, capturedKey)) return
    await refresh().catch(() => undefined)
  }

  /**
   * 把旧版本地收藏逐条导入当前服务器（只提交 messageId，由服务端逐条校验）。
   * 成功记录写入 migratedTo；失败/未确认条目保留在本地，由用户再次尝试。
   * 账号/服务器切换时停止旧批次（未确认记录不写 migratedTo，仍保留在本机）。
   */
  async function importLegacy(): Promise<{ imported: number; failed: number; interrupted: boolean }> {
    if (importing.value) return { imported: 0, failed: 0, interrupted: false }
    importing.value = true
    const origin = resolveServerOrigin()
    const capturedEpoch = epoch
    const capturedKey = currentSessionKey()
    let imported = 0
    let failed = 0
    let interrupted = false
    try {
      for (const record of legacyRecords.value.slice()) {
        if (!isSameSession(capturedEpoch, capturedKey)) {
          interrupted = true
          break
        }
        try {
          await favoritesApi.add(record.messageId)
          if (!isSameSession(capturedEpoch, capturedKey)) {
            interrupted = true
            break
          }
          await putFavorite({ ...record, migratedTo: origin })
          imported += 1
        } catch (cause) {
          console.warn('[favorites] 旧收藏导入失败', record.messageId, cause)
          failed += 1
        }
      }
      if (!interrupted) {
        await Promise.all([refresh().catch(() => undefined), loadLegacy()])
      }
      return { imported, failed, interrupted }
    } finally {
      importing.value = false
    }
  }

  /** 同账号其他设备的收藏变更 → 刷新；重连认证成功 → 刷新；撤回/编辑/失去会话权限 → 就地更新。 */
  function ensureRealtime(): void {
    if (subscribed) return
    subscribed = true
    lastWsState = chatWs.state
    chatWs.on('frame', (frame) => {
      if (!ownerId.value) return
      if (frame.type === 'favorite.changed') {
        void refresh().catch(() => undefined)
        return
      }
      applyRealtimeFrame(frame)
    })
    chatWs.on('statechange', (next) => {
      const previous = lastWsState
      lastWsState = next
      if (next === 'authed' && previous !== 'authed' && ownerId.value) {
        void refresh().catch(() => undefined)
      }
    })
  }

  /** 仅清空内存（登出/切账号/切服务器时调用；IndexedDB 旧记录保留）。 */
  function clear(): void {
    epoch += 1
    items.value = []
    loaded.value = false
    loading.value = false
    error.value = ''
    ownerId.value = ''
    legacyRecords.value = []
    loadedFor = ''
    refreshSeq += 1
  }

  return {
    items,
    loaded,
    loading,
    error,
    ownerId,
    legacyRecords,
    importing,
    count,
    legacyCount,
    loadForUser,
    refresh,
    loadLegacy,
    isFavorited,
    toggle,
    remove,
    importLegacy,
    clear,
  }
})
