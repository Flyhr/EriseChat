<script setup lang="ts">
import { computed } from 'vue'

// 头像（纯展示）：无头像文件时显示昵称首字符；M2 接入 filesApi.downloadUrl 加载真实图片。
const props = defineProps<{
  displayName: string
  avatarFileId?: string
  size?: number
}>()

const initial = computed(() => props.displayName.trim().charAt(0).toUpperCase() || '?')
const dimension = computed(() => `${props.size ?? 40}px`)
</script>

<template>
  <span class="avatar" :style="{ width: dimension, height: dimension }">
    <img v-if="avatarFileId" class="avatar__image" :src="avatarFileId" alt="" />
    <span v-else class="avatar__initial">{{ initial }}</span>
  </span>
</template>

<style scoped>
.avatar {
  display: inline-flex;
  flex: none;
  align-items: center;
  justify-content: center;
  overflow: hidden;
  border-radius: var(--radius-full);
  background: var(--color-primary-soft);
  color: var(--color-primary);
  font-weight: 600;
}

.avatar__image {
  width: 100%;
  height: 100%;
  object-fit: cover;
}
</style>
