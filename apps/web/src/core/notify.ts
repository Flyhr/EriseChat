// 新消息提醒（本机能力）：提示音 + 系统通知。
// 判定逻辑抽成纯函数便于单测；播放/弹窗失败一律静默，不影响消息入库与展示。
// 不引入音频文件、第三方库或推送服务（AGENTS.md §2/§3）。

/** 判定提示音/通知所需的上下文（全部来自本机状态，不涉及服务端）。 */
export interface IncomingNotifyState {
  /** 是否本人发出的消息（多端同步回显时不提醒）。 */
  isSelf: boolean
  /** 页面是否可见（document.visibilityState === 'visible'）。 */
  documentVisible: boolean
  /** 当前正在查看的会话（Chat 页维护，未打开会话页时为 null）。 */
  activeConversationId: string | null
  /** 新消息所在会话。 */
  conversationId: string
  /** 该会话是否免打扰（F-10）：免打扰时不提示音、不弹桌面通知（未读仍照常累计）。 */
  muted: boolean
  soundEnabled: boolean
  desktopNotificationsEnabled: boolean
}

/** 提示音：非本人消息、非免打扰，且（窗口不可见 或 当前不在该会话）时播放。 */
export function shouldPlayIncomingSound(state: IncomingNotifyState): boolean {
  if (!state.soundEnabled || state.isSelf || state.muted) return false
  if (state.documentVisible && state.activeConversationId === state.conversationId) return false
  return true
}

/** 系统通知：非本人消息、非免打扰，且窗口不可见时弹出（后台/最小化场景）。 */
export function shouldShowDesktopNotification(state: IncomingNotifyState): boolean {
  if (!state.desktopNotificationsEnabled || state.isSelf || state.muted) return false
  return !state.documentVisible
}

/** 桌面壳（apps/desktop preload）暴露的通知接口；Web 版不存在。 */
export interface DesktopNotifier {
  /** conversationId 仅供系统通知点击后打开对应会话（F-01），主进程会再校验 UUID 形态。 */
  notify: (payload: {
    title?: string
    body?: string
    conversationId?: string
  }) => Promise<boolean> | void
}

export function getDesktopNotifier(): DesktopNotifier | null {
  if (typeof window === 'undefined') return null
  const bridge = (
    window as Window & { eisechatDesktop?: { notify?: DesktopNotifier['notify'] } }
  ).eisechatDesktop
  return bridge?.notify ? { notify: bridge.notify } : null
}

/** 单例 AudioContext（首次播放时创建，遵守浏览器自动播放策略）。 */
let audioContext: AudioContext | null = null

/**
 * 播放一段短促的双音提示（WebAudio 合成，无音频资源文件）。
 * 自动播放被拦截、无音频设备等情况下静默跳过。
 */
export function playIncomingSound(): void {
  try {
    if (typeof window === 'undefined') return
    const AudioCtor =
      window.AudioContext ??
      (window as unknown as { webkitAudioContext?: typeof AudioContext }).webkitAudioContext
    if (!AudioCtor) return
    audioContext ??= new AudioCtor()
    if (audioContext.state === 'suspended') {
      void audioContext.resume().catch(() => undefined)
    }
    const now = audioContext.currentTime
    const oscillator = audioContext.createOscillator()
    const gain = audioContext.createGain()
    oscillator.type = 'sine'
    oscillator.frequency.setValueAtTime(660, now)
    oscillator.frequency.setValueAtTime(880, now + 0.12)
    gain.gain.setValueAtTime(0.0001, now)
    gain.gain.exponentialRampToValueAtTime(0.18, now + 0.01)
    gain.gain.exponentialRampToValueAtTime(0.0001, now + 0.26)
    oscillator.connect(gain)
    gain.connect(audioContext.destination)
    oscillator.start(now)
    oscillator.stop(now + 0.28)
  } catch {
    // 音频不可用时静默跳过，不影响消息流程。
  }
}

export type NotificationPermissionState = 'granted' | 'denied' | 'default' | 'unsupported'

function getNotificationApi(): typeof Notification | null {
  if (typeof window === 'undefined' || typeof window.Notification !== 'function') return null
  return window.Notification
}

/**
 * 请求系统通知权限（Web 版用；桌面壳走 preload 原生通知则无需浏览器授权）。
 * @returns 授权结果；环境不支持时返回 'unsupported'。
 */
export async function ensureNotificationPermission(): Promise<NotificationPermissionState> {
  const api = getNotificationApi()
  if (!api) return 'unsupported'
  if (api.permission === 'granted' || api.permission === 'denied') return api.permission
  try {
    const permission = await api.requestPermission()
    return permission === 'granted' || permission === 'denied' || permission === 'default'
      ? permission
      : 'denied'
  } catch {
    return 'denied'
  }
}

/**
 * Web 版系统通知（桌面壳优先用 preload 的 notify，不调用本函数）。返回是否实际弹出。
 * 点击通知时聚焦窗口，并派发 `eisechat:notification-click` 事件携带会话 ID，
 * 由 App.vue 订阅后跳转到对应会话（F-01）；会话 ID 由订阅方再做 UUID 校验。
 */
export function showWebNotification(title: string, body: string, conversationId?: string): boolean {
  const api = getNotificationApi()
  if (!api || api.permission !== 'granted') return false
  try {
    const tag = conversationId ? `eisechat-incoming-${conversationId}` : 'eisechat-incoming'
    const notification = new api(title, { body, tag })
    notification.onclick = () => {
      window.focus()
      notification.close()
      if (conversationId) {
        window.dispatchEvent(
          new CustomEvent('eisechat:notification-click', { detail: { conversationId } }),
        )
      }
    }
    return true
  } catch {
    return false
  }
}
