import { describe, expect, it } from 'vitest'
import {
  AVATAR_MAX_SOURCE_BYTES,
  groupAvatarFileIdOf,
  hasGroupAvatar,
  isAcceptableAvatarFile,
} from './groupAvatar'

const fileOf = (type: string, size: number) => ({ type, size })

describe('isAcceptableAvatarFile', () => {
  it('接受常见图片类型', () => {
    for (const type of ['image/png', 'image/jpeg', 'image/webp', 'image/gif']) {
      expect(isAcceptableAvatarFile(fileOf(type, 1024))).toBe(true)
    }
  })

  it('拒绝非图片类型', () => {
    for (const type of ['application/pdf', 'text/plain', '', 'image']) {
      expect(isAcceptableAvatarFile(fileOf(type, 1024))).toBe(false)
    }
  })

  it('拒绝空文件与非法大小', () => {
    expect(isAcceptableAvatarFile(fileOf('image/png', 0))).toBe(false)
    expect(isAcceptableAvatarFile(fileOf('image/png', -1))).toBe(false)
    expect(isAcceptableAvatarFile(fileOf('image/png', Number.NaN))).toBe(false)
  })

  it('5MB 边界：恰好通过、超出拒绝', () => {
    expect(AVATAR_MAX_SOURCE_BYTES).toBe(5 * 1024 * 1024)
    expect(isAcceptableAvatarFile(fileOf('image/png', AVATAR_MAX_SOURCE_BYTES))).toBe(true)
    expect(isAcceptableAvatarFile(fileOf('image/png', AVATAR_MAX_SOURCE_BYTES + 1))).toBe(false)
  })
})

describe('groupAvatarFileIdOf', () => {
  it('群聊返回 fileId', () => {
    expect(groupAvatarFileIdOf({ type: 'group', avatarFileId: 'file-1' })).toBe('file-1')
  })

  it('群聊未设置返回 null', () => {
    expect(groupAvatarFileIdOf({ type: 'group' })).toBeNull()
    expect(groupAvatarFileIdOf({ type: 'group', avatarFileId: null })).toBeNull()
    expect(groupAvatarFileIdOf({ type: 'group', avatarFileId: '' })).toBeNull()
  })

  it('单聊即使带 fileId 也返回 null', () => {
    expect(groupAvatarFileIdOf({ type: 'direct', avatarFileId: 'file-1' })).toBeNull()
  })

  it('空会话返回 null', () => {
    expect(groupAvatarFileIdOf(null)).toBeNull()
    expect(groupAvatarFileIdOf(undefined)).toBeNull()
  })
})

describe('hasGroupAvatar', () => {
  it('与 fileId 归一保持一致', () => {
    expect(hasGroupAvatar({ type: 'group', avatarFileId: 'file-1' })).toBe(true)
    expect(hasGroupAvatar({ type: 'group' })).toBe(false)
    expect(hasGroupAvatar({ type: 'direct', avatarFileId: 'file-1' })).toBe(false)
  })
})
