import { ref } from 'vue'
import { defineStore } from 'pinia'

/** 纯客户端偏好（提示音、桌面通知）的 localStorage key；服务端不感知，也不跨设备同步。 */
export const SETTINGS_STORAGE_KEY = 'eisechat.settings'

/** 客户端本地设置集合（未来可扩展消息字号、主题等纯视觉偏好）。 */
export interface ClientSettings {
  /** 收到新消息时播放短提示音。 */
  soundEnabled: boolean
  /** 窗口不可见时弹系统通知（桌面壳走 preload，Web 版走浏览器 Notification）。 */
  desktopNotificationsEnabled: boolean
}

/** 默认值：提示音开启、系统通知关闭（需要用户显式授权/开启）。 */
export const DEFAULT_SETTINGS: ClientSettings = {
  soundEnabled: true,
  desktopNotificationsEnabled: false,
}

/** 解析 localStorage 中保存的原始设置：缺失、损坏或类型不符时回退默认值。 */
export function parseStoredSettings(raw: string | null | undefined): ClientSettings {
  const result: ClientSettings = { ...DEFAULT_SETTINGS }
  if (!raw) return result
  try {
    const parsed = JSON.parse(raw) as Record<string, unknown> | null
    if (typeof parsed !== 'object' || parsed === null) return result
    if (typeof parsed.soundEnabled === 'boolean') result.soundEnabled = parsed.soundEnabled
    if (typeof parsed.desktopNotificationsEnabled === 'boolean') {
      result.desktopNotificationsEnabled = parsed.desktopNotificationsEnabled
    }
  } catch {
    // 损坏的 JSON 按默认值处理，下一次保存会覆盖。
  }
  return result
}

function readStoredSettings(): ClientSettings {
  try {
    return parseStoredSettings(window.localStorage.getItem(SETTINGS_STORAGE_KEY))
  } catch {
    return { ...DEFAULT_SETTINGS }
  }
}

function writeStoredSettings(settings: ClientSettings): void {
  try {
    window.localStorage.setItem(SETTINGS_STORAGE_KEY, JSON.stringify(settings))
  } catch {
    // localStorage 不可用（隐私模式/配额满）：仅本次会话生效，不阻断设置操作。
  }
}

// 客户端本地设置（提示音/桌面通知）：持久化到 localStorage，只保存本机偏好，
// 不写入服务端、不同步到其他设备；登出不清除（属于本机 UI 偏好而非账号数据）。
export const useSettingsStore = defineStore('settings', () => {
  const stored = readStoredSettings()
  const soundEnabled = ref(stored.soundEnabled)
  const desktopNotificationsEnabled = ref(stored.desktopNotificationsEnabled)

  function persist(): void {
    writeStoredSettings({
      soundEnabled: soundEnabled.value,
      desktopNotificationsEnabled: desktopNotificationsEnabled.value,
    })
  }

  function setSoundEnabled(value: boolean): void {
    if (soundEnabled.value === value) return
    soundEnabled.value = value
    persist()
  }

  function setDesktopNotificationsEnabled(value: boolean): void {
    if (desktopNotificationsEnabled.value === value) return
    desktopNotificationsEnabled.value = value
    persist()
  }

  return {
    soundEnabled,
    desktopNotificationsEnabled,
    setSoundEnabled,
    setDesktopNotificationsEnabled,
  }
})
