// 全局运行配置（docs/project-overview.md §9、protocol/protocol.md §1/§8）。
// 环境变量说明见 apps/web/.env.example；这里是唯一默认值来源，业务代码不要直接读 import.meta.env。
// M4-01：服务器地址支持运行时配置（localStorage，桌面/APK 首次使用需填写）；Web 同源访问留空即用默认值。

import { readStorage, removeStorage, writeStorage } from './safeStorage'
import { findServerPreset, parseServerPresets, type ServerPreset } from './serverPresets'

export interface AppConfig {
  /** REST 基础路径（默认 /api，开发期由 vite 代理到 127.0.0.1:6580） */
  apiBase: string
  /** WebSocket 地址（默认按当前页面 origin 推导） */
  wsUrl: string
  /** 客户端协议版本，与服务端 hello.protocolVersion 比对（protocol.md §8） */
  protocolVersion: number
  /** 心跳间隔（秒）：客户端每 25s 发 ping（AGENTS.md §5.8） */
  heartbeatSec: number
  /** 心跳超时（秒）：30s 内无任何响应判定断线 */
  heartbeatTimeoutSec: number
  /** 重连退避基数（毫秒）：1/2/5/10/30s，超出后保持 30s */
  reconnectDelaysMs: readonly number[]
  /** 单次 sync 每会话最多返回条数（protocol.md §4.4） */
  syncBatchSize: number
  /** 普通 HTTP 请求超时（毫秒） */
  requestTimeoutMs: number
  /** IndexedDB 数据库名（core/db.ts 使用） */
  dbName: string
}

/** 运行时服务器地址的 localStorage 键（只保存 origin，如 https://chat.example.com）。 */
export const SERVER_URL_STORAGE_KEY = 'erisechat.serverUrl'

/**
 * 单条文本消息最大字符数（protocol/protocol.md §4.3：服务端校验同值）。
 * 输入框、草稿存储与发送入口统一引用该常量，避免多处魔法数字漂移。
 */
export const MAX_TEXT_LENGTH = 4096

function defaultWsUrl(): string {
  if (typeof window === 'undefined') return '/ws'
  const protocol = window.location.protocol === 'https:' ? 'wss:' : 'ws:'
  return `${protocol}//${window.location.host}/ws`
}

const env = import.meta.env

/** F-31：打包壳登录页服务器地址下拉预设（VITE_SERVER_PRESETS 可覆盖，见 .env.example）。 */
export const serverPresets: readonly ServerPreset[] = parseServerPresets(env.VITE_SERVER_PRESETS)

/** 按地址匹配预设（登录页下拉回显当前选中项）。 */
export function matchServerPreset(url: string): ServerPreset | undefined {
  return findServerPreset(serverPresets, url)
}

/** 构建期/同源默认值；运行时服务器地址见 resolveApiBase()/resolveWsUrl()。 */
export const config: AppConfig = {
  apiBase: env.VITE_API_BASE ?? '/api',
  wsUrl: env.VITE_WS_URL || defaultWsUrl(),
  protocolVersion: Number(env.VITE_PROTOCOL_VERSION ?? 1),
  heartbeatSec: 25,
  heartbeatTimeoutSec: 30,
  reconnectDelaysMs: [1000, 2000, 5000, 10000, 30000],
  syncBatchSize: 200,
  requestTimeoutMs: 15000,
  dbName: 'erisechat',
}

/**
 * 规范化服务器地址：trim → 必须是 http/https 且带主机名 → 只保留 origin（去尾部斜杠、丢弃 path/query/hash）。
 * 非法或空值返回 null（调用方回退默认配置）。
 */
export function normalizeServerUrl(raw: string | null | undefined): string | null {
  const value = (raw ?? '').trim()
  if (!value) return null
  let parsed: URL
  try {
    parsed = new URL(value)
  } catch {
    return null
  }
  if (parsed.protocol !== 'http:' && parsed.protocol !== 'https:') return null
  if (!parsed.hostname) return null
  return parsed.origin
}

function readStoredServerUrl(): string | null {
  return normalizeServerUrl(readStorage(SERVER_URL_STORAGE_KEY))
}

/** 桌面壳 preload 注入的只读服务器地址（M4-02 提供 window.erisechatDesktop.serverUrl），Web 版不存在。 */
function readInjectedServerUrl(): string | null {
  if (typeof window === 'undefined') return null
  const injected = (window as Window & { erisechatDesktop?: { serverUrl?: string } }).erisechatDesktop?.serverUrl
  return normalizeServerUrl(injected ?? null)
}

/** 当前生效的服务器地址（运行时配置 > 桌面注入 > 空串 = 与页面同源）。 */
export function getServerUrl(): string {
  return readStoredServerUrl() ?? readInjectedServerUrl() ?? ''
}

/**
 * 实际生效的服务器 origin（未配置时回退当前页面 origin）。
 * 仅当 origin 变化时才需要隔离旧登录态（F-34）。
 */
export function resolveServerOrigin(url: string = getServerUrl()): string {
  const normalized = normalizeServerUrl(url)
  if (normalized) return normalized
  if (typeof window === 'undefined') return ''
  return window.location.origin
}

/** REST 基础路径：配置了服务器地址时为 `<server>/api`，否则退回构建期/同源默认值。 */
export function resolveApiBase(): string {
  const server = readStoredServerUrl() ?? readInjectedServerUrl()
  return server ? `${server}/api` : config.apiBase
}

/**
 * 实际 REST API 的绝对 origin：跨进程传递与通知归属比较统一走这个入口。
 * resolveApiBase() 可能是相对路径（同源部署 / Vite 代理的 `/api`）或绝对地址，
 * 单参数 `new URL(value)` 无法解析相对路径；以当前页面为基准解析后只取 origin。
 * 传入 apiBase 可解析通知来源记录的地址；空值或非法值返回空串。
 */
export function resolveApiOrigin(apiBase: string = resolveApiBase()): string {
  const value = (apiBase ?? '').trim()
  if (!value || typeof window === 'undefined') return ''
  try {
    return new URL(value, window.location.href).origin
  } catch {
    return ''
  }
}

/** WebSocket 地址：配置了服务器地址时为 `ws(s)://<host>/ws`，否则退回默认推导。 */
export function resolveWsUrl(): string {
  const server = readStoredServerUrl() ?? readInjectedServerUrl()
  if (!server) return config.wsUrl
  const url = new URL(server)
  const wsProtocol = url.protocol === 'https:' ? 'wss:' : 'ws:'
  return `${wsProtocol}//${url.host}/ws`
}

/**
 * 保存服务器地址（空字符串 = 清除配置回退同源默认）。
 * 返回是否保存成功：非法地址返回 false 且不写入。
 */
export function setServerUrl(raw: string): boolean {
  if ((raw ?? '').trim().length === 0) {
    resetServerUrl()
    return true
  }
  const normalized = normalizeServerUrl(raw)
  if (!normalized) return false
  // 隐私模式/存储被禁用：持久化失败不报错，本次会话内仍会通过返回值提示已保存。
  writeStorage(SERVER_URL_STORAGE_KEY, normalized)
  return true
}

/** 清除运行时服务器地址，回退构建期/同源默认值。 */
export function resetServerUrl(): void {
  removeStorage(SERVER_URL_STORAGE_KEY)
}

/**
 * 是否运行在「打包壳」内（打包壳才需要登录页提供服务器地址入口）：
 * - `file:`：Electron 桌面壳直接加载本地 index.html；
 * - `localhost`：Android Capacitor `androidScheme: 'http'` 的页面 origin；
 * - Vite 开发态（DEV=true）排除；Web 同源部署（域名 / 127.0.0.1 服务端静态页）返回 false。
 */
export function isPackagedShell(dev: boolean = Boolean(env.DEV)): boolean {
  if (typeof window === 'undefined') return false
  if (window.location.protocol === 'file:') return true
  return !dev && window.location.hostname === 'localhost'
}

export interface ServerProbeResult {
  ok: boolean
  /** 服务端 health 返回的版本号；失败或未返回时为空串。 */
  version: string
  /** 失败原因（中文，可直接展示）。 */
  message: string
}

/**
 * 探测服务器可达性：GET `<server>/api/health`，默认 5 秒超时。
 * 只用于登录页提示，失败不阻断保存（允许先存后修）。
 */
export async function probeServerHealth(serverUrl: string, timeoutMs = 5000): Promise<ServerProbeResult> {
  const normalized = normalizeServerUrl(serverUrl)
  if (!normalized) return { ok: false, version: '', message: '地址格式不正确' }
  const controller = new AbortController()
  const timer = setTimeout(() => controller.abort(), timeoutMs)
  try {
    const response = await fetch(`${normalized}/api/health`, { signal: controller.signal })
    if (!response.ok) return { ok: false, version: '', message: `服务返回 ${response.status}` }
    const body = (await response.json()) as { version?: unknown }
    return {
      ok: true,
      version: typeof body.version === 'string' ? body.version : '',
      message: '',
    }
  } catch {
    return { ok: false, version: '', message: '无法连接，请检查地址或网络' }
  } finally {
    clearTimeout(timer)
  }
}

/**
 * 第 attempt 次重连的等待时间（毫秒）：退避基数 + 0~500ms 随机抖动（AGENTS.md §5.8）。
 * attempt 从 0 开始；超过数组长度后按最后一项（30s）重试。
 */
export function reconnectDelayMs(attempt: number, random: () => number = Math.random): number {
  const delays = config.reconnectDelaysMs
  const index = Math.min(Math.max(attempt, 0), delays.length - 1)
  const base = delays[index] ?? delays[delays.length - 1] ?? 30000
  return base + Math.floor(random() * 500)
}
