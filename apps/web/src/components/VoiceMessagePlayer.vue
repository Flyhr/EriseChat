<script setup lang="ts">
import { computed, onBeforeUnmount, ref } from 'vue'
import AppIcon from './AppIcon.vue'
import { filesApi } from '../api/files'
import { getOrLoadBlobUrl } from '../core/blobCache'
import {
  clampVoiceSeekMs,
  formatDuration,
  voiceProgressRatio,
  voiceSeekTimeText,
} from '../core/voice'
import { claimVoicePlayback, clearVoicePosition, recallVoicePosition, releaseVoicePlayback, rememberVoicePosition } from '../core/voicePlayer'

// F-28：语音消息播放器（播放/暂停 + 进度条拖拽定位）。
// 点击播放时才下载（走 filesApi.downloadBlob + blobCache），不在历史渲染时预取；
// 同一时间只允许一条语音播放（core/voicePlayer 负责全局互斥），播放结束/出错自动复位。
// 进度以 content.durationMs 为准（MediaRecorder 产出的 webm 可能缺时长元数据），不读 audio.duration。
const props = defineProps<{
  fileId: string
  durationMs: number
}>()

type PlayerState = 'idle' | 'loading' | 'playing' | 'error'

/** C2b：播放速度档位，按 1× → 1.5× → 2× 循环切换，播放中立即生效。 */
const RATES = [1, 1.5, 2] as const
const rate = ref<number>(1)
const rateLabel = computed(() => `${rate.value}×`)

const state = ref<PlayerState>('idle')
/** 当前播放位置（毫秒）。 */
const progressMs = ref(0)
/** 拖动中的位置；null 表示未拖动。 */
const seekMs = ref<number | null>(null)
let audio: HTMLAudioElement | null = null
let disposed = false
/** audio 尚未创建时点进度条记下的位置，播放时从该处开始。 */
let pendingSeekMs = 0
/** 用户本会话内明确拖动/键盘定位过：下次播放以最新位置为准，不再套用暂停时缓存的旧位置。 */
let userSeeked = false

const totalMs = computed(() =>
  Number.isFinite(props.durationMs) && props.durationMs > 0 ? Math.floor(props.durationMs) : 0,
)
const displayMs = computed(() => seekMs.value ?? progressMs.value)
const ratio = computed(() => voiceProgressRatio(displayMs.value, props.durationMs))
const timeText = computed(() => {
  if (state.value === 'error') return '语音加载失败'
  if (state.value === 'loading') return '加载中…'
  if (seekMs.value !== null) return voiceSeekTimeText(displayMs.value, props.durationMs)
  return formatDuration(totalMs.value)
})
const actionLabel = computed(() => (state.value === 'playing' ? '暂停语音' : '播放语音'))
const trackDisabled = computed(() => state.value === 'loading' || state.value === 'error')

/** 记住当前播放位置，供切换会话/暂停后继续（播完、出错时不记）。 */
function rememberPosition(): void {
  if (!audio) return
  const currentMs = Math.round(audio.currentTime * 1000)
  if (currentMs > 0 && currentMs < totalMs.value) rememberVoicePosition(props.fileId, currentMs)
}

/** 停止并复位（播放结束、被其它语音抢占或组件卸载时调用）。 */
function stopPlayback(): void {
  if (state.value === 'playing') rememberPosition()
  if (audio) {
    audio.pause()
    audio.currentTime = 0
  }
  progressMs.value = 0
  seekMs.value = null
  pendingSeekMs = 0
  userSeeked = false
  if (state.value === 'playing') state.value = 'idle'
  releaseVoicePlayback(stopPlayback)
}

function onEnded(): void {
  progressMs.value = 0
  seekMs.value = null
  pendingSeekMs = 0
  userSeeked = false
  state.value = 'idle'
  clearVoicePosition(props.fileId)
  releaseVoicePlayback(stopPlayback)
}

function onTimeUpdate(): void {
  if (!audio || seekMs.value !== null) return
  const currentMs = Math.round(audio.currentTime * 1000)
  progressMs.value = totalMs.value > 0 ? Math.min(totalMs.value, Math.max(0, currentMs)) : Math.max(0, currentMs)
}

function onError(): void {
  state.value = 'error'
  progressMs.value = 0
  seekMs.value = null
  userSeeked = false
  clearVoicePosition(props.fileId)
  releaseVoicePlayback(stopPlayback)
}

/** 定位到指定毫秒（夹到 0~总时长；暂停状态下只改位置不播放）。 */
function applySeek(ms: number): void {
  const target = clampVoiceSeekMs(ms, props.durationMs)
  progressMs.value = target
  pendingSeekMs = target
  userSeeked = true
  // 以最新拖动位置覆盖暂停时缓存的位置，避免下次播放被旧位置盖掉；
  // 拖回起点时同步清除记录，避免离开会话再回来从旧位置续播。
  if (target > 0 && target < totalMs.value) rememberVoicePosition(props.fileId, target)
  else clearVoicePosition(props.fileId)
  if (!audio) return
  if (target >= totalMs.value && totalMs.value > 0) {
    // 拖到最右端：按播放结束处理（复位）
    audio.pause()
    onEnded()
    return
  }
  audio.currentTime = target / 1000
}

function msFromPointer(event: PointerEvent): number {
  const el = event.currentTarget as HTMLElement
  const rect = el.getBoundingClientRect()
  if (rect.width <= 0 || totalMs.value <= 0) return 0
  const clicked = (event.clientX - rect.left) / rect.width
  return clampVoiceSeekMs(Math.round(clicked * totalMs.value), props.durationMs)
}

function onTrackPointerDown(event: PointerEvent): void {
  if (trackDisabled.value || event.button !== 0) return
  event.preventDefault()
  el(event).setPointerCapture?.(event.pointerId)
  const ms = msFromPointer(event)
  seekMs.value = ms
  // 播放中拖动可先试听；暂停状态不动播放，松手才生效
  if (state.value === 'playing' && audio && ms < totalMs.value) audio.currentTime = ms / 1000
}

function onTrackPointerMove(event: PointerEvent): void {
  if (trackDisabled.value || seekMs.value === null) return
  const ms = msFromPointer(event)
  seekMs.value = ms
  if (state.value === 'playing' && audio && ms < totalMs.value) audio.currentTime = ms / 1000
}

function onTrackPointerUp(event: PointerEvent): void {
  if (seekMs.value === null) return
  const ms = seekMs.value
  el(event).releasePointerCapture?.(event.pointerId)
  seekMs.value = null
  applySeek(ms)
}

function onTrackPointerCancel(): void {
  seekMs.value = null
}

function onTrackKeydown(event: KeyboardEvent): void {
  if (trackDisabled.value) return
  const base = displayMs.value
  let target: number | null = null
  switch (event.key) {
    case 'ArrowLeft':
      target = base - (event.shiftKey ? 5_000 : 1_000)
      break
    case 'ArrowRight':
      target = base + (event.shiftKey ? 5_000 : 1_000)
      break
    case 'Home':
      target = 0
      break
    case 'End':
      target = totalMs.value
      break
    default:
      return
  }
  event.preventDefault()
  applySeek(target)
}

function el(event: Event): HTMLElement {
  return event.currentTarget as HTMLElement
}

async function togglePlayback(): Promise<void> {
  if (state.value === 'loading' || state.value === 'error') return
  if (state.value === 'playing') {
    rememberPosition()
    audio?.pause()
    state.value = 'idle'
    releaseVoicePlayback(stopPlayback)
    return
  }
  if (!audio) {
    state.value = 'loading'
    try {
      const url = await getOrLoadBlobUrl(props.fileId, () => filesApi.downloadBlob(props.fileId))
      if (disposed) return
      audio = new Audio(url)
      audio.playbackRate = rate.value
      audio.addEventListener('ended', onEnded)
      audio.addEventListener('timeupdate', onTimeUpdate)
      audio.addEventListener('error', onError)
      if (pendingSeekMs > 0 && pendingSeekMs < totalMs.value) {
        audio.currentTime = pendingSeekMs / 1000
      }
    } catch {
      if (!disposed) state.value = 'error'
      return
    }
  }
  // C2b：从当前登录期间记录的位置继续（播完已清除，不会把进度按旧值恢复）。
  // 用户刚拖动过时以最新位置为准，不被暂停时缓存的旧位置覆盖。
  const savedMs = userSeeked ? 0 : recallVoicePosition(props.fileId)
  if (savedMs > 0 && savedMs < totalMs.value) {
    pendingSeekMs = savedMs
    audio.currentTime = savedMs / 1000
    progressMs.value = savedMs
  }
  claimVoicePlayback(stopPlayback)
  try {
    await audio.play()
    if (!disposed) {
      state.value = 'playing'
      userSeeked = false
    }
  } catch {
    if (!disposed) state.value = 'error'
    releaseVoicePlayback(stopPlayback)
  }
}

/** 循环切换播放速度：1× → 1.5× → 2×，播放中立即生效。 */
function cycleRate(): void {
  const index = RATES.indexOf(rate.value as (typeof RATES)[number])
  const next = RATES[(index + 1) % RATES.length]
  rate.value = next
  if (audio) audio.playbackRate = next
}

onBeforeUnmount(() => {
  disposed = true
  if (audio) {
    audio.removeEventListener('ended', onEnded)
    audio.removeEventListener('timeupdate', onTimeUpdate)
    audio.removeEventListener('error', onError)
  }
  stopPlayback()
  audio = null
})
</script>

<template>
  <div
    class="voice-player"
    :class="{ 'voice-player--playing': state === 'playing', 'voice-player--error': state === 'error' }"
  >
    <button
      class="voice-player__main"
      type="button"
      :aria-label="actionLabel"
      :disabled="state === 'loading' || state === 'error'"
      @click="togglePlayback"
    >
      <AppIcon :name="state === 'playing' ? 'pause' : 'play'" :size="15" />
    </button>
    <span
      class="voice-player__track"
      role="slider"
      :tabindex="trackDisabled ? -1 : 0"
      :aria-valuemin="0"
      :aria-valuemax="totalMs"
      :aria-valuenow="Math.round(displayMs)"
      :aria-valuetext="timeText"
      aria-label="播放进度"
      @pointerdown="onTrackPointerDown"
      @pointermove="onTrackPointerMove"
      @pointerup="onTrackPointerUp"
      @pointercancel="onTrackPointerCancel"
      @keydown="onTrackKeydown"
    >
      <span class="voice-player__bar">
        <span class="voice-player__fill" :style="{ width: `${Math.round(ratio * 100)}%` }" />
      </span>
    </span>
    <button
      class="voice-player__rate"
      type="button"
      :aria-label="`播放速度 ${rate} 倍，点击切换`"
      :title="`当前播放速度 ${rateLabel}`"
      @click="cycleRate"
    >
      {{ rateLabel }}
    </button>
    <span class="voice-player__time">{{ timeText }}</span>
  </div>
</template>

<style scoped>
.voice-player {
  display: flex;
  align-items: center;
  gap: var(--ec-space-2);
  min-width: 168px;
  max-width: min(260px, 60vw);
  min-height: 36px;
  /* 上下 4px + 按钮 28px 撑满 36px，左右留出与圆角相称的边距，避免按钮贴角。 */
  padding: 4px var(--ec-space-2) 4px 6px;
  border: 1px solid var(--ec-media-border);
  /* 圆角与文件卡一致（--ec-radius-md）；此前误用未定义的 --ec-radius-pill，导致渲染成直角。 */
  border-radius: var(--ec-radius-md);
  background: var(--ec-media-surface);
  color: var(--ec-text-primary);
  box-shadow: var(--ec-shadow-media);
  transition: background var(--ec-transition-fast) var(--ec-ease);
}

@media (hover: hover) and (pointer: fine) {
  .voice-player:hover {
    background: var(--ec-media-surface-hover);
  }
}

/* 播放按钮：flex 居中图标（原 grid 未设 place-items，图标贴在按钮左上角，视觉上错位）。 */
.voice-player__main {
  display: flex;
  flex: none;
  align-items: center;
  justify-content: center;
  width: 28px;
  height: 28px;
  min-width: 28px;
  min-height: 28px;
  padding: 0;
  border: none;
  border-radius: 50%;
  background: var(--ec-brand-600);
  color: var(--ec-text-inverse);
  line-height: 0;
  cursor: pointer;
}

.voice-player__main:disabled {
  cursor: progress;
}

.voice-player__main:focus-visible {
  outline: var(--ec-focus-width) solid var(--ec-focus-ring);
  outline-offset: var(--ec-focus-offset);
}

/* 命中区：视觉 3px、上下各 8px padding 撑到 19px，方便触屏拖动 */
.voice-player__track {
  display: flex;
  flex: 1;
  align-items: center;
  min-width: 48px;
  height: 20px;
  padding: 8px 0;
  cursor: pointer;
  touch-action: none;
  box-sizing: content-box;
}

.voice-player__track:focus-visible {
  outline: var(--ec-focus-width) solid var(--ec-focus-ring);
  outline-offset: var(--ec-focus-offset);
  border-radius: var(--ec-radius-sm);
}

.voice-player__bar {
  position: relative;
  width: 100%;
  height: 3px;
  overflow: hidden;
  border-radius: var(--ec-radius-full);
  background: var(--ec-media-track);
}

.voice-player__fill {
  position: absolute;
  inset: 0 auto 0 0;
  background: var(--ec-brand-600);
  transition: width 120ms linear;
}

.voice-player__time {
  flex: none;
  font-size: var(--ec-font-size-small);
  color: var(--ec-text-secondary);
  font-variant-numeric: tabular-nums;
}

/* C2b：倍速按钮，与时间并排；保持 1× 起，点击循环到 1.5×/2×。 */
.voice-player__rate {
  flex: none;
  padding: 2px 6px;
  border: 1px solid var(--ec-media-border);
  border-radius: var(--ec-radius-sm);
  background: transparent;
  color: var(--ec-text-secondary);
  font-size: var(--ec-font-size-small);
  font-variant-numeric: tabular-nums;
  line-height: 1.2;
  cursor: pointer;
}

.voice-player__rate:hover {
  color: var(--ec-text-primary);
}

.voice-player__rate:focus-visible {
  outline: var(--ec-focus-width) solid var(--ec-focus-ring);
  outline-offset: var(--ec-focus-offset);
}

.voice-player--error {
  background: var(--ec-media-surface);
  color: var(--ec-danger);
}

.voice-player--error .voice-player__main {
  background: var(--ec-danger);
}

/* 自己发送的语音在蓝底气泡内：改为半透明白底 + 白色进度，保证对比度；
   媒体消息不套气泡（.message-bubble--plain）时沿用默认浅色胶囊。 */
:global(.message-bubble--self:not(.message-bubble--plain)) .voice-player {
  border-color: transparent;
  background: rgb(255 255 255 / 22%);
  box-shadow: none;
  color: var(--ec-text-inverse);
}

:global(.message-bubble--self:not(.message-bubble--plain)) .voice-player__bar {
  background: rgb(255 255 255 / 35%);
}

:global(.message-bubble--self:not(.message-bubble--plain)) .voice-player__fill {
  background: var(--ec-text-inverse);
}

:global(.message-bubble--self:not(.message-bubble--plain)) .voice-player__time {
  color: var(--ec-text-inverse);
}

:global(.message-bubble--self:not(.message-bubble--plain)) .voice-player__rate {
  border-color: rgb(255 255 255 / 45%);
  color: var(--ec-text-inverse);
}

:global(.message-bubble--self:not(.message-bubble--plain)) .voice-player__main {
  background: var(--ec-text-inverse);
  color: var(--ec-brand-600);
}
</style>
