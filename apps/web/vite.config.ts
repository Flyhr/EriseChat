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
//
// 两套产物按构建目标分开，避免「相对资源」与「深链接」互相冲突：
//   - Web（默认 mode=production）：base '/'，产物 dist/web，供服务端同域托管。
//     刷新 /chat/<id> 时，/assets/* 必须从站点根解析；若用 './' 会变成 /chat/assets/*，
//     静态中间件未命中后回退成 index.html，脚本以 HTML 加载 → 白屏。
//   - 壳（mode=file）：base './'，产物 dist/file，供 Electron file:// 与 Android WebView 加载。
//     壳资源不在站点根，绝对路径会解析到 file:///assets/ 或错误 origin。
// 消费者：infra/docker/Dockerfile、infra/publish-linux.sh 取 dist/web；
//         apps/desktop/electron-builder.yml、apps/mobile/capacitor.config.ts、
//         infra/build-apk.sh 取 dist/file。
export default defineConfig(({ mode }) => {
  const forFile = mode === 'file'
  return {
    base: forFile ? './' : '/',
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
      outDir: forFile ? 'dist/file' : 'dist/web',
      sourcemap: false,
      target: 'es2022',
    },
  }
})
