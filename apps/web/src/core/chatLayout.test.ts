import { describe, expect, it } from 'vitest'
import {
  buildMessageItems,
  dateSeparatorLabel,
  filterConversations,
  shouldShowNewMessageBadge,
  type ConversationFilter,
} from './chatLayout'
import type { Conversation, Message } from '../types/frames'

// UI-02 批次5：聊天日期分组 / 会话筛选 / 新消息浮标纯函数单测。
// 固定「今天」为本地时间 2026-10-26 12:00，避免依赖运行时的当前时间。
const NOW = new Date(2026, 9, 26, 12, 0, 0).getTime()

function at(year: number, month: number, day: number, hour = 10): number {
  return new Date(year, month - 1, day, hour, 0, 0).getTime()
}

function message(seq: number, createdAt: number, conversationId = 'cv-1'): Message {
  return {
    messageId: `m-${conversationId}-${seq}`,
    conversationId,
    senderId: 'u-1',
    seq,
    msgType: 'text',
    content: `第 ${seq} 条`,
    createdAt,
  }
}

function conversation(
  conversationId: string,
  type: 'direct' | 'group',
  createdAt: number,
): Conversation {
  return {
    conversationId,
    type,
    title: conversationId,
    createdAt,
    lastSeq: 0,
    lastReadSeq: 0,
    members: [],
  }
}

describe('dateSeparatorLabel', () => {
  it('今天返回「今天」', () => {
    expect(dateSeparatorLabel(at(2026, 10, 26, 8), NOW)).toBe('今天')
  })

  it('昨天返回「昨天」', () => {
    expect(dateSeparatorLabel(at(2026, 10, 25, 23), NOW)).toBe('昨天')
  })

  it('更早日期返回 MM-DD（跨年也按本月日）', () => {
    expect(dateSeparatorLabel(at(2025, 12, 31), NOW)).toBe('12-31')
  })

  it('非法时间返回空串（不插分隔）', () => {
    expect(dateSeparatorLabel(Number.NaN, NOW)).toBe('')
  })
})

describe('buildMessageItems', () => {
  it('空列表返回空数组', () => {
    expect(buildMessageItems([], NOW)).toEqual([])
  })

  it('单日消息只在开头插入一个分隔，消息顺序不变', () => {
    const items = buildMessageItems(
      [message(1, at(2026, 10, 26, 9)), message(2, at(2026, 10, 26, 10))],
      NOW,
    )
    expect(items.map((item) => item.kind)).toEqual(['separator', 'message', 'message'])
    expect(items[0]).toMatchObject({ kind: 'separator', label: '今天' })
    expect(items[1]).toMatchObject({ kind: 'message', key: 'm-cv-1-1' })
    expect(items[2]).toMatchObject({ kind: 'message', key: 'm-cv-1-2' })
  })

  it('跨天处插入日期分隔且保持输入顺序', () => {
    const items = buildMessageItems(
      [
        message(1, at(2026, 10, 25, 9)),
        message(2, at(2026, 10, 25, 10)),
        message(3, at(2026, 10, 26, 9)),
      ],
      NOW,
    )
    expect(items.map((item) => item.kind)).toEqual([
      'separator',
      'message',
      'message',
      'separator',
      'message',
    ])
    expect(items[0]).toMatchObject({ kind: 'separator', label: '昨天' })
    expect(items[3]).toMatchObject({ kind: 'separator', label: '今天' })
    expect(items[4]).toMatchObject({ kind: 'message', key: 'm-cv-1-3' })
  })

  it('分隔 key 稳定可去重且消息 key 使用 messageId', () => {
    const items = buildMessageItems([message(1, at(2026, 10, 26, 9))], NOW)
    expect(items[0].key).toBe('separator-2026-10-26')
    expect(items[1].key).toBe('m-cv-1-1')
  })

  it('窗口切片传入前一条消息时，同一自然日不重复插入日期分隔', () => {
    const previous = message(10, at(2026, 10, 26, 8))
    const items = buildMessageItems([message(11, at(2026, 10, 26, 9))], NOW, previous)
    expect(items.map((item) => item.kind)).toEqual(['message'])
    expect(items[0]).toMatchObject({ kind: 'message', key: 'm-cv-1-11' })
  })

  it('窗口切片跨天时仍插入日期分隔', () => {
    const previous = message(10, at(2026, 10, 25, 23))
    const items = buildMessageItems([message(11, at(2026, 10, 26, 9))], NOW, previous)
    expect(items.map((item) => item.kind)).toEqual(['separator', 'message'])
    expect(items[0]).toMatchObject({ kind: 'separator', label: '今天' })
    expect(items[1]).toMatchObject({ kind: 'message', key: 'm-cv-1-11' })
  })

  it('前一条消息时间非法时按无前置消息处理（窗口首条照常插分隔）', () => {
    const items = buildMessageItems(
      [message(11, at(2026, 10, 26, 9))],
      NOW,
      message(10, Number.NaN),
    )
    expect(items.map((item) => item.kind)).toEqual(['separator', 'message'])
  })
})

describe('filterConversations', () => {
  const conversations = [
    conversation('cv-direct-1', 'direct', at(2026, 10, 26, 9)),
    conversation('cv-group-1', 'group', at(2026, 10, 26, 10)),
    conversation('cv-group-2', 'group', at(2026, 10, 26, 11)),
  ]

  it('all 返回全部且顺序不变', () => {
    const result = filterConversations(conversations, 'all')
    expect(result.map((item) => item.conversationId)).toEqual([
      'cv-direct-1',
      'cv-group-1',
      'cv-group-2',
    ])
  })

  it('unread 仅保留未读数 > 0 的会话', () => {
    const result = filterConversations(conversations, 'unread', (id) =>
      id === 'cv-group-1' ? 2 : 0,
    )
    expect(result.map((item) => item.conversationId)).toEqual(['cv-group-1'])
  })

  it('group 仅保留群聊（保持原顺序）', () => {
    const result = filterConversations(conversations, 'group')
    expect(result.map((item) => item.conversationId)).toEqual(['cv-group-1', 'cv-group-2'])
  })

  it('空列表与未知筛选条件不抛错', () => {
    expect(filterConversations([], 'unread', () => 1)).toEqual([])
    expect(filterConversations([], 'group')).toEqual([])
    // 运行时若拿到意外值，按「全部」宽松处理，避免页面空白。
    expect(filterConversations(conversations, 'unknown' as ConversationFilter)).toHaveLength(3)
  })
})

describe('shouldShowNewMessageBadge', () => {
  it('贴底时不显示浮标', () => {
    expect(shouldShowNewMessageBadge(true, 5)).toBe(false)
  })

  it('未贴底且有新消息时显示', () => {
    expect(shouldShowNewMessageBadge(false, 1)).toBe(true)
  })

  it('未贴底但无累计新消息时不显示', () => {
    expect(shouldShowNewMessageBadge(false, 0)).toBe(false)
  })
})
