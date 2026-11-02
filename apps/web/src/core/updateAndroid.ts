// F-29：Android 壳内 APK 更新（通过 Capacitor 注入的全局桥调用本地插件 ApkUpdater）。
// 约束：不新增 npm 依赖（不 import @capacitor/core），Web/桌面端调用时静默降级为「不可用」。

export interface ApkUpdateProgress {
  downloaded: number
  total: number
  percent: number
}

interface ApkUpdaterPlugin {
  downloadAndInstall(options: { url: string; sha256: string; size: number }): Promise<void>
  addListener?: (
    eventName: 'progress',
    listener: (progress: ApkUpdateProgress) => void,
  ) => Promise<{ remove(): Promise<void> }> | void
}

interface CapacitorGlobal {
  Plugins?: Record<string, unknown>
  registerPlugin?: (name: string) => unknown
}

function getApkUpdater(): ApkUpdaterPlugin | null {
  if (typeof window === 'undefined') return null
  const capacitor = (window as Window & { Capacitor?: CapacitorGlobal }).Capacitor
  if (!capacitor) return null
  const registered = capacitor.Plugins?.ApkUpdater
  if (registered && typeof (registered as ApkUpdaterPlugin).downloadAndInstall === 'function') {
    return registered as ApkUpdaterPlugin
  }
  if (typeof capacitor.registerPlugin === 'function') {
    try {
      const plugin = capacitor.registerPlugin('ApkUpdater') as ApkUpdaterPlugin | null
      if (plugin && typeof plugin.downloadAndInstall === 'function') return plugin
    } catch {
      return null
    }
  }
  return null
}

/** 当前运行环境是否支持 App 内更新（Android 壳且插件已注册）。 */
export function isAndroidUpdaterAvailable(): boolean {
  return getApkUpdater() !== null
}

/** 订阅下载进度；插件不可用时返回空取消函数。 */
export function subscribeApkUpdateProgress(listener: (progress: ApkUpdateProgress) => void): () => void {
  const plugin = getApkUpdater()
  if (!plugin?.addListener) return () => {}
  let removed = false
  try {
    const handle = plugin.addListener('progress', listener)
    return () => {
      if (removed) return
      removed = true
      void Promise.resolve(handle)
        .then((value) => value?.remove())
        .catch(() => {})
    }
  } catch {
    return () => {}
  }
}

/**
 * 下载并调起系统安装器（Android 插件内部校验大小与 sha256）。
 * 成功后系统会弹出安装确认；Android 11+ 首次需允许「安装未知应用」。
 */
export async function downloadAndInstallApk(options: {
  url: string
  sha256: string
  size: number
}): Promise<{ ok: boolean; message?: string }> {
  const plugin = getApkUpdater()
  if (!plugin) return { ok: false, message: '当前客户端不支持 App 内更新，请到下载页手动安装' }
  try {
    await plugin.downloadAndInstall(options)
    return { ok: true }
  } catch (error) {
    const message = error instanceof Error && error.message ? error.message : '下载更新失败，请稍后重试'
    return { ok: false, message }
  }
}
