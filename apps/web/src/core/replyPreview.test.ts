import { describe, expect, it } from 'vitest'
import type { MessageReply } from '../types/frames'
import {
  REPLY_PREVIEW_MAX_LENGTH,
  pendingReplyPreview,
  replyCardText,
  replySummaryOf,
  truncateReplyPreview,
} from './replyPreview'

/** 造一个 Reply 摘要；默认文本回复。 */
function reply(overrides: Partial<MessageReply> = {}): MessageReply {
  return {
    messageId: 'm-1',
    seq: 3,
    senderId: 'u-1',
    msgType: 'text',
    text: '原始消息',
    ...overrides,
  }
}

describe('truncateReplyPreview', () => {
  it('短文本原样返回', () => {
    expect(truncateReplyPreview('你好')).toBe('你好')
  })

  it('等于上限时原样返回，超过上限追加省略号', () => {
    const exact = 'a'.repeat(REPLY_PREVIEW_MAX_LENGTH)
    expect(truncateReplyPreview(exact)).toBe(exact)
    const longer = 'a'.repeat(REPLY_PREVIEW_MAX_LENGTH + 5)
    expect(truncateReplyPreview(longer)).toBe('a'.repeat(REPLY_PREVIEW_MAX_LENGTH) + '…')
  })

  it('上限非法时回退到默认上限且不拆代理对', () => {
    const emoji = '😀'.repeat(REPLY_PREVIEW_MAX_LENGTH + 2)
    const truncated = truncateReplyPreview(emoji)
    expect(Array.from(truncated)).toHaveLength(REPLY_PREVIEW_MAX_LENGTH + 1)
    expect(truncated.endsWith('…')).toBe(true)
  })
})

describe('replySummaryOf', () => {
  it('空值与文本回复', () => {
    expect(replySummaryOf(null)).toBe('')
    expect(replySummaryOf(undefined)).toBe('')
    expect(replySummaryOf(reply())).toBe('原始消息')
  })

  it('文本为空白时回退 [消息]', () => {
    expect(replySummaryOf(reply({ text: '   ' }))).toBe('[消息]')
    expect(replySummaryOf(reply({ text: undefined }))).toBe('[消息]')
  })

  it('图片与文件显示名称', () => {
    expect(replySummaryOf(reply({ msgType: 'image', text: undefined, name: '照片.png' }))).toBe(
      '[图片] 照片.png',
    )
    expect(replySummaryOf(reply({ msgType: 'file', text: undefined, name: '报告.pdf' }))).toBe(
      '[文件] 报告.pdf',
    )
    expect(replySummaryOf(reply({ msgType: 'image', text: undefined, name: undefined }))).toBe('[图片]')
  })

  it('系统消息取系统文案，缺失时回退', () => {
    expect(replySummaryOf(reply({ msgType: 'system', text: 'ALICE 创建了群聊' }))).toBe(
      'ALICE 创建了群聊',
    )
    expect(replySummaryOf(reply({ msgType: 'system', text: undefined }))).toBe('[系统消息]')
  })

  it('超长文本按 60 字符截断', () => {
    const long = 'b'.repeat(REPLY_PREVIEW_MAX_LENGTH + 10)
    expect(replySummaryOf(reply({ text: long }))).toBe('b'.repeat(REPLY_PREVIEW_MAX_LENGTH) + '…')
  })
})

describe('pendingReplyPreview', () => {
  it('本地待发文本与文件内容', () => {
    expect(pendingReplyPreview(null)).toBe('')
    expect(pendingReplyPreview({ msgType: 'text', text: '待发原文' })).toBe('待发原文')
    expect(pendingReplyPreview({ msgType: 'file', name: '说明.txt' })).toBe('[文件] 说明.txt')
  })
})

describe('replyCardText', () => {
  it('有昵称时拼接，无昵称时只显示摘要', () => {
    expect(replyCardText(reply(), 'ALICE')).toBe('ALICE：原始消息')
    expect(replyCardText(reply(), '  ')).toBe('原始消息')
    expect(replyCardText(null)).toBe('')
  })

  it('兼容本地待发摘要', () => {
    expect(replyCardText({ msgType: 'image', name: '截图.png' }, 'BOB')).toBe('BOB：[图片] 截图.png')
  })
})
