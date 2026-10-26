import { app, BrowserWindow, Menu, Tray, nativeImage, shell, type NativeImage } from 'electron'
import fs from 'node:fs'
import path from 'node:path'
import { getSettings, registerIpcHandlers, registerUnreadIndicator } from './ipc'

// EiseChat Windows 桌面壳（M0 骨架 → M4-02 托盘/系统通知/窗口记忆/关闭到托盘）。
// 只负责窗口生命周期、托盘、通知与白名单 IPC，不写聊天业务逻辑（AGENTS.md §3 / project-overview §7.3、§10）。
// 安全默认：contextIsolation=true、nodeIntegration=false、sandbox=true；页面跳转/弹窗仅允许配置的服务器与本地文件。

const DEV_SERVER_URL = process.env.EISECHAT_DEV_SERVER_URL ?? 'http://127.0.0.1:5173'
const MIN_WIDTH = 960
const MIN_HEIGHT = 640

// Windows 系统通知需要一个 Application User Model ID 才能正确归属到本应用；
// 必须与 electron-builder.yml 的 appId 一致（开始菜单快捷方式的 AUMID 由安装包写入）。
const APP_USER_MODEL_ID = 'com.eisechat.desktop'
app.setAppUserModelId(APP_USER_MODEL_ID)

interface WindowState {
  width: number
  height: number
  x?: number
  y?: number
  maximized: boolean
}

let mainWindow: BrowserWindow | null = null
let tray: Tray | null = null
let isQuitting = false
let windowState = loadWindowState()

// 未读提醒（F-02）：托盘图标在「有未读且窗口不在前台」时闪烁，窗口回到前台或读完即停止。
let unreadTotal = 0
let trayNormalImage: NativeImage | null = null
let trayAlertImage: NativeImage | null = null
let trayBlinkTimer: NodeJS.Timeout | null = null
let trayBlinkShowingAlert = false
const TRAY_BLINK_INTERVAL_MS = 500

function windowStatePath(): string {
  return path.join(app.getPath('userData'), 'window-state.json')
}

/** 读取上次窗口大小/位置；文件缺失或损坏时回退默认值（不影响启动）。 */
function loadWindowState(): WindowState {
  try {
    const parsed = JSON.parse(fs.readFileSync(windowStatePath(), 'utf8')) as Partial<WindowState>
    return {
      width: Math.max(MIN_WIDTH, Math.round(Number(parsed.width) || 1200)),
      height: Math.max(MIN_HEIGHT, Math.round(Number(parsed.height) || 800)),
      x: typeof parsed.x === 'number' ? parsed.x : undefined,
      y: typeof parsed.y === 'number' ? parsed.y : undefined,
      maximized: parsed.maximized === true,
    }
  } catch {
    return { width: 1200, height: 800, maximized: false }
  }
}

/** 保存窗口大小/位置（关闭/退出前调用；最小化时保留还原后的边界）。 */
function saveWindowState(window: BrowserWindow): void {
  if (window.isMinimized()) return
  const bounds = window.getNormalBounds()
  windowState = {
    width: bounds.width,
    height: bounds.height,
    x: bounds.x,
    y: bounds.y,
    maximized: window.isMaximized(),
  }
  try {
    fs.writeFileSync(windowStatePath(), JSON.stringify(windowState, null, 2), 'utf8')
  } catch (error) {
    console.warn('[desktop] 保存窗口状态失败', error)
  }
}

/** 允许页面导航的来源：本地打包文件、开发服务器、已配置的服务器 origin。 */
function isAllowedNavigation(targetUrl: string): boolean {
  if (targetUrl.startsWith('file://')) return true
  if (!app.isPackaged && targetUrl.startsWith(DEV_SERVER_URL)) return true
  const serverUrl = getSettings().serverUrl
  if (!serverUrl) return false
  try {
    return new URL(targetUrl).origin === new URL(serverUrl).origin
  } catch {
    return false
  }
}

function showMainWindow(): void {
  if (!mainWindow) {
    mainWindow = createMainWindow()
  }
  if (!mainWindow.isVisible()) mainWindow.show()
  if (mainWindow.isMinimized()) mainWindow.restore()
  mainWindow.focus()
}

function createMainWindow(): BrowserWindow {
  const window = new BrowserWindow({
    width: windowState.width,
    height: windowState.height,
    x: windowState.x,
    y: windowState.y,
    minWidth: MIN_WIDTH,
    minHeight: MIN_HEIGHT,
    show: false,
    autoHideMenuBar: true,
    webPreferences: {
      preload: path.join(__dirname, 'preload.js'),
      contextIsolation: true,
      nodeIntegration: false,
      sandbox: true,
    },
  })

  if (windowState.maximized) window.maximize()
  window.once('ready-to-show', () => window.show())

  // 窗口回到前台（显示/聚焦/还原）时停止托盘与任务栏闪烁；最小化/失焦后若仍有未读则继续提示。
  const refreshUnreadAlert = (): void => applyUnreadIndicator(unreadTotal)
  window.on('focus', refreshUnreadAlert)
  window.on('show', refreshUnreadAlert)
  window.on('restore', refreshUnreadAlert)
  window.on('blur', refreshUnreadAlert)
  window.on('minimize', refreshUnreadAlert)
  window.on('hide', refreshUnreadAlert)

  // 内部应用：页面不得自行打开新窗口或跳转到未知来源；外链交给系统浏览器。
  window.webContents.setWindowOpenHandler(({ url }) => {
    if (url.startsWith('http://') || url.startsWith('https://')) {
      void shell.openExternal(url)
    }
    return { action: 'deny' }
  })
  window.webContents.on('will-navigate', (event, url) => {
    if (!isAllowedNavigation(url)) {
      event.preventDefault()
    }
  })

  // 关闭到托盘（默认开启，可在 desktop settings.json 关闭）：点关闭按钮隐藏窗口而非退出。
  window.on('close', (event) => {
    saveWindowState(window)
    if (isQuitting || !getSettings().closeToTray) return
    event.preventDefault()
    window.hide()
  })

  window.on('closed', () => {
    mainWindow = null
  })

  if (app.isPackaged) {
    // 打包态：apps/web 构建产物由 electron-builder 复制到 resources/web-dist（见 electron-builder.yml）。
    void window.loadFile(path.join(process.resourcesPath, 'web-dist', 'index.html'))
  } else {
    // 开发态：加载 `cd apps/web && npm run dev` 启动的 Vite 开发服务器。
    void window.loadURL(DEV_SERVER_URL)
  }

  return window
}

/** 托盘图标路径：打包态在 resources/，开发态在 apps/desktop/build/。 */
function trayIconPath(fileName: string): string {
  return app.isPackaged
    ? path.join(process.resourcesPath, fileName)
    : path.join(__dirname, '..', 'build', fileName)
}

/** 系统托盘：显示主窗口 / 退出；点击托盘图标聚焦窗口；有未读且窗口不在前台时图标闪烁。 */
function createTray(): void {
  trayNormalImage = nativeImage.createFromPath(trayIconPath('icon.ico'))
  trayAlertImage = nativeImage.createFromPath(trayIconPath('tray-alert.png'))
  if (trayNormalImage.isEmpty()) trayNormalImage = nativeImage.createEmpty()
  if (trayAlertImage.isEmpty()) trayAlertImage = null

  tray = new Tray(trayNormalImage)
  tray.setToolTip('EiseChat')
  tray.setContextMenu(
    Menu.buildFromTemplate([
      { label: '显示主窗口', click: () => showMainWindow() },
      { type: 'separator' },
      {
        label: '退出 EiseChat',
        click: () => {
          isQuitting = true
          app.quit()
        },
      },
    ]),
  )
  tray.on('click', () => showMainWindow())
}

/** 主窗口是否在前台：可见、未最小化且已获得焦点。 */
function isMainWindowActive(): boolean {
  if (!mainWindow || mainWindow.isDestroyed()) return false
  return mainWindow.isVisible() && !mainWindow.isMinimized() && mainWindow.isFocused()
}

/** 停止托盘闪烁并恢复常态图标。 */
function stopTrayBlink(): void {
  if (trayBlinkTimer) {
    clearInterval(trayBlinkTimer)
    trayBlinkTimer = null
  }
  trayBlinkShowingAlert = false
  if (tray && trayNormalImage) {
    try {
      tray.setImage(trayNormalImage)
    } catch (error) {
      console.warn('[desktop] 恢复托盘图标失败', error)
    }
  }
}

/** 有未读且窗口不在前台时，让托盘图标从未读提醒图标与常态图标之间闪烁。 */
function startTrayBlink(): void {
  if (trayBlinkTimer || !tray || !trayAlertImage) return
  trayBlinkTimer = setInterval(() => {
    if (!tray || !trayAlertImage || !trayNormalImage) return
    trayBlinkShowingAlert = !trayBlinkShowingAlert
    try {
      tray.setImage(trayBlinkShowingAlert ? trayAlertImage : trayNormalImage)
    } catch (error) {
      console.warn('[desktop] 托盘图标闪烁失败', error)
    }
  }, TRAY_BLINK_INTERVAL_MS)
}

/**
 * 全局未读指示（F-02）：托盘提示始终可用；macOS/Linux 用系统角标；Windows 退化为窗口标题数字。
 * 窗口不在前台时托盘图标闪烁 + 任务栏按钮闪烁，提示有新消息；回到前台或未读归零后停止。
 * 未读数由渲染进程经 IPC `unread:total` 推送，主进程只负责展示，不保存业务状态。
 */
function applyUnreadIndicator(count: number): void {
  const total = Math.max(0, Math.min(9999, Math.trunc(count)))
  unreadTotal = total
  if (tray) {
    try {
      tray.setToolTip(total > 0 ? `EiseChat — ${total} 条未读消息（点击打开）` : 'EiseChat')
    } catch (error) {
      console.warn('[desktop] 更新托盘提示失败', error)
    }
  }
  if (process.platform === 'darwin' || process.platform === 'linux') {
    try {
      app.setBadgeCount(total)
    } catch (error) {
      console.warn('[desktop] 更新系统角标失败', error)
    }
  }
  if (mainWindow && !mainWindow.isDestroyed()) {
    try {
      mainWindow.setTitle(total > 0 ? `EiseChat（${total} 条未读）` : 'EiseChat')
    } catch (error) {
      console.warn('[desktop] 更新窗口标题失败', error)
    }
  }
  const shouldAlert = total > 0 && !isMainWindowActive()
  if (shouldAlert) {
    startTrayBlink()
    if (mainWindow && !mainWindow.isDestroyed()) {
      try {
        mainWindow.flashFrame(true)
      } catch (error) {
        console.warn('[desktop] 任务栏闪烁失败', error)
      }
    }
  } else {
    stopTrayBlink()
    if (mainWindow && !mainWindow.isDestroyed()) {
      try {
        mainWindow.flashFrame(false)
      } catch (error) {
        console.warn('[desktop] 停止任务栏闪烁失败', error)
      }
    }
  }
}

/** 开机自启（默认关闭，settings.json 的 autoLaunch 控制；Windows 写入登录启动项）。 */
function applyAutoLaunch(): void {
  if (process.platform !== 'win32') return
  try {
    app.setLoginItemSettings({ openAtLogin: getSettings().autoLaunch, path: process.execPath })
  } catch (error) {
    console.warn('[desktop] 设置开机自启失败', error)
  }
}

// 单实例锁：重复启动时聚焦已有窗口（M4-02 起隐藏窗口由托盘「显示主窗口」唤回）。
if (!app.requestSingleInstanceLock()) {
  app.quit()
} else {
  app.on('second-instance', () => {
    showMainWindow()
  })

  void app.whenReady().then(() => {
    registerIpcHandlers()
    applyAutoLaunch()
    mainWindow = createMainWindow()
    createTray()
    // F-02：渲染进程上报未读数后，由主进程更新托盘提示/任务栏标题（无窗口时也保留托盘提示）。
    registerUnreadIndicator(applyUnreadIndicator)

    app.on('activate', () => {
      if (BrowserWindow.getAllWindows().length === 0) {
        mainWindow = createMainWindow()
      } else {
        showMainWindow()
      }
    })
  })

  app.on('before-quit', () => {
    isQuitting = true
    if (mainWindow) {
      saveWindowState(mainWindow)
    }
  })

  app.on('window-all-closed', () => {
    // 关闭到托盘时窗口只是隐藏，不会触发该事件；未开启关闭到托盘时按平台退出。
    if (process.platform !== 'darwin') {
      app.quit()
    }
  })
}
