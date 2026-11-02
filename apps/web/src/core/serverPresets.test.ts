import { describe, expect, it } from 'vitest'
import {
  DEFAULT_SERVER_PRESET_LIST,
  findServerPreset,
  parseServerPresets,
  type ServerPreset,
} from './serverPresets'

describe('parseServerPresets', () => {
  it('解析「标签=地址」列表并规范化 origin', () => {
    const presets = parseServerPresets('服务器=https://chat.example.com/api/,本机调试=http://127.0.0.1:6580')
    expect(presets).toEqual([
      { label: '服务器', url: 'https://chat.example.com' },
      { label: '本机调试', url: 'http://127.0.0.1:6580' },
    ])
  })

  it('地址可以包含等号（按第一个等号切分）', () => {
    const presets = parseServerPresets('内网=http://192.168.1.23:6580/?a=1&b=2')
    expect(presets).toEqual([{ label: '内网', url: 'http://192.168.1.23:6580' }])
  })

  it('空值/空白回退默认预设', () => {
    expect(parseServerPresets(undefined)).toBe(DEFAULT_SERVER_PRESET_LIST)
    expect(parseServerPresets('   ')).toBe(DEFAULT_SERVER_PRESET_LIST)
  })

  it('非法条目被忽略；全部非法时回退默认预设', () => {
    expect(parseServerPresets('没有等号,=https://a.example.com,坏地址=ftp://x,好=https://b.example.com')).toEqual([
      { label: '好', url: 'https://b.example.com' },
    ])
    expect(parseServerPresets('没有等号,坏地址=ftp://x')).toBe(DEFAULT_SERVER_PRESET_LIST)
  })
})

describe('findServerPreset', () => {
  const presets: readonly ServerPreset[] = [
    { label: '服务器', url: 'https://chat.example.com' },
    { label: '调试', url: 'http://127.0.0.1:6580' },
  ]

  it('按 origin 匹配，忽略 path/query/尾斜杠', () => {
    expect(findServerPreset(presets, 'https://chat.example.com/')?.label).toBe('服务器')
    expect(findServerPreset(presets, 'http://127.0.0.1:6580/api/health')?.label).toBe('调试')
  })

  it('不匹配或非法地址返回 undefined', () => {
    expect(findServerPreset(presets, 'https://other.example.com')).toBeUndefined()
    expect(findServerPreset(presets, 'not-a-url')).toBeUndefined()
    expect(findServerPreset(presets, '')).toBeUndefined()
  })
})
