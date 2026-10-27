import { describe, expect, it } from 'vitest'
import { highlightSegments, snippetOf } from './searchHighlight'

describe('highlightSegments', () => {
  it('空文本返回空数组', () => {
    expect(highlightSegments('', '关键词')).toEqual([])
  })

  it('空关键词不高亮，返回单个片段', () => {
    expect(highlightSegments('今天开会', '  ')).toEqual([{ text: '今天开会', hit: false }])
  })

  it('中文命中切成三段', () => {
    expect(highlightSegments('今天开会讨论', '开会')).toEqual([
      { text: '今天', hit: false },
      { text: '开会', hit: true },
      { text: '讨论', hit: false },
    ])
  })

  it('ASCII 大小写不敏感，保留原文大小写', () => {
    expect(highlightSegments('Hello WORLD hello', 'hello')).toEqual([
      { text: 'Hello', hit: true },
      { text: ' WORLD ', hit: false },
      { text: 'hello', hit: true },
    ])
  })

  it('多次命中全部标出', () => {
    const segments = highlightSegments('abABab', 'ab')
    expect(segments.filter((segment) => segment.hit)).toHaveLength(3)
    expect(segments.map((segment) => segment.text).join('')).toBe('abABab')
  })

  it('关键词在开头/结尾时无空片段', () => {
    expect(highlightSegments('开会啦', '开会')).toEqual([
      { text: '开会', hit: true },
      { text: '啦', hit: false },
    ])
    expect(highlightSegments('去开会', '开会')).toEqual([
      { text: '去', hit: false },
      { text: '开会', hit: true },
    ])
  })

  it('正则特殊字符按字面匹配（不抛错）', () => {
    expect(highlightSegments('价格 1+1=2', '1+1')).toEqual([
      { text: '价格 ', hit: false },
      { text: '1+1', hit: true },
      { text: '=2', hit: false },
    ])
    expect(highlightSegments('a.b*c', '.')).toEqual([
      { text: 'a', hit: false },
      { text: '.', hit: true },
      { text: 'b*c', hit: false },
    ])
  })

  it('仅返回拼接后可还原原文的片段', () => {
    const text = '前 中 后 中 尾'
    const segments = highlightSegments(text, '中')
    expect(segments.map((segment) => segment.text).join('')).toBe(text)
  })
})

describe('snippetOf', () => {
  it('短文本原样返回', () => {
    expect(snippetOf('今天开会', '开会')).toBe('今天开会')
  })

  it('长文本以命中为中心截取并补省略号', () => {
    const text = `${'甲'.repeat(60)}关键词${'乙'.repeat(60)}`
    const snippet = snippetOf(text, '关键词', 10)
    expect(snippet.startsWith('…')).toBe(true)
    expect(snippet.endsWith('…')).toBe(true)
    expect(snippet).toContain('关键词')
    expect(snippet.length).toBeLessThan(text.length)
  })

  it('命中在开头时只补尾部省略号', () => {
    const snippet = snippetOf(`${'开会在最前'}${'丙'.repeat(80)}`, '开会', 5)
    expect(snippet.startsWith('…')).toBe(false)
    expect(snippet.endsWith('…')).toBe(true)
  })

  it('未命中时返回开头一段并补省略号', () => {
    const snippet = snippetOf('甲'.repeat(100), '不存在', 10)
    expect(snippet).toBe(`${'甲'.repeat(20)}…`)
  })

  it('非法/缺失半径按默认值处理', () => {
    const text = `${'甲'.repeat(40)}命中${'乙'.repeat(40)}`
    expect(snippetOf(text, '命中', Number.NaN)).toContain('命中')
    expect(snippetOf(text, '命中', -3)).toContain('命中')
  })

  it('空关键词返回开头一段', () => {
    const text = '甲'.repeat(100)
    expect(snippetOf(text, '  ', 10)).toBe(`${'甲'.repeat(20)}…`)
  })

  it('摘要仍可用 highlightSegments 渲染高亮', () => {
    const text = `${'甲'.repeat(30)}关键词${'乙'.repeat(30)}`
    const segments = highlightSegments(snippetOf(text, '关键词', 8), '关键词')
    expect(segments.some((segment) => segment.hit)).toBe(true)
  })
})
