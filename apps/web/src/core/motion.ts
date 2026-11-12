/**
 * 动效封装（批次 4 / U-16）。组件只调本模块，不直接依赖动画库 API，便于整体替换引擎。
 * 主线：motion/mini（Motion One 的 animate，MIT，WAAPI 驱动，只引入 animate 入口）。
 * 降级：把 ENGINE 改为 'waapi' 即改用原生 Web Animations API，导出接口不变，调用方零改动。
 * 规则（AGENTS.md §14.9）：只动 transform / opacity；reduced-motion 由本层兜底；
 * 动画必须可被打断，组件卸载时必须 cancel()，否则在途动画会继续持有 DOM。
 */
import { animate } from 'motion/mini'

/** 引擎开关：'motion' 为主线；'waapi' 为零依赖降级路径，保留但默认不启用。 */
const ENGINE: 'motion' | 'waapi' = 'motion'

type Bezier = [number, number, number, number]

interface MotionTokens {
  ease: Bezier
  enterMs: number
  staggerMs: number
  staggerMaxMs: number
}

/** CSS token 未就绪时的回退值，与 styles/variables.css 的同名 token 一致。 */
const FALLBACK: MotionTokens = {
  ease: [0.2, 0, 0, 1],
  enterMs: 160,
  staggerMs: 60,
  staggerMaxMs: 200,
}

/** 进度 / 循环类反馈用线性曲线（§14.9）。 */
const LINEAR: Bezier = [0, 0, 1, 1]

let cachedTokens: MotionTokens | null = null

/** 曲线与时长以 CSS token 为唯一来源；只读一次并缓存，避免 JS 侧另立一套数值。 */
function tokens(): MotionTokens {
  if (cachedTokens) return cachedTokens
  const style = getComputedStyle(document.documentElement)
  const read = (name: string): string => style.getPropertyValue(name).trim()
  const ms = (name: string, fallback: number): number => {
    const value = Number.parseFloat(read(name))
    return Number.isFinite(value) ? value : fallback
  }
  const bezier = read('--ec-ease').match(/-?\d*\.?\d+/g)?.map(Number)
  cachedTokens = {
    ease:
      bezier?.length === 4 ? [bezier[0], bezier[1], bezier[2], bezier[3]] : FALLBACK.ease,
    enterMs: ms('--ec-transition-base', FALLBACK.enterMs),
    staggerMs: ms('--ec-motion-stagger', FALLBACK.staggerMs),
    staggerMaxMs: ms('--ec-motion-stagger-max', FALLBACK.staggerMaxMs),
  }
  return cachedTokens
}

export interface MotionHandle {
  /** 取消在途动画；组件卸载或状态切换时必须调用。 */
  cancel: () => void
  /** 全部动画结束或被取消后 resolve，用于串联后续状态。 */
  finished: Promise<void>
}

interface Segment {
  cancel: () => void
  finished: Promise<unknown>
}

interface Frames {
  opacity: number[]
  /** 竖直位移（px），长度与 opacity 一致；不传表示只做透明度。 */
  translateY?: number[]
}

interface Timing {
  durationMs: number
  delayMs: number
  ease: Bezier
  /** 循环次数；Infinity 表示无限循环。 */
  repeat?: number
}

function combine(segments: Segment[]): MotionHandle {
  return {
    cancel: () => {
      for (const segment of segments) segment.cancel()
    },
    finished: Promise.all(segments.map((segment) => segment.finished)).then(() => undefined),
  }
}

function idle(): MotionHandle {
  return { cancel: () => {}, finished: Promise.resolve() }
}

export function prefersReducedMotion(): boolean {
  return window.matchMedia('(prefers-reduced-motion: reduce)').matches
}

function toElements(target: Element | Element[] | null): HTMLElement[] {
  if (!target) return []
  const list = Array.isArray(target) ? target : [target]
  return list.filter((item): item is HTMLElement => item instanceof HTMLElement)
}

function play(element: HTMLElement, frames: Frames, timing: Timing): Segment {
  if (ENGINE === 'waapi') {
    const keyframes: Keyframe[] = frames.opacity.map((opacity, index) => {
      const frame: Keyframe = { opacity }
      if (frames.translateY) frame.transform = `translateY(${frames.translateY[index]}px)`
      return frame
    })
    const animation = element.animate(keyframes, {
      duration: timing.durationMs,
      delay: timing.delayMs,
      easing: `cubic-bezier(${timing.ease.join(', ')})`,
      iterations: timing.repeat ?? 1,
      fill: 'both',
    })
    return {
      cancel: () => animation.cancel(),
      finished: animation.finished.catch(() => undefined),
    }
  }
  const controls = animate(
    element,
    frames.translateY
      ? {
          opacity: frames.opacity,
          transform: frames.translateY.map((value) => `translateY(${value}px)`),
        }
      : { opacity: frames.opacity },
    {
      duration: timing.durationMs / 1000,
      delay: timing.delayMs / 1000,
      ease: timing.ease,
      repeat: timing.repeat ?? 0,
    },
  )
  return {
    // stop() 会先把当前帧写回内联样式再取消，避免元素回跳到动画开始前的状态。
    cancel: () => controls.stop(),
    finished: controls.finished.catch(() => undefined),
  }
}

/**
 * 错峰间隔：总跨度不超过 staggerMax，元素多时按元素数均分压缩。
 * 例：6 个元素、60ms / 200ms 上限 → 40ms 间隔，最后一项在 200ms 开始。
 */
function staggerStep(count: number, stepMs: number, maxMs: number): number {
  if (count < 2) return 0
  return Math.min(stepMs, maxMs / (count - 1))
}

export interface EnterOptions {
  /** 首屏少量模块错峰入场；总跨度受 --ec-motion-stagger-max 约束。 */
  stagger?: boolean
  /** 上移距离（px），默认 8；0 表示只淡入。 */
  distance?: number
}

/**
 * 入场：opacity 0→1，可选 translateY(distance)→0。
 * reduced-motion 下不做动画，直接返回已完成句柄（元素保持自然状态）。
 */
export function enter(target: Element | Element[] | null, options: EnterOptions = {}): MotionHandle {
  const elements = toElements(target)
  if (elements.length === 0 || prefersReducedMotion()) return idle()
  const { ease, enterMs, staggerMs, staggerMaxMs } = tokens()
  const distance = options.distance ?? 8
  const step = options.stagger ? staggerStep(elements.length, staggerMs, staggerMaxMs) : 0
  const segments = elements.map((element, index) =>
    play(
      element,
      distance > 0 ? { opacity: [0, 1], translateY: [distance, 0] } : { opacity: [0, 1] },
      { durationMs: enterMs, delayMs: index * step, ease },
    ),
  )
  return combine(segments)
}

export interface LoopOptions {
  /** 单次循环时长（ms），默认 1200。 */
  durationMs?: number
  /** 多点之间的起始错峰（ms），默认 150。 */
  staggerMs?: number
}

/**
 * 循环反馈（三点加载）：opacity 0.35→1→0.35 无限循环，多点依次错开。
 * 只在加载指示可见时调用；reduced-motion 下返回空句柄，由调用方渲染静态文案。
 */
export function loop(target: Element | Element[] | null, options: LoopOptions = {}): MotionHandle {
  const elements = toElements(target)
  if (elements.length === 0 || prefersReducedMotion()) return idle()
  const durationMs = options.durationMs ?? 1200
  const staggerMs = options.staggerMs ?? 150
  const segments = elements.map((element, index) =>
    play(
      element,
      { opacity: [0.35, 1, 0.35] },
      { durationMs, delayMs: index * staggerMs, ease: LINEAR, repeat: Infinity },
    ),
  )
  return combine(segments)
}
