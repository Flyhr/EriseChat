import { ref } from 'vue'
import { defineStore } from 'pinia'

// 客户端本地设置（主题/提示音/桌面通知）。持久化方案 M1 统一做（localStorage 或 IndexedDB meta）。
export const useSettingsStore = defineStore('settings', () => {
  const soundEnabled = ref(true)
  const desktopNotificationsEnabled = ref(false)

  function setSoundEnabled(value: boolean): void {
    soundEnabled.value = value
  }

  function setDesktopNotificationsEnabled(value: boolean): void {
    desktopNotificationsEnabled.value = value
  }

  return {
    soundEnabled,
    desktopNotificationsEnabled,
    setSoundEnabled,
    setDesktopNotificationsEnabled,
  }
})
