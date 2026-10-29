import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { createMemoryStorage } from '../test/fixtures'
import {
  config,
  getServerUrl,
  normalizeServerUrl,
  resetServerUrl,
  resolveApiBase,
  resolveWsUrl,
  SERVER_URL_STORAGE_KEY,
  setServerUrl,
} from './config'

let storage: Storage

beforeEach(() => {
  storage = createMemoryStorage()
  vi.stubGlobal('localStorage', storage)
  vi.stubGlobal('window', undefined)
})

afterEach(() => {
  vi.unstubAllGlobals()
})

describe('normalizeServerUrl', () => {
  it('接受 https 地址并去掉 path/query/hash 与尾部斜杠', () => {
    expect(normalizeServerUrl('https://chat.example.com/')).toBe('https://chat.example.com')
    expect(normalizeServerUrl('  https://chat.example.com/app?x=1#top  ')).toBe('https://chat.example.com')
  })

  it('保留显式端口（本机开发地址）', () => {
    expect(normalizeServerUrl('http://127.0.0.1:6580')).toBe('http://127.0.0.1:6580')
    expect(normalizeServerUrl('http://127.0.0.1:6580/')).toBe('http://127.0.0.1:6580')
  })

  it('协议与主机名归一为小写', () => {
    expect(normalizeServerUrl('HTTPS://Chat.Example.COM')).toBe('https://chat.example.com')
  })

  it('拒绝非 http(s) 协议、缺少协议或缺少主机名', () => {
    expect(normalizeServerUrl('ftp://chat.example.com')).toBeNull()
    expect(normalizeServerUrl('chat.example.com')).toBeNull()
    expect(normalizeServerUrl('//chat.example.com')).toBeNull()
    expect(normalizeServerUrl('https://')).toBeNull()
  })

  it('空值/纯空白返回 null', () => {
    expect(normalizeServerUrl('')).toBeNull()
    expect(normalizeServerUrl('   ')).toBeNull()
    expect(normalizeServerUrl(null)).toBeNull()
    expect(normalizeServerUrl(undefined)).toBeNull()
  })
})

describe('getServerUrl / setServerUrl / resetServerUrl', () => {
  it('默认无配置时返回空串（与页面同源）', () => {
    expect(getServerUrl()).toBe('')
  })

  it('保存合法地址后规范化持久化，reset 后回退默认', () => {
    expect(setServerUrl('https://chat.example.com/')).toBe(true)
    expect(storage.getItem(SERVER_URL_STORAGE_KEY)).toBe('https://chat.example.com')
    expect(getServerUrl()).toBe('https://chat.example.com')

    resetServerUrl()
    expect(storage.getItem(SERVER_URL_STORAGE_KEY)).toBeNull()
    expect(getServerUrl()).toBe('')
  })

  it('非法地址返回 false 且不写入', () => {
    expect(setServerUrl('not-a-url')).toBe(false)
    expect(storage.getItem(SERVER_URL_STORAGE_KEY)).toBeNull()
  })

  it('传空字符串等价于清除配置', () => {
    expect(setServerUrl('https://chat.example.com')).toBe(true)
    expect(setServerUrl('')).toBe(true)
    expect(getServerUrl()).toBe('')
  })

  it('存储中的非法值被忽略并回退默认', () => {
    storage.setItem(SERVER_URL_STORAGE_KEY, 'garbage')
    expect(getServerUrl()).toBe('')
  })
})

describe('resolveApiBase / resolveWsUrl', () => {
  it('未配置时回退 config 默认值', () => {
    expect(resolveApiBase()).toBe(config.apiBase)
    expect(resolveWsUrl()).toBe(config.wsUrl)
  })

  it('配置 https 地址后 REST 走 <server>/api、WS 走 wss://<host>/ws', () => {
    setServerUrl('https://chat.example.com')
    expect(resolveApiBase()).toBe('https://chat.example.com/api')
    expect(resolveWsUrl()).toBe('wss://chat.example.com/ws')
  })

  it('配置 http 地址后 WS 走 ws://<host>/ws（保留端口）', () => {
    setServerUrl('http://127.0.0.1:6580')
    expect(resolveApiBase()).toBe('http://127.0.0.1:6580/api')
    expect(resolveWsUrl()).toBe('ws://127.0.0.1:6580/ws')
  })

  it('桌面壳注入地址时作为初始值（只读来源，Web 版无此对象）', () => {
    vi.stubGlobal('window', {
      eisechatDesktop: { serverUrl: 'https://desktop.example.com' },
    } as unknown as Window)
    expect(getServerUrl()).toBe('https://desktop.example.com')
    expect(resolveApiBase()).toBe('https://desktop.example.com/api')
    expect(resolveWsUrl()).toBe('wss://desktop.example.com/ws')
  })
})
