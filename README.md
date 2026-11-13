# EriseChat

非公开分发的轻量聊天工具，面向日常聊天使用：**自研 IM 底层**（不用 OpenIM）、**有安装程序**（Windows 安装包 / Android APK；**iPhone 仅出方案、本期不实现**）、**邮箱验证码登录**（不用手机号）、**零软件授权费**。

## 项目介绍

> 本节是本仓库**唯一的项目介绍**；可执行约束见 [AGENTS.md](AGENTS.md)，计划与详细说明见 `docs/`。

- **是什么**：自研轻量 IM（服务端 + 客户端）；服务端为 ASP.NET Core 单进程（REST + 自研 JSON-over-WebSocket + 静态前端），数据用 SQLite（WAL）+ 服务器本地文件目录，Docker Compose 一键部署（HTTPS 走 Tailscale Serve 或宿主 Caddy）。
- **给谁用**：由部署者自行分发给指定用户，适用于朋友、家庭或小群体的日常聊天，不限定工作场景；按 10~100 个账号、峰值 ≤100 在线设计，部署在云服务器 + 域名/HTTPS，不公开上架或面向公众推广。
- **交付物**：Windows 安装包 `EriseChat-Setup-x.y.z.exe`、Android APK `EriseChat-x.y.z.apk`（侧载安装）、云服务器上的服务端（含 Web 端与 `GET /download` 下载页）；iPhone 仅出方案、本期不实现。
- **技术骨架**：一套 Vue 3 前端核心复用到 Web / Windows（Electron）/ Android（Capacitor）；服务端是唯一事实源，客户端 IndexedDB 只是缓存。
- **里程碑**：M0~M5 已完成（2026-10-26；M4 交付 Windows 安装包与 `GET /download`，M5 产出 Android APK）；**M6（100 连接稳定性、备份恢复演练、10~20 人内测）未开始**。F 系列后续能力的逐项流水账已移至 `docs/internal-plan.md` 附录 D（本地文档，不入库）。
- **能力现状**：协议 [protocol/protocol.md](protocol/protocol.md) 共 **23 个 type**、`protocolVersion=1`；表结构 [protocol/schema.sql](protocol/schema.sql) 共 **13 张表**。邮箱验证码/密码登录与 JWT、单聊/群聊、文本/图片/文件/语音消息、`seq` 严格递增与 `clientMsgId` 幂等、断线补拉与 IndexedDB 缓存、已读/typing/在线状态、消息搜索与会话内搜索、撤回与编辑、多选与附件转发、服务端收藏跨设备同步、好友/群资料/群头像/群置顶/共享文件/自定义表情/@提及/群主转让、群成员资料卡（F-41）、附件上传队列、文本链接识别与安全打开（F-07）、客户端更新检查（F-29）均已落地；**多端在线为每账号默认 3 个连接（`App:MaxConnectionsPerUser` 可配 1~10），超限踢最早建立的连接（`kicked` + 4001），已读通过 `read.updated` 同步本人其他连接**（F-22）；Android 后台消息提醒由原生前台服务提供（F-39，真机验收待执行）。
- **交付物与版本**：Windows `EriseChat-Setup-1.2.0.exe`、Android `EriseChat-1.2.0.apk`、Web 前端与服务端均为 **1.2.0**（三端与服务端一致，`protocolVersion` 仍为 1）；命名统一为 EriseChat 后 `appId`/`applicationId` 与 JWT issuer/audience 变更，**旧版客户端需卸载重装、1.2.0 上线后所有用户需重新登录**（见 [docs/desktop.md](docs/desktop.md) §2.1、[docs/mobile.md](docs/mobile.md) §2.1）。
- **未完成与风险**：M6 稳定性与备份恢复演练、公网部署与 SMTP 发码通道、1.2.0 exe/APK 全新安装与登录收发验收、F-39 N01~N12 真机验收、F-40 回归测试、10~20 人内测尚未执行；服务器需按 [docs/deploy.md](docs/deploy.md) §7.3 / [docs/update-deploy.md](docs/update-deploy.md) §11 完成旧命名迁移。代码已提交（`39c2ac6` 全量命名统一，HEAD `6060caf`），**测试 / E2E 仍未运行**（按 `AGENTS.md` §11.2 默认不跑，需执行时按 §11.4 申请）。
- **目标周期**：约 6~7 周 MVP（Windows 安装包 + Android APK + 服务器云端部署 + 可用聊天闭环；**iPhone 不在本期**）。
- **成本**：软件授权 0 元；云服务器约 60~150 元/月（域名已有；TLS、邮件额度、Android 签名均免费）。

## 技术栈概览（锁定选型与硬约束见 [AGENTS.md](AGENTS.md) §3）

| 层 | 选型 |
|---|---|
| 服务端 | ASP.NET Core（.NET LTS）单进程，REST + 自研 JSON-over-WebSocket 协议 |
| 数据 | SQLite（WAL）+ EF Core；文件存服务器本地目录 |
| 账号 | **邮箱验证码注册/登录**（6 位、10 分钟、限流）；邮箱域名白名单默认空 = 不限制（管理员可配）；注册不限制、邀请码可选；可选密码登录 |
| 邮件 | 可配置 SMTP（企业邮箱）或免费邮件 API（Brevo/Resend 等，约 100~300 封/天）；开发期 mock |
| 客户端核心 | Vue 3 + TypeScript + Vite + Pinia（一套代码多端复用：Web/Windows/Android） |
| Windows | Electron + electron-builder → NSIS 安装包 `EriseChat-Setup-x.y.z.exe` |
| Android | Capacitor + Gradle 自签名 → `EriseChat-x.y.z.apk`（侧载安装，无需商店） |
| iPhone | **本期不实现，仅保留方案**（后续启用首选 PWA，不买 $99 开发者账号） |
| 部署 | 云服务器 2C4G（Ubuntu）+ **Docker Compose 单容器一键部署**（`docker compose up -d --build`）；HTTPS 内测用 Tailscale Serve，公网用宿主 Caddy |
| 备份 | SQLite 在线备份 + 文件目录打包，每日同步到对象存储/异机，保留 30 份 |

**不引入**：OpenIM、MongoDB、Kafka、Etcd、Redis、MinIO、Kubernetes、短信服务商、第三方推送 SDK、ICP/等保。

## 文档

> 分工：**本文件只讲项目介绍与入口**；[AGENTS.md](AGENTS.md) 只放可执行约束；计划与详细说明在 `docs/`。

| 文档 | 内容 |
|---|---|
| **README.md（本文件）** | **项目介绍与入口**：项目是什么/给谁用、技术栈概览、文档索引、里程碑、目标目录、重要边界 |
| `docs/internal-plan.md`（本地文档，不入库） | **现行主计划（V2.3.5）**：协议设计、数据模型、Windows/Android/服务器方案（iPhone 仅方案）、安装程序、云端部署、6~7 周路线、成本与风险 |
| [docs/project-overview.md](docs/project-overview.md) | **项目说明与目录架构**：项目定位/范围/技术栈/总体架构 + 文件级目录规划、模块职责、依赖方向；M0 建骨架按此执行 |
| [docs/deploy.md](docs/deploy.md) | 服务端 Docker Compose 一键部署、HTTPS、配置项、邮件通道、备份恢复、升级回滚 |
| [docs/update-deploy.md](docs/update-deploy.md) | 改代码后自行构建部署：出固定名源码包（`infra/package-src.sh`）→ 上传替换 → 服务器一键重建 → 验证与回滚 |
| [docs/web.md](docs/web.md) | Web 端开发运行、构建、生产部署（dist → wwwroot）、验收清单与排障 |
| [docs/desktop.md](docs/desktop.md) | Windows 桌面端安装、日常行为（托盘/通知/设置）、开发联调、NSIS 打包与排障 |
| [docs/mobile.md](docs/mobile.md) | Android 端安装、签名、USB/局域网联机、APK 打包与真机验收 |
| [AGENTS.md](AGENTS.md) | **项目约束**：技术栈、硬规则（许可/架构/范围）、协议可靠性要求、工作流、测试与提交规范、DoD，AI/开发代理开工前必读 |
| （历史文档） | 旧计划、可行性分析、research 技术底稿已清理，只保留本计划、项目说明与约束文档 |

> **入库范围（2026-10-26 起）：** 文档只提交说明类文件；计划、审查/验收报告、任务清单、截图与参考图等过程文件**只保留在本地、不提交仓库**（见 [AGENTS.md](AGENTS.md) §2.11/§4）。上表标注「本地文档，不入库」的文件在仓库中不存在，仅供本地/代理阅读。

## 里程碑（详见主计划 §8）

| 阶段 | 时间 | 目标 | 状态 |
|---|---|---|---|
| M0 | 3~4 天 | 云服务器选型/占位域名、协议与表结构定稿、仓库骨架 | 已完成 |
| M1 | 第 1~1.5 周 | 服务端：邮箱验证码注册/登录、JWT、WS、seq、ack、sync、SQLite，两账号互发无丢无重 | 已完成 |
| M2 | 第 2 周 | Web 客户端：邮箱登录/注册、会话、单聊、发送状态、断线补拉 | 已完成 |
| M3 | 第 3 周 | 群聊、图片/文件、未读/已读、typing | 已完成 |
| M4 | 第 4 周 | Windows 安装包 + 云服务器部署（Docker Compose 一键部署/HTTPS/备份）+ 下载页 | 已完成 |
| M5 | 第 5 周 | Android APK 打包与真机测试 | 已完成（真机验收待执行） |
| M6 | 第 6~7 周 | 100 连接稳定性、备份恢复演练、10~20 人内测（Windows + Android） | **未开始** |

## 目录（目标结构，文件级见 [docs/project-overview.md](docs/project-overview.md) §7）

```
apps/web        Vue 3 客户端核心（所有端共用）
apps/desktop    Electron 壳 + Windows 安装包
apps/mobile     Capacitor 壳 + Android APK
server/         ASP.NET Core 单进程（Chat.Server / Tests / Protocol）
protocol/       协议文档、JSON Schema、DDL
infra/          docker/（Dockerfile + deploy.sh）、package-src.sh、install.sh、caddy、backup、email、keys（keystore）
docs/           计划、协议、部署、使用说明
```

## 快速开始（当前可运行部分）

```bash
# 服务端（默认 http://127.0.0.1:6580）
dotnet build server/Chat.Server.sln      # 构建
dotnet run --project server/Chat.Server  # 启动
curl http://127.0.0.1:6580/api/health    # 健康检查：status / serverTime / protocolVersion

# Web 前端（开发期 /api、/ws 代理到 127.0.0.1:6580）
cd apps/web && npm install
npm run dev     # http://127.0.0.1:5173
npm run build        # Web 产物 apps/web/dist/web（不入库；服务端同域托管）
npm run build:file   # 壳产物 apps/web/dist/file（Electron / Android，相对资源）
# 生产部署见 docs/web.md

# Windows 壳（开发态需先启动上面的 Web dev server；安装包 M4 已产出，安装/打包见 docs/desktop.md）
cd apps/desktop && npm install && npm run dev   # Electron 加载 http://127.0.0.1:5173
npm run dist                                    # NSIS 安装包（apps/desktop/dist/EriseChat-Setup-1.2.0.exe，不入库）

# Android 壳（Web 资源打进 APK；APK 1.2.0 已产出，打包环境与安装见 docs/mobile.md）
cd apps/mobile && npm install && npm run sync
bash infra/build-apk.sh 1.2.0   # 产出 data/downloads/EriseChat-1.2.0.apk（不入库；需 JDK 21 + Android SDK）

# M3 端到端验收（一次性脚本，docs/test/ 不入库；需服务端以 Development 启动并监听 6580）
ERISECHAT_PYTHON=python node docs/test/m3-e2e.mjs

# 下载页 / 安装包分发（服务端启动后；安装包放 data/downloads/，浏览器打开 http://127.0.0.1:6580/download）

# 服务器部署（Docker Compose 一键部署，详见 docs/deploy.md §3 / docs/update-deploy.md）
bash infra/package-src.sh                       # 开发机：打固定名源码包 dist/erisechat-src.zip（覆盖）
# 上传：scp dist/erisechat-src.zip chat:~/erisechat-src.zip（目标名固定），服务器解压替换 /opt/erisechat/src
sudo bash infra/docker/deploy.sh                # 服务器上执行（构建镜像 + 启动 + 健康检查）
sudo bash infra/backup.sh                       # 服务器备份（每日 cron 见部署文档）
# 备用（无 Docker 环境）：publish-linux.sh + install.sh，见 docs/deploy.md 附录
```

> 完整命令清单见 [AGENTS.md](AGENTS.md) §8；M1 服务端与 M2/M3 Web 端已完成（认证/WS/消息/补拉/登录注册/会话列表/聊天页/群聊/图片文件/已读/typing/在线状态），M4 已交付 Windows 安装包、`GET /download` 下载页与 `infra/` 部署脚本/`docs/deploy.md`；M5 已产出 Android APK、F-27 统一打包 1.1.0 已发布（历史版本，当前为 1.2.0）（`GET /download` 可下载，安装说明见 [docs/mobile.md](docs/mobile.md)）；本机两个浏览器仍可直接完成登录与互发验证（端到端验收脚本见上方 `m3-e2e.mjs`，一次性，不入库）。

## 重要边界（内部使用说明）

- 服务器可见消息内容（不做端到端加密）；敏感信息请勿用本工具传递。
- 无系统级离线推送（不接厂商推送/短信）：Web 与桌面端在应用打开时自动补拉；Android 可开启「设置 → 消息提醒 → 后台消息提醒」，由原生前台服务（`dataSync`）在后台保持一条连接并发送系统通知（F-39），但该能力受 **Android 15+ 单日 6 小时**限制、**系统重启后需重新开启**，且不等同于系统级离线推送（详见 [docs/mobile.md](docs/mobile.md) §3.1）。
- **iPhone 本期不实现（仅计划）**：后续启用首选 PWA（添加到主屏幕），不买 Apple 开发者账号。
- 邮件验证码依赖发件通道：需配置 SMTP/免费邮件 API，并保留密码登录兜底。
- 公网入口需要基础加固：安全组最小化、HTTPS、限流；注册限制可按需开启（邮箱域名白名单/邀请码）、及时更新补丁。
- **备份必须做**：每日自动备份 + 至少一份异机/对象存储备份，上线前完成一次恢复演练。

## 下一步

1. **已完成**：M0~M5 里程碑；F 系列后续能力（消息搜索 F-11、会话内搜索 F-19、会话置顶/免打扰 F-10、个人资料 F-12、账号安全 F-13、群资料 F-14、群头像 F-23、共享文件 F-15、自定义表情 F-21、消息撤回/编辑 F-18、多端在线 F-22、语音消息 F-28、客户端更新检查 F-29、后台提醒 F-39、多选与附件转发 F-40、群成员资料卡 F-41、链接识别与安全打开 F-07）与 UI-00~UI-05 改造；1.2.0 全量命名统一与三端产物重打均已提交。
2. **下一步（真实待办，均未执行）**：① 1.2.0 exe/APK 全新安装与登录收发验收；② F-39 N01~N12 真机验收；③ F-40 回归测试；④ M6 稳定性、备份恢复演练与 10~20 人内测；⑤ 公网部署与 SMTP 发码通道；⑥ 服务器按 [docs/deploy.md](docs/deploy.md) §7.3 / [docs/update-deploy.md](docs/update-deploy.md) §11 完成旧命名迁移。按 `AGENTS.md` §11.2 默认不运行测试，需执行时按 §11.4 先申请。
