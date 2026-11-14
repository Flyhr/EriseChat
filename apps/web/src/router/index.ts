import { createRouter, createWebHashHistory, createWebHistory } from 'vue-router'
import { chatWs } from '../core/ws'
import { isConversationIdLike } from '../core/desktop'
import { isInviteTokenLike } from '../core/inviteToken'
import { useAuthStore } from '../stores/auth'

// 路由表（docs/project-overview.md §7.2）；页面组件懒加载，减小首屏体积。
// Electron 打包态由 file:// 加载 index.html：history 模式在 file 协议下无法工作（路径即文件路径），
// 使用 hash 路由（file:///…/index.html#/login）；Web 部署仍用 history 模式保持正常 URL。
const isFileProtocol = typeof window !== 'undefined' && window.location.protocol === 'file:'
const router = createRouter({
  history: isFileProtocol ? createWebHashHistory() : createWebHistory(),
  routes: [
    { path: '/', redirect: '/chat' },
    // /login 使用 blank 布局（无左侧导航，登录页视觉不变；UI-01）。
    { path: '/login', name: 'login', component: () => import('../pages/Login.vue'), meta: { layout: 'blank' } },
    { path: '/chat/:conversationId?', name: 'chat', component: () => import('../pages/Chat.vue') },
    { path: '/contacts', name: 'contacts', component: () => import('../pages/Contacts.vue') },
    { path: '/favorites', name: 'favorites', component: () => import('../pages/Favorites.vue') },
    { path: '/settings', name: 'settings', component: () => import('../pages/Settings.vue') },
    // 群邀请链接落地页（C1c）：blank 布局，登录后确认加入；未登录时守卫把令牌带进登录页。
    { path: '/join/:token', name: 'join', component: () => import('../pages/JoinGroup.vue'), meta: { layout: 'blank' } },
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
    // 未登录直达会话链接：只把格式校验过的会话 ID 带到登录页，不接受任意重定向 URL。
    // 会话访问权仍由服务端在加载会话时校验，前端不预判权限。
    const conversationId = to.name === 'chat' ? to.params.conversationId : undefined
    const target = isConversationIdLike(conversationId) ? conversationId : null
    // 群邀请链接同理：只带格式合法的令牌，登录成功后回到落地页由用户确认加入。
    const joinToken = to.name === 'join' ? to.params.token : undefined
    const inviteToken = isInviteTokenLike(joinToken) ? joinToken : null
    const query = target ? { conversationId: target } : inviteToken ? { join: inviteToken } : {}
    return { name: 'login', query }
  }
  if (auth.isLoggedIn && to.name === 'login') {
    return { name: 'chat' }
  }
  if (auth.isLoggedIn) ensureChatConnection(auth)
  return true
})

export default router
