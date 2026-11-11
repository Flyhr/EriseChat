<script setup lang="ts">
import { computed, onBeforeUnmount, ref, watch } from 'vue'
import type { FileContent, Message } from '../types/frames'
import type { PendingMessage } from '../stores/messages'
import { filesApi } from '../api/files'
import { getOrLoadBlobUrl } from '../core/blobCache'
import { fileIconOf, formatFileSize } from '../core/attachments'
import AppIcon from './AppIcon.vue'

// 图片缩略图 / 文件卡片（M3-07，纯展示 + 按需鉴权下载）。
// 关键约定：下载一律走 filesApi.downloadBlob（带 JWT），禁止裸 <img src="/api/files/xx">（会 401）；
// object URL 由 core/blobCache 统一缓存/回收，且不写入 IndexedDB（只缓存消息元数据）。
const props = defineProps<{
  message: Message | PendingMessage
}>()

const emit = defineEmits<{
  /** 点击图片缩略图：把已加载的 blob URL 交给父级打开大图预览。 */
  preview: [payload: { url: string; name: string }]
  /** 图片加载完成（父级据此在用户停留底部时重新滚到底）。 */
  loaded: []
}>()

const content = computed<FileContent | null>(() => {
  // 仅 image/file 有附件内容；voice 的 content 是 { fileId, durationMs }，由 MessageBubble 单独渲染。
  if (props.message.msgType !== 'image' && props.message.msgType !== 'file') return null
  const raw = props.message.content
  if (typeof raw !== 'object' || raw === null) return null
  if (!('name' in raw) || !('mime' in raw) || !('size' in raw)) return null
  return raw
})
const isImage = computed(() => props.message.msgType === 'image' && content.value !== null)
/** 文件消息的类型图标（按扩展名；未知类型回退通用文件图标）。 */
const fileIcon = computed(() => fileIconOf(content.value?.name ?? ''))

const blobUrl = ref<string | null>(null)
const imageState = ref<'loading' | 'ready' | 'error'>('loading')
const fileState = ref<'idle' | 'downloading' | 'error'>('idle')
let disposed = false

/** 图片缩略图：按 fileId 走鉴权下载 + blob 缓存，失败显示「图片不可用」并允许重试。 */
async function loadImage(): Promise<void> {
  const file = content.value
  if (!file) return
  imageState.value = 'loading'
  try {
    const url = await getOrLoadBlobUrl(file.fileId, () => filesApi.downloadBlob(file.fileId))
    if (disposed) return
    blobUrl.value = url
    imageState.value = 'ready'
    emit('loaded')
  } catch (error) {
    if (disposed) return
    imageState.value = 'error'
    console.warn('[attachment] 图片加载失败', error)
  }
}

watch(
  () => content.value?.fileId,
  (fileId) => {
    if (fileId && isImage.value) void loadImage()
  },
  { immediate: true },
)

onBeforeUnmount(() => {
  disposed = true
})

function openPreview(): void {
  if (!blobUrl.value || !content.value) return
  emit('preview', { url: blobUrl.value, name: content.value.name })
}

/** 文件下载：鉴权取 Blob → 临时 <a download> 触发保存（不暴露磁盘路径）。 */
async function download(): Promise<void> {
  const file = content.value
  if (!file || fileState.value === 'downloading') return
  fileState.value = 'downloading'
  try {
    const url = await getOrLoadBlobUrl(file.fileId, () => filesApi.downloadBlob(file.fileId))
    const anchor = document.createElement('a')
    anchor.href = url
    anchor.download = file.name
    anchor.rel = 'noopener'
    document.body.appendChild(anchor)
    anchor.click()
    anchor.remove()
    fileState.value = 'idle'
  } catch (error) {
    fileState.value = 'error'
    console.warn('[attachment] 文件下载失败', error)
  }
}
</script>

<template>
  <div v-if="content" class="message-attachment">
    <template v-if="isImage">
      <button
        v-if="imageState === 'ready' && blobUrl"
        class="message-attachment__thumb"
        type="button"
        :title="`查看大图：${content.name}`"
        :aria-label="`查看大图：${content.name}`"
        @click="openPreview"
      >
        <img
          :src="blobUrl"
          :alt="content.name"
          class="message-attachment__image"
          loading="lazy"
          decoding="async"
          @load="emit('loaded')"
        />
      </button>
      <div v-else-if="imageState === 'error'" class="message-attachment__fallback">
        <span>图片不可用</span>
        <button type="button" class="message-attachment__retry" @click="loadImage">重试</button>
      </div>
      <div v-else class="message-attachment__skeleton" role="img" aria-label="图片正在加载" />
    </template>

    <!-- 文件：类型图标 + 名称 + 大小，下载入口用矢量图标；不套气泡。 -->
    <div v-else class="message-attachment__file">
      <span class="message-attachment__icon" aria-hidden="true">
        <AppIcon :name="fileIcon" :size="18" />
      </span>
      <span class="message-attachment__file-info">
        <span class="message-attachment__name" :title="content.name">{{ content.name }}</span>
        <span class="message-attachment__size">
          {{ formatFileSize(content.size) }}<template v-if="fileState === 'error'"> · 下载失败，请重试</template>
        </span>
      </span>
      <button
        class="message-attachment__download"
        type="button"
        :disabled="fileState === 'downloading'"
        :title="fileState === 'downloading' ? '下载中…' : `下载 ${content.name}`"
        :aria-label="fileState === 'downloading' ? '下载中' : `下载 ${content.name}`"
        @click="download"
      >
        <AppIcon name="download" :size="18" />
      </button>
    </div>
  </div>
</template>

<style scoped>
.message-attachment {
  display: grid;
  gap: var(--ec-space-1);
  min-width: 0;
}

.message-attachment__thumb {
  padding: 0;
  border: none;
  background: none;
  cursor: zoom-in;
  line-height: 0;
}

.message-attachment__image {
  max-width: 240px;
  max-height: 240px;
  border-radius: var(--ec-radius-sm);
  object-fit: cover;
}

.message-attachment__skeleton {
  width: 160px;
  height: 120px;
  border-radius: var(--ec-radius-sm);
  background: var(--ec-bg-canvas);
}

.message-attachment__fallback {
  display: flex;
  align-items: center;
  gap: var(--ec-space-2);
  padding: var(--ec-space-3);
  border: 1px dashed var(--ec-border);
  border-radius: var(--ec-radius-sm);
  color: var(--ec-text-secondary);
  font-size: var(--ec-font-size-secondary);
}

.message-attachment__retry {
  padding: 0;
  border: none;
  background: none;
  color: var(--ec-brand-600);
  cursor: pointer;
}

.message-attachment__name {
  margin: 0;
  max-width: 240px;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  color: var(--ec-text-primary);
  font-size: var(--ec-font-size-secondary);
}

/* 文件卡片：无边框、无气泡底色，仅靠图标块与悬停反馈区分。 */
.message-attachment__file {
  display: flex;
  align-items: center;
  gap: var(--ec-space-2);
  min-width: 220px;
  max-width: min(320px, 60vw);
  padding: var(--ec-space-1) var(--ec-space-2);
  border-radius: var(--ec-radius-md);
}

.message-attachment__icon {
  display: inline-flex;
  flex: none;
  align-items: center;
  justify-content: center;
  width: 32px;
  height: 32px;
  border-radius: var(--ec-radius-sm);
  background: var(--ec-surface-hover);
  color: var(--ec-brand-600);
}

.message-attachment__file-info {
  display: grid;
  flex: 1;
  gap: 2px;
  min-width: 0;
}

.message-attachment__size {
  color: var(--ec-text-secondary);
  font-size: var(--ec-font-size-small);
}

.message-attachment__download {
  display: inline-flex;
  flex: none;
  align-items: center;
  justify-content: center;
  width: 32px;
  height: 32px;
  padding: 0;
  border: none;
  border-radius: var(--ec-radius-sm);
  background: transparent;
  color: var(--ec-text-secondary);
  cursor: pointer;
}

.message-attachment__download:disabled {
  opacity: 0.6;
  cursor: not-allowed;
}

.message-attachment__download:focus-visible {
  outline: var(--ec-focus-width) solid var(--ec-focus-ring);
  outline-offset: var(--ec-focus-offset);
}

/* 悬停效果仅在鼠标精确指针设备生效，触屏不产生粘滞 hover。 */
@media (hover: hover) and (pointer: fine) {
  .message-attachment__file:hover {
    background: var(--ec-surface-hover);
  }

  .message-attachment__download:hover:not(:disabled) {
    color: var(--ec-brand-600);
  }
}

/* 触屏下载按钮可点区域 ≥40px。 */
@media (max-width: 800px) {
  .message-attachment__download {
    width: 40px;
    height: 40px;
  }
}
</style>
