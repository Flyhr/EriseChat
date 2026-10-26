<script setup lang="ts">
import { computed, onBeforeUnmount, ref, watch } from 'vue'
import type { FileContent, Message } from '../types/frames'
import type { PendingMessage } from '../stores/messages'
import { filesApi } from '../api/files'
import { getOrLoadBlobUrl } from '../core/blobCache'
import { formatFileSize } from '../core/attachments'

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
  const raw = props.message.content
  if (typeof raw === 'string') return null
  if ('fileId' in raw) return raw
  return null
})
const isImage = computed(() => props.message.msgType === 'image' && content.value !== null)

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
      <div v-else class="message-attachment__skeleton" aria-label="图片加载中" />
      <p class="message-attachment__name">{{ content.name }}</p>
    </template>

    <div v-else class="message-attachment__file">
      <span class="message-attachment__icon" aria-hidden="true">📄</span>
      <span class="message-attachment__file-info">
        <span class="message-attachment__name">{{ content.name }}</span>
        <span class="message-attachment__size">{{ formatFileSize(content.size) }}</span>
        <span v-if="fileState === 'error'" class="message-attachment__error">
          下载失败，请重试
        </span>
      </span>
      <button
        class="message-attachment__download"
        type="button"
        :disabled="fileState === 'downloading'"
        @click="download"
      >
        {{ fileState === 'downloading' ? '下载中…' : '下载' }}
      </button>
    </div>
  </div>
</template>

<style scoped>
.message-attachment {
  display: grid;
  gap: var(--space-1);
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
  border-radius: var(--radius-sm);
  object-fit: cover;
}

.message-attachment__skeleton {
  width: 160px;
  height: 120px;
  border-radius: var(--radius-sm);
  background: var(--color-bg);
}

.message-attachment__fallback {
  display: flex;
  align-items: center;
  gap: var(--space-2);
  padding: var(--space-3);
  border: 1px dashed var(--color-border);
  border-radius: var(--radius-sm);
  color: var(--color-text-secondary);
  font-size: 12px;
}

.message-attachment__retry {
  padding: 0;
  border: none;
  background: none;
  color: var(--color-primary);
  cursor: pointer;
}

.message-attachment__name {
  margin: 0;
  max-width: 240px;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  font-size: 12px;
}

.message-attachment__file {
  display: flex;
  align-items: center;
  gap: var(--space-2);
  min-width: 200px;
  padding: var(--space-2);
  border: 1px solid var(--color-border);
  border-radius: var(--radius-sm);
  background: var(--color-surface);
}

.message-attachment__icon {
  font-size: 20px;
}

.message-attachment__file-info {
  display: grid;
  flex: 1;
  min-width: 0;
}

.message-attachment__size {
  color: var(--color-text-secondary);
  font-size: 11px;
}

.message-attachment__error {
  color: var(--color-danger);
  font-size: 11px;
}

.message-attachment__download {
  padding: var(--space-1) var(--space-2);
  border: 1px solid var(--color-border);
  border-radius: var(--radius-sm);
  background: var(--color-surface);
  color: var(--color-primary);
  cursor: pointer;
}

.message-attachment__download:disabled {
  opacity: 0.6;
  cursor: not-allowed;
}
</style>
