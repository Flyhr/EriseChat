import { describe, expect, it } from 'vitest'
import type { SharedFileItem } from '../api/conversations'
import {
  SHARED_FILES_PAGE_SIZE,
  fileKindOf,
  formatSharedFileTime,
  mergeSharedFiles,
  toPreviewItems,
} from './sharedFiles'

function makeFile(overrides: Partial<SharedFileItem> = {}): SharedFileItem {
  return {
    fileId: 'file-1',
    name: '图片.png',
    size: 1024,
    mime: 'image/png',
    messageId: 'msg-1',
    messageSeq: 10,
    senderId: 'user-1',
    createdAt: 1_800_000_000_000,
    ...overrides,
  }
}

describe('fileKindOf', () => {
  it('图片 MIME 判为 image（大小写无关）', () => {
    expect(fileKindOf('image/png')).toBe('image')
    expect(fileKindOf('IMAGE/JPEG')).toBe('image')
  })

  it('非图片 MIME 与空值判为 file', () => {
    expect(fileKindOf('application/pdf')).toBe('file')
    expect(fileKindOf('')).toBe('file')
    expect(fileKindOf(null)).toBe('file')
    expect(fileKindOf(undefined)).toBe('file')
  })
})

describe('formatSharedFileTime', () => {
  const now = new Date(2026, 9, 28, 15, 30, 0).getTime()

  it('今天显示今天 HH:mm', () => {
    const timestamp = new Date(2026, 9, 28, 9, 5, 0).getTime()
    expect(formatSharedFileTime(timestamp, now)).toBe('今天 09:05')
  })

  it('今年非今天显示 MM-DD', () => {
    const timestamp = new Date(2026, 2, 7, 12, 0, 0).getTime()
    expect(formatSharedFileTime(timestamp, now)).toBe('03-07')
  })

  it('往年显示 YYYY-MM-DD', () => {
    const timestamp = new Date(2025, 11, 31, 23, 59, 0).getTime()
    expect(formatSharedFileTime(timestamp, now)).toBe('2025-12-31')
  })

  it('非法时间返回占位符', () => {
    expect(formatSharedFileTime(Number.NaN, now)).toBe('—')
    expect(formatSharedFileTime(0, now)).toBe('—')
    expect(formatSharedFileTime(-1, now)).toBe('—')
  })
})

describe('mergeSharedFiles', () => {
  it('追加新条目并保持已有顺序', () => {
    const first = [makeFile({ fileId: 'a', messageSeq: 5 })]
    const second = [makeFile({ fileId: 'b', messageSeq: 3 }), makeFile({ fileId: 'c', messageSeq: 1 })]
    const merged = mergeSharedFiles(first, second)
    expect(merged.map((item) => item.fileId)).toEqual(['a', 'b', 'c'])
  })

  it('按 fileId 去重，不修改入参', () => {
    const first = [makeFile({ fileId: 'a' })]
    const incoming = [makeFile({ fileId: 'a', messageSeq: 99 }), makeFile({ fileId: 'b' })]
    const copy = [...first]
    const merged = mergeSharedFiles(first, incoming)
    expect(merged.map((item) => item.fileId)).toEqual(['a', 'b'])
    expect(merged[0].messageSeq).toBe(10)
    expect(first).toEqual(copy)
  })

  it('空分页与异常条目安全处理', () => {
    expect(mergeSharedFiles([], [])).toEqual([])
    const bad = [{ fileId: '' } as SharedFileItem]
    expect(mergeSharedFiles([], bad)).toEqual([])
  })
})

describe('toPreviewItems', () => {
  it('映射为 messageId + 展示名', () => {
    const items = toPreviewItems([makeFile({ messageId: 'm1', name: 'a.png' })])
    expect(items).toEqual([{ messageId: 'm1', name: 'a.png' }])
  })

  it('缺少 messageId 的条目跳过，名称为空回退「图片」', () => {
    const items = toPreviewItems([
      makeFile({ fileId: 'x', messageId: '' }),
      makeFile({ fileId: 'y', messageId: 'm2', name: '' }),
    ])
    expect(items).toEqual([{ messageId: 'm2', name: '图片' }])
  })
})

describe('SHARED_FILES_PAGE_SIZE', () => {
  it('与服务端默认分页大小一致', () => {
    expect(SHARED_FILES_PAGE_SIZE).toBe(50)
  })
})
