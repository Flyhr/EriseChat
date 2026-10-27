import { describe, expect, it } from 'vitest'
import type { Conversation, ConversationMember } from '../types/frames'
import {
  ANNOUNCEMENT_MAX_LENGTH,
  GROUP_TITLE_MAX_LENGTH,
  announcementCountOf,
  announcementIsEmpty,
  announcementPreview,
  canEditGroupProfile,
  normalizeAnnouncementInput,
  validateGroupTitle,
} from './groupProfile'

function member(userId: string, role: 'owner' | 'member'): ConversationMember {
  return {
    userId,
    displayName: userId,
    email: `${userId}@example.com`,
    role,
    joinedAt: 1,
  }
}

function conversation(type: 'direct' | 'group', members: ConversationMember[]): Conversation {
  return {
    conversationId: 'conv-1',
    type,
    title: '测试群',
    createdAt: 1,
    lastSeq: 0,
    lastReadSeq: 0,
    members,
    announcement: null,
  }
}

describe('canEditGroupProfile（F-14）', () => {
  it('群主可编辑', () => {
    const conv = conversation('group', [member('a', 'owner'), member('b', 'member')])
    expect(canEditGroupProfile(conv, 'a')).toBe(true)
  })

  it('普通成员不可编辑', () => {
    const conv = conversation('group', [member('a', 'owner'), member('b', 'member')])
    expect(canEditGroupProfile(conv, 'b')).toBe(false)
  })

  it('单聊不可编辑', () => {
    const conv = conversation('direct', [member('a', 'owner'), member('b', 'member')])
    expect(canEditGroupProfile(conv, 'a')).toBe(false)
  })

  it('非成员 / 空参数返回 false', () => {
    const conv = conversation('group', [member('a', 'owner')])
    expect(canEditGroupProfile(conv, 'c')).toBe(false)
    expect(canEditGroupProfile(conv, null)).toBe(false)
    expect(canEditGroupProfile(null, 'a')).toBe(false)
  })
})

describe('validateGroupTitle（F-14）', () => {
  it('空与纯空白返回错误', () => {
    expect(validateGroupTitle('')).toEqual({ ok: false, message: '群名称不能为空' })
    expect(validateGroupTitle('   ')).toEqual({ ok: false, message: '群名称不能为空' })
  })

  it('去首尾空格后返回合法值', () => {
    expect(validateGroupTitle('  产品组  ')).toEqual({ ok: true, value: '产品组' })
  })

  it('恰好 100 字符通过，101 字符报错', () => {
    const max = '名'.repeat(GROUP_TITLE_MAX_LENGTH)
    expect(validateGroupTitle(max)).toEqual({ ok: true, value: max })
    expect(validateGroupTitle('名'.repeat(GROUP_TITLE_MAX_LENGTH + 1))).toEqual({
      ok: false,
      message: `群名称最多 ${GROUP_TITLE_MAX_LENGTH} 个字符`,
    })
  })
})

describe('normalizeAnnouncementInput（F-14）', () => {
  it('CRLF / CR 统一为 LF 并去首尾空白', () => {
    expect(normalizeAnnouncementInput('  第一行\r\n第二行\r第三行  ')).toBe('第一行\n第二行\n第三行')
  })

  it('纯空白归一为空串（表示清空）', () => {
    expect(normalizeAnnouncementInput('  \r\n  ')).toBe('')
  })
})

describe('announcementCountOf（F-14）', () => {
  it('按 code point 计数，换行计入', () => {
    expect(announcementCountOf('ab\nc')).toBe(4)
    expect(announcementCountOf('😀好')).toBe(2)
  })

  it('null/undefined 视为 0', () => {
    expect(announcementCountOf(null)).toBe(0)
    expect(announcementCountOf(undefined)).toBe(0)
    expect(announcementCountOf('')).toBe(0)
  })

  it('与服务端上限常量一致', () => {
    expect(ANNOUNCEMENT_MAX_LENGTH).toBe(500)
  })
})

describe('announcementPreview（F-14）', () => {
  it('空公告返回空文本且不截断', () => {
    expect(announcementPreview(null)).toEqual({ text: '', truncated: false })
    expect(announcementPreview('   ')).toEqual({ text: '', truncated: false })
  })

  it('单行与恰好两行不截断', () => {
    expect(announcementPreview('一行公告')).toEqual({ text: '一行公告', truncated: false })
    expect(announcementPreview('第一行\n第二行')).toEqual({ text: '第一行\n第二行', truncated: false })
  })

  it('超过两行截断并追加省略号', () => {
    expect(announcementPreview('一\n二\n三\n四')).toEqual({
      text: '一\n二\n…',
      truncated: true,
    })
  })

  it('支持自定义行数（非法值回退 2 行）', () => {
    expect(announcementPreview('一\n二', 1)).toEqual({ text: '一\n…', truncated: true })
    expect(announcementPreview('一\n二\n三', 0)).toEqual({ text: '一\n二\n…', truncated: true })
    expect(announcementPreview('一\n二\n三', Number.NaN)).toEqual({ text: '一\n二\n…', truncated: true })
  })
})

describe('announcementIsEmpty（F-14）', () => {
  it('null/空白视为空，有内容不为空', () => {
    expect(announcementIsEmpty(null)).toBe(true)
    expect(announcementIsEmpty(undefined)).toBe(true)
    expect(announcementIsEmpty('  \n ')).toBe(true)
    expect(announcementIsEmpty('公告')).toBe(false)
  })
})
