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

/**
 * 在已加载消息中搜索关键词：去空白后为空返回 []；命中按 seq 降序、最多 limit 条。
 * 只读入参，不修改 messages 数组或其中对象。
 */
export function searchLocalMessages(
  messages: readonly Message[],
  query: string,
  options: { limit?: number } = {},
): Message[] {
  const keyword = query.trim().toLowerCase()
  if (!keyword) return []

  const hits = messages.filter((message) => {
    const text = searchableTextOf(message)
    return text !== null && text.toLowerCase().includes(keyword)
  })

  hits.sort((left, right) => right.seq - left.seq)
  return hits.slice(0, normalizeLimit(options.limit))
}

/** 命中总数（不受 limit 截断，用于「本地 N 条」提示）。 */
export function countLocalMatches(messages: readonly Message[], query: string): number {
  const keyword = query.trim().toLowerCase()
  if (!keyword) return 0
  return messages.reduce((total, message) => {
    const text = searchableTextOf(message)
    return text !== null && text.toLowerCase().includes(keyword) ? total + 1 : total
  }, 0)
}
