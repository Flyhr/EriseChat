<script setup lang="ts">
import EmptyState from '../components/EmptyState.vue'
import { APP_VERSION } from '../core/version'

// 下载页（UI-03 批次4）：说明 Windows 安装包与 Android APK 由服务端 /download 分发。
// 本页只做入口说明，不改变 /download 的行为、链接与权限（UI-03 范围约定）。
</script>

<template>
  <main class="download-page">
    <h2 class="download-page__title">下载 EiseChat</h2>
    <EmptyState
      icon="download"
      title="客户端安装包"
      description="Windows 安装包（EiseChat-Setup）与 Android APK 由服务端 /download 页面分发；内网部署时请向管理员获取访问地址。"
    >
      <p class="download-page__version">当前版本：v{{ APP_VERSION }}</p>
    </EmptyState>
    <p class="download-page__hint">
      安装包内容与版本随发布流程更新；如需核对版本或校验值，请联系管理员。
    </p>
  </main>
</template>

<style scoped>
.download-page {
  height: 100%;
  padding: var(--ec-space-6);
  overflow-y: auto;
  background: var(--ec-bg-canvas);
}

.download-page__title {
  margin: 0 0 var(--ec-space-4);
  color: var(--ec-text-primary);
  font-size: var(--ec-font-size-page-title);
  font-weight: var(--ec-font-weight-semibold);
}

.download-page__version {
  margin: 0;
  color: var(--ec-text-secondary);
  font-size: var(--ec-font-size-secondary);
}

.download-page__hint {
  max-width: 460px;
  margin: 0 auto;
  color: var(--ec-text-tertiary);
  font-size: var(--ec-font-size-small);
  line-height: 1.6;
  text-align: center;
}
</style>
