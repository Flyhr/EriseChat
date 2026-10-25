import { describe, expect, it } from 'vitest'
import {
  ALLOWED_EXTENSIONS,
  MAX_UPLOAD_BYTES,
  computeScaledSize,
  extensionOf,
  formatFileSize,
  isImageFile,
  validateAttachment,
} from './attachments'

/** validateAttachment 只读取 name/size/type，用轻量对象代替真实 File（Node 环境无需 Blob）。 */
function fakeFile(name: string, size: number, type = ''): File {
  return { name, size, type } as unknown as File
}

describe('extensionOf / isImageFile', () => {
  it('扩展名统一小写，无扩展名返回空串', () => {
    expect(extensionOf('照片.PNG')).toBe('.png')
    expect(extensionOf('report.final.PDF')).toBe('.pdf')
    expect(extensionOf('noext')).toBe('')
  })

  it('图片扩展名识别（png/jpg/jpeg/gif/webp）', () => {
    expect(isImageFile(fakeFile('a.png', 10, 'image/png'))).toBe(true)
    expect(isImageFile(fakeFile('a.GIF', 10, 'image/gif'))).toBe(true)
    expect(isImageFile(fakeFile('a.pdf', 10, 'application/pdf'))).toBe(false)
    expect(isImageFile(fakeFile('a.zip', 10, 'application/zip'))).toBe(false)
  })
})

describe('validateAttachment', () => {
  it('白名单内文件通过（png / zip）', () => {
    expect(validateAttachment(fakeFile('a.png', 1024)).ok).toBe(true)
    expect(validateAttachment(fakeFile('b.zip', 2048)).ok).toBe(true)
  })

  it('文件名大写扩展名也通过与服务端一致（OrdinalIgnoreCase）', () => {
    expect(validateAttachment(fakeFile('C.PDF', 10)).ok).toBe(true)
  })

  it('白名单外类型被拒绝（.exe）', () => {
    const result = validateAttachment(fakeFile('evil.exe', 10))
    expect(result.ok).toBe(false)
    if (!result.ok) expect(result.reason).toContain('不支持的文件类型')
  })

  it('空文件被拒绝', () => {
    const result = validateAttachment(fakeFile('empty.txt', 0))
    expect(result.ok).toBe(false)
    if (!result.ok) expect(result.reason).toContain('文件为空')
  })

  it('超过 50MB 被拒绝', () => {
    const result = validateAttachment(fakeFile('big.zip', MAX_UPLOAD_BYTES + 1))
    expect(result.ok).toBe(false)
    if (!result.ok) expect(result.reason).toContain('50MB')
  })

  it('白名单与服务端一致（12 种）', () => {
    expect(ALLOWED_EXTENSIONS).toHaveLength(12)
    expect(ALLOWED_EXTENSIONS).toContain('.7z')
    expect(ALLOWED_EXTENSIONS).toContain('.docx')
  })
})

describe('formatFileSize', () => {
  it('0 / 1023 B 边界', () => {
    expect(formatFileSize(0)).toBe('0 B')
    expect(formatFileSize(-1)).toBe('0 B')
    expect(formatFileSize(1023)).toBe('1023 B')
  })

  it('KB / MB 边界', () => {
    expect(formatFileSize(1024)).toBe('1.0 KB')
    expect(formatFileSize(1536)).toBe('1.5 KB')
    expect(formatFileSize(1024 * 1024)).toBe('1.0 MB')
    expect(formatFileSize(2.5 * 1024 * 1024)).toBe('2.5 MB')
  })
})

describe('computeScaledSize', () => {
  it('横图等比缩放到最长边 1280', () => {
    expect(computeScaledSize(2560, 1280)).toEqual({ width: 1280, height: 640 })
  })

  it('竖图等比缩放到最长边 1280', () => {
    expect(computeScaledSize(1280, 2560)).toEqual({ width: 640, height: 1280 })
  })

  it('小于 maxEdge 不放大', () => {
    expect(computeScaledSize(320, 240)).toEqual({ width: 320, height: 240 })
  })

  it('正方形图缩放到 1280×1280', () => {
    expect(computeScaledSize(4000, 4000)).toEqual({ width: 1280, height: 1280 })
  })

  it('自定义 maxEdge 与非法尺寸', () => {
    expect(computeScaledSize(2000, 1000, 500)).toEqual({ width: 500, height: 250 })
    expect(computeScaledSize(0, 100)).toEqual({ width: 0, height: 0 })
  })
})
