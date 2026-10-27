<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, ref, watch } from 'vue'
import type { Conversation } from '../types/frames'
import { conversationLabel, systemMessageText } from '../core/groupChat'
import AppIcon from './AppIcon.vue'
import Avatar from './Avatar.vue'
import UnreadBadge from './UnreadBadge.vue'

// 会话列表项（UI-02 批次1，计划 §5.2）：行高桌面 72 / 紧凑 64 / 移动 44；头像 44；
// 选中浅蓝底 + 左侧 3px 品牌色指示条；hover 浅灰蓝；标题单行、摘要一行、时间与未读角标保留。
// F-10：右侧「⋯」按钮 / 桌面右键打开小菜单，可切换置顶与免打扰；状态变化交给父组件调 store。
const props = defineProps<{
  conversation: Conversation
  /** 当前登录用户 ID：用于单聊时找出对方昵称 */
  selfUserId?: string
  active?: boolean
  /** 未读条数（0 或未传时不展示角标；会话被选中时由父组件传 0） */
  unread?: number
}>()

const emit = defineEmits<{
  select: [conversationId: string]
  /** F-10：请求父组件更新本人成员设置（置顶/免打扰）。 */
  'update-settings': [
    payload: { conversationId: string; settings: { pinned?: boolean; muted?: boolean } },
  ]
}>()

const otherMember = computed(() => {
  if (props.conversation.type === 'group') return undefined
  return props.conversation.members.find((member) => member.userId !== props.selfUserId)
})

const title = computed(() => {
  const conversation = props.conversation
  if (conversation.type === 'group') return conversationLabel(conversation)
  return otherMember.value?.displayName ?? conversation.title ?? '单聊会话'
})

const avatarFileId = computed(() => otherMember.value?.avatarFileId)

const preview = computed(() => {
  const message = props.conversation.lastMessage
  if (!message) return '暂无消息'

  switch (message.msgType) {
    case 'image':
      return '[图片]'
    case 'file':
      return typeof message.content === 'object' &&
        message.content !== null &&
        'name' in message.content
        ? `[文件] ${String(message.content.name)}`
        : '[文件]'
    case 'system':
      return truncate(systemMessageText(message.content))
    default:
      return typeof message.content === 'string' ? truncate(message.content) : '暂无消息'
  }
})

const timeText = computed(() => formatTime(props.conversation.lastMessage?.createdAt ?? props.conversation.createdAt))

const menuOpen = ref(false)
const moreButton = ref<HTMLButtonElement | null>(null)
const menuRef = ref<HTMLElement | null>(null)
const menuPosition = ref({ top: 0, left: 0 })
const MENU_WIDTH = 168

function toggleMenu(): void {
  if (menuOpen.value) {
    closeMenu(false)
    return
  }
  openMenu()
}

function openMenu(): void {
  const rect = moreButton.value?.getBoundingClientRect()
  if (!rect) return
  const left = Math.max(8, Math.min(rect.right - MENU_WIDTH, window.innerWidth - MENU_WIDTH - 8))
  menuPosition.value = { top: rect.bottom + 4, left }
  menuOpen.value = true
  void nextTick(() => {
    menuRef.value?.querySelector<HTMLButtonElement>('[role="menuitem"]')?.focus()
  })
}

function closeMenu(returnFocus = true): void {
  if (!menuOpen.value) return
  menuOpen.value = false
  if (returnFocus) moreButton.value?.focus()
}

function togglePinned(): void {
  emit('update-settings', {
    conversationId: props.conversation.conversationId,
    settings: { pinned: !props.conversation.pinned },
  })
  closeMenu()
}

function toggleMuted(): void {
  emit('update-settings', {
    conversationId: props.conversation.conversationId,
    settings: { muted: !props.conversation.muted },
  })
  closeMenu()
}

function onContextMenu(): void {
  openMenu()
}

function onDocumentPointerDown(event: PointerEvent): void {
  if (!menuOpen.value) return
  const target = event.target as Node | null
  if (target && (menuRef.value?.contains(target) || moreButton.value?.contains(target))) return
  closeMenu(false)
}

function onDocumentKeyDown(event: KeyboardEvent): void {
  if (event.key === 'Escape') closeMenu()
}

watch(menuOpen, (open) => {
  if (typeof document === 'undefined') return
  if (open) {
    document.addEventListener('pointerdown', onDocumentPointerDown, true)
    document.addEventListener('keydown', onDocumentKeyDown)
  } else {
    document.removeEventListener('pointerdown', onDocumentPointerDown, true)
    document.removeEventListener('keydown', onDocumentKeyDown)
  }
})

onBeforeUnmount(() => {
  if (typeof document === 'undefined') return
  document.removeEventListener('pointerdown', onDocumentPointerDown, true)
  document.removeEventListener('keydown', onDocumentKeyDown)
})

function truncate(text: string, max = 80): string {
  const normalized = text.replace(/\s+/g, ' ').trim()
  return normalized.length > max ? `${normalized.slice(0, max)}…` : normalized
}

function formatTime(timestamp: number): string {
  const date = new Date(timestamp)
  if (Number.isNaN(date.getTime())) return ''
  const now = new Date()
  const sameDay =
    date.getFullYear() === now.getFullYear() &&
    date.getMonth() === now.getMonth() &&
    date.getDate() === now.getDate()
  const hh = String(date.getHours()).padStart(2, '0')
  const mm = String(date.getMinutes()).padStart(2, '0')
  if (sameDay) return `${hh}:${mm}`
  const month = String(date.getMonth() + 1).padStart(2, '0')
  const day = String(date.getDate()).padStart(2, '0')
  return `${month}-${day} ${hh}:${mm}`
}
</script>

<template>
  <div
    class="conversation-item"
    :class="{ 'conversation-item--active': active }"
    @contextmenu.prevent="onContextMenu"
  >
    <button
      type="button"
      class="conversation-item__main"
      :aria-current="active ? 'true' : undefined"
      @click="emit('select', conversation.conversationId)"
    >
      <Avatar :display-name="title" :avatar-file-id="avatarFileId" :size="44" />
      <span class="conversation-item__body">
        <span class="conversation-item__title">{{ title }}</span>
        <span class="conversation-item__preview">{{ preview }}</span>
      </span>
      <span class="conversation-item__meta">
        <span class="conversation-item__time">{{ timeText }}</span>
        <span class="conversation-item__flags">
          <AppIcon
            v-if="conversation.muted"
            name="mute"
            :size="13"
            class="conversation-item__flag"
            role="img"
            aria-label="免打扰"
          />
          <AppIcon
            v-if="conversation.pinned"
            name="pin"
            :size="13"
            class="conversation-item__flag"
            role="img"
            aria-label="已置顶"
          />
          <UnreadBadge :count="unread" />
        </span>
      </span>
    </button>

    <button
      ref="moreButton"
      type="button"
      class="conversation-item__more"
      aria-label="会话操作"
      aria-haspopup="menu"
      :aria-expanded="menuOpen"
      @click.stop="toggleMenu"
    >
      <AppIcon name="more" :size="16" />
    </button>

    <Teleport to="body">
      <div
        v-if="menuOpen"
        ref="menuRef"
        class="conversation-menu"
        role="menu"
        :style="{ top: `${menuPosition.top}px`, left: `${menuPosition.left}px` }"
      >
        <button type="button" role="menuitem" class="conversation-menu__item" @click="togglePinned">
          <AppIcon name="pin" :size="16" />
          <span>{{ conversation.pinned ? '取消置顶' : '置顶会话' }}</span>
        </button>
        <button type="button" role="menuitem" class="conversation-menu__item" @click="toggleMuted">
          <AppIcon name="mute" :size="16" />
          <span>{{ conversation.muted ? '取消免打扰' : '消息免打扰' }}</span>
        </button>
      </div>
    </Teleport>
  </div>
</template>

<style scoped>
.conversation-item {
  position: relative;
  display: flex;
  align-items: stretch;
  width: 100%;
  min-height: 72px;
  transition: background var(--ec-transition-fast) var(--ec-ease);
}

/* 悬停效果仅在鼠标精确指针设备生效，触屏不产生粘滞 hover。 */
@media (hover: hover) and (pointer: fine) {
  .conversation-item:hover:not(.conversation-item--active) {
    background: var(--ec-surface-hover);
  }
}

.conversation-item--active {
  /* 「澄蓝」主题：选中行用极浅的横向蓝色渐变，比纯色底更轻。 */
  background: var(--ec-row-active-bg);
}

/* 选中会话左侧 3px 品牌色指示条（计划 §5.2）。 */
.conversation-item--active::before {
  position: absolute;
  top: var(--ec-space-2);
  bottom: var(--ec-space-2);
  left: 0;
  width: 3px;
  border-radius: 0 var(--ec-radius-full) var(--ec-radius-full) 0;
  background: var(--ec-brand-600);
  content: '';
}

.conversation-item__main {
  display: flex;
  flex: 1;
  gap: var(--ec-space-3);
  align-items: center;
  min-width: 0;
  padding: var(--ec-space-2) var(--ec-space-2) var(--ec-space-2) var(--ec-space-4);
  border: none;
  background: transparent;
  text-align: left;
  cursor: pointer;
}

.conversation-item__body {
  display: flex;
  flex: 1;
  flex-direction: column;
  gap: 2px;
  min-width: 0;
  overflow: hidden;
}

.conversation-item__title {
  overflow: hidden;
  color: var(--ec-text-primary);
  font-size: var(--ec-font-size-body);
  font-weight: var(--ec-font-weight-semibold);
  text-overflow: ellipsis;
  white-space: nowrap;
}

.conversation-item__preview {
  overflow: hidden;
  color: var(--ec-text-secondary);
  font-size: var(--ec-font-size-secondary);
  text-overflow: ellipsis;
  white-space: nowrap;
}

.conversation-item__meta {
  display: flex;
  flex: none;
  flex-direction: column;
  gap: var(--ec-space-1);
  align-items: flex-end;
  align-self: stretch;
  justify-content: center;
}

.conversation-item__time {
  color: var(--ec-text-secondary);
  font-size: var(--ec-font-size-small);
}

.conversation-item__flags {
  display: flex;
  gap: 4px;
  align-items: center;
}

.conversation-item__flag {
  color: var(--ec-text-tertiary);
}

.conversation-item__more {
  display: grid;
  flex: none;
  place-items: center;
  align-self: center;
  width: 32px;
  height: 32px;
  margin-right: var(--ec-space-2);
  border: none;
  border-radius: var(--ec-radius-sm);
  background: transparent;
  color: var(--ec-text-tertiary);
  cursor: pointer;
  opacity: 0;
  transition: opacity var(--ec-transition-fast) var(--ec-ease), background var(--ec-transition-fast) var(--ec-ease);
}

@media (hover: hover) and (pointer: fine) {
  .conversation-item:hover .conversation-item__more,
  .conversation-item--active .conversation-item__more {
    opacity: 1;
  }

  .conversation-item__more:hover {
    background: var(--ec-surface-hover);
    color: var(--ec-text-secondary);
  }
}

/* 键盘 Tab 到操作按钮时也可见，避免只能靠鼠标发现。 */
.conversation-item__more:focus-visible {
  opacity: 1;
}

/* 触屏无 hover：操作按钮常显，保证可点（≥44×44 由 UI-04 触控规则兜底）。 */
@media (hover: none) {
  .conversation-item__more {
    width: 40px;
    height: 44px;
    opacity: 1;
  }
}

/* 菜单：Teleport 到 body 后用 fixed 定位，避免被会话列表滚动容器裁剪。 */
.conversation-menu {
  position: fixed;
  z-index: 60;
  display: flex;
  flex-direction: column;
  width: 168px;
  padding: var(--ec-space-1);
  border: 1px solid var(--ec-border);
  border-radius: var(--ec-radius-md);
  background: var(--ec-surface);
  box-shadow: var(--ec-shadow-lg);
}

.conversation-menu__item {
  display: flex;
  gap: var(--ec-space-2);
  align-items: center;
  width: 100%;
  padding: var(--ec-space-2) var(--ec-space-3);
  border: none;
  border-radius: var(--ec-radius-sm);
  background: transparent;
  color: var(--ec-text-primary);
  font-size: var(--ec-font-size-secondary);
  text-align: left;
  cursor: pointer;
}

@media (hover: hover) and (pointer: fine) {
  .conversation-menu__item:hover {
    background: var(--ec-surface-hover);
  }
}

.conversation-menu__item:focus-visible {
  outline: 2px solid var(--ec-focus-ring);
  outline-offset: 1px;
}

/* 801~1199：紧凑行高 64（Electron 最小 960×640 仍可用）。 */
@media (max-width: 1199px) {
  .conversation-item {
    min-height: 64px;
  }
}

/* ≤800：移动单栏列表行高由 token 控制（UI-05 移动端密度），压缩内边距。 */
@media (max-width: 800px) {
  .conversation-item {
    min-height: var(--ec-row-min-height-mobile);
    border-bottom: 1px solid var(--ec-border);
  }

  .conversation-item:last-child {
    border-bottom: none;
  }

  .conversation-item__title {
    font-size: var(--ec-font-size-mobile-list-title);
  }

  .conversation-item__preview {
    font-size: var(--ec-font-size-mobile-list-sub);
  }
}
</style>
