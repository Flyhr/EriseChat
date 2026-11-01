// F-26：本地收藏的纯逻辑（快照生成、排序、过滤、幂等 ID）。
//
// 收藏是「本设备快照」：内容在收藏那一刻固化，原消息之后被撤回/编辑不会影响已收藏副本。
// 不落服务端、不做云端同步；存储层见 core/db.ts 的 favorites store 与 stores/favorites.ts。
import type { Conversation, FileContent, Message, MessageType } from '../types/frames'
import { isVoiceContent } from './voice'

/** 单账号本地收藏上限（超过后拒绝新增并提示）。 */
export const MAX_FAVORITES = 500

export interface FavoriteFileSnapshot {
  fileId: string
  name: string
  size: number
  mime: string
}

/** 收藏记录（IndexedDB favorites store 的 value，keyPath = favoriteId）。 */
export interface FavoriteRecord {
  /** `${ownerId}:${messageId}`：同一账号对同一消息只收藏一次（幂等）。 */
  favoriteId: string
  /** 收藏归属账号（按账号隔离，切换账号只做内存过滤）。 */
  ownerId: string
  messageId: string
  conversationId: string
  conversationTitle: string
  senderId: string
  senderName: string
  msgType: MessageType
  /** 文本消息的内容快照。 */
  text?: string
  /** 图片/文件消息的附件快照（不含 blob，打开时仍走鉴权下载）。 */
  file?: FavoriteFileSnapshot
  /** 语音消息的时长快照（F-28，毫秒）；非语音消息为 undefined。 */
  durationMs?: number
  /** 原消息在会话内的 seq，用于「打开原会话」定位。 */
  sourceSeq: number
  /** 原消息创建时间（UTC 毫秒）。 */
  createdAt: number
  /** 收藏时间（UTC 毫秒），列表按此倒序。 */
  favoritedAt: number
}

/** 收藏 ID：账号 + 消息 ID，保证重复点击不会产生两条记录。 */
export function makeFavoriteId(ownerId: string, messageId: string): string {
  return `${ownerId.trim()}:${messageId.trim()}`
}

/** 是否可收藏：文本/图片/文件/语音消息，且未撤回、非 system。 */
export function isFavoritable(message: Message | null | undefined): boolean {
  if (!message) return false
  if (
    message.msgType !== 'text' &&
    message.msgType !== 'image' &&
    message.msgType !== 'file' &&
    message.msgType !== 'voice'
  ) {
    return false
  }
  return typeof message.deletedAt !== 'number'
}

function fileSnapshotOf(message: Message): FavoriteFileSnapshot | undefined {
  const content = message.content
  if (typeof content === 'string') return undefined
  if (!('fileId' in content) || !('name' in content) || !('size' in content) || !('mime' in content)) {
    return undefined
  }
  const file: FileContent = content
  return { fileId: file.fileId, name: file.name, size: file.size, mime: file.mime }
}

function conversationTitleOf(conversation: Conversation | null | undefined): string {
  const title = conversation?.title?.trim()
  return title && title.length > 0 ? title : '会话'
}

/**
 * 生成一条收藏快照；不可收藏的消息返回 null。
 * senderName 由调用方按会话成员解析（群聊里他人消息可能不在 ConversationMember 列表，允许回退占位）。
 */
export function favoriteSnapshotOf(
  message: Message | null | undefined,
  conversation: Conversation | null | undefined,
  senderName: string,
  ownerId: string,
  now: number = Date.now(),
): FavoriteRecord | null {
  if (!message || !ownerId) return null
  if (!isFavoritable(message)) return null
  const isText = message.msgType === 'text'
  const text = isText && typeof message.content === 'string' ? message.content : undefined
  if (isText && (text === undefined || text.length === 0)) return null
  const isVoice = message.msgType === 'voice'
  const durationMs =
    isVoice && isVoiceContent(message.content) ? message.content.durationMs : undefined
  if (isVoice && durationMs === undefined) return null
  const file = isText || isVoice ? undefined : fileSnapshotOf(message)
  if (!isText && !isVoice && !file) return null
  const name = senderName.trim() || (message.senderId === ownerId ? '我' : '未知成员')
  return {
    favoriteId: makeFavoriteId(ownerId, message.messageId),
    ownerId,
    messageId: message.messageId,
    conversationId: message.conversationId,
    conversationTitle: conversationTitleOf(conversation),
    senderId: message.senderId,
    senderName: name,
    msgType: message.msgType,
    text,
    file,
    durationMs,
    sourceSeq: message.seq,
    createdAt: message.createdAt,
    favoritedAt: now,
  }
}

/** 按收藏时间倒序（同一时刻按 favoriteId 升序保证稳定），不修改入参。 */
export function sortFavorites(records: readonly FavoriteRecord[]): FavoriteRecord[] {
  return [...records].sort((a, b) => {
    if (b.favoritedAt !== a.favoritedAt) return b.favoritedAt - a.favoritedAt
    return a.favoriteId < b.favoriteId ? -1 : a.favoriteId > b.favoriteId ? 1 : 0
  })
}

/** 按账号过滤并按关键词匹配（文本/文件名/来源会话名，大小写不敏感）。 */
export function filterFavorites(
  records: readonly FavoriteRecord[],
  ownerId: string,
  query: string,
): FavoriteRecord[] {
  const keyword = query.trim().toLowerCase()
  return records.filter((record) => {
    if (record.ownerId !== ownerId) return false
    if (keyword.length === 0) return true
    const haystacks = [record.text ?? '', record.file?.name ?? '', record.conversationTitle, record.senderName]
    return haystacks.some((item) => item.toLowerCase().includes(keyword))
  })
}
