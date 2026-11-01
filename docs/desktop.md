# EiseChat Windows 桌面端安装、开发与打包说明

> 本文是 Windows 客户端（Electron 壳 `apps/desktop` + `apps/web` 构建产物）的安装、运行、开发联调、打包与验收说明。
> 协议见 `protocol/protocol.md`；服务端部署见 `docs/deploy.md`；Web 端见 `docs/web.md`；Android 见 `docs/mobile.md`；项目介绍以 `README.md` 为准。
> 最近更新：2026-10-28（F-27：统一发布 1.1.0）。

## 1. 交付物与定位

| 项 | 值 |
|---|---|
| 安装包 | `EiseChat-Setup-1.1.0.exe`（NSIS，当前用户安装、免代码签名） |
| 产物位置 | `apps/desktop/dist/`（不入库）；分发时复制到服务端 `data/downloads/`，经 `GET /download` 提供下载 |
| 本机当前文件 | `data/downloads/EiseChat-Setup-1.1.0.exe`，121,359,488 B，SHA256 `82fbd17c3861830d4e3686928f1c410530bd3a1fad7d938ff3a43e1e0813deec`；FileVersion/ProductVersion 均为 1.1.0（重新打包后会变，以文件/打包输出为准）。历史基线：`EiseChat-Setup-1.0.0.exe`（121,290,773 B）保留在 `data/downloads/` 供回滚 |
| 应用标识 | `appId=com.eisechat.desktop`，产品名 `EiseChat` |
| 包内容 | Electron 主进程/预加载脚本 + `apps/web` 构建产物（以 `resources/web-dist/` 随安装包分发） |
| 最低系统 | Windows 10/11 x64（Electron 45 运行时自带，无需安装 .NET/Node） |

桌面壳只负责：窗口生命周期、托盘、关闭到托盘、窗口记忆、单实例、开机自启、系统通知、服务器地址等桌面设置、外链交给系统浏览器、打包；**聊天业务全部在 `apps/web`**，与 Web/Android 共用同一份前端代码与协议。

## 2. 安装与首次登录

1. 双击 `EiseChat-Setup-1.1.0.exe`。
   - 若出现 SmartScreen「Windows 已保护你的电脑」：点「更多信息 → 仍要运行」。这是**未购买代码签名证书**的正常提示（AGENTS §2.2 不产生授权费用），不是安装包损坏。
   - 向导默认「当前用户」安装，无需管理员权限；可改安装目录；自动创建桌面与开始菜单快捷方式。
2. 安装完成启动 EiseChat；打包态登录页会显示「服务器地址」区块（未配置时自动展开），填写地址后点「保存并检测」，状态显示服务可达即可登录：
   - 本机联调：`http://127.0.0.1:6580`（服务端 `dotnet run` 默认监听）
   - 云端部署：`https://<你的域名>`（Caddy 自动 HTTPS，见 `docs/deploy.md`）
   - 无人值守/批量部署也可预写 `%APPDATA%\EiseChat\settings.json` 的 `"serverUrl"` 后启动（登录页会读取并显示）
3. 登录后可在「设置 → 连接与关于 → 服务器地址」修改；切换后会自动断开并按新地址重连，需要重新登录。
4. 新账号切「注册」→ 发送验证码（开发环境固定 `123456`，生产走真实邮件）→ 填昵称/验证码注册并登录。
5. 登录后可单聊/群聊、收发文本/图片/文件、查看已读与在线状态；断线/关机再开自动补拉消息。

## 3. 客户端行为（v1）

| 能力 | 说明 |
|---|---|
| 关闭到托盘 | 点窗口关闭按钮默认**隐藏到托盘**而不是退出；托盘菜单「退出 EiseChat」才真正退出 |
| 托盘 | 单击托盘图标或「显示主窗口」唤回窗口；最小化/隐藏后收到消息由系统通知提示 |
| 窗口记忆 | 关闭/退出前保存窗口大小与位置，下次启动恢复（异常可在应用数据目录删除 `window-state.json`） |
| 单实例 | 重复双击快捷方式不会开第二个实例，而是聚焦已有窗口 |
| 开机自启 | 默认关闭；可在应用数据目录 `settings.json` 设 `"autoLaunch": true`（写入 Windows 登录启动项） |
| 系统通知 | 窗口不可见时收到新消息弹 Windows 通知（点击通知聚焦窗口）；开关在应用内「设置 → 通知」（默认关闭） |
| 提示音 | 应用内设置开关（默认开启），窗口不可见或非当前会话时响；WebAudio 合成，无音频文件 |
| 外链 | 消息内 http(s) 链接点击后交给系统默认浏览器（应用内不新开窗口、不导航离开本应用） |
| 最小窗口 | 960×640（`electron/main.ts` 的 `MIN_WIDTH/MIN_HEIGHT`），更窄的窗口会被限制 |
| 安全默认 | `contextIsolation=true`、`nodeIntegration=false`、`sandbox=true`；页面仅能访问白名单 IPC（服务器地址、设置、通知） |

应用数据目录（Windows）：`%APPDATA%\EiseChat\`（以实际 `app.getPath('userData')` 为准），内含：

- `settings.json`：`serverUrl` / `closeToTray`（默认 true）/ `autoLaunch`（默认 false）
- `window-state.json`：窗口大小/位置/最大化状态
- Chromium 用户数据（含前端 localStorage、IndexedDB 缓存）：仅缓存，聊天数据以服务端为准

> 注意：桌面设置（`settings.json`）与 Web 端设置（localStorage）是两套；服务器地址在桌面端以 `settings.json` 为准，页面内「设置」修改也会同步写回。

## 4. 开发联调（在本机）

前置：Node.js **20+**（本机 v24.18.0）；服务端与 Web 依赖已安装。

```bash
# 终端 1：服务端（127.0.0.1:6580）
dotnet run --project server/Chat.Server

# 终端 2：Web 开发服务器（127.0.0.1:5173）
cd apps/web && npm run dev

# 终端 3：桌面壳（先 tsc 编译 electron/，再启动 Electron 加载 5173）
cd apps/desktop
npm install          # 首次
npm run dev          # = npm run build && electron .
```

说明：

- 开发态 `main.ts` 加载 `http://127.0.0.1:5173`（可用环境变量 `EISECHAT_DEV_SERVER_URL` 覆盖）；请确保终端 2 的 Vite 已启动，否则窗口空白。
- 改 `apps/web` 页面/逻辑：Vite 热更新，窗口内即时生效；改 `apps/desktop/electron/*.ts`：需重跑 `npm run dev`（或 `npm run build` 后重启 Electron）。
- `npm run typecheck` 只做 Electron 侧 tsc 类型检查；前端类型检查在 `apps/web` 的 `npm run typecheck`。
- 开发态若出现「登录 Failed to fetch」：确认 6580 已启动；服务端已开 CORS 允许桌面端 `file://` 页面跨域调用（M4-08）。

## 5. 打包 Windows 安装包

```bash
# 1) 先构建最新的 Web 产物（安装包内嵌的是打包时的 web 构建结果）
cd apps/web && npm run build && cd ../..

# 2) 打包 NSIS 安装包
cd apps/desktop
npm install          # 首次
npm run build        # tsc 编译 electron/ → dist-electron/
npm run dist         # electron-builder --win nsis → apps/desktop/dist/EiseChat-Setup-1.1.0.exe
```

- 打包配置 `apps/desktop/electron-builder.yml`：`appId`、产品名、NSIS 每用户安装（`oneClick: false`、`perMachine: false`、可改目录、桌面/开始菜单快捷方式）、`artifactName` 固定为 `EiseChat-Setup-${version}.${ext}`、`extraResources` 把 `apps/web/dist` 复制为安装包内 `resources/web-dist/`、`build/icon.ico` 作为 exe 图标与托盘图标。
- **顺序不能反**：先构建 `apps/web`，再打包；否则安装包里是旧界面（或 `../web/dist` 不存在导致打包失败/白屏）。
- 首次打包会下载 Electron 运行时与 NSIS 工具链（约 100MB+，网络慢需数分钟）；国内网络可设置镜像再重试：
  - `ELECTRON_MIRROR=https://npmmirror.com/mirrors/electron/`
  - `ELECTRON_BUILDER_BINARIES_MIRROR=https://npmmirror.com/mirrors/electron-builder-binaries/`
- 产物 `apps/desktop/dist/` 与 `data/downloads/*.exe` 均不入库（`.gitignore` 已忽略）。
- 发给用户/上传服务器：把 `apps/desktop/dist/EiseChat-Setup-<版本>.exe` 复制到服务器 `data/downloads/`（或本机 `data/downloads/` 供本机下载页使用），`GET /download` 会自动取最新 exe。

### 版本号规则

三端与服务端版本保持一致：`apps/web/package.json`、`apps/desktop/package.json`、`apps/mobile/android/app/build.gradle`、服务端发布版本号（`publish-linux.sh <版本>`）统一修改；安装包文件名随之变成 `EiseChat-Setup-<版本>.exe`。当前为 `1.1.0`。

## 6. 静态校验（可选）

```bash
# Git Bash
sha256sum data/downloads/EiseChat-Setup-1.1.0.exe
ls -l data/downloads/EiseChat-Setup-1.1.0.exe

# Windows PowerShell
Get-FileHash .\data\downloads\EiseChat-Setup-1.1.0.exe -Algorithm SHA256
```

对照基线（1.1.0）：大小 121,359,488 B、SHA256 `82fbd17c…13deec`、FileVersion/ProductVersion 1.1.0；重新打包后两者都会变化，以打包输出为准。也可在「控制面板 → 程序和功能」或安装向导中核对版本 1.1.0。历史基线（1.0.0）：121,290,773 B、SHA256 `2ea45f7b…6f02f9e`。

## 7. 验收清单（每次打包后建议过一遍）

| # | 项目 | 期望 |
|---|---|---|
| 1 | 安装 | 向导可改目录；桌面/开始菜单有快捷方式；无需管理员权限 |
| 2 | 启动 | 首次打开进入登录页；打包态显示「服务器地址」区块（未配置自动展开），填写并「保存并检测」成功后能登录 |
| 3 | 登录 | 验证码/密码登录、注册可用；错误提示中文 |
| 4 | 消息收发 | 单聊/群聊文本、图片、文件；与 Web/Android 互发正常 |
| 5 | 可靠性 | 断网/服务端重启后重连补拉，无丢失/重复，seq 连续 |
| 6 | 通知 | 窗口隐藏时收到新消息弹 Windows 通知，点击聚焦窗口；关闭通知开关后不弹 |
| 7 | 提示音 | 设置开启时窗口不可见/非当前会话响，关闭后不响 |
| 8 | 托盘 | 关闭按钮隐藏到托盘；托盘「显示主窗口」/单击唤回；「退出」真正退出 |
| 9 | 单实例/记忆 | 重复启动只聚焦一个窗口；改窗口大小后重启恢复 |
| 10 | 外链 | 消息内链接用系统浏览器打开，应用内不跳转 |
| 11 | 覆盖安装 | 用同版本/新版本重新安装成功，服务端数据不受影响 |
| 12 | 卸载 | 从「设置 → 应用」卸载干净；重装后登录态/设置按预期；服务端聊天数据保留 |

## 8. 故障排查

| 现象 | 排查 |
|---|---|
| 打开窗口白屏 | 开发态：终端 2 的 Vite（5173）是否启动；打包态：是否先构建了 `apps/web`（安装包内 `resources/web-dist/index.html` 是否存在）；F12 看静态资源 404 |
| 登录提示「Failed to fetch」/无法连接 | 登录页「服务器地址」是否已保存且「保存并检测」通过；服务端 6580 是否启动（`curl http://127.0.0.1:6580/api/health`）；服务端是否已开 CORS（桌面端 `file://` 跨域） |
| 收不到系统通知 | 应用内「设置 → 通知」开关是否开启；Windows 通知/专注助手设置；**应用必须运行**（关闭后无推送）；仅窗口不可见时弹 |
| 关闭后程序「不见了」 | 关闭到托盘是默认行为，看任务栏右下角托盘图标；要退出用托盘菜单 |
| 托盘图标空白 | 安装包缺少 `resources/icon.ico`（打包时 `build/icon.ico` 是否存在）；重新打包 |
| 开机自启不生效 | 检查 `%APPDATA%\EiseChat\settings.json` 的 `autoLaunch`；Windows 启动项（任务管理器 → 启动）是否被禁用 |
| 窗口位置异常/跑到屏幕外 | 删除 `%APPDATA%\EiseChat\window-state.json` 后重启 |
| SmartScreen 拦截 | 「更多信息 → 仍要运行」；这是未签名安装包的正常提示 |
| 覆盖安装后还是旧界面 | 打包前未重新构建 `apps/web`；重新执行 §5 的 1、2 步并核对 exe 时间/SHA256 |
| 版本号不对 | 三端版本未同步修改（§5 版本号规则），安装包名与程序版本都以 `apps/desktop/package.json` 为准 |
| 开发态改代码不生效 | 改的是 `electron/`（主进程/预加载）→ 需要重跑 `npm run dev`；只改 `apps/web` 才会热更新 |

## 9. 已知限制（v1）

- 无自动更新：发新版本需重新下载安装包覆盖安装（不购买签名证书/不做更新服务，AGENTS §2.2）。
- 无离线推送：程序未运行或窗口关闭（关闭到托盘只是隐藏，进程仍在）时，重新打开后靠补拉同步；真正退出后新消息不提醒。
- 免签名安装包：SmartScreen 会提示；杀毒软件偶发误报属正常现象。
- 通知内容只含会话名与消息摘要（由页面传入），不含 token 等敏感信息；聊天内容服务端可见（无 E2EE）。
- 不做 iOS；不做商店上架。

## 10. 相关文档

- 项目介绍与入口：`README.md`
- 硬约束：`AGENTS.md`
- 协议：`protocol/protocol.md`
- Web 端运行与构建：`docs/web.md`
- Android 安装与打包：`docs/mobile.md`
- 服务端部署与下载页：`docs/deploy.md`
- 目录与模块职责：`docs/project-overview.md` §7
