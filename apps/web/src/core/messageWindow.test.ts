import { describe, expect, it } from 'vitest'
import {
  DEFAULT_WINDOW_SIZE,
  anchorScrollTop,
  clampWindow,
  expandOlderRange,
  initialWindowRange,
  isNearBottom,
  isNearTop,
  shouldAutoLoadOlder,
  tailRange,
} from './messageWindow'

// F-05 批次 1：长会话分片渲染纯函数单测（窗口范围、滚动阈值、锚点补偿、自动加载判定）。

describe('initialWindowRange / tailRange', () => {
  it('空会话返回 {0, 0}', () => {
    expect(initialWindowRange(0)).toEqual({ start: 0, end: 0 })
    expect(tailRange(0)).toEqual({ start: 0, end: 0 })
  })

  it('消息数少于窗口大小时从 0 开始', () => {
    expect(initialWindowRange(50, 200)).toEqual({ start: 0, end: 50 })
  })

  it('消息数超过窗口大小时取末尾窗口', () => {
    expect(initialWindowRange(500, 200)).toEqual({ start: 300, end: 500 })
    expect(tailRange(1000)).toEqual({ start: 1000 - DEFAULT_WINDOW_SIZE, end: 1000 })
  })

  it('非法输入被钳制：负数/NaN total 返回空窗口，非法 size 至少保留 1 条', () => {
    expect(initialWindowRange(-5)).toEqual({ start: 0, end: 0 })
    expect(initialWindowRange(Number.NaN)).toEqual({ start: 0, end: 0 })
    expect(initialWindowRange(10, 0)).toEqual({ start: 9, end: 10 })
    expect(initialWindowRange(10, Number.POSITIVE_INFINITY)).toEqual({ start: 9, end: 10 })
  })
})

describe('expandOlderRange', () => {
  it('start 前移 step，end 不变', () => {
    expect(expandOlderRange({ start: 300, end: 500 }, 200, 500)).toEqual({ start: 100, end: 500 })
  })

  it('前移超过 0 时钳制到 0', () => {
    expect(expandOlderRange({ start: 100, end: 500 }, 200, 500)).toEqual({ start: 0, end: 500 })
  })

  it('窗口内已无更早消息时保持 {0, 0} 等原范围不变', () => {
    expect(expandOlderRange({ start: 0, end: 0 }, 200, 0)).toEqual({ start: 0, end: 0 })
  })

  it('非法 step 按 1 处理，end 超过 total 时钳制', () => {
    expect(expandOlderRange({ start: 150, end: 300 }, -10, 1000)).toEqual({ start: 149, end: 300 })
    expect(expandOlderRange({ start: 150, end: 1200 }, 200, 1000)).toEqual({ start: 0, end: 1000 })
  })
})

describe('clampWindow', () => {
  it('非法 start/end 回退为 0', () => {
    expect(clampWindow({ start: Number.NaN, end: Number.NaN }, 100)).toEqual({ start: 0, end: 0 })
  })

  it('end 小于 start 时收敛到 start', () => {
    expect(clampWindow({ start: 80, end: 20 }, 100)).toEqual({ start: 80, end: 80 })
  })

  it('超出 total 的 end 被钳制，小数取整', () => {
    expect(clampWindow({ start: 10.9, end: 500.4 }, 100)).toEqual({ start: 10, end: 100 })
  })
})

describe('isNearTop / isNearBottom', () => {
  it('顶部阈值内为 true，超出为 false', () => {
    expect(isNearTop(0)).toBe(true)
    expect(isNearTop(48)).toBe(true)
    expect(isNearTop(49)).toBe(false)
  })

  it('非法 scrollTop 视为顶部', () => {
    expect(isNearTop(Number.NaN)).toBe(true)
  })

  it('贴底阈值内为 true，超出为 false', () => {
    expect(isNearBottom(1000, 900, 100)).toBe(true)
    expect(isNearBottom(1000, 899, 100)).toBe(true)
    expect(isNearBottom(1000, 800, 100)).toBe(false)
  })

  it('内容不足一屏时视为贴底，非法输入返回 false', () => {
    expect(isNearBottom(300, 0, 500)).toBe(true)
    expect(isNearBottom(Number.NaN, 0, 0)).toBe(false)
  })
})

describe('anchorScrollTop', () => {
  it('内容变高时增加 scrollTop 保持视口内容不动', () => {
    expect(anchorScrollTop(100, 1000, 1200)).toBe(300)
  })

  it('内容变矮时不产生负值', () => {
    expect(anchorScrollTop(100, 1000, 800)).toBe(0)
  })

  it('高度不变时保持原位置，非法输入返回 0', () => {
    expect(anchorScrollTop(250, 1000, 1000)).toBe(250)
    expect(anchorScrollTop(Number.NaN, 1000, 1200)).toBe(0)
  })
})

describe('shouldAutoLoadOlder', () => {
  const base = { atTop: true, hasMore: true, loading: false, rangeStart: 0 }

  it('已到顶部、还有更多、未在加载且窗口已无更早消息时触发', () => {
    expect(shouldAutoLoadOlder(base)).toBe(true)
  })

  it('未到顶部不触发', () => {
    expect(shouldAutoLoadOlder({ ...base, atTop: false })).toBe(false)
  })

  it('没有更早消息不触发', () => {
    expect(shouldAutoLoadOlder({ ...base, hasMore: false })).toBe(false)
  })

  it('正在加载或窗口内仍有更早消息时不触发', () => {
    expect(shouldAutoLoadOlder({ ...base, loading: true })).toBe(false)
    expect(shouldAutoLoadOlder({ ...base, rangeStart: 100 })).toBe(false)
  })
})
