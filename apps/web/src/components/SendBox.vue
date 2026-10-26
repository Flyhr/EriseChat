<script setup lang="ts">
import { computed, nextTick, ref } from 'vue'
import AppIcon from './AppIcon.vue'
import { MAX_TEXT_LENGTH } from '../stores/messages'
import { ALLOWED_EXTENSIONS } from '../core/attachments'
import { insertAtCursor } from '../core/emoji'

// 发送框（纯展示 + 本地输入）：文本只 emit send，附件只 emit file-selected；
// 上传、压缩、发送状态机由 pages/Chat.vue 与 stores/messages.ts 负责。
// Enter 发送、Shift+Enter 换行；服务端文本上限 4096 字符，输入框与发送入口双重拦截（M3-00 N10）。
// M3-07：附件按钮 → 选择文件 → emit file-selected；上传中禁用发送与再次选择。
// M3-09：输入非空时 emit typing（由父组件按会话做 3 秒节流后发 typing 帧）。
// UI-02（计划 §5.5）：附件/表情图标化（AppIcon 内联 SVG）、自动增高到约 180px 后内部滚动、
// 中文输入法组合期间 Enter 不误发、emoji 由父组件抽屉选择后调用 insertEmoji 插入光标处。
const emit = defineEmits<{
  send: [text: string]
  'file-selected': [file: File]
  typing: []
  /** 点击「表情」按钮：由 Chat 页切换 Emoji 抽屉（互斥逻辑在页面层）。 */
  'pick-emoji': []
}>()

const props = withDefaults(
  defineProps<{
    /** 附件上传中：禁用输入、发送与附件按钮，并显示「上传中…」。 */
    uploading?: boolean
    /** 最近一次附件选择的错误文案（类型/大小/上传失败）；为空不显示。 */
    uploadError?: string | null
    /** Emoji 抽屉是否已打开（仅用于按钮 aria-expanded）。 */
    emojiOpen?: boolean
  }>(),
  { uploading: false, uploadError: null, emojiOpen: false },
)

/** 输入框最大高度（计划 §5.5：自动增长至约 360px，超出后内部滚动）。 */
const MAX_TEXTAREA_HEIGHT = 360

const text = ref('')
const fileInput = ref<HTMLInputElement | null>(null)
const input = ref<HTMLTextAreaElement | null>(null)
const length = computed(() => text.value.length)
const atLimit = computed(() => length.value >= MAX_TEXT_LENGTH)
/** 与服务端白名单一致的 accept（仅影响选择框过滤，真正校验在 attachments.ts）。 */
const accept = ALLOWED_EXTENSIONS.join(',')

/** 依据内容调整 textarea 高度：3 行起，超过约 360px 改为内部滚动。 */
function resizeInput(): void {
  const el = input.value
  if (!el) return
  el.style.height = 'auto'
  const nextHeight = Math.min(el.scrollHeight, MAX_TEXTAREA_HEIGHT)
  el.style.height = `${nextHeight}px`
  el.style.overflowY = el.scrollHeight > MAX_TEXTAREA_HEIGHT ? 'auto' : 'hidden'
}

/** 供 Chat 页在 Emoji 抽屉选择表情后调用：插入到当前光标处并保持焦点，不发送。 */
function insertEmoji(emoji: string): void {
  const el = input.value
  const caret = el?.selectionStart ?? text.value.length
  const result = insertAtCursor(text.value, caret, emoji)
  text.value = result.text
  void nextTick(() => {
    el?.focus()
    el?.setSelectionRange(result.caret, result.caret)
    resizeInput()
  })
}

function focusInput(): void {
  input.value?.focus()
}

defineExpose({ insertEmoji, focusInput })

function submit(): void {
  const value = text.value.trim()
  if (!value || value.length > MAX_TEXT_LENGTH) return
  emit('send', value)
  text.value = ''
  void nextTick(resizeInput)
}

function pickFile(): void {
  fileInput.value?.click()
}

function onFileChange(event: Event): void {
  const inputEl = event.target as HTMLInputElement
  const file = inputEl.files?.[0]
  inputEl.value = ''
  if (file) emit('file-selected', file)
}

/** 输入变化（M3-09）：非空且未超上限时通知父组件发送 typing（父组件负责 3 秒节流）。 */
function onInput(): void {
  resizeInput()
  if (props.uploading) return
  const value = text.value.trim()
  if (value.length === 0 || value.length > MAX_TEXT_LENGTH) return
  emit('typing')
}

/** Enter 发送（Shift+Enter 换行）：组合输入期间（IME 候选词确认）不发送。 */
function onEnter(event: KeyboardEvent): void {
  if (event.isComposing || event.keyCode === 229) return
  event.preventDefault()
  submit()
}
</script>

<template>
  <form class="send-box" @submit.prevent="submit">
    <div class="send-box__field" :class="{ 'send-box__field--error': Boolean(uploadError) }">
      <textarea
        ref="input"
        v-model="text"
        class="send-box__input"
        rows="3"
        :maxlength="MAX_TEXT_LENGTH"
        :disabled="uploading"
        placeholder="输入消息，Enter 发送，Shift+Enter 换行"
        aria-label="消息输入框"
        @input="onInput"
        @keydown.enter.exact="onEnter"
      />
      <!-- 工具栏与正文同处一个输入框：图标放在输入框下侧，分隔线用浅色避免抢视觉。 -->
      <div class="send-box__toolbar">
        <button
          class="send-box__icon"
          type="button"
          title="发送图片或文件"
          aria-label="发送图片或文件"
          :disabled="uploading"
          @click="pickFile"
        >
          <AppIcon name="paperclip" :size="18" />
        </button>
        <input
          ref="fileInput"
          class="send-box__file"
          type="file"
          :accept="accept"
          :disabled="uploading"
          @change="onFileChange"
        />
        <button
          class="send-box__icon"
          type="button"
          title="插入表情"
          aria-label="插入表情"
          aria-haspopup="dialog"
          :aria-expanded="emojiOpen"
          :disabled="uploading"
          data-emoji-trigger
          @click="emit('pick-emoji')"
        >
          <AppIcon name="emoji" :size="18" />
        </button>
        <span v-if="uploading" class="send-box__status">上传中…</span>
        <span v-else-if="uploadError" class="send-box__status send-box__status--error">
          {{ uploadError }}
        </span>
        <span class="send-box__spacer" />
        <span
          v-show="length > 0"
          class="send-box__counter"
          :class="{ 'send-box__counter--limit': atLimit }"
        >
          {{ length }}/{{ MAX_TEXT_LENGTH }}
        </span>
        <button class="send-box__send" type="submit" :disabled="uploading || !text.trim()">
          发送
        </button>
      </div>
    </div>
  </form>
</template>

<style scoped>
.send-box {
  display: block;
  flex-shrink: 0;
  /* 去掉输入条外部白底与顶部分割线：聊天区渐变背景直接透出，只保留输入框自身边框。 */
  padding: var(--ec-space-2) var(--ec-space-5) var(--ec-space-4);
  background: transparent;
}

.send-box__icon {
  display: grid;
  flex: none;
  width: 32px;
  height: 32px;
  place-items: center;
  border: none;
  border-radius: var(--ec-radius-sm);
  background: transparent;
  color: var(--ec-text-secondary);
  cursor: pointer;
  transition:
    background var(--ec-transition-fast) var(--ec-ease),
    color var(--ec-transition-fast) var(--ec-ease);
}

/* 悬停效果仅在鼠标精确指针设备生效，触屏不产生粘滞 hover。 */
@media (hover: hover) and (pointer: fine) {
.send-box__icon:hover:not(:disabled) {
  background: var(--ec-brand-050);
  color: var(--ec-brand-600);
}
}

.send-box__icon:disabled {
  opacity: 0.5;
  cursor: not-allowed;
}

/* 隐藏原生文件选择框，仅通过附件按钮触发 */
.send-box__file {
  display: none;
}

.send-box__field {
  display: flex;
  flex: 1;
  flex-direction: column;
  gap: 4px;
  min-width: 0;
  padding: var(--ec-space-2) var(--ec-space-3);
  border: 1px solid var(--ec-border);
  border-radius: var(--ec-radius-input);
  background: var(--ec-surface);
  transition: border-color var(--ec-transition-fast) var(--ec-ease);
}

.send-box__field:focus-within {
  border-color: var(--ec-brand-600);
}

.send-box__field--error {
  border-color: var(--ec-danger);
}

.send-box__input {
  width: 100%;
  /* 输入区高度为原先约 2 倍（3 行起），最大约 360px 后内部滚动。 */
  min-height: 64px;
  max-height: 360px;
  padding: 0;
  border: none;
  outline: none;
  resize: none;
  background: transparent;
  color: var(--ec-text-primary);
  font: inherit;
  line-height: var(--ec-line-height-body, 1.5);
  overflow-y: hidden;
}

.send-box__input::placeholder {
  color: var(--ec-text-tertiary);
}

.send-box__input:disabled {
  cursor: not-allowed;
}

.send-box__toolbar {
  display: flex;
  gap: 2px;
  /* 与正文同处一个输入框、无分隔线；固定最小高度避免首字输入抖动。 */
  min-height: 32px;
  align-items: center;
  padding-top: 2px;
  line-height: 16px;
}

.send-box__spacer {
  flex: 1 1 auto;
}

.send-box__counter {
  margin-left: auto;
  color: var(--ec-text-tertiary);
  font-size: var(--ec-font-size-small);
}

.send-box__counter--limit {
  color: var(--ec-danger);
}

.send-box__status {
  overflow: hidden;
  color: var(--ec-text-secondary);
  font-size: var(--ec-font-size-small);
  text-overflow: ellipsis;
  white-space: nowrap;
}

.send-box__status--error {
  color: var(--ec-danger);
}

.send-box__send {
  flex: none;
  height: 32px;
  margin-left: var(--ec-space-1);
  padding: 0 var(--ec-space-3);
  border: none;
  border-radius: var(--ec-radius-sm);
  background: var(--ec-brand-600);
  color: var(--ec-text-inverse);
  font-size: var(--ec-font-size-small);
  font-weight: var(--ec-font-weight-semibold);
  cursor: pointer;
  transition: background var(--ec-transition-fast) var(--ec-ease);
}

/* 悬停效果仅在鼠标精确指针设备生效，触屏不产生粘滞 hover。 */
@media (hover: hover) and (pointer: fine) {
.send-box__send:hover:not(:disabled) {
  background: var(--ec-brand-700);
}
}

.send-box__send:disabled {
  opacity: 0.5;
  cursor: not-allowed;
}

/* UI-05 移动端输入条：大圆角输入卡（工具按钮与输入框同卡），发送按钮改为圆角胶囊；
   底部补 safe-area，避免被系统手势条遮挡。 */
@media (max-width: 800px) {
  .send-box {
    padding: var(--ec-space-2) var(--ec-space-3)
      calc(var(--ec-space-2) + env(safe-area-inset-bottom));
  }

  .send-box__field {
    gap: 2px;
    padding: var(--ec-space-2) var(--ec-space-3) var(--ec-space-1);
    border-radius: var(--ec-radius-mobile-input);
  }

  .send-box__input {
    min-height: var(--ec-size-mobile-input-min);
    max-height: var(--ec-size-mobile-input-max);
  }

  .send-box__toolbar {
    gap: var(--ec-space-1);
    min-height: var(--ec-touch-target);
    padding-top: 0;
  }

  /* UI-04 批次4：触屏点按目标 ≥44×44（桌面保持紧凑）。 */
  .send-box__icon {
    width: var(--ec-touch-target);
    height: var(--ec-touch-target);
  }

  .send-box__send {
    height: var(--ec-size-mobile-send);
    padding: 0 var(--ec-space-4);
    border-radius: var(--ec-radius-full);
    font-size: var(--ec-font-size-secondary);
  }
}
</style>
