import { app, BrowserWindow, shell } from 'electron'
import path from 'node:path'
import { registerIpcHandlers } from './ipc'

// EiseChat Windows 桌面壳（M0 骨架）：只负责窗口生命周期、加载 apps/web 构建产物与白名单 IPC，
// 不写聊天业务逻辑（AGENTS.md §3 / project-overview §7.3、§10）。
// 安全默认：contextIsolation=true、nodeIntegration=false、sandbox=true，页面跳转/弹窗受限。

const DEV_SERVER_URL = process.env.EISECHAT_DEV_SERVER_URL ?? 'http://127.0.0.1:5173'

let mainWindow: BrowserWindow | null = null

function createMainWindow(): BrowserWindow {
  const window = new BrowserWindow({
    width: 1200,
    height: 800,
    minWidth: 960,
    minHeight: 640,
    show: false,
    autoHideMenuBar: true,
    webPreferences: {
      preload: path.join(__dirname, 'preload.js'),
      contextIsolation: true,
      nodeIntegration: false,
      sandbox: true,
    },
  })

  window.once('ready-to-show', () => window.show())

  // 内部应用：页面不得自行打开新窗口或跳转到未知来源；外链交给系统浏览器。
  window.webContents.setWindowOpenHandler(({ url }) => {
    if (url.startsWith('http://') || url.startsWith('https://')) {
      void shell.openExternal(url)
    }
    return { action: 'deny' }
  })
  window.webContents.on('will-navigate', (event, url) => {
    if (!app.isPackaged && url.startsWith(DEV_SERVER_URL)) {
      return
    }
    if (url.startsWith('file://')) {
      return
    }
    event.preventDefault()
  })

  if (app.isPackaged) {
    // 打包态：apps/web 构建产物由 electron-builder 复制到 resources/web-dist（见 electron-builder.yml）。
    void window.loadFile(path.join(process.resourcesPath, 'web-dist', 'index.html'))
  } else {
    // 开发态：加载 `cd apps/web && npm run dev` 启动的 Vite 开发服务器。
    void window.loadURL(DEV_SERVER_URL)
  }

  window.on('closed', () => {
    mainWindow = null
  })

  return window
}

// 单实例锁：重复启动时聚焦已有窗口（M4 补充托盘/托盘退出行为）。
if (!app.requestSingleInstanceLock()) {
  app.quit()
} else {
  app.on('second-instance', () => {
    if (mainWindow) {
      if (mainWindow.isMinimized()) {
        mainWindow.restore()
      }
      mainWindow.focus()
    }
  })

  void app.whenReady().then(() => {
    registerIpcHandlers()
    mainWindow = createMainWindow()

    app.on('activate', () => {
      if (BrowserWindow.getAllWindows().length === 0) {
        mainWindow = createMainWindow()
      }
    })
  })

  app.on('window-all-closed', () => {
    if (process.platform !== 'darwin') {
      app.quit()
    }
  })
}
