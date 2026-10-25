// 补拉/合并纯逻辑单测（M2-07）。协议约定见 protocol/protocol.md §4.4，硬要求见 AGENTS.md §5。
import { describe, expect, it } from 'vitest'
import type { Message } from '../types/frames'
import {
  buildSyncCursors,
  dedupeAndSort,
  detectGap,
  mergeMessages,
  syncBatchLimit,
  unreadCount,
} from './sync'

function message(seq: number, overrides: Partial<Message> = {}): Message {
  return {
    messageId: `m-${seq}`,
    conversationId: 'conv-1',
    senderId: 'user-1',
    seq,
    msgType: 'text',
    content: `消息 ${seq}`,
    createdAt: 1_700_000_000_000 + seq,
    ...overrides,
  }
}

describe('dedupeAndSort', () => {
  it('按 messageId 去重，同一 id 以后出现者为准', () => {
    const first = message(2, { content: '旧内容' })
    const second = message(2, { content: '新内容' })
    const result = dedupeAndSort([first, second])
    expect(result).toHaveLength(1)
    expect(result[0]?.content).toBe('新内容')
  })

  it('按 seq 升序排序且不修改入参', () => {
    const input = [message(3), message(1), message(2)]
    const snapshot = input.map((item) => item.seq)
    const result = dedupeAndSort(input)
    expect(result.map((item) => item.seq)).toEqual([1, 2, 3])
    expect(input.map((item) => item.seq)).toEqual(snapshot)
  })

  it('空数组返回空数组', () => {
    expect(dedupeAndSort([])).toEqual([])
  })
})

describe('mergeMessages', () => {
  it('合并本地缓存与增量消息，去重后按 seq 排序', () => {
    const existing = [message(1), message(2)]
    const incoming = [message(2), message(3), message(4)]
    const result = mergeMessages(existing, incoming)
    expect(result.map((item) => item.seq)).toEqual([1, 2, 3, 4])
    expect(new Set(result.map((item) => item.messageId)).size).toBe(4)
  })

  it('incoming 中的同 id 消息覆盖本地旧内容', () => {
    const existing = [message(2, { content: '本地' })]
    const incoming = [message(2, { content: '服务端' })]
    expect(mergeMessages(existing, incoming)[0]?.content).toBe('服务端')
  })
})

describe('detectGap', () => {
  it('seq 连续推进时无缺口', () => {
    expect(detectGap(0, [message(1), message(2), message(3)])).toEqual({ lastSeq: 3, hasGap: false })
  })

  it('从已有 lastSeq 继续推进', () => {
    expect(detectGap(3, [message(4), message(5)])).toEqual({ lastSeq: 5, hasGap: false })
  })

  it('乱序输入先排序再判断', () => {
    expect(detectGap(0, [message(3), message(1), message(2)])).toEqual({ lastSeq: 3, hasGap: false })
  })

  it('发现缺口时返回缺口起始 seq', () => {
    expect(detectGap(0, [message(1), message(3)])).toEqual({ lastSeq: 1, hasGap: true, missingFrom: 2 })
  })

  it('忽略已接收过的旧消息与重复消息', () => {
    expect(detectGap(5, [message(3), message(4), message(6)])).toEqual({ lastSeq: 6, hasGap: false })
  })

  it('负数 lastSeq 归一为 0', () => {
    expect(detectGap(-3, [message(1)])).toEqual({ lastSeq: 1, hasGap: false })
  })

  it('空消息列表保持 lastSeq 不变', () => {
    expect(detectGap(7, [])).toEqual({ lastSeq: 7, hasGap: false })
  })
})

describe('buildSyncCursors', () => {
  it('按 Map 迭代顺序生成 cursors 并遵守 limit', () => {
    const map = new Map([
      ['conv-1', 0],
      ['conv-2', 12],
      ['conv-3', 3],
    ])
    expect(buildSyncCursors(map, 2)).toEqual([
      { conversationId: 'conv-1', lastSeq: 0 },
      { conversationId: 'conv-2', lastSeq: 12 },
    ])
  })

  it('跳过非法 conversationId / lastSeq', () => {
    const map = new Map<string, number>([
      ['', 0],
      ['conv-nan', Number.NaN],
      ['conv-negative', -1],
      ['conv-float', 1.5],
      ['conv-ok', 9],
    ])
    expect(buildSyncCursors(map, 100)).toEqual([{ conversationId: 'conv-ok', lastSeq: 9 }])
  })

  it('limit 限制在 1~100 之间', () => {
    const many = new Map(Array.from({ length: 150 }, (_, index) => [`conv-${index}`, index] as const))
    expect(buildSyncCursors(many, 0)).toHaveLength(1)
    expect(buildSyncCursors(many, 1000)).toHaveLength(100)
  })

  it('超过 100 个会话时截断，剩余游标可在下一轮继续发送（M3-00 N2）', () => {
    const all = new Map(
      Array.from({ length: 130 }, (_, index) => [`conv-${index}`, index] as const),
    )
    const first = buildSyncCursors(all, 100)
    expect(first).toHaveLength(100)
    expect(first[99]).toEqual({ conversationId: 'conv-99', lastSeq: 99 })
    const remaining = new Map([...all].slice(100))
    const second = buildSyncCursors(remaining, 100)
    expect(second).toHaveLength(30)
    expect(second[0]).toEqual({ conversationId: 'conv-100', lastSeq: 100 })
  })

  it('空 Map 返回空数组', () => {
    expect(buildSyncCursors(new Map())).toEqual([])
  })
})

describe('unreadCount', () => {
  it('未读 = 最新 seq − 已读游标', () => {
    expect(unreadCount(5, 2)).toBe(3)
  })

  it('已读游标大于最新 seq 时归零', () => {
    expect(unreadCount(2, 5)).toBe(0)
  })

  it('负数归一处理', () => {
    expect(unreadCount(-1, 0)).toBe(0)
    expect(unreadCount(5, -2)).toBe(5)
  })
})

describe('syncBatchLimit', () => {
  it('每会话每批上限为协议约定的 200 条', () => {
    expect(syncBatchLimit()).toBe(200)
  })
})
