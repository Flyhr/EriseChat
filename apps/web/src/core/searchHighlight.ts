// 消息搜索高亮（F-11 批次2，计划 §F-11）：纯函数，不依赖 DOM/正则状态。
// 只做「大小写不敏感的机密片段切分」与「结果摘要窗口」，渲染由组件用文本节点完成（禁止 v-html）。

/** 高亮分段：text 为原文片段，hit=true 表示命中关键词。 */
export interface HighlightSegment {
  text: string
  hit: boolean
}

/** 把用户输入中的正则元字符转义，避免 q 含 . * + ? 等字符时误命中或抛错。 */
function escapeRegExp(value: string): string {
  return value.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')
}

/**
 * 将文本按关键词切成高亮/非高亮片段（大小写不敏感、支持多次命中）。
 * - 空文本返回空数组；空关键词视为不高亮，返回单个非高亮片段。
 * - 只切分、不改写原文，保证展示与原文逐字一致。
 */
export function highlightSegments(text: string, q: string): HighlightSegment[] {
  if (!text) return []

  const keyword = q.trim()
  if (!keyword) return [{ text, hit: false }]

  const pattern = new RegExp(escapeRegExp(keyword), 'gi')
  const segments: HighlightSegment[] = []
  let lastIndex = 0
  let match: RegExpExecArray | null

  while ((match = pattern.exec(text)) !== null) {
    if (match[0].length === 0) {
      // 理论不可达（关键词非空），防止死循环。
      pattern.lastIndex += 1
      continue
    }

    if (match.index > lastIndex) {
      segments.push({ text: text.slice(lastIndex, match.index), hit: false })
    }
    segments.push({ text: match[0], hit: true })
    lastIndex = match.index + match[0].length
  }

  if (segments.length === 0) return [{ text, hit: false }]
  if (lastIndex < text.length) {
    segments.push({ text: text.slice(lastIndex), hit: false })
  }
  return segments
}

/**
 * 生成结果摘要：以首次命中位置为中心截取前后 radius 个字符，必要时补省略号。
 * 未命中时返回开头一段（便于展示上下文）；空关键词返回开头一段。
 * 返回值是纯文本，调用方应再对摘要调用 highlightSegments 渲染高亮。
 */
export function snippetOf(text: string, q: string, radius = 30): string {
  if (!text) return ''

  const safeRadius = Number.isFinite(radius) ? Math.max(1, Math.floor(radius)) : 30
  const keyword = q.trim()
  if (!keyword) {
    return text.length <= safeRadius * 2 ? text : `${text.slice(0, safeRadius * 2)}…`
  }

  const index = text.toLowerCase().indexOf(keyword.toLowerCase())
  if (index < 0) {
    return text.length <= safeRadius * 2 ? text : `${text.slice(0, safeRadius * 2)}…`
  }

  const start = Math.max(0, index - safeRadius)
  const end = Math.min(text.length, index + keyword.length + safeRadius)
  const prefix = start > 0 ? '…' : ''
  const suffix = end < text.length ? '…' : ''
  return `${prefix}${text.slice(start, end)}${suffix}`
}
