import { describe, expect, it } from 'vitest'
import {
  LOCAL_SEARCH_LIMIT,
  countLocalMatches,
  mergeLocalSearchResults,
  searchLocalMessages,
  searchableTextOf,
} from './localSearch'
import type { Message } from '../types/frames'

// F-19 批次1：会话内本地搜索纯函数单测。
// 覆盖大小写/trim/正则元字符/已撤回/图片/系统/文件名/排序/limit/空查询等边界。

let nextId = 0

function textMessage(text: string, seq: number, patch: Partial<Message> = {}): Message {
  nextId += 1
  return {
    messageId: `m-${nextId}`,
    conversationId: 'cv-1',
    senderId: 'u-1',
    seq,
    msgType: 'text',
    content: text,
    createdAt: 1_800_000_000_000 + seq,
    ...patch,
  }
}

function fileMessage(name: string, seq: number, patch: Partial<Message> = {}): Message {
  return textMessage('', seq, {
    msgType: 'file',
    content: { fileId: `f-${seq}`, name, size: 1024, mime: 'application/pdf' },
    ...patch,
  })
}

describe('searchLocalMessages', () => {
  it('空关键词/纯空白返回空数组', () => {
    const messages = [textMessage('hello world', 1)]
    expect(searchLocalMessages(messages, '')).toEqual([])
    expect(searchLocalMessages(messages, '   ')).toEqual([])
    expect(searchLocalMessages(messages, '\n\t')).toEqual([])
  })

  it('中文关键词直接 includes 命中', () => {
    const messages = [textMessage('今天下午三点开会，请准时参加', 1)]
    expect(searchLocalMessages(messages, '开会')).toHaveLength(1)
    expect(searchLocalMessages(messages, '三点开会')).toHaveLength(1)
    expect(searchLocalMessages(messages, '明早')).toHaveLength(0)
  })

  it('英文大小写不敏感', () => {
    const messages = [textMessage('Deploy the Release today', 1)]
    expect(searchLocalMessages(messages, 'release')).toHaveLength(1)
    expect(searchLocalMessages(messages, 'DEPLOY')).toHaveLength(1)
    expect(searchLocalMessages(messages, 'dEpLoY')).toHaveLength(1)
  })

  it('关键词前后空白先 trim 再匹配', () => {
    const messages = [textMessage('会议纪要已发群里', 1)]
    expect(searchLocalMessages(messages, '  纪要  ')).toHaveLength(1)
  })

  it('正则元字符按字面匹配且不会抛错', () => {
    const messages = [
      textMessage('公式 a+b 的推导', 1),
      textMessage('匹配 .* 通配符', 2),
      textMessage('括号 (示例) 说明', 3),
      textMessage('axb 不应命中', 4),
    ]
    expect(() => searchLocalMessages(messages, 'a+b')).not.toThrow()
    expect(() => searchLocalMessages(messages, '.*')).not.toThrow()
    expect(() => searchLocalMessages(messages, '(')).not.toThrow()
    expect(searchLocalMessages(messages, 'a+b').map((m) => m.seq)).toEqual([1])
    expect(searchLocalMessages(messages, '.*').map((m) => m.seq)).toEqual([2])
    expect(searchLocalMessages(messages, '(').map((m) => m.seq)).toEqual([3])
    expect(searchLocalMessages(messages, 'a+b')).not.toContainEqual(expect.objectContaining({ seq: 4 }))
  })

  it('已撤回消息不参与匹配', () => {
    const messages = [
      textMessage('这条消息已撤回', 1, { deletedAt: 1_800_000_000_100 }),
      textMessage('这条消息正常', 2),
    ]
    expect(searchLocalMessages(messages, '消息')).toHaveLength(1)
    expect(searchLocalMessages(messages, '消息').map((m) => m.seq)).toEqual([2])
  })

  it('图片与系统消息不参与匹配', () => {
    const image: Message = textMessage('', 1, {
      msgType: 'image',
      content: { fileId: 'f-1', name: '截图.png', size: 10, mime: 'image/png' },
    })
    const system: Message = textMessage('', 2, {
      msgType: 'system',
      content: { event: 'group.created' },
    })
    expect(searchLocalMessages([image, system], '截图')).toEqual([])
    expect(searchLocalMessages([image, system], 'group')).toEqual([])
  })

  it('文件消息按文件名命中', () => {
    const messages = [fileMessage('季度汇报-最终版.pdf', 1), fileMessage('报销单.xlsx', 2)]
    expect(searchLocalMessages(messages, '汇报')).toHaveLength(1)
    expect(searchLocalMessages(messages, 'xlsx')).toHaveLength(1)
    expect(searchLocalMessages(messages, 'pdf').map((m) => m.seq)).toEqual([1])
    expect(searchLocalMessages(messages, '不存在的文件')).toEqual([])
  })

  it('结果按 seq 降序排列', () => {
    const messages = [
      textMessage('测试消息 1', 1),
      textMessage('测试消息 3', 3),
      textMessage('测试消息 2', 2),
    ]
    expect(searchLocalMessages(messages, '测试').map((m) => m.seq)).toEqual([3, 2, 1])
  })

  it('limit 截断默认 100 条且可自定义', () => {
    const messages = Array.from({ length: 120 }, (_, index) =>
      textMessage(`批量消息 ${index}`, index + 1),
    )
    expect(searchLocalMessages(messages, '批量')).toHaveLength(LOCAL_SEARCH_LIMIT)
    expect(searchLocalMessages(messages, '批量', { limit: 5 })).toHaveLength(5)
    expect(searchLocalMessages(messages, '批量', { limit: 5 }).map((m) => m.seq)).toEqual([120, 119, 118, 117, 116])
    expect(searchLocalMessages(messages, '批量', { limit: 0 })).toHaveLength(LOCAL_SEARCH_LIMIT)
  })

  it('无命中返回空数组', () => {
    expect(searchLocalMessages([textMessage('服务器维护通知', 1)], '发布计划')).toEqual([])
  })

  it('不修改入参数组与消息对象', () => {
    const messages = [textMessage('甲', 1), textMessage('乙', 2)]
    const snapshot = messages.map((message) => ({ ...message }))
    searchLocalMessages(messages, '甲')
    expect(messages).toHaveLength(2)
    expect(messages[0]).toMatchObject(snapshot[0])
    expect(messages[1]).toMatchObject(snapshot[1])
  })
})

describe('countLocalMatches', () => {
  it('返回不受 limit 限制的命中总数', () => {
    const messages = Array.from({ length: 8 }, (_, index) => textMessage('重复关键词', index + 1))
    expect(countLocalMatches(messages, '重复')).toBe(8)
    expect(searchLocalMessages(messages, '重复', { limit: 3 })).toHaveLength(3)
  })

  it('空关键词/无命中返回 0', () => {
    expect(countLocalMatches([textMessage('abc', 1)], ' ')).toBe(0)
    expect(countLocalMatches([textMessage('abc', 1)], 'zzz')).toBe(0)
  })
})

describe('searchableTextOf', () => {
  it('text 消息取字符串 content，非法结构返回 null', () => {
    expect(searchableTextOf(textMessage('正常文本', 1))).toBe('正常文本')
    const broken: Message = textMessage('', 1, {
      content: { name: 'x.txt' } as unknown as Message['content'],
    })
    expect(searchableTextOf(broken)).toBeNull()
  })

  it('file 消息缺少 name 时返回 null', () => {
    const broken: Message = textMessage('', 1, {
      msgType: 'file',
      content: { fileId: 'f-1' } as unknown as Message['content'],
    })
    expect(searchableTextOf(broken)).toBeNull()
  })
})

describe('mergeLocalSearchResults', () => {
  it('服务端结果返回后仍保留本地独有的文件命中', () => {
    const server = [textMessage('会议纪要', 5, { messageId: 'srv-text-5' })]
    const local = [fileMessage('会议纪要.pdf', 3, { messageId: 'local-file-3' })]

    const merged = mergeLocalSearchResults(server, local)

    expect(merged).toHaveLength(2)
    expect(merged.map((message) => message.messageId)).toContain('local-file-3')
    expect(merged.some((message) => message.msgType === 'file')).toBe(true)
  })

  it('重复 messageId 去重且以服务端结果为准', () => {
    const local = [textMessage('旧内容', 2, { messageId: 'dup-1' })]
    const server = [textMessage('服务端内容', 2, { messageId: 'dup-1' })]

    const merged = mergeLocalSearchResults(server, local)

    expect(merged).toHaveLength(1)
    expect(merged[0].content).toBe('服务端内容')
  })

  it('合并后按 seq 降序排序且不修改入参', () => {
    const server = [
      textMessage('a', 1, { messageId: 'srv-1' }),
      textMessage('b', 7, { messageId: 'srv-7' }),
    ]
    const local = [fileMessage('c.pdf', 4, { messageId: 'local-4' })]

    const merged = mergeLocalSearchResults(server, local)

    expect(merged.map((message) => message.seq)).toEqual([7, 4, 1])
    expect(server.map((message) => message.seq)).toEqual([1, 7])
    expect(local).toHaveLength(1)
  })
})
