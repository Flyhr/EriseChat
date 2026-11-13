// Android 后台消息提醒（Capacitor 本地插件 BackgroundNotify）：
// 应用退到后台后由原生前台服务保持一条 WebSocket 连接，收到 message.new 时发系统通知。
// 约束：不新增 npm 依赖（不 import @capacitor/core），非 Android 壳内静默降级为「不可用」。

import { normalizeBackgroundEndpoints } from './config'

/** 启动原生前台服务所需的连接参数；token 只经由插件传入原生侧，不写日志。 */
export interface BackgroundNotifyConfig {
  /** 绝对 REST 基础路径：原生侧用 OkHttp 直接请求，无法解析相对 `/api`（见 resolveAbsoluteApiBase）。 */
  apiBase: string
  wsUrl: string
  token: string
  deviceId: string
  userId: string
  /** true 表示用户在设置页显式开启；自动同步传 false，服务被终止后不自动恢复。 */
  userInitiated?: boolean
}

export interface BackgroundNotifyStartResult {
  started: boolean
  /** false 表示通知权限被拒绝（此时服务未启动）。 */
  permission?: boolean
  /** 未启动原因：notifications_disabled / channel_disabled / server_unconfigured 等。 */
  reason?: string
}

export interface BackgroundNotifyStatus {
  running: boolean
  /** WebSocket 是否已完成认证。 */
  connected: boolean
  /** 终止原因（kicked/auth_failed/auth_closed/protocol_mismatch/foreground_service_timeout 等）。 */
  stopReason?: string | null
}

/** 与原生通知缓存对齐的最小会话快照：本人修改免打扰后由 Web 同步，避免等下一次 conv.updated。 */
export interface BackgroundConversationState {
  conversationId: string
  title: string
  muted: boolean
  members: { userId: string; displayName: string }[]
}

/** 通知点击携带的来源：账号与服务器用于冷启动后判断是否仍属当前登录。 */
export interface PendingBackgroundClick {
  conversationId: string
  userId?: string
  serverUrl?: string
}

interface BackgroundNotifyPlugin {
  start(options: BackgroundNotifyConfig): Promise<BackgroundNotifyStartResult>
  stop(): Promise<void>
  isRunning(): Promise<{ running: boolean; connected?: boolean; stopReason?: string | null }>
  checkAvailability(): Promise<{ available: boolean; reason?: string }>
  updateConversations(options: { conversations: BackgroundConversationState[] }): Promise<void>
  setState(options: { foreground?: boolean; conversationId?: string | null }): Promise<void>
  consumePendingClick(): Promise<{
    conversationId?: string | null
    userId?: string | null
    serverUrl?: string | null
  } | null>
  addListener?: (
    eventName: 'notificationClick' | 'stateChange',
    listener: (data: {
      conversationId?: string
      userId?: string
      serverUrl?: string
      running?: boolean
      connected?: boolean
      stopReason?: string
    }) => void,
  ) => Promise<{ remove(): Promise<void> }> | void
}

interface CapacitorGlobal {
  Plugins?: Record<string, unknown>
  registerPlugin?: (name: string) => unknown
}

function getBackgroundNotifyPlugin(): BackgroundNotifyPlugin | null {
  if (typeof window === 'undefined') return null
  const capacitor = (window as Window & { Capacitor?: CapacitorGlobal }).Capacitor
  if (!capacitor) return null
  const registered = capacitor.Plugins?.BackgroundNotify
  if (registered && typeof (registered as BackgroundNotifyPlugin).start === 'function') {
    return registered as BackgroundNotifyPlugin
  }
  if (typeof capacitor.registerPlugin === 'function') {
    try {
      const plugin = capacitor.registerPlugin('BackgroundNotify') as BackgroundNotifyPlugin | null
      if (plugin && typeof plugin.start === 'function') return plugin
    } catch {
      return null
    }
  }
  return null
}

/** 当前运行环境是否支持 Android 后台消息提醒（原生壳且插件已注册）。 */
export function isAndroidBackgroundAvailable(): boolean {
  return getBackgroundNotifyPlugin() !== null
}

/** 启动后台消息提醒服务；返回是否已启动及通知权限状态。 */
export async function startBackgroundNotifications(
  config: BackgroundNotifyConfig,
): Promise<BackgroundNotifyStartResult> {
  const plugin = getBackgroundNotifyPlugin()
  if (!plugin) return { started: false }
  const endpoints = normalizeBackgroundEndpoints(config.apiBase, config.wsUrl)
  if (!endpoints) {
    return { started: false, reason: 'server_unconfigured' }
  }
  try {
    const result = await plugin.start({ ...config, ...endpoints })
    return { started: Boolean(result?.started), permission: result?.permission, reason: result?.reason }
  } catch {
    return { started: false }
  }
}

/** 读取原生服务的实际运行/连接状态；读取失败返回 null（未知状态，不据此改动用户偏好）。 */
export async function getBackgroundNotifyStatus(): Promise<BackgroundNotifyStatus | null> {
  const plugin = getBackgroundNotifyPlugin()
  if (!plugin) return { running: false, connected: false }
  try {
    const result = await plugin.isRunning()
    return {
      running: Boolean(result?.running),
      connected: Boolean(result?.connected),
      stopReason: result?.stopReason ?? null,
    }
  } catch {
    return null
  }
}

/** 复核系统通知开关与消息渠道是否仍可用（恢复前台时调用）。 */
export async function checkBackgroundNotificationsAvailability(): Promise<{
  available: boolean
  reason?: string
} | null> {
  const plugin = getBackgroundNotifyPlugin()
  if (!plugin) return null
  try {
    const result = await plugin.checkAvailability()
    return { available: Boolean(result?.available), reason: result?.reason }
  } catch {
    return null
  }
}

/** 停止后台消息提醒服务（关闭常驻通知、断开原生连接）。 */
export async function stopBackgroundNotifications(): Promise<void> {
  const plugin = getBackgroundNotifyPlugin()
  if (!plugin) return
  try {
    await plugin.stop()
  } catch {
    // 停止失败不影响前端流程；用户可再次操作。
  }
}

/** 同步当前会话（前后台由 Android 原生生命周期决定，这里不再设置 foreground）。 */
export function setBackgroundConversation(conversationId: string | null): void {
  const plugin = getBackgroundNotifyPlugin()
  if (!plugin) return
  try {
    void plugin.setState({ conversationId })
  } catch {
    // 插件不可用时忽略。
  }
}

/** 订阅通知点击事件；返回取消函数。 */
export function subscribeBackgroundNotificationClick(
  listener: (click: PendingBackgroundClick) => void,
): () => void {
  const plugin = getBackgroundNotifyPlugin()
  if (!plugin?.addListener) return () => {}
  let disposed = false
  try {
    const handle = plugin.addListener('notificationClick', (data) => {
      const conversationId = data?.conversationId?.trim()
      if (!conversationId) return
      listener({
        conversationId,
        userId: data?.userId?.trim() || undefined,
        serverUrl: data?.serverUrl?.trim() || undefined,
      })
    })
    return () => {
      if (disposed) return
      disposed = true
      void Promise.resolve(handle)
        .then((value) => value?.remove())
        .catch(() => {})
    }
  } catch {
    return () => {}
  }
}

/** 订阅原生服务状态变化（连接成功/掉线/终止），返回取消函数。 */
export function subscribeBackgroundStateChange(
  listener: (status: BackgroundNotifyStatus) => void,
): () => void {
  const plugin = getBackgroundNotifyPlugin()
  if (!plugin?.addListener) return () => {}
  let disposed = false
  try {
    const handle = plugin.addListener('stateChange', (data) => {
      listener({
        running: Boolean(data?.running),
        connected: Boolean(data?.connected),
        stopReason: data?.stopReason ?? null,
      })
    })
    return () => {
      if (disposed) return
      disposed = true
      void Promise.resolve(handle)
        .then((value) => value?.remove())
        .catch(() => {})
    }
  } catch {
    return () => {}
  }
}

/** 把已确认的会话状态同步给原生（本人修改免打扰后调用；非 Android 壳内静默忽略）。 */
export function syncBackgroundConversations(
  conversations: readonly BackgroundConversationState[],
): void {
  const plugin = getBackgroundNotifyPlugin()
  if (!plugin) return
  try {
    void Promise.resolve(plugin.updateConversations({ conversations: [...conversations] })).catch(
      () => {},
    )
  } catch {
    // 原生不可用/未注册：忽略，等下一次 conv.updated 修正。
  }
}

/** 读取并清除冷启动时由通知带起的待打开会话（应用被杀后点通知的场景）。 */
export async function consumePendingBackgroundClick(): Promise<PendingBackgroundClick | null> {
  const plugin = getBackgroundNotifyPlugin()
  if (!plugin) return null
  try {
    const result = await plugin.consumePendingClick()
    const conversationId = result?.conversationId?.trim()
    if (!conversationId) return null
    return {
      conversationId,
      userId: result?.userId?.trim() || undefined,
      serverUrl: result?.serverUrl?.trim() || undefined,
    }
  } catch {
    return null
  }
}
