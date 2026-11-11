<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, ref, watch } from 'vue'
import AppIcon from './AppIcon.vue'
import Avatar from './Avatar.vue'
import PresenceDot from './PresenceDot.vue'
import { presenceLabelOf, type PresenceState } from '../core/presence'
import { formatSessionTime } from '../core/sessionFormat'
import type { ConversationMember } from '../types/frames'

// F-41：群成员资料浮窗。展示昵称、在线状态、群内角色、邮箱与加入时间，
// 并提供「发送消息」「添加好友」两个入口；是否已是好友/已发申请由父组件按好友关系计算。
// 纯展示组件：不直接调用接口，动作通过 emits 交给 GroupMembersPanel 处理。
// 桌面按被点击成员行的位置开在侧栏左侧并做视口收敛；≤800 改为居中浮层（带遮罩）。
const CARD_WIDTH = 288
const VIEWPORT_MARGIN = 12
/** 桌面纵向收敛的预估高度：仅用于避免浮窗底部越界，真实高度由内容决定。 */
const ESTIMATED_HEIGHT = 320

const props = withDefaults(
  defineProps<{
    open: boolean
    member: ConversationMember | null
    /** 展示名：好友备注优先（由父组件计算）。 */
    displayName: string
    presence?: PresenceState
    /** 与当前用户的关系：friend 已是好友；outgoing/incoming 待处理申请；none 陌生人。 */
    relation?: 'friend' | 'outgoing' | 'incoming' | 'none'
    /** 是否为当前登录用户本人（本人不显示操作入口）。 */
    isSelf?: boolean
    /** 触发行的视口矩形：桌面用于把浮窗开在成员列表左侧。 */
    anchor?: { left: number; right: number; top: number } | null
    /** 正在创建/打开单聊。 */
    busy?: boolean
    /** 正在发送好友申请。 */
    requesting?: boolean
    /** 操作失败文案（父组件用 friendlyError 转换后传入）。 */
    error?: string
  }>(),
  {
    presence: undefined,
    relation: 'none',
    isSelf: false,
    anchor: null,
    busy: false,
    requesting: false,
    error: '',
  },
)

const emit = defineEmits<{
  close: []
  'start-chat': []
  'send-request': []
}>()

const card = ref<HTMLElement | null>(null)
let previousFocus: HTMLElement | null = null

const presenceText = computed(() => presenceLabelOf(props.presence))
const roleLabel = computed(() => (props.member?.role === 'owner' ? '群主' : '成员'))
const emailText = computed(() => props.member?.email?.trim() || '—')
const joinedText = computed(() => {
  const formatted = formatSessionTime(props.member?.joinedAt ?? 0)
  return formatted === '—' ? '—' : formatted.slice(0, 10)
})

const relationHint = computed(() => {
  switch (props.relation) {
    case 'friend':
      return '已是好友'
    case 'incoming':
      return '对方已向你发送好友申请，可在「通讯录 → 申请」中处理'
    default:
      return ''
  }
})

const cardStyle = computed(() => {
  if (!props.open) return undefined
  // ≤800 由 CSS 居中展示，不使用锚点定位。
  if (window.matchMedia('(max-width: 800px)').matches) return undefined
  const viewportWidth = window.innerWidth
  const viewportHeight = window.innerHeight
  const anchor = props.anchor
  const preferredLeft = anchor
    ? anchor.left - CARD_WIDTH - VIEWPORT_MARGIN
    : (viewportWidth - CARD_WIDTH) / 2
  const left = Math.min(
    Math.max(preferredLeft, VIEWPORT_MARGIN),
    Math.max(VIEWPORT_MARGIN, viewportWidth - CARD_WIDTH - VIEWPORT_MARGIN),
  )
  const preferredTop = anchor ? anchor.top : (viewportHeight - ESTIMATED_HEIGHT) / 2
  const top = Math.min(
    Math.max(preferredTop, VIEWPORT_MARGIN),
    Math.max(VIEWPORT_MARGIN, viewportHeight - ESTIMATED_HEIGHT - VIEWPORT_MARGIN),
  )
  return {
    left: `${left}px`,
    top: `${top}px`,
    maxHeight: `${Math.max(200, viewportHeight - top - VIEWPORT_MARGIN)}px`,
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
      card.value?.querySelector<HTMLElement>('.member-card__primary, .member-card__close')?.focus()
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
    <div v-if="open && member" class="member-card__scrim" aria-hidden="true" @click="emit('close')" />
    <section
      v-if="open && member"
      ref="card"
      class="member-card"
      role="dialog"
      aria-modal="false"
      aria-label="成员资料"
      :style="cardStyle"
    >
      <header class="member-card__head">
        <Avatar
          :display-name="displayName"
          :avatar-file-id="member.avatarFileId"
          :size="56"
        />
        <div class="member-card__ident">
          <p class="member-card__name">
            {{ displayName }}<em v-if="isSelf" class="member-card__me">（我）</em>
          </p>
          <p class="member-card__presence">
            <PresenceDot :online="presence" />
            <span>{{ presenceText }}</span>
            <span class="member-card__role">{{ roleLabel }}</span>
          </p>
        </div>
        <button type="button" class="member-card__close" aria-label="关闭成员资料" @click="emit('close')">
          <AppIcon name="close" :size="16" />
        </button>
      </header>

      <dl class="member-card__meta">
        <div class="member-card__row">
          <dt>邮箱</dt>
          <dd>{{ emailText }}</dd>
        </div>
        <div class="member-card__row">
          <dt>加入时间</dt>
          <dd>{{ joinedText }}</dd>
        </div>
      </dl>

      <p v-if="relationHint" class="member-card__hint">{{ relationHint }}</p>
      <p v-if="error" class="member-card__error" role="alert">{{ error }}</p>

      <div v-if="isSelf" class="member-card__hint">这是你自己</div>
      <div v-else class="member-card__actions">
        <button
          type="button"
          class="member-card__primary"
          :disabled="busy || requesting"
          @click="emit('start-chat')"
        >
          {{ busy ? '正在打开…' : '发送消息' }}
        </button>
        <button
          v-if="relation === 'none'"
          type="button"
          class="member-card__secondary"
          :disabled="busy || requesting"
          @click="emit('send-request')"
        >
          {{ requesting ? '发送中…' : '添加好友' }}
        </button>
        <button
          v-else-if="relation === 'outgoing'"
          type="button"
          class="member-card__secondary"
          disabled
        >
          已发送申请
        </button>
        <button
          v-else-if="relation === 'incoming'"
          type="button"
          class="member-card__secondary"
          disabled
        >
          对方已申请
        </button>
      </div>
    </section>
  </Teleport>
</template>

<style scoped>
.member-card__scrim {
  display: none;
}

.member-card {
  position: fixed;
  z-index: 60;
  display: flex;
  flex-direction: column;
  gap: var(--ec-space-2);
  width: 288px;
  padding: var(--ec-space-4);
  overflow-y: auto;
  border: 1px solid var(--ec-border);
  border-radius: var(--ec-radius-panel);
  background: var(--ec-surface);
  box-shadow: var(--ec-shadow-lg);
}

.member-card__head {
  display: flex;
  gap: var(--ec-space-3);
  align-items: flex-start;
}

.member-card__ident {
  display: flex;
  flex: 1;
  flex-direction: column;
  gap: var(--ec-space-1);
  min-width: 0;
}

.member-card__name {
  margin: 0;
  overflow: hidden;
  color: var(--ec-text-primary);
  font-size: var(--ec-font-size-body);
  font-weight: var(--ec-font-weight-semibold);
  text-overflow: ellipsis;
  white-space: nowrap;
}

.member-card__me {
  color: var(--ec-text-secondary);
  font-size: var(--ec-font-size-small);
  font-style: normal;
  font-weight: var(--ec-font-weight-regular);
}

.member-card__presence {
  display: flex;
  gap: var(--ec-space-1);
  align-items: center;
  margin: 0;
  color: var(--ec-text-secondary);
  font-size: var(--ec-font-size-small);
}

.member-card__role {
  margin-left: var(--ec-space-1);
  padding: 1px var(--ec-space-2);
  border-radius: var(--ec-radius-full);
  background: var(--ec-surface-hover);
  color: var(--ec-text-secondary);
}

.member-card__close {
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

.member-card__meta {
  display: flex;
  flex-direction: column;
  gap: var(--ec-space-1);
  margin: var(--ec-space-1) 0 0;
  padding-top: var(--ec-space-2);
  border-top: 1px solid var(--ec-border-soft);
}

.member-card__row {
  display: flex;
  gap: var(--ec-space-2);
  align-items: baseline;
  font-size: var(--ec-font-size-secondary);
}

.member-card__row dt {
  flex: none;
  width: 56px;
  color: var(--ec-text-secondary);
}

.member-card__row dd {
  margin: 0;
  overflow-wrap: anywhere;
  color: var(--ec-text-primary);
}

.member-card__hint {
  margin: 0;
  color: var(--ec-text-secondary);
  font-size: var(--ec-font-size-small);
  line-height: 1.5;
}

.member-card__error {
  margin: 0;
  color: var(--ec-danger);
  font-size: var(--ec-font-size-small);
  line-height: 1.5;
}

.member-card__actions {
  display: flex;
  gap: var(--ec-space-2);
  margin-top: var(--ec-space-2);
}

.member-card__primary,
.member-card__secondary {
  flex: 1;
  min-height: 34px;
  padding: 0 var(--ec-space-3);
  border-radius: var(--ec-radius-md);
  font-size: var(--ec-font-size-secondary);
  cursor: pointer;
}

.member-card__primary {
  border: none;
  background: var(--ec-brand-600);
  color: var(--ec-text-inverse);
}

.member-card__secondary {
  border: 1px solid var(--ec-border);
  background: var(--ec-surface);
  color: var(--ec-brand-600);
}

.member-card__primary:disabled,
.member-card__secondary:disabled {
  opacity: 0.6;
  cursor: not-allowed;
}

.member-card__primary:focus-visible,
.member-card__secondary:focus-visible,
.member-card__close:focus-visible {
  outline: var(--ec-focus-width) solid var(--ec-focus-ring);
  outline-offset: var(--ec-focus-offset);
}

/* 悬停效果仅在鼠标精确指针设备生效，触屏不产生粘滞 hover。 */
@media (hover: hover) and (pointer: fine) {
  .member-card__primary:hover:not(:disabled) {
    background: var(--ec-brand-700);
  }

  .member-card__secondary:hover:not(:disabled),
  .member-card__close:hover {
    background: var(--ec-surface-hover);
  }
}

/* ≤800：居中浮层 + 遮罩，按钮加高便于触摸。 */
@media (max-width: 800px) {
  .member-card__scrim {
    position: fixed;
    inset: 0;
    z-index: 59;
    display: block;
    background: var(--ec-scrim);
  }

  .member-card {
    top: 50%;
    left: 50%;
    width: min(340px, calc(100vw - var(--ec-space-8)));
    max-height: calc(100dvh - var(--ec-space-8));
    transform: translate(-50%, -50%);
  }

  .member-card__primary,
  .member-card__secondary {
    min-height: 44px;
    font-size: var(--ec-font-size-body);
  }

  .member-card__close {
    width: 44px;
    height: 44px;
  }
}
</style>
