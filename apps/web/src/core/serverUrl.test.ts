import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { createMemoryStorage } from '../test/fixtures'
import { startBackgroundNotifications } from './backgroundNotifications'
import {
  config,
  getServerUrl,
  isPackagedShell,
  isSameServerOrigin,
  normalizeBackgroundEndpoints,
  normalizeServerUrl,
  probeServerHealth,
  resetServerUrl,
  resolveAbsoluteApiBase,
  resolveApiBase,
  resolveApiOrigin,
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
      erisechatDesktop: { serverUrl: 'https://desktop.example.com' },
    } as unknown as Window)
    expect(getServerUrl()).toBe('https://desktop.example.com')
    expect(resolveApiBase()).toBe('https://desktop.example.com/api')
    expect(resolveWsUrl()).toBe('wss://desktop.example.com/ws')
  })
})

describe('resolveApiOrigin / resolveAbsoluteApiBase', () => {
  function stubPage(href: string): void {
    vi.stubGlobal('window', { location: { href } } as unknown as Window)
  }

  it('无 window 时返回空串与原值', () => {
    expect(resolveApiOrigin()).toBe('')
    expect(resolveAbsoluteApiBase()).toBe(config.apiBase)
  })

  it('本机配置为相对 /api 时按当前页面解析 origin 与绝对地址', () => {
    stubPage('http://127.0.0.1:5173/chat/abc')
    expect(resolveApiBase()).toBe('/api')
    expect(resolveApiOrigin()).toBe('http://127.0.0.1:5173')
    expect(resolveAbsoluteApiBase()).toBe('http://127.0.0.1:5173/api')
  })

  it('已配置远端服务器时取该地址的 origin，绝对地址原样保留', () => {
    stubPage('http://localhost/')
    setServerUrl('https://chat.example.com')
    expect(resolveApiOrigin()).toBe('https://chat.example.com')
    expect(resolveAbsoluteApiBase()).toBe('https://chat.example.com/api')
  })

  it('打包壳未配置服务器时绝对地址会指向壳自身（Android 页面 origin 为 http://localhost）', () => {
    stubPage('http://localhost/')
    expect(resolveAbsoluteApiBase()).toBe('http://localhost/api')
  })
})

describe('normalizeBackgroundEndpoints', () => {
  /** 打包壳页面：Android `androidScheme: 'http'` 下 origin 为 http://localhost。 */
  function stubShell(): void {
    vi.stubGlobal('window', {
      location: { href: 'http://localhost/', origin: 'http://localhost' },
    } as unknown as Window)
  }

  /** 临时改写构建期默认值，模拟 VITE_API_BASE / VITE_WS_URL。 */
  function withBuildTimeEndpoints(apiBase: string, wsUrl: string, run: () => void): void {
    const original = { apiBase: config.apiBase, wsUrl: config.wsUrl }
    config.apiBase = apiBase
    config.wsUrl = wsUrl
    try {
      run()
    } finally {
      config.apiBase = original.apiBase
      config.wsUrl = original.wsUrl
    }
  }

  /** 与调用方一致：取实际交给插件的两个地址。 */
  function endpoints(): [string, string] {
    return [resolveAbsoluteApiBase(), resolveWsUrl()]
  }

  it('开发态原生插件同样要求绝对地址', () => {
    expect(normalizeBackgroundEndpoints('/api', '/ws', false)).toBeNull()
  })

  it('打包壳未配置服务器时不可用：默认 /api 与按页面推导的 ws 都指回壳自身', () => {
    stubShell()
    expect(normalizeBackgroundEndpoints(...endpoints(), true)).toBeNull()
  })

  it('打包壳保存服务器地址后可用', () => {
    stubShell()
    setServerUrl('https://chat.example.com')
    expect(normalizeBackgroundEndpoints(...endpoints(), true)).toEqual({
      apiBase: 'https://chat.example.com/api',
      wsUrl: 'wss://chat.example.com/ws',
    })
  })

  it('桌面壳注入的地址同样算已配置', () => {
    vi.stubGlobal('window', {
      location: { href: 'http://localhost/', origin: 'http://localhost' },
      erisechatDesktop: { serverUrl: 'https://desktop.example.com' },
    } as unknown as Window)
    expect(normalizeBackgroundEndpoints(...endpoints(), true)).toEqual({
      apiBase: 'https://desktop.example.com/api',
      wsUrl: 'wss://desktop.example.com/ws',
    })
  })

  it('构建期配置了远端 API 与 WS 时可用（未保存服务器地址）', () => {
    stubShell()
    withBuildTimeEndpoints('https://chat.example.com/api', 'wss://chat.example.com/ws', () => {
      expect(getServerUrl()).toBe('')
      expect(normalizeBackgroundEndpoints(...endpoints(), true)).toEqual({
        apiBase: 'https://chat.example.com/api',
        wsUrl: 'wss://chat.example.com/ws',
      })
    })
  })

  it('拒绝原生无法使用的端点：非 http(s) API、非 ws(s) WS、协议相对 WS', () => {
    stubShell()
    expect(normalizeBackgroundEndpoints('file:///api', 'wss://chat.example.com/ws', true)).toBeNull()
    withBuildTimeEndpoints('https://chat.example.com/api', 'ftp://chat.example.com/ws', () => {
      expect(normalizeBackgroundEndpoints(...endpoints(), true)).toBeNull()
    })
    withBuildTimeEndpoints('https://chat.example.com/api', '//chat.example.com/ws', () => {
      expect(normalizeBackgroundEndpoints(...endpoints(), true)).toBeNull()
    })
  })

  it('只有 API 指向远端、WS 仍在壳内时不可用', () => {
    stubShell()
    withBuildTimeEndpoints('https://chat.example.com/api', 'ws://localhost/ws', () => {
      expect(normalizeBackgroundEndpoints(...endpoints(), true)).toBeNull()
    })
  })

  it('插件收到规范化后的端点和完整的连接参数', async () => {
    const start = vi.fn().mockResolvedValue({ started: true })
    vi.stubGlobal('window', {
      location: new URL('http://localhost/'),
      Capacitor: { Plugins: { BackgroundNotify: { start } } },
    })
    const options = {
      apiBase: 'https://CHAT.EXAMPLE.COM:443/api',
      wsUrl: 'wss://chat.exa\tmple.com:443/ws',
      token: 'test-token',
      deviceId: 'device',
      userId: 'user',
      userInitiated: true,
    }
    expect(await startBackgroundNotifications(options)).toMatchObject({ started: true })
    expect(start).toHaveBeenCalledExactlyOnceWith({
      ...options,
      apiBase: 'https://chat.example.com/api',
      wsUrl: 'wss://chat.example.com/ws',
    })
  })
})

describe('isSameServerOrigin', () => {
  function stubPage(href: string): void {
    vi.stubGlobal('window', { location: { href } } as unknown as Window)
  }

  it('相对路径与垃圾值一律不通过（通知来源必须是绝对地址）', () => {
    stubPage('http://127.0.0.1:5173/chat/abc')
    expect(isSameServerOrigin('/api')).toBe(false)
    expect(isSameServerOrigin('/unrelated')).toBe(false)
    expect(isSameServerOrigin('garbage')).toBe(false)
    expect(isSameServerOrigin('//chat.example.com')).toBe(false)
    expect(isSameServerOrigin('')).toBe(false)
    expect(isSameServerOrigin(undefined)).toBe(false)
  })

  it('同源绝对地址通过，路径与查询串归一后比较', () => {
    stubPage('http://127.0.0.1:5173/chat/abc')
    expect(isSameServerOrigin('http://127.0.0.1:5173')).toBe(true)
    expect(isSameServerOrigin('http://127.0.0.1:5173/api')).toBe(true)
    expect(isSameServerOrigin('  http://127.0.0.1:5173/chat/1?x=1  ')).toBe(true)
  })

  it('其他 origin 不通过', () => {
    stubPage('http://127.0.0.1:5173/chat/abc')
    expect(isSameServerOrigin('https://chat.example.com')).toBe(false)
    expect(isSameServerOrigin('http://127.0.0.1:6580')).toBe(false)
  })

  it('配置远端服务器后按配置的 origin 比较', () => {
    stubPage('http://localhost/')
    setServerUrl('https://chat.example.com')
    expect(isSameServerOrigin('https://chat.example.com')).toBe(true)
    expect(isSameServerOrigin('https://chat.example.com/api/')).toBe(true)
    expect(isSameServerOrigin('http://localhost')).toBe(false)
  })
})

describe('isPackagedShell', () => {
  function stubLocation(protocol: string, hostname: string): void {
    vi.stubGlobal('window', { location: { protocol, hostname } } as unknown as Window)
  }

  it('无 window（测试/SSR 环境）时返回 false', () => {
    expect(isPackagedShell(false)).toBe(false)
  })

  it('file: 协议视为 Electron 打包壳', () => {
    stubLocation('file:', '')
    expect(isPackagedShell(false)).toBe(true)
  })

  it('localhost 在非开发态视为 Android 打包壳，Vite 开发态不视为打包壳', () => {
    stubLocation('http:', 'localhost')
    expect(isPackagedShell(false)).toBe(true)
    expect(isPackagedShell(true)).toBe(false)
  })

  it('Web 同源部署（域名 / 127.0.0.1）不视为打包壳', () => {
    stubLocation('https:', 'chat.example.com')
    expect(isPackagedShell(false)).toBe(false)
    stubLocation('http:', '127.0.0.1')
    expect(isPackagedShell(false)).toBe(false)
  })
})

describe('probeServerHealth', () => {
  it('探测成功时返回版本号，并请求归一化后的 /api/health', async () => {
    const fetchMock = vi.fn().mockResolvedValue({
      ok: true,
      json: async () => ({ status: 'ok', version: '1.1.0' }),
    })
    vi.stubGlobal('fetch', fetchMock)

    const result = await probeServerHealth('https://chat.example.com/')
    expect(result).toEqual({ ok: true, version: '1.1.0', message: '' })
    expect(fetchMock).toHaveBeenCalledWith('https://chat.example.com/api/health', expect.anything())
  })

  it('服务返回非 2xx 时给出状态码提示', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue({ ok: false, status: 503 }))
    const result = await probeServerHealth('http://127.0.0.1:6580')
    expect(result.ok).toBe(false)
    expect(result.message).toBe('服务返回 503')
  })

  it('网络失败时给出无法连接提示', async () => {
    vi.stubGlobal('fetch', vi.fn().mockRejectedValue(new TypeError('Failed to fetch')))
    const result = await probeServerHealth('http://127.0.0.1:6580')
    expect(result.ok).toBe(false)
    expect(result.message).toBe('无法连接，请检查地址或网络')
  })

  it('非法地址直接返回格式错误，不发请求', async () => {
    const fetchMock = vi.fn()
    vi.stubGlobal('fetch', fetchMock)
    const result = await probeServerHealth('not-a-url')
    expect(result).toEqual({ ok: false, version: '', message: '地址格式不正确' })
    expect(fetchMock).not.toHaveBeenCalled()
  })
})
