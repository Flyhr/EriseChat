import { describe, expect, it } from 'vitest'
import { canForward, filterForwardTargets, forwardTextOf } from './forward'
import { makeConversation, makeMessage } from '../test/fixtures'

describe('canForward', () => {
  it('未撤回的文本消息可转发', () => {
    expect(canForward(makeMessage())).toBe(true)
  })

  it('空文本不可转发', () => {
    expect(canForward(makeMessage({ content: '' }))).toBe(false)
  })

  it('图片/文件/系统消息不可转发', () => {
    const image = makeMessage({ msgType: 'image', content: { fileId: 'f', name: 'a.png', size: 1, mime: 'image/png' } })
    const file = makeMessage({ msgType: 'file', content: { fileId: 'f', name: 'b.pdf', size: 1, mime: 'application/pdf' } })
    const system = makeMessage({ msgType: 'system', content: { event: 'e' } })
    expect(canForward(image)).toBe(false)
    expect(canForward(file)).toBe(false)
    expect(canForward(system)).toBe(false)
  })

  it('已撤回文本不可转发', () => {
    expect(canForward(makeMessage({ deletedAt: 2_000 }))).toBe(false)
  })

  it('空消息不可转发', () => {
    expect(canForward(null)).toBe(false)
    expect(canForward(undefined)).toBe(false)
  })
})

describe('forwardTextOf', () => {
  it('返回可转发消息的文本', () => {
    expect(forwardTextOf(makeMessage({ content: '转发我' }))).toBe('转发我')
  })

  it('不可转发消息返回空串', () => {
    expect(forwardTextOf(makeMessage({ deletedAt: 1 }))).toBe('')
    expect(forwardTextOf(null)).toBe('')
  })
})

describe('filterForwardTargets', () => {
  const current = makeConversation({ conversationId: 'c-current', title: '当前会话' })
  const direct = makeConversation({
    conversationId: 'c-direct',
    title: '张三',
    lastMessage: makeMessage({ messageId: 'x', conversationId: 'c-direct', createdAt: 3_000 }),
  })
  const group = makeConversation({
    conversationId: 'c-group',
    title: '产品群',
    type: 'group',
    lastMessage: makeMessage({ messageId: 'y', conversationId: 'c-group', createdAt: 2_000 }),
  })
  const pinned = makeConversation({
    conversationId: 'c-pinned',
    title: '重要通知',
    pinned: true,
    lastMessage: makeMessage({ messageId: 'z', conversationId: 'c-pinned', createdAt: 1_000 }),
  })

  it('排除当前会话', () => {
    const targets = filterForwardTargets([current, direct, group], 'c-current', '')
    expect(targets.map((item) => item.conversationId)).toEqual(['c-direct', 'c-group'])
  })

  it('置顶优先，其次按最近活跃倒序', () => {
    const targets = filterForwardTargets([direct, group, pinned], 'c-current', '')
    expect(targets.map((item) => item.conversationId)).toEqual(['c-pinned', 'c-direct', 'c-group'])
  })

  it('按标题过滤（大小写不敏感）', () => {
    const english = makeConversation({ conversationId: 'c-en', title: 'Project Alpha' })
    const targets = filterForwardTargets([direct, english], 'c-current', 'alpha')
    expect(targets.map((item) => item.conversationId)).toEqual(['c-en'])
  })

  it('无匹配返回空数组', () => {
    expect(filterForwardTargets([direct, group], 'c-current', '不存在')).toEqual([])
  })

  it('没有 lastMessage 时按会话创建时间排序', () => {
    const older = makeConversation({ conversationId: 'c-old', createdAt: 100 })
    const newer = makeConversation({ conversationId: 'c-new', createdAt: 200 })
    const targets = filterForwardTargets([older, newer], 'c-current', '')
    expect(targets.map((item) => item.conversationId)).toEqual(['c-new', 'c-old'])
  })

  it('活跃时间相同时按会话 ID 升序稳定', () => {
    const a = makeConversation({ conversationId: 'c-a', createdAt: 5_000 })
    const b = makeConversation({ conversationId: 'c-b', createdAt: 5_000 })
    const targets = filterForwardTargets([b, a], 'c-current', '')
    expect(targets.map((item) => item.conversationId)).toEqual(['c-a', 'c-b'])
  })
})
