<script setup lang="ts">
import { nextTick, onBeforeUnmount, ref, watch } from 'vue'

// 右侧抽屉公共壳（UI-01，计划 §4 / §5）：本批不接入任何页面，首个消费者是 UI-02 的 EmojiPanel。
// 语义：role=dialog + aria-modal；Esc / 点击遮罩关闭；打开时焦点进入抽屉，关闭后焦点返回触发元素。
const props = withDefaults(defineProps<{ open: boolean; title?: string }>(), { title: '' })
const emit = defineEmits<{ close: [] }>()

const panel = ref<HTMLElement | null>(null)
let previousFocus: HTMLElement | null = null

function onKeydown(event: KeyboardEvent): void {
  if (event.key !== 'Escape') return
  event.stopPropagation()
  emit('close')
}

watch(
  () => props.open,
  async (open) => {
    if (open) {
      previousFocus = document.activeElement instanceof HTMLElement ? document.activeElement : null
      document.addEventListener('keydown', onKeydown)
      await nextTick()
      panel.value?.focus()
      return
    }

    document.removeEventListener('keydown', onKeydown)
    previousFocus?.focus()
    previousFocus = null
  },
  { immediate: true },
)

onBeforeUnmount(() => {
  document.removeEventListener('keydown', onKeydown)
})
</script>

<template>
  <Teleport to="body">
    <div v-if="open" class="ec-drawer-mask" @click.self="emit('close')">
      <aside
        ref="panel"
        class="ec-drawer"
        role="dialog"
        aria-modal="true"
        :aria-label="title || '面板'"
        tabindex="-1"
      >
        <header v-if="title" class="ec-drawer__header">
          <h2 class="ec-drawer__title">{{ title }}</h2>
          <button class="ec-drawer__close" type="button" aria-label="关闭" @click="emit('close')">
            ×
          </button>
        </header>
        <div class="ec-drawer__body">
          <slot />
        </div>
      </aside>
    </div>
  </Teleport>
</template>

<style scoped>
.ec-drawer-mask {
  position: fixed;
  inset: 0;
  z-index: 200;
  display: flex;
  justify-content: flex-end;
  background: var(--ec-scrim);
}

.ec-drawer {
  display: flex;
  flex-direction: column;
  width: var(--ec-drawer-width);
  max-width: 300px;
  height: 100%;
  outline: none;
  background: var(--ec-surface);
  box-shadow: var(--ec-shadow-lg);
  animation: ec-drawer-in var(--ec-transition-base) var(--ec-ease);
}

.ec-drawer__header {
  display: flex;
  gap: var(--ec-space-2);
  align-items: center;
  justify-content: space-between;
  padding: var(--ec-space-4);
  border-bottom: 1px solid var(--ec-border);
}

.ec-drawer__title {
  margin: 0;
  color: var(--ec-text-primary);
  font-size: 15px;
  font-weight: var(--ec-font-weight-semibold);
}

.ec-drawer__close {
  display: grid;
  flex: none;
  width: 28px;
  height: 28px;
  place-items: center;
  border: none;
  border-radius: var(--ec-radius-sm);
  background: transparent;
  color: var(--ec-text-secondary);
  font-size: 18px;
  line-height: 1;
  cursor: pointer;
}

.ec-drawer__close:hover {
  background: var(--ec-surface-hover);
  color: var(--ec-text-primary);
}

.ec-drawer__body {
  flex: 1;
  min-height: 0;
  padding: var(--ec-space-4);
  overflow-y: auto;
}

@keyframes ec-drawer-in {
  from {
    transform: translateX(16px);
    opacity: 0;
  }

  to {
    transform: translateX(0);
    opacity: 1;
  }
}

@media (prefers-reduced-motion: reduce) {
  .ec-drawer {
    animation: none;
  }
}
</style>
