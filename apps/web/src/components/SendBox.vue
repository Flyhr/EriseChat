<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import AppIcon from './AppIcon.vue'
import { MAX_TEXT_LENGTH } from '../core/config'
import { ALLOWED_EXTENSIONS, formatFileSize } from '../core/attachments'
import { uploadStatusText, type UploadItem } from '../core/uploadQueue'
import { insertAtCursor } from '../core/emoji'
import { filterMentionCandidates, findMentionToken, insertMention } from '../core/mentions'
import type { MentionCandidateMember, MentionToken } from '../core/mentions'
import { filesOfClipboard, normalizeDroppedFile } from '../core/fileDrop'
import { replyCardText, type ReplyPreviewLike } from '../core/replyPreview'
import { mapRecorderError, startVoiceRecording, type VoiceRecorderHandle } from '../core/recorder'
import { formatDuration } from '../core/voice'

// 发送框（纯展示 + 本地输入）：文本只 emit send，附件只 emit file-selected；
// 上传、压缩、发送状态机由 pages/Chat.vue 与 stores/messages.ts 负责。
// Enter 发送、Shift+Enter 换行；服务端文本上限 4096 字符，输入框与发送入口双重拦截（M3-00 N10）。
// M3-07：附件按钮 → 选择文件（支持多选数组）→ emit file-selected；上传中禁用发送与再次选择。
// F-06：textarea 粘贴剪贴板文件（截图/复制的文件）时拦截默认文本粘贴，走同一上传链路。
// M3-09：输入非空时 emit typing（由父组件按会话做 3 秒节流后发 typing 帧）。
// UI-02（计划 §5.5）：附件/表情图标化（AppIcon 内联 SVG）、自动增高到约 180px 后内部滚动、
// 中文输入法组合期间 Enter 不误发、emoji 由父组件抽屉选择后调用 insertEmoji 插入光标处。
const emit = defineEmits<{
  send: [text: string]
  'file-selected': [files: File[]]
  typing: []
  /** 点击「表情」按钮：由 Chat 页切换 Emoji 抽屉（互斥逻辑在页面层）。 */
  'pick-emoji': []
  /** 输入文本变化（含清空）：由 Chat 页写入草稿存储（F-04）。 */
  'draft-change': [text: string]
  /** F-09：取消当前回复引用。 */
  'cancel-reply': []
  /** F-28：录音完成，交由父组件上传并以 voice 消息发送。 */
  'send-voice': [payload: { blob: Blob; durationMs: number }]
  /** F-28：录音失败/过短等提示，由父组件用 toast 展示。 */
  'voice-error': [message: string]
  /** F-35：附件上传队列——取消上传中项。 */
  'cancel-upload': [id: string]
  /** F-35：附件上传队列——单项重试失败项。 */
  'retry-upload': [id: string]
  /** F-35：附件上传队列——移出等待项。 */
  'dismiss-upload': [id: string]
}>()

const props = withDefaults(
  defineProps<{
    /** 附件上传中：禁用输入、发送与附件按钮，并显示「上传中…」。 */
    uploading?: boolean
    /** 最近一次附件选择的错误文案（类型/大小/上传失败）；为空不显示。 */
    uploadError?: string | null
    /** Emoji 抽屉是否已打开（仅用于按钮 aria-expanded）。 */
    emojiOpen?: boolean
    /** 当前会话 id（F-04）：变化时用 initialDraft 重置输入框内容。 */
    conversationId?: string
    /** 当前会话的初始草稿（F-04）：仅作为切换会话时的恢复值，输入后由 draft-change 回传。 */
    initialDraft?: string
    /** F-09：当前回复目标摘要（空 = 未回复），显示在输入框上方的引用条。 */
    replyTo?: ReplyPreviewLike | null
    /** F-25：群聊 @ 成员候选（空数组 = 单聊/不启用，不弹候选面板）。 */
    mentionCandidates?: MentionCandidateMember[]
    /** F-35：附件上传队列（等待/上传中/已发送/失败/已取消逐项展示）。 */
    uploadItems?: UploadItem[]
  }>(),
  {
    uploading: false,
    uploadError: null,
    emojiOpen: false,
    conversationId: '',
    initialDraft: '',
    replyTo: null,
    mentionCandidates: () => [],
    uploadItems: () => [],
  },
)

/** 输入框最大高度（计划 §5.5：自动增长至约 360px，超出后内部滚动）。 */
const MAX_TEXTAREA_HEIGHT = 360

/** 输入文本（F-04）：挂载时按所在会话的草稿初始化，切换会话时由 watch(conversationId) 重置。 */
const text = ref(props.initialDraft ?? '')
const fileInput = ref<HTMLInputElement | null>(null)
const input = ref<HTMLTextAreaElement | null>(null)
const length = computed(() => text.value.length)
const atLimit = computed(() => length.value >= MAX_TEXT_LENGTH)
/** 与服务端白名单一致的 accept（仅影响选择框过滤，真正校验在 attachments.ts）。 */
const accept = ALLOWED_EXTENSIONS.join(',')

// F-28：录音状态。点击麦克风开始，输入区切换为录音条（计时/取消/发送）；Esc 取消。
const recording = ref<VoiceRecorderHandle | null>(null)
const recordingElapsed = ref(0)
const recordingStopping = ref(false)
const recordingActive = computed(() => recording.value !== null)
/** 浏览器是否具备录音能力（无麦克风权限时点击仍会提示，由 recorder 映射错误文案）。 */
const voiceSupported = computed(
  () =>
    typeof MediaRecorder !== 'undefined' &&
    typeof navigator !== 'undefined' &&
    typeof navigator.mediaDevices?.getUserMedia === 'function',
)
let recordingTimer: number | null = null
let recordingStartedAt = 0

function clearRecordingTimer(): void {
  if (recordingTimer !== null) {
    window.clearInterval(recordingTimer)
    recordingTimer = null
  }
}

function onRecordingKeydown(event: KeyboardEvent): void {
  if (event.key !== 'Escape') return
  event.preventDefault()
  cancelRecording()
}

watch(recordingActive, (active) => {
  if (active) document.addEventListener('keydown', onRecordingKeydown, true)
  else document.removeEventListener('keydown', onRecordingKeydown, true)
})

async function startRecording(): Promise<void> {
  if (recording.value || props.uploading || recordingStopping.value) return
  if (!voiceSupported.value) {
    emit('voice-error', '当前环境不支持录音')
    return
  }
  closeMention()
  try {
    const handle = await startVoiceRecording()
    recording.value = handle
    recordingStartedAt = Date.now()
    recordingElapsed.value = 0
    clearRecordingTimer()
    recordingTimer = window.setInterval(() => {
      recordingElapsed.value = Math.min(Date.now() - recordingStartedAt, 60_000)
      if (recordingElapsed.value >= 60_000) void finishRecording(true)
    }, 200)
  } catch (error) {
    emit('voice-error', mapRecorderError(error))
  }
}

async function finishRecording(autoStop = false): Promise<void> {
  const handle = recording.value
  if (!handle || recordingStopping.value) return
  recordingStopping.value = true
  clearRecordingTimer()
  try {
    const result = await handle.stop()
    recording.value = null
    if (autoStop) emit('voice-error', '录音已达 60 秒，已自动结束')
    if (result.durationMs < 1_000 || result.blob.size === 0) {
      emit('voice-error', '录音时间太短（至少 1 秒）')
      return
    }
    emit('send-voice', { blob: result.blob, durationMs: Math.min(result.durationMs, 60_000) })
  } catch {
    recording.value = null
    emit('voice-error', '录音结束失败，请重试')
  } finally {
    recordingStopping.value = false
    recordingElapsed.value = 0
  }
}

function cancelRecording(): void {
  const handle = recording.value
  if (!handle) return
  recording.value = null
  clearRecordingTimer()
  recordingElapsed.value = 0
  handle.cancel()
}

/** 依据内容调整 textarea 高度：单行起，超过约 360px 改为内部滚动。 */
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

// F-25：@ 成员选择面板状态（纯前端文本约定：选中后把「@昵称 」写回输入框，不额外发帧）。
const field = ref<HTMLElement | null>(null)
const mentionToken = ref<MentionToken | null>(null)
const mentionActiveIndex = ref(0)
/** 候选面板是否打开（光标处于未完成的 @ 片段内且有候选成员）。 */
const mentionOpen = computed(() => mentionToken.value !== null)
const mentionQuery = computed(() => mentionToken.value?.query ?? '')
const mentionList = computed(() =>
  mentionToken.value ? filterMentionCandidates(props.mentionCandidates, mentionToken.value.query) : [],
)
/** 输入框 aria-activedescendant：把当前高亮项告诉读屏器。 */
const mentionActiveId = computed(() => {
  if (!mentionOpen.value || mentionList.value.length === 0) return undefined
  const index = Math.min(mentionActiveIndex.value, mentionList.value.length - 1)
  const member = mentionList.value[index]
  return member ? mentionOptionId(member.userId) : undefined
})

function mentionOptionId(userId: string): string {
  return `send-box-mention-${userId}`
}

/** 依据当前光标重新计算 @ 片段与候选；无候选/单聊/输入法组合时关闭面板。 */
function updateMentionState(): void {
  const el = input.value
  if (!el || props.uploading || props.mentionCandidates.length === 0 || el.disabled) {
    mentionToken.value = null
    mentionActiveIndex.value = 0
    return
  }
  const token = findMentionToken(text.value, el.selectionStart ?? text.value.length)
  if (!token) {
    mentionToken.value = null
    mentionActiveIndex.value = 0
    return
  }
  mentionToken.value = token
  const list = filterMentionCandidates(props.mentionCandidates, token.query)
  if (mentionActiveIndex.value >= list.length) mentionActiveIndex.value = 0
}

function closeMention(): void {
  mentionToken.value = null
  mentionActiveIndex.value = 0
}

function moveMention(step: number): void {
  const total = mentionList.value.length
  if (total === 0) return
  mentionActiveIndex.value = (mentionActiveIndex.value + step + total) % total
}

/** 选中候选项：替换 @ 片段为「@昵称 」，光标置其后并保持焦点，不发送消息。 */
function selectMention(member: MentionCandidateMember): void {
  const token = mentionToken.value
  if (!token) return
  const el = input.value
  const caret = el?.selectionStart ?? text.value.length
  const result = insertMention(text.value, { ...token, end: caret }, member)
  text.value = result.text
  closeMention()
  void nextTick(() => {
    el?.focus()
    el?.setSelectionRange(result.caret, result.caret)
    resizeInput()
    updateMentionState()
  })
}

function selectActiveMention(): void {
  const member = mentionList.value[mentionActiveIndex.value]
  if (member) selectMention(member)
}

/** 键盘：面板打开时 ↑/↓ 移动、Enter/Tab 选中、Esc 关闭；面板打开时 Enter 不发送消息。 */
function onKeydown(event: KeyboardEvent): void {
  if (event.isComposing || event.keyCode === 229) return

  if (mentionOpen.value) {
    if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
      event.preventDefault()
      moveMention(event.key === 'ArrowDown' ? 1 : -1)
      return
    }
    if (event.key === 'Enter' || event.key === 'Tab') {
      event.preventDefault()
      selectActiveMention()
      return
    }
    if (event.key === 'Escape') {
      event.preventDefault()
      closeMention()
      return
    }
  }

  if (event.key === 'Enter' && !event.shiftKey) {
    event.preventDefault()
    submit()
    return
  }
  if (event.key === 'Escape' && props.replyTo) {
    emit('cancel-reply')
  }
}

/** 点击输入框外关闭候选面板（点击面板内部由 mousedown.prevent 保持焦点，不关闭）。 */
function onDocumentPointerDown(event: PointerEvent): void {
  const target = event.target as Node | null
  if (target && field.value?.contains(target)) return
  closeMention()
}

watch(mentionOpen, (open) => {
  if (open) document.addEventListener('pointerdown', onDocumentPointerDown, true)
  else document.removeEventListener('pointerdown', onDocumentPointerDown, true)
})

onBeforeUnmount(() => {
  document.removeEventListener('pointerdown', onDocumentPointerDown, true)
  document.removeEventListener('keydown', onRecordingKeydown, true)
  clearRecordingTimer()
  const handle = recording.value
  recording.value = null
  handle?.cancel()
})

// 切换会话/成员变化时关闭面板，避免把上一个会话的候选带过去；录音中切换会话则取消录音。
watch(
  () => props.conversationId,
  () => {
    closeMention()
    cancelRecording()
  },
)
watch(
  () => props.mentionCandidates,
  () => {
    if (mentionOpen.value) updateMentionState()
  },
)

// 草稿（F-04）：切换会话时用父组件传入的 initialDraft 重置输入框；输入变化统一回传父组件持久化。
watch(
  () => props.conversationId,
  () => {
    text.value = props.initialDraft ?? ''
    void nextTick(resizeInput)
  },
)

watch(text, (value) => {
  emit('draft-change', value)
})

onMounted(() => {
  if (text.value) void nextTick(resizeInput)
})

function submit(): void {
  if (recording.value) return
  const value = text.value.trim()
  if (!value || value.length > MAX_TEXT_LENGTH) return
  emit('send', value)
  // 清空后由 watch(text) 回传空串，父组件据此删除草稿（发送成功即清）。
  text.value = ''
  void nextTick(resizeInput)
}

function pickFile(): void {
  fileInput.value?.click()
}

function onFileChange(event: Event): void {
  const inputEl = event.target as HTMLInputElement
  const files = Array.from(inputEl.files ?? [])
  inputEl.value = ''
  if (files.length > 0) emit('file-selected', files)
}

/**
 * 粘贴上传（F-06）：剪贴板含文件（如 Win+Shift+S 截图）时拦截文件名的默认文本粘贴，
 * 走与附件按钮相同的上传链路；纯文本粘贴保持默认行为。
 */
function onPaste(event: ClipboardEvent): void {
  const files = filesOfClipboard(event.clipboardData)
  if (files.length === 0) return
  event.preventDefault()
  emit('file-selected', files.map((file) => normalizeDroppedFile(file)))
}

/** 输入变化（M3-09）：非空且未超上限时通知父组件发送 typing（父组件负责 3 秒节流）。 */
function onInput(): void {
  resizeInput()
  updateMentionState()
  if (props.uploading) return
  const value = text.value.trim()
  if (value.length === 0 || value.length > MAX_TEXT_LENGTH) return
  emit('typing')
}
</script>

<template>
  <form class="send-box" @submit.prevent="submit">
    <div ref="field" class="send-box__field" :class="{ 'send-box__field--error': Boolean(uploadError) }">
      <!-- F-35：附件上传队列；逐项真实进度，等待可移除、上传中可取消、失败可单项重试。 -->
      <div v-if="uploadItems.length > 0" class="send-box__uploads" role="list" aria-label="附件上传队列">
        <div
          v-for="item in uploadItems"
          :key="item.id"
          class="send-box__upload"
          :class="`send-box__upload--${item.status}`"
          role="listitem"
        >
          <div class="send-box__upload-main">
            <span class="send-box__upload-name" :title="item.fileName">{{ item.fileName }}</span>
            <span class="send-box__upload-meta">
              {{ formatFileSize(item.size) }} · {{ uploadStatusText(item) }}
            </span>
          </div>
          <div
            v-if="item.status === 'uploading'"
            class="send-box__upload-track"
            role="progressbar"
            :aria-valuemin="0"
            :aria-valuemax="100"
            :aria-valuenow="item.progress"
            :aria-label="`${item.fileName} 上传进度`"
          >
            <span class="send-box__upload-bar" :style="{ width: `${item.progress}%` }" />
          </div>
          <div class="send-box__upload-actions">
            <button
              v-if="item.status === 'pending'"
              class="send-box__upload-action"
              type="button"
              @click="emit('dismiss-upload', item.id)"
            >
              移除
            </button>
            <button
              v-else-if="item.status === 'uploading'"
              class="send-box__upload-action"
              type="button"
              @click="emit('cancel-upload', item.id)"
            >
              取消
            </button>
            <template v-else-if="item.status === 'failed'">
              <button
                class="send-box__upload-action send-box__upload-action--retry"
                type="button"
                @click="emit('retry-upload', item.id)"
              >
                重试
              </button>
              <button
                class="send-box__upload-action"
                type="button"
                @click="emit('dismiss-upload', item.id)"
              >
                移除
              </button>
            </template>
            <button
              v-else-if="item.status === 'cancelled'"
              class="send-box__upload-action"
              type="button"
              @click="emit('dismiss-upload', item.id)"
            >
              移除
            </button>
          </div>
          <p v-if="item.status === 'failed' && item.error" class="send-box__upload-error">
            {{ item.error }}
          </p>
        </div>
      </div>
      <div v-if="replyTo" class="send-box__reply">
        <span class="send-box__reply-text">{{ replyCardText(replyTo) }}</span>
        <button
          class="send-box__reply-cancel"
          type="button"
          title="取消回复"
          aria-label="取消回复"
          @click="emit('cancel-reply')"
        >
          <AppIcon name="close" :size="14" />
        </button>
      </div>
      <!-- F-25：@ 成员候选面板，绝对定位在输入卡上方；↑/↓ 移动、Enter/Tab 选中、Esc 关闭。 -->
      <div
        v-if="mentionOpen"
        class="send-box__mentions"
        role="listbox"
        aria-label="选择要提及的成员"
        :aria-activedescendant="mentionActiveId"
      >
        <button
          v-for="(member, index) in mentionList"
          :id="mentionOptionId(member.userId)"
          :key="member.userId"
          type="button"
          role="option"
          class="send-box__mention"
          :class="{ 'send-box__mention--active': index === mentionActiveIndex }"
          :aria-selected="index === mentionActiveIndex"
          @mousedown.prevent
          @click="selectMention(member)"
        >
          <span class="send-box__mention-name">{{ member.displayName }}</span>
          <span class="send-box__mention-role">{{ member.role === 'owner' ? '群主' : '成员' }}</span>
        </button>
        <p v-if="mentionList.length === 0" class="send-box__mention-empty">
          {{ mentionQuery ? `没有匹配「${mentionQuery}」的成员` : '没有可提及的成员' }}
        </p>
      </div>
      <!-- F-28：录音中把输入区切换为录音条（计时/取消/发送），Esc 取消。 -->
      <div v-if="recordingActive" class="send-box__recording" role="status" aria-live="polite">
        <span class="send-box__recording-dot" aria-hidden="true" />
        <span class="send-box__recording-time">{{ formatDuration(recordingElapsed) }}</span>
        <span class="send-box__recording-hint">录音中，Esc 取消</span>
        <span class="send-box__spacer" />
        <button
          class="send-box__icon"
          type="button"
          title="取消录音"
          aria-label="取消录音"
          :disabled="recordingStopping"
          @click="cancelRecording"
        >
          <AppIcon name="close" :size="18" />
        </button>
        <button
          class="send-box__send"
          type="button"
          :disabled="recordingStopping"
          @click="finishRecording(false)"
        >
          发送
        </button>
      </div>
      <template v-else>
      <textarea
        ref="input"
        v-model="text"
        class="send-box__input"
        rows="1"
        :maxlength="MAX_TEXT_LENGTH"
        :disabled="uploading"
        placeholder="输入消息"
        aria-label="消息输入框"
        :aria-activedescendant="mentionActiveId"
        @input="onInput"
        @keydown="onKeydown"
        @keyup="updateMentionState"
        @click="updateMentionState"
        @focus="updateMentionState"
        @paste="onPaste"
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
        <button
          v-if="voiceSupported"
          class="send-box__icon"
          type="button"
          title="录制语音消息"
          aria-label="录制语音消息"
          :disabled="uploading || recordingStopping"
          @click="startRecording"
        >
          <AppIcon name="mic" :size="18" />
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
        <button
          class="send-box__send send-box__send--icon"
          type="submit"
          aria-label="发送消息"
          :disabled="uploading || !text.trim()"
        >
          <AppIcon name="send" :size="18" />
        </button>
      </div>
      </template>
    </div>
  </form>
</template>

<style scoped>
.send-box {
  display: block;
  flex-shrink: 0;
  /* 去掉输入条外部白底与顶部分割线：聊天区渐变背景直接透出，只保留输入框自身边框。 */
  /* UI-D P2-B：收紧外部留白，让输入条更贴近参考图的紧凑形态。 */
  padding: 6px var(--ec-space-4) var(--ec-space-3);
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
  position: relative;
  display: flex;
  flex: 1;
  flex-direction: column;
  gap: 4px;
  min-width: 0;
  /* UI-D P2-B：紧凑输入条（工具按钮与输入框同卡），去掉过大留白。 */
  padding: 8px 10px 6px 14px;
  border: 1px solid var(--ec-border);
  border-radius: var(--ec-radius-composer);
  background: var(--ec-surface);
  box-shadow: var(--ec-composer-shadow);
  transition:
    border-color var(--ec-transition-fast) var(--ec-ease),
    box-shadow var(--ec-transition-fast) var(--ec-ease);
}

.send-box__field:focus-within {
  border-color: var(--ec-brand-500);
  box-shadow:
    var(--ec-composer-shadow),
    0 0 0 3px rgba(37, 99, 235, 0.12);
}

.send-box__field--error {
  border-color: var(--ec-danger);
}

/* F-25：@ 成员候选面板（绝对定位在输入卡上方，不推动聊天内容）。 */
.send-box__mentions {
  position: absolute;
  right: 0;
  bottom: calc(100% + var(--ec-space-2));
  left: 0;
  z-index: 30;
  display: flex;
  flex-direction: column;
  gap: 2px;
  max-height: 240px;
  padding: var(--ec-space-1);
  overflow-y: auto;
  border: 1px solid var(--ec-border);
  border-radius: var(--ec-radius-panel);
  background: var(--ec-surface);
  box-shadow: var(--ec-shadow-lg);
}

.send-box__mention {
  display: flex;
  gap: var(--ec-space-2);
  align-items: center;
  justify-content: space-between;
  padding: var(--ec-space-2) var(--ec-space-3);
  border: none;
  border-radius: var(--ec-radius-sm);
  background: transparent;
  color: var(--ec-text-primary);
  font: inherit;
  text-align: left;
  cursor: pointer;
}

.send-box__mention--active {
  background: var(--ec-brand-050);
  color: var(--ec-brand-600);
}

@media (hover: hover) and (pointer: fine) {
  .send-box__mention:hover {
    background: var(--ec-brand-050);
    color: var(--ec-brand-600);
  }
}

.send-box__mention-name {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.send-box__mention-role {
  flex: none;
  color: var(--ec-text-tertiary);
  font-size: var(--ec-font-size-small);
}

.send-box__mention-empty {
  margin: 0;
  padding: var(--ec-space-3);
  color: var(--ec-text-tertiary);
  font-size: var(--ec-font-size-small);
  text-align: center;
}

/* F-09：回复引用条（输入框左上角），点击 × 或按 Esc 取消。 */
.send-box__reply {
  display: flex;
  gap: var(--ec-space-2);
  align-items: center;
  margin-bottom: var(--ec-space-1);
  padding: 5px 10px;
  border-left: 3px solid var(--ec-brand-500);
  border-radius: var(--ec-radius-sm);
  background: var(--ec-brand-050);
  color: var(--ec-text-secondary);
  font-size: var(--ec-font-size-small);
}

.send-box__reply-text {
  flex: 1;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.send-box__reply-cancel {
  display: grid;
  flex: none;
  width: 20px;
  height: 20px;
  place-items: center;
  padding: 0;
  border: none;
  border-radius: var(--ec-radius-full);
  background: transparent;
  color: var(--ec-text-secondary);
  cursor: pointer;
}

@media (hover: hover) and (pointer: fine) {
.send-box__reply-cancel:hover {
  background: var(--ec-surface-hover);
  color: var(--ec-text-primary);
}
}

.send-box__input {
  width: 100%;
  /* UI-D P2-B：单行起，随内容自动增高，最大约 360px 后内部滚动。 */
  min-height: 24px;
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
  height: 34px;
  margin-left: var(--ec-space-1);
  padding: 0 var(--ec-space-5);
  border: none;
  border-radius: var(--ec-radius-full);
  background: var(--ec-brand-600);
  color: var(--ec-text-inverse);
  font-size: var(--ec-font-size-small);
  font-weight: var(--ec-font-weight-semibold);
  cursor: pointer;
  transition:
    background var(--ec-transition-fast) var(--ec-ease),
    transform var(--ec-transition-fast) var(--ec-ease);
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

/* UI-D P2-B：主色方形发送按钮（参考图）；录音条的「发送」文字按钮保持原样。 */
.send-box__send--icon {
  display: grid;
  width: 34px;
  padding: 0;
  place-items: center;
  border-radius: var(--ec-radius-sm);
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

  /* F-25：移动端候选面板全宽、限制高度，触屏行高 ≥44px。 */
  .send-box__mentions {
    max-height: 200px;
  }

  .send-box__mention {
    min-height: var(--ec-touch-target);
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

  /* F-28：移动端录音条与输入区同高，方便单手触达。 */
  .send-box__recording {
    min-height: var(--ec-touch-target);
  }
}

/* F-28：录音条（替代输入区的临时状态）。 */
.send-box__recording {
  display: flex;
  align-items: center;
  gap: var(--ec-space-2);
  min-height: 64px;
  padding: 0 var(--ec-space-2);
}

.send-box__recording-dot {
  flex: none;
  width: 8px;
  height: 8px;
  border-radius: 50%;
  background: var(--ec-danger);
  animation: send-box-recording-blink 1.2s ease-in-out infinite;
}

.send-box__recording-time {
  flex: none;
  font-size: var(--ec-font-size-body);
  font-weight: 600;
  color: var(--ec-text-primary);
  font-variant-numeric: tabular-nums;
}

.send-box__recording-hint {
  min-width: 0;
  overflow: hidden;
  font-size: var(--ec-font-size-small);
  color: var(--ec-text-tertiary);
  text-overflow: ellipsis;
  white-space: nowrap;
}

@keyframes send-box-recording-blink {
  0%,
  100% {
    opacity: 1;
  }
  50% {
    opacity: 0.3;
  }
}

@media (prefers-reduced-motion: reduce) {
  .send-box__recording-dot {
    animation: none;
  }
}

/* F-35：附件上传队列（逐项状态 + 真实进度条）。 */
.send-box__uploads {
  display: flex;
  flex-direction: column;
  gap: var(--ec-space-2);
  max-height: 40vh;
  margin-bottom: var(--ec-space-2);
  overflow-y: auto;
}

.send-box__upload {
  display: flex;
  flex-direction: column;
  gap: 4px;
  padding: var(--ec-space-2) var(--ec-space-3);
  border: 1px solid var(--ec-border);
  border-radius: var(--ec-radius-input);
  background: var(--ec-surface-hover);
}

.send-box__upload--sent {
  opacity: 0.7;
}

.send-box__upload-main {
  display: flex;
  align-items: center;
  gap: var(--ec-space-2);
  min-width: 0;
}

.send-box__upload-name {
  flex: 1;
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  font-size: var(--ec-font-size-small);
  color: var(--ec-text-primary);
}

.send-box__upload-meta {
  flex: none;
  font-size: var(--ec-font-size-caption);
  color: var(--ec-text-secondary);
}

.send-box__upload-track {
  position: relative;
  height: 4px;
  border-radius: 999px;
  background: var(--ec-border);
  overflow: hidden;
}

.send-box__upload-bar {
  display: block;
  height: 100%;
  border-radius: inherit;
  background: var(--ec-brand-600);
  transition: width 120ms ease;
}

.send-box__upload-actions {
  display: flex;
  gap: var(--ec-space-1);
  justify-content: flex-end;
}

.send-box__upload-action {
  min-height: 28px;
  padding: 0 var(--ec-space-2);
  border: none;
  border-radius: var(--ec-radius-small);
  background: transparent;
  color: var(--ec-text-secondary);
  font-size: var(--ec-font-size-caption);
  cursor: pointer;
}

.send-box__upload-action:hover {
  background: var(--ec-surface);
  color: var(--ec-text-primary);
}

.send-box__upload-action--retry {
  color: var(--ec-brand-600);
  font-weight: 600;
}

.send-box__upload-error {
  margin: 0;
  font-size: var(--ec-font-size-caption);
  color: var(--ec-danger);
}
</style>
