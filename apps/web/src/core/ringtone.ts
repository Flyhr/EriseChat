// 主叫回铃音：Web Audio 合成双音（440/480Hz），响 1 秒停 2 秒循环，直到接通或通话结束。
// 只在用户点击“呼叫”的手势里创建 AudioContext，符合浏览器自动播放策略；不需要额外权限与依赖。
let context: AudioContext | null = null
let timer: ReturnType<typeof setTimeout> | null = null
let playing = false

function ensureContext(): AudioContext | null {
  if (typeof window === 'undefined') return null
  if (!context) {
    const Ctor = window.AudioContext
    if (!Ctor) return null
    context = new Ctor()
  }
  if (context.state === 'suspended') {
    void context.resume()
  }
  return context
}

function beep(ctx: AudioContext, frequency: number, at: number, duration: number): void {
  const oscillator = ctx.createOscillator()
  const gain = ctx.createGain()
  oscillator.type = 'sine'
  oscillator.frequency.value = frequency
  gain.gain.setValueAtTime(0.0001, at)
  gain.gain.exponentialRampToValueAtTime(0.12, at + 0.02)
  gain.gain.exponentialRampToValueAtTime(0.0001, at + duration)
  oscillator.connect(gain).connect(ctx.destination)
  oscillator.start(at)
  oscillator.stop(at + duration + 0.02)
}

function schedule(): void {
  const ctx = ensureContext()
  if (!ctx) return
  const now = ctx.currentTime
  beep(ctx, 440, now, 0.4)
  beep(ctx, 480, now + 0.45, 0.4)
  timer = setTimeout(schedule, 3000)
}

export const ringtone = {
  start(): void {
    if (playing) return
    playing = true
    schedule()
  },
  stop(): void {
    playing = false
    if (timer) {
      clearTimeout(timer)
      timer = null
    }
  },
}
