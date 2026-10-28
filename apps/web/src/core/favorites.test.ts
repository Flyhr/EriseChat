import { describe, expect, it } from 'vitest'
import {
  MAX_FAVORITES,
  favoriteSnapshotOf,
  filterFavorites,
  isFavoritable,
  makeFavoriteId,
  sortFavorites,
  type FavoriteRecord,
} from './favorites'
import type { Conversation, Message } from '../types/frames'

const SELF = 'u-self'
const OTHER = 'u-other'

function makeMessage(overrides: Partial<Message> = {}): Message {
  return {
    messageId: 'm1',
    conversationId: 'c1',
    senderId: SELF,
    seq: 7,
    msgType: 'text',
    content: 'hello world',
    createdAt: 1_000_000,
    ...overrides,
  }
}

function makeConversation(overrides: Partial<Conversation> = {}): Conversation {
  return {
    conversationId: 'c1',
    type: 'group',
    title: '产品群',
    createdAt: 900_000,
    lastSeq: 10,
    lastReadSeq: 0,
    members: [],
    ...overrides,
  }
}

function makeRecord(overrides: Partial<FavoriteRecord> = {}): FavoriteRecord {
  return {
    favoriteId: 'u-self:m1',
    ownerId: SELF,
    messageId: 'm1',
    conversationId: 'c1',
    conversationTitle: '产品群',
    senderId: OTHER,
    senderName: '张三',
    msgType: 'text',
    text: 'hello',
    sourceSeq: 1,
    createdAt: 1_000,
    favoritedAt: 2_000,
    ...overrides,
  }
}

describe('makeFavoriteId', () => {
  it('按账号与消息 ID 生成幂等 ID', () => {
    expect(makeFavoriteId(SELF, 'm1')).toBe(`${SELF}:m1`)
    expect(makeFavoriteId(SELF, 'm1')).toBe(makeFavoriteId(SELF, 'm1'))
  })

  it('不同账号或不同消息得到不同 ID', () => {
    expect(makeFavoriteId(SELF, 'm1')).not.toBe(makeFavoriteId(OTHER, 'm1'))
    expect(makeFavoriteId(SELF, 'm1')).not.toBe(makeFavoriteId(SELF, 'm2'))
  })

  it('去除首尾空白', () => {
    expect(makeFavoriteId(' u1 ', ' m1 ')).toBe('u1:m1')
  })
})

describe('isFavoritable', () => {
  it('文本/图片/文件消息可收藏', () => {
    expect(isFavoritable(makeMessage())).toBe(true)
    expect(isFavoritable(makeMessage({ msgType: 'image', content: { fileId: 'f1', name: 'a.png', size: 10, mime: 'image/png' } }))).toBe(true)
    expect(isFavoritable(makeMessage({ msgType: 'file', content: { fileId: 'f2', name: 'b.pdf', size: 20, mime: 'application/pdf' } }))).toBe(true)
  })

  it('系统消息不可收藏', () => {
    expect(isFavoritable(makeMessage({ msgType: 'system', content: { event: 'member.added', text: 'x' } }))).toBe(false)
  })

  it('已撤回消息不可收藏', () => {
    expect(isFavoritable(makeMessage({ deletedAt: 1_100_000 }))).toBe(false)
  })

  it('空消息不可收藏', () => {
    expect(isFavoritable(null)).toBe(false)
    expect(isFavoritable(undefined)).toBe(false)
  })
})

describe('favoriteSnapshotOf', () => {
  it('文本消息生成快照（含会话名、发送者、seq、时间）', () => {
    const message = makeMessage()
    const snapshot = favoriteSnapshotOf(message, makeConversation(), '张三', SELF, 5_000_000)
    expect(snapshot).toMatchObject({
      favoriteId: `${SELF}:m1`,
      ownerId: SELF,
      messageId: 'm1',
      conversationId: 'c1',
      conversationTitle: '产品群',
      senderId: SELF,
      senderName: '张三',
      msgType: 'text',
      text: 'hello world',
      sourceSeq: 7,
      createdAt: 1_000_000,
      favoritedAt: 5_000_000,
    })
    expect(snapshot?.file).toBeUndefined()
  })

  it('图片/文件消息生成附件快照（不含 blob）', () => {
    const image = favoriteSnapshotOf(
      makeMessage({ msgType: 'image', content: { fileId: 'f1', name: 'a.png', size: 10, mime: 'image/png' } }),
      makeConversation(),
      '',
      SELF,
    )
    expect(image?.file).toEqual({ fileId: 'f1', name: 'a.png', size: 10, mime: 'image/png' })
    expect(image?.text).toBeUndefined()

    const file = favoriteSnapshotOf(
      makeMessage({ msgType: 'file', content: { fileId: 'f2', name: 'b.pdf', size: 20, mime: 'application/pdf' } }),
      makeConversation(),
      '',
      SELF,
    )
    expect(file?.file?.name).toBe('b.pdf')
  })

  it('不可收藏消息返回 null', () => {
    expect(favoriteSnapshotOf(makeMessage({ deletedAt: 1 }), makeConversation(), '', SELF)).toBeNull()
    expect(favoriteSnapshotOf(makeMessage({ msgType: 'system', content: { event: 'e' } }), makeConversation(), '', SELF)).toBeNull()
    expect(favoriteSnapshotOf(makeMessage({ content: '' }), makeConversation(), '', SELF)).toBeNull()
  })

  it('空 ownerId 返回 null（未登录不生成收藏）', () => {
    expect(favoriteSnapshotOf(makeMessage(), makeConversation(), '张三', '')).toBeNull()
  })

  it('发送者昵称缺失时按自己/他人回退占位', () => {
    expect(favoriteSnapshotOf(makeMessage({ senderId: SELF }), makeConversation(), '  ', SELF)?.senderName).toBe('我')
    expect(favoriteSnapshotOf(makeMessage({ senderId: OTHER }), makeConversation(), '  ', SELF)?.senderName).toBe('未知成员')
  })

  it('会话标题缺失时回退「会话」', () => {
    expect(favoriteSnapshotOf(makeMessage(), makeConversation({ title: '  ' }), '张三', SELF)?.conversationTitle).toBe('会话')
  })

  it('重复收藏同一消息 generate 相同 favoriteId（幂等）', () => {
    const a = favoriteSnapshotOf(makeMessage(), makeConversation(), '张三', SELF, 1)
    const b = favoriteSnapshotOf(makeMessage(), makeConversation(), '张三', SELF, 2)
    expect(a?.favoriteId).toBe(b?.favoriteId)
  })
})

describe('sortFavorites', () => {
  it('按收藏时间倒序且不修改入参', () => {
    const first = makeRecord({ favoriteId: 'a', favoritedAt: 1 })
    const second = makeRecord({ favoriteId: 'b', favoritedAt: 3 })
    const third = makeRecord({ favoriteId: 'c', favoritedAt: 2 })
    const input = [first, second, third]
    const sorted = sortFavorites(input)
    expect(sorted.map((item) => item.favoriteId)).toEqual(['b', 'c', 'a'])
    expect(input.map((item) => item.favoriteId)).toEqual(['a', 'b', 'c'])
  })

  it('收藏时间相同时按 favoriteId 升序稳定', () => {
    const sorted = sortFavorites([
      makeRecord({ favoriteId: 'b', favoritedAt: 1 }),
      makeRecord({ favoriteId: 'a', favoritedAt: 1 }),
    ])
    expect(sorted.map((item) => item.favoriteId)).toEqual(['a', 'b'])
  })

  it('空数组返回空数组', () => {
    expect(sortFavorites([])).toEqual([])
  })
})

describe('filterFavorites', () => {
  it('按账号隔离', () => {
    const mine = makeRecord({ favoriteId: 'a' })
    const others = makeRecord({ favoriteId: 'b', ownerId: OTHER })
    expect(filterFavorites([mine, others], SELF, '').map((item) => item.favoriteId)).toEqual(['a'])
  })

  it('匹配文本、文件名与来源会话名（大小写不敏感）', () => {
    const text = makeRecord({ favoriteId: 'a', text: 'Hello World', conversationTitle: '产品群' })
    const file = makeRecord({
      favoriteId: 'b',
      conversationTitle: '文件群',
      msgType: 'file',
      text: undefined,
      file: { fileId: 'f', name: 'report.PDF', size: 1, mime: 'application/pdf' },
    })
    const records = [text, file]
    expect(filterFavorites(records, SELF, 'hello').map((item) => item.favoriteId)).toEqual(['a'])
    expect(filterFavorites(records, SELF, 'report').map((item) => item.favoriteId)).toEqual(['b'])
    expect(filterFavorites(records, SELF, '产品').map((item) => item.favoriteId)).toEqual(['a'])
  })

  it('空关键词返回该账号全部收藏', () => {
    const records = [makeRecord({ favoriteId: 'a' }), makeRecord({ favoriteId: 'b' })]
    expect(filterFavorites(records, SELF, '   ')).toHaveLength(2)
  })

  it('无命中返回空数组', () => {
    expect(filterFavorites([makeRecord()], SELF, '不存在的内容')).toEqual([])
  })
})

describe('MAX_FAVORITES', () => {
  it('上限为 500', () => {
    expect(MAX_FAVORITES).toBe(500)
  })
})
