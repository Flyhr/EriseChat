<script setup lang="ts">
import EmptyState from '../components/EmptyState.vue'

// 管理页（UI-03 批次4）：仅样式与文案跟随设计系统；不加按钮、不接 /api/admin/、不改路由守卫权限。
// 用户管理、注册策略与审计日志属于后续版本。
</script>

<template>
  <main class="admin-page">
    <h2 class="admin-page__title">管理</h2>
    <EmptyState
      icon="settings"
      title="管理功能尚未开放"
      description="用户管理、注册策略与审计日志将在后续版本提供；当前仅保留入口占位。"
    />
  </main>
</template>

<style scoped>
.admin-page {
  height: 100%;
  padding: var(--ec-space-6);
  overflow-y: auto;
  background: var(--ec-bg-canvas);
}

.admin-page__title {
  margin: 0 0 var(--ec-space-4);
  color: var(--ec-text-primary);
  font-size: var(--ec-font-size-page-title);
  font-weight: var(--ec-font-weight-semibold);
}
</style>
