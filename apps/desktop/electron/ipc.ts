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

/** 通知点击目标：主进程校验形态后缓存，渲染层消费一次即清除（F-01）。 */
export interface NotificationClickTarget {
  conversationId: string
  /** 产生通知时登录的账号 id；渲染层据此丢弃旧账号目标。 */
  userId: string
  /** 产生通知时的服务器 origin；渲染层据此丢弃旧服务器目标。 */
  serverUrl: string
}

// 缓存在主进程，渲染层重载或未登录期间不丢；同时落盘以覆盖应用重启后的冷启动消费。
let pendingNotificationClick: NotificationClickTarget | null = null

function pendingClickPath(): string {
  return path.join(app.getPath('userData'), 'pending-notification-click.json')
}

function isUserIdLike(value: unknown): value is string {
  return typeof value === 'string' && value.length > 0 && value.length <= 128
}

function readPendingClick(): NotificationClickTarget | null {
  try {
    const parsed = JSON.parse(fs.readFileSync(pendingClickPath(), 'utf8')) as Partial<NotificationClickTarget>
    const conversationId = parsed.conversationId
    const userId = parsed.userId
    if (typeof conversationId !== 'string' || !isConversationIdLike(conversationId)) return null
    if (!isUserIdLike(userId)) return null
    const serverUrl = normalizeServerUrl(parsed.serverUrl)
    if (!serverUrl) return null
    return { conversationId, userId, serverUrl }
  } catch {
    return null
  }
}

function writePendingClick(target: NotificationClickTarget | null): void {
  pendingNotificationClick = target
  try {
    if (target === null) {
      fs.rmSync(pendingClickPath(), { force: true })
      return
    }
    fs.mkdirSync(path.dirname(pendingClickPath()), { recursive: true })
    fs.writeFileSync(pendingClickPath(), JSON.stringify(target), 'utf8')
  } catch (error) {
    console.warn('[desktop] 保存通知点击目标失败', error)
  }
}

export function registerIpcHandlers(): void {
  // 冷启动：读回上次未消费的点击目标，等渲染层登录后消费一次。
  pendingNotificationClick = readPendingClick()

  ipcMain.handle('app:get-info', () => ({
    name: app.getName(),
    version: app.getVersion(),
    packaged: app.isPackaged,
  }))

  // preload 在注入 window.erisechatDesktop.serverUrl 时需同步取值（Web 启动即需要 apiBase/wsUrl）。
  ipcMain.on('settings:get-server-url-sync', (event) => {
    event.returnValue = loadSettings().serverUrl
  })

  ipcMain.handle('settings:get', () => loadSettings())
  ipcMain.handle('settings:update', (_event, patch: Partial<DesktopSettings>) => updateSettings(patch ?? {}))
  ipcMain.handle('settings:set-server-url', (_event, url: unknown) => {
    const next = updateSettings({ serverUrl: typeof url === 'string' ? url : '' })
    return { ok: true, serverUrl: next.serverUrl }
  })

  // 新消息系统通知（M4-02）：点击后聚焦主窗口并打开对应会话（F-01）。
  // 会话 ID、来源账号与服务器都在此校验形态；缺失来源的通知不携带跳转目标，
  // 避免旧账号/旧服务器的目标或任意路由参数透传给渲染进程。
  ipcMain.handle('window:notify', (_event, payload: {
    title?: string
    body?: string
    conversationId?: string
    userId?: string
    serverUrl?: string
  }) => {
    if (!Notification.isSupported()) return false
    const title = typeof payload?.title === 'string' && payload.title ? payload.title : 'EriseChat'
    const body = typeof payload?.body === 'string' ? payload.body : ''
    if (!body.trim()) return false
    const conversationId =
      typeof payload?.conversationId === 'string' && isConversationIdLike(payload.conversationId)
        ? payload.conversationId
        : null
    const userId = isUserIdLike(payload?.userId) ? payload.userId : null
    const serverUrl = normalizeServerUrl(payload?.serverUrl)
    const target: NotificationClickTarget | null =
      conversationId && userId && serverUrl ? { conversationId, userId, serverUrl } : null

    const notification = new Notification({ title, body })
    notification.on('click', () => {
      focusMainWindow()
      if (!target) return
      // 先缓存（含落盘）再通知渲染层，保证重载/重启期间点击不丢、且只消费一次。
      writePendingClick(target)
      const window = BrowserWindow.getAllWindows()[0]
      if (window && !window.isDestroyed()) {
        window.webContents.send('notification:click', conversationId)
      }
    })
    notification.show()
    return true
  })

  // 渲染层在启动、登录或收到点击信号后各调用一次；取走即清空，避免重复跳转与旧目标残留。
  ipcMain.handle('notification:consume-click', () => {
    const target = pendingNotificationClick
    if (target) writePendingClick(null)
    return target
  })

  // 全局未读数（F-02）：渲染进程上报后由主进程决定托盘/任务栏展示方式（见 registerUnreadIndicator）。
  ipcMain.on('unread:total', (_event, count: unknown) => {
    if (!unreadIndicatorHandler) return
    const total = typeof count === 'number' && Number.isFinite(count) ? Math.max(0, Math.min(9999, Math.trunc(count))) : 0
    try {
      unreadIndicatorHandler(total)
    } catch (error) {
      console.warn('[desktop] 更新未读指示失败', error)
    }
  })
}

/** 会话 ID 形态校验：与服务端一致的小写/大写 UUID 均可，避免把任意路由参数写进 URL。 */
function isConversationIdLike(value: string): boolean {
  return /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(value)
}

let unreadIndicatorHandler: ((count: number) => void) | null = null

/**
 * 注册未读指示回调（main.ts 提供实现：托盘提示/任务栏标题）。
 * 放在独立函数里是为了让 ipc.ts 不依赖主进程窗口/托盘的具体实现，便于维护。
 */
export function registerUnreadIndicator(handler: (count: number) => void): void {
  unreadIndicatorHandler = handler
}
