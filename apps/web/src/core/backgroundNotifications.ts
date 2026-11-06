// Android 后台消息提醒（Capacitor 本地插件 BackgroundNotify）：
// 应用退到后台后由原生前台服务保持一条 WebSocket 连接，收到 message.new 时发系统通知。
// 约束：不新增 npm 依赖（不 import @capacitor/core），非 Android 壳内静默降级为「不可用」。

/** 启动原生前台服务所需的连接参数；token 只经由插件传入原生侧，不写日志。 */
export interface BackgroundNotifyConfig {
  apiBase: string
  wsUrl: string
  token: string
  deviceId: string
  userId: string
}

export interface BackgroundNotifyStartResult {
  started: boolean
  /** false 表示通知权限被拒绝（此时服务未启动）。 */
  permission?: boolean
}

interface BackgroundNotifyPlugin {
  start(options: BackgroundNotifyConfig): Promise<BackgroundNotifyStartResult>
  stop(): Promise<void>
  isRunning(): Promise<{ running: boolean }>
  setState(options: { foreground: boolean; conversationId?: string | null }): Promise<void>
  consumePendingClick(): Promise<{ conversationId?: string | null }>
  addListener?: (
    eventName: 'notificationClick',
    listener: (data: { conversationId?: string }) => void,
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
  try {
    const result = await plugin.start(config)
    return { started: Boolean(result?.started), permission: result?.permission }
  } catch {
    return { started: false }
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

/** 同步应用前台状态与当前会话（原生服务据此抑制前台/当前会话的通知）。 */
export function setBackgroundConversation(conversationId: string | null): void {
  const plugin = getBackgroundNotifyPlugin()
  if (!plugin) return
  try {
    void plugin.setState({ foreground: true, conversationId })
  } catch {
    // 插件不可用时忽略。
  }
}

/** 订阅通知点击事件；返回取消函数。 */
export function subscribeBackgroundNotificationClick(
  listener: (conversationId: string) => void,
): () => void {
  const plugin = getBackgroundNotifyPlugin()
  if (!plugin?.addListener) return () => {}
  let disposed = false
  try {
    const handle = plugin.addListener('notificationClick', (data) => {
      const conversationId = data?.conversationId?.trim()
      if (conversationId) listener(conversationId)
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

/** 读取并清除冷启动时由通知带起的待打开会话（应用被杀后点通知的场景）。 */
export async function consumePendingBackgroundClick(): Promise<string | null> {
  const plugin = getBackgroundNotifyPlugin()
  if (!plugin) return null
  try {
    const result = await plugin.consumePendingClick()
    const conversationId = result?.conversationId?.trim()
    return conversationId ? conversationId : null
  } catch {
    return null
  }
}
