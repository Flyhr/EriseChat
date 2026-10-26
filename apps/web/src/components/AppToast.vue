// 轻提示渲染层（UI-03 批次1）：在 App.vue 挂载一次，读取 core/toast.ts 的全局队列。
// 仅展示瞬时反馈，不含操作按钮；role=status + aria-live=polite，避免打断读屏。
<script setup lang="ts">
import { toasts, dismissToast } from '../core/toast'
</script>

<template>
  <Teleport to="body">
    <div class="ec-toast" role="status" aria-live="polite">
      <TransitionGroup name="ec-toast">
        <button
          v-for="toast in toasts"
          :key="toast.id"
          type="button"
          class="ec-toast__item"
          :class="`ec-toast__item--${toast.kind}`"
          @click="dismissToast(toast.id)"
        >
          {{ toast.message }}
        </button>
      </TransitionGroup>
    </div>
  </Teleport>
</template>

<style scoped>
.ec-toast {
  position: fixed;
  /* UI-04：刘海屏/状态栏安全区，避免提示被系统栏遮挡。 */
  top: calc(var(--ec-space-5) + env(safe-area-inset-top, 0px));
  left: 50%;
  z-index: 80;
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: var(--ec-space-2);
  transform: translateX(-50%);
  pointer-events: none;
}

.ec-toast__item {
  max-width: min(480px, calc(100vw - var(--ec-space-8)));
  padding: var(--ec-space-2) var(--ec-space-4);
  border: 1px solid var(--ec-border);
  border-radius: var(--ec-radius-full);
  background: var(--ec-surface);
  box-shadow: var(--ec-shadow-md);
  color: var(--ec-text-primary);
  font-size: var(--ec-font-size-secondary);
  line-height: 1.5;
  cursor: pointer;
  pointer-events: auto;
}

.ec-toast__item--success {
  border-color: var(--ec-success);
  color: var(--ec-success);
}

.ec-toast__item--error {
  border-color: var(--ec-danger);
  color: var(--ec-danger);
}

.ec-toast-enter-active,
.ec-toast-leave-active {
  transition:
    opacity var(--ec-transition-fast) var(--ec-ease),
    transform var(--ec-transition-fast) var(--ec-ease);
}

.ec-toast-enter-from,
.ec-toast-leave-to {
  opacity: 0;
  transform: translateY(-6px);
}
</style>
