import { describe, expect, it } from 'vitest'
import { EMOJI_GROUPS, insertAtCursor } from './emoji'

// UI-05 批次2：Emoji 数据与光标插入纯函数的最小回归（不测样式/布局）。
describe('EMOJI_GROUPS', () => {
  it('包含常用的三个分组，且每组都有表情', () => {
    expect(EMOJI_GROUPS.map((group) => group.label)).toEqual(['常用', '表情', '手势'])
    for (const group of EMOJI_GROUPS) {
      expect(group.emojis.length).toBeGreaterThan(0)
    }
  })

  it('总数量满足面板展示需求（≥60 个基础 Emoji）', () => {
    const total = EMOJI_GROUPS.reduce((sum, group) => sum + group.emojis.length, 0)
    expect(total).toBeGreaterThanOrEqual(60)
  })

  it('分组内无重复项', () => {
    for (const group of EMOJI_GROUPS) {
      expect(new Set(group.emojis).size).toBe(group.emojis.length)
    }
  })
})

describe('insertAtCursor', () => {
  it('在文本中间插入：光标停在 emoji 之后', () => {
    expect(insertAtCursor('abc', 1, '😀')).toEqual({ text: 'a😀bc', caret: 3 })
  })

  it('在开头插入', () => {
    expect(insertAtCursor('abc', 0, '👍')).toEqual({ text: '👍abc', caret: 2 })
  })

  it('在末尾插入', () => {
    expect(insertAtCursor('abc', 3, '🙏')).toEqual({ text: 'abc🙏', caret: 5 })
  })

  it('空文本插入', () => {
    expect(insertAtCursor('', 0, '😊')).toEqual({ text: '😊', caret: 2 })
  })

  it('caret 越界时夹紧到合法区间，不抛错', () => {
    expect(insertAtCursor('ab', -5, 'x')).toEqual({ text: 'xab', caret: 1 })
    expect(insertAtCursor('ab', 99, 'y')).toEqual({ text: 'aby', caret: 3 })
  })

  it('多码元 Emoji 按 UTF-16 长度推进光标（✌️ 为 2 个码元）', () => {
    expect(insertAtCursor('ab', 1, '✌️')).toEqual({ text: 'a✌️b', caret: 3 })
  })
})
