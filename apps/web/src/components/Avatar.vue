<script setup lang="ts">
import { computed, onBeforeUnmount, ref, watch } from 'vue'
import { getOrLoadBlobUrl } from '../core/blobCache'
import { filesApi } from '../api/files'

// 头像（纯展示，UI-01 修 H-04；F-12 接入鉴权图片）：
// - 默认字母/首字头像（brand-050 底 + brand-600 字）；
// - 传入 avatarFileId 时内部用 blobCache 下载并展示（image/*），不把 blob URL 写入 store/持久化；
// - avatarUrl 仅允许传入外部已准备好的 blob: URL（当前无调用方），优先于 avatarFileId；
// - 下载失败或加载中显示首字母（不破图）；卸载后忽略过期结果（blobCache 负责 URL 生命周期）。
const props = defineProps<{
  displayName: string
  size?: number
  avatarFileId?: string
  avatarUrl?: string
}>()

const initial = computed(() => props.displayName.trim().charAt(0).toUpperCase() || '?')
const dimension = computed(() => `${props.size ?? 40}px`)
const fontSize = computed(() => `${Math.max(12, Math.round((props.size ?? 40) * 0.42))}px`)

/** 内部加载到的 object URL（仅 avatarFileId 路径使用）。 */
const loadedUrl = ref<string | null>(null)
/** 下载失败：回退首字母，避免反复请求。 */
const loadFailed = ref(false)
/** 请求序号：快速切换 fileId 时丢弃过期结果。 */
let requestSeq = 0

const resolvedUrl = computed(() => props.avatarUrl ?? loadedUrl.value)
const showImage = computed(() => Boolean(resolvedUrl.value) && !loadFailed.value)

async function loadAvatar(fileId: string | undefined): Promise<void> {
  if (!fileId || props.avatarUrl) {
    loadedUrl.value = null
    loadFailed.value = false
    return
  }

  const seq = ++requestSeq
  try {
    const url = await getOrLoadBlobUrl(fileId, () => filesApi.downloadBlob(fileId))
    if (seq !== requestSeq) return
    loadedUrl.value = url
    loadFailed.value = false
  } catch {
    if (seq !== requestSeq) return
    loadedUrl.value = null
    loadFailed.value = true
  }
}

watch(
  () => [props.avatarFileId, props.avatarUrl] as const,
  () => {
    void loadAvatar(props.avatarFileId)
  },
  { immediate: true },
)

function onImageError(): void {
  loadFailed.value = true
}

onBeforeUnmount(() => {
  // 让未完成的加载结果失效；blob URL 由 blobCache 统一管理（不在此 revoke）。
  requestSeq += 1
})
</script>

<template>
  <span
    class="avatar ec-avatar"
    :style="{ width: dimension, height: dimension, fontSize }"
    :title="displayName"
  >
    <img
      v-if="showImage"
      class="ec-avatar__image avatar__image"
      :src="resolvedUrl ?? undefined"
      alt=""
      @error="onImageError"
    />
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
