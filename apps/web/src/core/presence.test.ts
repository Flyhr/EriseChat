import { describe, expect, it } from 'vitest'
import { presenceLabelOf, updatePresence } from './presence'

describe('presenceLabelOf', () => {
  it('在线/离线/未知三种文案', () => {
    expect(presenceLabelOf(true)).toBe('在线')
    expect(presenceLabelOf(false)).toBe('离线')
    expect(presenceLabelOf(undefined)).toBe('未知')
  })
})

describe('updatePresence', () => {
  it('记录用户在线状态', () => {
    expect(updatePresence({}, 'u1', true)).toEqual({ u1: true })
  })

  it('覆盖已有状态（在线 → 离线）', () => {
    expect(updatePresence({ u1: true }, 'u1', false)).toEqual({ u1: false })
  })

  it('不修改传入的对象（保持不可变更新）', () => {
    const original = { u1: true }
    const next = updatePresence(original, 'u2', true)
    expect(original).toEqual({ u1: true })
    expect(next).toEqual({ u1: true, u2: true })
    expect(next).not.toBe(original)
  })

  it('空 userId 不写入', () => {
    expect(updatePresence({ u1: true }, '', true)).toEqual({ u1: true })
  })
})
