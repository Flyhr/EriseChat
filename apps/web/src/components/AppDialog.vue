// 确认弹窗（UI-D P4）：外壳改用 Naive UI NModal，保留原有 props / emit / 默认插槽与焦点归还逻辑。
// 面板视觉仍由 --ec-* token 控制；busy 期间禁用 Esc/遮罩关闭的语义不变。
// 只负责展示与派发 confirm/cancel，不直接执行业务请求；调用方在 busy 期间保持打开。
<script setup lang="ts">
import { nextTick, onBeforeUnmount, ref, watch } from 'vue'
import { NModal } from 'naive-ui'

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

/** NModal 的 show 变为 false（含遮罩/内部关闭）时统一派发 cancel，busy 期间由 close() 拦截。 */
function onShowUpdate(show: boolean): void {
  if (!show) close()
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
  <NModal
    :show="open"
    :mask-closable="!busy"
    :close-on-esc="false"
    :auto-focus="false"
    @update:show="onShowUpdate"
    @mask-click="close"
  >
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
  </NModal>
</template>

<style scoped>
.ec-dialog__panel {
  width: min(420px, calc(100vw - var(--ec-space-8)));
  max-height: calc(100dvh - var(--ec-space-8));
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

/* 悬停效果仅在鼠标精确指针设备生效，触屏不产生粘滞 hover。 */
@media (hover: hover) and (pointer: fine) {
.ec-dialog__btn:hover:not(:disabled) {
  background: var(--ec-surface-hover);
}
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

/* 悬停效果仅在鼠标精确指针设备生效，触屏不产生粘滞 hover。 */
@media (hover: hover) and (pointer: fine) {
.ec-dialog__btn--primary:hover:not(:disabled) {
  background: var(--ec-brand-700);
}
}

.ec-dialog__btn--danger {
  border-color: var(--ec-danger);
  background: var(--ec-danger);
}

/* UI-04 批次4：触屏按钮可点区域 ≥44px。 */
@media (max-width: 800px) {
  .ec-dialog__btn {
    min-height: 44px;
  }
}
</style>
