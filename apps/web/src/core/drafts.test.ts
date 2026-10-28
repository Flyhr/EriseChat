// 草稿存储单测（F-04-01）：纯函数 + 注入式假 storage，覆盖往返、脏数据过滤、容量裁剪与降级。
import { describe, expect, it } from 'vitest'
import { MAX_TEXT_LENGTH } from './config'
import {
  DRAFTS_MAX_CONVERSATIONS,
  DRAFTS_STORAGE_KEY,
  parseDrafts,
  pruneDrafts,
  readDrafts,
  serializeDrafts,
  writeDrafts,
  type DraftMap,
} from './drafts'
import { createFakeStorage } from '../test/fixtures'

function entry(text: string, updatedAt: number): { text: string; updatedAt: number } {
  return { text, updatedAt }
}

describe('parseDrafts / serializeDrafts', () => {
  it('序列化后可完整往返', () => {
    const map: DraftMap = {
      'conv-1': entry('第一行\n第二行 ', 1000),
      'conv-2': entry('emoji 😀 中文', 2000),
    }
    expect(parseDrafts(serializeDrafts(map))).toEqual(map)
  })

  it('非法 JSON 与非对象内容返回空对象', () => {
    expect(parseDrafts(null)).toEqual({})
    expect(parseDrafts('')).toEqual({})
    expect(parseDrafts('{oops')).toEqual({})
    expect(parseDrafts('[]')).toEqual({})
    expect(parseDrafts('42')).toEqual({})
    expect(parseDrafts('"text"')).toEqual({})
    expect(parseDrafts('null')).toEqual({})
  })

  it('逐条过滤无效项，保留有效项', () => {
    const raw = JSON.stringify({
      '': entry('空会话 id 应丢弃', 1),
      'bad-text-type': { text: 123, updatedAt: 2 },
      'empty-text': entry('', 3),
      'too-long': entry('x'.repeat(MAX_TEXT_LENGTH + 1), 4),
      'bad-time': { text: '时间非法', updatedAt: Number.NaN },
      'bad-time-type': { text: '时间非数字', updatedAt: '5' },
      'not-object': '直接字符串',
      good: entry('保留的草稿', 6),
    })
    expect(parseDrafts(raw)).toEqual({ good: entry('保留的草稿', 6) })
  })

  it('文本长度边界：恰好上限保留，超 1 字符丢弃', () => {
    const raw = JSON.stringify({
      atLimit: entry('x'.repeat(MAX_TEXT_LENGTH), 1),
      overLimit: entry('x'.repeat(MAX_TEXT_LENGTH + 1), 2),
    })
    expect(Object.keys(parseDrafts(raw))).toEqual(['atLimit'])
  })
})

describe('readDrafts / writeDrafts', () => {
  it('写入后可读回，且使用约定的 key', () => {
    const storage = createFakeStorage()
    const map: DraftMap = { 'conv-1': entry('草稿', 100) }
    writeDrafts(map, storage)
    expect(storage.data.has(DRAFTS_STORAGE_KEY)).toBe(true)
    expect(readDrafts(storage)).toEqual(map)
  })

  it('读取损坏内容返回空对象', () => {
    const storage = createFakeStorage()
    storage.data.set(DRAFTS_STORAGE_KEY, '{not json')
    expect(readDrafts(storage)).toEqual({})
  })

  it('写入失败静默（不抛错），读取失败也静默', () => {
    const writeFail = createFakeStorage({}, { failWrite: true })
    expect(() => writeDrafts({ 'conv-1': entry('草稿', 1) }, writeFail)).not.toThrow()
    const readFail = createFakeStorage({}, { failRead: true })
    expect(readDrafts(readFail)).toEqual({})
  })

  it('storage 为 null（禁用存储）时读写均静默降级', () => {
    expect(readDrafts(null)).toEqual({})
    expect(() => writeDrafts({ 'conv-1': entry('草稿', 1) }, null)).not.toThrow()
  })

  it('removeItem 清理 key 后读取为空', () => {
    const storage = createFakeStorage()
    writeDrafts({ 'conv-1': entry('草稿', 1) }, storage)
    storage.removeItem(DRAFTS_STORAGE_KEY)
    expect(readDrafts(storage)).toEqual({})
  })
})

describe('pruneDrafts', () => {
  it('按 updatedAt 保留最近 limit 个会话', () => {
    const map: DraftMap = {}
    for (let index = 1; index <= 5; index += 1) {
      map[`conv-${index}`] = entry(`草稿${index}`, index * 100)
    }
    expect(Object.keys(pruneDrafts(map, 3)).sort()).toEqual(['conv-3', 'conv-4', 'conv-5'])
  })

  it('默认上限为 DRAFTS_MAX_CONVERSATIONS（20）', () => {
    const map: DraftMap = {}
    for (let index = 0; index < DRAFTS_MAX_CONVERSATIONS + 5; index += 1) {
      map[`conv-${index}`] = entry('草稿', index)
    }
    const pruned = pruneDrafts(map)
    expect(Object.keys(pruned)).toHaveLength(DRAFTS_MAX_CONVERSATIONS)
    expect(pruned['conv-0']).toBeUndefined()
    expect(pruned[`conv-${DRAFTS_MAX_CONVERSATIONS + 4}`]).toBeDefined()
  })

  it('不修改入参历史对象，且 limit 非法时至少保留 1 条', () => {
    const map: DraftMap = { a: entry('A', 2), b: entry('B', 1) }
    const pruned = pruneDrafts(map, 0)
    expect(Object.keys(pruned)).toEqual(['a'])
    expect(Object.keys(map)).toEqual(['a', 'b'])
  })
})
