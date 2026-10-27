import { describe, expect, it } from 'vitest'
import { MAX_SEND_FILES, filesOf, filesOfClipboard, hasFiles, normalizeDroppedFile, truncateFiles } from './fileDrop'

// F-06 批次 1：拖拽 / 粘贴文件提取纯函数单测（数据提取、文件名兜底、批量截断）。

const makeFile = (name: string, type = 'image/png', content = 'x'): File =>
  new File([content], name, { type, lastModified: 1700000000000 })

describe('hasFiles', () => {
  it('空数据源返回 false', () => {
    expect(hasFiles(null)).toBe(false)
    expect(hasFiles(undefined)).toBe(false)
    expect(hasFiles({})).toBe(false)
  })

  it('types 含 Files 时返回 true（拖拽过程中 files 可能为空）', () => {
    expect(hasFiles({ types: ['Files'] })).toBe(true)
    expect(hasFiles({ types: ['text/plain', 'Files'] })).toBe(true)
  })

  it('files 非空时返回 true，即使 types 不含 Files', () => {
    expect(hasFiles({ types: ['text/plain'], files: [makeFile('a.png')] })).toBe(true)
    expect(hasFiles({ files: { length: 2 } })).toBe(true)
  })

  it('只有普通文本类型时返回 false', () => {
    expect(hasFiles({ types: ['text/plain'], files: [] })).toBe(false)
  })
})

describe('filesOf', () => {
  it('空数据源返回空数组', () => {
    expect(filesOf(null)).toEqual([])
    expect(filesOf(undefined)).toEqual([])
    expect(filesOf({ files: [] })).toEqual([])
  })

  it('返回全部文件并忽略非 File 项', () => {
    const first = makeFile('a.png')
    const second = makeFile('b.txt', 'text/plain')
    const result = filesOf({ files: [first, 'not-a-file', second] })
    expect(result).toHaveLength(2)
    expect(result[0]).toBe(first)
    expect(result[1]).toBe(second)
  })
})

describe('filesOfClipboard', () => {
  it('从剪贴板提取截图文件', () => {
    const shot = makeFile('image.png')
    expect(filesOfClipboard({ types: ['Files'], files: [shot] })).toEqual([shot])
  })

  it('纯文本剪贴板返回空数组', () => {
    expect(filesOfClipboard({ types: ['text/plain'], files: [] })).toEqual([])
    expect(filesOfClipboard(null)).toEqual([])
  })
})

describe('normalizeDroppedFile', () => {
  it('已有合法文件名时原样返回', () => {
    const original = makeFile('报告.pdf', 'application/pdf')
    expect(normalizeDroppedFile(original)).toBe(original)
  })

  it('扩展名大小写不影响判断', () => {
    const original = makeFile('PHOTO.PNG')
    expect(normalizeDroppedFile(original)).toBe(original)
  })

  it('空文件名按 MIME 生成图片名并保留类型', () => {
    const normalized = normalizeDroppedFile(makeFile(''), 1700000000123)
    expect(normalized.name).toBe('粘贴图片-1700000000123.png')
    expect(normalized.type).toBe('image/png')
  })

  it('无扩展名但带 MIME 的文件按 MIME 生成扩展名', () => {
    const normalized = normalizeDroppedFile(makeFile('截图 2026', 'image/jpeg'), 88)
    expect(normalized.name).toBe('粘贴图片-88.jpg')
  })

  it('未知 MIME 回退为 .bin，交给白名单校验给出明确错误', () => {
    const normalized = normalizeDroppedFile(makeFile('', 'application/octet-stream'), 5)
    expect(normalized.name).toBe('粘贴文件-5.bin')
  })

  it('非图片 MIME 生成文件前缀', () => {
    const normalized = normalizeDroppedFile(
      makeFile('', 'application/vnd.openxmlformats-officedocument.wordprocessingml.document'),
      9,
    )
    expect(normalized.name).toBe('粘贴文件-9.docx')
  })

  it('时间戳非法时按 0 处理，不抛异常', () => {
    const normalized = normalizeDroppedFile(makeFile(''), Number.NaN)
    expect(normalized.name).toBe('粘贴图片-0.png')
  })
})

describe('truncateFiles', () => {
  it('未超过上限时全部保留且无丢弃', () => {
    const files = [makeFile('1.png'), makeFile('2.png')]
    expect(truncateFiles(files)).toEqual({ files, rejectedCount: 0 })
  })

  it('超过默认上限时保留前 N 个并返回丢弃数量', () => {
    const files = Array.from({ length: MAX_SEND_FILES + 3 }, (_, index) => makeFile(`${index}.png`))
    const result = truncateFiles(files)
    expect(result.files).toHaveLength(MAX_SEND_FILES)
    expect(result.rejectedCount).toBe(3)
    expect(result.files[0]).toBe(files[0])
  })

  it('支持自定义上限，非法上限回退默认值', () => {
    const files = [makeFile('1.png'), makeFile('2.png'), makeFile('3.png')]
    expect(truncateFiles(files, 2)).toEqual({ files: files.slice(0, 2), rejectedCount: 1 })
    expect(truncateFiles(files, 0).files).toHaveLength(1)
    expect(truncateFiles(files, Number.NaN).files).toHaveLength(3)
  })

  it('非数组输入返回空结果', () => {
    expect(truncateFiles(null as unknown as File[])).toEqual({ files: [], rejectedCount: 0 })
  })
})
