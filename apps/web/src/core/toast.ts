// 轻提示（UI-03 批次1，计划 §5.11 / §16.3）：全局瞬时反馈，只用于操作成功等短消息，
// 不承载错误详情、不做确认交互（确认用 AppDialog）。
// 实现为模块级响应式队列：任意模块可调用 showToast，AppToast.vue 在 App.vue 中挂载一次统一渲染。
import { readonly, ref } from 'vue'

export type ToastKind = 'info' | 'success' | 'error'

export interface ToastItem {
  id: number
  message: string
  kind: ToastKind
}

/** 同屏最多条数（超出时挤掉最早的）。 */
const MAX_TOASTS = 3
/** 单条展示时长（毫秒）。 */
const TOAST_DURATION_MS = 3000

const items = ref<ToastItem[]>([])
let nextId = 1
const timers = new Map<number, ReturnType<typeof setTimeout>>()

/** 当前展示中的提示（只读，供 AppToast 渲染）。 */
export const toasts = readonly(items)

/**
 * 显示一条轻提示。
 * @returns 提示 id（message 为空时返回 0，不创建提示）。
 */
export function showToast(message: string, kind: ToastKind = 'info'): number {
  const text = message.trim()
  if (text.length === 0) return 0
  const id = nextId++
  items.value = [...items.value, { id, message: text, kind }]
  while (items.value.length > MAX_TOASTS) {
    const oldest = items.value[0]
    if (!oldest) break
    dismissToast(oldest.id)
  }
  timers.set(
    id,
    setTimeout(() => dismissToast(id), TOAST_DURATION_MS),
  )
  return id
}

/** 手动关闭一条提示（也会清除其定时器）。 */
export function dismissToast(id: number): void {
  const timer = timers.get(id)
  if (timer !== undefined) {
    clearTimeout(timer)
    timers.delete(id)
  }
  items.value = items.value.filter((item) => item.id !== id)
}
