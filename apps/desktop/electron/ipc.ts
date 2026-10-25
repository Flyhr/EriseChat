import { app, BrowserWindow, ipcMain, Notification } from 'electron'
import fs from 'node:fs'
import path from 'node:path'

// 主进程 IPC（M4-02）：桌面设置读写（服务器地址/关闭到托盘/开机自启）、系统通知、应用信息。
// 渲染进程只能调用这里明确定义的通道；安全默认不变（contextIsolation/sandbox）。
export interface DesktopSettings {
  /** 服务端地址（http(s) origin；空串=渲染进程按同源/Web 默认处理） */
  serverUrl: string
  /** 点关闭按钮时隐藏到托盘（默认开启） */
  closeToTray: boolean
  /** 开机自启（默认关闭） */
  autoLaunch: boolean
}

const DEFAULT_SETTINGS: DesktopSettings = { serverUrl: '', closeToTray: true, autoLaunch: false }

let cachedSettings: DesktopSettings | null = null

function settingsPath(): string {
  return path.join(app.getPath('userData'), 'settings.json')
}

function normalizeServerUrl(raw: unknown): string | null {
  if (typeof raw !== 'string') return null
  const value = raw.trim()
  if (!value) return null
  try {
    const parsed = new URL(value)
    if (parsed.protocol !== 'http:' && parsed.protocol !== 'https:') return null
    if (!parsed.hostname) return null
    return parsed.origin
  } catch {
    return null
  }
}

function loadSettings(): DesktopSettings {
  if (cachedSettings) return cachedSettings
  try {
    const parsed = JSON.parse(fs.readFileSync(settingsPath(), 'utf8')) as Partial<DesktopSettings>
    cachedSettings = {
      serverUrl: normalizeServerUrl(parsed.serverUrl) ?? '',
      closeToTray: parsed.closeToTray !== false,
      autoLaunch: parsed.autoLaunch === true,
    }
  } catch {
    cachedSettings = { ...DEFAULT_SETTINGS }
  }
  return cachedSettings
}

function persistSettings(next: DesktopSettings): void {
  cachedSettings = next
  try {
    fs.mkdirSync(path.dirname(settingsPath()), { recursive: true })
    fs.writeFileSync(settingsPath(), JSON.stringify(next, null, 2), 'utf8')
  } catch (error) {
    console.warn('[desktop] 保存设置失败', error)
  }
}

export function getSettings(): DesktopSettings {
  return loadSettings()
}

export function updateSettings(patch: Partial<DesktopSettings>): DesktopSettings {
  const current = loadSettings()
  const next: DesktopSettings = {
    serverUrl: patch.serverUrl !== undefined ? (normalizeServerUrl(patch.serverUrl) ?? '') : current.serverUrl,
    closeToTray: patch.closeToTray !== undefined ? patch.closeToTray === true : current.closeToTray,
    autoLaunch: patch.autoLaunch !== undefined ? patch.autoLaunch === true : current.autoLaunch,
  }
  persistSettings(next)
  if (patch.autoLaunch !== undefined && process.platform === 'win32') {
    try {
      app.setLoginItemSettings({ openAtLogin: next.autoLaunch, path: process.execPath })
    } catch (error) {
      console.warn('[desktop] 更新开机自启失败', error)
    }
  }
  return next
}

function focusMainWindow(): void {
  const window = BrowserWindow.getAllWindows()[0]
  if (!window) return
  if (window.isMinimized()) window.restore()
  window.show()
  window.focus()
}

export function registerIpcHandlers(): void {
  ipcMain.handle('app:get-info', () => ({
    name: app.getName(),
    version: app.getVersion(),
    packaged: app.isPackaged,
  }))

  // preload 在注入 window.eisechatDesktop.serverUrl 时需同步取值（Web 启动即需要 apiBase/wsUrl）。
  ipcMain.on('settings:get-server-url-sync', (event) => {
    event.returnValue = loadSettings().serverUrl
  })

  ipcMain.handle('settings:get', () => loadSettings())
  ipcMain.handle('settings:update', (_event, patch: Partial<DesktopSettings>) => updateSettings(patch ?? {}))
  ipcMain.handle('settings:set-server-url', (_event, url: unknown) => {
    const next = updateSettings({ serverUrl: typeof url === 'string' ? url : '' })
    return { ok: true, serverUrl: next.serverUrl }
  })

  // 新消息系统通知（M4-02）：仅应用运行期间有效；点击通知聚焦主窗口（不点具体会话，避免越层导航）。
  ipcMain.handle('window:notify', (_event, payload: { title?: string; body?: string }) => {
    if (!Notification.isSupported()) return false
    const title = typeof payload?.title === 'string' && payload.title ? payload.title : 'EiseChat'
    const body = typeof payload?.body === 'string' ? payload.body : ''
    if (!body.trim()) return false
    const notification = new Notification({ title, body })
    notification.on('click', () => focusMainWindow())
    notification.show()
    return true
  })
}
