<script setup lang="ts">
import AppIcon from './AppIcon.vue'
import { EMOJI_GROUPS } from '../core/emoji'

// UI-02：聊天区内的 Emoji 面板（同层内嵌，非遮罩抽屉）。
// 面板占据消息区与输入框之间的正常文档流空间，打开时消息区自动收缩、输入框位置上移；
// 只负责展示分组与派发选择，不做输入框操作、不发送消息、不发请求；开合由 Chat 页负责。
const emit = defineEmits<{ pick: [emoji: string]; close: [] }>()
</script>

<template>
  <div class="ec-emoji-panel">
    <header class="ec-emoji-panel__header">
      <span class="ec-emoji-panel__title">表情</span>
      <button
        type="button"
        class="ec-emoji-panel__close"
        aria-label="收起表情"
        @click="emit('close')"
      >
        <AppIcon name="close" :size="16" />
      </button>
    </header>

    <div class="ec-emoji-panel__body">
      <section v-for="group in EMOJI_GROUPS" :key="group.label" class="ec-emoji-panel__group">
        <h3 class="ec-emoji-panel__label">{{ group.label }}</h3>
        <div class="ec-emoji-panel__grid">
          <button
            v-for="emoji in group.emojis"
            :key="emoji"
            type="button"
            class="ec-emoji-panel__item"
            :aria-label="`插入表情 ${emoji}`"
            @click="emit('pick', emoji)"
          >
            {{ emoji }}
          </button>
        </div>
      </section>
    </div>
  </div>
</template>

<style scoped>
/* 内嵌面板：固定高度占位，消息区（flex:1）自动收缩，输入框随之上移。 */
.ec-emoji-panel {
  display: flex;
  flex: none;
  flex-direction: column;
  height: 240px;
  min-height: 0;
  border-top: 1px solid var(--ec-border);
  background: var(--ec-surface);
}

.ec-emoji-panel__header {
  display: flex;
  flex: none;
  align-items: center;
  justify-content: space-between;
  padding: var(--ec-space-2) var(--ec-space-3) 0 var(--ec-space-4);
}

.ec-emoji-panel__title {
  color: var(--ec-text-secondary);
  font-size: 12px;
  font-weight: var(--ec-font-weight-semibold);
}

.ec-emoji-panel__close {
  display: grid;
  padding: 4px;
  border: none;
  border-radius: var(--ec-radius-sm);
  background: transparent;
  color: var(--ec-text-secondary);
  cursor: pointer;
  place-items: center;
}

.ec-emoji-panel__close:hover,
.ec-emoji-panel__close:focus-visible {
  background: var(--ec-surface-hover);
}

.ec-emoji-panel__body {
  display: flex;
  flex: 1;
  flex-direction: column;
  gap: var(--ec-space-3);
  min-height: 0;
  padding: var(--ec-space-2) var(--ec-space-4) var(--ec-space-3);
  overflow-y: auto;
}

.ec-emoji-panel__group {
  display: flex;
  flex-direction: column;
  gap: var(--ec-space-2);
}

.ec-emoji-panel__label {
  margin: 0;
  color: var(--ec-text-secondary);
  font-size: 12px;
  font-weight: var(--ec-font-weight-semibold);
}

.ec-emoji-panel__grid {
  display: flex;
  flex-wrap: wrap;
  gap: var(--ec-space-1);
}

.ec-emoji-panel__item {
  display: grid;
  flex: none;
  width: 32px;
  height: 32px;
  padding: 0;
  place-items: center;
  border: none;
  border-radius: var(--ec-radius-sm);
  background: transparent;
  font-size: 18px;
  line-height: 1;
  cursor: pointer;
}

.ec-emoji-panel__item:hover,
.ec-emoji-panel__item:focus-visible {
  background: var(--ec-brand-050);
}

@media (max-width: 800px) {
  .ec-emoji-panel {
    height: 200px;
  }

  .ec-emoji-panel__item {
    width: 36px;
    height: 36px;
    font-size: 20px;
  }
}
</style>
