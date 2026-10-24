// 全局运行配置（docs/project-overview.md §9、protocol/protocol.md §1/§8）。
// 环境变量说明见 apps/web/.env.example；这里是唯一默认值来源，业务代码不要直接读 import.meta.env。

export interface AppConfig {
  /** REST 基础路径（默认 /api，开发期由 vite 代理到 127.0.0.1:8080） */
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

function defaultWsUrl(): string {
  if (typeof window === 'undefined') return '/ws'
  const protocol = window.location.protocol === 'https:' ? 'wss:' : 'ws:'
  return `${protocol}//${window.location.host}/ws`
}

const env = import.meta.env

export const config: AppConfig = {
  apiBase: env.VITE_API_BASE ?? '/api',
  wsUrl: env.VITE_WS_URL || defaultWsUrl(),
  protocolVersion: Number(env.VITE_PROTOCOL_VERSION ?? 1),
  heartbeatSec: 25,
  heartbeatTimeoutSec: 30,
  reconnectDelaysMs: [1000, 2000, 5000, 10000, 30000],
  syncBatchSize: 200,
  requestTimeoutMs: 15000,
  dbName: 'eisechat',
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
