import { describe, expect, it } from 'vitest'
import type { VersionArtifact, VersionManifest } from '../api/version'
import {
  compareVersions,
  platformArtifactOf,
  shouldOfferUpdate,
  updatePromptOf,
  webUpdateOf,
} from './update'

function artifact(overrides: Partial<VersionArtifact> = {}): VersionArtifact {
  return {
    version: '1.1.0',
    fileName: 'EriseChat-Setup-1.1.0.exe',
    url: '/download/files/EriseChat-Setup-1.1.0.exe',
    size: 1024,
    sha256: 'a'.repeat(64),
    modifiedAt: 1_700_000_000_000,
    ...overrides,
  }
}

function manifest(overrides: Partial<VersionManifest> = {}): VersionManifest {
  return {
    version: '1.1.0',
    protocolVersion: 1,
    artifacts: { desktop: null, android: null },
    ...overrides,
  }
}

describe('compareVersions', () => {
  it('相同版本返回 0', () => {
    expect(compareVersions('1.2.3', '1.2.3')).toBe(0)
  })

  it('补丁号更高返回 1', () => {
    expect(compareVersions('1.2.4', '1.2.3')).toBe(1)
  })

  it('次版本更低返回 -1', () => {
    expect(compareVersions('1.1.9', '1.2.0')).toBe(-1)
  })

  it('主版本优先于次版本', () => {
    expect(compareVersions('2.0.0', '1.9.9')).toBe(1)
  })

  it('非法段按 0 处理', () => {
    expect(compareVersions('1.x.3', '1.0.3')).toBe(0)
    expect(compareVersions('a.b.c', '0.0.0')).toBe(0)
  })

  it('忽略多余段', () => {
    expect(compareVersions('1.2.3.4', '1.2.3')).toBe(0)
  })
})

describe('shouldOfferUpdate', () => {
  it('最新版本更高时提示', () => {
    expect(shouldOfferUpdate('1.1.0', '1.2.0')).toBe(true)
  })

  it('相同或更低版本不提示', () => {
    expect(shouldOfferUpdate('1.1.0', '1.1.0')).toBe(false)
    expect(shouldOfferUpdate('1.2.0', '1.1.9')).toBe(false)
  })

  it('空最新版本不提示', () => {
    expect(shouldOfferUpdate('1.1.0', '')).toBe(false)
    expect(shouldOfferUpdate('1.1.0', '   ')).toBe(false)
  })
})

describe('platformArtifactOf', () => {
  it('分别返回桌面与 Android 产物', () => {
    const data = manifest({
      artifacts: { desktop: artifact(), android: artifact({ version: '1.0.0' }) },
    })
    expect(platformArtifactOf(data, 'desktop')?.fileName).toBe('EriseChat-Setup-1.1.0.exe')
    expect(platformArtifactOf(data, 'android')?.version).toBe('1.0.0')
  })

  it('清单或产物缺失返回 null', () => {
    expect(platformArtifactOf(null, 'desktop')).toBeNull()
    expect(platformArtifactOf(manifest(), 'android')).toBeNull()
  })
})

describe('updatePromptOf', () => {
  it('产物版本更高且带 sha256 时提示更新', () => {
    const prompt = updatePromptOf(
      manifest({ version: '1.2.0', artifacts: { desktop: artifact({ version: '1.2.0' }), android: null } }),
      '1.1.0',
      'desktop',
    )

    expect(prompt.available).toBe(true)
    expect(prompt.latest).toBe('1.2.0')
    expect(prompt.url).toBe('/download/files/EriseChat-Setup-1.1.0.exe')
    expect(prompt.sha256).toBe('a'.repeat(64))
  })

  it('版本相同不提示', () => {
    const prompt = updatePromptOf(
      manifest({ artifacts: { desktop: artifact(), android: null } }),
      '1.1.0',
      'desktop',
    )

    expect(prompt.available).toBe(false)
    expect(prompt.latest).toBe('1.1.0')
  })

  it('sha256 为空时不提示（无法校验下载）', () => {
    const prompt = updatePromptOf(
      manifest({ artifacts: { desktop: artifact({ version: '1.3.0', sha256: '' }), android: null } }),
      '1.1.0',
      'desktop',
    )

    expect(prompt.available).toBe(false)
  })

  it('平台无产物时不提示，展示版本回退到当前版本', () => {
    const prompt = updatePromptOf(manifest({ version: '1.3.0' }), '1.1.0', 'android')

    expect(prompt.available).toBe(false)
    expect(prompt.url).toBe('')
    expect(prompt.latest).toBe('1.1.0')
  })
})

describe('webUpdateOf', () => {
  it('webVersion 更高时提示刷新', () => {
    const prompt = webUpdateOf(manifest({ webVersion: '1.2.1' }), '1.2.0')

    expect(prompt.available).toBe(true)
    expect(prompt.latest).toBe('1.2.1')
  })

  it('缺少 webVersion 时不提示', () => {
    const prompt = webUpdateOf(manifest({ webVersion: null }), '1.2.0')

    expect(prompt.available).toBe(false)
    expect(prompt.latest).toBe('1.2.0')
  })
})
