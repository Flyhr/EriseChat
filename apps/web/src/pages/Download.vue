<script setup lang="ts">
// 下载页（PWA/安装包下载入口；安装包由服务端 /download 提供，M4 接入）。
</script>

<template>
  <main class="simple-page">
    <h2 class="simple-page__title">下载 EiseChat</h2>
    <p class="simple-page__hint">
      Windows 安装包与 Android APK 将在 M4 提供，由服务端 <code>/download</code> 页面分发。
    </p>
  </main>
</template>

<style scoped>
.simple-page {
  padding: var(--space-6);
}

.simple-page__title {
  margin: 0 0 var(--space-2);
}

.simple-page__hint {
  margin: 0;
  font-size: 13px;
  color: var(--color-text-secondary);
}

code {
  font-family: var(--font-mono);
}
</style>
