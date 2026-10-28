// 单测共享夹具（P2 清理）：createMemoryStorage / createFakeStorage / makeMessage / makeConversation
// 原先在多个 .test.ts 里各写一份。默认值取各测试套件的公共值，差异用 overrides 覆盖。

import type { Conversation, Message } from '../types/frames'

/** 内存版 localStorage（node 测试环境无 DOM，不引入 jsdom）。 */
export function createMemoryStorage(): Storage {
  const data = new Map<string, string>()
  return {
    get length() {
      return data.size
    },
    clear: () => data.clear(),
    getItem: (key: string) => data.get(key) ?? null,
    key: (index: number) => Array.from(data.keys())[index] ?? null,
    removeItem: (key: string) => {
      data.delete(key)
    },
    setItem: (key: string, value: string) => {
      data.set(key, value)
    },
  }
}

/** 可注入存储的假实现：可预置数据，也可模拟读写抛错（隐私模式/配额满）。 */
export interface FakeStorage {
  data: Map<string, string>
  getItem(key: string): string | null
  setItem(key: string, value: string): void
  removeItem(key: string): void
}

export function createFakeStorage(
  initial: Record<string, string> = {},
  options: { failRead?: boolean; failWrite?: boolean } = {},
): FakeStorage {
  const data = new Map<string, string>(Object.entries(initial))
  return {
    data,
    getItem(key: string): string | null {
      if (options.failRead) throw new Error('storage unavailable')
      return data.get(key) ?? null
    },
    setItem(key: string, value: string): void {
      if (options.failWrite) throw new Error('storage unavailable')
      data.set(key, value)
    },
    removeItem(key: string): void {
      data.delete(key)
    },
  }
}

/** 消息夹具（字段与 Message 契约一致，按需 overrides）。 */
export function makeMessage(overrides: Partial<Message> = {}): Message {
  return {
    messageId: 'm1',
    conversationId: 'c1',
    senderId: 'u-self',
    seq: 1,
    msgType: 'text',
    content: 'hello',
    createdAt: 1_000,
    ...overrides,
  }
}

/** 会话夹具（单聊默认；群聊用 overrides 覆盖 type/title）。 */
export function makeConversation(overrides: Partial<Conversation> = {}): Conversation {
  return {
    conversationId: 'c1',
    type: 'direct',
    title: '张三',
    createdAt: 1_000,
    lastSeq: 1,
    lastReadSeq: 0,
    members: [],
    ...overrides,
  }
}
