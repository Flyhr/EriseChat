import { describe, expect, it } from 'vitest'
import { formatLastSeen, formatSessionTime, shortDeviceLabel } from './sessionFormat'

const DAY = 24 * 60 * 60 * 1000

describe('formatSessionTime', () => {
  it('有效时间输出本地 YYYY-MM-DD HH:mm', () => {
    const timestamp = new Date(2026, 0, 2, 3, 4).getTime()
    expect(formatSessionTime(timestamp)).toBe('2026-01-02 03:04')
  })

  it('非法时间返回占位符', () => {
    expect(formatSessionTime(0)).toBe('—')
    expect(formatSessionTime(Number.NaN)).toBe('—')
  })
})

describe('formatLastSeen', () => {
  const now = Date.UTC(2026, 0, 10, 12, 0, 0)

  it('一分钟内显示刚刚', () => {
    expect(formatLastSeen(now - 30_000, now)).toBe('刚刚')
  })

  it('一小时内显示分钟', () => {
    expect(formatLastSeen(now - 5 * 60_000, now)).toBe('5 分钟前')
  })

  it('一天内显示小时', () => {
    expect(formatLastSeen(now - 3 * 60 * 60_000, now)).toBe('3 小时前')
  })

  it('七天内显示天', () => {
    expect(formatLastSeen(now - 2 * DAY, now)).toBe('2 天前')
  })

  it('超过七天回退到绝对时间', () => {
    const lastSeen = now - 10 * DAY
    expect(formatLastSeen(lastSeen, now)).toBe(formatSessionTime(lastSeen))
  })

  it('未来时间按刚刚处理，非法值返回占位符', () => {
    expect(formatLastSeen(now + 10_000, now)).toBe('刚刚')
    expect(formatLastSeen(0, now)).toBe('—')
  })
})

describe('shortDeviceLabel', () => {
  it('空值返回未知设备', () => {
    expect(shortDeviceLabel('')).toBe('未知设备')
    expect(shortDeviceLabel('   ')).toBe('未知设备')
  })

  it('短标识原样返回，长标识保留前 10 位与后 4 位', () => {
    expect(shortDeviceLabel('device-abc')).toBe('device-abc')
    const long = 'abcdefghijklmnopqrstuvwxyz'
    expect(shortDeviceLabel(long)).toBe('abcdefghij…wxyz')
  })

  it('非法 maxLength 回退默认值', () => {
    const long = 'a'.repeat(30)
    expect(shortDeviceLabel(long, Number.NaN)).toBe(`${'a'.repeat(10)}…${'a'.repeat(4)}`)
  })
})
