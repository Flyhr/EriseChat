import type { Message } from '../types/frames'

// 聊天渲染纯函数（UI-02 批次2，计划 §5.4 / §16.4）：日期分隔与渲染项组装。
// 无 Vue/DOM 依赖，便于单测（UI-02 批次5 继续扩展筛选与浮标判定）。

export interface DateSeparatorItem {
  kind: 'separator'
  key: string
  label: string
}

export interface MessageRowItem {
  kind: 'message'
  key: string
  message: Message
}

export type MessageListItem = DateSeparatorItem | MessageRowItem

/** 本地日期 key（YYYY-MM-DD）：用于判断跨天；非法时间返回空串。 */
function dateKeyOf(timestamp: number): string {
  const date = new Date(timestamp)
  if (Number.isNaN(date.getTime())) return ''
  const month = String(date.getMonth() + 1).padStart(2, '0')
  const day = String(date.getDate()).padStart(2, '0')
  return `${date.getFullYear()}-${month}-${day}`
}

/** 日期分隔文案：今天 / 昨天 / MM-DD（非法时间返回空串，不插入分隔）。 */
export function dateSeparatorLabel(timestamp: number, now: number = Date.now()): string {
  const key = dateKeyOf(timestamp)
  if (!key) return ''
  if (key === dateKeyOf(now)) return '今天'
  const yesterday = new Date(now)
  yesterday.setDate(yesterday.getDate() - 1)
  if (key === dateKeyOf(yesterday.getTime())) return '昨天'
  const date = new Date(timestamp)
  const month = String(date.getMonth() + 1).padStart(2, '0')
  const day = String(date.getDate()).padStart(2, '0')
  return `${month}-${day}`
}

/**
 * 把按 seq 升序的消息组装为渲染项：跨天处插入日期分隔（相同时刻只插一次）。
 * 输入顺序即展示顺序，不重排（协议 §4.3：客户端按 seq 排序后渲染）。
 */
export function buildMessageItems(
  messages: readonly Message[],
  now: number = Date.now(),
): MessageListItem[] {
  const items: MessageListItem[] = []
  let lastDateKey = ''
  for (const message of messages) {
    const key = dateKeyOf(message.createdAt)
    if (key && key !== lastDateKey) {
      lastDateKey = key
      items.push({
        kind: 'separator',
        key: `separator-${key}`,
        label: dateSeparatorLabel(message.createdAt, now),
      })
    }
    items.push({ kind: 'message', key: message.messageId, message })
  }
  return items
}
