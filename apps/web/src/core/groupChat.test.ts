import { describe, expect, it } from 'vitest'
import {
  canTransferOwner,
  conversationLabel,
  memberCountLabel,
  sortMembers,
  systemMessageText,
} from './groupChat'
import type { ConversationMember } from '../types/frames'

function member(
  userId: string,
  displayName: string,
  role: 'owner' | 'member',
): ConversationMember {
  return {
    userId,
    displayName,
    email: `${userId}@example.com`,
    role,
    joinedAt: 1_730_000_000_000,
  }
}

describe('systemMessageText', () => {
  it('优先使用服务端 content.text', () => {
    expect(
      systemMessageText({
        event: 'member.joined',
        actorId: 'u1',
        targetUserIds: ['u2'],
        text: '小明 邀请 小红 加入群聊',
      }),
    ).toBe('小明 邀请 小红 加入群聊')
  })

  it('缺 text 时按 group.created 兜底', () => {
    expect(systemMessageText({ event: 'group.created' })).toBe('群聊已创建')
  })

  it('缺 text 时按 member.joined 兜底', () => {
    expect(systemMessageText({ event: 'member.joined' })).toBe('有新成员加入群聊')
  })

  it('缺 text 时按 member.left 兜底', () => {
    expect(systemMessageText({ event: 'member.left' })).toBe('有成员退出群聊')
  })

  it('缺 text 时按 member.removed 兜底', () => {
    expect(systemMessageText({ event: 'member.removed' })).toBe('有成员被移出群聊')
  })

  it('缺 text 时按 owner.transferred 兜底（F-24）', () => {
    expect(systemMessageText({ event: 'owner.transferred' })).toBe('群主已转让')
  })

  it('未知事件回退「系统消息」', () => {
    expect(systemMessageText({ event: 'future.event' })).toBe('系统消息')
  })

  it('字符串 content 原样返回', () => {
    expect(systemMessageText('纯文本系统消息')).toBe('纯文本系统消息')
  })

  it('null/undefined 回退「系统消息」', () => {
    expect(systemMessageText(null)).toBe('系统消息')
    expect(systemMessageText(undefined)).toBe('系统消息')
  })

  it('无 text/event 的对象回退「系统消息」', () => {
    expect(systemMessageText({ fileId: 'f1', name: 'a.png', size: 1, mime: 'image/png' })).toBe(
      '系统消息',
    )
  })
})

describe('sortMembers', () => {
  it('群主排最前，其余按昵称 zh-CN 排序', () => {
    const sorted = sortMembers([
      member('u3', '阿强', 'member'),
      member('u1', '小明', 'owner'),
      member('u2', '小红', 'member'),
    ])
    expect(sorted.map((item) => item.userId)).toEqual(['u1', 'u3', 'u2'])
  })

  it('昵称相同时按 userId 排序（稳定）', () => {
    const sorted = sortMembers([
      member('u2', '同名', 'member'),
      member('u1', '同名', 'member'),
    ])
    expect(sorted.map((item) => item.userId)).toEqual(['u1', 'u2'])
  })

  it('不修改入参数组', () => {
    const input = [member('u2', '乙', 'member'), member('u1', '甲', 'owner')]
    const snapshot = input.map((item) => item.userId)
    sortMembers(input)
    expect(input.map((item) => item.userId)).toEqual(snapshot)
  })
})

describe('memberCountLabel', () => {
  it('正常数量', () => {
    expect(memberCountLabel(3)).toBe('3 人')
  })

  it('0 与负数/非法值按 0 处理', () => {
    expect(memberCountLabel(0)).toBe('0 人')
    expect(memberCountLabel(-2)).toBe('0 人')
    expect(memberCountLabel(Number.NaN)).toBe('0 人')
  })
})

describe('conversationLabel', () => {
  const groupMembers = [member('u1', '小明', 'owner'), member('u2', '小红', 'member')]

  it('群聊显示群名与成员数', () => {
    expect(
      conversationLabel({ type: 'group', title: '研发群', members: groupMembers }),
    ).toBe('研发群（2）')
  })

  it('群名为空时回退「群聊」并带成员数', () => {
    expect(conversationLabel({ type: 'group', title: '  ', members: groupMembers })).toBe(
      '群聊（2）',
    )
  })

  it('单聊不加成员数', () => {
    expect(
      conversationLabel({ type: 'direct', title: '小明', members: groupMembers }),
    ).toBe('小明')
  })

  it('单聊无标题时回退「单聊会话」', () => {
    expect(conversationLabel({ type: 'direct', title: '', members: groupMembers })).toBe(
      '单聊会话',
    )
  })
})

describe('canTransferOwner', () => {
  const groupMembers = [member('u1', '甲', 'owner'), member('u2', '乙', 'member')]
  const group = { type: 'group' as const, members: groupMembers }

  it('群主可转让给本群其他成员', () => {
    expect(canTransferOwner(group, 'u1', 'u2')).toBe(true)
  })

  it('非群主不可转让', () => {
    expect(canTransferOwner(group, 'u2', 'u1')).toBe(false)
  })

  it('目标是自己不可转让', () => {
    expect(canTransferOwner(group, 'u1', 'u1')).toBe(false)
  })

  it('目标不在本群不可转让', () => {
    expect(canTransferOwner(group, 'u1', 'u3')).toBe(false)
  })

  it('单聊不可转让', () => {
    expect(canTransferOwner({ type: 'direct', members: groupMembers }, 'u1', 'u2')).toBe(false)
  })

  it('自身 ID 为空不可转让', () => {
    expect(canTransferOwner(group, '', 'u2')).toBe(false)
  })
})
