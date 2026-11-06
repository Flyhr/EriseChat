<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { useRouter } from 'vue-router'
import AppDialog from '../components/AppDialog.vue'
import AppIcon from '../components/AppIcon.vue'
import Avatar from '../components/Avatar.vue'
import EmptyState from '../components/EmptyState.vue'
import { friendlyError } from '../core/errorMessages'
import { formatSessionTime } from '../core/sessionFormat'
import { truncateText } from '../core/text'
import { showToast } from '../core/toast'
import { voicePreviewText } from '../core/voice'
import type { FavoriteItem } from '../types/frames'
import { useFavoritesStore } from '../stores/favorites'

// F-38：收藏页（服务端事实源，同账号跨设备同步）。
// 只展示当前仍可访问的原消息；已撤回或失去会话访问权的条目显示「原消息不可用」且只能删除。
const router = useRouter()
const favorites = useFavoritesStore()

const pendingDelete = ref<FavoriteItem | null>(null)
const deleting = ref(false)
const refreshing = ref(false)

/** 类型标签：文本 / 图片 / 文件 / 语音；不可用条目统一显示「不可用」。 */
function typeLabelOf(item: FavoriteItem): string {
  if (!item.available || !item.message) return '不可用'
  if (item.message.msgType === 'image') return '图片'
  if (item.message.msgType === 'file') return '文件'
  if (item.message.msgType === 'voice') return '语音'
  return '文本'
}

/** 一行摘要：文本截断；图片/文件/语音给占位；不可用条目隐藏正文与文件名。 */
function summaryOf(item: FavoriteItem): string {
  if (!item.available || !item.message) return '原消息不可用'
  const message = item.message
  if (message.msgType === 'image') return '[图片]'
  if (message.msgType === 'file') {
    return typeof message.content === 'object' && message.content && 'name' in message.content
      ? `[文件] ${message.content.name}`
      : '[文件]'
  }
  if (message.msgType === 'voice') {
    return typeof message.content === 'object' && message.content && 'durationMs' in message.content
      ? voicePreviewText(message.content.durationMs)
      : '[语音]'
  }
  const text = (typeof message.content === 'string' ? message.content : '').replace(/\s+/g, ' ').trim()
  return text.length === 0 ? '[空文本]' : truncateText(text, 90)
}

/** 打开原会话并定位到该消息（仅可用条目；定位复用 F-37 按目标加载上下文）。 */
function openOriginal(item: FavoriteItem): void {
  if (!item.available) return
  void router.push({
    name: 'chat',
    params: { conversationId: item.conversationId },
    query: { jumpMessageId: item.messageId },
  })
}

function askDelete(item: FavoriteItem): void {
  pendingDelete.value = item
}

function closeDelete(): void {
  if (!deleting.value) pendingDelete.value = null
}

async function confirmDelete(): Promise<void> {
  const item = pendingDelete.value
  if (!item) return
  deleting.value = true
  try {
    await favorites.remove(item.messageId)
    showToast('已删除收藏')
    pendingDelete.value = null
  } catch (error) {
    showToast(friendlyError(error), 'error')
  } finally {
    deleting.value = false
  }
}

async function refresh(): Promise<void> {
  if (refreshing.value) return
  refreshing.value = true
  try {
    await favorites.refresh()
  } catch (error) {
    showToast(friendlyError(error), 'error')
  } finally {
    refreshing.value = false
  }
}

/** 旧版本地收藏导入当前服务器：成功标记已迁移，失败条目保留并告知结果。 */
async function importLegacy(): Promise<void> {
  try {
    const result = await favorites.importLegacy()
    if (result.failed > 0) {
      showToast(
        `已导入 ${result.imported} 条，${result.failed} 条失败（失败条目已保留，可稍后重试）`,
        result.imported > 0 ? 'info' : 'error',
      )
    } else {
      showToast(`已导入 ${result.imported} 条收藏到当前服务器`)
    }
  } catch (error) {
    showToast(friendlyError(error), 'error')
  }
}

onMounted(() => {
  if (!favorites.loaded) {
    void refresh()
    void favorites.loadLegacy()
  }
})
</script>

<template>
  <main class="favorites-page">
    <header class="favorites-page__header">
      <div>
        <h2 class="favorites-page__title">收藏</h2>
        <p v-if="favorites.loaded" class="favorites-page__count">
          {{ favorites.count }} 条收藏（同步到当前账号）
        </p>
      </div>
      <button class="favorites-page__refresh" type="button" :disabled="refreshing" @click="refresh">
        {{ refreshing ? '刷新中…' : '刷新' }}
      </button>
    </header>

    <div v-if="favorites.legacyCount > 0" class="favorites-page__legacy">
      <p class="favorites-page__legacy-text">
        检测到旧版本地收藏 {{ favorites.legacyCount }} 条（仅存于本机，未同步到当前服务器）。
      </p>
      <button
        class="favorites-page__legacy-button"
        type="button"
        :disabled="favorites.importing"
        @click="importLegacy"
      >
        {{ favorites.importing ? '导入中…' : '导入到当前服务器' }}
      </button>
    </div>

    <p v-if="!favorites.loaded && favorites.loading" class="favorites-page__loading">正在读取收藏…</p>

    <template v-else-if="favorites.error">
      <EmptyState icon="star" title="收藏加载失败" :description="favorites.error" />
      <div class="favorites-page__retry">
        <button class="favorites-page__legacy-button" type="button" @click="refresh">重试</button>
      </div>
    </template>

    <EmptyState
      v-else-if="favorites.count === 0"
      icon="star"
      title="还没有收藏"
      description="在消息上右键（移动端长按）打开操作菜单，点击「收藏」即可保存到当前账号。"
    />

    <ul v-else class="favorites-page__list">
      <li
        v-for="item in favorites.items"
        :key="item.messageId"
        class="favorite-item"
        :class="{ 'favorite-item--unavailable': !item.available }"
      >
        <div class="favorite-item__main">
          <Avatar :display-name="item.available ? item.senderName : '?'" :size="36" />
          <div class="favorite-item__body">
            <p class="favorite-item__meta">
              <span class="favorite-item__sender">{{ item.available ? item.senderName : '未知发送者' }}</span>
              <span class="favorite-item__type">{{ typeLabelOf(item) }}</span>
            </p>
            <p class="favorite-item__text">{{ summaryOf(item) }}</p>
            <p class="favorite-item__source">
              <span class="favorite-item__conversation">{{ item.conversationTitle || '会话' }}</span>
              <span>收藏于 {{ formatSessionTime(item.favoritedAt) }}</span>
            </p>
          </div>
        </div>
        <div class="favorite-item__actions">
          <button
            v-if="item.available"
            class="favorite-item__button"
            type="button"
            @click="openOriginal(item)"
          >
            <AppIcon name="chat" :size="14" />
            <span>打开原会话</span>
          </button>
          <button
            class="favorite-item__button favorite-item__button--danger"
            type="button"
            @click="askDelete(item)"
          >
            删除
          </button>
        </div>
      </li>
    </ul>

    <AppDialog
      :open="pendingDelete !== null"
      title="删除收藏"
      :message="`确定删除「${pendingDelete ? summaryOf(pendingDelete) : ''}」这条收藏吗？`"
      confirm-text="删除"
      tone="danger"
      :busy="deleting"
      @confirm="confirmDelete"
      @cancel="closeDelete"
    />
  </main>
</template>

<style scoped>
.favorites-page {
  height: 100%;
  padding: var(--ec-space-6);
  overflow-y: auto;
  background: var(--ec-bg-canvas);
}

.favorites-page__header {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: var(--ec-space-4);
  max-width: 880px;
  margin: 0 auto var(--ec-space-2);
}

.favorites-page__title {
  margin: 0;
  color: var(--ec-text-primary);
  font-size: var(--ec-font-size-page-title);
  font-weight: var(--ec-font-weight-semibold);
}

.favorites-page__count {
  margin: var(--ec-space-1) 0 0;
  color: var(--ec-text-tertiary);
  font-size: var(--ec-font-size-small);
}

.favorites-page__refresh {
  flex: none;
  height: 32px;
  padding: 0 var(--ec-space-3);
  border: 1px solid var(--ec-border);
  border-radius: var(--ec-radius-input);
  background: var(--ec-surface);
  color: var(--ec-text-secondary);
  font-size: var(--ec-font-size-small);
  cursor: pointer;
}

.favorites-page__refresh:hover:not(:disabled) {
  border-color: var(--ec-brand-500);
  color: var(--ec-brand-600);
}

.favorites-page__refresh:disabled {
  color: var(--ec-disabled);
  cursor: default;
}

.favorites-page__legacy {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  justify-content: space-between;
  gap: var(--ec-space-3);
  max-width: 880px;
  margin: 0 auto var(--ec-space-4);
  padding: var(--ec-space-3) var(--ec-space-4);
  border: 1px solid var(--ec-warning);
  border-radius: var(--ec-radius-panel);
  background: var(--ec-brand-050);
}

.favorites-page__legacy-text {
  margin: 0;
  color: var(--ec-text-secondary);
  font-size: var(--ec-font-size-small);
}

.favorites-page__legacy-button {
  flex: none;
  height: 32px;
  padding: 0 var(--ec-space-4);
  border: none;
  border-radius: var(--ec-radius-input);
  background: var(--ec-brand-600);
  color: var(--ec-text-inverse);
  font-size: var(--ec-font-size-small);
  cursor: pointer;
}

.favorites-page__legacy-button:hover:not(:disabled) {
  background: var(--ec-brand-500);
}

.favorites-page__legacy-button:disabled {
  background: var(--ec-disabled);
  cursor: default;
}

.favorites-page__loading {
  max-width: 880px;
  margin: 0 auto;
  padding: var(--ec-space-8) 0;
  color: var(--ec-text-secondary);
  font-size: var(--ec-font-size-body);
  text-align: center;
}

.favorites-page__retry {
  display: flex;
  justify-content: center;
  padding: var(--ec-space-4) 0;
}

.favorites-page__list {
  display: flex;
  flex-direction: column;
  gap: var(--ec-space-3);
  max-width: 880px;
  margin: 0 auto;
  padding: 0;
  list-style: none;
}

.favorite-item {
  display: flex;
  flex-direction: column;
  gap: var(--ec-space-3);
  padding: var(--ec-space-4);
  border: 1px solid var(--ec-border);
  border-radius: var(--ec-radius-panel);
  background: var(--ec-surface);
  box-shadow: var(--ec-shadow-sm);
}

.favorite-item--unavailable {
  background: var(--ec-surface-hover);
}

.favorite-item--unavailable .favorite-item__text {
  color: var(--ec-text-tertiary);
  font-style: italic;
}

.favorite-item__main {
  display: flex;
  gap: var(--ec-space-3);
  align-items: flex-start;
}

.favorite-item__body {
  display: flex;
  flex: 1;
  flex-direction: column;
  gap: var(--ec-space-1);
  min-width: 0;
}

.favorite-item__meta {
  display: flex;
  gap: var(--ec-space-2);
  align-items: baseline;
  margin: 0;
}

.favorite-item__sender {
  color: var(--ec-text-primary);
  font-size: var(--ec-font-size-body);
  font-weight: var(--ec-font-weight-semibold);
}

.favorite-item__type {
  color: var(--ec-text-tertiary);
  font-size: var(--ec-font-size-small);
}

.favorite-item__text {
  margin: 0;
  color: var(--ec-text-primary);
  font-size: var(--ec-font-size-body);
  line-height: 1.5;
  overflow-wrap: anywhere;
}

.favorite-item__source {
  display: flex;
  flex-wrap: wrap;
  gap: var(--ec-space-2);
  margin: 0;
  color: var(--ec-text-tertiary);
  font-size: var(--ec-font-size-small);
}

.favorite-item__conversation {
  color: var(--ec-text-secondary);
}

.favorite-item__actions {
  display: flex;
  gap: var(--ec-space-2);
  justify-content: flex-end;
}

.favorite-item__button {
  display: inline-flex;
  gap: var(--ec-space-1);
  align-items: center;
  height: 32px;
  padding: 0 var(--ec-space-3);
  border: 1px solid var(--ec-border);
  border-radius: var(--ec-radius-input);
  background: var(--ec-surface);
  color: var(--ec-text-secondary);
  font-size: var(--ec-font-size-small);
  cursor: pointer;
}

.favorite-item__button:hover {
  border-color: var(--ec-brand-500);
  color: var(--ec-brand-600);
}

.favorite-item__button--danger {
  color: var(--ec-danger);
}

.favorite-item__button--danger:hover {
  border-color: var(--ec-danger);
  color: var(--ec-danger);
}

@media (max-width: 800px) {
  .favorites-page {
    padding: var(--ec-space-4);
  }

  .favorites-page__header {
    align-items: center;
  }
}
</style>
