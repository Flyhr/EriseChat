import { describe, expect, it } from 'vitest'
import {
  filterMentionCandidates,
  findMentionToken,
  hasSelfMention,
  insertMention,
  splitRichText,
} from './mentions'

// F-25 批次 1：@提及识别、候选过滤、插入与富文本分段纯函数单测。

const members = [
  { userId: 'u1', displayName: '张三' },
  { userId: 'u2', displayName: '张三丰' },
  { userId: 'u3', displayName: '李四' },
  { userId: 'u4', displayName: 'Alice' },
]

describe('findMentionToken', () => {
  it('空文本、光标在行首、非法光标返回 null', () => {
    expect(findMentionToken('', 0)).toBeNull()
    expect(findMentionToken('@张三', 0)).toBeNull()
    expect(findMentionToken('@张三', Number.NaN)).toBeNull()
  })

  it('刚输入 @ 返回空 query 片段', () => {
    expect(findMentionToken('@', 1)).toEqual({ start: 0, end: 1, query: '' })
  })

  it('行首 @ + 昵称时返回完整片段', () => {
    expect(findMentionToken('@张三', 3)).toEqual({ start: 0, end: 3, query: '张三' })
  })

  it('光标在片段中间（未到昵称末尾）也返回片段', () => {
    expect(findMentionToken('@张三丰', 2)).toEqual({ start: 0, end: 2, query: '张' })
  })

  it('@ 在空白/标点之后可用，中文与换行同样成立', () => {
    expect(findMentionToken('你好 @张', 5)).toEqual({ start: 3, end: 5, query: '张' })
    expect(findMentionToken('第一行\n@张', 6)).toEqual({ start: 4, end: 6, query: '张' })
    expect(findMentionToken('收到，@张', 5)).toEqual({ start: 3, end: 5, query: '张' })
  })

  it('@ 前是普通字符（如邮箱 a@b）不触发', () => {
    expect(findMentionToken('a@b', 3)).toBeNull()
    expect(findMentionToken('mail a@b.com', 12)).toBeNull()
  })

  it('片段内出现空格或换行后视为已结束', () => {
    expect(findMentionToken('@张三 你好', 7)).toBeNull()
    expect(findMentionToken('@张三\n你好', 7)).toBeNull()
    expect(findMentionToken('@ 张三', 4)).toBeNull()
  })

  it('query 超过 20 个字符不触发', () => {
    expect(findMentionToken(`@${'a'.repeat(20)}`, 21)).toEqual({ start: 0, end: 21, query: 'a'.repeat(20) })
    expect(findMentionToken(`@${'a'.repeat(21)}`, 22)).toBeNull()
  })

  it('多个 @ 时只取光标前最近的一个', () => {
    expect(findMentionToken('a@b @张', 6)).toEqual({ start: 4, end: 6, query: '张' })
  })
})

describe('filterMentionCandidates', () => {
  it('空 query 排除自己后按原顺序返回（最多 8 个）', () => {
    const list = filterMentionCandidates(members, '', 'u1')
    expect(list.map((item) => item.userId)).toEqual(['u2', 'u3', 'u4'])
  })

  it('前缀命中优先于包含命中，同组保持传入顺序', () => {
    expect(filterMentionCandidates(members, '张').map((item) => item.displayName)).toEqual(['张三', '张三丰'])
    expect(filterMentionCandidates(members, '三').map((item) => item.displayName)).toEqual(['张三', '张三丰'])
  })

  it('英文昵称大小写不敏感', () => {
    expect(filterMentionCandidates(members, 'ali').map((item) => item.userId)).toEqual(['u4'])
    expect(filterMentionCandidates(members, 'ALICE').map((item) => item.userId)).toEqual(['u4'])
  })

  it('不包含自己，limit 生效，无匹配返回空数组', () => {
    expect(filterMentionCandidates(members, '张', 'u2').map((item) => item.displayName)).toEqual(['张三'])
    expect(filterMentionCandidates(members, '', undefined, 2).map((item) => item.userId)).toEqual(['u1', 'u2'])
    expect(filterMentionCandidates(members, '王五')).toEqual([])
  })

  it('query 两端空白被裁剪', () => {
    expect(filterMentionCandidates(members, '  李四  ').map((item) => item.displayName)).toEqual(['李四'])
  })
})

describe('insertMention', () => {
  it('在片段范围插入「@昵称 」并返回插入后的光标位置', () => {
    expect(insertMention('你好 @张', { start: 3, end: 5, query: '张' }, { userId: 'u1', displayName: '张三' })).toEqual({
      text: '你好 @张三 ',
      caret: 7,
    })
  })

  it('片段在文本中间时只替换该范围，保留前后文字', () => {
    expect(insertMention('@张 后文', { start: 0, end: 2, query: '张' }, { userId: 'u1', displayName: '张三' })).toEqual({
      text: '@张三  后文',
      caret: 4,
    })
  })

  it('非法 token 下标会被夹紧到文本范围', () => {
    expect(insertMention('hi', { start: -5, end: 99, query: '' }, { userId: 'u1', displayName: '张三' })).toEqual({
      text: '@张三 ',
      caret: 4,
    })
  })
})

describe('splitRichText', () => {
  it('空文本返回空数组', () => {
    expect(splitRichText('', ['张三'])).toEqual([])
    expect(splitRichText(null as unknown as string, ['张三'])).toEqual([])
  })

  it('无提及与无链接返回单段文本', () => {
    expect(splitRichText('普通消息，没有提及', ['张三'])).toEqual([{ kind: 'text', value: '普通消息，没有提及' }])
  })

  it('行首提及、结尾提及、后面跟中文标点都能分段', () => {
    expect(splitRichText('@张三 你好', ['张三'])).toEqual([
      { kind: 'mention', value: '@张三', self: false },
      { kind: 'text', value: ' 你好' },
    ])
    expect(splitRichText('你好 @张三', ['张三'])).toEqual([
      { kind: 'text', value: '你好 ' },
      { kind: 'mention', value: '@张三', self: false },
    ])
    expect(splitRichText('@张三，收到', ['张三'])).toEqual([
      { kind: 'mention', value: '@张三', self: false },
      { kind: 'text', value: '，收到' },
    ])
  })

  it('昵称按长度降序匹配：@张三丰 不会误判为 @张三', () => {
    expect(splitRichText('@张三丰 你好', ['张三', '张三丰'])).toEqual([
      { kind: 'mention', value: '@张三丰', self: false },
      { kind: 'text', value: ' 你好' },
    ])
  })

  it('@ 前不是边界（如 abc@张三）按普通文本处理', () => {
    expect(splitRichText('abc@张三', ['张三'])).toEqual([{ kind: 'text', value: 'abc@张三' }])
  })

  it('未知昵称、未闭合昵称不产生提及段', () => {
    expect(splitRichText('@王五 你好', ['张三'])).toEqual([{ kind: 'text', value: '@王五 你好' }])
    expect(splitRichText('@张 你好', ['张三'])).toEqual([{ kind: 'text', value: '@张 你好' }])
  })

  it('self 标记：与 selfName 一致时为 true，其他提及时为 false', () => {
    expect(splitRichText('@张三 和 @李四', ['张三', '李四'], '张三')).toEqual([
      { kind: 'mention', value: '@张三', self: true },
      { kind: 'text', value: ' 和 ' },
      { kind: 'mention', value: '@李四', self: false },
    ])
  })

  it('链接段优先：URL 内的 @ 不算提及', () => {
    expect(splitRichText('https://example.com/@张三 看', ['张三'])).toEqual([
      { kind: 'link', value: 'https://example.com/@张三', href: 'https://example.com/@张三' },
      { kind: 'text', value: ' 看' },
    ])
  })

  it('URL 与 @ 混合时保持原文顺序', () => {
    expect(splitRichText('看 https://a.com/x 和 @张三', ['张三'])).toEqual([
      { kind: 'text', value: '看 ' },
      { kind: 'link', value: 'https://a.com/x', href: 'https://a.com/x' },
      { kind: 'text', value: ' 和 ' },
      { kind: 'mention', value: '@张三', self: false },
    ])
  })

  it('多个提及与英文昵称都能识别', () => {
    expect(splitRichText('@Alice @张三 开会', ['Alice', '张三'])).toEqual([
      { kind: 'mention', value: '@Alice', self: false },
      { kind: 'text', value: ' ' },
      { kind: 'mention', value: '@张三', self: false },
      { kind: 'text', value: ' 开会' },
    ])
  })
})

describe('hasSelfMention', () => {
  it('包含对 selfName 的完整提及时为 true', () => {
    expect(hasSelfMention('@张三 你好', '张三')).toBe(true)
    expect(hasSelfMention('你好 @张三，收到', '张三')).toBe(true)
  })

  it('未提及、只提及他人、selfName 为空时为 false', () => {
    expect(hasSelfMention('你好', '张三')).toBe(false)
    expect(hasSelfMention('@李四 你好', '张三')).toBe(false)
    expect(hasSelfMention('@张三 你好', '')).toBe(false)
  })
})
