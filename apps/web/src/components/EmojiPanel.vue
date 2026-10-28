<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import AppDialog from './AppDialog.vue'
import AppIcon from './AppIcon.vue'
import { EMOJI_GROUPS } from '../core/emoji'
import { readRecentEmojis, recordRecentEmoji } from '../core/emojiRecent'
import { checkCustomEmojiFile, customEmojiFileName } from '../core/customEmoji'
import { prepareCustomEmojiFile } from '../core/customEmojiImage'
import { clearCustomEmojis, deleteCustomEmoji, listCustomEmojis, putCustomEmoji } from '../core/db'
import type { CustomEmojiRecord } from '../core/db'
import { friendlyError } from '../core/errorMessages'
import { showToast } from '../core/toast'

// F-21：聊天输入框上方的悬浮面板（不参与文档流）。
// 内置 Emoji 只派发 pick；自定义表情上传/删除/清空在本组件内完成，点击时把 File 交给 Chat 页发送。
const emit = defineEmits<{ pick: [emoji: string]; 'pick-custom': [file: File] }>()

type TabKey = 'emoji' | 'custom'
const tab = ref<TabKey>('emoji')

// ===== 最近使用（本设备 localStorage，仅内置 Unicode 表情） =====
const recentEmojis = ref<string[]>([])
const recentVisible = computed(() => recentEmojis.value.slice(0, 12))

function loadRecent(): void {
  recentEmojis.value = readRecentEmojis()
}

function pickBuiltin(emoji: string): void {
  recordRecentEmoji(emoji)
  loadRecent()
  emit('pick', emoji)
}

// ===== 自定义表情（本设备 IndexedDB；objectURL 仅用于展示，卸载时释放） =====
interface CustomEmojiItem {
  id: string
  url: string
  blob: Blob
  mime: string
  createdAt: number
}

const customEmojis = ref<CustomEmojiItem[]>([])
const customLoading = ref(false)
const customLoaded = ref(false)
const customError = ref('')
const uploading = ref(false)
const fileInput = ref<HTMLInputElement | null>(null)

const pendingDelete = ref<CustomEmojiItem | null>(null)
const clearAllOpen = ref(false)
const removeOpen = computed(() => pendingDelete.value !== null)
/** 触屏长按已打开删除确认时，抑制随后的 click 发送。 */
let suppressPick = false
let longPressTimer: number | null = null

function revokeUrls(): void {
  for (const item of customEmojis.value) URL.revokeObjectURL(item.url)
  customEmojis.value = []
}

async function loadCustomEmojis(): Promise<void> {
  customLoading.value = true
  customError.value = ''
  try {
    const records = await listCustomEmojis()
    revokeUrls()
    customEmojis.value = records.map((record: CustomEmojiRecord) => ({
      id: record.id,
      blob: record.blob,
      mime: record.mime,
      createdAt: record.createdAt,
      url: URL.createObjectURL(record.blob),
    }))
  } catch (error) {
    customError.value = friendlyError(error)
  } finally {
    customLoading.value = false
    customLoaded.value = true
  }
}

function selectTab(next: TabKey): void {
  tab.value = next
  if (next === 'custom' && !customLoaded.value) void loadCustomEmojis()
  if (next === 'emoji') loadRecent()
}

function pickCustom(item: CustomEmojiItem): void {
  if (suppressPick) {
    suppressPick = false
    return
  }
  emit('pick-custom', new File([item.blob], customEmojiFileName(item.mime), { type: item.mime }))
}

function triggerUpload(): void {
  if (uploading.value) return
  fileInput.value?.click()
}

async function onFileChange(event: Event): Promise<void> {
  const input = event.target as HTMLInputElement
  const file = input.files?.[0]
  input.value = ''
  if (!file) return

  const check = checkCustomEmojiFile(file)
  if (!check.ok) {
    showToast(check.reason, 'error')
    return
  }

  uploading.value = true
  try {
    const prepared = await prepareCustomEmojiFile(file)
    const record: CustomEmojiRecord = {
      id: newId(),
      blob: prepared.blob,
      mime: prepared.mime,
      createdAt: Date.now(),
    }
    await putCustomEmoji(record)
    await loadCustomEmojis()
    showToast('已添加到自定义表情', 'success')
  } catch (error) {
    showToast(friendlyError(error), 'error')
  } finally {
    uploading.value = false
  }
}

function newId(): string {
  if (typeof crypto !== 'undefined' && typeof crypto.randomUUID === 'function') return crypto.randomUUID()
  return `emoji-${Date.now()}-${Math.random().toString(36).slice(2, 10)}`
}

// 删除：桌面悬停出现删除按钮；触屏长按 500ms 打开确认。
function askDelete(item: CustomEmojiItem): void {
  pendingDelete.value = item
}

function startLongPress(item: CustomEmojiItem): void {
  clearLongPress()
  longPressTimer = window.setTimeout(() => {
    longPressTimer = null
    suppressPick = true
    askDelete(item)
  }, 500)
}

function clearLongPress(): void {
  if (longPressTimer !== null) {
    window.clearTimeout(longPressTimer)
    longPressTimer = null
  }
}

async function confirmDelete(): Promise<void> {
  const item = pendingDelete.value
  pendingDelete.value = null
  suppressPick = false
  if (!item) return
  try {
    await deleteCustomEmoji(item.id)
    await loadCustomEmojis()
    showToast('已删除自定义表情', 'success')
  } catch (error) {
    showToast(friendlyError(error), 'error')
  }
}

async function confirmClearAll(): Promise<void> {
  clearAllOpen.value = false
  try {
    await clearCustomEmojis()
    await loadCustomEmojis()
    showToast('已清空自定义表情', 'success')
  } catch (error) {
    showToast(friendlyError(error), 'error')
  }
}

onMounted(() => {
  loadRecent()
  void loadCustomEmojis()
})

onBeforeUnmount(() => {
  clearLongPress()
  revokeUrls()
})
</script>

<template>
  <div class="ec-emoji-panel" role="dialog" aria-label="表情面板">
    <div class="ec-emoji-panel__tabs" role="tablist" aria-label="表情分类">
      <button
        id="ec-emoji-tab-emoji"
        type="button"
        class="ec-emoji-panel__tab"
        :class="{ 'ec-emoji-panel__tab--active': tab === 'emoji' }"
        role="tab"
        :aria-selected="tab === 'emoji'"
        aria-controls="ec-emoji-panel-emoji"
        @click="selectTab('emoji')"
      >
        表情
      </button>
      <button
        id="ec-emoji-tab-custom"
        type="button"
        class="ec-emoji-panel__tab"
        :class="{ 'ec-emoji-panel__tab--active': tab === 'custom' }"
        role="tab"
        :aria-selected="tab === 'custom'"
        aria-controls="ec-emoji-panel-custom"
        @click="selectTab('custom')"
      >
        自定义
      </button>
    </div>

    <div v-show="tab === 'emoji'" id="ec-emoji-panel-emoji" class="ec-emoji-panel__body" role="tabpanel" aria-labelledby="ec-emoji-tab-emoji">
      <section v-if="recentVisible.length > 0" class="ec-emoji-panel__group">
        <h3 class="ec-emoji-panel__label">最近使用</h3>
        <div class="ec-emoji-panel__grid">
          <button
            v-for="emoji in recentVisible"
            :key="`recent-${emoji}`"
            type="button"
            class="ec-emoji-panel__item"
            :aria-label="`插入最近使用的表情 ${emoji}`"
            @click="pickBuiltin(emoji)"
          >
            {{ emoji }}
          </button>
        </div>
      </section>

      <section v-for="group in EMOJI_GROUPS" :key="group.label" class="ec-emoji-panel__group">
        <h3 class="ec-emoji-panel__label">{{ group.label }}</h3>
        <div class="ec-emoji-panel__grid">
          <button
            v-for="emoji in group.emojis"
            :key="emoji"
            type="button"
            class="ec-emoji-panel__item"
            :aria-label="`插入表情 ${emoji}`"
            @click="pickBuiltin(emoji)"
          >
            {{ emoji }}
          </button>
        </div>
      </section>
    </div>

    <div v-show="tab === 'custom'" id="ec-emoji-panel-custom" class="ec-emoji-panel__body" role="tabpanel" aria-labelledby="ec-emoji-tab-custom">
      <div v-if="customLoading" class="ec-emoji-panel__hint">加载中…</div>
      <div v-else-if="customError" class="ec-emoji-panel__hint">
        {{ customError }}
        <button type="button" class="ec-emoji-panel__link" @click="loadCustomEmojis">重试</button>
      </div>

      <template v-else>
        <p v-if="customEmojis.length === 0" class="ec-emoji-panel__hint">还没有自定义表情，上传一张图片试试</p>
        <div v-else class="ec-emoji-panel__grid ec-emoji-panel__grid--custom">
          <div
            v-for="item in customEmojis"
            :key="item.id"
            class="ec-emoji-panel__custom-item"
            @pointerdown="startLongPress(item)"
            @pointerup="clearLongPress"
            @pointerleave="clearLongPress"
            @pointercancel="clearLongPress"
          >
            <button
              type="button"
              class="ec-emoji-panel__item ec-emoji-panel__item--custom"
              :aria-label="`发送自定义表情 ${item.id.slice(0, 8)}`"
              @click="pickCustom(item)"
            >
              <img :src="item.url" alt="" class="ec-emoji-panel__thumb" draggable="false" />
            </button>
            <button
              type="button"
              class="ec-emoji-panel__delete"
              aria-label="删除该自定义表情"
              @click.stop="askDelete(item)"
            >
              <AppIcon name="close" :size="12" />
            </button>
          </div>
        </div>

        <div class="ec-emoji-panel__actions">
          <button type="button" class="ec-emoji-panel__upload" :disabled="uploading" @click="triggerUpload">
            <AppIcon name="plus" :size="14" />
            {{ uploading ? '上传中…' : '上传' }}
          </button>
          <button
            v-if="customEmojis.length > 0"
            type="button"
            class="ec-emoji-panel__link ec-emoji-panel__link--danger"
            @click="clearAllOpen = true"
          >
            清空全部
          </button>
        </div>
      </template>

      <input
        ref="fileInput"
        type="file"
        class="ec-emoji-panel__file"
        accept="image/png,image/jpeg,image/webp,image/gif"
        @change="onFileChange"
      />
    </div>

    <AppDialog
      :open="removeOpen"
      title="删除自定义表情"
      message="删除后本设备不再显示该表情，已发送的消息不受影响。"
      confirm-text="删除"
      tone="danger"
      @confirm="confirmDelete"
      @cancel="pendingDelete = null"
    />
    <AppDialog
      :open="clearAllOpen"
      title="清空自定义表情"
      message="将删除本设备全部自定义表情，已发送的消息不受影响。"
      confirm-text="清空"
      tone="danger"
      @confirm="confirmClearAll"
      @cancel="clearAllOpen = false"
    />
  </div>
</template>

<style scoped>
/* 悬浮卡片：绝对定位于发送框上方（定位由 Chat 页提供），打开/关闭不改变消息区高度。 */
.ec-emoji-panel {
  display: flex;
  flex-direction: column;
  width: 360px;
  max-width: calc(100vw - var(--ec-space-8));
  max-height: 300px;
  overflow: hidden;
  background: var(--ec-surface);
  border: 1px solid var(--ec-border);
  border-radius: var(--ec-radius-panel);
  box-shadow: var(--ec-shadow-lg);
}

.ec-emoji-panel__tabs {
  display: flex;
  flex: none;
  gap: var(--ec-space-1);
  padding: var(--ec-space-2) var(--ec-space-3) 0;
  border-bottom: 1px solid var(--ec-border);
}

.ec-emoji-panel__tab {
  padding: var(--ec-space-2) var(--ec-space-3);
  border: none;
  border-bottom: 2px solid transparent;
  background: transparent;
  color: var(--ec-text-secondary);
  font-size: 13px;
  font-weight: var(--ec-font-weight-medium);
  cursor: pointer;
}

.ec-emoji-panel__tab--active {
  border-bottom-color: var(--ec-brand-600);
  color: var(--ec-brand-600);
  font-weight: var(--ec-font-weight-semibold);
}

.ec-emoji-panel__body {
  display: flex;
  flex: 1;
  flex-direction: column;
  gap: var(--ec-space-3);
  min-height: 0;
  padding: var(--ec-space-3) var(--ec-space-4);
  overflow-y: auto;
}

.ec-emoji-panel__group {
  display: flex;
  flex-direction: column;
  gap: var(--ec-space-2);
}

.ec-emoji-panel__label {
  margin: 0;
  color: var(--ec-text-secondary);
  font-size: 12px;
  font-weight: var(--ec-font-weight-semibold);
}

.ec-emoji-panel__grid {
  display: flex;
  flex-wrap: wrap;
  gap: var(--ec-space-1);
}

.ec-emoji-panel__item {
  display: grid;
  flex: none;
  width: 32px;
  height: 32px;
  padding: 0;
  place-items: center;
  border: none;
  border-radius: var(--ec-radius-sm);
  background: transparent;
  font-size: 18px;
  line-height: 1;
  cursor: pointer;
}

.ec-emoji-panel__item--custom {
  padding: 2px;
  overflow: hidden;
}

.ec-emoji-panel__thumb {
  width: 100%;
  height: 100%;
  object-fit: contain;
  pointer-events: none;
}

.ec-emoji-panel__custom-item {
  position: relative;
  display: flex;
}

.ec-emoji-panel__delete {
  position: absolute;
  top: -4px;
  right: -4px;
  display: grid;
  width: 16px;
  height: 16px;
  padding: 0;
  place-items: center;
  border: 1px solid var(--ec-border);
  border-radius: var(--ec-radius-full);
  background: var(--ec-surface);
  color: var(--ec-text-secondary);
  opacity: 0;
  cursor: pointer;
}

.ec-emoji-panel__custom-item:hover .ec-emoji-panel__delete,
.ec-emoji-panel__custom-item:focus-within .ec-emoji-panel__delete {
  opacity: 1;
}

/* 触屏没有 hover：删除按钮常显，长按同样可删除。 */
@media (hover: none) {
  .ec-emoji-panel__delete {
    opacity: 1;
  }
}

.ec-emoji-panel__hint {
  margin: 0;
  color: var(--ec-text-tertiary);
  font-size: var(--ec-font-size-secondary);
  line-height: 1.6;
}

.ec-emoji-panel__actions {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: var(--ec-space-2);
  margin-top: var(--ec-space-1);
}

.ec-emoji-panel__upload {
  display: inline-flex;
  align-items: center;
  gap: var(--ec-space-1);
  padding: var(--ec-space-1) var(--ec-space-3);
  border: 1px dashed var(--ec-border);
  border-radius: var(--ec-radius-sm);
  background: transparent;
  color: var(--ec-text-secondary);
  font-size: var(--ec-font-size-small);
  cursor: pointer;
}

.ec-emoji-panel__upload:disabled {
  color: var(--ec-text-tertiary);
  cursor: not-allowed;
}

.ec-emoji-panel__link {
  padding: 0;
  border: none;
  background: transparent;
  color: var(--ec-brand-600);
  font-size: var(--ec-font-size-small);
  cursor: pointer;
}

.ec-emoji-panel__link--danger {
  color: var(--ec-danger);
}

.ec-emoji-panel__file {
  display: none;
}

.ec-emoji-panel__item:focus-visible,
.ec-emoji-panel__tab:focus-visible,
.ec-emoji-panel__upload:focus-visible,
.ec-emoji-panel__link:focus-visible,
.ec-emoji-panel__delete:focus-visible {
  outline: var(--ec-focus-width) solid var(--ec-focus-ring);
  outline-offset: var(--ec-focus-offset);
}

/* 悬停效果仅在鼠标精确指针设备生效，触屏不产生粘滞 hover。 */
@media (hover: hover) and (pointer: fine) {
  .ec-emoji-panel__item:hover,
  .ec-emoji-panel__upload:hover,
  .ec-emoji-panel__tab:hover {
    background: var(--ec-brand-050);
  }
}

@media (max-width: 800px) {
  .ec-emoji-panel {
    width: auto;
    max-height: 260px;
  }

  /* UI-04 批次4：触屏点按目标不小于 44×44。 */
  .ec-emoji-panel__item {
    width: 44px;
    height: 44px;
    font-size: 20px;
  }

  .ec-emoji-panel__delete {
    width: 22px;
    height: 22px;
  }
}
</style>
