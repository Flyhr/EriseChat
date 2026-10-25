import { createRouter, createWebHistory } from 'vue-router'
import { chatWs } from '../core/ws'
import { useAuthStore } from '../stores/auth'

// 路由表（docs/project-overview.md §7.2）；页面组件懒加载，减小首屏体积。
const router = createRouter({
  history: createWebHistory(),
  routes: [
    { path: '/', redirect: '/chat' },
    { path: '/login', name: 'login', component: () => import('../pages/Login.vue') },
    { path: '/chat', name: 'chat', component: () => import('../pages/Chat.vue') },
    { path: '/contacts', name: 'contacts', component: () => import('../pages/Contacts.vue') },
    { path: '/settings', name: 'settings', component: () => import('../pages/Settings.vue') },
    { path: '/admin', name: 'admin', component: () => import('../pages/Admin.vue') },
    { path: '/download', name: 'download', component: () => import('../pages/Download.vue') },
    { path: '/:pathMatch(.*)*', redirect: '/chat' },
  ],
})

/** 登录态存在但尚未建连时补建（如刷新后从 localStorage 恢复登录态直达受保护页；M2-03）。 */
function ensureChatConnection(auth: ReturnType<typeof useAuthStore>): void {
  if (auth.accessToken === null) return
  if (chatWs.state === 'idle' || chatWs.state === 'closed') {
    chatWs.setAuth(auth.accessToken, auth.deviceId)
    chatWs.connect()
  }
}

// 登录守卫（M2-02）：未登录访问非 /login 一律回登录页；已登录访问 /login 直接进 /chat。
// 守卫在 app.use(pinia)/app.use(router) 后的首次导航时执行，此时 pinia 已激活。
router.beforeEach((to) => {
  const auth = useAuthStore()
  if (!auth.isLoggedIn && to.name !== 'login') {
    return { name: 'login' }
  }
  if (auth.isLoggedIn && to.name === 'login') {
    return { name: 'chat' }
  }
  if (auth.isLoggedIn) ensureChatConnection(auth)
  return true
})

export default router
