// 通话信令客户端（protocol.md §3.1）：只负责 call.* 帧的收发与状态机，不做 WebRTC。
// 媒体由 stores/call.ts 用 RTCPeerConnection 处理；本文件保持可单测（不依赖 DOM）。
import { chatWs } from './ws'
import type { CallKind, CallPeer, CallStateText } from '../types/frames'

export type CallPhase = 'idle' | 'outgoing' | 'incoming' | 'connecting' | 'active'

export interface CallSnapshot {
  callId: string
  conversationId: string
  kind: CallKind
  phase: CallPhase
  /** 本端是主叫还是被叫；重连同步时按服务端 call.state.direction 恢复。 */
  direction: 'outgoing' | 'incoming'
  peer: CallPeer | null
  createdAt: number
  answeredAt: number | null
  connectedAt: number | null
}

export interface CallMediaSignals {
  /** 收到远端 SDP（含 offer / answer）。 */
  onSdp: (payload: { type: 'offer' | 'answer'; sdp: string }) => void
  /** 收到远端 ICE。 */
  onIce: (candidate: RTCIceCandidateInit) => void
  /** 通话结束（本地或远端发起），参数为 end.cancel 的 reason。 */
  onEnded: (reason: string) => void
  /** 本端不能接听（Web/旧客户端）时的来电提示。 */
  onUnsupportedIncoming?: (info: { peer: CallPeer; kind: CallKind }) => void
  /** 上面那通来电被主叫取消/超时结束。 */
  onUnsupportedIncomingEnded?: () => void
}

/** 通话状态变化的监听器签名。 */
export type CallChangeListener = (snapshot: CallSnapshot | null) => void

class CallClient {
  private snapshot: CallSnapshot | null = null
  private readonly listeners = new Set<CallChangeListener>()
  private media: CallMediaSignals | null = null
  private attached = false
  /** 只用于提示“请在其他设备接听”的来电 ID（本端不接听，不建快照）。 */
  private unsupportedCallId: string | null = null

  /** 当前通话快照（无通话为 null）。 */
  get current(): CallSnapshot | null {
    return this.snapshot
  }

  /** 订阅状态变化；返回取消订阅函数。 */
  subscribe(listener: CallChangeListener): () => void {
    this.listeners.add(listener)
    return () => {
      this.listeners.delete(listener)
    }
  }

  /** 绑定媒体回调（由 Pinia store 注入）。 */
  bindMedia(signals: CallMediaSignals | null): void {
    this.media = signals
  }

  /** 开始消费 WS 帧（幂等）。 */
  attach(): void {
    if (this.attached) return
    this.attached = true
    chatWs.on('frame', (frame) => this.handleFrame(frame))
  }

  /** 主叫发起通话。 */
  invite(conversationId: string, kind: CallKind): string {
    const callId = crypto.randomUUID()
    this.snapshot = {
      callId,
      conversationId,
      kind,
      phase: 'outgoing',
      direction: 'outgoing',
      peer: null,
      createdAt: Date.now(),
      answeredAt: null,
      connectedAt: null,
    }
    chatWs.send({ type: 'call.invite', reqId: callId, callId, conversationId, kind })
    this.emit()
    return callId
  }

  accept(): void {
    const current = this.snapshot
    if (!current || current.phase !== 'incoming') return
    this.patch({ phase: 'connecting' })
    chatWs.send({ type: 'call.accept', callId: current.callId })
  }

  reject(reason = 'declined'): void {
    const current = this.snapshot
    if (!current) return
    chatWs.send({ type: 'call.reject', callId: current.callId, reason })
    this.clear(reason)
  }

  hangUp(reason = 'completed'): void {
    const current = this.snapshot
    if (!current) return
    chatWs.send({ type: 'call.end', callId: current.callId, reason })
    this.clear(reason)
  }

  sendSdp(type: 'offer' | 'answer', sdp: string): void {
    const current = this.snapshot
    if (!current) return
    chatWs.send({ type: 'call.sdp', callId: current.callId, sdp: { type, sdp } })
  }

  sendIce(candidate: RTCIceCandidateInit): void {
    const current = this.snapshot
    if (!current) return
    chatWs.send({
      type: 'call.ice',
      callId: current.callId,
      candidate: {
        candidate: candidate.candidate ?? '',
        sdpMid: candidate.sdpMid ?? null,
        sdpMLineIndex: candidate.sdpMLineIndex ?? null,
        usernameFragment: candidate.usernameFragment ?? null,
      },
    })
  }

  /** 首次收到远端媒体后上报（服务端只记一次）。 */
  sendConnected(): void {
    const current = this.snapshot
    if (!current || current.connectedAt !== null) return
    this.patch({ phase: 'active', connectedAt: Date.now() })
    chatWs.send({ type: 'call.connected', callId: current.callId })
  }

  private handleFrame(frame: unknown): void {
    const typed = frame as { type?: string }
    switch (typed.type) {
      case 'call.incoming': {
        const incoming = frame as {
          callId: string
          conversationId: string
          kind: CallKind
          peer: CallPeer
          createdAt: number
          callSupported?: boolean
        }
        // Web 与旧客户端不能接听：只提示“请在其他设备接听”，不进入来电状态。
        if (incoming.callSupported === false) {
          if (this.snapshot) return
          this.unsupportedCallId = incoming.callId
          this.media?.onUnsupportedIncoming?.({ peer: incoming.peer, kind: incoming.kind })
          break
        }
        if (this.snapshot) return
        this.snapshot = {
          callId: incoming.callId,
          conversationId: incoming.conversationId,
          kind: incoming.kind,
          phase: 'incoming',
          direction: 'incoming',
          peer: incoming.peer,
          createdAt: incoming.createdAt,
          answeredAt: null,
          connectedAt: null,
        }
        this.emit()
        break
      }
      case 'call.state': {
        const state = frame as {
          callId?: string | null
          state?: CallStateText | null
          kind?: CallKind | null
          peer?: CallPeer | null
          createdAt?: number | null
          answeredAt?: number | null
          connectedAt?: number | null
          direction?: 'outgoing' | 'incoming' | null
        }
        if (!state.callId) {
          if (this.snapshot) {
            this.clear('sync')
          }
          if (this.unsupportedCallId) {
            this.unsupportedCallId = null
            this.media?.onUnsupportedIncomingEnded?.()
          }
          break
        }
        if (!this.snapshot) {
          // 重连后恢复进行中的通话：按服务端状态重建快照（按 direction 区分主被叫）。
          const direction = state.direction ?? 'incoming'
          this.snapshot = {
            callId: state.callId,
            conversationId: '',
            kind: state.kind ?? 'audio',
            phase:
              state.state === 'ringing'
                ? direction === 'outgoing'
                  ? 'outgoing'
                  : 'incoming'
                : state.state === 'active'
                  ? 'active'
                  : 'connecting',
            direction,
            peer: state.peer ?? null,
            createdAt: state.createdAt ?? Date.now(),
            answeredAt: state.answeredAt ?? null,
            connectedAt: state.connectedAt ?? null,
          }
          this.emit()
          break
        }
        if (this.snapshot.callId !== state.callId) break
        this.patch({
          peer: state.peer ?? this.snapshot.peer,
          answeredAt: state.answeredAt ?? this.snapshot.answeredAt,
          connectedAt: state.connectedAt ?? this.snapshot.connectedAt,
          phase:
            state.state === 'active'
              ? 'active'
              : state.state === 'connecting'
                ? 'connecting'
                : this.snapshot.phase,
        })
        break
      }
      case 'call.accepted': {
        const accepted = frame as { callId: string; peer: CallPeer; answeredAt: number }
        if (!this.snapshot || this.snapshot.callId !== accepted.callId) return
        this.patch({ phase: 'connecting', peer: accepted.peer, answeredAt: accepted.answeredAt })
        break
      }
      case 'call.sdp': {
        const sdp = frame as { callId: string; sdp: { type: 'offer' | 'answer'; sdp: string } }
        if (!this.snapshot || this.snapshot.callId !== sdp.callId) return
        this.media?.onSdp(sdp.sdp)
        break
      }
      case 'call.ice': {
        const ice = frame as { callId: string; candidate: RTCIceCandidateInit }
        if (!this.snapshot || this.snapshot.callId !== ice.callId) return
        this.media?.onIce(ice.candidate)
        break
      }
      case 'call.cancel':
      case 'call.end': {
        const ended = frame as { callId: string; reason?: string | null }
        if (ended.callId === this.unsupportedCallId) {
          this.unsupportedCallId = null
          this.media?.onUnsupportedIncomingEnded?.()
          return
        }
        if (!this.snapshot || this.snapshot.callId !== ended.callId) return
        this.clear(ended.reason ?? (typed.type === 'call.end' ? 'completed' : 'canceled'))
        break
      }
      case 'error': {
        // 服务端拒绝呼叫（忙、不可达、被拉黑等）：reqId 即 callId，收到后就地收场。
        const error = frame as { code?: string; reqId?: string | null }
        if (!this.snapshot || !error.reqId || this.snapshot.callId !== error.reqId) return
        this.clear(`error:${error.code ?? 'UNKNOWN'}`)
        break
      }
      default:
        break
    }
  }

  private patch(partial: Partial<CallSnapshot>): void {
    if (!this.snapshot) return
    this.snapshot = { ...this.snapshot, ...partial }
    this.emit()
  }

  private clear(reason: string): void {
    if (!this.snapshot) return
    this.snapshot = null
    this.emit()
    this.media?.onEnded(reason)
  }

  private emit(): void {
    const snapshot = this.snapshot
    for (const listener of this.listeners) {
      listener(snapshot)
    }
  }
}

export const callClient = new CallClient()
