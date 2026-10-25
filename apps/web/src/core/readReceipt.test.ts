import { describe, expect, it } from 'vitest'
import { lastOwnMessageOf, readStateOf, shouldAdvanceRead } from './readReceipt'
import type { Message } from '../types/frames'

function message(partial: Partial<Message> & Pick<Message, 'messageId' | 'senderId' | 'seq'>): Message {
  return {
    conversationId: 'cv-1',
    msgType: 'text',
    content: '你好',
    createdAt: 1_700_000_000_000,
    ...partial,
  }
}

const baseContext = {
  visible: true,
  atBottom: true,
  loaded: true,
  conversationId: 'cv-1',
  lastSeq: 5,
  reportedSeq: 3,
}

describe('shouldAdvanceRead', () => {
  it('页面可见、贴底、已加载且有新进度时推进', () => {
    expect(shouldAdvanceRead(baseContext)).toBe(true)
  })

  it('页面不可见时不推进', () => {
    expect(shouldAdvanceRead({ ...baseContext, visible: false })).toBe(false)
  })

  it('不在底部（向上翻阅旧消息）时不推进', () => {
    expect(shouldAdvanceRead({ ...baseContext, atBottom: false })).toBe(false)
  })

  it('历史未加载时不推进', () => {
    expect(shouldAdvanceRead({ ...baseContext, loaded: false })).toBe(false)
  })

  it('未进入会话时不推进', () => {
    expect(shouldAdvanceRead({ ...baseContext, conversationId: null })).toBe(false)
    expect(shouldAdvanceRead({ ...baseContext, conversationId: '' })).toBe(false)
  })

  it('没有新进度（lastSeq 非正或已上报）时不重复推进', () => {
    expect(shouldAdvanceRead({ ...baseContext, lastSeq: 0 })).toBe(false)
    expect(shouldAdvanceRead({ ...baseContext, lastSeq: -1 })).toBe(false)
    expect(shouldAdvanceRead({ ...baseContext, reportedSeq: 5 })).toBe(false)
    expect(shouldAdvanceRead({ ...baseContext, reportedSeq: 6 })).toBe(false)
  })
})

describe('readStateOf', () => {
  it('对端已读游标 >= 消息 seq 时为已读', () => {
    expect(readStateOf(3, 3)).toBe('read')
    expect(readStateOf(5, 3)).toBe('read')
  })

  it('对端游标为空或落后时为未读', () => {
    expect(readStateOf(undefined, 3)).toBe('unread')
    expect(readStateOf(null, 3)).toBe('unread')
    expect(readStateOf(2, 3)).toBe('unread')
    expect(readStateOf(Number.NaN, 1)).toBe('unread')
  })
})

describe('lastOwnMessageOf', () => {
  it('返回最后一条自己发出的消息', () => {
    const messages = [
      message({ messageId: 'm1', senderId: 'self', seq: 1 }),
      message({ messageId: 'm2', senderId: 'peer', seq: 2 }),
      message({ messageId: 'm3', senderId: 'self', seq: 3 }),
    ]
    expect(lastOwnMessageOf(messages, 'self')?.messageId).toBe('m3')
  })

  it('没有自己的消息、空列表或未提供 selfUserId 时返回 undefined', () => {
    expect(lastOwnMessageOf([], 'self')).toBeUndefined()
    expect(lastOwnMessageOf([message({ messageId: 'm1', senderId: 'peer', seq: 1 })], 'self')).toBeUndefined()
    expect(lastOwnMessageOf([message({ messageId: 'm1', senderId: 'self', seq: 1 })], undefined)).toBeUndefined()
  })
})
