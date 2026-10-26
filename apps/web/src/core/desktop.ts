// 桌面壳（Electron preload 注入的 window.eisechatDesktop）扩展能力桥：
// 未读角标同步、系统通知点击回调；Web 版没有该对象，所有调用静默降级。
// 只暴露白名单能力，不接触 Node/Electron 原始对象（apps/desktop/electron/preload.ts）。

/** 通知点击回调（主进程 → 渲染进程），参数为会话 ID（已由主进程校验过格式）。 */
export type NotificationClickCallback = (conversationId: string) => void

interface DesktopBridge {
  /** 把全局未读数同步到托盘/任务栏（F-02）。 */
  setUnreadTotal?: (count: number) => void
  /** 订阅系统通知点击事件（F-01），返回取消订阅函数。 */
  onNotificationClick?: (callback: NotificationClickCallback) => () => void
}

function getBridge(): DesktopBridge | null {
  if (typeof window === 'undefined') return null
  const bridge = (window as Window & { eisechatDesktop?: DesktopBridge }).eisechatDesktop
  return bridge ?? null
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
    window.addEventListener('eisechat:notification-click', handler)
    disposers.push(() => window.removeEventListener('eisechat:notification-click', handler))
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
