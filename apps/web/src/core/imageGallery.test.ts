import { describe, expect, it } from 'vitest'
import {
  MAX_SCALE,
  MIN_SCALE,
  clampScale,
  formatScale,
  imageItemsOf,
  nextScale,
  stepIndex,
} from './imageGallery'

// F-08 批次 1：图片预览纯函数单测（列表提取、环形切换、缩放边界与文案）。

const image = (messageId: string, name?: string): {
  messageId: string
  msgType: string
  content: { fileId: string; name: string }
} => ({
  messageId,
  msgType: 'image',
  content: { fileId: `file-${messageId}`, name: name ?? `${messageId}.png` },
})

describe('imageItemsOf', () => {
  it('空列表返回空数组，非数组输入安全返回', () => {
    expect(imageItemsOf([])).toEqual([])
    expect(imageItemsOf(null as unknown as [])).toEqual([])
  })

  it('只保留 image 消息并保持原顺序', () => {
    const items = imageItemsOf([
      image('m1', '一.png'),
      { messageId: 't1', msgType: 'text', content: 'hi' },
      image('m2', '二.png'),
      { messageId: 'f1', msgType: 'file', content: { fileId: 'f', name: 'a.pdf' } },
      { messageId: 's1', msgType: 'system', content: { text: 'created' } },
    ])
    expect(items).toEqual([
      { messageId: 'm1', name: '一.png' },
      { messageId: 'm2', name: '二.png' },
    ])
  })

  it('跳过 content 不是对象 / 缺 fileId / fileId 为空的消息', () => {
    const items = imageItemsOf([
      { messageId: 'a', msgType: 'image', content: 'string-content' },
      { messageId: 'b', msgType: 'image', content: { name: 'no-file-id.png' } },
      { messageId: 'c', msgType: 'image', content: { fileId: '', name: 'empty.png' } },
      { messageId: 'd', msgType: 'image', content: { fileId: 'ok', name: 'ok.png' } },
    ])
    expect(items).toEqual([{ messageId: 'd', name: 'ok.png' }])
  })

  it('缺少 name 或 name 为空白时回退为「图片」', () => {
    const items = imageItemsOf([
      { messageId: 'a', msgType: 'image', content: { fileId: 'f1' } },
      { messageId: 'b', msgType: 'image', content: { fileId: 'f2', name: '   ' } },
    ])
    expect(items.map((item) => item.name)).toEqual(['图片', '图片'])
  })
})

describe('stepIndex', () => {
  it('空列表返回 -1', () => {
    expect(stepIndex(0, 1, 0)).toBe(-1)
    expect(stepIndex(2, -1, -3)).toBe(-1)
  })

  it('环形前进与后退', () => {
    expect(stepIndex(0, 1, 3)).toBe(1)
    expect(stepIndex(2, 1, 3)).toBe(0)
    expect(stepIndex(0, -1, 3)).toBe(2)
  })

  it('单图时始终停留在 0', () => {
    expect(stepIndex(0, 1, 1)).toBe(0)
    expect(stepIndex(0, -5, 1)).toBe(0)
  })

  it('不环形时钳制在边界', () => {
    expect(stepIndex(2, 1, 3, false)).toBe(2)
    expect(stepIndex(0, -1, 3, false)).toBe(0)
    expect(stepIndex(1, 1, 3, false)).toBe(2)
  })

  it('越界/非法输入安全处理', () => {
    expect(stepIndex(10, 0, 3)).toBe(1)
    expect(stepIndex(Number.NaN, 1, 3)).toBe(1)
    expect(stepIndex(1, Number.NaN, 3)).toBe(1)
  })
})

describe('clampScale / nextScale / formatScale', () => {
  it('clampScale 钳制到默认 1x~4x', () => {
    expect(clampScale(0.2)).toBe(MIN_SCALE)
    expect(clampScale(9)).toBe(MAX_SCALE)
    expect(clampScale(2.5)).toBe(2.5)
    expect(clampScale(Number.NaN)).toBe(MIN_SCALE)
  })

  it('nextScale 按 0.25 步长增减并钳制', () => {
    expect(nextScale(1, 1)).toBe(1.25)
    expect(nextScale(1, -1)).toBe(1)
    expect(nextScale(4, 1)).toBe(4)
    expect(nextScale(3.9, 1)).toBe(4)
  })

  it('nextScale 消除浮点噪声', () => {
    expect(nextScale(1.1, 1)).toBe(1.35)
    expect(nextScale(1.35, -1)).toBe(1.1)
  })

  it('nextScale 支持自定义步长与区间', () => {
    expect(nextScale(1, 1, { step: 0.5, min: 0.5, max: 2 })).toBe(1.5)
    expect(nextScale(2, 1, { step: 0.5, min: 0.5, max: 2 })).toBe(2)
  })

  it('formatScale 输出百分比文案', () => {
    expect(formatScale(1)).toBe('100%')
    expect(formatScale(1.5)).toBe('150%')
    expect(formatScale(4)).toBe('400%')
    expect(formatScale(Number.NaN)).toBe('100%')
  })
})
