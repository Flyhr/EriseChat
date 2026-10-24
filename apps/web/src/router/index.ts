import { createRouter, createWebHistory } from 'vue-router'

// 路由表（docs/project-overview.md §7.2）；页面组件懒加载，减小首屏体积。
// 登录守卫 M1 接入 auth store 后启用：未登录访问受保护路由 → /login。
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

router.beforeEach(() => {
  // TODO(M1): 读取 auth store 登录态；未登录访问非 /login 路由时跳转登录页。
  return true
})

export default router
