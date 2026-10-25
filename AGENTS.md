# AGENTS.md — EiseChat 项目约束

> 本文件是仓库级硬约束，任何 AI / 开发代理在改动本仓库前必须阅读并遵守。
> 唯一现行计划：`docs/internal-plan.md`（V2.3.4）。文档职责与优先级见 §1（项目介绍以 `README.md` 为准，项目说明与目录架构见 `docs/project-overview.md`）。
> 本文件版本 1.6（2026-10-25），随计划变更时同步更新（1.6 仅按 M0 实际工程补全 §8 命令与文档状态，规则内容不变）。

## 1. 文档分工与优先级

本仓库按「**一处讲介绍、一处放约束**」分工，禁止在 AGENTS 里重复项目介绍，也禁止把可执行约束只写在 README：

| 文档 | 唯一职责 | 不放什么 |
|---|---|---|
| `README.md` | **项目介绍与入口**：项目是什么、给谁用、规模与形态、技术栈概览、文档索引、里程碑、重要边界 | 可执行约束、逐文件目录与实现细节 |
| 本文件 `AGENTS.md` | **仓库级硬约束**：必须做/禁止做什么、技术栈锁定、协议可靠性要求、工作流、测试与提交规范、DoD | 项目介绍（一律放 `README.md`） |
| `docs/internal-plan.md` | 唯一现行计划：做什么、何时做、协议与数据模型、验收与风险 | 硬约束（以本文件为准） |
| `docs/project-overview.md` | 详细项目说明与文件级目录架构：模块怎么分、代码放哪、依赖方向 | 二次定义硬约束 |
| `protocol/` | 协议帧与表结构唯一来源 | 业务约束 |

- **优先级**：本文件 §2 硬规则 > `docs/internal-plan.md` > 其他文档。
- **项目介绍以 `README.md` 为准**：改动项目定位、形态、规模等介绍性内容时先改 README，再同步其他文档（本文件不再重复介绍项目）。

## 2. 硬规则（不可违反，违反即返工）

1. **不引入第三方 IM**：OpenIM、融云、环信、腾讯云 IM、Sendbird 等一律不用；IM 协议由本项目自研。
2. **不产生授权费用**：不买商业授权/SDK、不买签名证书、不买 Apple 开发者账号、不接付费推送或短信服务。
3. **依赖许可**：只允许 MIT / Apache-2.0 / BSD / ISC 等宽松许可；**禁止** GPL / AGPL / SSPL / BUSL 或商业限制许可进入仓库。新增 npm/NuGet 依赖前检查 LICENSE，不确定先问用户。
4. **架构边界**：不引入 Docker / Kubernetes / Redis / Kafka / MongoDB / PostgreSQL / MinIO / Nginx；不引 SignalR 等框架依赖（WS 手写）；不拆微服务。服务端必须保持单进程可部署。
5. **范围边界**：不写 iPhone/iOS 代码、不做 TestFlight；不做语音/视频通话、朋友圈、AI、机器人、E2EE；公开发布类事项（ICP、等保、短信、厂商推送）不在本项目范围。
6. **密钥纪律**：token、密码、验证码明文、SMTP 凭据、JWT 密钥、keystore 一律不进源码/日志/示例/提交历史；放 `secrets.json`（gitignore）或环境变量。日志禁止输出敏感值。
7. **协议兼容**：协议帧与数据库结构只允许向后兼容的增量修改（新增可选字段/新 type 可以，改语义/删字段不行）；先改 `protocol/protocol.md` 与计划文档，再改实现。
8. **数据归属**：服务端是唯一事实源（SQLite + `/data/files`）；客户端可用 IndexedDB 做缓存，但不得只存在本地；不接云数据库/云存储（备份到对象存储除外）。
9. **不破坏既有文件**：不删除、不重命名用户已有文件或目录；执行删除/覆盖前确认目标，必要时先问。
10. **工程规范（同属硬规则）**：测试文件只放 `docs/test/`（新建子目录先申请）、提交信息必须用 `类型：[编号]-简述`、默认不跑测试、测试按缺口增加；细则见 §11。

## 3. 技术栈（固定，不自选替代）

| 层 | 选型 |
|---|---|
| 服务端 | ASP.NET Core（.NET 8/10 LTS）+ Kestrel，REST + WebSocket + 静态前端一体 |
| 实时通信 | 自研 JSON-over-WebSocket（一行一帧，带 `type`，DTO 放 `Chat.Protocol`，可生成 TS 类型） |
| 数据 | SQLite（WAL）+ EF Core，单文件 `/data/chat.db`；迁移放 `server/Chat.Server/Data/Migrations` |
| 认证 | 邮箱验证码 + 可选密码；JWT HS256（默认 7 天）；密码 PBKDF2-SHA256 ≥100,000 次迭代或 Argon2id |
| 邮件 | 可配置 SMTP（企业邮箱/QQ 邮箱授权码）或免费邮件 API（Brevo/Resend 等）；`Development` 输出到控制台（不写日志文件）+ 固定测试码 |
| 前端 | Vue 3 + TypeScript + Vite + Pinia + 轻量 UI；核心模块 `core/ws.ts`、`core/sync.ts`、`stores/` |
| Windows 壳 | Electron + electron-builder（NSIS，`appId=com.eisechat.desktop`，安装包 `EiseChat-Setup-x.y.z.exe`，免签名） |
| Android 壳 | Capacitor，Web 资源打进 APK（不依赖在线加载），最低 API 26，自签名 keystore 放 `infra/keys/`（gitignore），APK 名 `EiseChat-x.y.z.apk` |
| 部署 | Ubuntu 22.04/24.04 + Caddy 自动 HTTPS（反代 `127.0.0.1:8080`）+ systemd；备份到对象存储低频/异机 |
| 测试 | xUnit（服务端）+ Vitest（前端核心）单测随工程提交；临时脚本、验证数据与输出放 `docs/test/`（细则见 §11.1）；不搞重型测试体系 |

> 依赖与架构禁区以 §2 硬规则为准（本表只固定选型，不重复列禁用清单）。

## 4. 目录与命名

- 目标结构见 `docs/project-overview.md` §7（文件级，M0 按此建骨架）与计划附录 B（概要）：`apps/web`、`apps/desktop`、`apps/mobile`、`server/{Chat.Server,Chat.Server.Tests,Chat.Protocol}`、`protocol/`、`infra/`、`docs/`；测试文件位置见 §11.1（`docs/test/`，不入库）。
- 不提交：`node_modules/`、`bin/`、`obj/`、`dist/`、`.electron-builder/`、`*.db`、`data/`、`logs/`、`infra/keys/`、`secrets.json`、`*.keystore`。
- 命名：C# PascalCase；TS 组件/类型 PascalCase，函数/变量 camelCase；REST 路径小写复数（`/api/conversations`）；WS 帧 `type` 点分隔（`message.send`）；数据库表/列 snake_case；时间统一 UTC 毫秒存储、前端本地化显示。
- 语言：文档、注释、提交说明用中文；代码标识符、日志键、数据库字段用英文。
- 协议唯一来源：`protocol/protocol.md`；表结构唯一来源：`protocol/schema.sql`（与 EF 迁移同步维护）。
- 新增项目文档一律写在 `docs/`，不散落到仓库根或代码目录（`README.md` 除外）。

## 5. 协议与消息可靠性（实现必须满足，验收会逐条测）

1. **会话内单调 `seq`**：事务内 `seq = max(seq)+1` 落库，严格 +1，不跳号、不改号。
2. **幂等发送**：`clientMsgId`（客户端 UUID）在 `(conversation_id, client_msg_id)` 唯一；重复发送直接回 `message.ack`，不重复落库。
3. **发送状态机**：本地队列「发送中」→ 收到 ack 变「已发送」→ 2s/5s/10s 重试 3 次仍失败标「失败，可重发」。
4. **接收去重排序**：按 `messageId` 去重、按 `seq` 排序；发现 `seq > lastSeq+1` 立即 `sync.request`。
5. **补拉同步**：每个会话保存 `lastSeq`；启动、重连、切回前台触发；`hasMore=true` 继续拉直到追平；每会话每批 ≤200 条。
6. **未读**：未读 = 会话最新 `seq` − 我的 `last_read_seq`；打开会话滚到底部上报已读。
7. **单活动连接**：一个账号只保留一个活动连接，新登录踢旧连接（发 `kicked` 帧）；断线不删数据，靠 `seq` 补拉。
8. 心跳：客户端 25 秒 `ping`，30 秒无响应判断线；重连指数退避 `1/2/5/10/30s` + 随机抖动。

帧清单与字段见计划 §4.2；表结构与字段见 §4.4；不得擅自改字段名或语义。

## 6. 数据与安全

- 密码：PBKDF2-SHA256（≥100,000 次、随机盐）或 Argon2id；验证码只存哈希。
- JWT：HS256，密钥首次启动生成写入 `secrets.json`，有效期内可配置。
- 限流：同邮箱 60 秒/次、10 次/天；同 IP 20 次/小时；验证码错误 5 次作废；登录失败 5 次锁 5 分钟。
- 文件：默认上限 50MB（可配）；下载只走 `GET /api/files/{id}` 并校验会话成员；不暴露磁盘路径；上传校验 MIME/扩展名；图片发送前客户端压缩（最长边 1280px、约 ≤300KB）。
- 日志：滚动保留 30 天；错误返回 traceId；禁止记录 token/密码/验证码/邮件明文。
- 公网入口：8080 只监听 `127.0.0.1`；Caddy 自动 HTTPS；安全组只开必要端口；可用 fail2ban。
- 备份：每日备份 SQLite（在线备份 API）+ 文件目录到对象存储/异机；恢复步骤写文档并演练。
- 明确不做 E2EE（内部信任模型），使用说明中写清楚。

## 7. 工作流（每个任务都按此执行）

1. 先读 `docs/internal-plan.md` 相关章节，确认任务属于 M0~M6 或 P2 清单；计划外需求先问用户，不擅自动工。
2. 小步实现：先改协议/表结构文档 → 服务端 → Web 核心 → 壳/安装包；每一步保持可构建、可运行。
3. 验收前自检：`dotnet build` + `npm run build` 通过；默认做代码级逻辑验证（§11.2），不自动跑测试；消息相关改动在里程碑验收时用两个客户端实测「发送→断线→重连补拉」无丢无重（M1/M2/M6）。
4. 同步文档：受影响文档（`README.md`、`protocol/`、部署说明）必须更新；计划级变更追加到计划附录 D「更新记录」并告知用户。
5. 交付说明（每个任务必写）：**修改文件、修改原因、验证方式、测试结果、剩余风险**；测试结果须与实际执行一致（未执行的测试不得声称通过，见 §11.2）。
6. 未经用户明确要求，不执行 `git push`、`git reset --hard`、`git clean -fd`，以及发布、外发文件等对外动作。

## 8. 常用命令（M0 已建好工程，按实际脚本补全；与 README 保持一致）

```bash
dotnet build server/Chat.Server.sln             # 服务端构建
dotnet run --project server/Chat.Server         # 服务端启动（http://127.0.0.1:8080）
dotnet test server/Chat.Server.Tests            # 服务端单测（默认不跑，见 §11）
cd apps/web && npm install && npm run dev       # Web 开发（http://127.0.0.1:5173）
cd apps/web && npm run build                    # Web 构建（产物 dist/，不入库）
cd apps/desktop && npm install && npm run dev   # Electron 开发（需先启动 Web dev server）
cd apps/desktop && npm run dist                 # NSIS 安装包（M4 验证）
cd apps/mobile && npm install && npm run sync   # 同步 Web 产物到 Android 工程
cd apps/mobile/android && ./gradlew assembleRelease  # APK（M5 验证，需 Android SDK/Gradle）
infra/install.sh && infra/backup.sh             # 服务器安装/备份（M4 前创建）
```

> 默认不执行测试命令；确需运行时按 §11.3 分批跑最小范围，超过约 2 分钟或全量/压测/e2e 先申请（§11.4）。

## 9. 完成的定义（DoD）

- [ ] 构建通过；改动有验证记录（默认代码级逻辑验证；如按 §11 执行了测试，附命令与结果，零用例不算通过）。
- [ ] 协议/数据模型改动已同步 `protocol/` 文档。
- [ ] 消息链路改动：seq 连续、幂等、断线补拉无重复/空洞（附验证方式）。
- [ ] 安装包相关改动：exe/apk 可安装运行；覆盖安装/卸载不影响服务端数据。
- [ ] 文档与 README 同步；用户可理解的交付说明。

## 10. 会话边界与后续

- P2 待办（不提前做）：语音消息、消息搜索、撤回、多设备、群已读、自动更新、Android 前台服务保活。
- iPhone 仅当用户明确启动后续版本时再评估（PWA / TestFlight 方案见计划 §5.4），禁止现在写 iOS 代码。
- 超出本文件或计划范围的提案：先给用户 1 段说明 + 影响评估（工作量、依赖、许可、风险），确认后再做。

## 11. 测试、提交与交付规范

> 本章为硬性要求（同 §2，违反即返工）：测试文件位置、默认不跑测试、分批与申请规则、提交信息格式、交付说明五要素、密钥与 git 安全，均为必做项。

### 11.1 测试文件位置

- 测试脚本、一次性补丁/检查脚本、验证用例、测试数据与输出，统一放 `docs/test/`（已 gitignore、不入库，可随时删除）。
- 可按功能新建子目录（如 `docs/test/m3-files/`），但**新建子目录前必须先向用户申请**并说明用途。
- 工程内正式单测（xUnit 在 `server/Chat.Server.Tests`、Vitest 在 `apps/web`）属于代码，随工程提交，不放 `docs/test/`。
- 不再新建仓库根级 `tests/` 目录（由 `docs/test/` 取代）。

### 11.2 默认不运行测试（先做代码级逻辑验证）

- 除用户当轮明确要求，**不启动** `pytest` / `vitest` / `mvn` / `playwright` / 容器内集成等测试运行。
- 改完先做**代码级逻辑验证**：阅读改动代码与调用链、`grep` 引用、类型与契约自检（DTO/接口/参数一致），确认逻辑无问题后再交付。
- 未实际执行的测试不得声称通过；零用例执行也不能算「测试通过」。

### 11.3 确需测试时：分批、局部、短时

- 把测试拆成多个小批次，每批只跑与改动直接相关的**最小范围**（单文件、单类或单用例）。
- 跨端契约改动要覆盖实际受影响的调用端（如 DTO 改动同时验证服务端与前端调用处）。
- 鉴权、发布、删除等关键路径保留**最小回归**。
- 非必要不跑全量；批量越大，反馈越慢、越容易掩盖局部问题。

### 11.4 长时/全量测试必须先申请

- 预计超过约 **2 分钟**的测试、基准、压测、e2e、全量回归，以及确需扩大测试范围时，先向用户说明**范围、必要性、预计耗时**，取得同意后再启动；未获批准不得启动。

### 11.5 测试按缺口增加

- 优先复用现有用例；必要时在现有文件补**最小回归**。
- 只有现有文件无法合理承载时才新建测试文件。
- 不为简单文案、样式或等价重构新增重复测试。

### 11.6 提交信息格式

- 格式：`类型：[编号]-简述`；简述用中文；需要时另起一行写具体描述。
- 编号（如 `[M3-03]`）有则写、没有可省略。
- 示例：`feat：[M3-03]-添加了新的内容`

| 类型 | 含义 |
|---|---|
| `feat` | 新功能 |
| `fix` | 修补 bug |
| `docs` | 文档 |
| `style` | 格式（不影响代码运行） |
| `refactor` | 重构（非新功能、非修 bug） |
| `chore` | 构建过程或辅助工具 |
| `revert` | 撤销、版本回退 |
| `perf` | 性能优化 |
| `test` | 测试 |
| `improvement` | 改进 |
| `build` | 打包 |
| `ci` | 持续集成 |

### 11.7 提交与密钥安全

- 提交与密钥纪律见 §7.6 与 §2.6（本处不重复列清单）；提交前额外检查 `git status` 与待提交内容，确认没有密钥或临时文件混入。

## 附：关键文件

- 计划：`docs/internal-plan.md`（唯一现行版）
- 说明与目录架构：`docs/project-overview.md`
- 不提交清单：`.gitignore`
- 协议：`protocol/protocol.md`（M0 创建）
- 测试与临时脚本：`docs/test/`（gitignore，不入库，细则见 §11.1）
- 说明：`README.md`
- 约束：本文件
