import { describe, expect, it } from 'vitest'
import { APP_VERSION, APP_VERSION_LABEL } from './version'

// UI-05 批次2：版本号常量约束（打包前改版本时防止漏改展示文案）。
describe('APP_VERSION', () => {
  it('使用语义化版本格式 x.y.z', () => {
    expect(APP_VERSION).toMatch(/^\d+\.\d+\.\d+$/)
  })

  it('展示文案由 APP_VERSION 派生，避免页面各自拼接', () => {
    expect(APP_VERSION_LABEL).toBe(`v${APP_VERSION}`)
  })
})
