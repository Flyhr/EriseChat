import { describe, expect, it } from 'vitest'
import { TypingThrottle } from './typingThrottle'
import { pruneTyping, typingLabelOf, upsertTyping } from './typing'

describe('TypingThrottle', () => {
  it('首次输入允许发送 typing', () => {
    const throttle = new TypingThrottle()
    expect(throttle.shouldSend('cv-1', 1_000)).toBe(true)
  })

  it('3 秒窗口内再次输入被节流', () => {
    const throttle = new TypingThrottle()
    expect(throttle.shouldSend('cv-1', 1_000)).toBe(true)
    expect(throttle.shouldSend('cv-1', 1_500)).toBe(false)
    expect(throttle.shouldSend('cv-1', 3_999)).toBe(false)
  })

  it('超过窗口后允许再次发送', () => {
    const throttle = new TypingThrottle()
    expect(throttle.shouldSend('cv-1', 1_000)).toBe(true)
    expect(throttle.shouldSend('cv-1', 4_000)).toBe(true)
  })

  it('不同会话各自独立节流', () => {
    const throttle = new TypingThrottle()
    expect(throttle.shouldSend('cv-1', 1_000)).toBe(true)
    expect(throttle.shouldSend('cv-2', 1_000)).toBe(true)
    expect(throttle.shouldSend('cv-2', 2_000)).toBe(false)
  })

  it('空会话 ID 不发送；reset 后重新计时', () => {
    const throttle = new TypingThrottle()
    expect(throttle.shouldSend('', 1_000)).toBe(false)
    expect(throttle.shouldSend('cv-1', 1_000)).toBe(true)
    throttle.reset('cv-1')
    expect(throttle.shouldSend('cv-1', 1_500)).toBe(true)
    throttle.reset()
    expect(throttle.shouldSend('cv-2', 1_500)).toBe(true)
  })
})

describe('typing 纯逻辑', () => {
  it('upsertTyping 刷新同一用户的过期时间（同一用户只保留一条）', () => {
    const first = upsertTyping([], 'u1', 1_000, 3_000)
    expect(first).toEqual([{ userId: 'u1', expiresAt: 4_000 }])
    const second = upsertTyping(first, 'u1', 2_500, 3_000)
    expect(second).toEqual([{ userId: 'u1', expiresAt: 5_500 }])
  })

  it('pruneTyping 过滤已过期条目', () => {
    const entries = [
      { userId: 'u1', expiresAt: 4_000 },
      { userId: 'u2', expiresAt: 9_000 },
    ]
    expect(pruneTyping(entries, 4_000).map((entry) => entry.userId)).toEqual(['u2'])
    expect(pruneTyping(entries, 9_001)).toEqual([])
  })

  it('typingLabelOf：单聊有人输入时固定「对方正在输入…」', () => {
    expect(typingLabelOf('direct', ['小明'])).toBe('对方正在输入…')
  })

  it('typingLabelOf：无人输入时返回空串（单聊也不常驻提示）', () => {
    expect(typingLabelOf('direct', [])).toBe('')
    expect(typingLabelOf('direct', ['  '])).toBe('')
    expect(typingLabelOf('group', [])).toBe('')
  })

  it('typingLabelOf：群聊按人数展示昵称或「多人正在输入…」', () => {
    expect(typingLabelOf('group', ['小明'])).toBe('小明 正在输入…')
    expect(typingLabelOf('group', ['小明', '小红'])).toBe('小明、小红 正在输入…')
    expect(typingLabelOf('group', ['小明', '小红', '小刚'])).toBe('多人正在输入…')
    expect(typingLabelOf('group', [])).toBe('')
    expect(typingLabelOf('group', ['  '])).toBe('')
  })
})
