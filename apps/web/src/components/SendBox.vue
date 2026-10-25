<script setup lang="ts">
import { computed, ref } from 'vue'
import { MAX_TEXT_LENGTH } from '../stores/messages'

// 发送框（纯展示 + 本地输入）：只 emit 文本，发送/重试/状态机由 stores/messages.ts 负责。
// Enter 发送、Shift+Enter 换行；服务端文本上限 4096 字符，输入框与发送入口双重拦截（M3-00 N10）。
const emit = defineEmits<{
  send: [text: string]
}>()

const text = ref('')
const length = computed(() => text.value.length)
const atLimit = computed(() => length.value >= MAX_TEXT_LENGTH)

function submit(): void {
  const value = text.value.trim()
  if (!value || value.length > MAX_TEXT_LENGTH) return
  emit('send', value)
  text.value = ''
}
</script>

<template>
  <form class="send-box" @submit.prevent="submit">
    <div class="send-box__editor">
      <textarea
        v-model="text"
        class="send-box__input"
        rows="2"
        :maxlength="MAX_TEXT_LENGTH"
        placeholder="输入消息，Enter 发送，Shift+Enter 换行"
        @keydown.enter.exact.prevent="submit"
      />
      <span class="send-box__counter" :class="{ 'send-box__counter--limit': atLimit }">
        {{ length }}/{{ MAX_TEXT_LENGTH }}<template v-if="atLimit">（已达上限）</template>
      </span>
    </div>
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

.send-box__editor {
  flex: 1;
  display: grid;
  gap: var(--space-1);
  min-width: 0;
}

.send-box__input {
  width: 100%;
  box-sizing: border-box;
  padding: var(--space-2) var(--space-3);
  border: 1px solid var(--color-border);
  border-radius: var(--radius-sm);
  outline: none;
  resize: none;
}

.send-box__input:focus {
  border-color: var(--color-primary);
}

.send-box__counter {
  justify-self: end;
  color: var(--color-text-secondary);
  font-size: 11px;
}

.send-box__counter--limit {
  color: var(--color-danger);
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
