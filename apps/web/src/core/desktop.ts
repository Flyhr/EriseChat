// 桌面壳（Electron preload 注入的 window.erisechatDesktop）扩展能力桥：
// 未读角标同步、系统通知点击回调、服务器地址、客户端更新检查（F-29）；Web 版没有该对象，所有调用静默降级。
// 只暴露白名单能力，不接触 Node/Electron 原始对象（apps/desktop/electron/preload.ts）。

import type { VersionArtifact } from '../api/version'
import type { DesktopUpdateCheckResult, UpdateProgress } from './update'

/** 通知点击回调（主进程 → 渲染进程），参数为会话 ID（已由主进程校验过格式）。 */
export type NotificationClickCallback = (conversationId: string) => void

interface DesktopBridge {
  /** 把全局未读数同步到托盘/任务栏（F-02）。 */
  setUnreadTotal?: (count: number) => void
  /** 订阅系统通知点击事件（F-01），返回取消订阅函数。 */
  onNotificationClick?: (callback: NotificationClickCallback) => () => void
  /** 保存服务器地址到壳 settings.json（M4-02；登录页首启配置时与 localStorage 保持一致）。 */
  setServerUrl?: (url: string) => Promise<{ ok: boolean; serverUrl: string }>
  /** 检查更新（F-29-04）：主进程从当前服务器请求 /api/version 并比较版本。 */
  checkUpdate?: () => Promise<{ ok: boolean; message?: string; result?: DesktopUpdateCheckResult }>
  /** 下载更新包到临时目录并校验 sha256（F-29-04）。 */
  downloadUpdate?: (artifact: VersionArtifact) => Promise<{ ok: boolean; message?: string }>
  /** 静默安装已下载的更新包并退出（F-29-04）。 */
  installUpdate?: () => Promise<{ ok: boolean; message?: string }>
  /** 订阅下载进度（F-29-04），返回取消订阅函数。 */
  onUpdateProgress?: (listener: (progress: UpdateProgress) => void) => () => void
}

function getBridge(): DesktopBridge | null {
  if (typeof window === 'undefined') return null
  const bridge = (window as Window & { erisechatDesktop?: DesktopBridge }).erisechatDesktop
  return bridge ?? null
}

/** 是否运行在 Electron 桌面壳内（preload 暴露 window.erisechatDesktop）。 */
export function isDesktopShell(): boolean {
  return getBridge() !== null
}

/** 会话 ID 形态校验：服务端统一小写 UUID；通知 IPC 只接受该格式，避免越层注入任意路由参数。 */
export function isConversationIdLike(value: unknown): value is string {
  return (
    typeof value === 'string' &&
    /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(value)
  )
}

/** 同步全局未读数到桌面壳（托盘提示/任务栏标题）；无桌面壳或异常时静默跳过。 */
export function setDesktopUnreadTotal(count: number): void {
  const bridge = getBridge()
  if (typeof bridge?.setUnreadTotal !== 'function') return
  const total = Number.isFinite(count) ? Math.max(0, Math.min(9999, Math.trunc(count))) : 0
  try {
    bridge.setUnreadTotal(total)
  } catch {
    // 桌面桥不可用时静默降级，不影响 Web 内的未读展示。
  }
}

/**
 * 把服务器地址同步到桌面壳 settings.json（登录页首启配置 / 清除时调用）。
 * 无桌面壳（Web/APK）或旧版壳未暴露接口时静默跳过；失败不阻断 Web 内的登录流程。
 */
export async function syncServerUrlToDesktop(url: string): Promise<boolean> {
  const bridge = getBridge()
  if (typeof bridge?.setServerUrl !== 'function') return false
  try {
    const result = await bridge.setServerUrl(url)
    return result?.ok === true
  } catch {
    return false
  }
}

/**
 * 订阅「点击系统通知 → 打开对应会话」：
 * - 桌面壳：主进程通过 IPC `notification:click` 回调；
 * - Web 版：core/notify.ts 的 showWebNotification 在点击时派发同名自定义事件。
 * 回调参数统一做 UUID 形态校验。
 */
export function subscribeNotificationClick(callback: NotificationClickCallback): () => void {
  const disposers: Array<() => void> = []

  const bridge = getBridge()
  if (typeof bridge?.onNotificationClick === 'function') {
    try {
      const dispose = bridge.onNotificationClick((conversationId) => {
        if (isConversationIdLike(conversationId)) callback(conversationId)
      })
      if (typeof dispose === 'function') disposers.push(dispose)
    } catch {
      // 旧版桌面壳没有该能力时静默跳过。
    }
  }

  if (typeof window !== 'undefined') {
    const handler = (event: Event): void => {
      const detail = (event as CustomEvent<{ conversationId?: unknown }>).detail
      if (isConversationIdLike(detail?.conversationId)) callback(detail.conversationId)
    }
    window.addEventListener('erisechat:notification-click', handler)
    disposers.push(() => window.removeEventListener('erisechat:notification-click', handler))
  }

  return () => {
    for (const dispose of disposers) {
      try {
        dispose()
      } catch {
        // 取消订阅失败不影响页面卸载。
      }
    }
  }
}

/**
 * 检查桌面端更新（F-29-04）：由主进程请求当前服务器 /api/version 并比较版本。
 * 无桌面壳（Web/APK）或旧版壳未暴露接口时返回失败，调用方按提示降级。
 */
export async function checkDesktopUpdate(): Promise<{
  ok: boolean
  message?: string
  result?: DesktopUpdateCheckResult
}> {
  const bridge = getBridge()
  if (typeof bridge?.checkUpdate !== 'function') return { ok: false, message: '当前桌面端不支持更新检查' }
  try {
    return await bridge.checkUpdate()
  } catch {
    return { ok: false, message: '检查更新失败，请稍后重试' }
  }
}

/** 下载桌面端安装包（F-29-04，主进程校验 sha256）；失败返回中文提示。 */
export async function downloadDesktopUpdate(
  artifact: VersionArtifact,
): Promise<{ ok: boolean; message?: string }> {
  const bridge = getBridge()
  if (typeof bridge?.downloadUpdate !== 'function') return { ok: false, message: '当前桌面端不支持下载更新' }
  try {
    return await bridge.downloadUpdate(artifact)
  } catch {
    return { ok: false, message: '下载更新失败，请稍后重试' }
  }
}

/** 安装已下载的桌面端更新（F-29-04，静默安装后应用退出）。 */
export async function installDesktopUpdate(): Promise<{ ok: boolean; message?: string }> {
  const bridge = getBridge()
  if (typeof bridge?.installUpdate !== 'function') return { ok: false, message: '当前桌面端不支持安装更新' }
  try {
    return await bridge.installUpdate()
  } catch {
    return { ok: false, message: '安装更新失败，请手动运行下载的安装包' }
  }
}

/** 订阅桌面端下载进度（F-29-04）；无桌面壳时返回空取消函数。 */
export function subscribeDesktopUpdateProgress(listener: (progress: UpdateProgress) => void): () => void {
  const bridge = getBridge()
  if (typeof bridge?.onUpdateProgress !== 'function') return () => {}
  try {
    return bridge.onUpdateProgress(listener)
  } catch {
    return () => {}
  }
}
