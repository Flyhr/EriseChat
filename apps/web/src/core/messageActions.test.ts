import { describe, expect, it } from 'vitest'
import {
  MESSAGE_EDIT_WINDOW_MS,
  MESSAGE_RECALL_WINDOW_MS,
  buildMessageActions,
  canEdit,
  canRecall,
  isRecalled,
  recallPlaceholderText,
} from './messageActions'
import { makeMessage } from '../test/fixtures'

const SELF = 'u-self'
const OTHER = 'u-other'

describe('canRecall', () => {
  it('本人窗口内的文本消息可撤回', () => {
    const message = makeMessage()
    expect(canRecall(message, SELF, message.createdAt + 1_000)).toBe(true)
  })

  it('他人消息不可撤回', () => {
    const message = makeMessage({ senderId: OTHER })
    expect(canRecall(message, SELF, message.createdAt + 1_000)).toBe(false)
  })

  it('超过窗口不可撤回（含边界）', () => {
    const message = makeMessage()
    expect(canRecall(message, SELF, message.createdAt + MESSAGE_RECALL_WINDOW_MS)).toBe(true)
    expect(canRecall(message, SELF, message.createdAt + MESSAGE_RECALL_WINDOW_MS + 1)).toBe(false)
  })

  it('系统消息与已撤回消息不可撤回', () => {
    const system = makeMessage({ msgType: 'system', content: { event: 'group.created' } })
    expect(canRecall(system, SELF, system.createdAt + 1_000)).toBe(false)
    const recalled = makeMessage({ deletedAt: 1_000_100 })
    expect(canRecall(recalled, SELF, recalled.createdAt + 1_000)).toBe(false)
  })

  it('非法时间戳或空消息返回 false', () => {
    const message = makeMessage({ createdAt: Number.NaN })
    expect(canRecall(message, SELF, Date.now())).toBe(false)
    expect(canRecall(null, SELF, Date.now())).toBe(false)
  })
})

describe('canEdit', () => {
  it('本人窗口内的文本消息可编辑', () => {
    const message = makeMessage()
    expect(canEdit(message, SELF, message.createdAt + 1_000)).toBe(true)
  })

  it('图片/文件/系统消息不可编辑', () => {
    const image = makeMessage({
      msgType: 'image',
      content: { fileId: 'f1', name: 'a.png', size: 10, mime: 'image/png' },
    })
    expect(canEdit(image, SELF, image.createdAt + 1_000)).toBe(false)
    const system = makeMessage({ msgType: 'system', content: { event: 'x' } })
    expect(canEdit(system, SELF, system.createdAt + 1_000)).toBe(false)
  })

  it('超过编辑窗口或已撤回不可编辑', () => {
    const message = makeMessage()
    expect(canEdit(message, SELF, message.createdAt + MESSAGE_EDIT_WINDOW_MS)).toBe(true)
    expect(canEdit(message, SELF, message.createdAt + MESSAGE_EDIT_WINDOW_MS + 1)).toBe(false)
    expect(canEdit({ ...message, deletedAt: 1 }, SELF, message.createdAt + 1_000)).toBe(false)
  })

  it('支持自定义窗口（服务端可配置）', () => {
    const message = makeMessage()
    expect(canEdit(message, SELF, message.createdAt + 5_000, 1_000)).toBe(false)
    expect(canRecall(message, SELF, message.createdAt + 500, 1_000)).toBe(true)
  })
})

describe('recallPlaceholderText', () => {
  it('自己/单聊对方/群聊成员的占位文案', () => {
    const recalled = makeMessage({ deletedAt: 2 })
    expect(recallPlaceholderText(recalled, { selfUserId: SELF })).toBe('你撤回了一条消息')
    const other = makeMessage({ senderId: OTHER, deletedAt: 2 })
    expect(recallPlaceholderText(other, { selfUserId: SELF })).toBe('对方撤回了一条消息')
    expect(recallPlaceholderText(other, { selfUserId: SELF, isGroup: true, senderName: '鲍勃' })).toBe(
      '鲍勃 撤回了一条消息',
    )
    expect(recallPlaceholderText(other, { selfUserId: SELF, isGroup: true })).toBe('成员 撤回了一条消息')
  })

  it('未撤回返回空串；isRecalled 与 deletedAt 一致', () => {
    const message = makeMessage()
    expect(recallPlaceholderText(message, { selfUserId: SELF })).toBe('')
    expect(isRecalled(message)).toBe(false)
    expect(isRecalled({ deletedAt: 1 })).toBe(true)
    expect(isRecalled(null)).toBe(false)
  })
})

describe('buildMessageActions', () => {
  it('本人窗口内文本：回复 + 转发 + 收藏 + 撤回 + 编辑（撤回为 danger）', () => {
    const message = makeMessage()
    const actions = buildMessageActions(message, { selfUserId: SELF, now: message.createdAt + 1_000 })
    expect(actions.map((action) => action.id)).toEqual(['reply', 'forward', 'favorite', 'recall', 'edit'])
    expect(actions.find((action) => action.id === 'recall')?.danger).toBe(true)
  })

  it('已收藏时显示「取消收藏」', () => {
    const message = makeMessage()
    const actions = buildMessageActions(message, {
      selfUserId: SELF,
      now: message.createdAt + 1_000,
      favorited: true,
    })
    expect(actions.map((action) => action.id)).toContain('unfavorite')
    expect(actions.map((action) => action.id)).not.toContain('favorite')
    expect(actions.find((action) => action.id === 'unfavorite')?.label).toBe('取消收藏')
  })

  it('他人消息超过窗口：回复 + 转发 + 收藏', () => {
    const message = makeMessage({ senderId: OTHER })
    const actions = buildMessageActions(message, {
      selfUserId: SELF,
      now: message.createdAt + MESSAGE_EDIT_WINDOW_MS + 1,
    })
    expect(actions.map((action) => action.id)).toEqual(['reply', 'forward', 'favorite'])
  })

  it('图片/文件消息只提供收藏（不转发）', () => {
    const image = makeMessage({ msgType: 'image', content: { fileId: 'f', name: 'a.png', size: 1, mime: 'image/png' } })
    expect(buildMessageActions(image, { selfUserId: SELF }).map((action) => action.id)).toEqual(['reply', 'favorite'])
    const file = makeMessage({ msgType: 'file', content: { fileId: 'f', name: 'b.pdf', size: 1, mime: 'application/pdf' } })
    expect(buildMessageActions(file, { selfUserId: SELF }).map((action) => action.id)).toEqual(['reply', 'favorite'])
  })

  it('已撤回消息不提供转发/收藏/撤回/编辑', () => {
    const message = makeMessage({ deletedAt: 2 })
    const actions = buildMessageActions(message, { selfUserId: SELF, now: message.createdAt + 1_000 })
    expect(actions.map((action) => action.id)).toEqual(['reply'])
  })

  it('系统消息不提供转发/收藏', () => {
    const message = makeMessage({ msgType: 'system', content: { event: 'e' } })
    expect(buildMessageActions(message, { selfUserId: SELF }).map((action) => action.id)).toEqual(['reply'])
  })
})
