import { defineConfig } from 'vite'
import vue from '@vitejs/plugin-vue'

// EiseChat Web 开发/构建配置（docs/project-overview.md §7.2、§13.2）。
// 开发期把 /api、/ws 代理到本机服务端（Kestrel 127.0.0.1:8080）；生产环境由服务端同域提供，
// 不需要代理（见 docs/project-overview.md §7.4：构建产物复制到 server/Chat.Server/wwwroot/）。
export default defineConfig({
  plugins: [vue()],
  server: {
    port: 5173,
    proxy: {
      '/api': {
        target: 'http://127.0.0.1:8080',
        changeOrigin: true,
      },
      '/ws': {
        target: 'ws://127.0.0.1:8080',
        ws: true,
      },
    },
  },
  build: {
    outDir: 'dist',
    sourcemap: false,
    target: 'es2022',
  },
})
