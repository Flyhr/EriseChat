<script setup lang="ts">
import { onBeforeUnmount, watch } from 'vue'

// 图片大图预览（M3-07；UI-03 批次4 token 化）：全屏遮罩，点击空白/Esc 关闭；
// 下载复用已鉴权加载的 blob URL（<a download>），行为与旧版一致。
const props = defineProps<{
  open: boolean
  /** 已鉴权下载得到的 blob URL（由 MessageAttachment 打开时传入）。 */
  url: string
  name: string
}>()

const emit = defineEmits<{
  close: []
}>()

function onKeydown(event: KeyboardEvent): void {
  if (event.key === 'Escape') emit('close')
}

watch(
  () => props.open,
  (open) => {
    if (typeof document === 'undefined') return
    if (open) {
      document.addEventListener('keydown', onKeydown)
    } else {
      document.removeEventListener('keydown', onKeydown)
    }
  },
  { immediate: true },
)

onBeforeUnmount(() => {
  if (typeof document !== 'undefined') document.removeEventListener('keydown', onKeydown)
})
</script>

<template>
  <Teleport to="body">
    <div v-if="open" class="image-preview" role="dialog" aria-modal="true" @click.self="emit('close')">
      <div class="image-preview__panel">
        <img :src="url" :alt="name" class="image-preview__image" />
        <div class="image-preview__actions">
          <a class="image-preview__download" :href="url" :download="name">下载</a>
          <button class="image-preview__close" type="button" @click="emit('close')">关闭</button>
        </div>
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
  align-items: center;
  justify-content: center;
  padding: var(--ec-space-4);
  background: var(--ec-overlay);
}

.image-preview__panel {
  display: grid;
  gap: var(--ec-space-3);
  max-width: 92vw;
  max-height: 92vh;
}

.image-preview__image {
  max-width: 92vw;
  max-height: 82vh;
  border-radius: var(--ec-radius-md);
  object-fit: contain;
}

.image-preview__actions {
  display: flex;
  justify-content: center;
  gap: var(--ec-space-3);
}

.image-preview__download,
.image-preview__close {
  padding: var(--ec-space-2) var(--ec-space-4);
  border: 1px solid var(--ec-overlay-border);
  border-radius: var(--ec-radius-full);
  background: transparent;
  color: var(--ec-text-inverse);
  text-decoration: none;
  font-size: var(--ec-font-size-secondary);
  cursor: pointer;
}

.image-preview__download:hover,
.image-preview__close:hover {
  background: var(--ec-overlay-hover);
}

.image-preview__download:focus-visible,
.image-preview__close:focus-visible {
  outline: var(--ec-focus-width) solid var(--ec-focus-ring);
  outline-offset: var(--ec-focus-offset);
}
</style>
