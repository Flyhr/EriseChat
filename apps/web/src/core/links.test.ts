import { describe, expect, it } from 'vitest'
import { MAX_LINK_SEGMENTS, isSafeExternalUrl, splitLinks } from './links'

// F-07 批次 1：链接识别与安全 URL 纯函数单测（分段、标点剥离、协议白名单、数量上限）。

describe('isSafeExternalUrl', () => {
  it('允许 http / https（协议大小写不敏感）', () => {
    expect(isSafeExternalUrl('http://example.com')).toBe(true)
    expect(isSafeExternalUrl('https://example.com/path?x=1#h')).toBe(true)
    expect(isSafeExternalUrl('HTTPS://EXAMPLE.COM')).toBe(true)
    expect(isSafeExternalUrl('  https://example.com  ')).toBe(true)
  })

  it('拒绝非 http(s) 协议', () => {
    expect(isSafeExternalUrl('javascript:alert(1)')).toBe(false)
    expect(isSafeExternalUrl('data:text/html,<script>alert(1)</script>')).toBe(false)
    expect(isSafeExternalUrl('file:///C:/Windows/System32/calc.exe')).toBe(false)
    expect(isSafeExternalUrl('ftp://example.com/file.txt')).toBe(false)
    expect(isSafeExternalUrl('mailto:a@b.com')).toBe(false)
  })

  it('拒绝空串、无主机与非法 URL', () => {
    expect(isSafeExternalUrl('')).toBe(false)
    expect(isSafeExternalUrl('   ')).toBe(false)
    expect(isSafeExternalUrl('https://')).toBe(false)
    expect(isSafeExternalUrl('http:///')).toBe(false)
    expect(isSafeExternalUrl('not a url')).toBe(false)
    expect(isSafeExternalUrl('example.com')).toBe(false)
  })
})

describe('splitLinks', () => {
  it('空文本或非字符串输入返回空数组', () => {
    expect(splitLinks('')).toEqual([])
    expect(splitLinks(null as unknown as string)).toEqual([])
    expect(splitLinks(undefined as unknown as string)).toEqual([])
  })

  it('无链接时返回单段文本', () => {
    expect(splitLinks('普通消息，没有链接')).toEqual([{ kind: 'text', value: '普通消息，没有链接' }])
  })

  it('整条文本就是一个链接', () => {
    expect(splitLinks('https://example.com')).toEqual([
      { kind: 'link', value: 'https://example.com', href: 'https://example.com' },
    ])
  })

  it('链接前后带文本时正确分段', () => {
    expect(splitLinks('看这个 https://example.com/a 很好')).toEqual([
      { kind: 'text', value: '看这个 ' },
      { kind: 'link', value: 'https://example.com/a', href: 'https://example.com/a' },
      { kind: 'text', value: ' 很好' },
    ])
  })

  it('多个链接与换行分隔都保留', () => {
    const segments = splitLinks('https://a.com 和 https://b.com/x\n第二行 https://c.com')
    expect(segments.map((segment) => segment.kind)).toEqual(['link', 'text', 'link', 'text', 'link'])
    expect(segments.filter((segment) => segment.kind === 'link')).toHaveLength(3)
  })

  it('大小写混合的协议也能识别', () => {
    expect(splitLinks('HtTpS://Example.COM/Path')[0]).toMatchObject({
      kind: 'link',
      href: 'HtTpS://Example.COM/Path',
    })
    expect(splitLinks('HTTP://example.com')[0]).toMatchObject({ kind: 'link' })
  })

  it('剥掉尾部中文句读，句号保留为文本', () => {
    expect(splitLinks('https://a.com/path。继续')).toEqual([
      { kind: 'link', value: 'https://a.com/path', href: 'https://a.com/path' },
      { kind: 'text', value: '。继续' },
    ])
  })

  it('剥掉尾部英文标点组合', () => {
    expect(splitLinks('见 https://a.com/x?y=1, 后面')).toEqual([
      { kind: 'text', value: '见 ' },
      { kind: 'link', value: 'https://a.com/x?y=1', href: 'https://a.com/x?y=1' },
      { kind: 'text', value: ', 后面' },
    ])
  })

  it('括号包裹的链接只识别链接本体', () => {
    expect(splitLinks('(https://a.com/x)')).toEqual([
      { kind: 'text', value: '(' },
      { kind: 'link', value: 'https://a.com/x', href: 'https://a.com/x' },
      { kind: 'text', value: ')' },
    ])
    expect(splitLinks('「https://a.com/y」')).toEqual([
      { kind: 'text', value: '「' },
      { kind: 'link', value: 'https://a.com/y', href: 'https://a.com/y' },
      { kind: 'text', value: '」' },
    ])
  })

  it('javascript: / data: / ftp: / file: 不识别为链接', () => {
    for (const text of [
      'javascript:alert(1)',
      'data:text/html,<script>alert(1)</script>',
      'ftp://example.com/file.txt',
      'file:///C:/Windows/System32/calc.exe',
    ]) {
      const segments = splitLinks(text)
      expect(segments.every((segment) => segment.kind === 'text')).toBe(true)
      expect(segments.map((segment) => segment.value).join('')).toBe(text)
    }
  })

  it('仅凭 https:// 没有主机名时不识别', () => {
    expect(splitLinks('https://')).toEqual([{ kind: 'text', value: 'https://' }])
  })

  it('超过上限的链接按文本渲染', () => {
    const links = Array.from({ length: MAX_LINK_SEGMENTS + 1 }, (_, index) => `https://a.com/${index}`)
    const text = links.join(' ')
    const segments = splitLinks(text)
    const linkSegments = segments.filter((segment) => segment.kind === 'link')
    expect(linkSegments).toHaveLength(MAX_LINK_SEGMENTS)
    expect(segments.map((segment) => segment.value).join('')).toBe(text)
    expect(segments[segments.length - 1]?.kind).toBe('text')
  })

  it('分段值拼接后与原文本完全一致（含标点与空白）', () => {
    const samples = [
      '纯文本',
      '前 https://a.com 后',
      'https://a.com。',
      '（https://a.com/path?x=1）',
      'a https://a.com b https://b.com/c。d',
      'javascript:alert(1) https://ok.example.com',
      'https://a.com/x?y=1,please!',
    ]
    for (const sample of samples) {
      expect(splitLinks(sample).map((segment) => segment.value).join('')).toBe(sample)
    }
  })
})
