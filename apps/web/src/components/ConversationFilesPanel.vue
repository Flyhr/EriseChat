<script setup lang="ts">
// F-15：会话共享文件面板（右侧 aside 的「文件」页签内容）。
// 自包含加载/分页/预览/下载；所有文件走 filesApi.downloadBlob（鉴权）+ blobCache，禁止裸 URL。
import { computed, onBeforeUnmount, ref, watch } from 'vue'
import { conversationsApi, type SharedFileItem } from '../api/conversations'
import { filesApi } from '../api/files'
import { formatFileSize } from '../core/attachments'
import { getOrLoadBlobUrl } from '../core/blobCache'
import { friendlyError } from '../core/errorMessages'
import {
  SHARED_FILES_PAGE_SIZE,
  formatSharedFileTime,
  mergeSharedFiles,
} from '../core/sharedFiles'
import AppIcon from './AppIcon.vue'
import EmptyState from './EmptyState.vue'
import ImagePreview from './ImagePreview.vue'

const props = defineProps<{ conversationId: string }>()

type FileTab = 'image' | 'file'

const activeTab = ref<FileTab>('image')
const itemsByTab = ref<Record<FileTab, SharedFileItem[]>>({ image: [], file: [] })
const hasMoreByTab = ref<Record<FileTab, boolean>>({ image: true, file: true })
const loading = ref(false)
const loadingMore = ref(false)
const errorMessage = ref('')
const urls = ref<Record<string, string>>({})
const failedIds = ref<Set<string>>(new Set())
const requestSeq = ref(0)

/** 当前页签的列表。 */
const currentItems = computed(() => itemsByTab.value[activeTab.value])
const hasMore = computed(() => hasMoreByTab.value[activeTab.value])
/** 图片预览条目：优先使用已加载的 blob URL。 */
const previewItems = computed(() =>
  itemsByTab.value.image.map((file) => ({
    messageId: file.messageId,
    name: file.name,
    url: urls.value[file.fileId] ?? null,
  })),
)
const previewOpen = ref(false)
const previewIndex = ref(0)

function switchTab(tab: FileTab): void {
  if (activeTab.value === tab) return
  activeTab.value = tab
  if (itemsByTab.value[tab].length === 0 && !loading.value) {
    void load(tab, true)
  }
  if (tab === 'image') void loadThumbnails()
}

async function load(tab: FileTab, reset: boolean): Promise<void> {
  if (!props.conversationId) return
  const seq = ++requestSeq.value
  if (reset) {
    loading.value = true
    errorMessage.value = ''
  } else {
    loadingMore.value = true
  }
  try {
    const beforeSeq = reset ? undefined : lastSeqOf(itemsByTab.value[tab])
    const page = await conversationsApi.listFiles(props.conversationId, {
      type: tab,
      beforeSeq,
      limit: SHARED_FILES_PAGE_SIZE,
    })
    if (seq !== requestSeq.value) return
    itemsByTab.value = {
      ...itemsByTab.value,
      [tab]: reset ? page.items : mergeSharedFiles(itemsByTab.value[tab], page.items),
    }
    hasMoreByTab.value = { ...hasMoreByTab.value, [tab]: page.hasMore }
    if (tab === 'image') void loadThumbnails()
  } catch (error) {
    if (seq !== requestSeq.value) return
    errorMessage.value = friendlyError(error)
  } finally {
    if (seq === requestSeq.value) {
      loading.value = false
      loadingMore.value = false
    }
  }
}

function lastSeqOf(items: SharedFileItem[]): number | undefined {
  return items.length > 0 ? items[items.length - 1].messageSeq : undefined
}

function loadMore(): void {
  if (loading.value || loadingMore.value || !hasMore.value) return
  void load(activeTab.value, false)
}

/** 图片缩略图统一走 blobCache：同一 fileId 只请求一次，失败后显示占位。 */
async function loadThumbnails(): Promise<void> {
  const pending = itemsByTab.value.image.filter(
    (file) => !urls.value[file.fileId] && !failedIds.value.has(file.fileId),
  )
  for (const file of pending) {
    try {
      const url = await getOrLoadBlobUrl(file.fileId, () => filesApi.downloadBlob(file.fileId))
      urls.value = { ...urls.value, [file.fileId]: url }
    } catch {
      const next = new Set(failedIds.value)
      next.add(file.fileId)
      failedIds.value = next
    }
  }
}

function openPreview(file: SharedFileItem): void {
  const index = itemsByTab.value.image.findIndex((item) => item.messageId === file.messageId)
  if (index < 0) return
  previewIndex.value = index
  previewOpen.value = true
  // 打开时补齐相邻图片 URL，减少切换等待。
  void loadThumbnails()
}

function closePreview(): void {
  previewOpen.value = false
}

async function download(file: SharedFileItem): Promise<void> {
  try {
    const url = await filesApi.downloadBlob(file.fileId)
    const anchor = document.createElement('a')
    anchor.href = URL.createObjectURL(url)
    anchor.download = file.name
    anchor.rel = 'noopener'
    anchor.click()
    setTimeout(() => URL.revokeObjectURL(anchor.href), 1000)
  } catch (error) {
    errorMessage.value = friendlyError(error)
  }
}

watch(
  () => props.conversationId,
  (conversationId) => {
    if (!conversationId) return
    itemsByTab.value = { image: [], file: [] }
    hasMoreByTab.value = { image: true, file: true }
    urls.value = {}
    failedIds.value = new Set()
    errorMessage.value = ''
    activeTab.value = 'image'
    void load('image', true)
  },
  { immediate: true },
)

onBeforeUnmount(() => {
  requestSeq.value += 1
})
</script>

<template>
  <div class="files-panel">
    <div class="files-panel__tabs" role="tablist" aria-label="共享文件类型">
      <button
        type="button"
        role="tab"
        :aria-selected="activeTab === 'image'"
        :class="['files-panel__tab', { 'files-panel__tab--active': activeTab === 'image' }]"
        @click="switchTab('image')"
      >
        图片
      </button>
      <button
        type="button"
        role="tab"
        :aria-selected="activeTab === 'file'"
        :class="['files-panel__tab', { 'files-panel__tab--active': activeTab === 'file' }]"
        @click="switchTab('file')"
      >
        文件
      </button>
    </div>

    <p v-if="errorMessage" class="files-panel__error" role="alert">{{ errorMessage }}</p>

    <div v-if="loading" class="files-panel__loading" aria-label="加载中">
      <span v-for="index in 6" :key="index" class="files-panel__skeleton" />
    </div>

    <EmptyState
      v-else-if="currentItems.length === 0"
      :icon="activeTab === 'image' ? 'image' : 'file'"
      :title="activeTab === 'image' ? '暂无图片' : '暂无文件'"
      description="会话中发送的图片和文件会显示在这里。"
    />

    <template v-else>
      <div v-if="activeTab === 'image'" class="files-panel__grid" role="list">
        <button
          v-for="file in currentItems"
          :key="file.fileId"
          type="button"
          class="files-panel__thumb"
          role="listitem"
          :aria-label="`预览图片：${file.name}`"
          @click="openPreview(file)"
        >
          <img
            v-if="urls[file.fileId]"
            :src="urls[file.fileId]"
            :alt="file.name"
            loading="lazy"
            decoding="async"
          />
          <span v-else class="files-panel__thumb-fallback">
            <AppIcon name="image" :size="22" />
          </span>
        </button>
      </div>

      <ul v-else class="files-panel__list">
        <li v-for="file in currentItems" :key="file.fileId" class="files-panel__row">
          <span class="files-panel__row-icon"><AppIcon name="file" :size="18" /></span>
          <span class="files-panel__row-main">
            <span class="files-panel__row-name" :title="file.name">{{ file.name }}</span>
            <span class="files-panel__row-meta">
              {{ formatFileSize(file.size) }} · {{ formatSharedFileTime(file.createdAt) }}
            </span>
          </span>
          <button
            type="button"
            class="files-panel__download"
            :aria-label="`下载 ${file.name}`"
            title="下载"
            @click="download(file)"
          >
            <AppIcon name="download" :size="16" />
          </button>
        </li>
      </ul>

      <button
        v-if="hasMore"
        type="button"
        class="files-panel__more"
        :disabled="loadingMore"
        @click="loadMore"
      >
        {{ loadingMore ? '加载中…' : '加载更多' }}
      </button>
    </template>

    <ImagePreview
      :open="previewOpen"
      :items="previewItems"
      :index="previewIndex"
      @close="closePreview"
      @update:index="previewIndex = $event"
    />
  </div>
</template>

<style scoped>
.files-panel {
  display: flex;
  flex-direction: column;
  gap: var(--ec-space-3);
  min-height: 0;
}

.files-panel__tabs {
  display: flex;
  gap: var(--ec-space-1);
  padding: 3px;
  border-radius: var(--ec-radius-full);
  background: var(--ec-surface-hover);
}

.files-panel__tab {
  flex: 1;
  min-height: 30px;
  padding: 0 var(--ec-space-3);
  border: none;
  border-radius: var(--ec-radius-full);
  background: transparent;
  color: var(--ec-text-secondary);
  font-size: var(--ec-font-size-secondary);
  cursor: pointer;
}

.files-panel__tab--active {
  background: var(--ec-surface);
  color: var(--ec-text-primary);
  font-weight: var(--ec-font-weight-semibold);
  box-shadow: var(--ec-shadow-sm);
}

.files-panel__error {
  margin: 0;
  color: var(--ec-danger);
  font-size: var(--ec-font-size-secondary);
}

.files-panel__loading {
  display: grid;
  grid-template-columns: repeat(3, minmax(0, 1fr));
  gap: var(--ec-space-2);
}

.files-panel__skeleton {
  aspect-ratio: 1;
  border-radius: var(--ec-radius-md);
  background: var(--ec-surface-hover);
}

.files-panel__grid {
  display: grid;
  grid-template-columns: repeat(3, minmax(0, 1fr));
  gap: var(--ec-space-2);
}

.files-panel__thumb {
  position: relative;
  display: grid;
  aspect-ratio: 1;
  min-width: 0;
  padding: 0;
  overflow: hidden;
  place-items: center;
  border: 1px solid var(--ec-border-soft);
  border-radius: var(--ec-radius-md);
  background: var(--ec-surface-hover);
  cursor: pointer;
}

.files-panel__thumb img {
  display: block;
  width: 100%;
  height: 100%;
  object-fit: cover;
}

.files-panel__thumb-fallback {
  color: var(--ec-text-tertiary);
}

.files-panel__list {
  display: flex;
  flex-direction: column;
  gap: var(--ec-space-2);
  margin: 0;
  padding: 0;
  list-style: none;
}

.files-panel__row {
  display: flex;
  gap: var(--ec-space-2);
  align-items: center;
  min-width: 0;
  padding: var(--ec-space-2);
  border-radius: var(--ec-radius-md);
  background: var(--ec-surface-hover);
}

.files-panel__row-icon {
  display: grid;
  flex: none;
  width: 32px;
  height: 32px;
  place-items: center;
  border-radius: var(--ec-radius-sm);
  background: var(--ec-surface);
  color: var(--ec-brand-600);
}

.files-panel__row-main {
  display: flex;
  flex: 1;
  flex-direction: column;
  gap: 2px;
  min-width: 0;
}

.files-panel__row-name {
  overflow: hidden;
  color: var(--ec-text-primary);
  font-size: var(--ec-font-size-secondary);
  text-overflow: ellipsis;
  white-space: nowrap;
}

.files-panel__row-meta {
  color: var(--ec-text-tertiary);
  font-size: var(--ec-font-size-small);
}

.files-panel__download {
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
}

.files-panel__more {
  align-self: center;
  min-height: 32px;
  padding: 0 var(--ec-space-4);
  border: 1px solid var(--ec-border);
  border-radius: var(--ec-radius-full);
  background: var(--ec-surface);
  color: var(--ec-text-secondary);
  font-size: var(--ec-font-size-secondary);
  cursor: pointer;
}

@media (hover: hover) and (pointer: fine) {
  .files-panel__tab:hover,
  .files-panel__thumb:hover,
  .files-panel__download:hover,
  .files-panel__more:hover {
    background: var(--ec-nav-bg);
  }
}

@media (max-width: 800px) {
  .files-panel__tab,
  .files-panel__download,
  .files-panel__more {
    min-height: 44px;
  }

  .files-panel__download {
    width: 44px;
    height: 44px;
  }
}
</style>
