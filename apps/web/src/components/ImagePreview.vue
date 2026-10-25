<script setup lang="ts">
import { onBeforeUnmount, watch } from 'vue'

// 图片大图预览（M3-07）：全屏遮罩，点击空白/Esc 关闭；下载复用已鉴权加载的 blob URL（<a download>）。
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
  padding: var(--space-4);
  background: rgba(15, 23, 42, 0.78);
}

.image-preview__panel {
  display: grid;
  gap: var(--space-3);
  max-width: 92vw;
  max-height: 92vh;
}

.image-preview__image {
  max-width: 92vw;
  max-height: 82vh;
  border-radius: var(--radius-sm);
  object-fit: contain;
}

.image-preview__actions {
  display: flex;
  justify-content: center;
  gap: var(--space-3);
}

.image-preview__download,
.image-preview__close {
  padding: var(--space-2) var(--space-4);
  border: 1px solid rgba(255, 255, 255, 0.4);
  border-radius: var(--radius-full);
  background: transparent;
  color: #fff;
  text-decoration: none;
  font-size: 13px;
  cursor: pointer;
}
</style>
