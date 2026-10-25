import { contextBridge, ipcRenderer } from 'electron'

// preload 只通过 contextBridge 暴露白名单能力，不暴露 Node/Electron 原始对象。
// M4-02：服务器地址（启动时同步注入只读值 + 读写接口）、系统通知接口；页面通过 window.eisechatDesktop 访问。
const desktopApi = {
  platform: process.platform,
  versions: {
    electron: process.versions.electron,
    chrome: process.versions.chrome,
  },
  /** 已保存的服务器地址（启动时同步读取，Web 初始化 apiBase/wsUrl 用；空串=同源/Web 默认）。 */
  serverUrl: ipcRenderer.sendSync('settings:get-server-url-sync') as string,
  /** 读取桌面设置（serverUrl/closeToTray/autoLaunch）。 */
  getSettings: (): Promise<unknown> => ipcRenderer.invoke('settings:get'),
  /** 保存服务器地址（返回是否保存成功与规范化后的地址）。 */
  setServerUrl: (url: string): Promise<{ ok: boolean; serverUrl: string }> =>
    ipcRenderer.invoke('settings:set-server-url', url),
  /** 系统通知（窗口不可见时由 Web 调用；点击通知聚焦主窗口）。 */
  notify: (payload: { title?: string; body?: string }): Promise<boolean> =>
    ipcRenderer.invoke('window:notify', payload),
} as const

contextBridge.exposeInMainWorld('eisechatDesktop', desktopApi)

export type DesktopApi = typeof desktopApi
