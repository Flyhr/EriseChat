<script setup lang="ts">
import { ref } from 'vue'
import { useRouter } from 'vue-router'
import { authApi } from '../api/auth'
import { chatWs } from '../core/ws'
import { clearAllData } from '../core/db'
import { useAuthStore } from '../stores/auth'
import { useConversationsStore } from '../stores/conversations'
import { useMessagesStore } from '../stores/messages'

// 我的 / 设置页：M3-00 先提供最简退出登录；个人资料/通知/服务器地址按后续里程碑补。
const router = useRouter()
const auth = useAuthStore()
const conversations = useConversationsStore()
const messages = useMessagesStore()

const loggingOut = ref(false)

/** 退出登录：先请求服务端注销会话（失败也继续本地清理），再断连清缓存回登录页（M3-00 N12）。 */
async function logout(): Promise<void> {
  if (loggingOut.value) return
  loggingOut.value = true
  try {
    await authApi.logout()
  } catch {
    // token 已过期/网络异常时服务端会话可能已失效；本地清理照常进行。
  } finally {
    chatWs.disconnect(1000, 'logout')
    auth.clearSession()
    conversations.clear()
    messages.clear()
    void clearAllData()
    await router.replace({ name: 'login' })
    loggingOut.value = false
  }
}
</script>

<template>
  <main class="simple-page">
    <h2 class="simple-page__title">设置</h2>
    <ul class="settings-list">
      <li class="settings-list__item">
        当前账号：{{ auth.user?.displayName }}（{{ auth.user?.email }}）
      </li>
      <li class="settings-list__item">个人资料（后续里程碑）</li>
      <li class="settings-list__item">提示音 / 桌面通知（M4）</li>
      <li class="settings-list__item">服务器地址（M4）</li>
      <li class="settings-list__item">
        <button class="settings-list__logout" type="button" :disabled="loggingOut" @click="logout">
          {{ loggingOut ? '退出中…' : '退出登录' }}
        </button>
      </li>
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

.settings-list__logout {
  padding: var(--space-2) var(--space-4);
  border: 1px solid var(--color-danger);
  border-radius: var(--radius-sm);
  background: transparent;
  color: var(--color-danger);
  font-size: 13px;
  cursor: pointer;
}

.settings-list__logout:disabled {
  opacity: 0.6;
  cursor: not-allowed;
}
</style>
