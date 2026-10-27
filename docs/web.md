# EiseChat Web 端运行与构建说明

> 本文是 Web 客户端（Vue 3 核心，`apps/web`）的开发运行、构建、部署与验收说明。
> 协议见 `protocol/protocol.md`；服务端部署见 `docs/deploy.md`；Windows 壳见 `docs/desktop.md`；Android 见 `docs/mobile.md`；项目介绍以 `README.md` 为准。
> 最近更新：2026-10-27（M1~M5 与 UI 改造完成后）。

## 1. 是什么、在哪里

| 形态 | 入口 / 产物 | 说明 |
|---|---|---|
| 开发服务器 | `http://127.0.0.1:5173` | `npm run dev`，带 `/api`、`/ws` 到本机服务端 8080 的代理 |
| 生产构建 | `apps/web/dist/`（不入库） | `npm run build`，相对基路径 `./`，可直接被服务端同域托管 |
| 生产访问 | `https://<域名>/` | 构建产物复制到 `server/Chat.Server/wwwroot/` 后由服务端静态托管 |
| 安装包下载页 | `GET /download` | 服务端页面（exe/apk 放 `data/downloads/`） |

- 一套代码多端复用：Web（浏览器）、Windows（Electron 加载同一构建产物）、Android（Capacitor 打包同一产物）。
- 系统要求：Node.js **20+**（本机 v24.18.0；Vite 8 建议 22 LTS 以上）、现代浏览器（Chrome/Edge 近两年版本）。
- 首次进入 `apps/web` 需 `npm install`（只装开发依赖，无原生编译）。

## 2. 快速开始（本地开发）

前提：本机服务端已启动并监听 `127.0.0.1:8080`（Kestrel）。

```bash
# 终端 1：服务端（仓库根）
dotnet run --project server/Chat.Server
curl http://127.0.0.1:8080/api/health   # {"status":"ok",...,"db":"ok"}

# 终端 2：Web 开发服务器
cd apps/web
npm install        # 首次
npm run dev        # http://127.0.0.1:5173
```

- 开发期 `vite.config.ts` 把 `/api` 代理到 `http://127.0.0.1:8080`、`/ws` 代理到 `ws://127.0.0.1:8080`，所以**开发调试请用 5173**；直接打开 8080 没有前端资源（除非已把 dist 复制到 wwwroot）。
- 服务端 `Development` 环境使用 mock 邮件：验证码固定 `123456`，同时打印到服务端控制台（`[email][mock] ... code=123456`），不写入日志文件；登录页与「发送验证码」响应也带 `devCode` 提示。
- 两浏览器验收：一个普通窗口 + 一个无痕窗口，用两个不同邮箱账号注册/登录（两者共享同一浏览器存储会互相顶号）。

## 3. 服务端地址配置

| 场景 | 地址来源 | 说明 |
|---|---|---|
| Web 生产（同域） | 默认同源 | `apiBase=/api`，`wsUrl=页面 origin+/ws` |
| Web 手动指定 | 登录页/设置页「服务器地址」 | 存 `localStorage['eisechat.serverUrl']`；清空恢复同源 |
| Windows 桌面壳 | preload 注入的已保存地址 | 见 `docs/desktop.md` |
| Android | App 登录页「服务器地址」 | 见 `docs/mobile.md` |

可选构建期环境变量（见 `apps/web/.env.example`，默认值集中在 `src/core/config.ts`，业务代码不直接读 `import.meta.env`）：

| 变量 | 默认 | 用途 |
|---|---|---|
| `VITE_API_BASE` | `/api` | REST 基础路径 |
| `VITE_WS_URL` | 页面 origin 派生 `/ws` | WebSocket 地址 |
| `VITE_PROTOCOL_VERSION` | `1` | 协议版本（与 `protocolVersion` 一致） |

## 4. 构建与检查

```bash
cd apps/web
npm run typecheck   # vue-tsc --noEmit（仅类型检查）
npm run build       # vue-tsc --noEmit && vite build → apps/web/dist/
npm run preview     # 本地预览 dist（127.0.0.1，非开发代理）
npm run test:unit   # vitest run（178 个纯逻辑用例，约 1 秒，不依赖服务端）
```

构建要点：

- `base: './'`（相对基路径）：既支持服务端同域部署，也支持 Electron 用 `file://` 加载本地 `index.html`，避免白屏。
- 产物：`dist/` 约 230KB（`index-*.js` ≈70KB、CSS ≈14KB、懒加载聊天页 ≈28KB，图片/字体无外部依赖）。
- 静态资源文件名带内容 hash，升级后浏览器自动取新版本；异常时 `Ctrl+F5` 强刷。
- `npm run build` 会先跑类型检查，类型错误直接失败——提交前请保证通过。

## 5. 部署到服务端（生产）

1. 构建 Web：

   ```bash
   cd apps/web && npm run build
   ```

2. 把构建产物复制到服务端静态目录（该目录由构建生成、已 gitignore，不手改）：

   ```bash
   # Git Bash（仓库根）
   cp -r apps/web/dist/. server/Chat.Server/wwwroot/

   # 或 Windows cmd
   robocopy apps\web\dist server\Chat.Server\wwwroot /E
   ```

3. 发布后端并安装（详见 `docs/deploy.md` §3）：

   ```bash
   bash infra/publish-linux.sh 1.0.0          # 产出 chat-server-1.0.0-linux-x64.zip（含 wwwroot）
   sudo bash infra/install.sh /tmp/chat-server-1.0.0-linux-x64.zip   # 服务器上执行
   ```

4. 浏览器打开 `https://<域名>/`，`/api/health` 自检，`/download` 检查安装包页。

注意事项：

- `infra/publish-linux.sh` **目前不会自动构建/复制前端**：必须先做第 1、2 步；`wwwroot/` 为空时服务端只提供 `/api`、`/ws`、`/download`，访问 `/` 会 404。
- **SPA 路由限制**：Web 使用 history 路由，但服务端当前没有 SPA fallback（未配置 `MapFallbackToFile`），部署后直接刷新 `/chat/:id` 之类深链接会 404；从根路径进入、页面内跳转正常。后续可在服务端加 fallback（改服务端前先按 AGENTS §2.7 更新协议/计划文档）。
- 服务端只监听 `127.0.0.1:8080`，公网入口由 Caddy 反代并提供 HTTPS（`docs/deploy.md` §1/§3.3）；8080 不对公网开放。
- 下载页安装包来自服务器 `data/downloads/`（`App:DownloadDirectory`），页面自动取最新的 `EiseChat-Setup-*.exe` / `EiseChat-*.apk`。

## 6. 运行时能力与限制（当前版本）

已有能力：邮箱验证码/密码登录注册、单聊与群聊、文本/图片/文件消息、已读回执（单聊）、typing、在线状态、断线重连与补拉、IndexedDB 缓存、设置持久化（提示音/桌面通知开关存 localStorage）、头像与昵称修改（设置页上传头像/改昵称，头像经鉴权接口 + blob 缓存加载、失败回退首字母）、账号安全（修改/设置登录密码、忘记密码邮箱验证码重置、登录设备列表与退出其他设备）、提示音（WebAudio 合成）、浏览器系统通知（需授权）。

限制（v1 有意不做 / 暂缺）：

- 无离线推送：标签页关闭后收不到新消息，重新打开时靠补拉同步。
- 浏览器标签页在后台可能被节流（计时器/心跳），长时间后台后重连由客户端退避机制处理。
- 浏览器通知要求 **https 或 localhost** 且用户授予通知权限；http 局域网地址下浏览器通知不可用（桌面壳走 Electron 通知，不受此限）。
- 不做端到端加密（服务端可见消息内容）；不做 iPhone/iOS 专用客户端。
- 深色主题仅预留 token，未实现。

## 7. 验收清单（每次较大改动后建议过一遍）

| # | 项目 | 期望 |
|---|---|---|
| 1 | 注册/登录 | 注册、验证码登录、密码登录三模式可用；错误提示中文且不含 token |
| 2 | 单聊文本 | 两浏览器互发 100 条，seq 连续、无丢失/重复 |
| 3 | 群聊 | 建群、邀请、移除、退出、系统消息、成员面板 |
| 4 | 图片/文件 | 压缩上传、缩略图、预览、鉴权下载、非成员 403 |
| 5 | 已读/typing/在线 | 滚到底上报已读；单聊「对方正在输入…」；在线点显示 |
| 6 | 断线补拉 | 停服务端约 1 分钟再恢复，重连后消息补齐且不重复 |
| 7 | 刷新恢复 | 刷新页面后会话列表/当前会话/未读正确（IndexedDB 缓存） |
| 8 | 设置持久化 | 提示音/桌面通知开关刷新后保持；提示音只在窗口不可见或非当前会话时响 |
| 9 | 浏览器通知 | 授权后，切到其他标签页收到新消息弹通知；点击不被拦截 |
| 10 | 响应式 | 1440/1000/800/390 宽度下无横向滚动、输入区不遮挡 |
| 11 | 错误处理 | 401 跳登录、被踢提示、连接断开横幅、404/网络错误中文提示 |
| 12 | 退出登录 | 回到登录页，清空会话/未读/缓存，不残留上账号数据 |
| 13 | 账号安全 | 修改密码后旧密码失效、新密码可登录；忘记密码可用邮箱验证码重置；登录设备列表显示当前设备，退出其他设备后其登录立即失效 |

## 8. 故障排查

| 现象 | 排查 |
|---|---|
| `npm run dev` 打不开 5173 | `npm install` 是否完成；端口被占用（改端口或结束进程）；`vite.config.ts` 固定 host `127.0.0.1`，不要用 `localhost` 以外的地址访问 |
| 页面能开但接口全 404 / Network 报错 | 服务端 8080 是否启动；代理目标是否被改；检查 `curl http://127.0.0.1:8080/api/health` |
| 登录提示「无法连接服务器」 | 服务端未启动；登录页服务器地址填错（本机应为 `http://127.0.0.1:8080`；局域网/手机不能用 `127.0.0.1`）；端口被改 |
| 收不到验证码 | 开发环境固定码 `123456`（不真的发邮件）；服务端控制台看 `[email][mock]`；Production 未配置 SMTP 会返回 500（`docs/deploy.md` §5） |
| 生产环境白屏 / 404 | `dist` 是否已复制到 `server/Chat.Server/wwwroot/`；是否从根路径进入；F12 看静态资源 404 还是 JS 报错 |
| 刷新深链接 404 | 已知限制（服务端无 SPA fallback）：从根路径进入；修复需服务端加 fallback |
| 401 被踢回登录页 | token 过期（默认 7 天）、账号被禁用、或在别处登录触发单活动踢旧；重新登录即可 |
| 浏览器通知不弹 | 使用 https/localhost；浏览器/系统通知权限是否允许；仅在窗口不可见时弹；开关是否打开 |
| 提示音没声音 | 设置页提示音开关；系统静音/浏览器静音；首次需用户交互后 AudioContext 才会解锁（先点一次页面） |
| 刷新后会话/消息丢失 | 无痕模式或清站点数据会清 IndexedDB；以服务端为准，重新同步即可 |
| 类型检查报错 | `npm run typecheck` 看具体文件；提交前必须通过 |

## 9. 目录速查（apps/web）

```
apps/web/
├── index.html              # 入口（viewport-fit、favicon、title）
├── vite.config.ts          # base './'、5173、/api /ws 代理、es2022
├── tsconfig.json / tsconfig.node.json
├── package.json            # dev/build/preview/typecheck/test:unit
└── src/
    ├── main.ts             # 应用启动、401/致命关闭处理、会话清理
    ├── App.vue             # 连接横幅 + 路由出口 + presence 订阅
    ├── router/index.ts     # 路由与登录守卫；file:// 用 hash 模式
    ├── core/               # config（唯一默认值）、ws 状态机、sync 补拉、http、db、附件/压缩/通知/排版纯函数
    ├── api/                # REST 封装（auth/users/conversations/messages/files/admin）
    ├── stores/             # auth/conversations/messages/contacts/presence/settings/typing
    ├── pages/              # Login / Chat / Contacts / Settings / Admin / Download
    ├── components/         # 侧栏、会话行、消息区、输入区、抽屉、弹窗、提示等
    ├── styles/             # variables.css（--ec-* token）+ global.css
    └── types/frames.d.ts   # 协议 DTO 类型（与 protocol/protocol.md 对应）
```

约定：协议/表结构不改；业务状态只放 `stores/`，连接与协议细节只放 `core/`，组件不直接 `new WebSocket()`/`fetch()`；新增依赖需先查许可（AGENTS §2.3）；测试文件与工程同目录 `*.test.ts`（工程内测试随工程提交，脚本类测试放 `docs/test/` 不入库）。

## 10. 相关文档

- 项目介绍与入口：`README.md`
- 硬约束：`AGENTS.md`
- 协议：`protocol/protocol.md`、`protocol/schema.sql`
- 服务端部署：`docs/deploy.md`
- Windows 壳：`docs/desktop.md`；Android：`docs/mobile.md`
- 目录与模块职责：`docs/project-overview.md` §7
