import { describe, expect, it } from 'vitest'
import type { Conversation } from '../types/frames'
import { isConversationPinned, lastActivityAt, sortConversations } from './conversationSort'

function makeConversation(
  conversationId: string,
  options: { pinned?: boolean; createdAt?: number; lastMessageAt?: number | null } = {},
): Conversation {
  const createdAt = options.createdAt ?? 1_000
  const lastMessage =
    options.lastMessageAt === null || options.lastMessageAt === undefined
      ? null
      : { createdAt: options.lastMessageAt }
  return {
    conversationId,
    type: 'direct',
    title: '',
    createdAt,
    lastSeq: 0,
    lastReadSeq: 0,
    pinned: options.pinned,
    lastMessage: lastMessage as Conversation['lastMessage'],
    members: [],
  }
}

describe('isConversationPinned', () => {
  it('只有 pinned === true 才视为置顶', () => {
    expect(isConversationPinned({ pinned: true })).toBe(true)
    expect(isConversationPinned({ pinned: false })).toBe(false)
    expect(isConversationPinned({})).toBe(false)
  })
})

describe('lastActivityAt', () => {
  it('有最近消息时用消息时间，否则用创建时间', () => {
    expect(lastActivityAt(makeConversation('a', { createdAt: 10, lastMessageAt: 99 }))).toBe(99)
    expect(lastActivityAt(makeConversation('a', { createdAt: 10, lastMessageAt: null }))).toBe(10)
  })

  it('非法时间按 0 兜底', () => {
    const conversation = makeConversation('a', { createdAt: Number.NaN })
    expect(lastActivityAt({ ...conversation, createdAt: Number.NaN })).toBe(0)
  })
})

describe('sortConversations', () => {
  it('置顶会话排在未置顶之前（即使时间更早）', () => {
    const oldPinned = makeConversation('pinned-old', { pinned: true, lastMessageAt: 10 })
    const newNormal = makeConversation('normal-new', { lastMessageAt: 100 })
    expect(sortConversations([newNormal, oldPinned]).map((item) => item.conversationId)).toEqual([
      'pinned-old',
      'normal-new',
    ])
  })

  it('置顶组内与未置顶组内都按最近消息时间倒序', () => {
    const pinnedNew = makeConversation('p-new', { pinned: true, lastMessageAt: 300 })
    const pinnedOld = makeConversation('p-old', { pinned: true, lastMessageAt: 200 })
    const normalNew = makeConversation('n-new', { lastMessageAt: 100 })
    const normalOld = makeConversation('n-old', { lastMessageAt: 50 })
    const sorted = sortConversations([normalOld, pinnedOld, normalNew, pinnedNew])
    expect(sorted.map((item) => item.conversationId)).toEqual(['p-new', 'p-old', 'n-new', 'n-old'])
  })

  it('无消息的会话按创建时间参与排序', () => {
    const createdLate = makeConversation('created-late', { createdAt: 500, lastMessageAt: null })
    const createdEarly = makeConversation('created-early', { createdAt: 100, lastMessageAt: null })
    const withMessage = makeConversation('with-message', { lastMessageAt: 300 })
    expect(
      sortConversations([createdEarly, withMessage, createdLate]).map((item) => item.conversationId),
    ).toEqual(['created-late', 'with-message', 'created-early'])
  })

  it('时间相同按 conversationId 升序稳定排序', () => {
    const b = makeConversation('b', { lastMessageAt: 100 })
    const a = makeConversation('a', { lastMessageAt: 100 })
    const c = makeConversation('c', { lastMessageAt: 100 })
    expect(sortConversations([c, b, a]).map((item) => item.conversationId)).toEqual(['a', 'b', 'c'])
  })

  it('不修改入参数组与元素顺序', () => {
    const first = makeConversation('first', { lastMessageAt: 10 })
    const second = makeConversation('second', { lastMessageAt: 20 })
    const input = [first, second]
    const sorted = sortConversations(input)
    expect(input.map((item) => item.conversationId)).toEqual(['first', 'second'])
    expect(sorted).not.toBe(input)
    expect(sorted.map((item) => item.conversationId)).toEqual(['second', 'first'])
  })

  it('空数组返回空数组', () => {
    expect(sortConversations([])).toEqual([])
  })

  it('全部置顶时仍按时间倒序', () => {
    const early = makeConversation('early', { pinned: true, lastMessageAt: 10 })
    const late = makeConversation('late', { pinned: true, lastMessageAt: 20 })
    expect(sortConversations([early, late]).map((item) => item.conversationId)).toEqual([
      'late',
      'early',
    ])
  })
})
