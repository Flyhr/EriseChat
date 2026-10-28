import type { Conversation } from '../types/frames'

/** 群头像原图上限（F-23）：只做本地预检，最终合法性（fileId/MIME/归属）仍由服务端校验。 */
export const AVATAR_MAX_SOURCE_BYTES = 5 * 1024 * 1024

/**
 * 本地预检：只接受图片类型且大小在 (0, 5MB] 的源文件。
 * 仅用于上传前给用户即时提示，不作为安全边界。
 */
export function isAcceptableAvatarFile(file: Pick<File, 'type' | 'size'>): boolean {
  if (!file.type.startsWith('image/')) {
    return false
  }

  return Number.isFinite(file.size) && file.size > 0 && file.size <= AVATAR_MAX_SOURCE_BYTES
}

/**
 * 群头像 fileId 归一：仅群聊返回非空字符串；单聊、未设置或空串一律返回 null。
 * 供会话列表 / 聊天顶栏 / 群资料区统一取头像。
 */
export function groupAvatarFileIdOf(
  conversation: Pick<Conversation, 'type' | 'avatarFileId'> | null | undefined,
): string | null {
  if (!conversation || conversation.type !== 'group') {
    return null
  }

  const fileId = conversation.avatarFileId
  return typeof fileId === 'string' && fileId.length > 0 ? fileId : null
}

/** 是否已设置群头像（模板展示判断用）。 */
export function hasGroupAvatar(
  conversation: Pick<Conversation, 'type' | 'avatarFileId'> | null | undefined,
): boolean {
  return groupAvatarFileIdOf(conversation) !== null
}
