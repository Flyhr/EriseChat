import { contextBridge, ipcRenderer, type IpcRendererEvent } from 'electron'

// preload 只通过 contextBridge 暴露白名单能力，不暴露 Node/Electron 原始对象。
// M4-02：服务器地址（启动时同步注入只读值 + 读写接口）、系统通知接口；页面通过 window.erisechatDesktop 访问。
// F-01/F-02：通知点击回调（主进程校验会话 ID 后转发）与全局未读数同步（托盘/任务栏指示）。
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
  /** 系统通知（窗口不可见时由 Web 调用；conversationId 用于点击后打开对应会话，主进程会校验）。 */
  notify: (payload: {
    title?: string
    body?: string
    conversationId?: string
  }): Promise<boolean> => ipcRenderer.invoke('window:notify', payload),
  /** 同步全局未读数到桌面壳（托盘提示 + 任务栏标题/角标）。 */
  setUnreadTotal: (count: number): void => {
    if (typeof count !== 'number' || !Number.isFinite(count)) return
    ipcRenderer.send('unread:total', count)
  },
  /** 订阅系统通知点击（主进程已校验会话 ID 形态），返回取消订阅函数。 */
  onNotificationClick: (callback: (conversationId: string) => void): (() => void) => {
    const handler = (_event: IpcRendererEvent, conversationId: unknown): void => {
      if (typeof conversationId === 'string' && conversationId.length > 0) {
        callback(conversationId)
      }
    }
    ipcRenderer.on('notification:click', handler)
    return () => {
      ipcRenderer.removeListener('notification:click', handler)
    }
  },
  /** F-29：检查桌面端更新（主进程请求当前服务器 /api/version 并比较版本）。 */
  checkUpdate: (): Promise<unknown> => ipcRenderer.invoke('update:check'),
  /** F-29：下载桌面安装包（主进程校验 sha256，进度通过 onUpdateProgress 推送）。 */
  downloadUpdate: (artifact: unknown): Promise<unknown> => ipcRenderer.invoke('update:download', artifact),
  /** F-29：安装已下载的安装包（静默安装后退出应用）。 */
  installUpdate: (): Promise<unknown> => ipcRenderer.invoke('update:install'),
  /** F-29：订阅下载进度，返回取消订阅函数。 */
  onUpdateProgress: (
    callback: (progress: { downloaded: number; total: number; percent: number }) => void,
  ): (() => void) => {
    const handler = (_event: IpcRendererEvent, progress: unknown): void => {
      if (!progress || typeof progress !== 'object') return
      const record = progress as Record<string, unknown>
      if (
        typeof record.downloaded !== 'number' ||
        typeof record.total !== 'number' ||
        typeof record.percent !== 'number'
      ) {
        return
      }
      callback({ downloaded: record.downloaded, total: record.total, percent: record.percent })
    }
    ipcRenderer.on('update:progress', handler)
    return () => {
      ipcRenderer.removeListener('update:progress', handler)
    }
  },
} as const

contextBridge.exposeInMainWorld('erisechatDesktop', desktopApi)

export type DesktopApi = typeof desktopApi
