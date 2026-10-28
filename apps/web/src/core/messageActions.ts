// F-18/F-26：消息操作（回复/转发/收藏/撤回/编辑）的纯逻辑。
//
// 时间窗口与 protocol.md §4.3 一致（服务端为最终判定，这里只决定 UI 是否展示入口）：
// - 撤回：发送者本人、非 system、未撤回、创建后 120 秒内；
// - 编辑：发送者本人、文本、未撤回、创建后 900 秒内；
// - 转发：仅文本、未撤回（纯前端发送一条新消息）；
// - 收藏：文本/图片/文件、未撤回（本地 IndexedDB 快照）。
import type { Message } from '../types/frames'
import { canForward } from './forward'
import { isFavoritable } from './favorites'

/** 撤回窗口（毫秒），与服务端 AppOptions.MessageRecallWindowSeconds 默认值一致。 */
export const MESSAGE_RECALL_WINDOW_MS = 120_000
/** 编辑窗口（毫秒），与服务端 AppOptions.MessageEditWindowSeconds 默认值一致。 */
export const MESSAGE_EDIT_WINDOW_MS = 900_000

export type MessageActionId = 'reply' | 'forward' | 'favorite' | 'unfavorite' | 'recall' | 'edit'

export interface MessageAction {
  id: MessageActionId
  label: string
  /** 危险操作（UI 使用 danger 色）。 */
  danger?: boolean
}

export interface MessageActionOptions {
  selfUserId: string
  isGroup?: boolean
  now?: number
  recallWindowMs?: number
  editWindowMs?: number
  /** 群聊里他人消息的发送者昵称（用于撤回占位文案）。 */
  senderName?: string
  /** F-26：该消息是否已被当前账号收藏（决定显示「收藏」还是「取消收藏」）。 */
  favorited?: boolean
}

/** 是否已被撤回（deletedAt 非空）。 */
export function isRecalled(message: Pick<Message, 'deletedAt'> | null | undefined): boolean {
  return Boolean(message) && typeof message?.deletedAt === 'number'
}

function withinWindow(createdAt: number, now: number, windowMs: number): boolean {
  if (!Number.isFinite(createdAt) || !Number.isFinite(now)) return false
  if (!Number.isFinite(windowMs) || windowMs <= 0) return false
  const elapsed = now - createdAt
  return elapsed >= 0 && elapsed <= windowMs
}

/** 是否可撤回：本人 + 非 system + 未撤回 + 窗口内。 */
export function canRecall(
  message: Message | null | undefined,
  selfUserId: string,
  now: number = Date.now(),
  recallWindowMs: number = MESSAGE_RECALL_WINDOW_MS,
): boolean {
  if (!message || !selfUserId) return false
  if (message.senderId !== selfUserId) return false
  if (message.msgType === 'system') return false
  if (isRecalled(message)) return false
  return withinWindow(message.createdAt, now, recallWindowMs)
}

/** 是否可编辑：本人 + 文本 + 未撤回 + 窗口内。 */
export function canEdit(
  message: Message | null | undefined,
  selfUserId: string,
  now: number = Date.now(),
  editWindowMs: number = MESSAGE_EDIT_WINDOW_MS,
): boolean {
  if (!message || !selfUserId) return false
  if (message.senderId !== selfUserId) return false
  if (message.msgType !== 'text') return false
  if (isRecalled(message)) return false
  return withinWindow(message.createdAt, now, editWindowMs)
}

/** 已撤回消息的占位文案（自己/单聊对方/群聊成员）。 */
export function recallPlaceholderText(
  message: Pick<Message, 'senderId' | 'deletedAt'> | null | undefined,
  options: Pick<MessageActionOptions, 'selfUserId' | 'isGroup' | 'senderName'>,
): string {
  if (!isRecalled(message)) return ''
  if (message?.senderId === options.selfUserId) return '你撤回了一条消息'
  if (options.isGroup) {
    const name = options.senderName?.trim()
    return `${name && name.length > 0 ? name : '成员'} 撤回了一条消息`
  }
  return '对方撤回了一条消息'
}

/** 构建消息操作菜单项（回复恒有；转发/收藏/撤回/编辑按能力与权限过滤）。 */
export function buildMessageActions(
  message: Message | null | undefined,
  options: MessageActionOptions,
): MessageAction[] {
  const actions: MessageAction[] = [{ id: 'reply', label: '回复' }]
  if (canForward(message)) {
    actions.push({ id: 'forward', label: '转发' })
  }
  if (isFavoritable(message)) {
    actions.push(
      options.favorited
        ? { id: 'unfavorite', label: '取消收藏' }
        : { id: 'favorite', label: '收藏' },
    )
  }
  if (canRecall(message, options.selfUserId, options.now, options.recallWindowMs)) {
    actions.push({ id: 'recall', label: '撤回', danger: true })
  }
  if (canEdit(message, options.selfUserId, options.now, options.editWindowMs)) {
    actions.push({ id: 'edit', label: '编辑' })
  }
  return actions
}
