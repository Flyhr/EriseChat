<script setup lang="ts">
import { ref } from 'vue'
import { useRouter } from 'vue-router'
import { authApi } from '../api/auth'
import { chatWs } from '../core/ws'
import { clearAllData } from '../core/db'
import { getServerUrl, resetServerUrl, setServerUrl } from '../core/config'
import { useAuthStore } from '../stores/auth'
import { useConversationsStore } from '../stores/conversations'
import { useMessagesStore } from '../stores/messages'
import ComingSoonHint from '../components/ComingSoonHint.vue'

// 我的 / 设置页：M3-00 提供最简退出登录；M4-01 增加服务器地址运行时配置（桌面/APK 必需，Web 同源留空）。
// 个人资料/通知开关按后续里程碑补。
const router = useRouter()
const auth = useAuthStore()
const conversations = useConversationsStore()
const messages = useMessagesStore()

const loggingOut = ref(false)

const savedServerUrl = ref(getServerUrl())
const serverUrlInput = ref(savedServerUrl.value)
const savingServer = ref(false)
const serverMessage = ref('')
const currentServerLabel = ref(savedServerUrl.value || '与当前页面同源')

/** 保存服务器地址：校验 → 持久化 → 断开旧连接并按新地址重连（token 可能失效，认证失败会回登录页）。 */
function saveServer(): void {
  if (savingServer.value) return
  const value = serverUrlInput.value.trim()
  if (value.length > 0 && !setServerUrl(value)) {
    serverMessage.value = '地址无效：需以 http:// 或 https:// 开头'
    return
  }
  if (value.length === 0) {
    resetServerUrl()
  }
  savingServer.value = true
  savedServerUrl.value = getServerUrl()
  currentServerLabel.value = savedServerUrl.value || '与当前页面同源'
  serverUrlInput.value = savedServerUrl.value
  if (chatWs.state !== 'idle' && chatWs.state !== 'closed') {
    chatWs.disconnect(1000, 'server url changed')
  }
  chatWs.connect()
  serverMessage.value = savedServerUrl.value
    ? '已保存，正在使用新地址重新连接（服务器变更后需重新登录）'
    : '已恢复同源默认地址，正在重新连接'
  savingServer.value = false
}

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
      <li class="settings-list__item">
        <ComingSoonHint label="深色主题" hint="即将上线" layout="inline">
          外观：深色主题（当前仅提供浅色主题）
        </ComingSoonHint>
      </li>
      <li class="settings-list__item settings-server">
        <span class="settings-server__title">服务器地址</span>
        <p class="settings-server__current">当前：{{ currentServerLabel }}</p>
        <div class="settings-server__row">
          <input
            v-model="serverUrlInput"
            type="url"
            placeholder="https://chat.example.com 或 http://127.0.0.1:8080（留空 = 与页面同源）"
          />
          <button type="button" :disabled="savingServer" @click="saveServer">保存</button>
        </div>
        <p v-if="serverMessage" class="settings-server__message">{{ serverMessage }}</p>
        <p class="settings-server__hint">
          桌面/APK 首次使用需填写服务器地址；保存后会断开旧连接并按新地址重连。
        </p>
      </li>
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
  height: 100%;
  padding: var(--space-6);
  overflow-y: auto;
}

.simple-page__title {
  margin: 0 0 var(--space-4);
}

.settings-list {
  display: grid;
  gap: var(--space-2);
  max-width: 520px;
}

.settings-list__item {
  padding: var(--space-3) var(--space-4);
  background: var(--color-surface);
  border: 1px solid var(--color-border);
  border-radius: var(--radius-sm);
  color: var(--color-text-secondary);
  font-size: 13px;
}

.settings-server {
  display: grid;
  gap: var(--space-2);
}

.settings-server__title {
  color: var(--color-text);
  font-weight: 600;
}

.settings-server__current {
  margin: 0;
}

.settings-server__row {
  display: flex;
  gap: var(--space-2);
}

.settings-server__row input {
  flex: 1;
  min-width: 0;
  padding: var(--space-2) var(--space-3);
  border: 1px solid var(--color-border);
  border-radius: var(--radius-sm);
  outline: none;
  box-sizing: border-box;
}

.settings-server__row input:focus {
  border-color: var(--color-primary);
}

.settings-server__row button {
  flex: none;
  padding: 0 var(--space-4);
  border: 1px solid var(--color-primary);
  border-radius: var(--radius-sm);
  background: var(--color-primary);
  color: var(--color-text-inverse);
  cursor: pointer;
}

.settings-server__row button:disabled {
  opacity: 0.6;
  cursor: not-allowed;
}

.settings-server__message {
  margin: 0;
  color: var(--color-primary);
}

.settings-server__hint {
  margin: 0;
  color: var(--color-text-secondary);
  font-size: 12px;
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
