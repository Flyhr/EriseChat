<script setup lang="ts">
// 聊天主页面（M1 实现：会话列表 + 消息流 + 发送框 + 断线横幅）。
// 规则：页面只做编排，状态走 stores/，连接走 core/ws.ts，不直接 new WebSocket / fetch。
</script>

<template>
  <main class="chat-page">
    <aside class="chat-page__sidebar">
      <header class="chat-page__sidebar-header">消息</header>
      <p class="chat-page__placeholder">会话列表（M1）</p>
    </aside>
    <section class="chat-page__main">
      <p class="chat-page__placeholder">选择会话开始聊天（M1）</p>
    </section>
  </main>
</template>

<style scoped>
.chat-page {
  display: grid;
  grid-template-columns: 280px 1fr;
  height: 100%;
  background: var(--color-surface);
}

.chat-page__sidebar {
  display: flex;
  flex-direction: column;
  border-right: 1px solid var(--color-border);
}

.chat-page__sidebar-header {
  display: flex;
  align-items: center;
  height: var(--header-height);
  padding: 0 var(--space-4);
  font-weight: 600;
  border-bottom: 1px solid var(--color-border);
}

.chat-page__main {
  display: flex;
  align-items: center;
  justify-content: center;
}

.chat-page__placeholder {
  color: var(--color-text-secondary);
  font-size: 13px;
}
</style>
