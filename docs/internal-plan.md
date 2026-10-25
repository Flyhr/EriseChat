# 内部版执行计划（V2.3.5）：自研轻量 IM + 可安装客户端

> 变更原因（用户 m00240 + m00264 + m00289 + m00356）：**内部使用、不在网络发布；要有安装程序；不买授权；自研底层慢慢完善。** 已明确：Windows 桌面为主 + 少量 Android；**移动端先只做 Android，iPhone 仅出方案、不实现**；云服务器用 **2C4G 轻量**；峰值 **≤100 在线**；账号用**邮箱验证码**注册/登录，不用手机号；**注册不做限制**；邮箱域名白名单默认**留空 = 不限制**，做成管理员可配置项；域名已有，开发期先用占位域名。
> 本文是唯一现行计划（V2.3.5），旧计划 V1.1/V2.0 已按用户要求删除。V2.3 相对 V2.2 的变化：部署参数确认（2C4G 轻量 / 占位域名 / 不限制注册 / 白名单后台可配），成本按“域名已有”修正。V2.3.1 为文档一致性修订（成本口径、去掉 Docker 表述、敏感值只出控制台、补目录架构文档），不改变需求与范围；V2.3.2 按用户要求补充工程规范（测试文件统一放 `docs/test/`、默认不跑测试、提交信息格式 `类型：[编号]-简述`、任务交付说明五要素、禁止 `git push`/`git reset --hard`/`git clean -fd`；明确文档分工（`README.md` 为项目介绍唯一出处、`AGENTS.md` 仅放约束）），详见附录 D。V2.3.3 为 M3 库表增量（messages 增可选 file_id，支撑文件消息下载鉴权与孤儿清理），不改变需求与范围；V2.3.4 为 M3-04 群聊服务端（成员管理语义、系统消息事件名、conv.updated 移除通知；新增 `App:GroupMaxMembers` / `App:GroupOnlyOwnerCanInvite`），不改帧格式与库表。V2.3.5 为 M3-05 已读/typing/presence 语义校准（read.update 只增不减与越界校验、新成员 last_read_seq 初始值、typing 服务端节流、presence 0↔1 推送、移出/退出只收 removedConversationIds），不改帧格式与库表。

---

## 0. 一页结论

- **结论：可行，成本低、工期短。** 云服务器 + 单进程 + SQLite，不需要备案主体（内部使用）、不需要买任何软件授权。
- **方向：自研轻量 IM 底层**（不用 OpenIM 全家桶）。内部 ≤100 在线，核心需求只有账号、单聊、群聊、图片/文件、断线补拉、可安装客户端；用**单进程 + SQLite + 本地文件 + 自写 JSON-over-WebSocket 协议**即可，服务端核心约 2,000~3,000 行。
- **交付物（重点：必须有安装程序）：**
  1. **Windows 桌面安装包**：`EiseChat-Setup-x.y.z.exe`，双击安装、开始菜单/桌面快捷方式、可卸载。
  2. **Android APK**：`EiseChat-x.y.z.apk`，侧载安装（无需应用商店、无需 Google 服务）。
  3. **iPhone：本期只出方案、先不实现**（移动端本期只交付上条 Android APK）。后续若启用，首选 PWA（零成本）；原生 iOS 需 $99/年开发者账号 + Mac + 签名/审核，暂不购买。详见 §5.4。
  4. **服务器部署**：云服务器 2C4G（Ubuntu 22.04/24.04），`install.sh` 注册 systemd + Caddy 自动 HTTPS；不使用 Docker（见 AGENTS.md 硬规则 §2.4）。
- **账号：邮箱验证码注册/登录**（6 位数字、10 分钟有效、限流）；邮箱域名白名单**默认留空 = 不限制**、管理员后台可配；注册不限制、邀请码可选；不接短信、零短信费用；开发期可 mock 验证码。
- **成本：软件授权 0 元。** 必须支出 = 云服务器（约 60~150 元/月）；域名已有（0 元/年）；TLS、邮件免费额度、Android 自签名、Apple 开发者（不买）均免费。合计约 **60~150 元/月**。
- **周期：约 6~7 周 MVP**（1~2 人 + AI 辅助；邮箱验证码与云部署比纯局域网方案多约 3~5 天）。之后按 P2 清单慢慢完善。

**明确不做的功能**：短信/手机号登录、音视频通话、朋友圈、机器人、端到端加密、多租户、集群、应用商店上架、系统级离线推送（v1 不做，打开应用即同步）、**iPhone 客户端（本期仅方案，不实现）**。

**如果以后要对外发布**：再回到 V1.1 的合规与扩容路线；本计划的协议与数据模型可保留、向前兼容。

---

## 1. 需求边界（内部版）

| 维度 | 决策 | 说明 |
|---|---|---|
| 使用范围 | 公司/团队内部，10~100 个账号，峰值 ≤100 在线 | 不面向社会公众 |
| 网络 | **云服务器（公网域名 + HTTPS）** | 安全组只开必要端口；注册默认不限制（内部使用），可按需开启邮箱域名白名单/邀请码 |
| 注册方式 | **邮箱验证码注册/登录**（不接短信） | 6 位验证码、10 分钟有效、限流；**默认不限制注册**，邮箱域名白名单留空 = 不限制（管理员可配） |
| 客户端 | Windows 安装包 + Android APK | Windows 主入口；Android 侧载；**iPhone 仅方案、本期不实现** |
| 服务器 | 云服务器 2C4G（Ubuntu 22.04/24.04），单进程 | 40~80GB SSD；数据 = 1 个 SQLite 文件 + 文件目录 |
| 合规 | **不需要** ICP、等保、安全评估、实名、内容审核 | 不对外运营；但公网入口需基础安全（TLS、限流、白名单、及时更新补丁） |
| 授权费用 | 软件 0 元；仅付云服务器（域名已有） | 不买商业授权、推送、签名证书、Apple 开发者账号 |
| 运维 | 服务自启 + Caddy 自动 HTTPS + 每日备份到对象存储/异机 | 1 人可维护，不需要专职运维 |
| 未来扩展 | 保留协议与数据边界；不提前实现 | 语音消息、搜索、撤回等按 P2 清单迭代 |

---

## 2. 技术选型

| 层 | 选型 | 理由 |
|---|---|---|
| 服务端 | **ASP.NET Core（.NET 8/10 LTS）单进程**，Kestrel 托管 REST + WebSocket + 静态前端 | 一个二进制就是全部后端；安装简单 |
| 实时通信 | **自研 JSON-over-WebSocket 协议**（不引入 OpenIM/SignalR 依赖） | 规模小、可控、可慢慢完善；协议透明 |
| 数据库 | **SQLite（WAL 模式）+ EF Core**，单文件 `/data/chat.db` | 峰值 ≤100 在线完全够用；备份 = 复制文件；后续可换 PostgreSQL |
| 文件存储 | **本地磁盘目录** `/data/files`（按年月分目录） | 无 MinIO 等外部依赖；加鉴权即可 |
| 认证 | **邮箱验证码 + 可选密码**，JWT；密码 PBKDF2/Argon2id | 无短信费用；内部账号可控 |
| 邮件服务 | **可配置 SMTP**（企业邮箱/QQ 邮箱授权码）或**免费邮件 API**（Brevo/Resend 等，约 100~300 封/天） | 验证码量级很小；开发期可 mock |
| 客户端核心 | **Vue 3 + TypeScript + Vite + Pinia + 轻量 UI** | 一套代码复用到桌面壳、Android 壳、PWA |
| Windows 壳 | **Electron + electron-builder（NSIS 安装包）** | 安装包成熟、免签名可用、开发快；体积约 80~120MB（可接受） |
| Android 壳 | **Capacitor**（打包静态 Web 资源）+ Gradle 自签名 APK | 免商店、免 Google 服务；一套代码出 APK |
| iPhone | **本期不实现，仅保留方案**（PWA / TestFlight 对比见 §5.4） | 先把 Android 做好；iOS 后续按需评估，不买 $99 开发者账号 |
| 接入与 TLS | **Caddy + Let's Encrypt 自动 HTTPS**（一个普通域名） | 公网访问必须 HTTPS；证书免费自动续期 |
| 云服务器 | 2C4G / 40~80GB SSD / Ubuntu 22.04 或 24.04 | 峰值 ≤100 在线足够；约 60~150 元/月 |
| 备份存储 | 对象存储低频（约 10GB）或另一台机器 | 约 1~5 元/月；保证异机备份 |
| 测试 | xUnit（服务端）+ Vitest（前端核心）+ 真机验收 + 简单并发脚本 | 内部项目不搞重型测试体系 |

**不引入**：OpenIM、MongoDB、Kafka、Etcd、Redis、MinIO、Kubernetes、短信服务商、第三方推送 SDK。

---

## 3. 总体架构

```
┌──────────────────────────── 客户端（同一套 Vue 代码） ────────────────────────────┐
│  Windows 桌面（Electron 壳）            Android（Capacitor 壳 / APK）               │
│        安装包 .exe                            侧载 .apk                            │
│  iPhone（PWA）—— 仅计划，本期不实现（不打包、不验收）                                  │
└───────────────────────────────────┬────────────────────────────────────────────┘
                                    │  HTTPS / WSS（公网域名，Caddy 自动证书）
                                    ▼
                    ┌──────────────────────────────┐
                    │  Caddy（443）→ 127.0.0.1:8080 │  TLS / gzip / 静态缓存
                    └──────────────┬───────────────┘
                                   ▼
                 ┌──────────────────────────────────────┐
                 │  ChatServer（ASP.NET Core 单进程）     │
                 │  REST API  /api/*                     │
                 │  WebSocket /ws（自研协议）            │
                 │  静态前端 wwwroot（含 /download 页）   │
                 │  后台任务（清理、备份触发、统计）       │
                 │  邮件发送（SMTP / HTTP API）→ 用户邮箱 │
                 └──────────────────┬───────────────────┘
                                    ▼
                 ┌──────────────────────────────────────┐
                 │ SQLite /data/chat.db（WAL）           │
                 │ 文件目录 /data/files/yyyyMM/...       │
                 │ 备份目录 /data/backup/（同步到对象存储）│
                 └──────────────────────────────────────┘
```

**部署形态（定案：云服务器）**

- 云服务器 2C4G（Ubuntu 22.04/24.04），单机单进程，systemd 自启。
- 安全组只开 22（限管理员 IP）、80、443；应用端口 8080 只监听 `127.0.0.1`，不直接暴露公网。
- 域名 A 记录指向服务器公网 IP，Caddy 自动申请/续期 Let's Encrypt 证书（HTTPS + WSS）。
- 开发/测试期先用占位域名 `chat.example.com`（Caddy、客户端默认地址均可用它跑通），拿到真实域名后统一替换。
- 可选私网/零公网方案：WireGuard 或 Tailscale，客户端先连 VPN 再访问内网地址（不需要公网端口与域名，但每个客户端要装 VPN，管理成本更高）。
- 邮件：出站走 SMTP（465/587）或邮件 API，不依赖 25 端口；发件域名配置 SPF/DKIM 降低进垃圾箱概率。

**为什么单进程足够**：10~100 个账号、峰值 ≤100 个 WebSocket 连接，SQLite 单写 + 内存连接表远未到瓶颈。真正成本是文件流量与备份，用目录和定时任务即可解决。规模上去（>500 在线）再拆 PostgreSQL/分进程，协议不变。

---

## 4. 自研协议 v1（核心）

### 4.1 邮箱验证码账号体系

**注册流程**
1. `POST /api/auth/email/send`，body `{ "email": "user@company.com", "purpose": "register" }` → 服务端校验注册策略（白名单为空则不限制；邀请码默认关闭）、限流后发送 6 位验证码（10 分钟有效，最多验 5 次）。
2. `POST /api/auth/register`，body `{ "email", "code", "displayName", "password?" }` → 创建账号（`users.email` 唯一、记录 `email_verified_at`）→ 返回 JWT。
3. 未配置邮件服务或开发环境：验证码只输出到开发控制台（stdout，不写入日志文件），并允许固定测试码（仅 `Development`），保证不接真实邮件也能开发。

**登录流程**
- 验证码登录：`POST /api/auth/email/send`（`purpose=login`）→ `POST /api/auth/login/code` `{ "email", "code", "deviceId" }` → JWT。
- 密码登录（用户可选设置）：`POST /api/auth/login/password` `{ "email", "password", "deviceId" }` → JWT。
- 限流：同邮箱 60 秒/次、10 次/天；同 IP 20 次/小时；验证码错误 5 次作废。
- 注册限制：默认**不限制**（内部使用）；管理员可后台配置邮箱域名白名单（空 = 不限制）、关闭注册或开启邀请码。

**会话与连接**
- 登录成功返回 `accessToken`（JWT，默认 7 天，可配置）+ 用户信息 + `serverTime`。
- WebSocket：`ws(s)://<域名>/ws`，连接后首帧 `auth`；服务端返回 `hello`（含心跳间隔、服务器时间）。
- 心跳：客户端每 25 秒发 `ping`；30 秒无响应判定断线。
- 重连：指数退避 `1s / 2s / 5s / 10s / 30s` + 随机抖动；重连成功后自动补拉（见 4.3）。
- 认证失败/被踢 → 服务端发 `error`（`AUTH_EXPIRED` / `kicked`），客户端跳登录页。

### 4.2 帧格式（JSON，一行一帧）

所有帧含 `type`；客户端请求可带 `reqId`，服务端在响应中回传便于匹配。

| type | 方向 | 关键字段 | 说明 |
|---|---|---|---|
| `auth` | C→S | `token`, `deviceId` | WS 首帧认证 |
| `hello` | S→C | `userId`, `heartbeatSec`, `serverTime` | 认证成功 |
| `ping` / `pong` | 双向 | `ts` | 心跳 |
| `message.send` | C→S | `clientMsgId`, `conversationId`, `msgType`, `content`, `replyTo?` | 发消息（带幂等 ID） |
| `message.ack` | S→C | `clientMsgId`, `messageId`, `conversationId`, `seq`, `createdAt` | 落库成功回执 |
| `message.new` | S→C | `message` | 广播新消息 |
| `sync.request` | C→S | `cursors: [{conversationId, lastSeq}]` | 重连/启动补拉 |
| `sync.response` | S→C | `batches`, `hasMore` | 补拉结果（每会话最多 200 条） |
| `conv.list` / `conv.updated` | 双向 | `conversations[]` | 会话列表变更 |
| `read.update` | C→S | `conversationId`, `lastReadSeq` | 更新已读游标 |
| `read.updated` | S→C | `conversationId`, `userId`, `lastReadSeq` | 已读广播（单聊用） |
| `typing` | 双向 | `conversationId`, `userId` | 正在输入（3 秒节流） |
| `presence` | S→C | `userId`, `online` | 在线状态 |
| `kicked` | S→C | `reason` | 同账号在别处登录 |
| `error` | S→C | `code`, `message`, `reqId?` | 统一错误 |

### 4.3 消息可靠性设计（必须实现对）

1. **会话内单调 `seq`**：每个会话维护递增序号，落库时同一事务内计算 `seq = max(seq)+1`，严格顺序。
2. **幂等发送**：`clientMsgId`（客户端 UUID）在 `(conversation_id, client_msg_id)` 上唯一；重复发送直接回 `message.ack`，不重复落库。
3. **发送队列**：消息先进本地队列并标"发送中"；收到 ack 改"已发送"；2s/5s/10s 重试，3 次失败标"失败，可重发"。
4. **去重与排序**：接收端按 `messageId` 去重，按 `seq` 排序；发现 `seq > lastSeq + 1`（缺口）立即 `sync.request`。
5. **补拉同步**：本地保存每个会话 `lastSeq`；启动、重连、切回前台时发起；`hasMore=true` 则继续，直到追平。
6. **未读数**：未读 = 会话最新 `seq` − 我的 `last_read_seq`；打开会话并滚到底部时上报已读。
7. **多设备简化**：v1 一个账号一个活动连接（新登录踢旧连接）；后续多设备把游标按设备存即可，协议不变。

### 4.4 数据模型（SQLite，v1）

| 表 | 关键字段 | 说明 |
|---|---|---|
| `users` | `id`(GUID), `email` unique, `display_name`, `password_hash?`, `email_verified_at`, `avatar_file_id?`, `role`(user/admin), `status`, `created_at` | 邮箱注册/登录；密码可选 |
| `email_codes` | `id`, `email`, `purpose`, `code_hash`, `expires_at`, `attempts`, `created_at`, `ip` | 验证码：10 分钟有效、限流、失败次数 |
| `sessions` | `id`, `user_id`, `device_id`, `jwt_id`, `created_at`, `last_seen_at`, `revoked_at?` | 会话/踢人用 |
| `conversations` | `id`, `type`(direct/group), `title`, `direct_key?` unique, `created_by`, `created_at`, `last_seq`, `last_message_id?` | `direct_key` = 两用户 ID 排序拼接，保证单聊唯一 |
| `conversation_members` | `conversation_id`, `user_id`, `role`(owner/member), `joined_at`, `last_read_seq` | 成员与已读游标 |
| `messages` | `id`, `conversation_id`, `sender_id`, `client_msg_id`, `seq`, `msg_type`(text/image/file/system), `content_json`, `file_id?`, `reply_to?`, `created_at`, `deleted_at?` | unique `(conversation_id, client_msg_id)`；index `(conversation_id, seq)`；index `(file_id)`；不建库级外键（见 schema.sql 注释） |
| `files` | `id`, `owner_id`, `original_name`, `mime`, `size`, `storage_path`, `created_at` | 下载时校验会话成员 |
| `invites` | `code`, `created_by`, `used_by?`, `expires_at`, `created_at` | 邀请注册（可选） |
| `admin_audit_logs` | `id`, `admin_id`, `action`, `target`, `detail_json`, `created_at` | 建号、禁用、备份等操作留痕 |
| `app_settings` | `key`, `value` | 管理员可配置：注册开关、允许邮箱域名（默认空 = 不限制）、是否需邀请码、上传上限等 |

> 时间统一 UTC 毫秒存储，前端本地化显示。

### 4.5 文件与图片

- 上传：`POST /api/files`（multipart，需登录，默认上限 50MB，可配置）→ `{ fileId, name, size, mime }`。
- 发送：消息 `msgType=image|file`，`content={ fileId, name, size, mime }`；图片发送前客户端压缩（最长边 1280px、约 ≤300KB 更省流量）。
- 下载：`GET /api/files/{fileId}`，服务端校验"请求者是否是引用该文件的会话成员"，通过后流式返回；不暴露磁盘路径。
- 存储：`/data/files/yyyyMM/{fileId}{ext}`；后台任务清理无消息引用的孤儿文件。
- 云服务器注意：文件目录在系统盘；如文件多，可挂载数据盘或对象存储（P2）。

### 4.6 群聊（v1 最小集）

- 创建群：选择成员（按邮箱搜索）→ 生成 group 会话 + 系统消息。
- 邀请：成员可邀请（默认所有成员可邀请，可配置仅群主）；上限默认 100 人。
- 退出/移除：成员退出、群主移除成员；系统消息记录。
- 群消息与单聊共用同一套 `seq`/补拉/幂等模型。
- 已读回执：v1 不做群已读详情，避免复杂度（单聊已读保留）。
- 群名：必填 1~100 字符（去首尾空格）；v1 建群后不支持改名。
- 群主（创建者）：不可退出、不可被移除；v1 不支持转让群主或解散群（避免出现空群）。
- 成员上限由 `App:GroupMaxMembers` 控制（默认 100，含群主）；邀请默认所有成员可邀请，`App:GroupOnlyOwnerCanInvite=true` 时仅群主可邀请。
- 新成员加入时 `last_read_seq` 初始化为加入前的会话 `lastSeq`（加入系统消息计 1 条未读，历史消息不计未读）；之后由 `read.update` 按只增不减更新（v1 群聊不展示群已读详情）。

### 4.7 安全（内部级 + 公网入口加固）

- 密码（可选）：PBKDF2-SHA256（≥100,000 次迭代，随机盐）或 Argon2id；禁止明文存储与日志输出。
- 邮箱验证码：只存哈希；白名单为空时不限制邮箱域名，非空时只发白名单域名；同邮箱/同 IP 限流，避免成为垃圾邮件发送器。
- JWT：HS256，密钥首次启动生成写入 `secrets.json`；有效期默认 7 天，可配置。
- 登录防刷：同 IP + 邮箱 5 次失败锁 5 分钟；管理员初始密码首次启动随机生成，只在控制台输出一次（不写入日志文件），登录后应立即修改。
- 公网加固：安全组最小开放；8080 只监听 127.0.0.1；Caddy 自动 HTTPS；启用 ASP.NET 限流；可选 fail2ban；订阅系统安全更新；定期检查日志。
- 数据：SQLite 与文件目录只在服务器；备份同步到对象存储/异机；**不做 E2EE**（内部信任模型，使用说明中明确）。
- 日志：访问/错误/管理员操作日志，滚动保留 30 天。

---

## 5. 客户端设计

### 5.1 Web 核心（Vue 3）

| 页面 | 功能 |
|---|---|
| 登录/注册 | 邮箱 + 验证码登录；新用户邮箱验证码注册（填昵称，可选设密码）；可选密码登录；服务器地址（桌面/APK 首次配置，默认先用占位域名，真实域名后替换）；错误与限流提示 |
| 会话列表 | 最新消息、未读数、时间、在线状态；发起单聊入口 |
| 聊天页 | 消息气泡、时间分组、发送状态（发送中/已发送/失败）、图片预览、文件下载、历史加载、正在输入、已读标记（单聊） |
| 通讯录/建群 | 按邮箱搜索用户、勾选成员建群、群成员管理 |
| 我的/设置 | 修改昵称/密码、通知开关、服务器地址（桌面/APK）、版本信息 |
| 管理页（管理员可见） | 建号/邀请码、重置密码、禁用账号、注册开关与邮箱域名白名单（留空 = 不限制）、查看统计、手动备份 |

前端模块：

- `src/core/ws.ts`：连接状态机（connecting/open/reconnecting/closed）、帧编解码、心跳、自动重连、发送队列、请求响应匹配。
- `src/core/sync.ts`：补拉逻辑——保存 `lastSeq`（IndexedDB），启动/重连/回前台触发 `sync.request`，去重排序。
- `src/stores/`：Pinia（auth、conversations、messages、contacts、settings）。
- `src/pages/`、`src/components/`、`src/api/`：页面、组件、REST 封装。
- 本地缓存：IndexedDB（会话 + 最近消息），刷新后先渲染本地再后台补拉。

体验细节：未读角标（标题 + 托盘/任务栏 badge）、新消息提示音（可关）、断网顶部横幅、发送失败可重试、粘贴图片直接发送（可选）。

### 5.2 Windows 桌面壳（Electron）

- 结构：`apps/desktop/`（`main.ts` 主进程、`preload.ts`、`ipc.ts`），加载 `apps/web` 构建产物。
- 安全默认：`contextIsolation: true`、`nodeIntegration: false`、`sandbox: true`、CSP、限制 `will-navigate` 与 `setWindowOpenHandler` 只允许配置的服务器域名。
- 能力：系统托盘图标 + 未读角标；关闭到托盘（可设置）；新消息系统通知（应用运行期间）；开机自启（可选）；窗口大小/位置记忆；单实例锁。
- 打包：`electron-builder` → **NSIS 安装包** `EiseChat-Setup-1.0.0.exe`；每用户安装（无需管理员）；开始菜单/桌面快捷方式 + 卸载器；`appId=com.eisechat.desktop`。
- 签名：不做代码签名（证书要花钱）；首次运行 SmartScreen 提示"未知发布者"，选"仍要运行"；下载页写明步骤。
- 更新：v1 手动下载新安装包覆盖安装（数据在服务端，本地无需迁移）；P2 可加 electron-updater + 内网/对象存储更新目录。

### 5.3 Android 壳（Capacitor）

- 结构：`apps/mobile/`（Capacitor 工程），Web 资源随 APK 打包（不依赖在线加载），服务器地址在登录页/设置页填写。
- 网络：允许 HTTPS（默认）；如测试期间用 IP 直连可临时允许明文（`usesCleartextTraffic=true`），仅限内网。
- 图标/启动页：统一 App 图标生成；最低 Android 8.0（API 26）。
- 打包：`keytool` 生成自签名 keystore（`infra/keys/`，**加入 .gitignore 并离线备份**）→ Gradle `assembleRelease` → `EiseChat-1.0.0.apk`。
- 安装：手机允许"安装未知应用"→ 打开 APK；下载页提供下载与二维码。
- 后台限制（明确告知）：**v1 无系统级推送**，应用在后台可能被系统冻结，新消息不实时；打开应用自动补拉。若内测反馈强烈，P2 加"前台服务保活"插件（仅 Android，耗电增加），仍不需要付费推送。

### 5.4 iPhone（仅计划，本期不实现）

**范围说明：本期移动端只交付 Android，iPhone 不做代码、不打包、不验收。** 下表与结论仅作为后续版本启用时的决策依据。

| 方案 | 成本 | 难度 | 结论 |
|---|---|---|---|
| **PWA（推荐）** | 0 | 低：用户 Safari 打开 → 分享 → 添加到主屏幕 | 日常聊天够用；无安装包、无后台推送 |
| TestFlight | $99/年 Apple 开发者账号 | 中：需 Mac 打包、每个版本上传、审核周期 | 仅为内部 ≤100 人不划算 |
| 免费 7 天侧载（Xcode/AltStore） | 0 | 高：每 7 天重签、设备 UDID 管理 | 不稳定，不适合日常 |
| 企业证书分发 | 数千元/年且易被吊销 | 高 | 风险大，放弃 |

**预定结论（后续版本）：优先 PWA**（零成本、免安装包，Safari 添加到主屏幕）；若届时确需安装包与推送，再评估 $99 TestFlight；企业证书方案放弃。**本期不实现，不影响 MVP 范围与排期。**

### 5.5 下载页

服务器自带页面 `GET /download`（静态 HTML，动态填入版本号）：

- Windows：`EiseChat-Setup-x.y.z.exe`（含 SmartScreen 提示说明）
- Android：`EiseChat-x.y.z.apk`（下载 + 二维码 + 安装步骤）
- iPhone：本期不放（后续版本上线 iOS 时再补 PWA 添加主屏幕说明）
- 更新日志 + 服务器域名 + 联系管理员说明

---

## 6. 安装程序与分发（本计划重点交付物）

### 6.1 Windows 客户端安装包

- 工具链：Node 20+ → `electron-builder --win nsis`。
- 产物：`EiseChat-Setup-1.0.0.exe`（约 80~120MB）。
- 安装行为：每用户安装、默认 `%LOCALAPPDATA%\Programs\EiseChat`、开始菜单/桌面快捷方式、注册卸载项；覆盖安装自动升级版本号。
- 分发：下载页/内网文件共享；无需签名证书；附《安装说明》（SmartScreen 提示处理）。

### 6.2 Android APK

- 工具链：Android SDK + JDK 17 + Gradle（Capacitor 工程自带 wrapper）。
- 步骤：`npx cap sync android` → 配置签名 → `gradlew assembleRelease` → 产物 `app-release.apk`。
- 签名：`keytool -genkeypair -keystore eisechat-release.jks ...` 自签名（免费）；keystore 与密码离线保存并备份（丢失后无法覆盖升级）。
- 分发：下载页/文件分享；首次安装需允许"未知来源"；不需要 Google Play。

### 6.3 服务器部署（云服务器主路径）

**交付形式**：`chat-server-1.0.0-linux-x64.zip`（self-contained，目标机无需装 .NET）；Windows 安装脚本作为可选（办公室常开电脑场景）。

**云服务器一键部署（Ubuntu 22.04/24.04）**
1. 安全组：只开 22（限管理员 IP）、80、443。
2. 域名 A 记录指向服务器公网 IP；安装 Caddy，先用占位域名配置 `chat.example.com { reverse_proxy 127.0.0.1:8080 }`（真实域名后替换），自动 HTTPS。
3. 解压 `chat-server-*.zip` 到 `/opt/eisechat-server`，数据目录 `/var/lib/eisechat`（内含 `chat.db` 与 `files/`，即文档中逻辑上的 `/data`）。
4. 首次生成 `secrets.json`：JWT 密钥、SMTP 凭据等；随机管理员密码只在首次启动时输出到控制台一次（不写入日志文件，`secrets.json` 不存明文密码）。
5. 创建 `eisechat` 系统用户；写入 `/etc/systemd/system/eisechat.service`（`Restart=always`、开机自启）。
6. `systemctl enable --now eisechat` → 访问 `https://<域名>` 完成初始化（设置注册策略：白名单可留空、上传上限）。
7. 配置邮件：SMTP 或邮件 API（见 §4.1），发送测试验证码到管理员邮箱。
8. 配置备份：每日把 `chat.db` + `files/` 打包同步到对象存储/异机（见 6.4）。

**Windows（可选，办公室常开电脑）**：`install.ps1` 解压到 `C:\ChatServer`、生成 secrets、`sc.exe` 注册服务、自动启动、开放防火墙端口、在控制台输出访问地址与管理员密码（仅一次，不写入日志文件）。

**不使用 Docker**：按 AGENTS.md 硬规则 §2.4，服务端直接以 systemd 托管单进程可执行文件；不建 `infra/docker/`、不维护 compose 文件。

**升级**：停服务 → 备份 → 替换程序文件（保留 `/var/lib/eisechat`）→ 启动 → `GET /api/health` 校验版本。
**卸载**：移除服务与程序目录，保留数据目录（提示用户手动删除）。

### 6.4 数据备份与恢复（必须做）

- `backup.sh` / `backup.ps1`：SQLite 在线备份 + 打包 `files/` → `backup/yyyyMMdd-HHmm/`；保留最近 30 份。
- 异机：rsync/scp 到另一台机器或对象存储（约 1~5 元/月），至少一份不在本机。
- 调度：cron / 计划任务每天凌晨 2 点；失败写日志并在管理页提示。
- 恢复演练：新机器安装 → 解压备份 → 指向数据目录 → 启动 → 抽查历史消息与图片。**上线前必须演练一次。**

### 6.5 版本与更新策略

- 版本号 `主.次.修订`（1.0.0）；服务端/桌面/APK 同步；协议版本单独 `protocolVersion=1`。
- 兼容：服务端向后兼容至少一个旧客户端版本；协议改动升 `protocolVersion`，旧客户端提示升级。
- 方式：v1 手动下载新包覆盖安装；桌面壳 P2 加自动更新；Android P2 可加应用内检查更新（下载 APK 提示安装）。

---

## 7. 服务端模块结构

```
server/
├── Chat.Server/
│   ├── Program.cs                 # 启动、DI、Kestrel、静态文件、迁移
│   ├── Api/                       # REST 端点
│   │   ├── AuthEndpoints.cs       # 邮箱验证码、注册、登录
│   │   ├── UserEndpoints.cs
│   │   ├── ConversationEndpoints.cs
│   │   ├── MessageEndpoints.cs    # 历史分页（WS 之外兜底）
│   │   ├── FileEndpoints.cs
│   │   ├── AdminEndpoints.cs
│   │   └── DownloadEndpoints.cs
│   ├── Realtime/
│   │   ├── WsMiddleware.cs        # /ws 握手 + 连接生命周期
│   │   ├── ConnectionManager.cs   # userId → connections（内存在线表）
│   │   ├── FrameCodec.cs          # 帧编解码/序列化（DTO 在 Chat.Protocol）
│   │   ├── MessageHub.cs          # 收帧分发：send/ack/sync/read/typing
│   │   └── HeartbeatService.cs
│   ├── Modules/
│   │   ├── Auth/                  # 邮箱验证码、JWT、登录限流
│   │   ├── Email/                 # SMTP / HTTP API 发送、模板、mock
│   │   ├── Users/
│   │   ├── Conversations/         # 单聊去重、群成员、系统消息
│   │   ├── Messages/              # 发送事务、seq、幂等、同步查询
│   │   ├── Files/                 # 上传/下载鉴权、路径安全
│   │   └── Admin/                 # 建号、禁用、审计
│   ├── Data/                      # DbContext、迁移、种子（初始化管理员）
│   ├── Common/                    # 配置、日志、错误处理、时间工具
│   ├── BackgroundJobs/            # 孤儿文件清理、备份触发、统计
│   └── wwwroot/                   # 前端构建产物 + /download 页
├── Chat.Server.Tests/             # xUnit：协议、seq、幂等、同步、鉴权、验证码
└── Chat.Protocol/                 # 协议 DTO（可生成 TS 类型）
```

**关键实现点**

- `MessageHub` 发送流程：校验成员身份 → 事务内计算 seq → 插入 messages → 更新会话 last_seq → 返回 ack → 向在线成员广播 `message.new`（重复发送由 clientMsgId 唯一约束短路）。
- `EmailCodeService`：生成/校验 6 位验证码（只存哈希）、限流、过期；邮件发送失败记录原因并允许重发；`Development` 环境把验证码输出到控制台（不写入日志文件）、允许固定测试码。
- `ConnectionManager`：`ConcurrentDictionary<Guid, List<WebSocket>>`；单设备策略下新连接踢旧连接；断线不清数据（靠 seq 补拉）。
- 后台任务：孤儿文件清理、每日备份触发、统计。
- 配置：`appsettings.json`（端口、数据库路径、文件目录、上传上限、注册模式、允许邮箱域名、JWT 过期、备份目标）+ `secrets.json`（JWT 密钥、SMTP 凭据等，首次生成；不存明文密码/验证码）。
- 日志：`logs/` 按天滚动；错误含 traceId，客户端错误提示带 traceId 便于排查。

**REST 端点（v1）**

| 方法 | 路径 | 说明 |
|---|---|---|
| POST | `/api/auth/email/send` | 发送验证码（register/login） |
| POST | `/api/auth/register` | 邮箱验证码注册，返回 JWT |
| POST | `/api/auth/login/code` | 邮箱验证码登录 |
| POST | `/api/auth/login/password` | 邮箱 + 密码登录（可选） |
| GET | `/api/me` / PATCH `/api/me` | 当前用户信息、改昵称/密码 |
| GET | `/api/users?search=` | 按邮箱/昵称搜索（建群/单聊用） |
| POST/GET | `/api/conversations` | 创建单聊/群、我的会话列表 |
| GET | `/api/conversations/{id}/messages?beforeSeq=&limit=` | 历史分页 |
| POST/DELETE | `/api/conversations/{id}/members` | 群成员管理 |
| POST/GET | `/api/files` · `/api/files/{id}` | 上传/下载 |
| GET | `/api/health` | 健康检查（版本、DB 状态） |
| GET | `/download` | 下载页（静态） |
| POST 等 | `/api/admin/*` | 管理端（admin 角色） |

WebSocket 为消息主通道；REST 负责登录、历史兜底、文件与元数据。

> **M2 校准（2026-10-25）**：`GET /api/users?search=` 已实现并落地为 `{ users: [...] }`（昵称/邮箱模糊匹配、排除当前用户、最多 20 条，`search` 长度 1~64）；`PATCH /api/me`、群成员、文件与 `admin/*` 未实现，按 M3+ 里程碑补齐；其余端点行为与 M1/M2 实现一致。

> **M3-04 校准（2026-10-25）**：群成员接口落地为 `POST /api/conversations/{id}/members`（body `{ userIds: [] }`，批量邀请，返回更新后的 Conversation）与 `DELETE /api/conversations/{id}/members/{userId}`（退出/移除；目标=自己即退出；成功 204）；`PATCH /api/conversations/{id}` 未实现。

---

## 8. 开发路线（6~7 周 MVP，按周验收）

> 邮箱验证码、云部署、域名/HTTPS 相比纯局域网版多约 3~5 天；云资源与邮件通道可在 M0 并行准备，不阻塞编码。

| 阶段 | 时间 | 内容 | 验收标准 |
|---|---|---|---|
| **M0 决策与骨架** | 3~4 天 | 云厂商/地域选型（默认 2C4G 轻量）、占位域名准备、邮箱发送通道；定协议与表结构；建仓库骨架（可与编码并行） | 仓库可构建；协议/DDL 评审通过；云主机可 SSH、占位域名可解析 |
| **M1 服务端核心 + 邮箱账号** | 第 1~1.5 周 | 登录/JWT、邮箱验证码发送/校验（SMTP + mock）、限流、WS 框架、心跳、message.send/ack、seq、sync、历史分页、SQLite 迁移、xUnit | 邮箱验证码注册/登录成功；两账号互发 100 条无丢无重有序；服务重启后补拉完整；限流生效 |
| **M2 Web 客户端（登录 + 单聊）** | 第 2 周 | 登录/注册页、会话列表、聊天页、发送状态、seq 去重、断线补拉、本地缓存 | 两台浏览器邮箱注册登录互聊；断网 1 分钟恢复后消息补齐且不重复；刷新后聊天恢复 |
| **M3 群聊 + 图片文件 + 已读** | 第 3 周 | 建群/邀请/退出、图片压缩发送、文件上传下载、未读/已读、typing、系统消息 | 3 个账号完成建群聊天；图片/文件可收发；未读数正确 |
| **M4 桌面安装包 + 云端部署** | 第 4 周 | Electron 壳（托盘/通知/单实例）、NSIS 安装包；云服务器部署（Caddy HTTPS、systemd、邮件配置、备份到对象存储）、下载页 | 干净 Windows 装 exe 能登录聊天；公网 `https://域名` 可访问；服务器重启自启、数据不丢；备份生成并同步成功 |
| **M5 Android APK** | 第 5 周 | Capacitor 工程、图标、HTTPS 网络配置、自签 APK、真机测试、二维码分发 | 2 台 Android 真机安装 APK 并与桌面互聊；杀进程重开补拉正常 |
| **M6 内测与打磨** | 第 6~7 周 | 100 连接稳定性、错误处理、日志、备份恢复演练、使用文档、10~20 人试用（Windows + Android） | 试用一周无阻塞问题；恢复演练通过；内测问题清单闭环 |

**P2（内测后慢慢完善）**：语音消息、消息搜索、消息撤回、桌面自动更新、多设备同步、群管理增强（禁言/公告）、暗色主题、消息导出、**iPhone 方案启用评估（仅计划，不在本期）**、对象存储文件、端到端加密（专项评估）。本期不含 iPhone 客户端：iOS 相关只写方案（§5.4），不排期、不测试。

---

## 9. 成本（月度）

| 项目 | 方案 | 费用 |
|---|---|---|
| 软件授权 | 全部自研 + 开源依赖（Vue/Electron/Capacitor/ASP.NET Core 均为 MIT/Apache） | **0** |
| 云服务器 | 2C4G / 40~80GB SSD（阿里云/腾讯云轻量或 ECS） | **约 60~150 元/月**（年付更低） |
| 域名 | 已有域名 | **0 元**（开发期先用占位域名 `chat.example.com`） |
| TLS 证书 | Let's Encrypt / Caddy 自动申请续期 | **0** |
| 邮件发送 | 免费额度（Brevo/Resend 约 100~300 封/天）或企业邮箱 SMTP | **0**（内部验证码量级很小） |
| 短信 | 不使用（邮箱验证码替代） | **0** |
| 推送 | v1 不做系统推送 | **0** |
| Android 签名 | 自签名 keystore | **0** |
| Apple 开发者 | 不买（iPhone 本期不实现） | **0** |
| 代码签名证书 | 不买（SmartScreen 提示可接受） | **0** |
| 备份存储 | 对象存储低频约 10GB 或另一台机器 | **约 1~5 元/月** |
| **合计** | | **约 60~150 元/月** |

> 对比原 OpenIM 方案：8C32G 服务器（≥2,000 元/月）+ 合规投入（几万元）+ 授权/推送费用，成本下降一个数量级。

---

## 10. 与原计划的差异（V1.0/V1.1 → V2.3 内部版）

| 项 | V1.0 / V1.1 | V2.3 内部版 |
|---|---|---|
| IM 引擎 | OpenIM Server（+ 可选 ChatServer） | **自研协议**（JSON over WebSocket） |
| 依赖组件 | MongoDB/Kafka/Etcd/Redis/MinIO，8C16G~8C32G | **单进程 + SQLite + 本地文件**，2C4G 云服务器 |
| 授权 | OpenIM 客户端 AGPL / 商业授权问题 | **全部自有代码 + MIT/Apache 依赖，零授权费** |
| 客户端 | PWA / PWA+Capacitor 壳 | **Windows 安装包 + Android APK**；iPhone 仅方案、本期不实现 |
| 账号体系 | 手机号验证码 + OpenIM 用户同步 | **邮箱验证码注册/登录 + 可选密码**，无短信 |
| 邮件服务 | 无 | **SMTP/免费邮件 API**（验证码），开发期 mock |
| 服务器 | 本地/机房多容器 + Nginx | **云服务器 2C4G + Caddy 自动 HTTPS + systemd** |
| 推送 | APNs/个推/Web Push 网关 | **v1 不做系统推送**（打开即同步），P2 再评估 |
| 合规 | ICP/等保/安全评估/实名/审核 | **不需要**（仅内部使用；公网入口需基础加固） |
| 容量 | 1,000 同时在线压测目标 | 10~100 账号，峰值 ≤100 在线 |
| 工期 | 16 周公开发布路线 | **约 6~7 周 MVP** + 持续迭代 |
| 运维 | 多组件监控/备份/升级 | systemd 自启 + 每日备份到异机/对象存储 |
| 成本 | 8C32G 服务器 + 合规 + 授权 | **约 60~150 元/月**（与 §9 一致） |
| 主要风险 | 合规、推送可达性、WASM 兼容 | **自研协议 bug、邮件送达、公网安全、Android 后台挂起、单机备份（iOS 相关风险延后到后续版本）** |

---

## 11. 风险与对策

| 风险 | 影响 | 对策 |
|---|---|---|
| 自研协议出现丢消息/重复/乱序 | 核心体验受损 | seq + 幂等 + ack 重试 + 缺口补拉 + M1 单测/集成测试（硬性验收） |
| 邮箱验证码进垃圾箱/发送失败 | 用户无法注册/登录 | 发件域名配置 SPF/DKIM；用免费 API 的已验证发件；保留密码登录兜底；开发期 mock 验证码 |
| 邮件服务限额/SMTP 被封 | 验证码中断 | 抽象邮件接口，可切换 SMTP/API/企业邮箱；管理员可临时手动发码或建号 |
| 公网服务器被扫描/恶意注册 | 垃圾账号、安全风险 | 注册开关/邮箱域名白名单（默认空 = 不限制，可按需收紧）/邀请码；登录/发码限流；安全组最小化；fail2ban；及时打补丁 |
| 域名/证书过期 | 客户端无法连接 | Caddy 自动续期 + 到期提醒；备用 IP 直连（自签）方案写入运维手册 |
| 云服务器磁盘/实例故障 | 数据丢失 | 每日自动备份到对象存储/异机；保留 30 份；上线前恢复演练 |
| Android 后台收不到新消息 | 用户误以为消息延迟 | 文档明确"打开即同步"；P2 可选前台服务保活；内测收集反馈 |
| iPhone 本期不实现 | iOS 用户暂不能用 | 已与用户确认：先做 Android；后续按需启用 PWA/TestFlight 方案（§5.4） |
| SQLite 并发上限 | 高峰卡顿 | WAL 模式、短事务；≤100 在线压测；超规模换 PostgreSQL（协议不变） |
| 单机故障（进程崩溃） | 服务中断 | systemd `Restart=always`；健康检查；日志与告警（免费 uptime 监控） |
| 无签名安装包被 SmartScreen 拦截 | 用户不会装 | 下载页图文说明；"仍要运行"步骤；同一套安装包内部分发 |
| 管理员密码/密钥泄露 | 数据风险 | 首次随机密码、可改、操作留痕；`secrets.json` 仅服务器可读；数据只在内网/自己人 |
| 无 E2EE，服务器可见消息 | 隐私边界 | 内部信任模型写入使用说明；敏感信息场景后续单独评估 E2EE |
| 升级破坏数据 | 升级失败 | 迁移脚本 + 升级前强制备份 + 回滚步骤写入发布流程 |

---

## 12. 已确认的部署参数（无待确认项）

| 项 | 已确认值 | 说明 |
|---|---|---|
| 邮箱域名限制 | **先为空 = 不限制** | 做成管理员可配置项（`app_settings.allowed_email_domains`，空 = 不限制）；后台可随时收紧 |
| 注册限制 | **不做限制** | 内部使用；保留管理员“关闭注册 / 开启邀请码”开关作为应急手段 |
| 云服务器 | **2C4G 轻量应用服务器（默认）** | 阿里云/腾讯云均可；地域选离团队近的；后续可升配 |
| 域名 | **已有** | 开发/测试先用占位域名 `chat.example.com`；真实域名确定后替换 Caddy 与客户端默认地址 |
| 邮箱发件 | 待选（SMTP 或免费邮件 API） | 开发期 mock，不阻塞编码；上线前接真实通道 |

> 无阻塞项：云服务器/域名/邮件通道准备好后即可按 M0→M1 推进；先用本机 + 两个浏览器跑通闭环。
> iPhone 本期不实现（仅计划），后续要上线 iOS 时再单独评估（首选 PWA）。

---

## 13. 第一步（可以在本仓库立即开始，不需要云服务器）

**M0 + M1 开工清单**

1. 建立目录骨架（文件级清单见 `docs/project-overview.md` §7）：`apps/web`、`apps/desktop`、`apps/mobile`、`server/`、`protocol/`、`infra/`、`docs/`。
2. 固化 `protocol/protocol.md`（本文 §4 的帧定义）+ `protocol/schema.sql`（§4.4 的 DDL）。
3. 邮件服务：先写 `IEmailSender` 抽象 + `MockEmailSender`（验证码输出到控制台，不写入日志文件），保证不接真实邮件也能开发；上线前再接 SMTP/免费 API（`infra/email` 配置与测试脚本）。
4. 服务端最小闭环：邮箱验证码注册/登录 + JWT + WebSocket + `message.send/ack` + `sync` + SQLite 迁移 + xUnit 测试。
5. Web 最小闭环：登录/注册页 + 会话列表 + 聊天页 + 发送队列 + 补拉。
6. 本地验证：同一台电脑起服务，两个浏览器（或隐身窗口）用两个邮箱账号互发 100 条；停止服务 1 分钟后恢复，校验消息完整、无重复、顺序正确。
7. 并行准备：云服务器（2C4G 轻量，选定地域）+ 占位域名（先用 `chat.example.com`，真实域名后替换）+ 邮箱发件通道；M4 再接入部署与 HTTPS。

**当前仓库状态（2026-10-25，M4 已完成）**：M0 交付保持（工程基础文件、协议/DDL、服务端与三端壳工程均可构建）。M1 服务端已完成：邮件抽象（Development Mock，SMTP 待接）与验证码发送（HMAC 哈希、10 分钟、内存限流）、验证码注册/登录与密码登录（PBKDF2-SHA256 ≥10 万次）、JWT HS256 + `sessions` 撤销、统一错误体与异常中间件、EF Core + SQLite（WAL、`InitialCreate`、启动自动迁移）、WebSocket `/ws`（10 秒认证、25s 心跳、单活动连接踢旧）、`message.send/ack/new`（事务内 `seq` 严格 +1、`clientMsgId` 幂等）、`sync` 补拉、单聊会话与 `conv.updated`、历史分页，40 个 xUnit 用例通过。M2 Web 端已完成：登录/注册（三模式、冷却与错误码文案）、会话列表（实时摘要/未读/发起单聊）、聊天页（历史分页、乐观发送与重试、`message.new` 合并、自动滚底）、IndexedDB 缓存与断线补拉（`sync.request` + 缺口重补）、连接状态横幅与点击重连、用户搜索；前端核心逻辑 34 个 Vitest 用例、端到端脚本（100 条互发无丢无重、断线重连补拉 5 条）与 `npm run build`/`dotnet build` 均通过；`GET /api/users?search=` 已按实现校准（见 §7 注）。M3 已完成（服务端 + Web）：群聊创建/邀请/退出/移除（系统消息 + 实时推送）、图片/文件消息（files 引用、下载鉴权、孤儿清理）、已读/typing/presence；服务端 40 个 xUnit 用例覆盖 M1~M3 关键路径，Web 端完成群聊与成员管理面板、系统消息渲染、图片压缩与鉴权下载、已读回执/typing/在线状态，前端累计 91 个 Vitest 用例（M3 新增 57），M3 端到端验收脚本 77 项检查全部通过。M4 已完成（2026-10-25）：运行时服务器地址配置、Electron 桌面壳（托盘/关闭到托盘/窗口记忆/系统通知/开机自启/单实例）、NSIS 安装包 `EiseChat-Setup-1.0.0.exe`（免签名，121,290,299 B，SHA256 `2f949a2a…ce9722550`）、`GET /download` 下载页与安装包分发、`infra/`（发布/安装/Caddy/备份/恢复）与 `docs/deploy.md`。待完成：Android APK（M5）、实际公网部署与 SMTP 邮件通道（用户侧资源/后续实现）、稳定性与备份恢复演练（M6），M5/M6 未开始。旧计划、可行性分析与 research 技术底稿均已清理；代码按 `docs/project-overview.md` §7 的目录架构新建。

---

## 附录 A：接口与协议示例

**邮箱验证码注册/登录（REST）**

```json
// 1) 发送验证码
POST /api/auth/email/send
{ "email": "user@company.com", "purpose": "register" }

// 2) 注册（验证码 6 位，10 分钟有效）
POST /api/auth/register
{ "email": "user@company.com", "code": "482913", "displayName": "张三", "password": "optional" }
→ { "accessToken": "eyJhbGciOi...", "user": { "id": "u_...", "email": "user@company.com", "displayName": "张三" } }
```

**WebSocket 帧（JSON，一行一帧）**

```json
// C→S 首帧认证
{ "type": "auth", "token": "eyJhbGciOi...", "deviceId": "desktop-abc123" }

// S→C 认证成功
{ "type": "hello", "userId": "u_...", "heartbeatSec": 25, "serverTime": 1730000000000 }

// C→S 发消息
{ "type": "message.send", "reqId": "r1", "clientMsgId": "c-9f2a...", "conversationId": "cv_...", "msgType": "text", "content": "你好" }

// S→C 落库回执
{ "type": "message.ack", "reqId": "r1", "clientMsgId": "c-9f2a...", "messageId": "m_...", "conversationId": "cv_...", "seq": 42, "createdAt": 1730000000123 }

// C→S 重连补拉
{ "type": "sync.request", "cursors": [ { "conversationId": "cv_1", "lastSeq": 40 }, { "conversationId": "cv_2", "lastSeq": 7 } ] }

// S→C 补拉结果
{ "type": "sync.response", "hasMore": false, "batches": [ { "conversationId": "cv_1", "messages": [ { "messageId": "m_...", "seq": 41, "msgType": "text", "content": "在吗", "senderId": "u_a", "createdAt": 1730000000200 } ] } ] }
```

错误帧：`{ "type": "error", "code": "NOT_MEMBER", "message": "你不是该会话成员", "reqId": "r1" }`

---

## 附录 B：仓库目录（V2.3.5 目标结构，目录级）

```
chat-platform/                      # 当前仓库根 D:\Code\EiseChat
├── apps/
│   ├── web/                        # Vue 3 客户端核心（页面/状态/WS/同步）
│   ├── desktop/                    # Electron 壳 + electron-builder 配置
│   └── mobile/                     # Capacitor 壳 + Android 工程
├── server/
│   ├── Chat.Server/                # ASP.NET Core 单进程
│   ├── Chat.Server.Tests/          # xUnit
│   └── Chat.Protocol/              # 协议 DTO（生成 TS 类型）
├── protocol/                       # protocol.md + JSON Schema + schema.sql
├── infra/
│   ├── install.sh / install.ps1    # 服务器安装（Linux 主路径）
│   ├── caddy/Caddyfile             # HTTPS 反代
│   ├── backup.sh / backup.ps1      # 备份与恢复
│   └── keys/                       # Android keystore（.gitignore）
├── docs/                           # 本计划、项目说明、部署/使用说明；test/ 为测试脚本（gitignore）
└── README.md
```

> 文件级目录（逐文件职责、依赖方向、构建产物与创建顺序）见 `docs/project-overview.md` §7；本树只到目录级。

---

## 附录 C：与现有文档的关系

| 文档 | 状态 | 说明 |
|---|---|---|
| `README.md` | **项目介绍与入口** | 项目是什么、给谁用、规模与形态、技术栈概览、文档索引、里程碑、重要边界；**项目介绍唯一出处**，不写可执行约束 |
| `docs/internal-plan.md` | **现行主计划（V2.3.5）** | 内部使用、自研、邮箱验证码、云服务器、可安装、零授权费；移动端先 Android，iPhone 仅方案；白名单后台可配、注册不限制、域名先用占位 |
| `docs/project-overview.md` | **现行项目说明 + 目录架构** | 项目定位/范围/技术栈/总体架构/关键设计约定 + 文件级目录规划、模块职责与依赖方向；M0 建骨架依据（项目介绍以 `README.md` 为准，本文做展开） |
| `AGENTS.md` | 仓库根 | **仓库级硬约束（不含项目介绍）**：技术栈、许可与架构硬规则、协议可靠性要求、工作流、测试与提交规范、DoD |
| 旧计划 V1.1/V2.0、`docs/feasibility-analysis.md`、`research/*.md` | 已删除 | 按用户要求清理无用文档，只保留本计划、项目说明与约束文档；如需追溯可从备份恢复 |

---

## 附录 D：更新记录

| 版本 | 日期 | 变更 |
|---|---|---|
| —（M4 收尾状态同步） | 2026-10-25 | §13「当前仓库状态」更新为 M4 已完成：Windows 安装包 1.0.0（NSIS，免签名）、运行时服务器地址配置、Electron 桌面壳（托盘/关闭到托盘/窗口记忆/系统通知/开机自启/单实例）、`GET /download` 下载页与安装包分发、`infra/` 发布/安装/Caddy/备份/恢复脚本与 `docs/deploy.md`；M5（Android APK）与 M6（稳定性/恢复演练）未开始；需求、范围、部署参数与计划版本（V2.3.5）未变 |
| —（M3 收尾状态同步） | 2026-10-25 | §13「当前仓库状态」更新为 M3 已完成（服务端 + Web）：群聊/成员管理/系统消息、图片文件、已读、typing、presence 与 M3 Web UI 全部落地；服务端 40 个 xUnit 用例、前端累计 91 个 Vitest 用例（M3 新增 57）、M3 端到端验收脚本 77 项检查与 `dotnet build`/`npm run build` 通过；`README.md` 当前状态/快速开始/下一步同步；`protocol/` 无帧变更、库表无变更；需求、范围与计划版本（V2.3.5）未变 |
| V2.3.5 | 2026-10-25 | M3-05 已读/typing/presence 语义校准：read.update 只增不减与越界校验、新成员 last_read_seq=加入前 lastSeq、typing 服务端 1 秒节流、presence 仅 0↔1 连接推送、移出/退出只收 removedConversationIds；protocol/protocol.md 同步；不改帧格式与库表 |
| V2.3.4 | 2026-10-25 | M3-04 群聊服务端：成员管理语义、系统消息事件名、conv.updated 移除通知；新增 App:GroupMaxMembers / GroupOnlyOwnerCanInvite；不改帧格式与库表 |
| V2.3.3 | 2026-10-25 | M3 库表增量：messages 增可选 file_id + idx_messages_file_id（文件消息下载鉴权与孤儿清理用，不建库级外键）；protocol/protocol.md §7 与变更记录同步；需求、范围与帧格式未变 |
| —（M2 收尾状态同步） | 2026-10-25 | §13「当前仓库状态」更新为 M2 已完成：M1 服务端 + M2 Web 端全部落地，前端 34 个 Vitest 用例、端到端脚本（100 条互发、断线补拉）与 `npm run build`/`dotnet build` 通过；`README.md` 当前状态/下一步同步；`protocol/` 无帧变更，`GET /api/users?search=` 在 §7 按实现补注；需求、范围与计划版本（V2.3.2）未变 |
| —（M1 收尾状态同步） | 2026-10-25 | §13「当前仓库状态」更新为 M1 已完成：认证/WS/消息/补拉/单聊会话/EF 迁移与 xUnit、端到端验证全部落地；`protocol/protocol.md` 按实现补注（v1 文本消息上限 4096 字符、会话列表排序口径、M1 已实现帧范围）并在变更记录追加 v1（M1-11 校准）行；`README.md` 当前状态/下一步同步；需求、范围与计划版本（V2.3.2）未变 |
| —（M0 收尾状态同步） | 2026-10-25 | §13「当前仓库状态」更新为 M0 已完成：协议/DDL、服务端、Web/桌面/移动端骨架全部落地并完成构建/冒烟验证，M1 业务与 `infra/`、部署说明待做；`AGENTS.md` §8 常用命令按实际工程补全（版本 1.5→1.6，规则内容不变）；`README.md` 当前状态/快速开始同步；需求、范围与计划版本（V2.3.2）未变 |
| —（状态同步） | 2026-10-25 | §13「当前仓库状态」按实际进度更新：工程基础文件、协议/DDL、服务端骨架与健康检查已完成，Web/桌面/移动端骨架与 M1 待接入；需求、范围与计划版本（V2.3.2）未变 |
| V2.3.2 | 2026-10-25 | 按 m00148 + m00204 补充工程规范与文档分工：测试文件统一放 `docs/test/`（不再建根级 `tests/` 目录）、默认不跑测试与分批最小范围、提交信息格式、交付说明五要素、禁止 `git push`/`git reset --hard`/`git clean -fd`；明确 `README.md` 为项目介绍唯一出处、`AGENTS.md` 仅放约束；同步 `AGENTS.md` 1.4（2026-10-25 再作去重修订至 1.5：删除 `docs/test/`、git 安全、密钥与禁用依赖清单的重复条目，统一归口 §2/§11.1/§7.6，规则内容不变）与 `docs/project-overview.md` V1.2；工程规范（测试位置/默认不跑测试/测试按缺口/提交信息格式/交付说明）列为硬规则 `AGENTS.md` §2.10 |
| V2.3.1 | 2026-10-25 | 文档一致性修订：成本口径统一为约 60~150 元/月（§10 更正）；删除「可选 Docker」表述（与 AGENTS.md 硬规则 §2.4 冲突）；管理员初始密码与开发期验证码改为「只输出到控制台、不写入日志文件」；新增 `docs/project-overview.md`（项目说明 + 文件级目录架构）；补齐 `.gitignore`；`Realtime/Protocol.cs` 改为 `FrameCodec.cs`（DTO 唯一来源在 `Chat.Protocol`） |
| V2.3 | 2026-10-25 | 按 m00356 确认部署参数：邮箱域名白名单留空 = 不限制（管理员可配）、注册不限制、云服务器 2C4G 轻量默认、域名已有先用占位域名；删除 V1.1/V2.0 旧计划 |
| V2.2 | 2026-10-25 | 按 m00289 收敛：iPhone 移出本期实现范围（仅保留 PWA/TestFlight 方案），移动端先只做 Android APK；里程碑、范围、风险与附录同步更新 |
| V2.1 | 2026-10-25 | 按 m00264 细化：邮箱验证码注册/登录（替代手机号/管理员建号）、云服务器 + 域名/HTTPS 部署、iPhone 定案 PWA（后被 V2.2 取代）、成本改为按月（约 90~215 元/月）、风险补充邮件送达与公网安全 |
| V2.0 | 2026-10-25 | 根据 m00240 改为内部使用版：自研轻量 IM 替代 OpenIM；三端客户端与安装程序；一键安装；零授权费；6 周 MVP；去掉合规、短信、推送、多组件依赖 |
| V1.1 | 2026-10-25 | 公开发布/合规路线（已被取代） |
