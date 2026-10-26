import { createApp } from 'vue'
import { createPinia } from 'pinia'
import App from './App.vue'
import router from './router'
import { setUnauthorizedHandler } from './core/http'
import { chatWs } from './core/ws'
import { clearAllData } from './core/db'
import { useAuthStore } from './stores/auth'
import { useConversationsStore } from './stores/conversations'
import { useFriendsStore } from './stores/friends'
import { useMessagesStore } from './stores/messages'
import './styles/variables.css'
import './styles/global.css'

// 应用入口：只做装配。登录态恢复在 stores/auth 创建时完成（localStorage + expiresAt 校验）；
// WebSocket 建连在登录成功/路由守卫中触发（M2-03）。
const app = createApp(App)
app.use(createPinia())
app.use(router)

const auth = useAuthStore()
const conversations = useConversationsStore()
const friends = useFriendsStore()
const messages = useMessagesStore()

/**
 * 清理登录相关状态（M3-00）：断连 + 清登录态 + 清会话/消息内存状态 + 清 IndexedDB 缓存，
 * 保证账号切换（退出/被踢/登录失效）后不残留上一位用户的数据与连接。
 */
function cleanupSession(): void {
  chatWs.disconnect(1000, 'session cleanup')
  auth.clearSession()
  conversations.clear()
  friends.clear()
  messages.clear()
  void clearAllData()
}

// 401 统一处理（M2-02/M3-00）：清登录态与缓存并回 /login；/auth/* 登录类接口自身 401 由页面提示，不在此跳转。
setUnauthorizedHandler(() => {
  cleanupSession()
  if (router.currentRoute.value.name !== 'login') {
    void router.replace({ name: 'login', query: { notice: '登录已失效，请重新登录' } })
  }
})

// 被踢下线（4001）/认证类致命关闭（4002）：清会话回登录页并提示（protocol.md §1、M3-00）。
chatWs.on('fatalClose', ({ code }) => {
  cleanupSession()
  const notice = code === 4001 ? '账号已在其他设备登录，请重新登录' : '登录已失效，请重新登录'
  if (router.currentRoute.value.name !== 'login') {
    void router.replace({ name: 'login', query: { notice } })
  }
})

app.mount('#app')
