// 长会话分片渲染纯函数（F-05 批次 1，计划 §F-05）：窗口范围计算、滚动阈值判断与
// 向上加载时的滚动锚点补偿。无 Vue/DOM 依赖，便于单测。

export interface WindowRange {
  start: number
  end: number
}

/** 默认窗口大小：一次最多渲染约 200 条消息（批次 2 判定的上限为 400 行）。 */
export const DEFAULT_WINDOW_SIZE = 200

/** 把任意数值钳制为 [min, max] 区间的整数；非法数值回退到 min。 */
function clampInt(value: number, min: number, max: number): number {
  if (!Number.isFinite(value)) return min
  const truncated = Math.trunc(value)
  if (truncated < min) return min
  if (truncated > max) return max
  return truncated
}

/** 归一化的消息总数：非法/负数返回 0。 */
function normalizeTotal(total: number): number {
  if (!Number.isFinite(total) || total <= 0) return 0
  return Math.trunc(total)
}

/** 归一化的窗口大小：非法（非有限数或 < 1）按 1 处理，保证窗口至少包含一条消息。 */
function normalizeSize(size: number): number {
  if (!Number.isFinite(size) || size < 1) return 1
  return Math.trunc(size)
}

/**
 * 把窗口范围钳制到 [0, total]：start/end 取整并保证 0 ≤ start ≤ end ≤ total。
 * 非法 start（NaN/Infinity）按 0，非法 end 按 start 处理。
 */
export function clampWindow(range: WindowRange, total: number): WindowRange {
  const safeTotal = normalizeTotal(total)
  const start = clampInt(range.start, 0, safeTotal)
  const end = clampInt(range.end, start, safeTotal)
  return { start, end }
}

/**
 * 初始窗口：取末尾 size 条（end = total，start = max(0, total - size)）。
 * 用于会话首次打开/刷新后恢复，保证先看到最新消息。
 */
export function initialWindowRange(total: number, size: number = DEFAULT_WINDOW_SIZE): WindowRange {
  return tailRange(total, size)
}

/** 末尾窗口：end = total，start 为 end 前移 size 条后的位置。 */
export function tailRange(total: number, size: number = DEFAULT_WINDOW_SIZE): WindowRange {
  const safeTotal = normalizeTotal(total)
  const safeSize = normalizeSize(size)
  return { start: Math.max(0, safeTotal - safeSize), end: safeTotal }
}

/**
 * 向上扩窗：end 不变，start 前移 step 条并钳制到 0。
 * 用户滚到窗口顶部、但还有更早消息已在内存中时使用。
 */
export function expandOlderRange(
  range: WindowRange,
  step: number = DEFAULT_WINDOW_SIZE,
  total: number = Number.POSITIVE_INFINITY,
): WindowRange {
  const safeTotal = Number.isFinite(total) ? normalizeTotal(total) : Number.MAX_SAFE_INTEGER
  const base = clampWindow(range, safeTotal)
  const safeStep = normalizeSize(step)
  return clampWindow({ start: base.start - safeStep, end: base.end }, safeTotal)
}

/** 用户是否已滚动到（或超过）顶部阈值内。 */
export function isNearTop(scrollTop: number, threshold: number = 48): boolean {
  if (!Number.isFinite(scrollTop)) return true
  const safeThreshold = Number.isFinite(threshold) && threshold > 0 ? threshold : 0
  return scrollTop <= safeThreshold
}

/**
 * 用户是否贴底：剩余可滚动距离不超过阈值。
 * 内容不足一屏时 remaining ≤ 0，同样视为贴底。
 */
export function isNearBottom(
  scrollHeight: number,
  scrollTop: number,
  clientHeight: number,
  threshold: number = 80,
): boolean {
  if (!Number.isFinite(scrollHeight) || !Number.isFinite(scrollTop) || !Number.isFinite(clientHeight)) {
    return false
  }
  const safeThreshold = Number.isFinite(threshold) && threshold > 0 ? threshold : 0
  return scrollHeight - scrollTop - clientHeight <= safeThreshold
}

/**
 * 向上加载/扩窗后保持视口内容不动：新 scrollTop = 旧 scrollTop + 高度增量。
 * 内容变高（向上插入）时返回更大的值，从而让用户看到原位置；结果不小于 0。
 */
export function anchorScrollTop(
  prevScrollTop: number,
  prevScrollHeight: number,
  nextScrollHeight: number,
): number {
  if (
    !Number.isFinite(prevScrollTop) ||
    !Number.isFinite(prevScrollHeight) ||
    !Number.isFinite(nextScrollHeight)
  ) {
    return 0
  }
  return Math.max(0, prevScrollTop + (nextScrollHeight - prevScrollHeight))
}

export interface AutoLoadOlderInput {
  /** 用户是否已滚到顶部附近。 */
  atTop: boolean
  /** store 是否还有更早消息可拉取。 */
  hasMore: boolean
  /** store 是否正在加载更早消息。 */
  loading: boolean
  /** 当前窗口起点：0 表示内存中已无更早消息。 */
  rangeStart: number
}

/**
 * 是否应触发「加载更早消息」：仅当窗口中已无更早消息（rangeStart === 0）、
 * store 还有更多、且当前未在加载时，才允许请求，避免重复请求与无效请求。
 */
export function shouldAutoLoadOlder(input: AutoLoadOlderInput): boolean {
  return input.rangeStart === 0 && input.hasMore && !input.loading && input.atTop
}
