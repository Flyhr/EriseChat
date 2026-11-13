import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { createFakeStorage, createMemoryStorage } from '../test/fixtures'
import { SAVED_LOGIN_KEY, clearLegacySavedLogin } from './savedCredentials'

/** 1.2.0 命名迁移前的历史键。 */
const LEGACY_SAVED_LOGIN_KEY = 'eisechat.savedLogin'

let storage: Storage

beforeEach(() => {
  storage = createMemoryStorage()
  vi.stubGlobal('localStorage', storage)
})

afterEach(() => {
  vi.unstubAllGlobals()
})

describe('clearLegacySavedLogin', () => {
  it('清除新旧两个历史键', () => {
    storage.setItem(SAVED_LOGIN_KEY, 'stale')
    storage.setItem(LEGACY_SAVED_LOGIN_KEY, 'stale')

    clearLegacySavedLogin()

    expect(storage.getItem(SAVED_LOGIN_KEY)).toBeNull()
    expect(storage.getItem(LEGACY_SAVED_LOGIN_KEY)).toBeNull()
  })

  it('不读取、不解析、不写回旧值，只按键名删除', () => {
    // 故意放损坏内容：清理过程若读取或解析旧值就会暴露。
    storage.setItem(SAVED_LOGIN_KEY, '{not-json')
    storage.setItem(LEGACY_SAVED_LOGIN_KEY, '{not-json')
    const readSpy = vi.spyOn(storage, 'getItem')
    const writeSpy = vi.spyOn(storage, 'setItem')
    const removeSpy = vi.spyOn(storage, 'removeItem')

    expect(() => clearLegacySavedLogin()).not.toThrow()

    expect(readSpy).not.toHaveBeenCalled()
    expect(writeSpy).not.toHaveBeenCalled()
    expect(removeSpy).toHaveBeenCalledTimes(2)
    expect(removeSpy).toHaveBeenCalledWith(SAVED_LOGIN_KEY)
    expect(removeSpy).toHaveBeenCalledWith(LEGACY_SAVED_LOGIN_KEY)
    expect(storage.getItem(SAVED_LOGIN_KEY)).toBeNull()
    expect(storage.getItem(LEGACY_SAVED_LOGIN_KEY)).toBeNull()
  })

  it('无旧记录时幂等且不抛错', () => {
    expect(() => clearLegacySavedLogin()).not.toThrow()
    expect(() => clearLegacySavedLogin()).not.toThrow()
  })

  it('存储读写不可用时仍能清理且不抛错', () => {
    const broken = createFakeStorage(
      { [SAVED_LOGIN_KEY]: 'stale', [LEGACY_SAVED_LOGIN_KEY]: 'stale' },
      { failRead: true, failWrite: true },
    )
    vi.stubGlobal('localStorage', broken)

    expect(() => clearLegacySavedLogin()).not.toThrow()
    expect(broken.data.has(SAVED_LOGIN_KEY)).toBe(false)
    expect(broken.data.has(LEGACY_SAVED_LOGIN_KEY)).toBe(false)
  })

  it('存储访问本身抛错时静默降级', () => {
    vi.stubGlobal('localStorage', {
      get removeItem(): unknown {
        throw new Error('storage unavailable')
      },
    })

    expect(() => clearLegacySavedLogin()).not.toThrow()
  })
})
