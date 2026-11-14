// C3：消息表情回应的稳定标识与显示映射。
// 协议只传标识（message_reactions.emoji / ReactionSummary.emoji），前端在此映射字形与无障碍文案，
// 增删表情只需改这一处；未知标识回退原字符，保证新旧端混用时仍能显示。
import type { ReactionSummary } from '../types/frames'

/** 首发支持的常用表情，顺序即菜单与回应条的展示顺序（与服务端白名单一致）。 */
export const REACTION_EMOJIS = ['thumbs_up', 'heart', 'joy', 'wow', 'sad', 'tada'] as const

export type ReactionEmoji = (typeof REACTION_EMOJIS)[number]

interface ReactionDisplay {
  glyph: string
  label: string
}

const DISPLAY: Record<string, ReactionDisplay> = {
  thumbs_up: { glyph: '👍', label: '赞' },
  heart: { glyph: '❤️', label: '喜欢' },
  joy: { glyph: '😂', label: '笑哭' },
  wow: { glyph: '😮', label: '惊讶' },
  sad: { glyph: '😢', label: '难过' },
  tada: { glyph: '🎉', label: '庆祝' },
}

/** 表情字形；未知标识回退原字符。 */
export function reactionGlyph(emoji: string): string {
  return DISPLAY[emoji]?.glyph ?? emoji
}

/** 表情中文文案（用于 aria-label 与 title）。 */
export function reactionLabel(emoji: string): string {
  return DISPLAY[emoji]?.label ?? emoji
}

/** 当前消息上本人已选的表情（无则空串），用于菜单高亮与「再点一次取消」判断。 */
export function myReactionOf(reactions: readonly ReactionSummary[] | undefined): string {
  return reactions?.find((reaction) => reaction.mine)?.emoji ?? ''
}
