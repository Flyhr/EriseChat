<script setup lang="ts">
import { ref } from 'vue'
import { useRouter } from 'vue-router'
import AppDialog from '../components/AppDialog.vue'
import AppIcon from '../components/AppIcon.vue'
import Avatar from '../components/Avatar.vue'
import EmptyState from '../components/EmptyState.vue'
import { formatSessionTime } from '../core/sessionFormat'
import { showToast } from '../core/toast'
import type { FavoriteRecord } from '../core/favorites'
import { useFavoritesStore } from '../stores/favorites'

// F-26：本地收藏页（本设备 IndexedDB 快照，按账号隔离，不做云端同步）。
// 只做展示与删除：不发起任何收藏相关网络请求；「打开原会话」复用 F-11 的 jumpSeq 定位。
const router = useRouter()
const favorites = useFavoritesStore()

const pendingDelete = ref<FavoriteRecord | null>(null)
const deleting = ref(false)

/** 类型标签：文本 / 图片 / 文件。 */
function typeLabelOf(record: FavoriteRecord): string {
  if (record.msgType === 'image') return '图片'
  if (record.msgType === 'file') return '文件'
  return '文本'
}

/** 一行摘要：文本截断，图片/文件给占位与文件名。 */
function summaryOf(record: FavoriteRecord): string {
  if (record.msgType === 'image') return '[图片]'
  if (record.msgType === 'file') {
    return record.file ? `[文件] ${record.file.name}` : '[文件]'
  }
  const text = (record.text ?? '').replace(/\s+/g, ' ').trim()
  if (text.length === 0) return '[空文本]'
  return text.length > 90 ? `${text.slice(0, 90)}…` : text
}

/** 打开原会话并定位到收藏时的消息序号（原消息已撤回时仍可跳到会话历史）。 */
function openOriginal(record: FavoriteRecord): void {
  void router.push({
    name: 'chat',
    params: { conversationId: record.conversationId },
    query: { jumpSeq: String(record.sourceSeq) },
  })
}

function askDelete(record: FavoriteRecord): void {
  pendingDelete.value = record
}

function closeDelete(): void {
  if (!deleting.value) pendingDelete.value = null
}

async function confirmDelete(): Promise<void> {
  const record = pendingDelete.value
  if (!record) return
  deleting.value = true
  try {
    await favorites.remove(record.favoriteId)
    showToast('已删除收藏')
    pendingDelete.value = null
  } catch (error) {
    console.warn('[favorites] 删除收藏失败', error)
    showToast('删除收藏失败，请稍后重试', 'error')
  } finally {
    deleting.value = false
  }
}
</script>

<template>
  <main class="favorites-page">
    <header class="favorites-page__header">
      <div>
        <h2 class="favorites-page__title">收藏</h2>
        <p v-if="favorites.loaded" class="favorites-page__count">{{ favorites.items.length }} 条本地收藏</p>
      </div>
    </header>

    <p class="favorites-page__hint">
      收藏仅保存在本设备并按账号隔离，不做云端同步；内容为收藏时的快照，原消息之后的撤回或编辑不会改变这里的内容。
    </p>

    <p v-if="!favorites.loaded" class="favorites-page__loading">正在读取本地收藏…</p>

    <EmptyState
      v-else-if="favorites.items.length === 0"
      icon="star"
      title="还没有收藏"
      description="在消息上右键（移动端长按）打开操作菜单，点击「收藏」即可把文本、图片或文件保存到这里。"
    />

    <ul v-else class="favorites-page__list">
      <li v-for="record in favorites.items" :key="record.favoriteId" class="favorite-item">
        <div class="favorite-item__main">
          <Avatar :display-name="record.senderName" :size="36" />
          <div class="favorite-item__body">
            <p class="favorite-item__meta">
              <span class="favorite-item__sender">{{ record.senderName }}</span>
              <span class="favorite-item__type">{{ typeLabelOf(record) }}</span>
            </p>
            <p class="favorite-item__text">{{ summaryOf(record) }}</p>
            <p class="favorite-item__source">
              <span class="favorite-item__conversation">{{ record.conversationTitle }}</span>
              <span>收藏于 {{ formatSessionTime(record.favoritedAt) }}</span>
            </p>
          </div>
        </div>
        <div class="favorite-item__actions">
          <button class="favorite-item__button" type="button" @click="openOriginal(record)">
            <AppIcon name="chat" :size="14" />
            <span>打开原会话</span>
          </button>
          <button
            class="favorite-item__button favorite-item__button--danger"
            type="button"
            @click="askDelete(record)"
          >
            删除
          </button>
        </div>
      </li>
    </ul>

    <AppDialog
      :open="pendingDelete !== null"
      title="删除收藏"
      :message="`确定删除「${pendingDelete ? summaryOf(pendingDelete) : ''}」这条收藏吗？删除后不可恢复。`"
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

.favorites-page__hint {
  max-width: 880px;
  margin: 0 auto var(--ec-space-5);
  color: var(--ec-text-secondary);
  font-size: var(--ec-font-size-small);
  line-height: 1.6;
}

.favorites-page__loading {
  max-width: 880px;
  margin: 0 auto;
  padding: var(--ec-space-8) 0;
  color: var(--ec-text-secondary);
  font-size: var(--ec-font-size-body);
  text-align: center;
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

.favorite-item__main {
  display: flex;
  gap: var(--ec-space-3);
  align-items: flex-start;
}

.favorite-item__body {
  display: flex;
  flex: 1;
  flex-direction: column;
  gap: 4px;
  min-width: 0;
}

.favorite-item__meta {
  display: flex;
  gap: var(--ec-space-2);
  align-items: center;
  margin: 0;
}

.favorite-item__sender {
  overflow: hidden;
  color: var(--ec-text-primary);
  font-size: var(--ec-font-size-body);
  font-weight: var(--ec-font-weight-semibold);
  text-overflow: ellipsis;
  white-space: nowrap;
}

.favorite-item__type {
  flex: none;
  padding: 1px 6px;
  border-radius: var(--ec-radius-full);
  background: var(--ec-brand-050);
  color: var(--ec-brand-600);
  font-size: 11px;
}

.favorite-item__text {
  margin: 0;
  overflow: hidden;
  color: var(--ec-text-primary);
  font-size: var(--ec-font-size-body);
  line-height: 1.5;
  overflow-wrap: anywhere;
  white-space: pre-wrap;
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
  overflow: hidden;
  max-width: 260px;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.favorite-item__actions {
  display: flex;
  gap: var(--ec-space-2);
  justify-content: flex-end;
}

.favorite-item__button {
  display: inline-flex;
  gap: 4px;
  align-items: center;
  min-height: var(--ec-touch-target);
  padding: 0 var(--ec-space-3);
  border: 1px solid var(--ec-border);
  border-radius: var(--ec-radius-sm);
  background: var(--ec-surface);
  color: var(--ec-text-secondary);
  font-size: var(--ec-font-size-small);
  cursor: pointer;
  transition: background-color var(--ec-transition-fast), border-color var(--ec-transition-fast);
}

.favorite-item__button:hover {
  border-color: var(--ec-brand-100);
  background: var(--ec-brand-050);
  color: var(--ec-brand-600);
}

.favorite-item__button:focus-visible {
  outline: var(--ec-focus-width) solid var(--ec-focus-ring);
  outline-offset: var(--ec-focus-offset);
}

.favorite-item__button--danger:hover {
  border-color: var(--ec-danger);
  background: var(--ec-surface-hover);
  color: var(--ec-danger);
}

@media (max-width: 800px) {
  .favorites-page {
    padding: var(--ec-space-4);
  }

  .favorite-item__actions {
    justify-content: stretch;
  }

  .favorite-item__button {
    flex: 1;
    justify-content: center;
  }
}
</style>
