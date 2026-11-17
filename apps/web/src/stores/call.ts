// 通话状态与媒体（protocol.md §3.1，V0.4 草案）：把 core/call.ts 的信令状态映射成界面状态，
// 并负责 getUserMedia / RTCPeerConnection 生命周期。只有 Windows 壳与 Android 壳会用到。
import { computed, ref } from 'vue'
import { defineStore } from 'pinia'
import { callsApi } from '../api/calls'
import { callClient, type CallPhase, type CallSnapshot } from '../core/call'
import { ringtone } from '../core/ringtone'
import { showToast } from '../core/toast'
import type { CallKind, CallPeer } from '../types/frames'

/** 通话媒体在界面上的阶段。 */
export type MediaPhase = 'idle' | 'preparing' | 'waiting' | 'connected' | 'failed'

/** TURN 凭证缓存：服务端默认 1 小时有效，这里保守缓存 10 分钟。 */
let iceCache: { servers: RTCIceServer[]; expiresAt: number } | null = null

async function loadIceServers(): Promise<RTCIceServer[]> {
  if (iceCache && iceCache.expiresAt > Date.now()) return iceCache.servers
  try {
    const response = await callsApi.iceServers()
    const servers: RTCIceServer[] = response.iceServers.map((server) => ({
      urls: server.urls,
      username: server.username,
      credential: server.credential,
    }))
    iceCache = { servers, expiresAt: Date.now() + 10 * 60 * 1000 }
    return servers
  } catch {
    // 取不到就退回纯 P2P：局域网与直连场景仍可通话。
    return []
  }
}

/** 服务端拒绝呼叫时的用户可见说明（错误码见 protocol.md §5.1）。 */
const CALL_ERROR_TEXT: Record<string, string> = {
  CALL_BUSY: '对方正在通话中',
  CALL_FORBIDDEN: '无法呼叫该用户',
  CALL_NOT_FOUND: '通话已结束',
  CALL_UNSUPPORTED: '当前会话不支持通话',
}

/** 结束提示按主被叫区分：被叫不该看到「对方未接听 / 对方已拒绝」这类主叫视角的文案。 */
function endTextFor(reason: string, callerSide: boolean): string {
  if (reason === 'declined') return callerSide ? '对方已拒绝' : ''
  if (reason === 'timeout') return callerSide ? '对方未接听' : '未接来电'
  if (reason === 'answered_elsewhere') return '已在其他设备接听'
  if (reason === 'disconnected') return '通话连接中断'
  if (reason === 'failed') return '通话未能建立'
  // canceled：主叫自己取消、被叫收到「对方已取消」；completed 等本地主动挂断不提示。
  return reason === 'canceled' && !callerSide ? '对方已取消' : ''
}

/** 断线宽限：与服务端 Call:DisconnectGraceSeconds 保持一致。 */
const DISCONNECT_GRACE_MS = 10_000

export const useCallStore = defineStore('call', () => {
  const snapshot = ref<CallSnapshot | null>(null)
  const mediaPhase = ref<MediaPhase>('idle')
  const errorMessage = ref('')
  const muted = ref(false)
  const cameraOff = ref(false)
  const elapsedSeconds = ref(0)
  /** 本端不能接听时的来电提示（Web/旧客户端）：只展示“请在其他设备接听”。 */
  const remoteRing = ref<{ peer: CallPeer; kind: CallKind } | null>(null)

  let localStream: MediaStream | null = null
  let remoteStream: MediaStream | null = null
  let peer: RTCPeerConnection | null = null
  let timer: ReturnType<typeof setInterval> | null = null
  let disconnectTimer: ReturnType<typeof setTimeout> | null = null
  let pendingIce: RTCIceCandidateInit[] = []
  let pendingRemoteSdp: { type: 'offer' | 'answer'; sdp: string } | null = null
  let attached = false
  /** 本端最近一次通话的角色（快照清空后仍保留，用于结束提示文案）。 */
  let lastDirection: 'outgoing' | 'incoming' = 'outgoing'
  const localStreamRef = ref<MediaStream | null>(null)
  const remoteStreamRef = ref<MediaStream | null>(null)

  const phase = computed<CallPhase>(() => snapshot.value?.phase ?? 'idle')
  const isIncoming = computed(() => phase.value === 'incoming')
  const isActive = computed(() => phase.value === 'active')
  const peerName = computed(() => snapshot.value?.peer?.displayName ?? '对方')
  const kind = computed<CallKind>(() => snapshot.value?.kind ?? 'audio')
  const elapsedText = computed(() => formatDuration(elapsedSeconds.value))
  const remoteRingText = computed(() => {
    const ring = remoteRing.value
    if (!ring) return ''
    const name = ring.peer.displayName || '有用户'
    return `${name} 发起${ring.kind === 'video' ? '视频' : '语音'}通话，请在 Windows 或 Android 客户端接听`
  })

  function attach(): void {
    if (attached) return
    attached = true
    callClient.bindMedia({
      onSdp: (payload) => handleRemoteSdpSync(payload),
      onIce: (candidate) => void handleRemoteIce(candidate),
      onUnsupportedIncoming: (info) => {
        remoteRing.value = info
      },
      onUnsupportedIncomingEnded: () => {
        remoteRing.value = null
      },
      onEnded: (reason) => {
        cleanup(reason)
        // 呼叫被服务端拒绝时给出明确提示，避免界面一直停在「正在呼叫」。
        if (reason.startsWith('error:')) {
          errorMessage.value = CALL_ERROR_TEXT[reason.slice(6)] ?? '通话未能建立'
          showToast(errorMessage.value, 'error')
          return
        }
        const endedText = endTextFor(reason, lastDirection === 'outgoing')
        if (endedText) {
          showToast(endedText, reason === 'failed' || reason === 'disconnected' ? 'error' : 'info')
        }
      },
    })
    callClient.subscribe((next) => {
      snapshot.value = next
      // 记住本端角色：结束时快照已清空，提示文案要靠它区分主被叫。
      if (next) {
        lastDirection = next.direction
      }
      // 回铃音只在主叫振铃阶段响：接通、结束或被叫侧都不响。
      if (next?.phase === 'outgoing') {
        ringtone.start()
      } else {
        ringtone.stop()
      }
      if (!next) {
        cleanup('ended')
      }
    })
    callClient.attach()
  }

  async function startCall(conversationId: string, callKind: CallKind): Promise<void> {
    if (snapshot.value) return
    attach()
    remoteRing.value = null
    errorMessage.value = ''
    mediaPhase.value = 'preparing'
    callClient.invite(conversationId, callKind)
    try {
      await openMedia(callKind)
      // 呼叫可能在准备媒体期间就被服务端拒绝（如对方不可达），此时不再协商。
      const invited = callClient.current
      if (!invited) {
        cleanup('error')
        return
      }
      mediaPhase.value = 'waiting'
      // 振铃阶段服务端不转发媒体帧：等对方接听（accepted → connecting）后再发 offer。
      if (invited.phase !== 'connecting') {
        await new Promise<void>((resolve) => {
          const stop = callClient.subscribe((next) => {
            if (!next || next.phase === 'connecting') {
              stop()
              resolve()
            }
          })
        })
      }
      if (!callClient.current) return
      const offer = await peer!.createOffer({ offerToReceiveAudio: true, offerToReceiveVideo: callKind === 'video' })
      await peer!.setLocalDescription(offer)
      callClient.sendSdp('offer', offer.sdp ?? '')
    } catch (error) {
      fail(error)
    }
  }

  async function acceptCall(): Promise<void> {
    const current = snapshot.value
    if (!current || current.phase !== 'incoming') return
    errorMessage.value = ''
    mediaPhase.value = 'preparing'
    try {
      await openMedia(current.kind)
      callClient.accept()
      mediaPhase.value = 'waiting'
      // 对方的 offer 可能比接听动作早到，缓冲区里补处理。
      if (pendingRemoteSdp) {
        const pending = pendingRemoteSdp
        pendingRemoteSdp = null
        await handleRemoteSdp(pending)
      }
    } catch (error) {
      // 麦克风拿不到时明确拒绝，避免对方一直听到静音。
      fail(error)
      callClient.reject('no_media')
    }
  }

  function rejectCall(): void {
    callClient.reject('declined')
  }

  function hangUp(): void {
    callClient.hangUp(elapsedSeconds.value > 0 ? 'completed' : 'canceled')
  }

  function toggleMute(): void {
    const stream = localStream
    if (!stream) return
    muted.value = !muted.value
    for (const track of stream.getAudioTracks()) {
      track.enabled = !muted.value
    }
  }

  function toggleCamera(): void {
    const stream = localStream
    if (!stream) return
    cameraOff.value = !cameraOff.value
    for (const track of stream.getVideoTracks()) {
      track.enabled = !cameraOff.value
    }
  }

  async function openMedia(callKind: CallKind): Promise<void> {
    const stream = await navigator.mediaDevices.getUserMedia({
      audio: true,
      video: callKind === 'video' ? { width: { ideal: 1280 }, height: { ideal: 720 }, facingMode: 'user' } : false,
    })
    localStream = stream
    localStreamRef.value = stream

    const connection = new RTCPeerConnection({ iceServers: await loadIceServers() })
    peer = connection
    for (const track of stream.getTracks()) {
      connection.addTrack(track, stream)
    }

    connection.onicecandidate = (event) => {
      if (event.candidate) {
        callClient.sendIce(event.candidate.toJSON())
      }
    }
    connection.ontrack = (event) => {
      const [remote] = event.streams
      if (remote) {
        remoteStream = remote
        remoteStreamRef.value = remote
      }
    }
    connection.onconnectionstatechange = () => {
      const state = connection.connectionState
      if (state === 'connected') {
        clearDisconnectTimer()
        mediaPhase.value = 'connected'
        callClient.sendConnected()
        startTimer()
        return
      }
      if (state === 'disconnected') {
        // 弱网抖动很常见：先尝试 ICE 重启，10 秒内没恢复才挂断（与服务端宽限一致）。
        void connection.restartIce()
        if (!disconnectTimer) {
          disconnectTimer = setTimeout(() => {
            disconnectTimer = null
            if (peer !== connection) return
            mediaPhase.value = 'failed'
            callClient.hangUp('disconnected')
          }, DISCONNECT_GRACE_MS)
        }
        return
      }
      if (state === 'failed') {
        clearDisconnectTimer()
        mediaPhase.value = 'failed'
        callClient.hangUp('failed')
      }
    }
  }

  function handleRemoteSdpSync(payload: { type: 'offer' | 'answer'; sdp: string }): void {
    // 媒体还没准备好时先缓存，等 openMedia 完成后补处理。
    if (!peer) {
      pendingRemoteSdp = payload
      return
    }
    void handleRemoteSdp(payload)
  }

  async function handleRemoteSdp(payload: { type: 'offer' | 'answer'; sdp: string }): Promise<void> {
    if (!peer) return
    try {
      await peer.setRemoteDescription({ type: payload.type, sdp: payload.sdp })
      for (const candidate of pendingIce) {
        await peer.addIceCandidate(candidate)
      }
      pendingIce = []
      if (payload.type === 'offer') {
        const answer = await peer.createAnswer()
        await peer.setLocalDescription(answer)
        callClient.sendSdp('answer', answer.sdp ?? '')
      }
    } catch (error) {
      fail(error)
    }
  }

  async function handleRemoteIce(candidate: RTCIceCandidateInit): Promise<void> {
    if (!peer) return
    if (!peer.remoteDescription) {
      pendingIce.push(candidate)
      return
    }
    try {
      await peer.addIceCandidate(candidate)
    } catch {
      // 迟到或重复的 candidate 直接忽略。
    }
  }

  function startTimer(): void {
    if (timer) return
    elapsedSeconds.value = 0
    timer = setInterval(() => {
      elapsedSeconds.value += 1
    }, 1000)
  }

  function cleanup(_reason: string): void {
    ringtone.stop()
    clearDisconnectTimer()
    if (timer) {
      clearInterval(timer)
      timer = null
    }
    for (const track of localStream?.getTracks() ?? []) {
      track.stop()
    }
    localStream = null
    localStreamRef.value = null
    remoteStream = null
    remoteStreamRef.value = null
    peer?.close()
    peer = null
    pendingIce = []
    pendingRemoteSdp = null
    muted.value = false
    cameraOff.value = false
    mediaPhase.value = 'idle'
    elapsedSeconds.value = 0
    snapshot.value = callClient.current
  }

  function fail(error: unknown): void {
    mediaPhase.value = 'failed'
    errorMessage.value = error instanceof Error ? error.message : String(error)
    if (snapshot.value) {
      callClient.hangUp('failed')
    }
  }

  function dismissRemoteRing(): void {
    remoteRing.value = null
  }

  function clearDisconnectTimer(): void {
    if (!disconnectTimer) return
    clearTimeout(disconnectTimer)
    disconnectTimer = null
  }

  return {
    snapshot,
    phase,
    mediaPhase,
    errorMessage,
    remoteRing,
    remoteRingText,
    muted,
    cameraOff,
    elapsedSeconds,
    elapsedText,
    localStream: localStreamRef,
    remoteStream: remoteStreamRef,
    isIncoming,
    isActive,
    peerName,
    kind,
    attach,
    startCall,
    acceptCall,
    rejectCall,
    hangUp,
    dismissRemoteRing,
    toggleMute,
    toggleCamera,
  }
})

/** 秒数格式化为 mm:ss（超 1 小时 h:mm:ss），与时间线记录一致。 */
export function formatDuration(totalSeconds: number): string {
  const seconds = Math.max(0, Math.floor(totalSeconds))
  if (seconds >= 3600) {
    return `${Math.floor(seconds / 3600)}:${String(Math.floor((seconds % 3600) / 60)).padStart(2, '0')}:${String(seconds % 60).padStart(2, '0')}`
  }
  return `${String(Math.floor(seconds / 60)).padStart(2, '0')}:${String(seconds % 60).padStart(2, '0')}`
}
