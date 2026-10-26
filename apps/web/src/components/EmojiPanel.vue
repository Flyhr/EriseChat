<script setup lang="ts">
import { EMOJI_GROUPS } from '../core/emoji'

// UI-02：聊天输入框上方的悬浮 Emoji 面板（不参与文档流、不挤出聊天内容）。
// 只负责展示分组与派发选择；开合、点击外部关闭由 Chat 页负责，不做输入框操作、不发请求。
const emit = defineEmits<{ pick: [emoji: string] }>()
</script>

<template>
  <div class="ec-emoji-panel" role="dialog" aria-label="表情面板">
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
/* 悬浮卡片：绝对定位于发送框上方（定位由 Chat 页提供），打开/关闭不改变消息区高度。 */
.ec-emoji-panel {
  display: flex;
  flex-direction: column;
  width: 360px;
  max-width: calc(100vw - var(--ec-space-8));
  max-height: 300px;
  overflow: hidden;
  background: var(--ec-surface);
  border: 1px solid var(--ec-border);
  border-radius: var(--ec-radius-panel);
  box-shadow: var(--ec-shadow-lg);
}

.ec-emoji-panel__body {
  display: flex;
  flex: 1;
  flex-direction: column;
  gap: var(--ec-space-3);
  min-height: 0;
  padding: var(--ec-space-3) var(--ec-space-4);
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
    width: auto;
    max-height: 260px;
  }

  .ec-emoji-panel__item {
    width: 36px;
    height: 36px;
    font-size: 20px;
  }
}
</style>
