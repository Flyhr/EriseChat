import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { createMemoryStorage } from '../test/fixtures'
import { createPinia, setActivePinia } from 'pinia'
import {
  DEFAULT_SETTINGS,
  SETTINGS_STORAGE_KEY,
  parseStoredSettings,
  useSettingsStore,
} from './settings'

let storage: Storage

beforeEach(() => {
  storage = createMemoryStorage()
  vi.stubGlobal('localStorage', storage)
  vi.stubGlobal('window', { localStorage: storage })
  setActivePinia(createPinia())
})

afterEach(() => {
  vi.unstubAllGlobals()
})

describe('parseStoredSettings', () => {
  it('缺失或空值时回退默认值', () => {
    expect(parseStoredSettings(null)).toEqual(DEFAULT_SETTINGS)
    expect(parseStoredSettings('')).toEqual(DEFAULT_SETTINGS)
  })

  it('损坏的 JSON 回退默认值', () => {
    expect(parseStoredSettings('{not-json')).toEqual(DEFAULT_SETTINGS)
  })

  it('只保存了部分字段时其余字段用默认值', () => {
    expect(parseStoredSettings('{"soundEnabled":false}')).toEqual({
      soundEnabled: false,
      desktopNotificationsEnabled: false,
      androidBackgroundNotificationsEnabled: false,
      messageFontSize: 'medium',
      listDensity: 'comfortable',
    })
  })

  it('字段类型不符时忽略该字段', () => {
    expect(parseStoredSettings('{"soundEnabled":"no","desktopNotificationsEnabled":1}')).toEqual(
      DEFAULT_SETTINGS,
    )
  })

  it('正确处理三个布尔字段（含 Android 后台通知开关）', () => {
    expect(
      parseStoredSettings(
        '{"soundEnabled":false,"desktopNotificationsEnabled":true,"androidBackgroundNotificationsEnabled":true}',
      ),
    ).toEqual({
      soundEnabled: false,
      desktopNotificationsEnabled: true,
      androidBackgroundNotificationsEnabled: true,
      messageFontSize: 'medium',
      listDensity: 'comfortable',
    })
  })
})

describe('useSettingsStore 持久化', () => {
  it('无本地记录时使用默认值', () => {
    const settings = useSettingsStore()
    expect(settings.soundEnabled).toBe(DEFAULT_SETTINGS.soundEnabled)
    expect(settings.desktopNotificationsEnabled).toBe(
      DEFAULT_SETTINGS.desktopNotificationsEnabled,
    )
  })

  it('切换开关后写入 localStorage，重新创建 store 能读回', () => {
    const settings = useSettingsStore()
    settings.setSoundEnabled(false)
    settings.setDesktopNotificationsEnabled(true)

    expect(JSON.parse(storage.getItem(SETTINGS_STORAGE_KEY) ?? '{}')).toEqual({
      soundEnabled: false,
      desktopNotificationsEnabled: true,
      androidBackgroundNotificationsEnabled: false,
      messageFontSize: 'medium',
      listDensity: 'comfortable',
    })

    // 模拟刷新页面：新的 pinia 实例重新创建 store，应从 localStorage 恢复。
    setActivePinia(createPinia())
    const restored = useSettingsStore()
    expect(restored.soundEnabled).toBe(false)
    expect(restored.desktopNotificationsEnabled).toBe(true)
  })

  it('localStorage 不可用时不抛异常，设置仅在内存生效', () => {
    vi.stubGlobal('window', {
      localStorage: {
        getItem: () => {
          throw new Error('blocked')
        },
        setItem: () => {
          throw new Error('blocked')
        },
      },
    })
    const settings = useSettingsStore()
    expect(() => settings.setSoundEnabled(false)).not.toThrow()
    expect(settings.soundEnabled).toBe(false)
  })
})
