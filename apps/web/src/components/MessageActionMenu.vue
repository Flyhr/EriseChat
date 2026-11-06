<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, ref, watch } from 'vue'
import type { Message } from '../types/frames'
import {
  buildMessageActions,
  type MessageActionId,
  type MessageActionOptions,
} from '../core/messageActions'

// F-18：消息操作菜单（右键/长按打开）。只负责展示与派发 action，
// 撤回/编辑是否可用由 core/messageActions 按身份与时间窗口过滤（服务端仍是最终判定）。
// 桌面按坐标弹出并做视口内收敛；≤800 改为贴底操作表，Esc/点击外部关闭，焦点进入并在关闭后归还。
const props = withDefaults(
  defineProps<{
    open: boolean
    message: Message | null
    selfUserId: string
    isGroup?: boolean
    senderName?: string
    /** F-26：该消息是否已被收藏（决定「收藏」/「取消收藏」）。 */
    favorited?: boolean
    /** F-36：是否可管理群置顶（群主）。 */
    canManagePinned?: boolean
    /** F-36：该消息是否为当前群置顶消息。 */
    pinned?: boolean
    /** 触发位置（视口坐标；移动端贴底时忽略）。 */
    position: { x: number; y: number }
  }>(),
  {
    isGroup: false,
    senderName: '',
    favorited: false,
    canManagePinned: false,
    pinned: false,
    position: () => ({ x: 0, y: 0 }),
  },
)

const emit = defineEmits<{
  select: [action: MessageActionId]
  close: []
}>()

const MENU_WIDTH = 148
const ITEM_HEIGHT = 40
const MARGIN = 8

const menu = ref<HTMLElement | null>(null)
let previousFocus: HTMLElement | null = null

const actions = computed(() => {
  if (!props.open || !props.message) return []
  const options: MessageActionOptions = {
    selfUserId: props.selfUserId,
    isGroup: props.isGroup,
    senderName: props.senderName,
    favorited: props.favorited,
    canManagePinned: props.canManagePinned,
    pinned: props.pinned,
  }
  return buildMessageActions(props.message, options)
})

const menuStyle = computed(() => {
  if (!props.open) return undefined
  if (window.matchMedia('(max-width: 800px)').matches) return undefined
  const height = actions.value.length * ITEM_HEIGHT + MARGIN * 2
  const maxX = Math.max(MARGIN, window.innerWidth - MENU_WIDTH - MARGIN)
  const maxY = Math.max(MARGIN, window.innerHeight - height - MARGIN)
  const x = Math.min(Math.max(props.position.x, MARGIN), maxX)
  const y = Math.min(Math.max(props.position.y, MARGIN), maxY)
  return { left: `${x}px`, top: `${y}px` }
})

function onKeydown(event: KeyboardEvent): void {
  if (event.key === 'Escape') {
    event.preventDefault()
    emit('close')
  }
}

/** 点击菜单外的任意位置关闭（捕获阶段，避免被其它组件 stopPropagation）。 */
function onPointerDown(event: PointerEvent): void {
  const target = event.target
  if (target instanceof Node && menu.value?.contains(target)) return
  emit('close')
}

watch(
  () => props.open,
  async (open) => {
    if (open) {
      previousFocus = document.activeElement instanceof HTMLElement ? document.activeElement : null
      document.addEventListener('keydown', onKeydown)
      document.addEventListener('pointerdown', onPointerDown, true)
      await nextTick()
      menu.value?.querySelector<HTMLElement>('.message-action-menu__item')?.focus()
    } else {
      document.removeEventListener('keydown', onKeydown)
      document.removeEventListener('pointerdown', onPointerDown, true)
      previousFocus?.focus?.()
      previousFocus = null
    }
  },
)

onBeforeUnmount(() => {
  document.removeEventListener('keydown', onKeydown)
  document.removeEventListener('pointerdown', onPointerDown, true)
})
</script>

<template>
  <Teleport to="body">
    <div
      v-if="open && actions.length > 0"
      ref="menu"
      class="message-action-menu"
      role="menu"
      aria-label="消息操作"
      :style="menuStyle"
    >
      <button
        v-for="action in actions"
        :key="action.id"
        type="button"
        role="menuitem"
        class="message-action-menu__item"
        :class="{ 'message-action-menu__item--danger': action.danger }"
        @click="emit('select', action.id)"
      >
        {{ action.label }}
      </button>
    </div>
  </Teleport>
</template>

<style scoped>
.message-action-menu {
  position: fixed;
  z-index: 60;
  display: flex;
  flex-direction: column;
  min-width: 148px;
  padding: var(--ec-space-1);
  border: 1px solid var(--ec-border);
  border-radius: var(--ec-radius-panel);
  background: var(--ec-surface);
  box-shadow: var(--ec-shadow-lg);
}

.message-action-menu__item {
  display: block;
  width: 100%;
  min-height: 36px;
  padding: var(--ec-space-2) var(--ec-space-3);
  border: none;
  border-radius: var(--ec-radius-sm);
  background: none;
  color: var(--ec-text-primary);
  font: inherit;
  text-align: left;
  cursor: pointer;
}

.message-action-menu__item:hover {
  background: var(--ec-brand-050);
  color: var(--ec-brand-600);
}

.message-action-menu__item:focus-visible {
  outline: var(--ec-focus-width) solid var(--ec-focus-ring);
  outline-offset: 1px;
}

.message-action-menu__item--danger {
  color: var(--ec-danger);
}

.message-action-menu__item--danger:hover {
  background: var(--ec-surface-hover);
  color: var(--ec-danger);
}

/* ≤800：贴底操作表（类似系统 ActionSheet），按钮加高便于触摸。 */
@media (max-width: 800px) {
  .message-action-menu {
    right: 0;
    bottom: 0;
    left: 0;
    min-width: 0;
    padding: var(--ec-space-2) var(--ec-space-3)
      calc(var(--ec-space-2) + env(safe-area-inset-bottom, 0px));
    border-right: none;
    border-bottom: none;
    border-left: none;
    border-radius: var(--ec-radius-panel) var(--ec-radius-panel) 0 0;
  }

  .message-action-menu__item {
    min-height: 48px;
    font-size: var(--ec-font-size-body);
    text-align: center;
  }
}
</style>
