// 自绘确认弹窗（UI-03 批次1，计划 §5.11 / §16.3）：替代 window.confirm，
// 支持危险操作（tone="danger"）、忙碌态、Esc/遮罩取消、打开聚焦与关闭后焦点归还。
// 只负责展示与派发 confirm/cancel，不直接执行业务请求；调用方在 busy 期间保持打开。
<script setup lang="ts">
import { nextTick, onBeforeUnmount, ref, watch } from 'vue'

const props = withDefaults(
  defineProps<{
    open: boolean
    title: string
    /** 可选正文；更复杂的内容用默认插槽。 */
    message?: string
    confirmText?: string
    cancelText?: string
    tone?: 'default' | 'danger'
    /** 业务提交中：禁用两个按钮且不响应 Esc/遮罩关闭。 */
    busy?: boolean
    /** 宽版（560px），用于表单类内容（如新建群聊）。 */
    wide?: boolean
  }>(),
  {
    message: '',
    confirmText: '确定',
    cancelText: '取消',
    tone: 'default',
    busy: false,
    wide: false,
  },
)

const emit = defineEmits<{ confirm: []; cancel: [] }>()

const panel = ref<HTMLElement | null>(null)
let previousFocus: HTMLElement | null = null

function close(): void {
  if (props.busy) return
  emit('cancel')
}

function onKeydown(event: KeyboardEvent): void {
  if (event.key === 'Escape') {
    event.preventDefault()
    close()
  }
}

watch(
  () => props.open,
  async (open) => {
    if (open) {
      previousFocus = document.activeElement instanceof HTMLElement ? document.activeElement : null
      document.addEventListener('keydown', onKeydown)
      await nextTick()
      panel.value?.querySelector<HTMLElement>('.ec-dialog__actions button:not([disabled])')?.focus()
    } else {
      document.removeEventListener('keydown', onKeydown)
      previousFocus?.focus?.()
      previousFocus = null
    }
  },
)

onBeforeUnmount(() => {
  document.removeEventListener('keydown', onKeydown)
})

function onConfirm(): void {
  if (props.busy) return
  emit('confirm')
}
</script>

<template>
  <Teleport to="body">
    <div v-if="open" class="ec-dialog" role="presentation" @mousedown.self="close">
      <section
        ref="panel"
        class="ec-dialog__panel"
        :class="{ 'ec-dialog__panel--wide': wide }"
        role="dialog"
        aria-modal="true"
        :aria-label="title"
      >
        <h2 class="ec-dialog__title">{{ title }}</h2>
        <p v-if="message" class="ec-dialog__message">{{ message }}</p>
        <div v-if="$slots.default" class="ec-dialog__body">
          <slot />
        </div>
        <footer class="ec-dialog__actions">
          <button type="button" class="ec-dialog__btn" :disabled="busy" @click="emit('cancel')">
            {{ cancelText }}
          </button>
          <button
            type="button"
            class="ec-dialog__btn ec-dialog__btn--primary"
            :class="{ 'ec-dialog__btn--danger': tone === 'danger' }"
            :disabled="busy"
            @click="onConfirm"
          >
            {{ confirmText }}
          </button>
        </footer>
      </section>
    </div>
  </Teleport>
</template>

<style scoped>
.ec-dialog {
  position: fixed;
  inset: 0;
  z-index: 60;
  display: flex;
  align-items: center;
  justify-content: center;
  padding: var(--ec-space-4);
  background: var(--ec-scrim);
}

.ec-dialog__panel {
  width: min(420px, calc(100vw - var(--ec-space-8)));
  max-height: calc(100vh - var(--ec-space-8));
  display: flex;
  flex-direction: column;
  padding: var(--ec-space-5);
  background: var(--ec-surface);
  border-radius: var(--ec-radius-panel);
  box-shadow: var(--ec-shadow-lg);
}

.ec-dialog__panel--wide {
  width: min(560px, calc(100vw - var(--ec-space-8)));
}

.ec-dialog__title {
  margin: 0;
  color: var(--ec-text-primary);
  font-size: var(--ec-font-size-body);
  font-weight: 600;
  line-height: 1.4;
}

.ec-dialog__message {
  margin: var(--ec-space-2) 0 0;
  color: var(--ec-text-secondary);
  font-size: var(--ec-font-size-body);
  line-height: 1.6;
}

.ec-dialog__body {
  margin-top: var(--ec-space-3);
  overflow-y: auto;
}

.ec-dialog__actions {
  display: flex;
  justify-content: flex-end;
  gap: var(--ec-space-2);
  margin-top: var(--ec-space-5);
}

.ec-dialog__btn {
  min-width: 76px;
  height: 36px;
  padding: 0 var(--ec-space-4);
  border: 1px solid var(--ec-border);
  border-radius: var(--ec-radius-md);
  background: var(--ec-surface);
  color: var(--ec-text-primary);
  font-size: var(--ec-font-size-body);
  cursor: pointer;
  transition: background-color var(--ec-transition-fast) var(--ec-ease);
}

.ec-dialog__btn:hover:not(:disabled) {
  background: var(--ec-surface-hover);
}

.ec-dialog__btn:focus-visible {
  outline: var(--ec-focus-width) solid var(--ec-focus-ring);
  outline-offset: var(--ec-focus-offset);
}

.ec-dialog__btn:disabled {
  opacity: 0.6;
  cursor: not-allowed;
}

.ec-dialog__btn--primary {
  border-color: var(--ec-brand-600);
  background: var(--ec-brand-600);
  color: var(--ec-text-inverse);
}

.ec-dialog__btn--primary:hover:not(:disabled) {
  background: var(--ec-brand-700);
}

.ec-dialog__btn--danger {
  border-color: var(--ec-danger);
  background: var(--ec-danger);
}
</style>
