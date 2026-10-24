<script setup lang="ts">
// 管理页（仅 admin 角色可见；M3 实现：用户管理、注册开关、审计日志）。
// 路由守卫在 M1 统一接入，非 admin 访问时应跳转 /chat。
</script>

<template>
  <main class="simple-page">
    <h2 class="simple-page__title">管理</h2>
    <p class="simple-page__hint">M3 实现用户管理、注册策略与审计日志。</p>
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
</style>
