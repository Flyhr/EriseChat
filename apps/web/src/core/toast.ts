// 轻提示（UI-D）：全局 showToast 桥接到 Naive UI message，替换原自绘 AppToast 渲染层。
// Provider 未就绪时先缓冲，注册 handler 后按顺序 flush，避免启动早期的提示丢失。
// 所有调用方仍使用 showToast(message, kind)，无需改动。
export type ToastKind = 'info' | 'success' | 'error'

/** 组件库提示处理器：由 ToastBridge 在 NMessageProvider 内注册，卸载时置空。 */
export type ToastHandler = (message: string, kind: ToastKind) => void

interface PendingToast {
  message: string
  kind: ToastKind
}

let handler: ToastHandler | null = null
const pending: PendingToast[] = []

export function setToastHandler(next: ToastHandler | null): void {
  handler = next
  if (!next) return
  for (const item of pending.splice(0)) next(item.message, item.kind)
}

/**
 * 显示一条轻提示。
 * @returns 恒为 0（现有调用方均不依赖返回值；保留数字返回以维持原函数签名）。
 */
export function showToast(message: string, kind: ToastKind = 'info'): number {
  const text = message.trim()
  if (text.length === 0) return 0
  if (handler) handler(text, kind)
  else pending.push({ message: text, kind })
  return 0
}
