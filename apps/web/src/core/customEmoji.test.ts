import { describe, expect, it } from 'vitest'
import {
  CUSTOM_EMOJI_GIF_MAX_BYTES,
  CUSTOM_EMOJI_MAX_SOURCE_BYTES,
  checkCustomEmojiFile,
  customEmojiFileName,
  sortCustomEmojis,
} from './customEmoji'

// F-21-02：自定义表情输入校验与辅助纯函数（不测 IndexedDB 读写，避免引入 fake-indexeddb）。
function fileOf(type: string, size: number): Pick<File, 'type' | 'size'> {
  return { type, size }
}

describe('checkCustomEmojiFile', () => {
  it('PNG/JPEG/WebP 通过并需要压缩', () => {
    for (const mime of ['image/png', 'image/jpeg', 'image/webp']) {
      expect(checkCustomEmojiFile(fileOf(mime, 120 * 1024))).toEqual({ ok: true, needsCompress: true })
    }
  })

  it('GIF 通过但不需要压缩', () => {
    expect(checkCustomEmojiFile(fileOf('image/gif', 800 * 1024))).toEqual({ ok: true, needsCompress: false })
  })

  it('拒绝非白名单类型', () => {
    for (const mime of ['image/bmp', 'image/svg+xml', 'text/plain', '']) {
      const result = checkCustomEmojiFile(fileOf(mime, 1024))
      expect(result.ok).toBe(false)
      if (!result.ok) expect(result.reason).toBe('仅支持 PNG/JPEG/WebP/GIF 图片')
    }
  })

  it('2MB 是包含边界，超过 1 字节即拒绝', () => {
    expect(checkCustomEmojiFile(fileOf('image/png', CUSTOM_EMOJI_MAX_SOURCE_BYTES)).ok).toBe(true)
    const tooLarge = checkCustomEmojiFile(fileOf('image/png', CUSTOM_EMOJI_MAX_SOURCE_BYTES + 1))
    expect(tooLarge.ok).toBe(false)
    if (!tooLarge.ok) expect(tooLarge.reason).toBe('图片不能超过 2MB')
  })

  it('GIF 上限 1MB，超过即拒绝', () => {
    expect(checkCustomEmojiFile(fileOf('image/gif', CUSTOM_EMOJI_GIF_MAX_BYTES)).ok).toBe(true)
    const tooLarge = checkCustomEmojiFile(fileOf('image/gif', CUSTOM_EMOJI_GIF_MAX_BYTES + 1))
    expect(tooLarge.ok).toBe(false)
    if (!tooLarge.ok) expect(tooLarge.reason).toBe('GIF 不能超过 1MB')
  })

  it('空文件被拒绝', () => {
    const empty = checkCustomEmojiFile(fileOf('image/png', 0))
    expect(empty.ok).toBe(false)
    if (!empty.ok) expect(empty.reason).toBe('图片文件为空')
  })
})

describe('customEmojiFileName', () => {
  it('按 MIME 映射文件名', () => {
    expect(customEmojiFileName('image/png')).toBe('emoji.png')
    expect(customEmojiFileName('image/jpeg')).toBe('emoji.jpg')
    expect(customEmojiFileName('image/webp')).toBe('emoji.webp')
    expect(customEmojiFileName('image/gif')).toBe('emoji.gif')
  })

  it('未知 MIME 回退 png', () => {
    expect(customEmojiFileName('application/octet-stream')).toBe('emoji.png')
    expect(customEmojiFileName('')).toBe('emoji.png')
  })
})

describe('sortCustomEmojis', () => {
  it('按 createdAt 倒序且稳定', () => {
    const items = [
      { id: 'a', createdAt: 1 },
      { id: 'b', createdAt: 3 },
      { id: 'c', createdAt: 2 },
      { id: 'd', createdAt: 3 },
    ]
    expect(sortCustomEmojis(items).map((item) => item.id)).toEqual(['b', 'd', 'c', 'a'])
  })

  it('不改入参', () => {
    const items = [{ id: 'a', createdAt: 1 }, { id: 'b', createdAt: 2 }]
    sortCustomEmojis(items)
    expect(items.map((item) => item.id)).toEqual(['a', 'b'])
  })

  it('非法 createdAt 按 0 排到最后', () => {
    const items = [
      { id: 'bad', createdAt: Number.NaN },
      { id: 'good', createdAt: 5 },
    ]
    expect(sortCustomEmojis(items).map((item) => item.id)).toEqual(['good', 'bad'])
  })
})
