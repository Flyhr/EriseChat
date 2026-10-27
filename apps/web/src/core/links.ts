// F-07 批次 1：文本链接识别与安全 URL 校验（纯函数，无 DOM 依赖，便于 Vitest 单测）。
// 仅识别 http/https；javascript:、data:、file:、ftp: 等一律按普通文本渲染，避免把不可信内容变成可点链接。

/** 单条文本最多渲染的链接数（超出部分按普通文本显示，避免超长文本攻击或性能问题）。 */
export const MAX_LINK_SEGMENTS = 20

/** 文本分段：普通文本或已校验的 http(s) 链接。 */
export type LinkSegment =
  | { kind: 'text'; value: string }
  | { kind: 'link'; value: string; href: string }

/** 匹配 http(s) 链接候选（大小写不敏感），终止于空白、引号、尖括号与中文标点。 */
const LINK_PATTERN = /https?:\/\/[^\s<>"'`。，！？；：、）】》」』]+/gi

/** 链接尾部常见的句读/括号，识别时剥掉，避免把句号当成 URL 的一部分。 */
const TRAILING_PUNCTUATION = new Set<string>([
  '.',
  ',',
  ';',
  '!',
  '?',
  ')',
  '。',
  '，',
  '！',
  '？',
  '；',
  '：',
  '、',
  '）',
  '】',
  '》',
  '」',
  '』',
  '…',
])

/** 判断是否为允许对外打开的安全 URL：严格 http/https 且带主机名。 */
export function isSafeExternalUrl(url: string): boolean {
  if (typeof url !== 'string') return false
  const value = url.trim()
  if (!value) return false
  try {
    const parsed = new URL(value)
    if (parsed.protocol !== 'http:' && parsed.protocol !== 'https:') return false
    return Boolean(parsed.hostname)
  } catch {
    return false
  }
}

/**
 * 把文本切成「普通文本 / 安全链接」分段，保持原文顺序与空白。
 * 未通过安全校验的候选按普通文本保留；单条文本最多识别 MAX_LINK_SEGMENTS 个链接。
 */
export function splitLinks(text: string): LinkSegment[] {
  if (typeof text !== 'string' || !text) return []

  const segments: LinkSegment[] = []
  let lastIndex = 0
  let linkCount = 0

  LINK_PATTERN.lastIndex = 0
  let match: RegExpExecArray | null
  while ((match = LINK_PATTERN.exec(text)) !== null) {
    if (linkCount >= MAX_LINK_SEGMENTS) break

    const candidate = trimTrailingPunctuation(match[0])
    if (!isSafeExternalUrl(candidate)) continue

    if (match.index > lastIndex) {
      segments.push({ kind: 'text', value: text.slice(lastIndex, match.index) })
    }
    segments.push({ kind: 'link', value: candidate, href: candidate })
    lastIndex = match.index + candidate.length
    linkCount += 1
  }

  if (lastIndex < text.length) {
    segments.push({ kind: 'text', value: text.slice(lastIndex) })
  }
  return segments
}

/** 从候选链接尾部剥掉句读/括号等标点（保留 URL 本体）。 */
function trimTrailingPunctuation(value: string): string {
  let end = value.length
  while (end > 0 && TRAILING_PUNCTUATION.has(value[end - 1] ?? '')) end -= 1
  return value.slice(0, end)
}
