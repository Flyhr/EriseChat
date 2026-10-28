<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import AppDialog from './AppDialog.vue'
import Avatar from './Avatar.vue'
import EmptyState from './EmptyState.vue'
import { filterForwardTargets, forwardTextOf } from '../core/forward'
import { showToast } from '../core/toast'
import { useConversationsStore } from '../stores/conversations'
import { useMessagesStore } from '../stores/messages'
import type { Message } from '../types/frames'

// F-26：文本消息转发弹窗（AppDialog wide）。
// 目标会话排除当前会话；可按标题搜索；选中后发送一条新消息（新 clientMsgId、不带 replyTo）。
// 只转发文本：图片/文件因服务端 fileId 归属校验，不在本批范围（菜单里也不出现转发入口）。
const props = defineProps<{
  open: boolean
  message: Message | null
  selfUserId: string
}>()

const emit = defineEmits<{
  close: []
  forwarded: [conversationId: string]
}>()

const conversations = useConversationsStore()
const messages = useMessagesStore()

const query = ref('')
const selectedId = ref('')
const submitting = ref(false)

const previewText = computed(() => forwardTextOf(props.message))
const targets = computed(() =>
  filterForwardTargets(conversations.sorted, props.message?.conversationId ?? '', query.value),
)
const selected = computed(() => targets.value.find((conversation) => conversation.conversationId === selectedId.value) ?? null)

watch(
  () => props.open,
  (open) => {
    if (!open) return
    query.value = ''
    selectedId.value = ''
    submitting.value = false
  },
)

function confirmForward(): void {
  const target = selected.value
  if (!target || submitting.value) return
  submitting.value = true
  const sent = messages.sendText(target.conversationId, previewText.value, props.selfUserId)
  submitting.value = false
  if (!sent) {
    showToast('转发失败，请检查网络后重试', 'error')
    return
  }
  showToast('已转发')
  emit('forwarded', target.conversationId)
  emit('close')
}
</script>

<template>
  <AppDialog
    :open="open"
    title="转发消息"
    confirm-text="转发"
    :busy="submitting"
    wide
    @confirm="confirmForward"
    @cancel="emit('close')"
  >
    <div class="forward-dialog">
      <p class="forward-dialog__preview">{{ previewText }}</p>
      <input
        v-model="query"
        class="forward-dialog__search"
        type="search"
        placeholder="搜索会话"
        aria-label="搜索会话"
      />
      <div class="forward-dialog__list" role="listbox" aria-label="转发目标会话">
        <button
          v-for="conversation in targets"
          :key="conversation.conversationId"
          type="button"
          role="option"
          :aria-selected="selectedId === conversation.conversationId"
          class="forward-dialog__item"
          :class="{ 'forward-dialog__item--active': selectedId === conversation.conversationId }"
          @click="selectedId = conversation.conversationId"
        >
          <Avatar :display-name="conversation.title" :size="32" :avatar-file-id="conversation.avatarFileId ?? undefined" />
          <span class="forward-dialog__title">{{ conversation.title }}</span>
          <span class="forward-dialog__type">{{ conversation.type === 'group' ? '群聊' : '单聊' }}</span>
        </button>
      </div>
      <EmptyState
        v-if="targets.length === 0"
        title="没有可转发的会话"
        :description="query.trim() ? '没有匹配的会话，换个关键词试试' : '当前没有其他会话可转发'"
      />
    </div>
  </AppDialog>
</template>

<style scoped>
.forward-dialog {
  display: flex;
  flex-direction: column;
  gap: var(--ec-space-3);
  min-height: 0;
}

.forward-dialog__preview {
  display: -webkit-box;
  margin: 0;
  padding: var(--ec-space-2) var(--ec-space-3);
  overflow: hidden;
  color: var(--ec-text-secondary);
  background: var(--ec-surface-hover);
  border-radius: var(--ec-radius-input);
  -webkit-box-orient: vertical;
  -webkit-line-clamp: 2;
}

.forward-dialog__search {
  width: 100%;
  height: 36px;
  padding: 0 var(--ec-space-3);
  color: var(--ec-text-primary);
  background: var(--ec-surface);
  border: 1px solid var(--ec-border);
  border-radius: var(--ec-radius-input);
}

.forward-dialog__search:focus-visible {
  border-color: var(--ec-focus);
  outline: 2px solid var(--ec-focus);
  outline-offset: 2px;
}

.forward-dialog__list {
  display: flex;
  flex-direction: column;
  gap: 2px;
  max-height: 300px;
  overflow-y: auto;
}

.forward-dialog__item {
  display: flex;
  gap: var(--ec-space-3);
  align-items: center;
  min-height: 48px;
  padding: var(--ec-space-1) var(--ec-space-2);
  text-align: left;
  background: transparent;
  border: none;
  border-radius: var(--ec-radius-panel);
  cursor: pointer;
}

.forward-dialog__item:hover {
  background: var(--ec-surface-hover);
}

.forward-dialog__item--active {
  background: var(--ec-brand-050);
}

.forward-dialog__title {
  flex: 1;
  min-width: 0;
  overflow: hidden;
  font-size: var(--ec-font-size-base);
  font-weight: 600;
  color: var(--ec-text-primary);
  text-overflow: ellipsis;
  white-space: nowrap;
}

.forward-dialog__type {
  flex: none;
  font-size: var(--ec-font-size-small);
  color: var(--ec-text-tertiary);
}

@media (max-width: 800px) {
  .forward-dialog__list {
    max-height: 40vh;
  }
}
</style>
