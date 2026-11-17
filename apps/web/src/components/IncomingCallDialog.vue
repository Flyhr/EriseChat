<script setup lang="ts">
// 来电弹窗（protocol.md §3.1）：被叫收到 call.incoming 时全屏提示；接听后关闭，交给 CallOverlay。
import { computed } from 'vue'
import { useCallStore } from '../stores/call'

const call = useCallStore()
const kindLabel = computed(() => (call.kind === 'video' ? '视频通话' : '语音通话'))
const peerInitial = computed(() => call.peerName.slice(0, 1).toUpperCase())

function accept(): void {
  void call.acceptCall()
}

function reject(): void {
  call.rejectCall()
}
</script>

<template>
  <div v-if="call.isIncoming" class="incoming-call" role="dialog" aria-modal="true" aria-label="来电">
    <div class="incoming-call__card">
      <div class="incoming-call__avatar">{{ peerInitial }}</div>
      <p class="incoming-call__name">{{ call.peerName }}</p>
      <p class="incoming-call__kind">{{ kindLabel }}来电</p>
      <div class="incoming-call__actions">
        <button type="button" class="incoming-call__button incoming-call__button--reject" @click="reject">
          拒绝
        </button>
        <button type="button" class="incoming-call__button incoming-call__button--accept" @click="accept">
          接听
        </button>
      </div>
    </div>
  </div>
</template>

<style scoped>
.incoming-call {
  position: fixed;
  inset: 0;
  z-index: 60;
  display: flex;
  align-items: center;
  justify-content: center;
  padding: var(--ec-space-6);
  background: rgba(8, 12, 24, 0.62);
  backdrop-filter: blur(6px);
}

.incoming-call__card {
  width: min(320px, 100%);
  padding: var(--ec-space-8) var(--ec-space-6);
  border-radius: var(--ec-radius-panel);
  background: var(--ec-surface);
  box-shadow: var(--ec-shadow-lg);
  text-align: center;
}

.incoming-call__avatar {
  width: 72px;
  height: 72px;
  margin: 0 auto var(--ec-space-4);
  border-radius: 50%;
  background: var(--ec-brand-600);
  color: var(--ec-text-inverse);
  font-size: 28px;
  line-height: 72px;
  font-weight: 600;
}

.incoming-call__name {
  margin: 0;
  font-size: var(--ec-font-size-page-title);
  font-weight: 600;
  color: var(--ec-text-primary);
}

.incoming-call__kind {
  margin: var(--ec-space-1) 0 var(--ec-space-6);
  font-size: var(--ec-font-size-small);
  color: var(--ec-text-secondary);
}

.incoming-call__actions {
  display: flex;
  gap: var(--ec-space-4);
}

.incoming-call__button {
  flex: 1;
  height: 44px;
  border: none;
  border-radius: var(--ec-radius-lg);
  font-size: var(--ec-font-size-body);
  font-weight: 600;
  cursor: pointer;
}

.incoming-call__button--reject {
  background: var(--ec-surface-hover);
  color: var(--ec-text-primary);
}

.incoming-call__button--accept {
  background: var(--ec-success);
  color: var(--ec-text-inverse);
}
</style>
