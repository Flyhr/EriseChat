<script setup lang="ts">
import { EMOJI_GROUPS } from '../core/emoji'

// UI-02：右侧 Emoji 抽屉内容（计划 §5.6 emoji 面板 / §16.3 场景 2）。
// 只负责展示分组与派发选择，不做输入框操作、不发送消息、不发请求；抽屉开关由 Chat 页负责。
const emit = defineEmits<{ pick: [emoji: string] }>()
</script>

<template>
  <div class="ec-emoji-panel">
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
</template>

<style scoped>
.ec-emoji-panel {
  display: flex;
  flex-direction: column;
  gap: var(--ec-space-3);
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
  display: grid;
  grid-template-columns: repeat(8, minmax(0, 1fr));
  gap: 2px;
}

.ec-emoji-panel__item {
  display: grid;
  width: 100%;
  aspect-ratio: 1;
  padding: 0;
  place-items: center;
  border: none;
  border-radius: var(--ec-radius-sm);
  background: transparent;
  font-size: 20px;
  line-height: 1;
  cursor: pointer;
}

.ec-emoji-panel__item:hover,
.ec-emoji-panel__item:focus-visible {
  background: var(--ec-brand-050);
}

@media (max-width: 800px) {
  .ec-emoji-panel__grid {
    grid-template-columns: repeat(7, minmax(0, 1fr));
  }

  .ec-emoji-panel__item {
    font-size: 22px;
  }
}
</style>
