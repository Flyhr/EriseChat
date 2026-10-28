// F-26：本地收藏状态（Pinia）。
//
// 收藏是「本设备快照」，存 IndexedDB（core/db.ts 的 favorites store）；跨刷新保留，按账号隔离，
// 不做云端同步。登录后加载当前账号的收藏；登出/切账号只清内存，不删除 IndexedDB 数据。
import { computed, ref } from 'vue'
import { defineStore } from 'pinia'
import { deleteFavorite, getAllFavorites, putFavorite } from '../core/db'
import {
  MAX_FAVORITES,
  favoriteSnapshotOf,
  filterFavorites,
  sortFavorites,
  type FavoriteRecord,
} from '../core/favorites'
import { showToast } from '../core/toast'
import type { Conversation, Message } from '../types/frames'

/** toggle 的结果：切换成功（新增/删除）、达到上限未新增、消息不可收藏。 */
export type FavoriteToggleResult = 'added' | 'removed' | 'full' | 'unsupported'

export const useFavoritesStore = defineStore('favorites', () => {
  const records = ref<FavoriteRecord[]>([])
  const loaded = ref(false)
  /** 当前账号（收藏按 ownerId 隔离；未登录为空串）。 */
  const ownerId = ref('')
  let loadedFor = ''

  /** 当前账号的收藏（收藏时间倒序）。 */
  const items = computed(() => sortFavorites(filterFavorites(records.value, ownerId.value, '')))

  /** 当前账号的收藏数量（上限校验用）。 */
  const count = computed(() => filterFavorites(records.value, ownerId.value, '').length)

  /** 加载指定账号的收藏；同一账号只从 IndexedDB 读一次。 */
  async function loadForUser(userId: string): Promise<void> {
    const next = userId.trim()
    if (!next) {
      clear()
      return
    }
    if (loadedFor === next) return
    ownerId.value = next
    try {
      records.value = await getAllFavorites()
      loaded.value = true
      loadedFor = next
    } catch (error) {
      console.warn('[favorites] 读取本地收藏失败', error)
      records.value = []
      loaded.value = true
      loadedFor = next
    }
  }

  /** 该消息是否已被当前账号收藏。 */
  function isFavorited(messageId: string): boolean {
    return records.value.some((record) => record.ownerId === ownerId.value && record.messageId === messageId)
  }

  /**
   * 收藏/取消收藏一条消息。返回结果由调用方决定 toast 文案（「收藏已满」已在内部提示）。
   */
  async function toggle(
    message: Message,
    conversation: Conversation | null | undefined,
    senderName: string,
    selfUserId: string,
  ): Promise<FavoriteToggleResult> {
    if (!selfUserId) return 'unsupported'
    const snapshot = favoriteSnapshotOf(message, conversation, senderName, selfUserId)
    if (!snapshot) return 'unsupported'
    const existing = records.value.find(
      (record) => record.ownerId === selfUserId && record.messageId === message.messageId,
    )
    if (existing) {
      try {
        await deleteFavorite(existing.favoriteId)
      } catch (error) {
        console.warn('[favorites] 取消收藏失败', error)
        throw error
      }
      records.value = records.value.filter((record) => record.favoriteId !== existing.favoriteId)
      return 'removed'
    }
    if (count.value >= MAX_FAVORITES) {
      showToast('收藏已满（最多 500 条），请先清理', 'error')
      return 'full'
    }
    try {
      await putFavorite(snapshot)
    } catch (error) {
      console.warn('[favorites] 收藏写入失败', error)
      throw error
    }
    records.value = [...records.value, snapshot]
    return 'added'
  }

  /** 删除一条收藏（收藏页使用）。 */
  async function remove(favoriteId: string): Promise<void> {
    await deleteFavorite(favoriteId)
    records.value = records.value.filter((record) => record.favoriteId !== favoriteId)
  }

  /** 仅清空内存（登出/切账号时调用；IndexedDB 数据保留给下次登录）。 */
  function clear(): void {
    records.value = []
    loaded.value = false
    loadedFor = ''
    ownerId.value = ''
  }

  return { records, loaded, ownerId, items, count, loadForUser, isFavorited, toggle, remove, clear }
})
