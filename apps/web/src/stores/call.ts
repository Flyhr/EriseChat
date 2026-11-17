// 通话状态与媒体（protocol.md §3.1，V0.4 草案）：把 core/call.ts 的信令状态映射成界面状态，
// 并负责 getUserMedia / RTCPeerConnection 生命周期。只有 Windows 壳与 Android 壳会用到。
import { computed, ref } from 'vue'
import { defineStore } from 'pinia'
import { callClient, type CallPhase, type CallSnapshot } from '../core/call'
import type { CallKind } from '../types/frames'

/** 通话媒体在界面上的阶段。 */
export type MediaPhase = 'idle' | 'preparing' | 'waiting' | 'connected' | 'failed'

export const useCallStore = defineStore('call', () => {
  const snapshot = ref<CallSnapshot | null>(null)
  const mediaPhase = ref<MediaPhase>('idle')
  const errorMessage = ref('')
  const muted = ref(false)
  const cameraOff = ref(false)
  const elapsedSeconds = ref(0)

  let localStream: MediaStream | null = null
  let remoteStream: MediaStream | null = null
  let peer: RTCPeerConnection | null = null
  let timer: ReturnType<typeof setInterval> | null = null
  let pendingIce: RTCIceCandidateInit[] = []
  let attached = false
  const localStreamRef = ref<MediaStream | null>(null)
  const remoteStreamRef = ref<MediaStream | null>(null)

  const phase = computed<CallPhase>(() => snapshot.value?.phase ?? 'idle')
  const isIncoming = computed(() => phase.value === 'incoming')
  const isActive = computed(() => phase.value === 'active')
  const peerName = computed(() => snapshot.value?.peer?.displayName ?? '对方')
  const kind = computed<CallKind>(() => snapshot.value?.kind ?? 'audio')
  const elapsedText = computed(() => formatDuration(elapsedSeconds.value))

  function attach(): void {
    if (attached) return
    attached = true
    callClient.bindMedia({
      onSdp: (payload) => void handleRemoteSdp(payload),
      onIce: (candidate) => void handleRemoteIce(candidate),
      onEnded: () => cleanup('ended'),
    })
    callClient.subscribe((next) => {
      snapshot.value = next
      if (!next) {
        cleanup('ended')
      }
    })
    callClient.attach()
  }

  async function startCall(conversationId: string, callKind: CallKind): Promise<void> {
    if (snapshot.value) return
    attach()
    errorMessage.value = ''
    mediaPhase.value = 'preparing'
    callClient.invite(conversationId, callKind)
    try {
      await openMedia(callKind)
      const offer = await peer!.createOffer({ offerToReceiveAudio: true, offerToReceiveVideo: callKind === 'video' })
      await peer!.setLocalDescription(offer)
      callClient.sendSdp('offer', offer.sdp ?? '')
      mediaPhase.value = 'waiting'
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

    const connection = new RTCPeerConnection({ iceServers: [] })
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
        mediaPhase.value = 'connected'
        callClient.sendConnected()
        startTimer()
      } else if (state === 'failed' || state === 'disconnected') {
        mediaPhase.value = 'failed'
        callClient.hangUp(state === 'failed' ? 'failed' : 'disconnected')
      }
    }
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

  return {
    snapshot,
    phase,
    mediaPhase,
    errorMessage,
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
