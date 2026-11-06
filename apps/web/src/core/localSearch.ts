// 会话内本地搜索（F-19 批次1，计划 §F-19）：在「当前会话已加载消息」上做纯内存过滤，
// 作为搜索面板的即时结果层（0 延迟、离线可用），不写库、不改 messages store 的事实数据。
// 搜索对象：text 消息的 content 字符串、file 消息的文件名；排除已撤回/图片/系统消息。
// 大小写不敏感（toLowerCase + includes，不用正则，避免关键词含元字符报错）。
import type { Message } from '../types/frames'

/** 本地结果默认上限（与服务端搜索单页量级一致）。 */
export const LOCAL_SEARCH_LIMIT = 100

/** 取出消息参与搜索的文本；不可搜索（已撤回/图片/系统/结构异常）返回 null。 */
export function searchableTextOf(message: Message): string | null {
  if (message.deletedAt != null) return null
  if (message.msgType === 'text') {
    return typeof message.content === 'string' ? message.content : null
  }
  if (message.msgType === 'file') {
    const content = message.content
    if (content && typeof content === 'object' && !Array.isArray(content)) {
      const name = (content as { name?: unknown }).name
      return typeof name === 'string' ? name : null
    }
  }
  return null
}

/** 归一化 limit：非法/<=0 回退默认值，避免调用方传入异常值。 */
function normalizeLimit(limit: number | undefined): number {
  if (limit === undefined) return LOCAL_SEARCH_LIMIT
  if (!Number.isFinite(limit)) return LOCAL_SEARCH_LIMIT
  const value = Math.floor(limit)
  return value > 0 ? value : LOCAL_SEARCH_LIMIT
}

/** F-37：本地搜索选项——与服务端的发送人/时间筛选、链接模式保持一致。 */
export interface LocalSearchOptions {
  limit?: number
  senderId?: string
  from?: number
  to?: number
  linkOnly?: boolean
}

const HTTP_URL_PATTERN = /https?:\/\/[^\s<>"'`]+/gi

/** 提取文本中的 HTTP/HTTPS URL（去尾部常见标点）。 */
export function extractHttpUrls(text: string): string[] {
  const urls: string[] = []
  for (const match of text.matchAll(HTTP_URL_PATTERN)) {
    const url = match[0].replace(/[.,;:!?)\]}，。；：！？）】]+$/u, '')
    if (url.length > 'https://'.length) urls.push(url)
  }
  return urls
}

function matchesLocalFilters(message: Message, options: LocalSearchOptions): boolean {
  const senderId = options.senderId?.trim()
  if (senderId && message.senderId !== senderId) return false
  const { from, to } = options
  if (from !== undefined && message.createdAt < from) return false
  if (to !== undefined && message.createdAt > to) return false
  return true
}

function matchesLocalSearch(message: Message, keyword: string, options: LocalSearchOptions): boolean {
  if (message.deletedAt != null) return false
  if (!matchesLocalFilters(message, options)) return false
  if (options.linkOnly) {
    if (message.msgType !== 'text' || typeof message.content !== 'string') return false
    return extractHttpUrls(message.content).some((url) => url.toLowerCase().includes(keyword))
  }
  const text = searchableTextOf(message)
  return text !== null && text.toLowerCase().includes(keyword)
}

/**
 * 在已加载消息中搜索关键词：去空白后为空返回 []；命中按 seq 降序、最多 limit 条。
 * F-37：应用与服务端相同的发送人/时间筛选与显式链接模式；只读入参。
 */
export function searchLocalMessages(
  messages: readonly Message[],
  query: string,
  options: LocalSearchOptions = {},
): Message[] {
  const keyword = query.trim().toLowerCase()
  if (!keyword) return []

  const hits = messages.filter((message) => matchesLocalSearch(message, keyword, options))
  hits.sort((left, right) => right.seq - left.seq)
  return hits.slice(0, normalizeLimit(options.limit))
}

/** 命中总数（不受 limit 截断）；筛选/链接模式与 searchLocalMessages 一致。 */
export function countLocalMatches(
  messages: readonly Message[],
  query: string,
  options: LocalSearchOptions = {},
): number {
  const keyword = query.trim().toLowerCase()
  if (!keyword) return 0
  return messages.reduce(
    (total, message) => (matchesLocalSearch(message, keyword, options) ? total + 1 : total),
    0,
  )
}

/**
 * F-19-05：合并服务端结果与本地结果——服务端只检索 text 消息，本地还会命中文件消息名，
 * 因此服务端结果返回后仍要保留本地独有的文件命中。
 * 规则：按 messageId 去重（服务端优先，内容以服务端为准），合并后按 seq 降序；不改入参。
 */
export function mergeLocalSearchResults(
  serverResults: readonly Message[],
  localResults: readonly Message[],
): Message[] {
  const merged: Message[] = []
  const seen = new Set<string>()

  for (const message of serverResults) {
    if (seen.has(message.messageId)) continue
    seen.add(message.messageId)
    merged.push(message)
  }
  for (const message of localResults) {
    if (seen.has(message.messageId)) continue
    seen.add(message.messageId)
    merged.push(message)
  }

  merged.sort((left, right) => right.seq - left.seq)
  return merged
}
