<script setup lang="ts">
import { computed, ref } from 'vue'
import type { Message } from '../types/frames'
import type { PendingMessage, PendingStatus } from '../stores/messages'
import { systemMessageText } from '../core/groupChat'
import { splitLinks } from '../core/links'
import Avatar from './Avatar.vue'
import ImagePreview from './ImagePreview.vue'
import MessageAttachment from './MessageAttachment.vue'

// 消息气泡（纯展示，UI-02 批次2 按 D-05 调整）：自己蓝底白字、对方白底描边；时间与状态移到气泡下方；
// 群聊他人消息左侧 32px 字母头像 + 昵称。文本只用插值渲染（自动转义），禁止 v-html（protocol.md §4.3）。
// pending 消息显示「发送中/失败重发」；system 消息居中灰字，不带气泡。
const props = withDefaults(
  defineProps<{
    message: Message | PendingMessage
    self?: boolean
    status?: PendingStatus
    /** 单聊已读回执（M3-09）：仅自己发出的最新一条展示；群聊不展示。 */
    receipt?: 'read' | 'unread'
    /** 群聊中展示发送者头像与昵称（非自己消息）。 */
    showSender?: boolean
    senderName?: string
  }>(),
  { self: false, showSender: false, senderName: '' },
)

const emit = defineEmits<{
  retry: []
  /** 图片加载完成（父级在用户停留底部时重新滚到底）。 */
  imageLoaded: []
}>()

const contentText = computed(() => {
  const content = props.message.content
  if (typeof content === 'string') return content
  if ('fileId' in content) return content.name
  return content.text ?? '[系统消息]'
})

/** 文本消息按安全链接分段（仅字符串内容；附件名与 system 消息不参与，链接以文本节点渲染，禁止 v-html）。 */
const linkSegments = computed(() =>
  typeof props.message.content === 'string' ? splitLinks(props.message.content) : [],
)

/** system 消息：居中灰字、无气泡/头像，不参与附件/时间/状态渲染。 */
const isSystem = computed(() => props.message.msgType === 'system')

const systemText = computed(() => systemMessageText(props.message.content))

/** 仅 image/file 且 content 为 FileContent 时用附件组件渲染；异常数据回退文本。 */
const showAttachment = computed(() => {
  if (props.message.msgType !== 'image' && props.message.msgType !== 'file') return false
  const content = props.message.content
  return typeof content === 'object' && content !== null && 'fileId' in content
})

const previewOpen = ref(false)
const previewUrl = ref('')
const previewName = ref('')

function onPreview(payload: { url: string; name: string }): void {
  previewUrl.value = payload.url
  previewName.value = payload.name
  previewOpen.value = true
}

function closePreview(): void {
  previewOpen.value = false
}

const timeText = computed(() =>
  new Date(props.message.createdAt).toLocaleTimeString('zh-CN', {
    hour: '2-digit',
    minute: '2-digit',
  }),
)
</script>

<template>
  <div v-if="isSystem" class="message-system">
    <span class="message-system__text">{{ systemText }}</span>
  </div>
  <div
    v-else
    class="message-row"
    :class="{ 'message-row--self': self, 'message-row--sender': showSender }"
  >
    <Avatar v-if="showSender" class="message-row__avatar" :display-name="senderName || '?'" :size="32" />
    <div class="message-row__body">
      <span v-if="showSender && senderName" class="message-row__sender">{{ senderName }}</span>
      <div
        class="message-bubble"
        :class="{
          'message-bubble--self': self,
          'message-bubble--sending': status === 'sending',
          'message-bubble--failed': status === 'failed',
        }"
      >
        <MessageAttachment
          v-if="showAttachment"
          :message="message"
          @preview="onPreview"
          @loaded="emit('imageLoaded')"
        />
        <p v-else class="message-bubble__text">
          <template v-if="linkSegments.length > 0">
            <template v-for="(segment, index) in linkSegments" :key="index">
              <a
                v-if="segment.kind === 'link'"
                class="message-bubble__link"
                :href="segment.href"
                target="_blank"
                rel="noopener noreferrer"
                >{{ segment.value }}</a
              >
              <template v-else>{{ segment.value }}</template>
            </template>
          </template>
          <template v-else>{{ contentText }}</template>
        </p>
      </div>
      <div class="message-row__meta">
        <time class="message-row__time">{{ timeText }}</time>
        <span
          v-if="receipt"
          class="message-row__receipt"
          :class="`message-row__receipt--${receipt}`"
        >
          {{ receipt === 'read' ? '已读' : '未读' }}
        </span>
        <span v-if="status === 'sending'" class="message-row__pending">发送中…</span>
        <button
          v-else-if="status === 'failed'"
          class="message-row__retry"
          type="button"
          @click="emit('retry')"
        >
          发送失败，点击重发
        </button>
      </div>
    </div>
    <ImagePreview
      :open="previewOpen"
      :url="previewUrl"
      :name="previewName"
      @close="closePreview"
    />
  </div>
</template>

<style scoped>
.message-system {
  display: flex;
  justify-content: center;
  padding: 0 var(--ec-space-2);
}

.message-system__text {
  color: var(--ec-text-secondary);
  font-size: var(--ec-font-size-secondary);
  text-align: center;
  word-break: break-word;
}

.message-row {
  display: flex;
  gap: var(--ec-space-2);
  align-items: flex-start;
}

/* 自己消息靠右：行方向反转，meta 右对齐。 */
.message-row--self {
  flex-direction: row-reverse;
}

.message-row__avatar {
  margin-top: 2px;
}

.message-row__body {
  display: flex;
  flex-direction: column;
  gap: 4px;
  max-width: min(520px, 78%);
  min-width: 0;
}

.message-row--self .message-row__body {
  align-items: flex-end;
}

.message-row__sender {
  color: var(--ec-text-secondary);
  font-size: var(--ec-font-size-small);
}

/* 「澄蓝」主题：气泡 18px 大圆角 + 单侧 6px 尾角；自己为蓝色渐变白字，对方为白底冷蓝描边。 */
.message-bubble {
  max-width: 100%;
  padding: 10px 14px;
  border: 1px solid var(--ec-bubble-other-border);
  border-radius: var(--ec-radius-bubble) var(--ec-radius-bubble) var(--ec-radius-bubble)
    var(--ec-radius-bubble-tail);
  background: var(--ec-bubble-other);
  color: var(--ec-text-primary);
  box-shadow: var(--ec-shadow-bubble);
}

.message-bubble--self {
  border-color: var(--ec-bubble-self-border);
  border-radius: var(--ec-radius-bubble) var(--ec-radius-bubble) var(--ec-radius-bubble-tail)
    var(--ec-radius-bubble);
  background: var(--ec-bubble-self-bg);
  color: var(--ec-text-inverse);
  box-shadow: var(--ec-shadow-bubble-self);
}

.message-bubble--sending {
  opacity: 0.75;
}

.message-bubble--failed {
  border-color: var(--ec-danger);
}

.message-bubble__text {
  margin: 0;
  white-space: pre-wrap;
  overflow-wrap: anywhere;
  word-break: break-word;
}

/* 链接：对方气泡用品牌色，自己蓝底白字气泡继承文字色；下划线补偿 offset 保证可辨识。 */
.message-bubble__link {
  color: var(--ec-brand-600);
  text-decoration: underline;
  text-underline-offset: 2px;
}

.message-bubble--self .message-bubble__link {
  color: inherit;
}

.message-row__meta {
  display: flex;
  gap: var(--ec-space-2);
  align-items: center;
  justify-content: flex-end;
  color: var(--ec-text-secondary);
  font-size: var(--ec-font-size-small);
}

.message-row__receipt--read {
  color: var(--ec-brand-600);
}

.message-row__retry {
  padding: 0;
  border: none;
  background: none;
  color: var(--ec-danger);
  font-size: var(--ec-font-size-small);
  cursor: pointer;
}

/* ≤800：窄屏下气泡最大宽放宽，圆角同步收一档。 */
@media (max-width: 800px) {
  .message-row__body {
    max-width: 84%;
  }

  .message-bubble {
    border-radius: 16px 16px 16px 5px;
  }

  .message-bubble--self {
    border-radius: 16px 16px 5px 16px;
  }
}
</style>
