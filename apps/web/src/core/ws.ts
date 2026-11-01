// WebSocket 连接层：状态机、心跳、重连、reqId 匹配（protocol/protocol.md §1/§2、AGENTS.md §5）。
// 只负责连接与帧收发，不做业务分发；业务帧统一通过 'frame' 事件交给上层（M2 起由 store/sync 消费）。
// 全项目唯一连接入口：业务代码不得直接 new WebSocket（docs/project-overview.md §10）。

import { config, reconnectDelayMs, resolveWsUrl } from './config'
import { readStorage, writeStorage } from './safeStorage'
import type { ClientFrame, ErrorFrame, KickedFrame, ServerFrame } from '../types/frames'

/** 连接状态：idle 未连接 / connecting 首次连接中 / authed 认证完成 / reconnecting 退避重连中 / closed 已关闭 */
export type WsState = 'idle' | 'connecting' | 'authed' | 'reconnecting' | 'closed'

/** 致命关闭码（protocol.md §1）：4001 被服务端踢下线（连接数超上限/远程退出）、4002 认证失败/超时/版本不兼容/帧格式错误。 */
export const TERMINAL_CLOSE_CODES: ReadonlySet<number> = new Set([4001, 4002])

/** 致命关闭事件：连接不会再自动重连，调用方应清理登录态并回登录页（M3-00）。 */
export interface FatalCloseInfo {
  code: number
  reason: string
}

/** 待响应请求（按 reqId 匹配） */
export interface PendingRequest {
  resolve: (frame: ServerFrame) => void
  reject: (error: Error) => void
  timer: ReturnType<typeof setTimeout>
  sentAt: number
}

export interface WsEventMap {
  statechange: WsState
  frame: ServerFrame
  kicked: KickedFrame
  error: ErrorFrame
  /** 致命关闭（4001/4002）：不会自动重连，上层应清会话并回登录页。 */
  fatalClose: FatalCloseInfo
}

export interface WsClientOptions {
  url?: string
  protocolVersion?: number
  heartbeatSec?: number
  heartbeatTimeoutSec?: number
  requestTimeoutMs?: number
  /** 便于测试注入的 WebSocket 工厂；默认 new WebSocket(url) */
  createSocket?: (url: string) => WebSocket
}

export class ChatWsClient {
  state: WsState = 'idle'
  /** 当前连接地址：未显式注入时每次 connect() 按 resolveWsUrl() 重新解析（M4-01）。 */
  url: string
  readonly protocolVersion: number
  readonly heartbeatSec: number
  readonly heartbeatTimeoutSec: number
  readonly requestTimeoutMs: number

  private readonly createSocket: (url: string) => WebSocket
  /** 构造时显式指定的地址（测试/特殊场景）；存在时 connect() 不重新解析服务器地址。 */
  private readonly urlOverride?: string
  private socket: WebSocket | null = null
  private readonly pending = new Map<string, PendingRequest>()
  private readonly handlers = new Map<keyof WsEventMap, Set<(payload: never) => void>>()

  private reconnectAttempt = 0
  private heartbeatTimer: ReturnType<typeof setInterval> | null = null
  private heartbeatDeadline: ReturnType<typeof setTimeout> | null = null
  private reconnectTimer: ReturnType<typeof setTimeout> | null = null
  private manualClose = false
  /** 致命关闭事件只通知一次（kicked 帧与随后的 4001 关闭不重复触发）。 */
  private fatalEmitted = false
  private requestSeq = 0
  private authToken: string | null = null
  private deviceId: string | null = null

  constructor(options: WsClientOptions = {}) {
    this.urlOverride = options.url
    this.url = options.url ?? resolveWsUrl()
    this.protocolVersion = options.protocolVersion ?? config.protocolVersion
    this.heartbeatSec = options.heartbeatSec ?? config.heartbeatSec
    this.heartbeatTimeoutSec = options.heartbeatTimeoutSec ?? config.heartbeatTimeoutSec
    this.requestTimeoutMs = options.requestTimeoutMs ?? config.requestTimeoutMs
    this.createSocket = options.createSocket ?? ((url: string) => new WebSocket(url))
  }

  get pendingCount(): number {
    return this.pending.size
  }

  on<K extends keyof WsEventMap>(event: K, handler: (payload: WsEventMap[K]) => void): () => void {
    let handlers = this.handlers.get(event)
    if (!handlers) {
      handlers = new Set()
      this.handlers.set(event, handlers)
    }
    const wrapped = handler as unknown as (payload: never) => void
    handlers.add(wrapped)
    return () => {
      this.handlers.get(event)?.delete(wrapped)
    }
  }

  /** 注入登录态（JWT + 设备标识）；M1 由 auth store 调用，重连时自动复用。 */
  setAuth(token: string | null, deviceId: string | null = this.deviceId): void {
    this.authToken = token
    this.deviceId = deviceId
  }

  /** 建立连接（幂等）：已连接或连接中时忽略。 */
  connect(): void {
    if (this.state === 'connecting' || this.state === 'authed') return
    this.clearReconnectTimer()
    // M4-01：每次建连重新解析服务器地址，设置页修改后 disconnect→connect 即生效。
    if (!this.urlOverride) {
      this.url = resolveWsUrl()
    }
    this.manualClose = false
    this.fatalEmitted = false
    this.setState(this.reconnectAttempt > 0 ? 'reconnecting' : 'connecting')

    let socket: WebSocket
    try {
      socket = this.createSocket(this.url)
    } catch (error) {
      console.warn('[ws] 创建连接失败，稍后重试', error)
      this.setState('closed')
      this.scheduleReconnect()
      return
    }

    this.socket = socket
    socket.onopen = () => this.handleOpen(socket)
    socket.onmessage = (event: MessageEvent) => this.handleRawMessage(event.data)
    socket.onerror = () => {
      // 浏览器不提供错误详情，统一由 onclose 处理重连。
    }
    socket.onclose = (event: CloseEvent) => this.handleClose(socket, event)
  }

  /** 主动断开：不自动重连。 */
  disconnect(code = 1000, reason = 'client close'): void {
    this.manualClose = true
    this.clearReconnectTimer()
    this.clearHeartbeat()
    this.rejectAllPending(new Error('连接已主动断开'))
    const socket = this.socket
    this.socket = null
    if (socket && socket.readyState === WebSocket.OPEN) socket.close(code, reason)
    this.setState('closed')
  }

  /** 发送帧（不等待响应）；未连接时返回 false。 */
  send(frame: ClientFrame): boolean {
    return this.sendRaw(frame)
  }

  /** 发送请求并等待同 reqId 的响应帧；超时或断线时 reject。 */
  request<T extends ServerFrame = ServerFrame>(frame: ClientFrame): Promise<T> {
    const reqId = frame.reqId ?? this.nextReqId()
    const withReqId = { ...(frame as object), reqId } as ClientFrame
    return new Promise<T>((resolve, reject) => {
      if (!this.sendRaw(withReqId)) {
        reject(new Error('WebSocket 未连接，无法发送请求'))
        return
      }
      const timer = setTimeout(() => {
        this.pending.delete(reqId)
        reject(new Error(`请求超时（reqId=${reqId}）`))
      }, this.requestTimeoutMs)
      this.pending.set(reqId, {
        resolve: (response) => resolve(response as T),
        reject,
        timer,
        sentAt: Date.now(),
      })
    })
  }

  private handleOpen(socket: WebSocket): void {
    if (this.socket !== socket) return
    // 认证必须是连接后的首帧（protocol.md §1、§4.1）。
    if (!this.authToken) {
      // 登录成功后在路由守卫/登录页先 setAuth(token) 再 connect()（M2-03 已接入）；未注入时服务端会在 10s 后关闭连接。
      console.warn('[ws] 尚未注入 JWT，暂不发送 auth 帧')
      return
    }
    this.sendRaw({
      type: 'auth',
      reqId: this.nextReqId(),
      token: this.authToken,
      deviceId: this.getOrCreateDeviceId(),
      protocolVersion: this.protocolVersion,
    })
  }

  private handleRawMessage(data: unknown): void {
    // 协议只接受文本帧，二进制帧忽略（protocol.md §1）。
    if (typeof data !== 'string') return

    let frame: ServerFrame
    try {
      frame = JSON.parse(data) as ServerFrame
    } catch {
      console.warn('[ws] 丢弃非法 JSON 帧（protocol.md §2）')
      return
    }
    if (!frame || typeof frame !== 'object' || typeof frame.type !== 'string') {
      console.warn('[ws] 丢弃缺少 type 的帧')
      return
    }

    // 收到任意帧都视为连接存活（protocol.md §1 心跳规则）。
    this.markAlive()

    // reqId 匹配：有等待中的请求时优先交给请求方，不再作为推送分发。
    if (frame.reqId) {
      const pending = this.pending.get(frame.reqId)
      if (pending) {
        clearTimeout(pending.timer)
        this.pending.delete(frame.reqId)
        pending.resolve(frame)
        return
      }
    }

    this.dispatch(frame)
  }

  private dispatch(frame: ServerFrame): void {
    switch (frame.type) {
      case 'hello':
        if (frame.protocolVersion !== this.protocolVersion) {
          // protocol.md §4.1：客户端收到不兼容版本必须提示升级并主动关闭，不能进入聊天。
          const reason = `协议版本不兼容（服务端 ${frame.protocolVersion}，客户端 ${this.protocolVersion}）`
          console.warn(`[ws] ${reason}`)
          this.emitFatal(4002, reason)
          this.closeSocket(4002, reason)
          break
        }
        this.reconnectAttempt = 0
        this.fatalEmitted = false
        this.setState('authed')
        this.startHeartbeat()
        break
      case 'ping':
        this.sendRaw({ type: 'pong', ts: frame.ts })
        break
      case 'kicked':
        // 被同账号新连接踢下线：不发重连、通知上层清理并回登录页（protocol.md §1）。
        this.manualClose = true
        this.emit('kicked', frame)
        this.emitFatal(4001, frame.reason || '账号连接数超出上限，已在该端保持登录')
        break
      case 'error':
        this.emit('error', frame)
        break
      default:
        // 其余业务帧与未知 type：未知 type 忽略且不断连（protocol.md §2），业务帧由上层处理。
        break
    }
    this.emit('frame', frame)
  }

  private handleClose(socket: WebSocket, event: CloseEvent): void {
    if (this.socket !== socket) return
    this.socket = null
    this.clearHeartbeat()
    this.rejectAllPending(new Error(`连接已关闭（close code ${event.code}）`))

    // 致命关闭码（4001 被踢 / 4002 认证类失败）：不自动重连，并通知上层清会话回登录页（M3-00）。
    if (this.manualClose) {
      this.setState('closed')
      return
    }
    if (TERMINAL_CLOSE_CODES.has(event.code)) {
      this.emitFatal(
        event.code,
        event.reason || (event.code === 4001 ? '账号连接数超出上限，已在该端保持登录' : '认证失败或已过期'),
      )
      this.setState('closed')
      return
    }
    this.scheduleReconnect()
  }

  private scheduleReconnect(): void {
    if (this.reconnectTimer) return
    this.setState('reconnecting')
    const delay = reconnectDelayMs(this.reconnectAttempt)
    this.reconnectAttempt += 1
    console.info(`[ws] ${Math.round(delay / 100) / 10}s 后重连（第 ${this.reconnectAttempt} 次）`)
    this.reconnectTimer = setTimeout(() => {
      this.reconnectTimer = null
      this.connect()
    }, delay)
  }

  private startHeartbeat(): void {
    this.clearHeartbeat()
    this.heartbeatTimer = setInterval(() => {
      this.sendRaw({ type: 'ping', ts: Date.now() })
    }, this.heartbeatSec * 1000)
    this.markAlive()
  }

  private markAlive(): void {
    if (this.heartbeatDeadline) clearTimeout(this.heartbeatDeadline)
    this.heartbeatDeadline = setTimeout(() => {
      console.warn('[ws] 心跳超时，按断线处理')
      this.socket?.close()
    }, this.heartbeatTimeoutSec * 1000)
  }

  private clearHeartbeat(): void {
    if (this.heartbeatTimer) {
      clearInterval(this.heartbeatTimer)
      this.heartbeatTimer = null
    }
    if (this.heartbeatDeadline) {
      clearTimeout(this.heartbeatDeadline)
      this.heartbeatDeadline = null
    }
  }

  private clearReconnectTimer(): void {
    if (this.reconnectTimer) {
      clearTimeout(this.reconnectTimer)
      this.reconnectTimer = null
    }
  }

  private rejectAllPending(error: Error): void {
    for (const [reqId, pending] of this.pending) {
      clearTimeout(pending.timer)
      pending.reject(error)
      this.pending.delete(reqId)
    }
  }

  private sendRaw(frame: ClientFrame): boolean {
    const socket = this.socket
    if (!socket || socket.readyState !== WebSocket.OPEN) return false
    try {
      socket.send(JSON.stringify(frame))
      return true
    } catch (error) {
      console.warn('[ws] 发送帧失败', error)
      return false
    }
  }

  private nextReqId(): string {
    this.requestSeq += 1
    return `c-${Date.now().toString(36)}-${this.requestSeq}`
  }

  private getOrCreateDeviceId(): string {
    if (this.deviceId) return this.deviceId
    const storageKey = 'eisechat.deviceId'
    const existing = readStorage(storageKey)
    if (existing) {
      this.deviceId = existing
      return existing
    }
    // 同一设备重连复用同一 deviceId（protocol.md §4.1）；本地存储不可用时退化为固定值。
    const created = `web-${crypto.randomUUID()}`
    writeStorage(storageKey, created)
    this.deviceId = created
    return created
  }

  /** 通知一次致命关闭（kicked 帧与随后的关闭事件只触发一次）。 */
  private emitFatal(code: number, reason: string): void {
    if (this.fatalEmitted) return
    this.fatalEmitted = true
    this.emit('fatalClose', { code, reason })
  }

  /** 主动关闭且不再重连（协议版本不兼容等本地判定为致命的场景）。 */
  private closeSocket(code: number, reason: string): void {
    this.manualClose = true
    this.clearReconnectTimer()
    this.clearHeartbeat()
    this.rejectAllPending(new Error(reason))
    const socket = this.socket
    this.socket = null
    if (socket && socket.readyState === WebSocket.OPEN) socket.close(code, reason)
    this.setState('closed')
  }

  private setState(next: WsState): void {
    if (this.state === next) return
    this.state = next
    this.emit('statechange', next)
  }

  private emit<K extends keyof WsEventMap>(event: K, payload: WsEventMap[K]): void {
    const handlers = this.handlers.get(event)
    if (!handlers) return
    for (const handler of handlers) {
      ;(handler as unknown as (value: WsEventMap[K]) => void)(payload)
    }
  }
}

/** 全局唯一连接实例（docs/project-overview.md §9/§10：业务代码不得绕过本模块直连 WebSocket）。 */
export const chatWs = new ChatWsClient()
