<script setup lang="ts">
import { computed, onBeforeUnmount, ref, watch } from 'vue'
import { createPanelHistoryGuard } from '../core/historyGuard'
import { MIN_SCALE, formatScale, nextScale, stepIndex } from '../core/imageGallery'

// 图片大图预览（F-08）：会话内多图切换、缩放（1x~4x）、键盘/滑动操作；Esc/点击遮罩关闭。
// blob URL 由父级（Chat.vue）用已鉴权下载生成并负责回收，本组件只消费。
const props = defineProps<{
  open: boolean
  /** 会话内图片列表（url 由父级异步加载，可能暂时为 null）。 */
  items: readonly { messageId: string; name: string; url: string | null }[]
  /** 当前索引（越界或空列表时显示空态）。 */
  index: number
  /** 当前图是否正在加载（区分「加载中」与「加载失败」）。 */
  loading?: boolean
}>()

const emit = defineEmits<{
  close: []
  'update:index': [index: number]
}>()

/** 预览打开时压入 history 哨兵：Android 返回/浏览器后退先关预览（UI-04 批次3）。 */
const panelGuard = createPanelHistoryGuard(() => emit('close'))

const scale = ref(MIN_SCALE)
const total = computed(() => props.items.length)
const currentIndex = computed(() => (props.index >= 0 && props.index < total.value ? props.index : -1))
const current = computed(() => (currentIndex.value >= 0 ? props.items[currentIndex.value] ?? null : null))
const scaleText = computed(() => formatScale(scale.value))
const hasMultiple = computed(() => total.value > 1)

function resetScale(): void {
  scale.value = MIN_SCALE
}

function goTo(delta: number): void {
  if (total.value <= 0) return
  const next = stepIndex(props.index, delta, total.value)
  if (next >= 0 && next !== props.index) emit('update:index', next)
}

function zoom(delta: number): void {
  scale.value = nextScale(scale.value, delta)
}

function onToggleZoom(): void {
  scale.value = scale.value > MIN_SCALE ? MIN_SCALE : 2
}

function onWheel(event: WheelEvent): void {
  if (!current.value?.url) return
  zoom(event.deltaY > 0 ? -1 : 1)
}

function onDoubleClick(): void {
  if (!current.value?.url) return
  onToggleZoom()
}

let touchStartX: number | null = null
let touchStartY: number | null = null

function onTouchStart(event: TouchEvent): void {
  const touch = event.touches[0]
  touchStartX = touch?.clientX ?? null
  touchStartY = touch?.clientY ?? null
}

function onTouchEnd(event: TouchEvent): void {
  const touch = event.changedTouches[0]
  if (!touch || touchStartX === null) return
  const deltaX = touch.clientX - touchStartX
  const deltaY = touchStartY === null ? 0 : touch.clientY - touchStartY
  touchStartX = null
  touchStartY = null
  if (Math.abs(deltaX) < 40 || Math.abs(deltaX) < Math.abs(deltaY)) return
  goTo(deltaX < 0 ? 1 : -1)
}

function onKeydown(event: KeyboardEvent): void {
  switch (event.key) {
    case 'Escape':
      emit('close')
      break
    case 'ArrowLeft':
      event.preventDefault()
      goTo(-1)
      break
    case 'ArrowRight':
      event.preventDefault()
      goTo(1)
      break
    case '+':
    case '=':
      zoom(1)
      break
    case '-':
    case '_':
      zoom(-1)
      break
    case '0':
      resetScale()
      break
    default:
      break
  }
}

/** 打开时锁定页面滚动，关闭恢复（保存进入前的内联值）。 */
let previousBodyOverflow = ''

watch(
  () => props.open,
  (open) => {
    if (typeof document === 'undefined') return
    if (open) {
      previousBodyOverflow = document.body.style.overflow
      document.body.style.overflow = 'hidden'
      document.addEventListener('keydown', onKeydown)
      panelGuard.push()
    } else {
      document.removeEventListener('keydown', onKeydown)
      document.body.style.overflow = previousBodyOverflow
      panelGuard.dispose()
    }
  },
  { immediate: true },
)

// 切换图片/重新打开时重置缩放，避免上一张的放大状态延续。
watch(
  () => props.index,
  () => resetScale(),
)
watch(
  () => props.open,
  (open) => {
    if (open) resetScale()
  },
)

onBeforeUnmount(() => {
  if (typeof document !== 'undefined') {
    document.removeEventListener('keydown', onKeydown)
    document.body.style.overflow = previousBodyOverflow
  }
  panelGuard.dispose()
})
</script>

<template>
  <Teleport to="body">
    <div
      v-if="open"
      class="image-preview"
      role="dialog"
      aria-modal="true"
      aria-label="图片预览"
      @click.self="emit('close')"
    >
      <div class="image-preview__stage" @dblclick="onDoubleClick">
        <img
          v-if="current?.url"
          :src="current.url"
          :alt="current.name"
          class="image-preview__image"
          :style="{ transform: `scale(${scale})` }"
          @wheel.prevent="onWheel"
          @touchstart.passive="onTouchStart"
          @touchend.passive="onTouchEnd"
        />
        <div v-else class="image-preview__placeholder" role="status">
          {{ loading ? '图片加载中…' : '图片加载失败' }}
        </div>

        <div v-if="hasMultiple" class="image-preview__nav">
          <button
            type="button"
            class="image-preview__nav-button"
            aria-label="上一张"
            @click="goTo(-1)"
          >
            ‹
          </button>
          <span class="image-preview__counter">{{ currentIndex + 1 }} / {{ total }}</span>
          <button
            type="button"
            class="image-preview__nav-button"
            aria-label="下一张"
            @click="goTo(1)"
          >
            ›
          </button>
        </div>
      </div>

      <div class="image-preview__toolbar">
        <button
          type="button"
          class="image-preview__button"
          aria-label="缩小"
          :disabled="!current?.url || scale <= MIN_SCALE"
          @click="zoom(-1)"
        >
          −
        </button>
        <button
          type="button"
          class="image-preview__button image-preview__button--scale"
          aria-label="重置缩放"
          :disabled="!current?.url"
          @click="resetScale"
        >
          {{ scaleText }}
        </button>
        <button
          type="button"
          class="image-preview__button"
          aria-label="放大"
          :disabled="!current?.url || scale >= 4"
          @click="zoom(1)"
        >
          +
        </button>
        <a
          v-if="current?.url"
          class="image-preview__button"
          :href="current.url"
          :download="current.name"
        >
          下载
        </a>
        <button type="button" class="image-preview__button" @click="emit('close')">关闭</button>
      </div>
    </div>
  </Teleport>
</template>

<style scoped>
.image-preview {
  position: fixed;
  inset: 0;
  z-index: 1000;
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  gap: var(--ec-space-3);
  padding: var(--ec-space-4);
  background: var(--ec-overlay);
}

.image-preview__stage {
  position: relative;
  display: flex;
  align-items: center;
  justify-content: center;
  width: min(92vw, 1200px);
  height: min(82vh, 900px);
  overflow: hidden;
  border-radius: var(--ec-radius-md);
}

.image-preview__image {
  max-width: 100%;
  max-height: 100%;
  object-fit: contain;
  transform-origin: center;
  transition: transform var(--ec-transition-fast) var(--ec-ease);
  user-select: none;
  touch-action: pan-y;
}

.image-preview__placeholder {
  color: var(--ec-text-inverse);
  font-size: var(--ec-font-size-secondary);
}

.image-preview__nav {
  position: absolute;
  right: var(--ec-space-3);
  bottom: var(--ec-space-3);
  display: flex;
  gap: var(--ec-space-2);
  align-items: center;
  padding: var(--ec-space-1) var(--ec-space-2);
  border-radius: var(--ec-radius-full);
  background: var(--ec-overlay-hover);
}

.image-preview__counter {
  color: var(--ec-text-inverse);
  font-size: var(--ec-font-size-small);
  font-variant-numeric: tabular-nums;
}

.image-preview__nav-button {
  width: 36px;
  min-height: 36px;
  border: none;
  border-radius: var(--ec-radius-full);
  background: transparent;
  color: var(--ec-text-inverse);
  font-size: 20px;
  line-height: 1;
  cursor: pointer;
}

.image-preview__toolbar {
  display: flex;
  gap: var(--ec-space-2);
  align-items: center;
  justify-content: center;
  flex-wrap: wrap;
}

.image-preview__button {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  min-width: 44px;
  min-height: 36px;
  padding: var(--ec-space-1) var(--ec-space-3);
  border: 1px solid var(--ec-overlay-border);
  border-radius: var(--ec-radius-full);
  background: transparent;
  color: var(--ec-text-inverse);
  text-decoration: none;
  font-size: var(--ec-font-size-secondary);
  cursor: pointer;
}

.image-preview__button--scale {
  min-width: 64px;
  font-variant-numeric: tabular-nums;
}

.image-preview__button:disabled {
  opacity: 0.45;
  cursor: not-allowed;
}

/* 悬停效果仅在鼠标精确指针设备生效，触屏不产生粘滞 hover。 */
@media (hover: hover) and (pointer: fine) {
  .image-preview__button:not(:disabled):hover,
  .image-preview__nav-button:hover {
    background: var(--ec-overlay-hover);
  }
}

.image-preview__button:focus-visible,
.image-preview__nav-button:focus-visible {
  outline: var(--ec-focus-width) solid var(--ec-focus-ring);
  outline-offset: var(--ec-focus-offset);
}

@media (max-width: 800px) {
  .image-preview {
    padding: var(--ec-space-2);
    padding-bottom: calc(var(--ec-space-2) + env(safe-area-inset-bottom, 0px));
  }

  .image-preview__stage {
    width: 100%;
    height: 72vh;
  }

  .image-preview__nav {
    right: 50%;
    transform: translateX(50%);
  }

  .image-preview__toolbar {
    gap: var(--ec-space-1);
  }
}

@media (prefers-reduced-motion: reduce) {
  .image-preview__image {
    transition: none;
  }
}
</style>
