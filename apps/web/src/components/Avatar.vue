<script setup lang="ts">
import { computed } from 'vue'

// 头像（纯展示，UI-01 修 H-04）：默认字母/首字头像（brand-050 底 + brand-600 字）。
// avatarFileId 只是未来真实头像的引用占位，绝不当作 URL 使用；
// avatarUrl 仅允许传入「鉴权后得到的 blob: URL」（filesApi.downloadBlob / getOrLoadBlobUrl 生成），本期不接图片加载。
const props = defineProps<{
  displayName: string
  size?: number
  avatarFileId?: string
  avatarUrl?: string
}>()

const initial = computed(() => props.displayName.trim().charAt(0).toUpperCase() || '?')
const dimension = computed(() => `${props.size ?? 40}px`)
const fontSize = computed(() => `${Math.max(12, Math.round((props.size ?? 40) * 0.42))}px`)
const showImage = computed(() => Boolean(props.avatarUrl))
</script>

<template>
  <span
    class="avatar ec-avatar"
    :style="{ width: dimension, height: dimension, fontSize }"
    :title="displayName"
  >
    <img v-if="showImage" class="ec-avatar__image avatar__image" :src="avatarUrl" alt="" />
    <span v-else class="ec-avatar__initial" aria-hidden="true">{{ initial }}</span>
  </span>
</template>

<style scoped>
.ec-avatar {
  display: inline-flex;
  flex: none;
  align-items: center;
  justify-content: center;
  overflow: hidden;
  border-radius: var(--ec-radius-full);
  background: var(--ec-brand-050);
  color: var(--ec-brand-600);
  font-weight: var(--ec-font-weight-semibold);
  line-height: 1;
  user-select: none;
}

.ec-avatar__image {
  width: 100%;
  height: 100%;
  object-fit: cover;
}
</style>
