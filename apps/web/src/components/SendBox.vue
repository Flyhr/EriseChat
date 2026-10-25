<script setup lang="ts">
import { ref } from 'vue'

// 发送框（纯展示 + 本地输入）：只 emit 文本，发送/重试/状态机由 stores/messages.ts 负责。
// Enter 发送、Shift+Enter 换行；发送中/失败态由消息气泡展示，不在输入框阻断继续输入。
const emit = defineEmits<{
  send: [text: string]
}>()

const text = ref('')

function submit(): void {
  const value = text.value.trim()
  if (!value) return
  emit('send', value)
  text.value = ''
}
</script>

<template>
  <form class="send-box" @submit.prevent="submit">
    <textarea
      v-model="text"
      class="send-box__input"
      rows="2"
      placeholder="输入消息，Enter 发送，Shift+Enter 换行"
      @keydown.enter.exact.prevent="submit"
    />
    <button class="send-box__button" type="submit" :disabled="!text.trim()">发送</button>
  </form>
</template>

<style scoped>
.send-box {
  display: flex;
  gap: var(--space-2);
  align-items: flex-end;
  padding: var(--space-3) var(--space-4);
  border-top: 1px solid var(--color-border);
}

.send-box__input {
  flex: 1;
  padding: var(--space-2) var(--space-3);
  border: 1px solid var(--color-border);
  border-radius: var(--radius-sm);
  outline: none;
  resize: none;
}

.send-box__input:focus {
  border-color: var(--color-primary);
}

.send-box__button {
  padding: var(--space-2) var(--space-4);
  border: none;
  border-radius: var(--radius-sm);
  background: var(--color-primary);
  color: var(--color-text-inverse);
}

.send-box__button:disabled {
  opacity: 0.5;
  cursor: not-allowed;
}
</style>
