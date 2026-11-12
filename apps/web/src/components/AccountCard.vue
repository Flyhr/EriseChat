<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, ref, watch } from 'vue'
import AppIcon from './AppIcon.vue'
import Avatar from './Avatar.vue'

// 账号信息浮窗：左侧导航顶部的头像/昵称点击后弹出，展示当前登录账号的头像、昵称与邮箱。
// 纯展示组件（不调接口）：点击浮窗外部、按 Esc 或点关闭按钮收起，关闭后焦点回到触发按钮。
// 桌面按触发按钮位置开在导航右侧并做视口收敛；导航 ≤800 隐藏，故不需要移动端居中形态。
const CARD_WIDTH = 260
const VIEWPORT_MARGIN = 12
const ESTIMATED_HEIGHT = 120

const props = withDefaults(
  defineProps<{
    open: boolean
    displayName: string
    email?: string
    avatarFileId?: string | null
    /** 触发按钮的视口矩形：浮窗据此开在导航右侧。 */
    anchor?: { left: number; right: number; top: number } | null
    /** 触发按钮元素：点击它不视为「外部点击」，交给父组件切换开关。 */
    trigger?: HTMLElement | null
  }>(),
  {
    email: '',
    avatarFileId: null,
    anchor: null,
    trigger: null,
  },
)

const emit = defineEmits<{ close: [] }>()

const card = ref<HTMLElement | null>(null)
let previousFocus: HTMLElement | null = null
/** 视口尺寸变化时自增，驱动定位重算（导航位置固定，只需重新收敛边界）。 */
const viewportVersion = ref(0)

const emailText = computed(() => props.email?.trim() || '—')

const cardStyle = computed(() => {
  if (!props.open) return undefined
  void viewportVersion.value
  const viewportWidth = window.innerWidth
  const viewportHeight = window.innerHeight
  const anchor = props.anchor
  const preferredLeft = anchor ? anchor.right + 8 : VIEWPORT_MARGIN
  const left = Math.min(
    Math.max(preferredLeft, VIEWPORT_MARGIN),
    Math.max(VIEWPORT_MARGIN, viewportWidth - CARD_WIDTH - VIEWPORT_MARGIN),
  )
  const preferredTop = anchor ? anchor.top : VIEWPORT_MARGIN
  const top = Math.min(
    Math.max(preferredTop, VIEWPORT_MARGIN),
    Math.max(VIEWPORT_MARGIN, viewportHeight - ESTIMATED_HEIGHT - VIEWPORT_MARGIN),
  )
  return {
    left: `${left}px`,
    top: `${top}px`,
    maxHeight: `${Math.max(120, viewportHeight - top - VIEWPORT_MARGIN)}px`,
  }
})

function onKeydown(event: KeyboardEvent): void {
  if (event.key !== 'Escape') return
  event.preventDefault()
  emit('close')
}

/** 点击浮窗外的任意位置关闭（捕获阶段，避免被其它组件 stopPropagation）。 */
function onPointerDown(event: PointerEvent): void {
  const target = event.target
  if (target instanceof Node && card.value?.contains(target)) return
  // 点触发按钮本身不在这里关闭：由父组件的 click 切换开关，否则会出现「关了又立刻打开」。
  if (target instanceof Node && props.trigger?.contains(target)) return
  emit('close')
}

function onResize(): void {
  viewportVersion.value += 1
}

watch(
  () => props.open,
  async (open) => {
    if (open) {
      previousFocus = document.activeElement instanceof HTMLElement ? document.activeElement : null
      document.addEventListener('keydown', onKeydown)
      document.addEventListener('pointerdown', onPointerDown, true)
      window.addEventListener('resize', onResize)
      await nextTick()
      card.value?.querySelector<HTMLElement>('.account-card__close')?.focus()
    } else {
      document.removeEventListener('keydown', onKeydown)
      document.removeEventListener('pointerdown', onPointerDown, true)
      window.removeEventListener('resize', onResize)
      previousFocus?.focus?.()
      previousFocus = null
    }
  },
)

onBeforeUnmount(() => {
  document.removeEventListener('keydown', onKeydown)
  document.removeEventListener('pointerdown', onPointerDown, true)
  window.removeEventListener('resize', onResize)
})
</script>

<template>
  <Teleport to="body">
    <section
      v-if="open"
      ref="card"
      class="account-card"
      role="dialog"
      aria-modal="false"
      aria-label="账号信息"
      :style="cardStyle"
    >
      <header class="account-card__head">
        <Avatar :display-name="displayName" :avatar-file-id="avatarFileId ?? undefined" :size="48" />
        <div class="account-card__ident">
          <p class="account-card__name">{{ displayName }}</p>
          <p class="account-card__email">{{ emailText }}</p>
        </div>
        <button
          type="button"
          class="account-card__close"
          aria-label="关闭账号信息"
          @click="emit('close')"
        >
          <AppIcon name="close" :size="16" />
        </button>
      </header>
    </section>
  </Teleport>
</template>

<style scoped>
.account-card {
  position: fixed;
  z-index: 60;
  width: 260px;
  padding: var(--ec-space-3);
  overflow-y: auto;
  border: 1px solid var(--ec-border);
  border-radius: var(--ec-radius-panel);
  background: var(--ec-surface);
  box-shadow: var(--ec-shadow-lg);
}

.account-card__head {
  display: flex;
  gap: var(--ec-space-3);
  align-items: flex-start;
}

.account-card__ident {
  display: flex;
  flex: 1;
  flex-direction: column;
  gap: 2px;
  min-width: 0;
  padding-top: 2px;
}

.account-card__name {
  margin: 0;
  overflow: hidden;
  color: var(--ec-text-primary);
  font-size: var(--ec-font-size-body);
  font-weight: var(--ec-font-weight-semibold);
  text-overflow: ellipsis;
  white-space: nowrap;
}

.account-card__email {
  margin: 0;
  overflow: hidden;
  color: var(--ec-text-secondary);
  font-size: var(--ec-font-size-small);
  text-overflow: ellipsis;
  white-space: nowrap;
}

.account-card__close {
  display: inline-flex;
  flex: none;
  align-items: center;
  justify-content: center;
  width: 28px;
  height: 28px;
  padding: 0;
  border: none;
  border-radius: var(--ec-radius-sm);
  background: transparent;
  color: var(--ec-text-secondary);
  cursor: pointer;
}

.account-card__close:focus-visible {
  outline: var(--ec-focus-width) solid var(--ec-focus-ring);
  outline-offset: var(--ec-focus-offset);
}

/* 悬停效果仅在鼠标精确指针设备生效，触屏不产生粘滞 hover。 */
@media (hover: hover) and (pointer: fine) {
  .account-card__close:hover {
    background: var(--ec-surface-hover);
    color: var(--ec-text-primary);
  }
}
</style>
