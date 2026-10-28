// F-25 批次 1：@提及纯函数与富文本分段（无 DOM 依赖，便于 Vitest 单测）。
// @ 只是文本约定（content.text 中的「@昵称」），服务端不解析、不额外推送；
// 历史消息按「当前成员昵称」匹配，成员改名后旧消息可能不再高亮（已知限制，见交付说明）。
import { splitLinks } from './links'

/** 候选面板最多展示的成员数。 */
export const MENTION_CANDIDATE_LIMIT = 8

/** findMentionToken 识别出的未完成 @ 片段（caret 位于片段内）。 */
export interface MentionToken {
  /** '@' 的下标。 */
  start: number
  /** 光标位置（片段右边界，不含）。 */
  end: number
  /** '@' 与光标之间的文字（可能为空）。 */
  query: string
}

/** 群成员候选（只取渲染/选择所需字段）。 */
export interface MentionCandidateMember {
  userId: string
  displayName: string
}

/** 富文本分段：普通文本 / 安全链接 / @提及。 */
export type RichSegment =
  | { kind: 'text'; value: string }
  | { kind: 'link'; value: string; href: string }
  | { kind: 'mention'; value: string; self: boolean }

/** 单个 @ 查询的最大长度（超出视为普通文本，避免误触发）。 */
const MAX_QUERY_LENGTH = 20

/** 行首/空白/常见中英文标点之后才允许开启 @ 片段。 */
const MENTION_START_BOUNDARY = /[\s\u3000，。！？；：、（）【】《》「」『』“”‘’"'.,!?;:()[\]{}<>/\\|·…~`@#$%^&*+=_-]/

/** @昵称 之后必须是空白/标点/结尾，才算一次完整提及。 */
const MENTION_END_BOUNDARY = /[\s\u3000，。！？；：、（）【】《》「」『』“”‘’"'.,!?;:()[\]{}<>/\\|·…~`]/

function isStartBoundary(text: string, index: number): boolean {
  if (index <= 0) return true
  return MENTION_START_BOUNDARY.test(text[index - 1] ?? '')
}

function isEndBoundary(text: string, index: number): boolean {
  if (index >= text.length) return true
  return MENTION_END_BOUNDARY.test(text[index] ?? '')
}

/**
 * 从光标向前找最近的 '@'，判断光标是否处于一个「未完成」的 @ 片段内。
 * 要求：'@' 在行首/空白/常见标点之后；'@' 与光标之间不含空白；query 长度 ≤20。
 */
export function findMentionToken(text: string, caret: number): MentionToken | null {
  if (typeof text !== 'string' || !text) return null
  if (!Number.isFinite(caret)) return null
  const position = Math.max(0, Math.min(Math.trunc(caret), text.length))
  if (position === 0) return null

  const at = text.lastIndexOf('@', position - 1)
  if (at < 0) return null
  if (!isStartBoundary(text, at)) return null

  const query = text.slice(at + 1, position)
  if (query.length > MAX_QUERY_LENGTH) return null
  // 片段内出现空白/换行/@，说明这个 @ 已经结束（例如「@张三 你好」），不再弹候选。
  if (/[\s\u3000@]/.test(query)) return null

  return { start: at, end: position, query }
}

/**
 * 按输入词筛出 @ 候选成员：排除自己；昵称前缀命中优先，其次包含命中；大小写不敏感。
 * 同组内保持传入顺序（稳定排序），最多返回 limit 个。
 */
export function filterMentionCandidates(
  members: readonly MentionCandidateMember[],
  query: string,
  selfUserId?: string,
  limit: number = MENTION_CANDIDATE_LIMIT,
): MentionCandidateMember[] {
  const keyword = typeof query === 'string' ? query.trim().toLowerCase() : ''
  const normalizedLimit = Number.isFinite(limit) && limit > 0 ? Math.trunc(limit) : MENTION_CANDIDATE_LIMIT
  const prefixMatches: MentionCandidateMember[] = []
  const containsMatches: MentionCandidateMember[] = []

  for (const member of members) {
    if (!member?.userId || !member.displayName) continue
    if (selfUserId && member.userId === selfUserId) continue
    const name = member.displayName.toLowerCase()
    if (!keyword) {
      prefixMatches.push(member)
      continue
    }
    if (name.startsWith(keyword)) {
      prefixMatches.push(member)
    } else if (name.includes(keyword)) {
      containsMatches.push(member)
    }
  }

  return [...prefixMatches, ...containsMatches].slice(0, normalizedLimit)
}

/**
 * 把光标处的 @token 替换为「@昵称 」（尾随一个空格），返回新文本与插入后的光标位置。
 * 仅做文本编辑，不校验 member 是否真的在会话中。
 */
export function insertMention(text: string, token: MentionToken, member: MentionCandidateMember): { text: string; caret: number } {
  const safeText = typeof text === 'string' ? text : ''
  const start = Math.max(0, Math.min(token.start, safeText.length))
  const end = Math.max(start, Math.min(token.end, safeText.length))
  const displayName = member.displayName.trim()
  const inserted = `@${displayName} `
  return {
    text: `${safeText.slice(0, start)}${inserted}${safeText.slice(end)}`,
    caret: start + inserted.length,
  }
}

/** 候选昵称按长度降序（同长度保持传入顺序），保证「@张三丰」优先于「@张三」。 */
function sortNamesByLength(names: readonly string[]): string[] {
  return [...new Set(names.filter((name) => typeof name === 'string' && name.trim()))].sort(
    (left, right) => right.length - left.length,
  )
}

/** 把一段纯文本按已知昵称切成 text / mention 段。 */
function splitMentionsInText(text: string, names: readonly string[], selfName: string): RichSegment[] {
  if (!text) return []
  const segments: RichSegment[] = []
  let lastCut = 0
  let index = 0

  while (index < text.length) {
    if (text[index] !== '@' || !isStartBoundary(text, index)) {
      index += 1
      continue
    }
    const matched = names.find((name) => text.startsWith(name, index + 1) && isEndBoundary(text, index + 1 + name.length))
    if (!matched) {
      index += 1
      continue
    }
    if (index > lastCut) {
      segments.push({ kind: 'text', value: text.slice(lastCut, index) })
    }
    segments.push({ kind: 'mention', value: `@${matched}`, self: Boolean(selfName) && matched === selfName })
    lastCut = index + 1 + matched.length
    index = lastCut
  }

  if (lastCut < text.length) {
    segments.push({ kind: 'text', value: text.slice(lastCut) })
  }
  return segments
}

/**
 * 富文本分段：先按 F-07 规则切链接（链接内 @ 不算提及），再把纯文本段按已知昵称切成 @ 提及。
 * mentionNames 通常传会话成员的当前昵称；selfName 用于标记「提到我」。
 */
export function splitRichText(text: string, mentionNames: readonly string[], selfName = ''): RichSegment[] {
  if (typeof text !== 'string' || !text) return []
  const names = sortNamesByLength(mentionNames)
  const segments: RichSegment[] = []

  for (const segment of splitLinks(text)) {
    if (segment.kind === 'link') {
      segments.push({ kind: 'link', value: segment.value, href: segment.href })
      continue
    }
    segments.push(...splitMentionsInText(segment.value, names, selfName))
  }
  return segments
}

/** 文本中是否包含对 selfName 的完整 @提及。 */
export function hasSelfMention(text: string, selfName: string): boolean {
  if (typeof text !== 'string' || !text || !selfName) return false
  return splitRichText(text, [selfName], selfName).some((segment) => segment.kind === 'mention' && segment.self)
}
