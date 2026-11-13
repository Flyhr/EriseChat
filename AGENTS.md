# AGENTS.md — EriseChat 项目约束

> 版本 2.10（2026-11-12）。改动仓库前必须阅读并遵守本文件；计划变更涉及仓库约束时同步更新。2.4 起测试文件统一放根级 `tests/`（不入库），新增测试子目录须先征得用户同意。2.5 修订：校正 §10 后续功能清单、更正 §6 算法名、新增 §1.1 命名总则（唯一名称为 EriseChat，运行时标识与代码同步迁移）。2.6 修订：新增 §14「界面与 UI 约束」收口原散落约定；旧 UI 资产归档至 `docs/deprecated/` 并标记废弃，`docs/ui/README.md` 原地重写为现行 UI 更新文档。2.7 修订：撤销 §14.2 深色主题冻结，改为「允许实现 + 实施前置条件」。2.8 修订：深色主题已实现，§14.2 改为「已实现 + 主题变更前置条件」；实施规格见 `docs/ui/README.md` §9。2.9 修订：§14.6 体积阈值翻倍至 200 / 520 KB，新增 §14.9「加载反馈与动效」固化动效规范与依赖口径（主线 `motion`）；批次 4 目视验收暂缓至代码修改完成后。2.10 同步 §10 已确认的更新与统一发版范围。

## 1. 文档分工与优先级

**优先级**：本文件的硬规则 > `docs/internal-plan.md` > 其他文档。

| 文档 | 职责 |
|---|---|
| `README.md` | 项目介绍与入口。项目定位、形态、规模等变化先改 README，再同步其他文档。 |
| `AGENTS.md` | 仓库级硬约束。可执行约束统一写在本文件。 |
| `docs/internal-plan.md` | 唯一现行计划（V2.3.5）：范围、排期、协议与数据模型、验收和风险。 |
| `docs/project-overview.md` | 项目详解、文件级目录、模块职责与依赖方向。 |
| `docs/ui/README.md` | UI 的唯一现行说明文档：变更批次、当前基线、约束索引、优化路线与待办验收。 |
| `protocol/protocol.md`、`protocol/schema.sql` | 分别为协议帧和表结构的唯一来源；表结构与 EF 迁移同步。 |

### 1.1 命名总则

- 本项目唯一名称为 **EriseChat**：产品名与文案用 `EriseChat`，标识符与包名前缀用 `erisechat`，环境变量前缀用 `ERISECHAT`。仓库目录名 `EriseChat` 即产品名，不是路径特例。
- 历史遗留的三个大小写变体均为早期笔误，统一方向是改为上述对应形式；禁止反向统一。**除本节明确登记的密钥资产例外外**，不得以兼容性或风险为由保留旧值。
- 运行时标识（Android `applicationId` 与 Java 包名、Electron `appId` 与注入桥、JWT issuer/audience、前端 localStorage key、Docker 容器/镜像/compose 项目名与部署路径、产物名前缀及其服务端白名单、环境变量前缀）**必须与代码原子同步修改**，不得只改文档或只改代码。
- 密钥资产例外（2026-11-11 用户裁决）：`infra/keys/eisechat-release.jks` 及其签名别名、签名证书沿用历史名称与内容，**不改文件名、不重建、不重签**。这是本节唯一获准保留旧字面量的情形，仅限密钥资产本体与其引用路径，不构成上条所禁止的「以兼容性或风险为由保留旧值」；其余任何旧值一律不得保留。用户已确认按「保持密钥资产与签名别名不变」的方案改写本节，以消除「不保留例外」与例外登记之间的表述矛盾。


## 2. 硬规则

1. **不引入第三方 IM**：OpenIM、融云、环信、腾讯云 IM、Sendbird 等一律不用；IM 协议由本项目自研。
2. **不产生授权费用**：不买商业授权/SDK、不买签名证书、不买 Apple 开发者账号、不接付费推送或短信服务。
3. **依赖许可**：只允许 MIT / Apache-2.0 / BSD / ISC 等宽松许可；**禁止** GPL / AGPL / SSPL / BUSL 或商业限制许可进入仓库。新增 npm/NuGet 依赖前检查 LICENSE，不确定先问用户。
4. **架构与部署**：不引入 Kubernetes / Redis / Kafka / MongoDB / PostgreSQL / MinIO / Nginx / SignalR，不拆微服务。使用根目录 `docker-compose.yml` 与 `infra/docker/` 实现 Docker Compose 单容器一键部署，容器内运行 ASP.NET Core 单进程，使用 SQLite 和本地文件卷。允许 Docker / Compose 与官方 .NET/Node 基础镜像；禁止多容器编排。端口只绑定宿主 `127.0.0.1:6580`，HTTPS 由宿主 Tailscale Serve（内测）或 Caddy（公网）提供。安全组只开放必要端口，可用 fail2ban。
5. **范围边界**：不写 iPhone/iOS 代码、不做 TestFlight；iPhone 仅在用户明确启动后续版本时评估（计划 §5.4）。不做语音/视频通话、朋友圈、AI、机器人、E2EE；使用说明须写明不提供 E2EE。ICP、等保、短信、厂商推送等公开发布事项不在范围内。
6. **密钥纪律**：token、密码、验证码明文、SMTP 凭据、JWT 密钥、keystore 等敏感值不得进入源码、日志、示例或提交历史。凭据通过 `secrets.json` 或环境变量配置；Android keystore 按 §3 存放。日志不得记录邮件明文。
7. **协议兼容**：协议帧与数据库结构只允许向后兼容的增量修改，例如新增可选字段或帧类型；不得删除字段、改名或改变语义。
8. **数据归属**：服务端是唯一事实源（SQLite + `/data/files`）；客户端可用 IndexedDB 做缓存，但不得只存在本地；不接云数据库/云存储（备份到对象存储除外）。
9. **不破坏既有文件**：不删除、不重命名用户已有文件或目录；执行删除/覆盖前确认目标，必要时先问。
10. **测试与提交**：遵守 §11。
11. **文档入库**：遵守 §4。
12. **编码原则**：遵守 §12。
13. **界面文案**：Web、Windows、Android 产品 UI 禁止加入解释性文档，包括功能原理、设计理由、实现细节和长篇使用说明；说明放在 `docs/` 对应文档。界面只保留完成当前操作所需的简短文字，如操作标签、输入提示、状态和错误反馈。

## 3. 固定技术栈

| 层 | 选型 |
|---|---|
| 服务端 | ASP.NET Core（.NET 8/10 LTS）+ Kestrel，REST + WebSocket + 静态前端一体 |
| 实时通信 | 自研 JSON-over-WebSocket（一行一帧，带 `type`，DTO 放 `Chat.Protocol`，可生成 TS 类型） |
| 数据 | SQLite（WAL）+ EF Core，单文件 `/data/chat.db`；迁移放 `server/Chat.Server/Data/Migrations` |
| 认证 | 邮箱验证码 + 可选密码；密码和 JWT 要求见 §6 |
| 邮件 | 可配置 SMTP（企业邮箱/QQ 邮箱授权码）或免费邮件 API（Brevo/Resend 等）；`Development` 输出到控制台（不写日志文件）+ 固定测试码 |
| 前端 | Vue 3 + TypeScript + Vite + Pinia + 轻量 UI；核心模块 `core/ws.ts`、`core/sync.ts`、`stores/` |
| Windows 壳 | Electron + electron-builder（NSIS，`appId=com.erisechat.desktop`，安装包 `EriseChat-Setup-x.y.z.exe`，免签名） |
| Android 壳 | Capacitor，Web 资源打进 APK（不依赖在线加载），最低 API 26，自签名 keystore 放 `infra/keys/`（gitignore），APK 名 `EriseChat-x.y.z.apk` |

## 4. 目录与命名

- 目录结构见 `docs/project-overview.md` §7 与计划附录 B。
- 依赖、构建产物、数据库、运行数据、日志及本地凭据不入库；具体路径见 `.gitignore`。
- 命名：C# PascalCase；TS 组件/类型 PascalCase，函数/变量 camelCase；REST 路径小写复数（`/api/conversations`）；WS 帧 `type` 点分隔（`message.send`）；数据库表/列 snake_case；时间统一 UTC 毫秒存储、前端本地化显示。
- 语言：文档、注释、提交说明用中文；代码标识符、日志键、数据库字段用英文。
- 新增项目文档一律放在 `docs/`（`README.md` 除外）。
- **文档入库范围**：
  - **入库（说明类）**：`README.md`、`AGENTS.md`、`docs/project-overview.md`、`docs/deploy.md`、`docs/update-deploy.md`、`docs/web.md`、`docs/desktop.md`、`docs/mobile.md`、`docs/usage.md`、`docs/ui/README.md`、`protocol/protocol.md`、`protocol/schema.sql`。
  - **不入库（计划与过程类，本地保留）**：`docs/internal-plan.md`、UI 专项计划、审查与验收报告、任务清单、进度与运行记录、AI 指令、交付说明、测试脚本与日志、基线与对比截图、参考图与交互验收包，以及 `docs/deprecated/`（已废弃 UI 资产归档，整体不入库）。
  - 已入库的计划/过程文件用 `git rm --cached` 移出索引（**不删除本地文件**），并在 `.gitignore` 中登记；之后新建同类文件默认不提交，需要入库必须先在 AGENTS/README 中改规则并告知用户。
  - **UI 文档**：`docs/ui/README.md` 为唯一现行 UI 说明；`docs/deprecated/` 内全部文件已废弃（2026-11-12），不得作为设计、实现或验收依据（见 §14.7）。

## 5. 协议与消息可靠性

1. **会话内单调 `seq`**：事务内 `seq = max(seq)+1` 落库，严格 +1，不跳号、不改号。
2. **幂等发送**：`clientMsgId`（客户端 UUID）在 `(conversation_id, client_msg_id)` 唯一；重复发送直接回 `message.ack`，不重复落库。
3. **发送状态机**：本地队列「发送中」→ 收到 ack 变「已发送」→ 2s/5s/10s 重试 3 次仍失败标「失败，可重发」。
4. **接收去重排序**：按 `messageId` 去重、按 `seq` 排序；发现 `seq > lastSeq+1` 立即 `sync.request`。
5. **补拉同步**：每个会话保存 `lastSeq`；启动、重连、切回前台触发；`hasMore=true` 继续拉直到追平；每会话每批 ≤200 条。
6. **未读**：只统计 `seq > 我的 last_read_seq` 且 `sender_id ≠ 我` 的消息。服务端实时下发会话的 `unread` 字段，客户端禁止用 `lastSeq − lastReadSeq` 估算；打开会话并滚到底部后上报已读（协议 §4.5）。
7. **多端在线**：每账号连接上限由 `App:MaxConnectionsPerUser` 配置，默认 3，限制 1~10。同一浏览器的不同标签页分别计数，不按设备去重。超限时只踢最早建立的连接，发 `kicked` 帧并以 `4001` 关闭。断线不删数据；已读通过 `read.updated` 同步给本人其他连接（协议 §4.6）。
8. **心跳**：客户端每 25 秒发 `ping`，30 秒无响应判断线；重连按 `1/2/5/10/30s` 退避并加随机抖动。

## 6. 数据与安全

- 密码：PBKDF2-HMAC-SHA256（≥100,000 次、随机盐）或 Argon2id；验证码只存哈希。实现见 `server/Chat.Server/Common/Security/PasswordHasher.cs`（210,000 次迭代、16 字节随机盐）。
- JWT：HS256，有效期默认 3 天且可配置；密钥首次启动生成并写入 `secrets.json`。
- 限流：同邮箱 60 秒/次、10 次/天；同 IP 20 次/小时；验证码错误 5 次作废；登录失败 5 次锁 5 分钟。
- 文件：默认上限 50MB（可配）；下载只走 `GET /api/files/{id}` 并校验会话成员；不暴露磁盘路径；上传校验 MIME/扩展名；图片发送前客户端压缩（最长边 1280px、约 ≤300KB）。
- 日志与错误：日志滚动保留 30 天；错误返回 `traceId`。
- 备份：每日备份 SQLite（在线备份 API）+ 文件目录到对象存储/异机；恢复步骤写文档并演练。

## 7. 工作流

1. 先读 `docs/internal-plan.md` 相关章节，确认任务属于 M0~M6 或已确认待办。超出计划或本文件范围时，先说明提案和影响（工作量、依赖、许可、风险），经用户确认后再做。
2. 协议或表结构变更先更新 `protocol/protocol.md` 和计划；涉及表结构时同步 `protocol/schema.sql`。实现按服务端 → Web 核心 → 壳/安装包的顺序修改受影响部分，每一步保持可构建、可运行。
3. 更新受影响的 README、部署与使用说明等文档；计划级变更追加到计划附录 D 并告知用户。
4. 按 §9 验收和交付。未经用户明确要求，不执行 `git push`、`git reset --hard`、`git clean -fd`、发布或外发文件。

## 8. 常用命令

```bash
dotnet build server/Chat.Server.sln             # 服务端构建
dotnet run --project server/Chat.Server         # 服务端启动（http://127.0.0.1:6580）
dotnet test server/Chat.Server.Tests            # 服务端单测（默认不跑，见 §11）
cd apps/web && npm install && npm run dev       # Web 开发（http://127.0.0.1:5173）
cd apps/web && npm run build                    # Web 构建（产物 dist/，不入库）
cd apps/desktop && npm install && npm run dev   # Electron 开发（需先启动 Web dev server）
cd apps/desktop && npm run dist                 # NSIS 安装包
cd apps/mobile && npm install && npm run sync   # 同步 Web 产物到 Android 工程
cd apps/mobile/android && ./gradlew assembleRelease  # APK（需 Android SDK/Gradle）
bash infra/publish-linux.sh 1.1.0               # 备用 Linux 发布包
bash infra/package-src.sh                       # 开发机：打固定名源码包 dist/erisechat-src.zip（覆盖）
sudo bash infra/docker/deploy.sh                # 服务器 Docker 一键部署/升级（详见 docs/deploy.md §3）
docker compose ps | docker compose logs -f erisechat   # 容器状态 / 日志
sudo bash infra/backup.sh                       # 服务器每日备份（cron 见部署文档）
```

## 9. 验收与交付

- [ ] `dotnet build` 和 `npm run build` 通过；记录代码级验证结果。测试按 §11 执行，若已运行则记录命令与结果。
- [ ] 消息链路改动已验证 `seq` 连续、发送幂等、断线补拉无重复或空洞。M1/M2/M6 里程碑验收用两个客户端实测发送、断线、重连补拉。
- [ ] 安装包相关改动：exe/apk 可安装运行；覆盖安装/卸载不影响服务端数据。
- [ ] 已按 §7 同步文档。交付说明包含修改文件、修改原因、验证方式、测试结果和剩余风险。

## 10. 后续功能与既有授权

- P2 待办（不提前做）：群已读明细（F-20）。
- 已完成（原列 P2，现已落地）：消息搜索与会话内搜索（F-11 / F-19，2026-10-27~28）；多端同时在线（F-22，2026-11-01，见 §5.7）；客户端更新检查（F-29，2026-11-02）；Android 后台消息提醒前台服务（F-39，2026-11-06 代码实现完成，N01～N12 真机验收待执行）。
- F-29 已有设置手动检查及桌面、Android 下载和安装代码。已确认待实施的扩展包括静默检查、设置红点、桌面更新器、Android 安装授权引导和仅登录页的最低版本门槛。正式客户端应用内下载安装要求 HTTPS，显式调试可用回环 HTTP。
- 统一发版已确认，待实施。本地脚本与私有 GitHub 仓库自托管 runner 共用流程，一并部署服务端/Web 的 Docker Compose 镜像并发布 Windows、Android 安装包和清单。计划见 `docs/internal-plan.md` §14.1、§14.4 和本地专项 `docs/update-plan.md`。
- 移出本仓库：管理员功能（F-16，2026-11-01 用户裁决，由独立后台系统承接）。
- 已获授权并实现：消息撤回和编辑（F-18，2026-10-28，协议 §4.3）；语音消息（F-28，2026-11-02，协议 §7 第 9 条）。

## 11. 测试与提交

### 11.1 测试文件位置

- 所有测试文件（单元测试、集成测试、测试夹具与辅助代码）统一放在仓库根级 `tests/`，按内容分目录（如 `tests/web/`、`tests/server/`）；`tests/` 整体 gitignore，不入库。
- 新建测试子目录前必须先向用户说明用途并取得同意；不得在 `apps/`、`server/` 等源码目录内新增或散落测试文件。
- 一次性脚本、临时补丁、验证数据与输出继续放在 `docs/test/`（不入库）；新建其子目录同样需用户同意。
- 既有已入库的服务端 xUnit（`server/Chat.Server.Tests`）与前端 Vitest（`apps/web/src/**/*.test.ts`）作为历史代码保留，可继续本地运行；后续新增测试一律按上述规则放入 `tests/`，不再随工程提交。

### 11.2 默认不运行测试

- 仅在用户当轮明确要求时运行测试。默认检查改动、调用链、引用、类型及接口契约。
- 未执行测试或未执行到任何用例，不得报告测试通过。

### 11.3 测试范围

- 分批运行与改动直接相关的最小范围。跨端契约改动须覆盖受影响的调用端；鉴权、发布、删除等关键路径保留最小回归。

### 11.4 测试审批

- 预计超过约 2 分钟的测试，以及基准、压测、端到端测试、全量回归或扩大测试范围，均须先向用户说明范围、必要性和预计耗时，取得同意后再启动。

### 11.5 测试按缺口增加

- 优先复用现有用例，按实际缺口在现有文件补最小回归；现有文件无法承载时才新建。不为简单文案、样式或等价重构新增重复测试。

### 11.6 提交信息格式

- 格式：`类型：简述`；简述用中文；需要时另起一行写具体描述。
- **不写任务编号**（如 `F-07`、`M3-03`）：任务归属由计划文档追踪，提交信息只描述本次改动的性质。
- 一个提交只做一件事；跨类型改动（如改代码同时改文档）拆成多个提交。
- 示例：`feat：左导航顶部改为头像+昵称账号入口`

| 类型 | 含义 |
|---|---|
| `feat` | 一项新功能（feature） |
| `fix` | 一个 bug 修复 |
| `docs` | 仅文档更改 |
| `style` | 风格：不影响代码含义的更改（空白、格式、缺少分号等） |
| `refactor` | 重构：既不修正错误也不增加功能的代码更改 |
| `perf` | 改进性能的代码改动 |
| `test` | 添加缺失或更正现有测试 |
| `chore` | 更改构建过程或辅助工具和库，例如文档生成 |
| `revert` | 回滚到上一个版本 |

### 11.7 提交与密钥安全

- 提交前检查 `git status` 与待提交内容，确认没有密钥或临时文件混入。

## 12. 编码原则

本章适用于新增和修改的代码。既有代码仅在本次改动涉及且收益明确时小幅收敛，并在提交信息中说明。

### 12.1 防过度防御

- 仅在存在实际触发场景时考虑新增重试、锁、去重、兜底、缓存、幂等令牌或复杂状态机；极低概率场景和其他例外按 §12.5 处理。
- 安全、消息可靠性和权限校验按 §2、§5、§6 实施。

### 12.2 入口单校验

- 外部数据只在系统边界校验一次，失败时返回明确错误码或提示；通过后转换为明确的类型。内部直接使用该类型，不重复判空、判型或解析；用类型或构造时校验表达不变式。
- 在执行操作处校验权限、成员关系和资源归属。

### 12.3 拒绝提前抽象

- 单处逻辑就近内联；有实际复用或需要独立测试时才抽取，职责与命名须清晰。禁止提前分层、泛型化或增加目录和配置项。

### 12.4 控制复杂度

- 仅在需要处理或转换错误的外部边界使用 `try/catch`；内部错误直接向上传播。
- 按职责划分函数，优先使用顺序逻辑、早返回及语言或框架自带能力。避免深层嵌套和晦涩表达式；不给简单逻辑套设计模式、事件总线、中间件或通用框架。

### 12.5 例外审批

- 确有必要新增防御或抽象时，先在交付说明中写明触发条件、发生概率、影响、方案和复杂度代价，经用户确认后实施。

## 14. 界面与 UI 约束

本章为界面与 UI 的唯一可执行约束集合，适用于 `apps/web` 及复用它的 Electron / Capacitor 壳。原散落在 UI 专项计划、参考包与工作记录中的约定统一收口于此；现状与变更记录见 `docs/ui/README.md`，逐项验收见 `docs/web.md` §7。**本章优先于任何历史截图与参考图**。

### 14.1 界面文案

- 承接 §2.13：Web、Windows、Android 产品 UI 禁止解释性文档（功能原理、设计理由、实现细节、长篇使用说明）；只保留完成当前操作所需的简短文字：操作标签、输入提示、状态与错误反馈。
- 说明性内容一律放 `docs/`。输入框 placeholder 只写动作提示，不写快捷键、技术参数或格式说明。
- 全站术语统一：导航项称「联系人」，不得在同一界面混用「通讯录」等旧称。

### 14.2 设计 token、图标与主题层

- `styles/variables.css` 是 `--ec-*` token 的唯一来源；组件内不得写死颜色、间距、圆角与阴影等魔法数字。新增派生值须在注释里标明来源。
- CSS 自定义属性写错名不会报错、只会静默失效：新增或改名 token 后必须 grep 校验所有使用点与主题文件。
- 颜色 token 由 `styles/themes/azure.css` 按 `data-ec-theme` 提供，`core/theme.ts` 映射 Naive UI `themeOverrides`；切换主题时派生值应自动跟随。
- 「澄蓝」蓝白主题为默认主题，**深色主题（dark）已实现（2026-11-12）**：`styles/themes/dark.css` 按 `html[data-ec-theme='dark']` 覆写 `docs/ui/README.md` §9.2 列出的全部语义 token；主题由设置页显式选择、存本机 `localStorage`，不做系统主题自动跟随，`index.html` 首屏预置避免闪烁。**新增或修改主题时**必须满足 ① `--ec-*` 全量 token 审计无未定义引用 ② 双主题正文对比度 ≥4.5:1、组件边界 ≥3:1 ③ `npm run build` 与四档宽度 / 缩放 / 安全区目视验收通过。实施规格与验收清单见 `docs/ui/README.md` §9。
- 图标统一由 `AppIcon.vue` 使用 Phosphor 线性集；禁止 emoji 充当结构图标、禁止位图图标，同一层级保持线性 / 填充与描边粗细一致。
- 组件库渐进替换为 `naive-ui` + `@phosphor-icons/vue` + `@vueuse/core`（均 MIT）；替换须保持原 props / emit / 默认插槽、键盘可达性与焦点归还契约，调用方零改动。

### 14.3 媒体消息

- 图片 / 文件 / 语音消息**不套气泡**，以独立媒体卡呈现；媒体卡表面色、描边、图标底与进度轨道只走 `--ec-media-*` token，便于主题切换。
- 图片发送前客户端压缩；文件下载只走 `GET /api/files/{id}` 并校验会话成员（§6）。

### 14.4 消息操作菜单

- 鼠标**只由右键**触发（`contextmenu`）；鼠标左键长按不得触发。手指与触控笔保留长按（500ms）。
- 菜单内含回复 / 撤回 / 编辑 / 转发 / 收藏 / 置顶 / 多选；移动端 ≤800 贴底 ActionSheet。
- 任何提升可发现性的改动（如 hover 轻量入口）不得重新引入鼠标左键长按。

### 14.5 响应式与多端

- 四档断点统一写在 `variables.css`：≤800 / 801–1199 / 1200–1439 / ≥1440；组件不得自行引入第三套数值。
- ≤800 单栏 + 底部导航 + `--ec-safe-top/bottom` 安全区 + 软键盘适配；801–1199 导航收为图标栏、右侧面板覆盖式；≥1200 三栏。
- Web / Electron / Capacitor 共用一套 `apps/web` 源码；壳层只负责窗口、托盘、通知与安全区补齐，不另写聊天界面。

### 14.6 体积与构建

- `cd apps/web && npm run build`（含 `vue-tsc --noEmit`）为前端校验命令。
- 入口共享 chunk gzip **≤200 KB**、全站 JS+CSS gzip **≤520 KB**（2026-11-12 用户裁决，由原 ≤100 / ≤260 KB 翻倍，为动效基座留出空间）；每次引入组件库或新依赖后复核并记录实测值。
- 阈值放宽不等于放弃体积纪律：新增依赖仍须在交付说明中写明 gzip 实测增量，不得以「阈值已放宽」为由跳过复核。

### 14.7 UI 文档与废弃资产

- `docs/ui/README.md` 是 UI 的**唯一现行说明文档**；每次较大 UI 改动后同步其变更批次与基线。
- `docs/deprecated/` 为已废弃 UI 资产归档目录（2026-11-12 标记废弃），整体不入库；其中所有截图、参考图、交互验收包与旧说明**不得作为现行设计、实现或验收依据**。
- 归档文件只追加废弃横幅，不删除、不改写原始内容。需要废弃现行 UI 文档时，移入该目录并加横幅，再在原路径新建现行版。
- 新截图、对比图、验收包一律本地保留且不入库（§4）。

### 14.8 验收与待办

- UI 目视验收清单（四档宽度 1440/1200/900/390、100%/125% 缩放、亮色对比度、Android 安全区、Toast / 搜索框 / 弹窗渲染与焦点归还）见 `docs/ui/README.md` §6；截至 2026-11-12 尚未执行，建议并入 M6 内测。
- 测试默认不运行（§11.2）；需要执行时按 §11.4 先申请范围与耗时。

### 14.9 加载反馈与动效

本章把 `docs/ui-optimization-plan.md` §12.4 的动效规范固化为硬规则，批次 4 实施时必须遵守。

- **曲线与时长**：入场用 `--ec-ease` + `--ec-transition-base`（160ms）；退出用 `--ec-ease-in` + `--ec-transition-fast`（120ms）；页面内位移 / 展开收起用 `--ec-ease-in-out` + `--ec-transition-slow`（200ms）；旋转 / 进度 / 扫光用 `linear`。stagger 用 `--ec-motion-stagger`（60ms），总时长 ≤ `--ec-motion-stagger-max`（200ms），且只允许用于首屏少量模块。
- **动画属性**：只对 `transform` / `opacity` 做动画，禁止动画 `width` / `height` / `top` / `margin` 等触发布局重排的属性。
- **降级与可打断**：所有动画必须能被后续交互打断；`prefers-reduced-motion: reduce` 下由 `global.css` 全局降级并由动效封装层二次兜底，循环动画改为静态；组件卸载时必须取消在途动画。
- **消息流禁令**：消息列表、会话列表、搜索结果的逐条渲染不得使用 >200ms 的动画或逐条 stagger；禁止消息逐条「传送带」入场、逐字浮现、全局 1.5s 循环扫光与图片 LQIP / blur-up（后者需服务端缩略图能力，跨 §7.2）。动效不得改变 `seq` 连续、发送幂等、断线补拉、贴底与已读上报语义。
- **依赖口径**：允许引入轻量动画库（主线 `motion` / Motion One，MIT，须符合 §2.3 白名单并在 `core/motion.ts` 封装，保留 Web Animations API 零依赖降级路径）；**禁止更换前端框架**、禁止引入新图标库、禁止 GSAP（standard license 非白名单）/ Lottie / Framer Motion 等重型或受限许可库。新增依赖须核 LICENSE 并记录 gzip 实测增量（§14.6）。
- **验收**：本批次动效的目视 / 多端验收按用户裁决暂缓至代码修改完成后进行；届时需按 §11.4 申请范围与耗时。
