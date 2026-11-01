<script setup lang="ts">
import { computed, onBeforeUnmount, ref } from 'vue'
import AppIcon from './AppIcon.vue'
import { filesApi } from '../api/files'
import { getOrLoadBlobUrl } from '../core/blobCache'
import { formatDuration } from '../core/voice'
import { claimVoicePlayback, releaseVoicePlayback } from '../core/voicePlayer'

// F-28：语音消息播放器（纯展示 + 按需鉴权下载）。
// 点击播放时才下载（走 filesApi.downloadBlob + blobCache），不在历史渲染时预取；
// 同一时间只允许一条语音播放（core/voicePlayer 负责全局互斥），播放结束/出错自动复位。
const props = defineProps<{
  fileId: string
  durationMs: number
}>()

type PlayerState = 'idle' | 'loading' | 'playing' | 'error'

const state = ref<PlayerState>('idle')
const progress = ref(0)
let audio: HTMLAudioElement | null = null
let disposed = false

const label = computed(() => (state.value === 'error' ? '语音加载失败' : formatDuration(props.durationMs)))
const actionLabel = computed(() => (state.value === 'playing' ? '暂停语音' : '播放语音'))

/** 停止并复位（播放结束、被其它语音抢占或组件卸载时调用）。 */
function stopPlayback(): void {
  if (audio) {
    audio.pause()
    audio.currentTime = 0
  }
  progress.value = 0
  if (state.value === 'playing') state.value = 'idle'
  releaseVoicePlayback(stopPlayback)
}

function onEnded(): void {
  progress.value = 0
  state.value = 'idle'
  releaseVoicePlayback(stopPlayback)
}

function onTimeUpdate(): void {
  if (!audio) return
  const total = audio.duration
  progress.value = Number.isFinite(total) && total > 0 ? Math.min(1, audio.currentTime / total) : 0
}

async function togglePlayback(): Promise<void> {
  if (state.value === 'loading') return
  if (state.value === 'playing') {
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
      audio.addEventListener('ended', onEnded)
      audio.addEventListener('timeupdate', onTimeUpdate)
      audio.addEventListener('error', onError)
    } catch {
      if (!disposed) state.value = 'error'
      return
    }
  }
  claimVoicePlayback(stopPlayback)
  try {
    await audio.play()
    if (!disposed) state.value = 'playing'
  } catch {
    if (!disposed) state.value = 'error'
    releaseVoicePlayback(stopPlayback)
  }
}

function onError(): void {
  state.value = 'error'
  progress.value = 0
  releaseVoicePlayback(stopPlayback)
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
  <button
    class="voice-player"
    :class="{ 'voice-player--playing': state === 'playing', 'voice-player--error': state === 'error' }"
    type="button"
    :aria-label="actionLabel"
    :disabled="state === 'loading'"
    @click="togglePlayback"
  >
    <span class="voice-player__icon">
      <AppIcon :name="state === 'playing' ? 'pause' : 'play'" :size="15" />
    </span>
    <span class="voice-player__track" aria-hidden="true">
      <span class="voice-player__fill" :style="{ width: `${Math.round(progress * 100)}%` }" />
    </span>
    <span class="voice-player__time">{{ state === 'loading' ? '加载中…' : label }}</span>
  </button>
</template>

<style scoped>
.voice-player {
  display: flex;
  align-items: center;
  gap: var(--ec-space-2);
  min-width: 168px;
  max-width: min(260px, 60vw);
  height: 36px;
  padding: 0 var(--ec-space-2) 0 4px;
  border: none;
  border-radius: var(--ec-radius-pill);
  background: var(--ec-surface-hover);
  color: var(--ec-text-primary);
  cursor: pointer;
  transition: background var(--ec-transition-fast) var(--ec-ease);
}

.voice-player:disabled {
  cursor: progress;
}

@media (hover: hover) and (pointer: fine) {
  .voice-player:hover:not(:disabled) {
    background: var(--ec-brand-050);
  }
}

.voice-player:focus-visible {
  outline: 2px solid var(--ec-focus);
  outline-offset: 2px;
}

.voice-player__icon {
  display: grid;
  flex: none;
  width: 28px;
  height: 28px;
  place-items: center;
  border-radius: 50%;
  background: var(--ec-brand-600);
  color: var(--ec-text-inverse);
}

.voice-player__track {
  position: relative;
  flex: 1;
  min-width: 48px;
  height: 3px;
  overflow: hidden;
  border-radius: var(--ec-radius-pill);
  background: var(--ec-border);
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

.voice-player--error {
  background: var(--ec-surface-hover);
  color: var(--ec-danger);
}

.voice-player--error .voice-player__icon {
  background: var(--ec-danger);
}

/* 自己发送的语音在蓝底气泡内：改为半透明白底 + 白色进度，保证对比度。 */
:global(.message-bubble--self) .voice-player {
  background: rgb(255 255 255 / 22%);
  color: var(--ec-text-inverse);
}

:global(.message-bubble--self) .voice-player__track {
  background: rgb(255 255 255 / 35%);
}

:global(.message-bubble--self) .voice-player__fill {
  background: var(--ec-text-inverse);
}

:global(.message-bubble--self) .voice-player__time {
  color: var(--ec-text-inverse);
}

:global(.message-bubble--self) .voice-player__icon {
  background: var(--ec-text-inverse);
  color: var(--ec-brand-600);
}
</style>
