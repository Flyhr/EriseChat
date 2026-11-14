<script setup lang="ts">
import { computed, nextTick, ref, watch } from 'vue'
import type { Conversation } from '../types/frames'
import AppIcon from './AppIcon.vue'
import GroupMembersPanel from './GroupMembersPanel.vue'
import ConversationFilesPanel from './ConversationFilesPanel.vue'

// 会话详情容器（批次 B2）：群资料与成员、共享文件统一到这里，按需打开、页签切换、关闭与焦点归还。
// 页面只负责「打开哪个页签」，容器负责布局：≥1200 宽屏侧栏、801~1199 覆盖层（带遮罩）、≤800 全屏详情。
// 断点数值沿用 styles/variables.css 的四档，不新增第三套。
const props = defineProps<{
  open: boolean
  conversation: Conversation | null
  selfUserId: string
  tab: 'members' | 'files'
  /** 打开详情的触发元素：关闭后把焦点还回去，键盘用户不丢位置。 */
  trigger?: HTMLElement | null
}>()

const emit = defineEmits<{
  close: []
  'update:tab': [tab: 'members' | 'files']
  updated: [conversation: Conversation]
  left: [conversationId: string]
  'open-chat': [conversationId: string]
  /** 请求在聊天区定位到指定消息（C2c：共享媒体面板）。 */
  'locate-message': [messageId: string]
}>()

const isGroup = computed(() => props.conversation?.type === 'group')
const closeButton = ref<HTMLButtonElement | null>(null)

// 打开后把焦点交给关闭按钮（Esc 与读屏都从这里开始）；关闭时归还给触发元素。
watch(
  () => props.open,
  (open) => {
    if (open) {
      void nextTick(() => closeButton.value?.focus())
      return
    }
    props.trigger?.focus?.()
  },
)

/** 面板内 Esc 只关面板：阻止继续冒泡到页面的「退出会话」处理。 */
function onKeydown(event: KeyboardEvent): void {
  if (event.key !== 'Escape') return
  event.preventDefault()
  event.stopPropagation()
  emit('close')
}
</script>

<template>
  <div v-if="open && conversation" class="chat-details">
    <div class="chat-details__mask" aria-hidden="true" @click="emit('close')" />
    <aside
      class="chat-details__panel"
      aria-label="会话详情"
      @keydown="onKeydown"
    >
      <header class="chat-details__header">
        <div v-if="isGroup" class="chat-details__tabs" role="tablist" aria-label="详情页签">
          <button
            type="button"
            role="tab"
            :aria-selected="tab === 'members'"
            :class="['chat-details__tab', { 'chat-details__tab--active': tab === 'members' }]"
            @click="emit('update:tab', 'members')"
          >
            群资料与成员
          </button>
          <button
            type="button"
            role="tab"
            :aria-selected="tab === 'files'"
            :class="['chat-details__tab', { 'chat-details__tab--active': tab === 'files' }]"
            @click="emit('update:tab', 'files')"
          >
            文件
          </button>
        </div>
        <h2 v-else class="chat-details__title">文件</h2>
        <button
          ref="closeButton"
          type="button"
          class="chat-details__close"
          aria-label="关闭详情"
          @click="emit('close')"
        >
          <AppIcon name="close" :size="18" />
        </button>
      </header>
      <div class="chat-details__body">
        <GroupMembersPanel
          v-if="tab === 'members' && isGroup"
          :conversation="conversation"
          :self-user-id="selfUserId"
          @updated="emit('updated', $event)"
          @left="emit('left', $event)"
          @open-chat="emit('open-chat', $event)"
        />
        <ConversationFilesPanel
          v-else-if="tab === 'files'"
          :conversation-id="conversation.conversationId"
          @locate-message="emit('locate-message', $event)"
        />
      </div>
    </aside>
  </div>
</template>

<style scoped>
/* display: contents：宽屏时面板仍是 .chat-page__content 的 flex 子项，聊天区收窄而不是被覆盖。 */
.chat-details {
  display: contents;
}

.chat-details__panel {
  display: flex;
  flex: none;
  flex-direction: column;
  width: var(--ec-drawer-width);
  min-width: 0;
  min-height: 0;
  border-left: 1px solid var(--ec-border);
  background: var(--ec-surface);
}

.chat-details__header {
  display: flex;
  flex: none;
  gap: var(--ec-space-2);
  align-items: center;
  justify-content: space-between;
  padding: var(--ec-space-3) var(--ec-space-4);
  border-bottom: 1px solid var(--ec-border);
}

.chat-details__title {
  margin: 0;
  color: var(--ec-text-primary);
  font-size: 15px;
  font-weight: var(--ec-font-weight-semibold);
}

.chat-details__tabs {
  display: flex;
  gap: var(--ec-space-1);
  min-width: 0;
  padding: 3px;
  border-radius: var(--ec-radius-full);
  background: var(--ec-surface-hover);
}

.chat-details__tab {
  min-width: 0;
  min-height: 28px;
  padding: 0 var(--ec-space-3);
  border: none;
  border-radius: var(--ec-radius-full);
  background: transparent;
  color: var(--ec-text-secondary);
  font-size: var(--ec-font-size-secondary);
  white-space: nowrap;
  cursor: pointer;
}

.chat-details__tab--active {
  background: var(--ec-surface);
  color: var(--ec-text-primary);
  font-weight: var(--ec-font-weight-semibold);
  box-shadow: var(--ec-shadow-sm);
}

.chat-details__tab:focus-visible {
  outline: var(--ec-focus-width) solid var(--ec-focus-ring);
  outline-offset: var(--ec-focus-offset);
}

.chat-details__close {
  display: grid;
  flex: none;
  width: 28px;
  height: 28px;
  place-items: center;
  border: none;
  border-radius: var(--ec-radius-sm);
  background: transparent;
  color: var(--ec-text-secondary);
  cursor: pointer;
}

@media (hover: hover) and (pointer: fine) {
  .chat-details__close:hover {
    background: var(--ec-surface-hover);
    color: var(--ec-text-primary);
  }
}

.chat-details__close:focus-visible {
  outline: var(--ec-focus-width) solid var(--ec-focus-ring);
  outline-offset: var(--ec-focus-offset);
}

.chat-details__body {
  flex: 1;
  min-height: 0;
  padding: var(--ec-space-4);
  overflow-y: auto;
}

/* 遮罩只在 801~1199 覆盖层出现；宽屏与手机不显示。 */
.chat-details__mask {
  display: none;
}

/* 801~1199（窄桌面/小窗口）：覆盖在聊天区右侧并加遮罩，聊天列保持全宽，避免四栏挤压输入区。 */
@media (min-width: 801px) and (max-width: 1199px) {
  .chat-details__mask {
    position: absolute;
    inset: 0;
    z-index: 14;
    display: block;
    background: var(--ec-scrim);
  }

  .chat-details__panel {
    position: absolute;
    top: 0;
    right: 0;
    bottom: 0;
    z-index: 15;
    width: 260px;
    box-shadow: var(--ec-shadow-lg);
  }
}

/* ≤800：全屏详情页（聊天列由页面隐藏），返回/关闭在顶栏内。 */
@media (max-width: 800px) {
  .chat-details__panel {
    flex: 1;
    width: 100%;
    border-left: none;
  }

  .chat-details__close {
    min-width: var(--ec-touch-target);
    min-height: var(--ec-touch-target);
  }

  .chat-details__tab {
    min-height: var(--ec-touch-target);
  }
}
</style>
