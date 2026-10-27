// 登录设备展示格式化纯函数（F-13）：时间、最近活跃、设备简称。
// 只做展示层格式化，不读取/持久化任何会话状态。

const MINUTE = 60_000
const HOUR = 60 * MINUTE
const DAY = 24 * HOUR

/** 绝对时间（本地时区，YYYY-MM-DD HH:mm）；非法值返回「—」。 */
export function formatSessionTime(timestamp: number): string {
  if (!Number.isFinite(timestamp) || timestamp <= 0) {
    return '—'
  }

  const date = new Date(timestamp)
  const pad = (value: number) => String(value).padStart(2, '0')
  return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())} ${pad(date.getHours())}:${pad(date.getMinutes())}`
}

/**
 * 最近活跃相对时间：刚刚（<60s）/ N 分钟前（<60min）/ N 小时前（<24h）/ N 天前（<7d），
 * 超过 7 天回退到绝对时间；非法值返回「—」。
 */
export function formatLastSeen(lastSeenAt: number, now: number = Date.now()): string {
  if (!Number.isFinite(lastSeenAt) || lastSeenAt <= 0) {
    return '—'
  }

  const reference = Number.isFinite(now) ? now : Date.now()
  const diff = Math.max(0, reference - lastSeenAt)
  if (diff < MINUTE) {
    return '刚刚'
  }

  if (diff < HOUR) {
    return `${Math.floor(diff / MINUTE)} 分钟前`
  }

  if (diff < DAY) {
    return `${Math.floor(diff / HOUR)} 小时前`
  }

  if (diff < 7 * DAY) {
    return `${Math.floor(diff / DAY)} 天前`
  }

  return formatSessionTime(lastSeenAt)
}

/**
 * 设备标识简称（不暴露完整 ID）：去除首尾空白；空值返回「未知设备」；
 * 超长时保留前 10 位 + … + 后 4 位（maxLength 默认 20）。
 */
export function shortDeviceLabel(deviceId: string, maxLength: number = 20): string {
  const trimmed = (deviceId ?? '').trim()
  if (!trimmed) {
    return '未知设备'
  }

  const limit = Number.isFinite(maxLength) && maxLength > 8 ? Math.floor(maxLength) : 20
  if (trimmed.length <= limit) {
    return trimmed
  }

  return `${trimmed.slice(0, 10)}…${trimmed.slice(-4)}`
}
