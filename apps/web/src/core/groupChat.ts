import type { Conversation, ConversationMember, MessageContent } from '../types/frames'

// 群聊展示工具（M3-08，纯函数，便于单测）：系统消息文案兜底、成员排序、成员数与会话标签。
// 文案与排序规则对齐 protocol/protocol.md §4.3/§4.5 与 docs/internal-plan.md §4.6。

/** 系统事件兜底文案（服务端 content.text 缺失时使用；protocol.md §4.3 的四个事件）。 */
export const SYSTEM_EVENT_FALLBACK: Readonly<Record<string, string>> = {
  'group.created': '群聊已创建',
  'member.joined': '有新成员加入群聊',
  'member.left': '有成员退出群聊',
  'member.removed': '有成员被移出群聊',
}

/**
 * system 消息展示文案：优先 content.text（服务端已带操作者/被邀请人描述）；
 * 缺失时按 content.event 兜底；未知事件或异常数据回退「系统消息」。
 */
export function systemMessageText(content: MessageContent | null | undefined): string {
  if (content === null || content === undefined) return '系统消息'
  if (typeof content === 'string') return content.length > 0 ? content : '系统消息'
  if ('text' in content && content.text) return content.text
  if ('event' in content) return SYSTEM_EVENT_FALLBACK[content.event] ?? '系统消息'
  return '系统消息'
}

/** 成员排序：群主排最前，其余按昵称 zh-CN 排序，昵称相同再按 userId（结果稳定，不改入参）。 */
export function sortMembers(members: readonly ConversationMember[]): ConversationMember[] {
  return [...members].sort((left, right) => {
    if (left.role !== right.role) return left.role === 'owner' ? -1 : 1
    const byName = left.displayName.localeCompare(right.displayName, 'zh-CN')
    return byName !== 0 ? byName : left.userId.localeCompare(right.userId)
  })
}

/** 成员数徽标文案（如「3 人」）；非法/负数按 0 处理。 */
export function memberCountLabel(count: number): string {
  const safe = Number.isFinite(count) && count > 0 ? Math.floor(count) : 0
  return `${safe} 人`
}

/** 会话展示标签：群聊 = 群名（成员数），单聊不加成员数（由调用方补对方昵称）。 */
export function conversationLabel(
  conversation: Pick<Conversation, 'type' | 'title' | 'members'>,
): string {
  if (conversation.type === 'group') {
    const name = conversation.title.trim() || '群聊'
    return `${name}（${conversation.members.length}）`
  }
  return conversation.title.trim() || '单聊会话'
}
