import { defineConfig, type Plugin } from 'vite'
import vue from '@vitejs/plugin-vue'
import { APP_VERSION } from './src/core/version'

/**
 * 把客户端版本写入 dist/web-version.json，随静态资源一起部署。
 * 服务端据此下发清单的 webVersion（protocol/protocol.md §7 第 10 条），
 * 版本号唯一来源是 src/core/version.ts 的 APP_VERSION，不另写一份。
 */
function webVersionFile(version: string): Plugin {
  return {
    name: 'erisechat-web-version',
    generateBundle() {
      this.emitFile({
        type: 'asset',
        fileName: 'web-version.json',
        source: `${JSON.stringify({ version })}\n`,
      })
    },
  }
}

// EriseChat Web 开发/构建配置（docs/project-overview.md §7.2、§13.2）。
// 开发期把 /api、/ws 代理到本机服务端（Kestrel 127.0.0.1:6580）；生产环境由服务端同域提供，
// 不需要代理（见 docs/project-overview.md §7.4：构建产物复制到 server/Chat.Server/wwwroot/）。
export default defineConfig({
  // 相对基路径：Electron 打包态用 file:// 加载 index.html（resources/web-dist），
  // 绝对路径 /assets/... 会解析到 file:///assets/ 导致白屏；同域 HTTP 部署用相对路径同样正常。
  base: './',
  plugins: [vue(), webVersionFile(APP_VERSION)],
  server: {
    // 固定 IPv4：桌面壳（apps/desktop）开发态加载 http://127.0.0.1:5173，需与 Vite 监听地址一致。
    host: '127.0.0.1',
    port: 5173,
    proxy: {
      '/api': {
        target: 'http://127.0.0.1:6580',
        changeOrigin: true,
      },
      '/ws': {
        target: 'ws://127.0.0.1:6580',
        ws: true,
      },
    },
  },
  preview: {
    host: '127.0.0.1',
  },
  build: {
    outDir: 'dist',
    sourcemap: false,
    target: 'es2022',
  },
})
