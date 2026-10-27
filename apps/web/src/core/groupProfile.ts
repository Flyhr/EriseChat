// 群资料（群名 / 群公告）纯函数（F-14，计划 §F-14-04）。
// 只做展示层校验与格式化，不发请求、不依赖 DOM；服务端仍做最终校验。
import type { Conversation } from '../types/frames'

/** 群名称最大长度（与服务端 ConversationService.TitleMaxLength 一致）。 */
export const GROUP_TITLE_MAX_LENGTH = 100

/** 群公告最大长度（与服务端 ConversationService.AnnouncementMaxLength 一致）。 */
export const ANNOUNCEMENT_MAX_LENGTH = 500

/** 群名校验结果：ok=true 时 value 为 trim 后的值。 */
export type GroupTitleValidation =
  | { ok: true; value: string }
  | { ok: false; message: string }

/**
 * 当前用户能否编辑该会话的群资料：必须是群聊，且自己的成员行 role === 'owner'。
 * 单聊、非成员、非群主一律 false。
 */
export function canEditGroupProfile(
  conversation: Pick<Conversation, 'type' | 'members'> | null | undefined,
  selfUserId: string | null | undefined,
): boolean {
  if (!conversation || !selfUserId) {
    return false
  }

  if (conversation.type !== 'group') {
    return false
  }

  const self = conversation.members.find((member) => member.userId === selfUserId)
  return self?.role === 'owner'
}

/** 群名称校验：trim 后 1~100 字符；返回 trim 后的值或错误文案。 */
export function validateGroupTitle(raw: string): GroupTitleValidation {
  const value = (raw ?? '').trim()
  if (value.length === 0) {
    return { ok: false, message: '群名称不能为空' }
  }

  if (value.length > GROUP_TITLE_MAX_LENGTH) {
    return { ok: false, message: `群名称最多 ${GROUP_TITLE_MAX_LENGTH} 个字符` }
  }

  return { ok: true, value }
}

/**
 * 公告编辑内容归一：CRLF / CR 统一为 LF 并去首尾空白。
 * 结果为 '' 表示清空公告（调用方传 `announcement: ''`）。
 */
export function normalizeAnnouncementInput(raw: string): string {
  return (raw ?? '')
    .replace(/\r\n/g, '\n')
    .replace(/\r/g, '\n')
    .trim()
}

/** 公告字符数（按 code point 计，换行也计入；null/undefined 视为 0）。 */
export function announcementCountOf(raw: string | null | undefined): number {
  if (!raw) {
    return 0
  }

  return Array.from(raw).length
}

/**
 * 公告预览：按行截断到 maxLines 行；超出时末尾加 `…`。
 * 空公告返回 { text: '', truncated: false }。
 */
export function announcementPreview(
  raw: string | null | undefined,
  maxLines = 2,
): { text: string; truncated: boolean } {
  const text = (raw ?? '').trim()
  if (text.length === 0) {
    return { text: '', truncated: false }
  }

  const limit = Number.isFinite(maxLines) && maxLines > 0 ? Math.floor(maxLines) : 2
  const lines = text.split('\n')
  if (lines.length <= limit) {
    return { text, truncated: false }
  }

  return { text: `${lines.slice(0, limit).join('\n')}\n…`, truncated: true }
}

/** 公告是否为空（null/undefined/纯空白均视为空）。 */
export function announcementIsEmpty(raw: string | null | undefined): boolean {
  return (raw ?? '').trim().length === 0
}
