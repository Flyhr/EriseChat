import { app, BrowserWindow, Menu, Tray, nativeImage, shell } from 'electron'
import fs from 'node:fs'
import path from 'node:path'
import { getSettings, registerIpcHandlers } from './ipc'

// EiseChat Windows 桌面壳（M0 骨架 → M4-02 托盘/系统通知/窗口记忆/关闭到托盘）。
// 只负责窗口生命周期、托盘、通知与白名单 IPC，不写聊天业务逻辑（AGENTS.md §3 / project-overview §7.3、§10）。
// 安全默认：contextIsolation=true、nodeIntegration=false、sandbox=true；页面跳转/弹窗仅允许配置的服务器与本地文件。

const DEV_SERVER_URL = process.env.EISECHAT_DEV_SERVER_URL ?? 'http://127.0.0.1:5173'
const MIN_WIDTH = 960
const MIN_HEIGHT = 640

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

/** 系统托盘：显示主窗口 / 退出；点击托盘图标聚焦窗口。 */
function createTray(): void {
  const iconPath = app.isPackaged
    ? path.join(process.resourcesPath, 'icon.ico')
    : path.join(__dirname, '..', 'build', 'icon.ico')
  let image = nativeImage.createFromPath(iconPath)
  if (image.isEmpty()) {
    image = nativeImage.createEmpty()
  }

  tray = new Tray(image)
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
