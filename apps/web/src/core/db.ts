// IndexedDB 缓存（只缓存，不作事实源；project-overview.md §9、AGENTS.md §2.8）。
// M0 只提供最小可用封装；M1 按需补消息分页、会话快照与清理策略。

import { config } from './config'
import type { Conversation, Message } from '../types/frames'

export const STORE_MESSAGES = 'messages'
export const STORE_CONVERSATIONS = 'conversations'
export const STORE_META = 'meta'

export interface MetaRecord {
  key: string
  value: unknown
}

let dbPromise: Promise<IDBDatabase> | null = null

/** 打开（首次创建）本地缓存库；schema 变更时递增版本号并写 onupgradeneeded。 */
export function openDb(): Promise<IDBDatabase> {
  if (dbPromise) return dbPromise
  dbPromise = new Promise<IDBDatabase>((resolve, reject) => {
    const request = indexedDB.open(config.dbName, 1)

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

// ===== 业务封装（缓存层，M1 接入 store 时使用） =====

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
