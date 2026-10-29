# EiseChat 项目说明与目录架构（V1.2）

> 本文是**面向开发的项目总览**：项目是什么、代码放哪里、模块怎么分、从哪开始写。
> 唯一现行计划：`docs/internal-plan.md`（V2.3.5，回答"做什么、什么时候做"）；硬约束：`AGENTS.md`（回答"什么不能做"）。
> 优先级：`AGENTS.md` §2 硬规则 > `docs/internal-plan.md` > 本文 > 其他。本文与计划冲突时以计划为准，并回头修本文。
> 本文版本 1.2（2026-10-25）。**M0 建工程骨架以本文 §7 的目录与文件名为准。**

---

## 1. 文档体系与阅读顺序

| 文档 | 角色 | 什么时候读 |
|---|---|---|
| `AGENTS.md` | 仓库级硬约束（不含项目介绍）：许可、架构边界、范围边界、密钥纪律、协议可靠性、工作流、测试与提交规范 | **开工前必读**，每次改代码前对照 |
| `docs/project-overview.md`（本文） | 项目总览 + 文件级目录架构 + 模块职责与依赖方向 | 第一次上手；决定"代码写在哪"时 |
| `docs/internal-plan.md`（本地文档，不入库） | 唯一现行计划：需求、协议、数据模型、客户端/服务端设计、里程碑、成本、风险 | 做某个功能前读对应章节，确认属于哪个里程碑 |
| `README.md` | **项目介绍与入口（项目介绍唯一出处）**：是什么/给谁用、规模与形态、技术栈概览、文档索引、里程碑、重要边界 | 第一次了解项目；对外介绍 |
| `protocol/protocol.md` | 帧定义唯一来源（M0 创建，先改文档再改实现） | 改协议、写 WS 代码时 |
| `protocol/schema.sql` | 表结构唯一来源（M0 创建，与 EF 迁移同步） | 改库表、写迁移时 |
| `docs/deploy.md` | 云服务器部署、HTTPS、升级、备份恢复（M4 产出） | 部署/排障时 |
| `docs/update-deploy.md` | 改代码后的自行构建/部署/升级与回滚步骤（Docker 一键部署） | 发布新版本、改完代码上线时 |
| `docs/web.md` | Web 端（`apps/web`）开发运行、构建、生产部署（dist → wwwroot）、验收与排障 | 运行/构建/部署 Web 时 |
| `docs/desktop.md` | Windows 桌面端（`apps/desktop`）安装、托盘/通知/设置行为、开发联调、NSIS 打包 | 打包或排查桌面端时 |
| `docs/usage.md` | 安装与使用说明（M6 交付给最终用户） | 内测发版时 |
| `docs/ui/` | UI 改造参考图与改造前后基线截图（**本地资产不入库**；仅 `README.md` 入库） | UI 收口后对照参考与展示 |

> **入库范围（2026-10-26 起）：** 说明类文档入库；计划、审查/验收报告、任务清单、截图与参考图等过程文件只保留本地（`AGENTS.md` §2.11/§4）。

**推荐阅读顺序**：`README.md`（知道是什么）→ `AGENTS.md`（知道不能做什么）→ 本文 §2~§6（展开细节）→ 本文 §7~§10（知道代码放哪）→ 计划 §4（协议）+ §7（服务端）+ §5（客户端）→ 开始写。

**分工原则**：`README.md` 讲「是什么、给谁用」，本文讲「怎么理解、代码放哪」，计划讲「做什么、何时做、怎么验收」，`AGENTS.md` 只讲「必须遵守/禁止」；**项目介绍以 `README.md` 为准**，本文只做展开，不重复定义约束。

---

## 2. 项目概述

- **一句话**：EiseChat 是公司内部使用的**自研轻量 IM**：一台 ASP.NET Core 单进程服务端 + 一套 Vue 3 前端核心 + Windows（Electron）与 Android（Capacitor）安装包，服务 10~100 个账号、峰值 ≤100 在线。
- **为什么自己做**：不引第三方 IM（协议自研、可控可改）、零软件授权费用、单进程易部署易备份、出 Windows 安装包和 Android APK 给同事直接装。
- **交付物**：`EiseChat-Setup-x.y.z.exe`（Windows 安装包）、`EiseChat-x.y.z.apk`（Android 侧载包）、云服务器上的服务端（含 Web 端与 `GET /download` 下载页）。
- **技术骨架**：Caddy 自动 HTTPS → Kestrel（`127.0.0.1:6580`）→ SQLite（WAL）+ 本地文件目录；消息走自研 JSON-over-WebSocket，登录/历史/文件走 REST；服务端是**唯一事实源**，客户端 IndexedDB 只是缓存。
- **本期不做**：iPhone/iOS 代码（只出方案）、音视频、朋友圈、AI、机器人、E2EE、ICP/等保/短信/厂商推送（见 `AGENTS.md` §2.5）。

---

## 3. 用户、规模与约束

| 项 | 取值 | 说明 |
|---|---|---|
| 账号数 | 10~100 | 公司/团队内部，不面向公众 |
| 峰值在线 | ≤100 | WebSocket 长连接数（单人单活动连接） |
| 部署 | 1 台云服务器 2C4G / 40~80GB SSD / Ubuntu 22.04 或 24.04 | 单机单进程，systemd 自启 |
| 网络 | 公网域名 + HTTPS（WSS） | Caddy 自动申请/续期证书；开发期先用占位域名 |
| 数据量 | SQLite 单文件 + 文件目录 | 文件默认上限 50MB/个（可配）；单群默认 ≤100 人 |
| 运维 | 1 人 | 自启 + 自动 HTTPS + 每日备份到对象存储/异机 |
| 成本 | 软件 0 元；服务器约 60~150 元/月；域名已有 | 不买授权、签名证书、Apple 开发者账号 |

**扩展路径**：规模若超过约 500 在线，再考虑拆 PostgreSQL/分进程；协议与表结构预留了边界（`protocolVersion`、`last_read_seq` 按设备可扩展），协议本身不变。

---

## 4. 功能范围

### 4.1 v1（本期，M0~M6）

| 模块 | 内容 |
|---|---|
| 账号 | 邮箱验证码注册/登录（6 位、10 分钟、限流）；可选密码登录；昵称（`avatar_file_id` 字段预留） |
| 会话 | 单聊（`direct_key` 唯一去重）、群聊（建群、邀请、退群、移除）；会话列表 + 未读数 |
| 消息 | 文本、图片、文件；发送状态（发送中/已发送/失败可重发）；历史分页；正在输入；单聊已读 |
| 文件 | 上传（≤50MB，MIME/扩展名校验）、按会话成员鉴权下载、客户端图片压缩（最长边 1280px、约 ≤300KB） |
| 通知 | 应用内提示音 + 未读角标（Web/桌面托盘）；**无系统级推送** |
| 外壳 | Windows NSIS 安装包；Android APK（侧载） |
| 管理 | 建号/邀请码、重置密码、禁用账号、注册开关与邮箱域名白名单、统计、手动备份 |
| 分发 | 服务端 `GET /download` 下载页（exe/apk + 二维码 + 安装说明） |

### 4.2 P2（明确延后，不提前做）

语音消息、消息搜索、消息撤回、多设备登录、群已读详情、自动更新、Android 前台服务保活。

### 4.3 明确不做

第三方 IM/SDK、iOS 代码与 TestFlight、音视频通话、朋友圈、AI/机器人、E2EE、ICP/等保/实名/内容审核/短信/付费推送厂商、Docker/K8s/Redis/Kafka/MongoDB/PostgreSQL/MinIO/Nginx/SignalR、微服务拆分。

---

## 5. 技术栈

| 层 | 选型 | 关键点 |
|---|---|---|
| 服务端 | ASP.NET Core（.NET 8/10 LTS）+ Kestrel | 单进程同时托管 REST、WebSocket、静态前端 |
| 实时 | 自研 JSON-over-WebSocket（一行一帧、带 `type`） | DTO 放 `Chat.Protocol`，可生成 TS 类型；不引 SignalR |
| 数据 | SQLite（WAL）+ EF Core | `/data/chat.db`；迁移在 `server/Chat.Server/Data/Migrations` |
| 认证 | 邮箱验证码 + 可选密码；JWT HS256（默认 7 天） | 验证码只存哈希；密码 PBKDF2-SHA256 ≥100,000 或 Argon2id |
| 邮件 | SMTP（企业邮箱/QQ 授权码）或免费邮件 API（Brevo/Resend 等） | `Development` 输出到控制台（不写日志文件）+ 固定测试码 |
| 前端 | Vue 3 + TypeScript + Vite + Pinia + 轻量 UI | 核心 `core/ws.ts`、`core/sync.ts`；IndexedDB 缓存 |
| Windows 壳 | Electron + electron-builder（NSIS） | `appId=com.eisechat.desktop`；免签名，SmartScreen 提示选"仍要运行" |
| Android 壳 | Capacitor（Web 资源打进 APK，不依赖在线加载） | 最低 API 26；自签名 keystore 放 `infra/keys/`（gitignore，另行离线备份） |
| 部署 | Ubuntu + Caddy 自动 HTTPS + systemd | 6580 只监听 `127.0.0.1`；备份到对象存储/异机，保留最近 30 份 |
| 测试 | xUnit（服务端）+ Vitest（前端核心）单测随工程 | 临时脚本/验收数据统一放 `docs/test/`（不入库）；不搞重型测试体系 |

**依赖许可**：只允许 MIT / Apache-2.0 / BSD / ISC；新增 npm/NuGet 依赖前检查 LICENSE，禁止 GPL/AGPL/SSPL/BUSL。不确定先问用户。

---

## 6. 总体架构

```text
┌──────────────── 客户端（同一套 Vue 代码） ────────────────┐
│  Windows（Electron 壳，.exe）      Android（Capacitor 壳，.apk）│
│  iPhone（PWA）—— 仅方案，本期不实现，不打包不验收            │
└──────────────────────────┬──────────────────────────────┘
                           │ HTTPS / WSS（公网域名）
                           ▼
              ┌────────────────────────────┐
              │ Caddy 443 → 127.0.0.1:6580 │  TLS / gzip / 静态缓存
              └──────────────┬─────────────┘
                             ▼
        ┌────────────────────────────────────────┐
        │ Chat.Server（ASP.NET Core 单进程）        │
        │  REST /api/*                            │
        │  WebSocket /ws（自研协议）                │
        │  静态前端 wwwroot（含 /download 页）       │
        │  后台任务（孤儿文件清理、备份触发、统计）     │
        │  邮件发送（SMTP / HTTP API）→ 用户邮箱      │
        └─────────────────┬──────────────────────┘
                          ▼
        ┌────────────────────────────────────────┐
        │ SQLite /data/chat.db（WAL）              │
        │ 文件 /data/files/yyyyMM/{fileId}{ext}    │
        │ 备份 /data/backup/（同步到对象存储/异机）   │
        └────────────────────────────────────────┘
```

**三类流量**：静态资源与 `/download` → 服务端 `wwwroot`；登录/历史/文件/管理 → REST `/api/*`；实时消息 → WebSocket `/ws`（首帧 `auth` 认证）。

**为什么单进程够**：≤100 个 WS 连接 + SQLite 单写 + 内存连接表，远未到瓶颈；真正成本是文件流量与备份，用目录和定时任务解决。少一个组件就少一处故障点与备份项。

**数据归属**：服务端 SQLite + `/data/files` 是唯一事实源；客户端 IndexedDB 只缓存会话与最近消息，刷新/重装后以服务端补拉为准。

---

## 7. 目录架构（文件级规划）

### 7.1 顶层结构

```text
EriseChat/                                  ← 仓库根（D:\Code\EriseChat）
├── README.md                               # 速览、文档索引、里程碑表
├── AGENTS.md                               # 硬约束（AI/开发代理必读）
├── .gitignore                              # 不提交清单（构建产物/数据/密钥）
├── .editorconfig                           # 缩进/换行/编码统一（M0 建）
├── docker-compose.yml                      # Docker 一键部署入口（2.0 新增）
├── .dockerignore                           # 镜像构建上下文排除（2.0 新增）
│
├── docs/                                   # 文档（计划、说明、部署；test/ 测试脚本不入库）
├── protocol/                               # 协议与库表唯一来源
├── apps/                                   # 三个客户端工程（web / desktop / mobile）
├── server/                                 # 服务端解决方案（三个 .NET 工程）
├── infra/                                  # Docker 部署（docker/）、安装、备份、邮件、密钥
└── data/                                   # 本地开发运行时数据（gitignore，不入库）
```

### 7.2 完整目录树

```text
EriseChat/
├── README.md
├── AGENTS.md
├── .gitignore
├── .editorconfig                           # M0 建：UTF-8、LF、4 空格（C#）/2 空格（TS）
│
├── docs/
│   ├── project-overview.md                 # ← 本文
│   ├── internal-plan.md                    # 唯一现行计划（V2.3.5）
│   ├── deploy.md                           # M4：部署/HTTPS/升级/备份恢复
│   ├── web.md                              # Web 端运行/构建/部署/验收（说明类，入库）
│   ├── desktop.md                          # Windows 端安装/打包/联调（说明类，入库）
│   ├── usage.md                            # M6：安装使用说明（给最终用户）
│   └── test/                               # 测试与临时脚本（gitignore，不入库；子目录需申请）
│
├── protocol/
│   ├── protocol.md                         # 帧定义 + 字段 + 示例（唯一来源）
│   ├── schema.sql                          # 10 张表 DDL（与 EF 迁移保持同步）
│   └── protocol.schema.json                # 帧 JSON Schema（可选：校验/生成 TS 类型）
│
├── apps/
│   ├── web/                                # Vue 3 客户端核心（唯一业务前端）
│   │   ├── package.json
│   │   ├── vite.config.ts                  # 别名、dev proxy、构建目标
│   │   ├── tsconfig.json
│   │   ├── tsconfig.node.json
│   │   ├── index.html
│   │   ├── .env.example                    # 默认服务器地址等（不含任何密钥）
│   │   ├── public/                         # favicon、图标、静态资源
│   │   └── src/
│   │       ├── main.ts
│   │       ├── App.vue
│   │       ├── router/index.ts             # 路由 + 登录守卫
│   │       ├── core/                       # 与 UI 无关的基础设施
│   │       │   ├── ws.ts                   # 连接状态机、帧编解码、心跳、重连、发送队列、reqId 匹配
│   │       │   ├── sync.ts                 # lastSeq 补拉、去重排序、缺口检测
│   │       │   ├── http.ts                 # REST 封装、JWT 注入、统一错误码
│   │       │   ├── db.ts                   # IndexedDB（会话 + 最近消息）
│   │       │   └── config.ts               # 服务器地址、protocolVersion、超时/重试常量
│   │       ├── api/                        # REST 薄封装（与 server/Api 一一对应）
│   │       │   ├── auth.ts
│   │       │   ├── users.ts
│   │       │   ├── conversations.ts
│   │       │   ├── messages.ts
│   │       │   ├── files.ts
│   │       │   └── admin.ts
│   │       ├── stores/                     # Pinia
│   │       │   ├── auth.ts
│   │       │   ├── conversations.ts
│   │       │   ├── messages.ts
│   │       │   ├── contacts.ts
│   │       │   └── settings.ts
│   │       ├── pages/
│   │       │   ├── Login.vue               # 登录/注册（含服务器地址配置）
│   │       │   ├── Chat.vue                # 会话列表 + 聊天
│   │       │   ├── Contacts.vue            # 通讯录 / 建群
│   │       │   ├── Settings.vue            # 我的 / 设置
│   │       │   ├── Admin.vue               # 管理页（admin 角色可见）
│   │       │   └── Download.vue            # 下载页（PWA 场景；安装包走服务端 /download）
│   │       ├── components/
│   │       │   ├── ConversationItem.vue
│   │       │   ├── MessageList.vue
│   │       │   ├── MessageBubble.vue
│   │       │   ├── SendBox.vue
│   │       │   ├── Avatar.vue
│   │       │   ├── ConnectionBanner.vue    # 断网/重连横幅
│   │       │   └── EmptyState.vue
│   │       ├── types/frames.d.ts           # 协议类型（由 Chat.Protocol 生成或手工镜像）
│   │       └── styles/
│   │           ├── variables.css
│   │           └── global.css
│   │
│   ├── desktop/                            # Electron 壳：只做窗口/托盘/通知/打包，不写业务
│   │   ├── package.json
│   │   ├── electron/
│   │   │   ├── main.ts                     # 窗口、托盘、单实例锁、开机自启、加载 web 产物
│   │   │   ├── preload.ts                  # contextBridge 暴露白名单能力
│   │   │   └── ipc.ts                      # 未读角标、系统通知等 IPC
│   │   ├── electron-builder.yml            # appId、NSIS、产物名、图标
│   │   └── build/icon.ico
│   │
│   └── mobile/                             # Capacitor 壳：只做容器与打包，不写业务
│       ├── package.json
│       ├── capacitor.config.ts             # appId、webDir、服务器地址策略
│       └── android/                        # Capacitor 生成的 Gradle 工程（入库；keystore 不进）
│
├── server/
│   ├── Chat.Server.sln                     # 三个工程的解决方案
│   ├── Chat.Protocol/                      # 帧 DTO（唯一契约，前端类型从这里生成）
│   │   ├── Chat.Protocol.csproj
│   │   ├── Frames/                         # AuthFrame、MessageSendFrame、SyncRequestFrame…
│   │   └── Enums.cs                        # MessageType、ErrorCode、Role 等
│   ├── Chat.Server/                        # ASP.NET Core 单进程
│   │   ├── Chat.Server.csproj
│   │   ├── Program.cs                      # 启动、DI、Kestrel、静态文件、自动迁移
│   │   ├── appsettings.json                # 非敏感配置（端口/路径/上限/注册策略）
│   │   ├── appsettings.Development.json    # 本地开发（mock 邮件、详细错误）
│   │   ├── Api/                            # REST 边界层
│   │   ├── Realtime/                       # WS 连接与帧分发
│   │   ├── Modules/                        # 业务模块（事务在这里）
│   │   ├── Data/                           # EF Core：DbContext、迁移、种子
│   │   ├── Common/                         # 横切：配置、日志、错误、时间
│   │   ├── BackgroundJobs/                 # 后台任务
│   │   └── wwwroot/                        # 前端构建产物 + /download（构建生成，不手改）
│   └── Chat.Server.Tests/                  # xUnit
│
├── infra/
│   ├── docker/                             # Docker 一键部署（2.0 默认）
│   │   ├── Dockerfile                      # Node 构建 Web → .NET 发布 → aspnet 运行时
│   │   └── deploy.sh                       # 服务器一键部署/升级（生成 .env、构建、健康检查）
│   ├── install.sh                          # 旧：Linux 安装/升级（systemd，备选）
│   ├── install.ps1                         # Windows 可选安装（sc.exe 服务）
│   ├── backup.sh / backup.ps1              # 备份与恢复（SQLite 在线备份 + files/）
│   ├── eisechat.service                    # systemd 单元（旧方案备选）
│   ├── caddy/Caddyfile                     # 公网 HTTPS 反代 127.0.0.1:6580（宿主侧）
│   ├── email/                              # 邮件通道配置示例 + 自测脚本
│   └── keys/                               # Android keystore（gitignore + 离线备份）
└── data/                                   # 本地开发运行时数据（gitignore）
    ├── chat.db
    └── files/
```

### 7.3 目录职责速查

| 目录 | 放什么 | 不放什么 |
|---|---|---|
| `apps/web/src/core/` | WS 状态机、补拉同步、HTTP 封装、IndexedDB、常量 | 任何 Vue 组件、页面跳转逻辑 |
| `apps/web/src/stores/` | 跨页面共享状态与动作 | 直接 `new WebSocket()`、直接 `fetch` 裸调 |
| `apps/web/src/api/` | 与 REST 一一对应的薄函数 | 业务判断、UI 状态 |
| `apps/desktop/`、`apps/mobile/` | 窗口、托盘、通知、打包配置、容器配置 | 聊天业务逻辑（一律在 `apps/web`） |
| `server/Chat.Server/Api/` | 路由、鉴权、参数绑定与校验、DTO ↔ 模型映射 | SQL、事务、业务规则 |
| `server/Chat.Server/Modules/` | 业务规则、事务、seq 分配、幂等、权限判断 | HTTP/WS 细节、JSON 序列化 |
| `server/Chat.Server/Realtime/` | `/ws` 握手、连接表、帧编解码、帧分发、心跳 | 消息落库逻辑（调 `Modules/Messages`） |
| `server/Chat.Server/Data/` | DbContext、实体配置、迁移、种子数据 | 业务规则 |
| `server/Chat.Server/Common/` | 配置类、日志、统一错误响应、时间/ID 工具 | 某个具体业务的代码 |
| `server/Chat.Server/BackgroundJobs/` | 孤儿文件清理、备份触发、统计 | 与请求强相关的逻辑 |
| `server/Chat.Protocol/` | 帧 DTO、枚举、错误码（服务端与前端共享契约） | 数据库实体、业务逻辑 |
| `protocol/` | 协议文档、DDL、JSON Schema | 可执行代码 |
| `infra/` | Docker 部署（`infra/docker/`）、脚本、Caddy 配置、systemd（备选）、keystore（不进 git） | 应用源码 |
| `docs/test/` | 临时测试脚本、验证用例、测试数据与输出（gitignore，不入库；建子目录需申请） | xUnit / Vitest 单测（分别在 `server/Chat.Server.Tests`、`apps/web`） |

### 7.4 不建 / 不提交

- **不建**：K8s 清单、多容器编排、iOS/Xcode 工程、微服务目录、`apps/server`（服务端不放在 apps 下）、根级 `tests/`（测试脚本统一放 `docs/test/`）。Docker 部署文件只在 `infra/docker/`（2.0 新增）。
- **不提交**：`node_modules/`、`bin/`、`obj/`、`dist/`、`.electron-builder/`、`*.db`、`data/`、`logs/`、`infra/keys/`、`secrets.json`、`*.keystore`、`*.apk`、`*.exe`（完整清单见 `.gitignore`）。
- **构建生成、不手改**：`server/Chat.Server/wwwroot/`（前端产物）、`apps/mobile/android/app/src/main/assets/public/`（Capacitor 同步产物）。

### 7.5 服务端 `Chat.Server/` 内部文件

```text
Chat.Server/
├── Program.cs
├── appsettings.json
├── appsettings.Development.json
├── Api/
│   ├── AuthEndpoints.cs          # 邮箱验证码、注册、登录（code/password）
│   ├── UserEndpoints.cs          # /api/me、搜索用户
│   ├── ConversationEndpoints.cs  # 会话/群/成员
│   ├── MessageEndpoints.cs       # 历史分页（WS 之外兜底）
│   ├── FileEndpoints.cs          # 上传/下载（成员鉴权）
│   ├── AdminEndpoints.cs         # /api/admin/*
│   └── DownloadEndpoints.cs      # /download 页
├── Realtime/
│   ├── WsMiddleware.cs           # /ws 握手 + 连接生命周期
│   ├── ConnectionManager.cs      # userId → connections（内存在线表、踢旧连接）
│   ├── FrameCodec.cs             # 帧编解码（DTO 在 Chat.Protocol）
│   ├── MessageHub.cs             # 收帧分发：send/ack/sync/read/typing
│   └── HeartbeatService.cs
├── Modules/
│   ├── Auth/                     # 验证码、JWT、限流、登录锁定
│   ├── Email/                    # IEmailSender、SMTP/HTTP API、模板、Mock
│   ├── Users/
│   ├── Conversations/            # 单聊去重、群成员、系统消息
│   ├── Messages/                 # 发送事务、seq、幂等、同步查询
│   ├── Files/                    # 上传/下载鉴权、路径安全、孤儿清理
│   └── Admin/                    # 建号、禁用、审计
├── Data/
│   ├── ChatDbContext.cs
│   ├── Migrations/
│   └── Seed.cs                   # 初始化管理员
├── Common/                       # 配置、日志、错误处理中间件、时间工具
├── BackgroundJobs/               # 清理、备份触发、统计
└── wwwroot/                      # 前端产物 + download/index.html
```

### 7.6 创建顺序（M0 照此建骨架）

1. 根目录：`.editorconfig` 补齐；确认 `.gitignore` 生效（`git status` 干净）。
2. `protocol/`：先把 `protocol.md`、`schema.sql` 从计划 §4 固化下来（**文档先行**）。
3. `server/`：`dotnet new sln` + `Chat.Protocol`、`Chat.Server`、`Chat.Server.Tests` 三个工程并互相引用；`Program.cs` 跑通 `/api/health`。
4. `apps/web/`：Vite + Vue 3 + TS + Pinia 骨架，`core/ws.ts`、`core/sync.ts` 先写接口与状态机骨架。
5. `apps/desktop/`、`apps/mobile/`：能加载 `apps/web/dist` 即可（M4/M5 再打磨）。
6. `infra/`、`docs/deploy.md` 等按里程碑逐步补，不提前铺；测试脚本随手放 `docs/test/`（不入库），不新建 `tests/`。

---

## 8. 服务端模块与分层

| 层 | 目录 | 职责 | 依赖 |
|---|---|---|---|
| 边界层 | `Api/` | HTTP 路由、鉴权、参数校验、DTO 映射、错误码 | → `Modules`、`Chat.Protocol`、`Common` |
| 实时层 | `Realtime/` | `/ws` 握手认证、连接表、帧编解码、帧分发、心跳、踢旧连接 | → `Modules`、`Chat.Protocol`、`Common` |
| 业务层 | `Modules/` | 业务规则、事务、`seq` 分配、幂等、权限、限流 | → `Data`、`Chat.Protocol`、`Common` |
| 数据层 | `Data/` | EF Core `DbContext`、实体配置、迁移、种子 | → 仅 EF Core |
| 横切 | `Common/` | 配置、日志、错误处理、时间/ID 工具 | 被各层引用 |
| 后台 | `BackgroundJobs/` | 孤儿文件清理、备份触发、统计 | → `Modules`/`Data` |

**关键实现约定**：

- `MessageHub` 发送流程：校验成员身份 → 事务内 `seq = max(seq)+1` → 插入 `messages` → 更新会话 `last_seq` → 回 `message.ack` → 向在线成员广播 `message.new`。重复发送由 `(conversation_id, client_msg_id)` 唯一约束短路，直接回 ack。
- `ConnectionManager`：`ConcurrentDictionary<Guid, List<WebSocket>>`；新登录踢旧连接（发 `kicked`）；断线不清数据，靠 `seq` 补拉。
- `EmailCodeService`：验证码只存哈希、10 分钟有效、失败 5 次作废、同邮箱/同 IP 限流；`Development` 把验证码输出到控制台（不写日志文件）。
- 所有时间统一 **UTC 毫秒**存储，前端本地化显示。

---

## 9. 前端模块与分层

```text
pages/ ──► components/ ──► stores/ ──► api/  (REST)
                              │
                              └──────► core/ (ws.ts / sync.ts / db.ts / config.ts)
```

| 层 | 职责 | 规则 |
|---|---|---|
| `pages/` | 页面编排、路由参数 | 不直接调 `fetch`/`WebSocket` |
| `components/` | 纯展示 + 局部交互 | 不写全局状态 |
| `stores/` | 共享状态与动作（Pinia） | 通过 `core/` 与 `api/` 访问服务端 |
| `api/` | REST 薄封装 | 只做请求与类型映射 |
| `core/ws.ts` | 连接状态机（connecting/open/reconnecting/closed）、心跳、重连、发送队列、`reqId` 匹配 | 全局唯一连接入口 |
| `core/sync.ts` | `lastSeq` 补拉、去重排序、缺口检测 | 启动/重连/回前台触发 |
| `core/db.ts` | IndexedDB 缓存 | 只缓存，不作事实源 |

**体验细节**：未读角标（页面标题 + 桌面托盘）、新消息提示音（可关）、断网顶部横幅、发送失败可重试、粘贴图片直接发送（可选）。

---

## 10. 依赖方向与边界规则

- **客户端**：`apps/desktop`、`apps/mobile` 只消费 `apps/web` 的构建产物，**不得反向依赖**，也不得写业务逻辑。
- **服务端**：`Api` / `Realtime` → `Modules` → `Data`；`Common` 与 `Chat.Protocol` 是公共依赖；**禁止** `Modules` 之间横向引用造成环（共享逻辑下沉到 `Modules` 公共类或 `Common`）。
- **协议契约**：帧 DTO 只在 `Chat.Protocol` 定义；`Realtime/FrameCodec.cs` 只做编解码；前端 `types/frames.d.ts` 从 DTO 生成或镜像，不手写第二份契约。
- **库表**：DDL 唯一来源 `protocol/schema.sql`，EF 迁移与它同步维护；改表先改 SQL 与文档。
- **禁止**：在 `Api/` 写 SQL 与事务；在 `Data/` 写业务规则；前端绕过 `core/ws.ts` 直连 WebSocket；把业务放进 Electron 主进程/Android 原生层。

---

## 11. 关键设计约定

### 11.1 协议与消息可靠性（验收逐条测）

帧格式：**一行一帧 JSON**，所有帧带 `type`（点分隔，如 `message.send`）；请求可带 `reqId`，响应回传；`protocolVersion=1`。

| type | 方向 | 用途 |
|---|---|---|
| `auth` / `hello` | C→S / S→C | 首帧认证 / 认证成功 |
| `ping` / `pong` | 双向 | 心跳（客户端 25s，30s 无响应判断线） |
| `message.send` / `message.ack` / `message.new` | 双向 | 发消息（带 `clientMsgId`）/ 落库回执 / 广播 |
| `sync.request` / `sync.response` | C→S / S→C | 补拉（每会话每批 ≤200 条、`hasMore` 继续） |
| `conv.list` / `conv.updated` | 双向 | 会话列表变更 |
| `read.update` / `read.updated` | 双向 | 已读游标（单聊广播） |
| `typing` / `presence` / `kicked` / `error` | 双向 | 输入中、在线状态、踢下线、统一错误 |

必须满足（与 `AGENTS.md` §5 一致）：

1. 会话内 `seq` 严格 +1（事务内 `max(seq)+1`），不跳号不改号。
2. `(conversation_id, client_msg_id)` 唯一，重复发送只回 ack 不重复落库。
3. 发送队列：发送中 → ack 后已发送；2s/5s/10s 重试 3 次仍失败标"失败，可重发"。
4. 接收按 `messageId` 去重、按 `seq` 排序；`seq > lastSeq+1` 立即 `sync.request`。
5. 启动/重连/回前台补拉，`hasMore=true` 继续直到追平。
6. 未读 = 会话最新 `seq` − 我的 `last_read_seq`；滚到底部上报已读。
7. 单活动连接：新登录踢旧（`kicked`），断线不删数据。
8. 重连退避 1/2/5/10/30s + 随机抖动。

### 11.2 数据与时间

- 表结构 10 张：`users`、`email_codes`、`sessions`、`conversations`、`conversation_members`、`messages`、`files`、`invites`、`admin_audit_logs`、`app_settings`（字段见计划 §4.4 与 `protocol/schema.sql`）。
- 主键 GUID（`users.id`、`conversations.id` 等）；`clientMsgId` 用客户端 UUID。
- 消息类型：`text` / `image` / `file` / `system`；内容存 `content_json`。
- 时间列统一 UTC 毫秒（`created_at` 等），前端本地化。

### 11.3 安全

- 验证码只存哈希；密码 PBKDF2-SHA256（≥100,000 次、随机盐）或 Argon2id。
- JWT HS256 默认 7 天；密钥首次启动生成写入 `secrets.json`。
- 文件下载只走 `GET /api/files/{id}`，校验请求者是引用该文件的会话成员；不暴露磁盘路径。
- 日志禁止 token/密码/验证码/邮件明文；错误返回 `traceId` 便于排查。
- 明确不做 E2EE（内部信任模型，使用说明写清楚）。

---

## 12. 配置、密钥与环境

| 文件 | 提交 | 内容 |
|---|---|---|
| `appsettings.json` | ✅ | 端口、数据库/文件路径、上传上限、注册开关、邮箱白名单、JWT 过期、备份目标 |
| `appsettings.Development.json` | ✅ | 本地开发：mock 邮件、详细错误、固定测试码开关 |
| `secrets.json` | ❌ gitignore | JWT 密钥、SMTP 凭据等（首次启动生成，不存明文密码/验证码） |
| `.env.example`（web） | ✅ | 默认服务器地址等示例；真实 `.env` 不入库 |

- **环境**：`Development`（本机跑、控制台输出验证码、固定测试码）/ `Production`（`wwwroot` 静态前端、SMTP 必配、错误不暴露细节）。
- **逻辑数据目录与部署路径**：

  | 场景 | 逻辑路径 | 实际路径 |
  |---|---|---|
  | 部署（Linux） | `/data/chat.db`、`/data/files/` | `/var/lib/eisechat/chat.db`、`/var/lib/eisechat/files/` |
  | 部署（Windows 可选） | 同上 | `C:\ChatServer\data\...` |
  | 本地开发 | `data/` | 仓库根 `data/`（gitignore，可配置） |

  `appsettings.json` 里的 `DbPath` / `FileRoot` 指向实际路径；文档里提到 `/data` 时指挥逻辑目录。
- **敏感值纪律**：初始管理员密码只在首次启动时输出到控制台一次（不写日志文件），登录后立即修改；`Development` 下的验证码同样只出控制台。

---

## 13. 构建、打包与交付

### 13.1 常用命令（与 `README.md`、`AGENTS.md` §8 保持一致）

```bash
dotnet build server/Chat.Server.sln              # 服务端构建
dotnet test server/Chat.Server.Tests             # 服务端单测
cd apps/web && npm install && npm run dev        # Web 开发
cd apps/web && npm run build                     # Web 构建 → dist/
npm run desktop:dev && npm run desktop:build     # Electron 开发 / 打包（脚本名 M0 定）
npm run mobile:sync && cd apps/mobile/android && ./gradlew assembleRelease   # APK
sudo bash infra/docker/deploy.sh && infra/backup.sh   # 服务器 Docker 部署 / 备份
```

### 13.2 构建流水线（谁产出什么）

| 产物 | 来源 | 去向 |
|---|---|---|
| `apps/web/dist/` | `npm run build` | ① 复制到 `server/Chat.Server/wwwroot/`；② 桌面壳加载；③ `npx cap sync` 进 APK |
| `chat-server-x.y.z.zip` | `dotnet publish -c Release` + `wwwroot` | 服务器 `/opt/eisechat-server/` |
| `EiseChat-Setup-x.y.z.exe` | `electron-builder`（NSIS） | 下载页 / 内部分发，不入库 |
| `EiseChat-x.y.z.apk` | `gradlew assembleRelease` + 自签名 | 下载页 / 侧载，不入库 |

**版本号同步**：服务端、桌面、APK 使用同一 `主.次.修订`；`protocolVersion` 独立（v1 = 1）；发版前检查三者一致。

### 13.3 发版检查清单

- [ ] `dotnet build` + `dotnet test` + `npm run build` 通过（记录命令与结果）。
- [ ] 协议/表结构改动已先更新 `protocol/` 与计划文档，且只做了向后兼容增量。
- [ ] 版本号三处一致；`GET /api/health` 返回预期版本。
- [ ] 升级前已备份（SQLite + files/）；升级步骤按 `docs/deploy.md`。
- [ ] 双客户端实测：发送 → 断线 → 重连补拉无丢无重。
- [ ] 安装包可安装运行；覆盖安装/卸载不影响服务端数据。

---

## 14. 版本与兼容策略

- **版本号**：`主.次.修订`（如 `1.0.0`）；服务端、Windows、Android 同步；协议版本单独 `protocolVersion=1`。
- **协议/库表**：只允许向后兼容的增量（新增可选字段、新 `type`、新表/新列可以；改语义、删字段不允许）。破坏性变更 → 升 `protocolVersion` + 旧客户端提示升级。
- **服务端兼容**：向后兼容至少一个旧客户端版本；客户端可手动覆盖安装（数据在服务端，本地无需迁移）。
- **升级顺序**：停服务 → 备份 → 替换程序（保留数据目录）→ 启动 → `/api/health` 校验 → 客户端覆盖安装。
- **数据库迁移**：启动时自动执行 EF 迁移；迁移脚本必须可重复执行（幂等），上线前在备份副本上演练。

---

## 15. 里程碑与目录产出对照

| 里程碑 | 周次 | 主要产出 | 对应目录/文件 |
|---|---|---|---|
| M0 | 3~4 天 | 骨架、协议与 DDL 固化、健康检查、最小可运行构建 | `server/*` 三工程、`protocol/protocol.md`、`protocol/schema.sql`、`apps/web` 骨架、`.editorconfig` |
| M1 | 第 1~1.5 周 | 服务端核心：邮箱账号、JWT、WS、`message.send/ack`、`sync`、迁移、单测 | `server/Chat.Server/{Api,Realtime,Modules,Data}`、`Chat.Server.Tests` |
| M2 | 第 2 周 | Web 登录注册 + 会话列表 + 单聊（发送队列、补拉） | `apps/web/src/{core,api,stores,pages}` |
| M3 | 第 3 周 | 群聊、图片/文件、已读、通讯录与管理页 | `apps/web/src/{pages,components}`、`server/.../Files` |
| M4 | 第 4 周 | Windows 安装包 + 云服务器部署 + 下载页 | `apps/desktop/`、`server/.../wwwroot`、`infra/`、`docs/deploy.md` |
| M5 | 第 5 周 | Android APK（Capacitor + 自签名） | `apps/mobile/`、`infra/keys/`（gitignore） |
| M6 | 第 6~7 周 | 100 连接稳定性、备份恢复演练、10~20 人内测 | `docs/test/`（不入库）、`docs/usage.md`、发版检查清单 |

> 实现进度（2026-10-26）：M0~M5 已完成（M4：Windows 安装包 1.0.0、Electron 托盘/通知/窗口记忆/开机自启、`GET /download` 下载页与安装包分发、`infra/` 部署脚本与 `docs/deploy.md`；M5：Android APK 1.0.0（`com.eisechat.mobile`、minSdk 26、v2 自签名）已产出并可从下载页下载、图标/启动页、`infra/build-apk.sh` 与 `docs/mobile.md`），M6（稳定性与恢复演练）未开始；详细状态见 `docs/internal-plan.md` §13。

P2 待办（语音消息、搜索、撤回、多设备、群已读、自动更新、Android 前台保活）**不提前做**，先保证 v1 闭环。

---

## 16. 质量约定与验收

- **测试分层**：`server/Chat.Server.Tests`（xUnit：协议、seq、幂等、同步、鉴权、验证码）；`apps/web`（Vitest：`core/ws.ts`、`core/sync.ts` 纯逻辑）；`docs/test/`（M6 并发/稳定性脚本，不入库）；真机双客户端手工验收。
- **消息链路验收口径**：seq 连续、幂等重发不重复、断线重连补拉无空洞无重复、未读数正确、踢下线行为正确。
- **完成定义（DoD）**：见 `AGENTS.md` §9；没跑过的测试不得声称通过。
- **文档同步**：受影响文档（`README.md`、`protocol/`、部署说明）必须更新；计划级变更追加到计划附录 D 并告知用户。
- **默认不跑测试**：改完先做代码级逻辑验证（阅读代码、`grep` 引用、类型与契约自检）；确需测试时按 `AGENTS.md` §11 分批跑最小范围，超过约 2 分钟或全量/压测/e2e 先向用户申请；零用例不算通过。
- **交付说明**：每个任务写清修改文件、修改原因、验证方式、测试结果、剩余风险。
- **提交信息**：`类型：[编号]-简述`（中文），类型与格式见 `AGENTS.md` §11.6；未经用户明确要求不执行 `git push`/`git reset --hard`/`git clean -fd`。

---

## 17. 术语表

| 术语 | 含义 |
|---|---|
| `seq` | 会话内消息单调序号，严格 +1 |
| `lastSeq` | 客户端某会话已收到的最大 `seq`，补拉依据 |
| `last_read_seq` | 成员已读游标，未读 = 最新 `seq` − `last_read_seq` |
| `clientMsgId` | 客户端生成的 UUID，用于幂等发送 |
| `direct_key` | 单聊去重键：两用户 ID 排序拼接 |
| `protocolVersion` | 协议版本（v1 = 1），破坏性变更时升级 |
| `kicked` | 新登录踢旧连接时给旧连接的帧 |
| 补拉 | 用 `sync.request` 从服务端拉取缺失消息 |
| WAL | SQLite 预写日志模式，提升并发读写与崩溃恢复 |
| NSIS | Windows 安装包制作工具（electron-builder 内置） |
| Capacitor | 把 Web 资源打进原生 App 的容器框架 |
| keystore | Android 签名密钥库（`infra/keys/`，不进 git，需离线备份） |
| `traceId` | 错误追踪 ID，日志与客户端提示共用 |
| P2 | 第二阶段待办（明确延后，不提前做） |

---

## 附录：变更记录

| 版本 | 日期 | 变更 |
|---|---|---|
| V1.0 | 2026-10-25 | 初版（按 m00029）：整体项目说明 + 文件级目录架构；与计划 V2.3.1、`AGENTS.md` 1.1 对齐；明确模块分层、依赖方向、构建流水线与里程碑目录产出 |
| V1.1 | 2026-10-25 | 按 m00148 补充工程规范：测试文件统一放 `docs/test/`（不再建根级 `tests/`）、默认不跑测试与分批最小范围、提交信息格式与交付说明五要素；版本对齐计划 V2.3.2、`AGENTS.md` 1.2 |
| V1.2 | 2026-10-25 | 按 m00204 明确文档分工：`README.md` 为项目介绍唯一出处，`AGENTS.md` 仅放约束；本文降级为详细展开（不重复项目介绍与硬约束）；同步 `AGENTS.md` 1.4、计划附录 C/D；后续 `AGENTS.md` 去重修订至 1.5（2026-10-25，仅文档编辑，本文内容不变） |
