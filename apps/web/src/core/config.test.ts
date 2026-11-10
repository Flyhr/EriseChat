// 运行配置单测（M2-07）：默认值与重连退避（AGENTS.md §5.8）。
import { describe, expect, it } from 'vitest'
import { config, reconnectDelayMs } from './config'

describe('config 默认值', () => {
  it('协议/心跳/补拉参数与协议约定一致', () => {
    expect(config.protocolVersion).toBe(1)
    expect(config.heartbeatSec).toBe(25)
    expect(config.heartbeatTimeoutSec).toBe(30)
    expect([...config.reconnectDelaysMs]).toEqual([1000, 2000, 5000, 10000, 30000])
    expect(config.syncBatchSize).toBe(200)
    expect(config.dbName).toBe('erisechat')
  })
})

describe('reconnectDelayMs', () => {
  it('按 1/2/5/10/30s 退避，超出后保持 30s', () => {
    const bases = [0, 1, 2, 3, 4, 5, 99].map((attempt) => reconnectDelayMs(attempt, () => 0))
    expect(bases).toEqual([1000, 2000, 5000, 10000, 30000, 30000, 30000])
  })

  it('负数 attempt 归一为第 0 档', () => {
    expect(reconnectDelayMs(-1, () => 0)).toBe(1000)
  })

  it('叠加 0~499ms 随机抖动', () => {
    expect(reconnectDelayMs(0, () => 0)).toBe(1000)
    expect(reconnectDelayMs(0, () => 0.999)).toBe(1499)
    expect(reconnectDelayMs(5, () => 0.5)).toBe(30250)
  })

  it('random 为 1 时抖动上限不超过 499ms（随机源约定 <1）', () => {
    expect(reconnectDelayMs(1, () => 0.9999)).toBe(2499)
  })
})
