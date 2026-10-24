import { createApp } from 'vue'
import { createPinia } from 'pinia'
import App from './App.vue'
import router from './router'
import './styles/variables.css'
import './styles/global.css'

// 应用入口：只做装配。恢复登录态、连接 WebSocket、启动补拉等初始化逻辑统一放在 M1 的 bootstrap 中，
// 避免入口文件依赖具体业务。
const app = createApp(App)
app.use(createPinia())
app.use(router)
app.mount('#app')
