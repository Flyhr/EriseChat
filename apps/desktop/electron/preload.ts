import { contextBridge } from 'electron'

// preload 只通过 contextBridge 暴露白名单能力，不暴露 Node/Electron 原始对象。
// M4 接入未读角标、系统通知、窗口控制后再扩充；页面侧通过 window.eisechatDesktop 访问。
const desktopApi = {
  platform: process.platform,
  versions: {
    electron: process.versions.electron,
    chrome: process.versions.chrome,
  },
} as const

contextBridge.exposeInMainWorld('eisechatDesktop', desktopApi)

export type DesktopApi = typeof desktopApi
