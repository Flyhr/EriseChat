<script setup lang="ts">
import { computed, ref } from 'vue'
import { MAX_TEXT_LENGTH } from '../stores/messages'
import { ALLOWED_EXTENSIONS } from '../core/attachments'

// 发送框（纯展示 + 本地输入）：文本只 emit send，附件只 emit file-selected；
// 上传、压缩、发送状态机由 pages/Chat.vue 与 stores/messages.ts 负责。
// Enter 发送、Shift+Enter 换行；服务端文本上限 4096 字符，输入框与发送入口双重拦截（M3-00 N10）。
// M3-07：附件按钮 → 选择文件 → emit file-selected；上传中禁用发送与再次选择。
const emit = defineEmits<{
  send: [text: string]
  'file-selected': [file: File]
}>()

withDefaults(
  defineProps<{
    /** 附件上传中：禁用输入、发送与附件按钮，并显示「上传中…」。 */
    uploading?: boolean
    /** 最近一次附件选择的错误文案（类型/大小/上传失败）；为空不显示。 */
    uploadError?: string | null
  }>(),
  { uploading: false, uploadError: null },
)

const text = ref('')
const fileInput = ref<HTMLInputElement | null>(null)
const length = computed(() => text.value.length)
const atLimit = computed(() => length.value >= MAX_TEXT_LENGTH)
/** 与服务端白名单一致的 accept（仅影响选择框过滤，真正校验在 attachments.ts）。 */
const accept = ALLOWED_EXTENSIONS.join(',')

function submit(): void {
  const value = text.value.trim()
  if (!value || value.length > MAX_TEXT_LENGTH) return
  emit('send', value)
  text.value = ''
}

function pickFile(): void {
  fileInput.value?.click()
}

function onFileChange(event: Event): void {
  const input = event.target as HTMLInputElement
  const file = input.files?.[0]
  input.value = ''
  if (file) emit('file-selected', file)
}
</script>

<template>
  <form class="send-box" @submit.prevent="submit">
    <button
      class="send-box__attach"
      type="button"
      title="发送图片或文件"
      :disabled="uploading"
      @click="pickFile"
    >
      📎
    </button>
    <input
      ref="fileInput"
      class="send-box__file"
      type="file"
      :accept="accept"
      :disabled="uploading"
      @change="onFileChange"
    />
    <div class="send-box__editor">
      <textarea
        v-model="text"
        class="send-box__input"
        rows="2"
        :maxlength="MAX_TEXT_LENGTH"
        :disabled="uploading"
        placeholder="输入消息，Enter 发送，Shift+Enter 换行"
        @keydown.enter.exact.prevent="submit"
      />
      <span class="send-box__counter" :class="{ 'send-box__counter--limit': atLimit }">
        {{ length }}/{{ MAX_TEXT_LENGTH }}<template v-if="atLimit">（已达上限）</template>
      </span>
      <span v-if="uploading" class="send-box__status">上传中…</span>
      <span v-else-if="uploadError" class="send-box__status send-box__status--error">
        {{ uploadError }}
      </span>
    </div>
    <button
      class="send-box__button"
      type="submit"
      :disabled="uploading || !text.trim()"
    >
      发送
    </button>
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

.send-box__attach {
  height: 36px;
  width: 36px;
  border: 1px solid var(--color-border);
  border-radius: var(--radius-sm);
  background: var(--color-surface);
  font-size: 16px;
  line-height: 1;
  cursor: pointer;
}

.send-box__attach:disabled {
  opacity: 0.5;
  cursor: not-allowed;
}

/* 隐藏原生文件选择框，仅通过附件按钮触发 */
.send-box__file {
  display: none;
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

.send-box__input:disabled {
  background: var(--color-bg);
  cursor: not-allowed;
}

.send-box__counter {
  justify-self: end;
  color: var(--color-text-secondary);
  font-size: 11px;
}

.send-box__counter--limit {
  color: var(--color-danger);
}

.send-box__status {
  color: var(--color-text-secondary);
  font-size: 12px;
}

.send-box__status--error {
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
