import { describe, expect, it } from 'vitest'
import { EMOJI_RECENT_LIMIT, insertAtCursor, pushRecentEmoji } from './emoji'
import { EMOJI_RECENT_STORAGE_KEY, readRecentEmojis, recordRecentEmoji } from './emojiRecent'
import type { EmojiRecentStorage } from './emojiRecent'
import { createFakeStorage } from '../test/fixtures'

// F-21-01：最近使用表情纯函数与 localStorage 封装的最小回归（不测 DOM/组件）。
describe('pushRecentEmoji', () => {
  it('新表情放到首位', () => {
    expect(pushRecentEmoji(['😀', '😂'], '👍')).toEqual(['👍', '😀', '😂'])
  })

  it('重复项去重并上移，不产生重复', () => {
    expect(pushRecentEmoji(['😀', '😂', '👍'], '😂')).toEqual(['😂', '😀', '👍'])
  })

  it('超过上限时截断到 24 个', () => {
    const list = Array.from({ length: EMOJI_RECENT_LIMIT }, (_, index) => `e${index}`)
    const next = pushRecentEmoji(list, 'new')
    expect(next).toHaveLength(EMOJI_RECENT_LIMIT)
    expect(next[0]).toBe('new')
  })

  it('空串被忽略，返回副本', () => {
    const list = ['😀']
    const next = pushRecentEmoji(list, '')
    expect(next).toEqual(['😀'])
    expect(next).not.toBe(list)
  })

  it('不修改入参', () => {
    const list = ['😀', '😂']
    pushRecentEmoji(list, '👍')
    expect(list).toEqual(['😀', '😂'])
  })
})

describe('readRecentEmojis', () => {
  it('正常数组原样读取（最多 24 个）', () => {
    const storage = createFakeStorage({ [EMOJI_RECENT_STORAGE_KEY]: JSON.stringify(['😀', '👍']) })
    expect(readRecentEmojis(storage)).toEqual(['😀', '👍'])
  })

  it('非法 JSON 返回空数组', () => {
    expect(readRecentEmojis(createFakeStorage({ [EMOJI_RECENT_STORAGE_KEY]: '{oops' }))).toEqual([])
  })

  it('非数组返回空数组', () => {
    expect(readRecentEmojis(createFakeStorage({ [EMOJI_RECENT_STORAGE_KEY]: JSON.stringify({ a: 1 }) }))).toEqual([])
  })

  it('过滤非字符串与空串项', () => {
    const storage = createFakeStorage({ [EMOJI_RECENT_STORAGE_KEY]: JSON.stringify(['😀', 1, null, '', '👍']) })
    expect(readRecentEmojis(storage)).toEqual(['😀', '👍'])
  })

  it('没有记录时返回空数组', () => {
    expect(readRecentEmojis(createFakeStorage())).toEqual([])
  })
})

describe('recordRecentEmoji', () => {
  it('写入后可按最近使用读取', () => {
    const storage = createFakeStorage()
    recordRecentEmoji('😀', storage)
    recordRecentEmoji('👍', storage)
    expect(readRecentEmojis(storage)).toEqual(['👍', '😀'])
  })

  it('存储抛异常时静默忽略（隐私模式）', () => {
    const storage: EmojiRecentStorage = {
      getItem: () => null,
      setItem: () => {
        throw new Error('quota')
      },
    }
    expect(() => recordRecentEmoji('😀', storage)).not.toThrow()
  })
})

// 回归：F-21 不改动既有插入语义。
describe('insertAtCursor 回归', () => {
  it('光标越界夹紧且插入正确', () => {
    expect(insertAtCursor('ab', 99, '😀')).toEqual({ text: 'ab😀', caret: 4 })
  })

  it('中间插入不破坏原文本', () => {
    expect(insertAtCursor('hello', 2, '🙏')).toEqual({ text: 'he🙏llo', caret: 4 })
  })
})
