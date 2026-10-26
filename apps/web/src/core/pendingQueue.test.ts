import { describe, expect, it } from 'vitest'
import type { FileContent, Message } from '../types/frames'
import type { PendingRecord } from './db'
import {
  fromPendingRecord,
  groupPendingByConversation,
  reconcileClientMsgIds,
  toPendingRecord,
} from './pendingQueue'

const textPending = {
  clientMsgId: 'client-1',
  conversationId: 'conv-1',
  senderId: 'user-1',
  msgType: 'text' as const,
  content: '你好',
  createdAt: 1_700_000_000_000,
  status: 'sending' as const,
  retries: 0,
}

const fileContent: FileContent = {
  fileId: 'file-1',
  name: '截图.png',
  size: 1024,
  mime: 'image/png',
}

const filePending = {
  ...textPending,
  clientMsgId: 'client-2',
  msgType: 'image' as const,
  content: fileContent,
}

function buildMessage(overrides: Partial<Message>): Message {
  return {
    messageId: 'msg-1',
    conversationId: 'conv-1',
    senderId: 'user-1',
    seq: 1,
    msgType: 'text',
    content: '你好',
    createdAt: 1_700_000_000_000,
    ...overrides,
  }
}

function buildRecord(overrides: Partial<PendingRecord>): PendingRecord {
  return { ...textPending, ...overrides }
}

describe('toPendingRecord', () => {
  it('文本待发消息字段原样复制', () => {
    expect(toPendingRecord(textPending)).toEqual(textPending)
  })

  it('文件消息的 content 生成新对象，避免写入响应式引用', () => {
    const record = toPendingRecord(filePending)
    expect(record).toEqual(filePending)
    expect(record.content).not.toBe(fileContent)
  })
})

describe('fromPendingRecord', () => {
  it('合法文本记录通过校验', () => {
    expect(fromPendingRecord(textPending)).toEqual(textPending)
  })

  it('合法文件记录通过校验并复制 content', () => {
    const record = fromPendingRecord(filePending)
    expect(record).toEqual(filePending)
    expect(record?.content).not.toBe(fileContent)
  })

  it('非对象/数组/字符串等脏数据返回 null', () => {
    expect(fromPendingRecord(null)).toBeNull()
    expect(fromPendingRecord(undefined)).toBeNull()
    expect(fromPendingRecord('pending')).toBeNull()
    expect(fromPendingRecord([textPending])).toBeNull()
  })

  it('必填字段缺失或为空返回 null', () => {
    expect(fromPendingRecord(buildRecord({ clientMsgId: '' }))).toBeNull()
    expect(fromPendingRecord(buildRecord({ conversationId: '' }))).toBeNull()
    expect(fromPendingRecord(buildRecord({ senderId: '' }))).toBeNull()
  })

  it('非法 msgType / status / createdAt / retries 返回 null', () => {
    expect(fromPendingRecord(buildRecord({ msgType: 'system' as never }))).toBeNull()
    expect(fromPendingRecord(buildRecord({ status: 'sent' as never }))).toBeNull()
    expect(fromPendingRecord(buildRecord({ createdAt: Number.NaN }))).toBeNull()
    expect(fromPendingRecord(buildRecord({ createdAt: 0 }))).toBeNull()
    expect(fromPendingRecord(buildRecord({ retries: -1 }))).toBeNull()
    expect(fromPendingRecord(buildRecord({ retries: 1.5 }))).toBeNull()
  })

  it('文件消息 content 不完整时返回 null', () => {
    expect(
      fromPendingRecord({ ...filePending, content: { fileId: 'file-1', name: 'x' } }),
    ).toBeNull()
    expect(
      fromPendingRecord({ ...filePending, content: { ...fileContent, size: 0 } }),
    ).toBeNull()
    expect(fromPendingRecord({ ...filePending, content: 'not-file-content' })).toBeNull()
  })
})

describe('groupPendingByConversation', () => {
  it('按会话分组且保持原顺序', () => {
    const records = [
      buildRecord({ clientMsgId: 'a', conversationId: 'conv-1' }),
      buildRecord({ clientMsgId: 'b', conversationId: 'conv-2' }),
      buildRecord({ clientMsgId: 'c', conversationId: 'conv-1' }),
    ]
    expect(groupPendingByConversation(records)).toEqual({
      'conv-1': [records[0], records[2]],
      'conv-2': [records[1]],
    })
  })

  it('空数组返回空对象', () => {
    expect(groupPendingByConversation([])).toEqual({})
  })
})

describe('reconcileClientMsgIds', () => {
  it('incoming 命中相同 clientMsgId 时返回该 id', () => {
    const incoming = [buildMessage({ messageId: 'msg-9', clientMsgId: 'client-1' })]
    expect([...reconcileClientMsgIds(incoming, [buildRecord({ clientMsgId: 'client-1' })])]).toEqual([
      'client-1',
    ])
  })

  it('未命中、无 clientMsgId 或空输入时返回空集合', () => {
    const pending = [buildRecord({ clientMsgId: 'client-1' })]
    expect(reconcileClientMsgIds([buildMessage({ messageId: 'msg-2' })], pending).size).toBe(0)
    expect(
      reconcileClientMsgIds([buildMessage({ messageId: 'msg-3', clientMsgId: 'other' })], pending)
        .size,
    ).toBe(0)
    expect(reconcileClientMsgIds([], pending).size).toBe(0)
    expect(
      reconcileClientMsgIds([buildMessage({ clientMsgId: 'client-1' })], []).size,
    ).toBe(0)
  })
})
