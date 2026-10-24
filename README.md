# EiseChat（内部版）

内部使用的轻量聊天工具：**自研 IM 底层**（不用 OpenIM）、**有安装程序**（Windows 安装包 / Android APK；**iPhone 仅出方案、本期不实现**）、**邮箱验证码登录**（不用手机号）、**零软件授权费**。

## 项目介绍

> 本节是本仓库**唯一的项目介绍**；可执行约束见 [AGENTS.md](AGENTS.md)，计划与详细说明见 `docs/`。

- **是什么**：自研轻量 IM（服务端 + 客户端）；服务端为 ASP.NET Core 单进程（REST + 自研 JSON-over-WebSocket + 静态前端），数据用 SQLite（WAL）+ 服务器本地文件目录，Caddy 自动 HTTPS。
- **给谁用**：公司/团队内部，10~100 个账号、峰值 ≤100 在线；部署在云服务器 + 域名/HTTPS；不做公网运营，不需要 ICP/等保/实名/内容审核。
- **交付物**：Windows 安装包 `EiseChat-Setup-x.y.z.exe`、Android APK `EiseChat-x.y.z.apk`（侧载安装）、云服务器上的服务端（含 Web 端与 `GET /download` 下载页）；iPhone 仅出方案、本期不实现。
- **技术骨架**：一套 Vue 3 前端核心复用到 Web / Windows（Electron）/ Android（Capacitor）；服务端是唯一事实源，客户端 IndexedDB 只是缓存。
- **当前状态**：📋 计划定稿（V2.3.2）+ 目录架构已规划，部署参数已确认（2C4G 轻量 / 域名已有先用占位 / 注册不限制 / 邮件通道待接），可直接开始 M0/M1 编码。
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
| Windows | Electron + electron-builder → NSIS 安装包 `EiseChat-Setup-x.y.z.exe` |
| Android | Capacitor + Gradle 自签名 → `EiseChat-x.y.z.apk`（侧载安装，无需商店） |
| iPhone | **本期不实现，仅保留方案**（后续启用首选 PWA，不买 $99 开发者账号） |
| 部署 | 云服务器 2C4G（Ubuntu）+ systemd + Caddy 自动 HTTPS（Let's Encrypt）；测试期用占位域名 `chat.example.com` |
| 备份 | SQLite 在线备份 + 文件目录打包，每日同步到对象存储/异机，保留 30 份 |

**不引入**：OpenIM、MongoDB、Kafka、Etcd、Redis、MinIO、Kubernetes、短信服务商、第三方推送 SDK、ICP/等保。

## 文档

> 分工：**本文件只讲项目介绍与入口**；[AGENTS.md](AGENTS.md) 只放可执行约束；计划与详细说明在 `docs/`。

| 文档 | 内容 |
|---|---|
| **README.md（本文件）** | **项目介绍与入口**：项目是什么/给谁用、技术栈概览、文档索引、里程碑、目标目录、重要边界 |
| [docs/internal-plan.md](docs/internal-plan.md) | **现行主计划（V2.3.2）**：协议设计、数据模型、Windows/Android/服务器方案（iPhone 仅方案）、安装程序、云端部署、6~7 周路线、成本与风险 |
| [docs/project-overview.md](docs/project-overview.md) | **项目说明与目录架构**：项目定位/范围/技术栈/总体架构 + 文件级目录规划、模块职责、依赖方向；M0 建骨架按此执行 |
| [AGENTS.md](AGENTS.md) | **项目约束**：技术栈、硬规则（许可/架构/范围）、协议可靠性要求、工作流、测试与提交规范、DoD，AI/开发代理开工前必读 |
| （历史文档） | 旧计划、可行性分析、research 技术底稿已清理，只保留本计划、项目说明与约束文档 |

## 里程碑（详见主计划 §8）

| 阶段 | 时间 | 目标 |
|---|---|---|
| M0 | 3~4 天 | 云服务器选型/占位域名、协议与表结构定稿、仓库骨架 |
| M1 | 第 1~1.5 周 | 服务端：邮箱验证码注册/登录、JWT、WS、seq、ack、sync、SQLite，两账号互发无丢无重 |
| M2 | 第 2 周 | Web 客户端：邮箱登录/注册、会话、单聊、发送状态、断线补拉 |
| M3 | 第 3 周 | 群聊、图片/文件、未读/已读、typing |
| M4 | 第 4 周 | Windows 安装包 + 云服务器部署（Caddy HTTPS/systemd/备份）+ 下载页 |
| M5 | 第 5 周 | Android APK 打包与真机测试 |
| M6 | 第 6~7 周 | 100 连接稳定性、备份恢复演练、10~20 人内测（Windows + Android） |

## 目录（目标结构，文件级见 [docs/project-overview.md](docs/project-overview.md) §7）

```
apps/web        Vue 3 客户端核心（所有端共用）
apps/desktop    Electron 壳 + Windows 安装包
apps/mobile     Capacitor 壳 + Android APK
server/         ASP.NET Core 单进程（Chat.Server / Tests / Protocol）
protocol/       协议文档、JSON Schema、DDL
infra/          install.sh、caddy、backup、email、keys（keystore）
docs/           计划、协议、部署、使用说明
```

## 重要边界（内部版）

- 服务器可见消息内容（不做端到端加密）；敏感信息请勿用本工具传递。
- v1 无系统级离线推送：新消息在**打开应用时**自动补拉（Android 后台可能收不到即时提醒）。
- **iPhone 本期不实现（仅计划）**：后续启用首选 PWA（添加到主屏幕），不买 Apple 开发者账号。
- 邮件验证码依赖发件通道：需配置 SMTP/免费邮件 API，并保留密码登录兜底。
- 公网入口需要基础加固：安全组最小化、HTTPS、限流；注册限制可按需开启（邮箱域名白名单/邀请码）、及时更新补丁。
- **备份必须做**：每日自动备份 + 至少一份异机/对象存储备份，上线前完成一次恢复演练。

## 下一步

1. 无需再确认：按主计划 §12 已定参数开工（邮箱白名单留空 = 不限制、云服务器 2C4G 轻量、占位域名 `chat.example.com`、真实域名后替换）。
2. 开始 M0/M1：按 [docs/project-overview.md](docs/project-overview.md) §7 建仓库骨架 → 协议与 DDL → 邮箱验证码/登录 → WS 消息/同步 → Web 单聊 → 本地两浏览器互发验证。
