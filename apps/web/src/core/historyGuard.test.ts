import { afterEach, describe, expect, it, vi } from 'vitest'
import { createPanelHistoryGuard } from './historyGuard'

// UI-05 批次2：面板历史保护纯逻辑（不依赖真实浏览器 history，用桩对象模拟）。
interface FakeWindow {
  history: {
    state: unknown
    pushState: ReturnType<typeof vi.fn>
    replaceState: ReturnType<typeof vi.fn>
  }
  addEventListener: ReturnType<typeof vi.fn>
  removeEventListener: ReturnType<typeof vi.fn>
  emitPopState: () => void
}

function createFakeWindow(): FakeWindow {
  const listeners = new Set<() => void>()
  const fake: FakeWindow = {
    history: {
      state: { back: null },
      pushState: vi.fn((state: unknown) => {
        fake.history.state = state
      }),
      replaceState: vi.fn((state: unknown) => {
        fake.history.state = state
      }),
    },
    addEventListener: vi.fn((_type: string, handler: () => void) => listeners.add(handler)),
    removeEventListener: vi.fn((_type: string, handler: () => void) => listeners.delete(handler)),
    emitPopState: () => {
      for (const handler of [...listeners]) handler()
    },
  }
  return fake
}

afterEach(() => {
  vi.unstubAllGlobals()
  vi.restoreAllMocks()
})

describe('createPanelHistoryGuard', () => {
  it('push 幂等：重复调用只压入一个哨兵、只注册一个监听', () => {
    const fake = createFakeWindow()
    vi.stubGlobal('window', fake)
    const onBack = vi.fn()
    const guard = createPanelHistoryGuard(onBack)

    guard.push()
    guard.push()

    expect(fake.history.pushState).toHaveBeenCalledTimes(1)
    expect(fake.addEventListener).toHaveBeenCalledTimes(1)
    expect(fake.history.state).toMatchObject({ back: null, ecPanelGuard: true })
  })

  it('返回一次触发 onBack，并停在当前页（第二次返回不再触发）', () => {
    const fake = createFakeWindow()
    vi.stubGlobal('window', fake)
    const onBack = vi.fn()
    const guard = createPanelHistoryGuard(onBack)

    guard.push()
    fake.emitPopState()
    fake.emitPopState()

    expect(onBack).toHaveBeenCalledTimes(1)
  })

  it('dispose 后返回不再触发 onBack', () => {
    const fake = createFakeWindow()
    vi.stubGlobal('window', fake)
    const onBack = vi.fn()
    const guard = createPanelHistoryGuard(onBack)

    guard.push()
    guard.dispose()
    fake.emitPopState()

    expect(onBack).not.toHaveBeenCalled()
    expect(fake.removeEventListener).toHaveBeenCalledTimes(1)
  })

  it('dispose 时哨兵仍在：用 replaceState 标记失效并保留原有 state 字段', () => {
    const fake = createFakeWindow()
    vi.stubGlobal('window', fake)
    const guard = createPanelHistoryGuard(vi.fn())

    guard.push()
    guard.dispose()

    expect(fake.history.replaceState).toHaveBeenCalledTimes(1)
    expect(fake.history.state).toMatchObject({ back: null, ecPanelGuard: false })
  })

  it('未 push 直接 dispose：不写 history', () => {
    const fake = createFakeWindow()
    vi.stubGlobal('window', fake)
    const guard = createPanelHistoryGuard(vi.fn())

    guard.dispose()

    expect(fake.history.replaceState).not.toHaveBeenCalled()
    expect(fake.removeEventListener).toHaveBeenCalledTimes(1)
  })
})
