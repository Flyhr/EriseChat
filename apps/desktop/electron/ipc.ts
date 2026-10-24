import { app, ipcMain } from 'electron'

// 主进程 IPC 注册（M0 骨架）：目前只提供只读的应用信息；
// 未读角标、系统通知、窗口控制等能力在 M4 按需增加（渲染进程只能调用这里明确定义的通道）。
export function registerIpcHandlers(): void {
  ipcMain.handle('app:get-info', () => ({
    name: app.getName(),
    version: app.getVersion(),
    packaged: app.isPackaged,
  }))
}
