// UI-02：基础 Emoji 面板数据与「插入到光标处」纯函数（计划 §5.5 / §16.3 场景 2）。
// 仅使用 Unicode 字符，不引入表情 SDK/字体包；点击表情只插入不发送，由父组件负责。
export interface EmojiGroup {
  /** 分组名称（中文，界面直接展示）。 */
  readonly label: string
  /** 该分组下的 Unicode Emoji（顺序即展示顺序）。 */
  readonly emojis: readonly string[]
}

/** 常用 / 表情 / 手势三组，共 60+ 个基础 Emoji。 */
export const EMOJI_GROUPS: readonly EmojiGroup[] = [
  {
    label: '常用',
    emojis: ['😀', '😁', '😂', '🤣', '😊', '😉', '😍', '😘', '😎', '🤗', '😅', '🙃', '😴', '🤔', '😮', '😢', '😭', '😡', '👍', '🙏'],
  },
  {
    label: '表情',
    emojis: ['🥰', '😋', '🤤', '😷', '🤒', '🤕', '🤠', '🥳', '😇', '🙂', '😐', '😬', '🙄', '😳', '🥺', '😱', '😤', '😫', '🤯', '😵', '🤐', '🤫', '🫡', '🫠'],
  },
  {
    label: '手势',
    emojis: ['👋', '🤝', '👌', '✌️', '🤞', '🤟', '🤘', '🤙', '👈', '👉', '👆', '👇', '☝️', '✋', '🤚', '🖐️', '🖖', '👏', '🙌', '🤲', '🤜', '🤛', '👊', '✊'],
  },
]

export interface EmojiInsertResult {
  /** 插入后的完整文本。 */
  text: string
  /** 插入后的光标位置（emoji 之后）。 */
  caret: number
}

/**
 * 在 text 的 caret 位置插入 emoji（纯函数，不触碰组件状态）。
 * caret 会被夹紧到 [0, text.length]，越界不抛错；插入后光标停在 emoji 之后。
 */
export function insertAtCursor(text: string, caret: number, emoji: string): EmojiInsertResult {
  const safeCaret = Math.min(Math.max(0, Math.trunc(caret)), text.length)
  const nextText = text.slice(0, safeCaret) + emoji + text.slice(safeCaret)
  return { text: nextText, caret: safeCaret + emoji.length }
}

/** F-21：最近使用表情最多保留 24 个（本地 UI 偏好，不落服务端）。 */
export const EMOJI_RECENT_LIMIT = 24

/**
 * 把 emoji 放到最近使用列表最前面（纯函数，不修改入参）。
 * 规则：忽略空串；已存在则先移除再前置（去重）；超过上限截断到 24 个。
 */
export function pushRecentEmoji(list: readonly string[], emoji: string): string[] {
  if (typeof emoji !== 'string' || emoji.length === 0) return [...list]
  const rest = list.filter((item) => typeof item === 'string' && item.length > 0 && item !== emoji)
  return [emoji, ...rest].slice(0, EMOJI_RECENT_LIMIT)
}
