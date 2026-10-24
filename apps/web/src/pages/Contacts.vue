<script setup lang="ts">
// 通讯录 / 建群页（M2 实现；数据来自 stores/contacts.ts 与 api/users.ts）。
</script>

<template>
  <main class="simple-page">
    <h2 class="simple-page__title">通讯录</h2>
    <p class="simple-page__hint">M2 实现用户搜索、建群与成员管理。</p>
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
