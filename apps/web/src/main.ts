import { createApp } from 'vue'
import { createPinia } from 'pinia'
import App from './App.vue'
import router from './router'
import { setUnauthorizedHandler } from './core/http'
import { useAuthStore } from './stores/auth'
import './styles/variables.css'
import './styles/global.css'

// 应用入口：只做装配。登录态恢复在 stores/auth 创建时完成（localStorage + expiresAt 校验），
// WebSocket 连接与补拉等业务初始化留 M2-03 的 bootstrap，避免入口依赖具体业务。
const app = createApp(App)
app.use(createPinia())
app.use(router)

// 401 统一处理（M2-02）：清登录态并回 /login；/auth/* 登录类接口自身 401 由页面提示，不在此跳转。
const auth = useAuthStore()
setUnauthorizedHandler(() => {
  auth.clearSession()
  if (router.currentRoute.value.name !== 'login') {
    void router.replace({ name: 'login' })
  }
})

app.mount('#app')
