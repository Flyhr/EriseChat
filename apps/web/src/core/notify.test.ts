import { describe, expect, it } from 'vitest'
import {
  getDesktopNotifier,
  playIncomingSound,
  shouldPlayIncomingSound,
  shouldShowDesktopNotification,
  type IncomingNotifyState,
} from './notify'

function makeState(overrides: Partial<IncomingNotifyState> = {}): IncomingNotifyState {
  return {
    isSelf: false,
    documentVisible: true,
    activeConversationId: 'conv-1',
    conversationId: 'conv-1',
    muted: false,
    soundEnabled: true,
    desktopNotificationsEnabled: false,
    ...overrides,
  }
}

describe('shouldPlayIncomingSound', () => {
  it('正在查看该会话且窗口可见时不播放', () => {
    expect(shouldPlayIncomingSound(makeState())).toBe(false)
  })

  it('窗口可见但当前在别的会话时播放', () => {
    expect(shouldPlayIncomingSound(makeState({ activeConversationId: 'conv-2' }))).toBe(true)
  })

  it('当前没有打开任何会话（如联系人页）时播放', () => {
    expect(shouldPlayIncomingSound(makeState({ activeConversationId: null }))).toBe(true)
  })

  it('窗口不可见时播放', () => {
    expect(shouldPlayIncomingSound(makeState({ documentVisible: false }))).toBe(true)
  })

  it('自己的消息（多端回显）不播放', () => {
    expect(shouldPlayIncomingSound(makeState({ isSelf: true }))).toBe(false)
  })

  it('用户关闭提示音后不播放', () => {
    expect(shouldPlayIncomingSound(makeState({ soundEnabled: false }))).toBe(false)
  })

  it('免打扰会话不播放（即使窗口不可见且不在该会话）', () => {
    expect(
      shouldPlayIncomingSound(
        makeState({ muted: true, documentVisible: false, activeConversationId: null }),
      ),
    ).toBe(false)
  })
})

describe('shouldShowDesktopNotification', () => {
  it('未开启桌面通知时不弹', () => {
    expect(shouldShowDesktopNotification(makeState({ documentVisible: false }))).toBe(false)
  })

  it('窗口可见时不弹（前台由页面内提示承担）', () => {
    expect(
      shouldShowDesktopNotification(makeState({ desktopNotificationsEnabled: true })),
    ).toBe(false)
  })

  it('开启且窗口不可见时弹出', () => {
    expect(
      shouldShowDesktopNotification(
        makeState({ desktopNotificationsEnabled: true, documentVisible: false }),
      ),
    ).toBe(true)
  })

  it('自己的消息不弹', () => {
    expect(
      shouldShowDesktopNotification(
        makeState({ desktopNotificationsEnabled: true, documentVisible: false, isSelf: true }),
      ),
    ).toBe(false)
  })

  it('免打扰会话不弹桌面通知', () => {
    expect(
      shouldShowDesktopNotification(
        makeState({ desktopNotificationsEnabled: true, documentVisible: false, muted: true }),
      ),
    ).toBe(false)
  })
})

describe('运行环境兜底（node 测试环境无 window）', () => {
  it('无桌面壳桥接时返回 null', () => {
    expect(getDesktopNotifier()).toBeNull()
  })

  it('无 window 时播放提示音不抛异常', () => {
    expect(() => playIncomingSound()).not.toThrow()
  })
})
