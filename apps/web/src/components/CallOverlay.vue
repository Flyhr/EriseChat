<script setup lang="ts">
// 通话浮层（protocol.md §3.1）：通话中显示对端画面/音量状态、计时与挂断/静音/摄像头开关。
import { computed, onBeforeUnmount, ref, watch } from 'vue'
import { useCallStore } from '../stores/call'

const call = useCallStore()
const remoteVideo = ref<HTMLVideoElement | null>(null)
const localVideo = ref<HTMLVideoElement | null>(null)

const visible = computed(() => call.phase === 'outgoing' || call.phase === 'connecting' || call.phase === 'active')
const statusText = computed(() => {
  if (call.mediaPhase === 'preparing') return '正在准备麦克风'
  if (call.mediaPhase === 'failed') return '媒体连接失败'
  if (call.phase === 'outgoing') return '正在呼叫'
  if (call.phase === 'connecting') return '正在接通'
  return call.elapsedText
})
const isVideo = computed(() => call.kind === 'video')

function bindStreams(): void {
  if (remoteVideo.value && call.remoteStream && remoteVideo.value.srcObject !== call.remoteStream) {
    remoteVideo.value.srcObject = call.remoteStream
  }
  if (localVideo.value && call.localStream && localVideo.value.srcObject !== call.localStream) {
    localVideo.value.srcObject = call.localStream
  }
}

watch(() => [call.remoteStream, call.localStream, visible.value], bindStreams, { flush: 'post' })

onBeforeUnmount(() => {
  if (remoteVideo.value) remoteVideo.value.srcObject = null
  if (localVideo.value) localVideo.value.srcObject = null
})
</script>

<template>
  <div v-if="visible" class="call-overlay" role="dialog" aria-modal="true" aria-label="通话中">
    <div class="call-overlay__stage">
      <video
        v-if="isVideo"
        ref="remoteVideo"
        class="call-overlay__remote"
        autoplay
        playsinline
      />
      <div v-else class="call-overlay__audio">
        <div class="call-overlay__avatar">{{ call.peerName.slice(0, 1).toUpperCase() }}</div>
      </div>
      <video
        v-if="isVideo"
        ref="localVideo"
        class="call-overlay__local"
        autoplay
        playsinline
        muted
      />
      <div class="call-overlay__meta">
        <p class="call-overlay__name">{{ call.peerName }}</p>
        <p class="call-overlay__status">{{ statusText }}</p>
        <p v-if="call.errorMessage" class="call-overlay__error">{{ call.errorMessage }}</p>
      </div>
    </div>
    <div class="call-overlay__actions">
      <button type="button" class="call-overlay__button" @click="call.toggleMute()">
        {{ call.muted ? '取消静音' : '静音' }}
      </button>
      <button v-if="isVideo" type="button" class="call-overlay__button" @click="call.toggleCamera()">
        {{ call.cameraOff ? '开摄像头' : '关摄像头' }}
      </button>
      <button type="button" class="call-overlay__button call-overlay__button--hangup" @click="call.hangUp()">
        挂断
      </button>
    </div>
  </div>
</template>

<style scoped>
.call-overlay {
  position: fixed;
  inset: 0;
  z-index: 55;
  display: flex;
  flex-direction: column;
  background: rgba(6, 10, 20, 0.92);
  color: #f5f7fb;
}

.call-overlay__stage {
  position: relative;
  flex: 1;
  min-height: 0;
  display: flex;
  align-items: center;
  justify-content: center;
}

.call-overlay__remote {
  width: 100%;
  height: 100%;
  object-fit: contain;
  background: #000;
}

.call-overlay__audio {
  display: flex;
  align-items: center;
  justify-content: center;
}

.call-overlay__avatar {
  width: 108px;
  height: 108px;
  border-radius: 50%;
  background: var(--ec-brand-600);
  color: #fff;
  font-size: 40px;
  line-height: 108px;
  text-align: center;
  font-weight: 600;
}

.call-overlay__local {
  position: absolute;
  right: var(--ec-space-4);
  bottom: var(--ec-space-4);
  width: 108px;
  height: 144px;
  border-radius: var(--ec-radius-md);
  object-fit: cover;
  background: #000;
  box-shadow: var(--ec-shadow-lg);
}

.call-overlay__meta {
  position: absolute;
  top: var(--ec-safe-top, 0);
  left: 0;
  right: 0;
  padding: var(--ec-space-6) var(--ec-space-4) var(--ec-space-4);
  text-align: center;
}

.call-overlay__name {
  margin: 0;
  font-size: var(--ec-font-size-page-title);
  font-weight: 600;
}

.call-overlay__status {
  margin: var(--ec-space-1) 0 0;
  font-size: var(--ec-font-size-small);
  color: rgba(245, 247, 251, 0.72);
}

.call-overlay__error {
  margin: var(--ec-space-2) 0 0;
  font-size: var(--ec-font-size-small);
  color: #ffb4b4;
}

.call-overlay__actions {
  display: flex;
  gap: var(--ec-space-3);
  padding: var(--ec-space-5) var(--ec-space-4) calc(var(--ec-space-5) + var(--ec-safe-bottom, 0px));
  justify-content: center;
}

.call-overlay__button {
  min-width: 88px;
  height: 44px;
  padding: 0 var(--ec-space-4);
  border: none;
  border-radius: 22px;
  background: rgba(245, 247, 251, 0.16);
  color: #f5f7fb;
  font-size: var(--ec-font-size-body);
  cursor: pointer;
}

.call-overlay__button--hangup {
  background: var(--ec-danger);
}
</style>
