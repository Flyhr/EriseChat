<script setup lang="ts">
// 我的 / 设置页（M2 实现：头像昵称、提示音、通知、退出登录、服务器地址）。
</script>

<template>
  <main class="simple-page">
    <h2 class="simple-page__title">设置</h2>
    <ul class="settings-list">
      <li class="settings-list__item">个人资料（M2）</li>
      <li class="settings-list__item">提示音 / 桌面通知（M2）</li>
      <li class="settings-list__item">服务器地址（M2）</li>
      <li class="settings-list__item">退出登录（M1）</li>
    </ul>
  </main>
</template>

<style scoped>
.simple-page {
  padding: var(--space-6);
}

.simple-page__title {
  margin: 0 0 var(--space-4);
}

.settings-list {
  display: grid;
  gap: var(--space-2);
  max-width: 420px;
}

.settings-list__item {
  padding: var(--space-3) var(--space-4);
  background: var(--color-surface);
  border: 1px solid var(--color-border);
  border-radius: var(--radius-sm);
  color: var(--color-text-secondary);
  font-size: 13px;
}
</style>
