// F-07：外部链接打开策略（纯前端，不 import @capacitor/*，避免把原生壳依赖带进 Web 包）。
// - Web / Electron：返回 false，调用方保留 <a target="_blank"> 的默认行为
//   （桌面壳由 main.ts 的 setWindowOpenHandler 交给系统浏览器）。
// - Android（Capacitor）：WebView 不实现 onCreateWindow，target="_blank" 点了没有任何反应，
//   因此改调 @capacitor/browser 的原生插件，用系统浏览器/自定义标签页打开。
// 与 core/updateAndroid.ts 相同的取插件方式：优先读 Capacitor 注入的全局桥，再退回 registerPlugin。
import { isSafeExternalUrl } from './links'

interface CapacitorBrowserPlugin {
  open(options: { url: string; presentationStyle?: string }): Promise<void>
  close?(): Promise<void>
}

interface CapacitorGlobal {
  isNativePlatform?: () => boolean
  getPlatform?: () => string
  Plugins?: Record<string, unknown>
  registerPlugin?: (name: string) => unknown
}

function getCapacitor(): CapacitorGlobal | null {
  if (typeof window === 'undefined') return null
  const bridge = (window as Window & { Capacitor?: CapacitorGlobal }).Capacitor
  return bridge ?? null
}

/** 是否运行在 Capacitor 原生壳内（Android/iOS）；Web 与 Electron 返回 false。 */
export function isNativeShell(): boolean {
  const bridge = getCapacitor()
  if (!bridge) return false
  try {
    if (typeof bridge.isNativePlatform === 'function') return bridge.isNativePlatform()
    if (typeof bridge.getPlatform === 'function') return bridge.getPlatform() !== 'web'
  } catch {
    return false
  }
  return false
}

/** 取 Browser 插件代理；未安装（旧 APK）或非原生壳时返回 null。 */
function getBrowserPlugin(): CapacitorBrowserPlugin | null {
  const bridge = getCapacitor()
  if (!bridge) return null
  const registered = bridge.Plugins?.Browser
  if (registered && typeof (registered as CapacitorBrowserPlugin).open === 'function') {
    return registered as CapacitorBrowserPlugin
  }
  if (typeof bridge.registerPlugin === 'function') {
    try {
      const plugin = bridge.registerPlugin('Browser') as CapacitorBrowserPlugin | null
      if (plugin && typeof plugin.open === 'function') return plugin
    } catch {
      return null
    }
  }
  return null
}

/**
 * 打开外部链接：只放行 http(s)（二次校验，避免把不可信协议交给系统）。
 * 原生壳内用系统浏览器打开并返回 true；Web/桌面返回 false，表示调用方应保留 <a> 的默认行为。
 */
export async function openExternalUrl(url: string): Promise<boolean> {
  if (!isSafeExternalUrl(url)) return false
  if (!isNativeShell()) return false
  const plugin = getBrowserPlugin()
  if (!plugin) return false
  try {
    await plugin.open({ url })
    return true
  } catch {
    // 系统无可用浏览器等情况：静默失败，不弹错误打断阅读。
    return false
  }
}
