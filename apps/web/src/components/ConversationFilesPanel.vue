<script setup lang="ts">
// F-15 / C2c：会话共享媒体面板（右侧 aside 的「文件」页签内容）。
// 自包含加载/分页/预览/下载/定位；文件与缩略图一律走 filesApi.downloadBlob（鉴权）+ blobCache，禁止裸 URL。
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import {
  conversationsApi,
  type SharedFileItem,
  type SharedLinkItem,
} from '../api/conversations'
import { filesApi } from '../api/files'
import { formatFileSize } from '../core/attachments'
import { getOrLoadBlobUrl } from '../core/blobCache'
import { friendlyError } from '../core/errorMessages'
import {
  SHARED_FILES_PAGE_SIZE,
  formatSharedFileTime,
  groupByMonth,
  mergeSharedFiles,
  mergeSharedLinks,
} from '../core/sharedFiles'
import {
  rememberSharedPanelScroll,
  rememberSharedPanelTab,
  sharedPanelStateOf,
  type SharedPanelTab,
} from '../core/sharedPanelState'
import { chatWs } from '../core/ws'
import AppIcon from './AppIcon.vue'
import EmptyState from './EmptyState.vue'
import ImagePreview from './ImagePreview.vue'
import LoadingDots from './LoadingDots.vue'

const props = defineProps<{ conversationId: string }>()

const emit = defineEmits<{
  /** 请求在聊天区定位到指定消息（C2c：图片预览与链接/文件条目）。 */
  'locate-message': [messageId: string]
}>()

type FileTab = SharedPanelTab

const TABS: { id: FileTab; label: string }[] = [
  { id: 'image', label: '媒体' },
  { id: 'link', label: '链接' },
  { id: 'file', label: '文件' },
]

const emptyIcon = computed(() =>
  activeTab.value === 'image' ? 'image' : activeTab.value === 'link' ? 'link' : 'file',
)
const emptyTitle = computed(() =>
  activeTab.value === 'image' ? '暂无图片' : activeTab.value === 'link' ? '暂无链接' : '暂无文件',
)
const emptyDescription = computed(() =>
  activeTab.value === 'link'
    ? '会话消息中的链接会显示在这里。'
    : '会话中发送的图片和文件会显示在这里。',
)

const rootRef = ref<HTMLElement | null>(null)
const activeTab = ref<FileTab>('image')
const itemsByTab = ref<Record<'image' | 'file', SharedFileItem[]>>({ image: [], file: [] })
const hasMoreByTab = ref<Record<'image' | 'file', boolean>>({ image: false, file: false })
const loadedTabs = ref<Record<FileTab, boolean>>({ image: false, link: false, file: false })
const links = ref<SharedLinkItem[]>([])
const linksHasMore = ref(false)
const linksCursor = ref<{ seq: number | null; offset: number | null }>({ seq: null, offset: null })
const loadingTab = ref<FileTab | null>(null)
const loadingMoreTab = ref<FileTab | null>(null)
const errorMessage = ref('')
const urls = ref<Record<string, string>>({})
const failedIds = ref<Set<string>>(new Set())
const requestSeqs: Record<FileTab, number> = { image: 0, link: 0, file: 0 }
/** 会话切换或组件卸载时递增，用于丢弃上一个会话的在途响应。 */
let loadEpoch = 0
let unsubscribeRecall: (() => void) | null = null

/** 首屏加载按页签区分：一个页签在加载时不阻塞其它页签切换。 */
const loading = computed(() => loadingTab.value === activeTab.value)
const loadingMore = computed(() => loadingMoreTab.value === activeTab.value)

/** 按月分组的三个页签数据（C2c）。 */
const imageGroups = computed(() => groupByMonth(itemsByTab.value.image))
const fileGroups = computed(() => groupByMonth(itemsByTab.value.file))
const linkGroups = computed(() => groupByMonth(links.value))

/** 当前页签的条目数（链接页签走独立列表）。 */
const currentCount = computed(() =>
  activeTab.value === 'link' ? links.value.length : itemsByTab.value[activeTab.value].length,
)
const hasMore = computed(() =>
  activeTab.value === 'link' ? linksHasMore.value : hasMoreByTab.value[activeTab.value],
)
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

/** 明细面板的滚动容器（本组件挂在 .chat-details__body 内）。 */
function scrollParent(): HTMLElement | null {
  return rootRef.value?.parentElement ?? null
}

/** 页签与滚动位置存在模块作用域：明细面板关闭会卸载本组件，组件内状态会丢。 */
function rememberScroll(): void {
  const parent = scrollParent()
  if (!parent) return
  rememberSharedPanelScroll(props.conversationId, activeTab.value, parent.scrollTop)
}

function restoreScroll(): void {
  const parent = scrollParent()
  if (!parent) return
  parent.scrollTop = sharedPanelStateOf(props.conversationId).scroll[activeTab.value] ?? 0
}

function switchTab(tab: FileTab): void {
  if (activeTab.value === tab) return
  rememberScroll()
  activeTab.value = tab
  rememberSharedPanelTab(props.conversationId, tab)
  // 加载跟随当前页签：其它页签的在途请求不阻塞本次切换。
  if (!loadedTabs.value[tab] && loadingTab.value !== tab) void load(tab, true)
  if (tab === 'image') void loadThumbnails()
  void nextTick(restoreScroll)
}

async function load(tab: FileTab, reset: boolean): Promise<void> {
  const conversationId = props.conversationId
  if (!conversationId) return
  const epoch = loadEpoch
  const seq = ++requestSeqs[tab]
  if (reset) {
    loadingTab.value = tab
    errorMessage.value = ''
  } else {
    loadingMoreTab.value = tab
  }
  try {
    if (tab === 'link') {
      const cursor = reset
        ? { seq: undefined, offset: undefined }
        : {
            seq: linksCursor.value.seq ?? undefined,
            offset: linksCursor.value.offset ?? undefined,
          }
      let page = await conversationsApi.listLinks(conversationId, {
        beforeSeq: cursor.seq,
        beforeOffset: cursor.offset,
        limit: SHARED_FILES_PAGE_SIZE,
      })
      // 链接稀疏时服务端可能返回空页但仍有游标：继续取下一页（最多 3 跳），避免空页卡住翻页。
      let hops = 0
      while (
        page.items.length === 0 &&
        page.hasMore &&
        (page.nextBeforeSeq ?? null) !== null &&
        hops < 3
      ) {
        hops += 1
        page = await conversationsApi.listLinks(conversationId, {
          beforeSeq: page.nextBeforeSeq ?? undefined,
          beforeOffset: page.nextBeforeOffset ?? 0,
          limit: SHARED_FILES_PAGE_SIZE,
        })
      }
      if (epoch !== loadEpoch || seq !== requestSeqs[tab]) return
      links.value = reset ? page.items : mergeSharedLinks(links.value, page.items)
      linksHasMore.value = page.hasMore
      linksCursor.value = {
        seq: page.nextBeforeSeq ?? null,
        offset: page.nextBeforeOffset ?? null,
      }
      loadedTabs.value = { ...loadedTabs.value, link: true }
    } else {
      const beforeSeq = reset ? undefined : lastSeqOf(itemsByTab.value[tab])
      const page = await conversationsApi.listFiles(conversationId, {
        type: tab,
        beforeSeq,
        limit: SHARED_FILES_PAGE_SIZE,
      })
      if (epoch !== loadEpoch || seq !== requestSeqs[tab]) return
      itemsByTab.value = {
        ...itemsByTab.value,
        [tab]: reset ? page.items : mergeSharedFiles(itemsByTab.value[tab], page.items),
      }
      hasMoreByTab.value = { ...hasMoreByTab.value, [tab]: page.hasMore }
      loadedTabs.value = { ...loadedTabs.value, [tab]: true }
    }
    if (tab === 'image') void loadThumbnails()
  } catch (error) {
    if (epoch !== loadEpoch || seq !== requestSeqs[tab]) return
    errorMessage.value = friendlyError(error)
  } finally {
    if (seq === requestSeqs[tab]) {
      if (loadingTab.value === tab) loadingTab.value = null
      if (loadingMoreTab.value === tab) loadingMoreTab.value = null
    }
  }
  // 首屏数据落地后再还原滚动位置（重建面板/切换页签时），不为了找回旧位置额外翻页。
  if (reset && epoch === loadEpoch && activeTab.value === tab) {
    await nextTick()
    restoreScroll()
  }
}

function lastSeqOf(items: SharedFileItem[]): number | undefined {
  return items.length > 0 ? items[items.length - 1].messageSeq : undefined
}

function loadMore(): void {
  const tab = activeTab.value
  if (!loadedTabs.value[tab] || loadingTab.value === tab || loadingMoreTab.value === tab) return
  if (!hasMore.value) return
  void load(tab, false)
}

/** 图片缩略图统一走 blobCache：同一 fileId 只请求一次，失败后显示占位；切会话后旧响应不得写回。 */
async function loadThumbnails(): Promise<void> {
  const conversationId = props.conversationId
  if (!conversationId) return
  const pending = itemsByTab.value.image.filter(
    (file) => !urls.value[file.fileId] && !failedIds.value.has(file.fileId),
  )
  for (const file of pending) {
    try {
      const url = await getOrLoadBlobUrl(file.fileId, () => filesApi.downloadBlob(file.fileId))
      if (props.conversationId !== conversationId) return
      urls.value = { ...urls.value, [file.fileId]: url }
    } catch {
      if (props.conversationId !== conversationId) return
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

/** 请求聊天区定位到指定消息（图片预览、文件行、链接条目共用）。 */
function locateMessage(messageId: string): void {
  if (!messageId) return
  emit('locate-message', messageId)
}

/** 图片预览里的「定位原消息」。 */
function locateCurrentPreview(): void {
  const current = previewItems.value[previewIndex.value]
  if (current?.messageId) locateMessage(current.messageId)
}

/**
 * 已打开的共享面板随撤回事件失效：删掉对应消息的图片/文件/链接条目，预览列表自动收敛。
 * 撤回后的下载仍由文件接口鉴权拒绝（服务端校验消息未撤回），这里只处理展示。
 */
function removeRecalledMessage(messageId: string): void {
  if (!messageId) return
  if (links.value.some((link) => link.messageId === messageId)) {
    links.value = links.value.filter((link) => link.messageId !== messageId)
  }
  const images = itemsByTab.value.image
  const files = itemsByTab.value.file
  const nextImages = images.filter((item) => item.messageId !== messageId)
  const nextFiles = files.filter((item) => item.messageId !== messageId)
  if (nextImages.length !== images.length || nextFiles.length !== files.length) {
    itemsByTab.value = { image: nextImages, file: nextFiles }
  }
  if (previewOpen.value) {
    if (previewItems.value.length === 0) {
      previewOpen.value = false
    } else if (previewIndex.value >= previewItems.value.length) {
      previewIndex.value = previewItems.value.length - 1
    }
  }
}

watch(
  () => props.conversationId,
  (conversationId) => {
    loadEpoch += 1
    requestSeqs.image = 0
    requestSeqs.link = 0
    requestSeqs.file = 0
    itemsByTab.value = { image: [], file: [] }
    hasMoreByTab.value = { image: false, file: false }
    loadedTabs.value = { image: false, link: false, file: false }
    links.value = []
    linksHasMore.value = false
    linksCursor.value = { seq: null, offset: null }
    urls.value = {}
    failedIds.value = new Set()
    errorMessage.value = ''
    loadingTab.value = null
    loadingMoreTab.value = null
    if (!conversationId) return
    activeTab.value = sharedPanelStateOf(conversationId).tab
    void load(activeTab.value, true)
    if (activeTab.value === 'image') void loadThumbnails()
  },
  { immediate: true },
)

onMounted(() => {
  // 复用既有撤回事件：面板保持打开时，其它端的撤回立即反映到当前列表与预览。
  unsubscribeRecall = chatWs.on('frame', (frame) => {
    if (frame.type !== 'message.recalled') return
    if (frame.conversationId !== props.conversationId) return
    removeRecalledMessage(frame.messageId)
  })
  void nextTick(restoreScroll)
})

onBeforeUnmount(() => {
  rememberScroll()
  unsubscribeRecall?.()
  unsubscribeRecall = null
  loadEpoch += 1
})
</script>

<template>
  <div ref="rootRef" class="files-panel">
    <div class="files-panel__tabs" role="tablist" aria-label="共享内容类型">
      <button
        v-for="tab in TABS"
        :key="tab.id"
        type="button"
        role="tab"
        :aria-selected="activeTab === tab.id"
        :class="['files-panel__tab', { 'files-panel__tab--active': activeTab === tab.id }]"
        @click="switchTab(tab.id)"
      >
        {{ tab.label }}
      </button>
    </div>

    <p v-if="errorMessage" class="files-panel__error" role="alert">{{ errorMessage }}</p>

    <div v-if="loading" class="files-panel__loading" aria-label="加载中">
      <span v-for="index in 6" :key="index" class="files-panel__skeleton" />
    </div>

    <EmptyState
      v-else-if="currentCount === 0"
      :icon="emptyIcon"
      :title="emptyTitle"
      :description="emptyDescription"
    />

    <template v-else>
      <template v-if="activeTab === 'image'">
        <section v-for="group in imageGroups" :key="group.key" class="files-panel__group">
          <h3 class="files-panel__month">{{ group.label }}</h3>
          <div class="files-panel__grid" role="list">
            <button
              v-for="file in group.items"
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
        </section>
      </template>

      <template v-else-if="activeTab === 'file'">
        <section v-for="group in fileGroups" :key="group.key" class="files-panel__group">
          <h3 class="files-panel__month">{{ group.label }}</h3>
          <ul class="files-panel__list">
            <li v-for="file in group.items" :key="file.fileId" class="files-panel__row">
              <span class="files-panel__row-icon"><AppIcon name="file" :size="18" /></span>
              <button
                type="button"
                class="files-panel__row-main"
                :aria-label="`定位到 ${file.name} 所在消息`"
                @click="locateMessage(file.messageId)"
              >
                <span class="files-panel__row-name" :title="file.name">{{ file.name }}</span>
                <span class="files-panel__row-meta">
                  {{ formatFileSize(file.size) }} · {{ formatSharedFileTime(file.createdAt) }}
                </span>
              </button>
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
        </section>
      </template>

      <template v-else>
        <section v-for="group in linkGroups" :key="group.key" class="files-panel__group">
          <h3 class="files-panel__month">{{ group.label }}</h3>
          <ul class="files-panel__links">
            <li
              v-for="link in group.items"
              :key="`${link.messageId}|${link.url}`"
              class="files-panel__link-row"
            >
              <span class="files-panel__row-icon"><AppIcon name="link" :size="18" /></span>
              <button
                type="button"
                class="files-panel__row-main"
                :aria-label="`定位到链接所在消息`"
                @click="locateMessage(link.messageId)"
              >
                <span class="files-panel__row-name" :title="link.url">{{ link.url }}</span>
                <span class="files-panel__row-meta">{{ formatSharedFileTime(link.createdAt) }}</span>
              </button>
            </li>
          </ul>
        </section>
      </template>

      <button
        v-if="hasMore"
        type="button"
        class="files-panel__more"
        :disabled="loadingMore"
        @click="loadMore"
      >
        <LoadingDots v-if="loadingMore" />
        <template v-else>加载更多</template>
      </button>
    </template>

    <ImagePreview
      :open="previewOpen"
      :items="previewItems"
      :index="previewIndex"
      can-locate
      @close="closePreview"
      @locate="locateCurrentPreview"
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
  padding: 0;
  border: none;
  background: transparent;
  text-align: left;
  cursor: pointer;
}

/* 按月分组（C2c）：月份标题 + 组内列表/网格。 */
.files-panel__group {
  display: flex;
  flex-direction: column;
  gap: var(--ec-space-2);
}

.files-panel__month {
  margin: 0;
  color: var(--ec-text-tertiary);
  font-size: var(--ec-font-size-small);
  font-weight: var(--ec-font-weight-semibold);
}

.files-panel__links {
  display: flex;
  flex-direction: column;
  gap: var(--ec-space-2);
  margin: 0;
  padding: 0;
  list-style: none;
}

.files-panel__link-row {
  display: flex;
  gap: var(--ec-space-2);
  align-items: center;
  min-width: 0;
  padding: var(--ec-space-2);
  border-radius: var(--ec-radius-md);
  background: var(--ec-surface-hover);
}

.files-panel__link-row .files-panel__row-name {
  word-break: break-all;
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
