// F-21：最近使用表情的本地存储封装（仅本设备 UI 偏好，不落服务端、不进消息链路）。
// 存储格式为 JSON 字符串数组；读取失败/损坏一律按空列表处理（隐私模式、配额异常不阻塞输入）。

import { EMOJI_RECENT_LIMIT, pushRecentEmoji } from './emoji'
import { getStorage } from './safeStorage'

export const EMOJI_RECENT_STORAGE_KEY = 'eisechat.emojiRecent.v1'

/** 存储接口（便于单测注入假实现；默认 localStorage）。 */
export interface EmojiRecentStorage {
  getItem(key: string): string | null
  setItem(key: string, value: string): void
}

function defaultStorage(): EmojiRecentStorage | null {
  return getStorage()
}

/**
 * 读取最近使用表情：非法 JSON、非数组、非字符串项、空串项都过滤掉；
 * 最多返回 24 个（与写入上限一致），读取异常返回空数组。
 */
export function readRecentEmojis(storage: EmojiRecentStorage | null = defaultStorage()): string[] {
  if (!storage) return []
  try {
    const raw = storage.getItem(EMOJI_RECENT_STORAGE_KEY)
    if (!raw) return []
    const parsed: unknown = JSON.parse(raw)
    if (!Array.isArray(parsed)) return []
    return parsed
      .filter((item): item is string => typeof item === 'string' && item.length > 0)
      .slice(0, EMOJI_RECENT_LIMIT)
  } catch {
    return []
  }
}

/** 记录一次使用：读取 → 去重前置 → 写回；写入异常静默忽略（隐私模式下不可写）。 */
export function recordRecentEmoji(
  emoji: string,
  storage: EmojiRecentStorage | null = defaultStorage(),
): void {
  if (typeof emoji !== 'string' || emoji.length === 0 || !storage) return
  try {
    const next = pushRecentEmoji(readRecentEmojis(storage), emoji)
    storage.setItem(EMOJI_RECENT_STORAGE_KEY, JSON.stringify(next))
  } catch {
    // localStorage 不可用（隐私模式/配额满）时忽略，不中断输入。
  }
}
