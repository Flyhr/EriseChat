// IndexedDB 缓存（只缓存，不作事实源；project-overview.md §9、AGENTS.md §2.8）。
// M0 只提供最小可用封装；M1 按需补消息分页、会话快照与清理策略。

import { config } from './config'
import type { Conversation, FileContent, Message, MessageSendType } from '../types/frames'

export const STORE_MESSAGES = 'messages'
export const STORE_CONVERSATIONS = 'conversations'
export const STORE_META = 'meta'
/** F-03：待发消息持久化（只缓存未收到 ack 的乐观消息，刷新后可恢复并重发）。 */
export const STORE_PENDING = 'pending'

export interface MetaRecord {
  key: string
  value: unknown
}

/** 待发消息状态：与 messages store 的 PendingStatus 保持一致（此处独立声明避免反向依赖）。 */
export type PendingRecordStatus = 'sending' | 'failed'

/** 待发消息的 IndexedDB 记录（与 stores/messages.ts 的 PendingMessage 同构）。 */
export interface PendingRecord {
  clientMsgId: string
  conversationId: string
  senderId: string
  /** text / image / file（system 仅服务端生成）。 */
  msgType: MessageSendType
  /** 文本为字符串；图片/文件为 FileContent。 */
  content: string | FileContent
  createdAt: number
  status: PendingRecordStatus
  retries: number
  /** F-09：回复引用目标 messageId（可选；恢复后重发仍带引用）。 */
  replyTo?: string
}

let dbPromise: Promise<IDBDatabase> | null = null

/** 打开（首次创建）本地缓存库；schema 变更时递增版本号并写 onupgradeneeded。 */
export function openDb(): Promise<IDBDatabase> {
  if (dbPromise) return dbPromise
  dbPromise = new Promise<IDBDatabase>((resolve, reject) => {
    const request = indexedDB.open(config.dbName, 2)

    request.onupgradeneeded = () => {
      const db = request.result
      if (!db.objectStoreNames.contains(STORE_MESSAGES)) {
        const store = db.createObjectStore(STORE_MESSAGES, { keyPath: 'messageId' })
        store.createIndex('byConversation', 'conversationId')
        store.createIndex('byConversationSeq', ['conversationId', 'seq'])
      }
      if (!db.objectStoreNames.contains(STORE_CONVERSATIONS)) {
        db.createObjectStore(STORE_CONVERSATIONS, { keyPath: 'conversationId' })
      }
      if (!db.objectStoreNames.contains(STORE_META)) {
        db.createObjectStore(STORE_META, { keyPath: 'key' })
      }
      // F-03：v1 → v2 只新增 pending store，不改动已有 store（向后兼容老缓存）。
      if (!db.objectStoreNames.contains(STORE_PENDING)) {
        const store = db.createObjectStore(STORE_PENDING, { keyPath: 'clientMsgId' })
        store.createIndex('byConversation', 'conversationId')
      }
    }

    request.onsuccess = () => resolve(request.result)
    request.onerror = () => reject(request.error ?? new Error('IndexedDB 打开失败'))
  })
  return dbPromise
}

function runRequest<T>(
  storeName: string,
  mode: IDBTransactionMode,
  fn: (store: IDBObjectStore) => IDBRequest,
): Promise<T> {
  return openDb().then(
    (db) =>
      new Promise<T>((resolve, reject) => {
        const transaction = db.transaction(storeName, mode)
        const request = fn(transaction.objectStore(storeName))
        request.onsuccess = () => resolve(request.result as T)
        request.onerror = () => reject(request.error ?? new Error(`IndexedDB 操作失败：${storeName}`))
      }),
  )
}

export function putRecord<T>(storeName: string, value: T): Promise<void> {
  return runRequest<void>(storeName, 'readwrite', (store) => store.put(value))
}

export function getRecord<T>(storeName: string, key: IDBValidKey): Promise<T | undefined> {
  return runRequest<T | undefined>(storeName, 'readonly', (store) => store.get(key))
}

export function deleteRecord(storeName: string, key: IDBValidKey): Promise<void> {
  return runRequest<void>(storeName, 'readwrite', (store) => store.delete(key))
}

export function getAllRecords<T>(storeName: string): Promise<T[]> {
  return runRequest<T[]>(storeName, 'readonly', (store) => store.getAll())
}

// ===== 业务封装（缓存层，M2 起由 messages store 接入） =====

export function saveMessage(message: Message): Promise<void> {
  return putRecord(STORE_MESSAGES, message)
}

export function saveMessages(messages: readonly Message[]): Promise<void> {
  return openDb().then(
    (db) =>
      new Promise<void>((resolve, reject) => {
        const transaction = db.transaction(STORE_MESSAGES, 'readwrite')
        const store = transaction.objectStore(STORE_MESSAGES)
        for (const message of messages) store.put(message)
        transaction.oncomplete = () => resolve()
        transaction.onerror = () => reject(transaction.error ?? new Error('IndexedDB 写入失败'))
      }),
  )
}

export function getMessagesByConversation(conversationId: string): Promise<Message[]> {
  return openDb().then(
    (db) =>
      new Promise<Message[]>((resolve, reject) => {
        const request = db
          .transaction(STORE_MESSAGES, 'readonly')
          .objectStore(STORE_MESSAGES)
          .index('byConversation')
          .getAll(IDBKeyRange.only(conversationId))
        request.onsuccess = () => resolve(request.result as Message[])
        request.onerror = () => reject(request.error ?? new Error('IndexedDB 查询失败'))
      }),
  )
}

export function saveConversation(conversation: Conversation): Promise<void> {
  return putRecord(STORE_CONVERSATIONS, conversation)
}

export function getConversations(): Promise<Conversation[]> {
  return getAllRecords<Conversation>(STORE_CONVERSATIONS)
}

export async function getMeta<T>(key: string): Promise<T | undefined> {
  const record = await getRecord<MetaRecord>(STORE_META, key)
  return record?.value as T | undefined
}

export function setMeta(key: string, value: unknown): Promise<void> {
  return putRecord<MetaRecord>(STORE_META, { key, value })
}

// ===== F-03 待发消息持久化（刷新/重启后恢复「发送中/失败」的乐观消息） =====

export function savePending(record: PendingRecord): Promise<void> {
  return putRecord(STORE_PENDING, record)
}

export function deletePending(clientMsgId: string): Promise<void> {
  return deleteRecord(STORE_PENDING, clientMsgId)
}

export function getAllPending(): Promise<PendingRecord[]> {
  return getAllRecords<PendingRecord>(STORE_PENDING)
}

/** 清空待发消息（登出/切账号时调用，避免把上一个账号的发送中消息带到下一个账号）。 */
export async function clearPending(): Promise<void> {
  try {
    const db = await openDb()
    await new Promise<void>((resolve, reject) => {
      const transaction = db.transaction(STORE_PENDING, 'readwrite')
      transaction.objectStore(STORE_PENDING).clear()
      transaction.oncomplete = () => resolve()
      transaction.onerror = () => reject(transaction.error ?? new Error('IndexedDB 清理失败'))
    })
  } catch (error) {
    console.warn('[db] 清理待发消息缓存失败', error)
  }
}

/**
 * 清空全部本地缓存（M3-00：退出登录/账号切换时调用）。
 * 缓存只是加速渲染，清空不影响服务端事实源；下次登录会重新拉取。
 */
export async function clearAllData(): Promise<void> {
  try {
    const db = await openDb()
    const storeNames = [STORE_MESSAGES, STORE_CONVERSATIONS, STORE_META, STORE_PENDING]
    await new Promise<void>((resolve, reject) => {
      const transaction = db.transaction(storeNames, 'readwrite')
      for (const storeName of storeNames) transaction.objectStore(storeName).clear()
      transaction.oncomplete = () => resolve()
      transaction.onerror = () => reject(transaction.error ?? new Error('IndexedDB 清理失败'))
    })
  } catch (error) {
    console.warn('[db] 清理本地缓存失败', error)
  }
}
