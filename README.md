# EiseChat（内部版）

内部使用的轻量聊天工具：**自研 IM 底层**（不用 OpenIM）、**有安装程序**（Windows 安装包 / Android APK；**iPhone 仅出方案、本期不实现**）、**邮箱验证码登录**（不用手机号）、**零软件授权费**。

## 项目介绍

> 本节是本仓库**唯一的项目介绍**；可执行约束见 [AGENTS.md](AGENTS.md)，计划与详细说明见 `docs/`。

- **是什么**：自研轻量 IM（服务端 + 客户端）；服务端为 ASP.NET Core 单进程（REST + 自研 JSON-over-WebSocket + 静态前端），数据用 SQLite（WAL）+ 服务器本地文件目录，Docker Compose 一键部署（HTTPS 走 Tailscale Serve 或宿主 Caddy）。
- **给谁用**：公司/团队内部，10~100 个账号、峰值 ≤100 在线；部署在云服务器 + 域名/HTTPS；不做公网运营，不需要 ICP/等保/实名/内容审核。
- **交付物**：Windows 安装包 `EiseChat-Setup-x.y.z.exe`、Android APK `EiseChat-x.y.z.apk`（侧载安装）、云服务器上的服务端（含 Web 端与 `GET /download` 下载页）；iPhone 仅出方案、本期不实现。
- **技术骨架**：一套 Vue 3 前端核心复用到 Web / Windows（Electron）/ Android（Capacitor）；服务端是唯一事实源，客户端 IndexedDB 只是缓存。
- **当前状态**：✅ **M1 / M2 / M3 / M4 / M5 已完成（2026-10-26）** —— M0 交付保持：工程基础配置（`.editorconfig`、`.gitignore`）、协议 [protocol/protocol.md](protocol/protocol.md)（22 类帧、错误码、兼容规则）、表结构 [protocol/schema.sql](protocol/schema.sql)（12 张表 DDL）、服务端与三端壳工程可构建。M1 服务端已完成：邮箱验证码注册/验证码登录/密码登录/注销（JWT HS256 + `sessions` 撤销）、WebSocket `/ws`（10 秒认证、心跳、单活动连接踢旧）、`message.send/ack/new`（事务内 `seq` 严格 +1、`clientMsgId` 幂等、广播除发送连接）、`sync.request/response` 补拉（每会话每批 ≤200）、单聊会话创建/列表与 `conv.updated` 推送、EF Core + SQLite（WAL、初始迁移、启动自动迁移），40 个 xUnit 用例通过。M2 Web 端已完成：登录/注册三模式、会话列表与发起单聊、聊天页（历史分页、乐观发送与 2s/5s/10s 重试、`message.new` 合并、自动滚底）、未读角标、断线补拉 + IndexedDB 缓存 + 连接横幅、用户搜索；前端核心逻辑 34 个 Vitest 用例与端到端脚本（100 条互发无丢无重、断线重连补拉）验证通过。M3 全部完成（服务端 + Web）：群聊创建/邀请/退出/移除与成员管理面板、系统消息、图片压缩与文件上传下载（鉴权下载、缩略图/预览）、已读回执（滚到底上报与已读/未读标记）、typing 输入提示与 3 秒节流、通讯录/单聊头部/群成员面板在线状态；服务端 xUnit 40 个用例覆盖 M1~M3 关键路径，前端 M3 新增 57 个 Vitest 用例（附件 15 + 群聊 18 + 已读/typing/presence 24，累计 91 个），M3 端到端验收脚本 77 项检查全部通过。M4 已完成（2026-10-25）：客户端支持运行时配置服务端地址；Electron 桌面壳支持托盘/关闭到托盘/窗口记忆/新消息系统通知/开机自启/单实例；NSIS 安装包 `EiseChat-Setup-1.0.0.exe` 已产出（121,290,299 B，SHA256 `2f949a2a…ce9722550`，免签名，SmartScreen 需「更多信息 → 仍要运行」），服务端提供 `GET /download` 下载页与 `/download/files/` 安装包分发；`infra/` 提供 Linux 发布/安装/Caddy/备份/恢复脚本与 [docs/deploy.md](docs/deploy.md)。M5 已完成（2026-10-26）：Android APK `EiseChat-1.0.0.apk` 已产出（3,276,544 B，SHA256 `dee0898…984d14`，包名 `com.eisechat.mobile`、`versionCode=10000`、minSdk 26 / targetSdk 36、v2 自签名静态校验通过），`GET /download` 下载页可下载；Android 图标/启动页、release 签名配置与 `infra/build-apk.sh`、[docs/mobile.md](docs/mobile.md) 安装联调说明已就绪（本机无真机，安装/登录需用户真机验收）。未完成：M6 稳定性与备份恢复演练；实际公网部署与 SMTP 邮件通道需用户云服务器侧资源/后续实现（当前 Production 发码为占位 500）。部署参数已确认（2C4G 轻量 / 域名已有先用占位 / 注册不限制 / 邮件通道待接）。UI 改造已完成（UI-00~UI-05，2026-10-26）：UI-00 现状审查与改版前基线截图；UI-01 蓝白设计系统（`--ec-*` token、内联 SVG 图标、左侧导航外壳）、基础组件与对比度修正；UI-02 聊天主界面重构（会话列表与筛选、聊天顶栏、蓝白气泡与日期分隔、新消息浮标、输入区图标化与 Emoji 面板、断线提示、移动端布局）；UI-03 自绘弹窗/轻提示与统一错误文案、联系人双栏与建群弹窗、设置中心与登录页视觉、空态/图片预览/下载与管理页；UI-04 四档响应式（≥1440 / 801~1199 / ≤800）、宽屏面板推挤、移动端底部导航与单列布局、Android 软键盘/安全区/返回、hover 与触控区分、动效与图片懒加载；UI-05 代码级检查、单测缺补与人工视觉/真机验收。前端构建通过、Vitest 178 例通过（含设置持久化、提示音/通知判定 20 例）。UI-06 单独打包回归不执行：1.1.0 安装包已在 F-27 统一发布中重打（Windows exe 121,359,488 B、Android APK 3,351,022 B）。UI 改造的计划、审查报告、任务清单与截图均为本地过程文件、不入库，执行完成后已清理。另：设置中心已实现本机设置持久化（提示音 / 桌面通知开关存 localStorage）与新消息提醒（提示音用 WebAudio 合成、窗口不可见时按开关弹桌面壳或浏览器系统通知，2026-10-27）。好友关系已完成（F-17，2026-10-27）：好友申请发送/幂等/反向自动接受、接受/拒绝/撤销、双向好友、备注与分组、联系人页「好友 / 申请 / 搜索」三块切换（含待处理徽标与附言弹窗）、好友与共同会话成员一样互相可见在线状态；新增 `friend.changed` 推送帧（协议 18 类帧、库表 12 张）与 7 个好友 REST 端点；服务端 xUnit 58 例通过，好友链路端到端脚本 29 项检查全部通过。长会话性能已完成（F-05，2026-10-27）：消息列表分片渲染（默认窗口 200 条、上限 400 行），向上滚动先在内存中扩窗、到达内存起点后自动拉取更早历史，并用滚动锚点补偿避免视口跳变，回到最新消息时窗口自动收缩；贴底、新消息浮标与已读上报语义保持不变，新增窗口/锚点纯函数单测 40 例（22 + 18）通过。拖拽与粘贴上传已完成（F-06，2026-10-27）：拖文件进聊天主区或 Win+Shift+S 截图后 Ctrl+V 即走现有「校验 → 图片压缩 → 上传 → 发送」链路，单次最多 10 个、超出截断提示；拖拽悬停显示投放提示，落点在侧栏或页面空白处不会打开文件，移动端不显示投放提示；新增拖拽/剪贴板提取纯函数与 19 例单测。文本消息链接识别已完成（F-07，2026-10-27）：仅 http/https 渲染为可点击链接，javascript:/data:/file:/ftp: 等一律按普通文本显示；Web 新标签打开、桌面壳经 setWindowOpenHandler 用系统浏览器打开；尾部中英文句读自动剥离、长链接换行不撑破气泡、单条文本最多 20 个链接；新增链接识别纯函数与 16 例单测。图片预览增强已完成（F-08，2026-10-27）：会话内图片可左右切换（按钮 / 键盘 ←→ / 移动端滑动），支持滚轮、按钮与双击缩放（1x~4x，不溢出屏幕）、Esc 或点击遮罩关闭、下载当前图；单图行为保持不变，Android 返回仍先关预览；多图列表与缩放纯函数 14 例单测通过，预览状态提升到会话页并复用鉴权 blob 缓存（按 fileId 去重、上限 100）。会话置顶与免打扰已完成（F-10，2026-10-27）：会话可置顶（置顶会话排在列表最前，组内仍按最近消息时间倒序）与消息免打扰（免打扰会话不播放提示音、不弹桌面通知，未读数照常累计）；两项设置存在 `conversation_members.pinned/muted`，新增 `PATCH /api/conversations/{conversationId}/member-settings` 仅更新本人设置且不广播，他人视角不受影响；会话行「⋯」/右键菜单可切换，列表显示置顶与静音图标；服务端新增 8 例单测，前端排序/通知判定 24 例单测通过。消息搜索已完成（F-11，2026-10-27）：左导航/聊天顶栏/移动端列表提供全局与会话内搜索，服务端 `GET /api/messages/search` 仅检索本人所在会话的文本消息（`q` 1~50、`limit` 1~50、`beforeSeq` 翻页，按 `seq` 降序），用 `json_extract` + 内存复核规避 JSON 转义导致的中文漏搜；面板支持命中高亮、加载更多、键盘上下选择与 Esc 关闭，点击结果跳转到对应会话并定位高亮（复用 F-09 机制，`?jumpSeq=` 定位后自动清理）；服务端搜索 10 例、高亮/摘要 15 例单测通过。个人资料已完成（F-12，2026-10-27）：设置页「个人资料」卡片可上传头像（客户端压缩到最长边 512、约 200KB）并修改昵称（1~32 字符），保存后侧栏、设置页、会话列表、群成员面板与消息头像/昵称立即更新；头像经鉴权接口 + blob 缓存加载，加载失败回退首字母，换头像后旧文件由孤儿清理按 24h 回收；服务端 xUnit（头像访问/引用保护 4 例 + 资料接口 10 例）通过。账号安全已完成（F-13，2026-10-28）：设置页「账号与安全」支持修改/设置登录密码（校验旧密码、新密码 8~128 位、PBKDF2-SHA256 存库）与登录设备管理（设备简称、登录时间、最近活跃、当前设备徽标，可退出其他设备并立即断开其连接）；登录页「忘记密码？」通过邮箱验证码重置密码（一次性、未注册邮箱不泄漏、不撤销已有会话）；`email_codes.purpose` 扩展 `reset`，新增 `POST /api/me/password`、`POST /api/auth/password/reset(/request)`、`GET/DELETE /api/sessions`；服务端 xUnit 新增 22 例（改密 9 + 重置 8 + 会话 5）通过，账号安全端到端脚本 27 项检查全部通过。群资料已完成（F-14，2026-10-28）：群成员面板顶部新增「群资料」区，展示群名与群公告，群主可通过弹窗修改群名（1~100 字符）与公告（≤500 字符、支持换行、可清空），其他成员只读并提示「仅群主可修改群资料」；公告默认 2 行预览可展开，空公告显示「暂无公告」；`conversations` 新增可空 `announcement`，新增 `PATCH /api/conversations/{conversationId}`（仅群主）并按收件人视角逐连接下发 `conv.updated`（不覆盖他人置顶/免打扰/已读）；服务端 xUnit 17 例、前端 groupProfile 17 例单测通过，群资料端到端脚本 23 项检查全部通过。群头像已完成（F-23，2026-10-28）：群成员面板「群资料」区支持群主上传/更换/移除群头像（客户端压缩到最长边 512、约 200KB），会话列表、聊天顶栏与群资料卡片三处展示，成员头像与单聊头像不受影响；`conversations` 新增可空 `avatar_file_id`，`PATCH /api/conversations/{conversationId}` 增加 `avatarFileId`（空串清空、仅群主、只能使用本人上传的图片），群头像文件对登录用户可下载、匿名 401，孤儿清理纳入群头像引用；服务端新增 13 例单测、前端 groupAvatar 9 例单测通过，群头像端到端脚本 22 项检查全部通过。会话共享文件已完成（F-15，2026-10-28）：聊天顶栏新增「文件」入口，右侧 aside 升级为页签面板——群聊可在「群资料与成员 / 文件」间切换、单聊仅「文件」；图片 3 列缩略图可点开预览、文件行可鉴权下载，支持加载更多、空态与错误重试；服务端 `GET /api/conversations/{conversationId}/files` 按 `type=image|file` 汇总会话附件（仅会话成员可读、按文件去重取最新引用 seq、`messageSeq` 降序、`beforeSeq` 排他分页、limit 1~100），未新增库表/列/帧；服务端新增 13 个测试（17 例）、前端纯函数 12 例单测通过，共享文件端到端脚本 20 项检查全部通过。自定义表情已完成（F-21，2026-10-28）：表情面板新增「表情/自定义」页签，可上传本设备表情（PNG/JPEG/WebP ≤2MB、GIF ≤1MB），非 GIF 压缩为 128px PNG 并保留透明、GIF 保留动画；自定义表情存入本设备 IndexedDB（换浏览器/清缓存为空，已发送消息不受删除影响），点击以普通 image 消息发送；内置表情增加「最近使用」行（localStorage，最多 24 个、展示 12 个）；新增自定义表情校验/排序纯函数与最近使用纯函数，前端 25 例单测通过。消息撤回与编辑已完成（F-18，2026-10-28）：发送者可撤回 2 分钟内的文本/图片/文件消息（撤回后双方显示「已撤回」，内容清空且附件不再暴露，文件由孤儿清理按保留期回收），可编辑 15 分钟内的文本消息（trim 后 1~4096 字符，可多次编辑并显示「已编辑」）；新增 `message.recall/recalled/edit/edited` 4 帧（协议共 22 类）、`messages.edited_at` 可空列（纯 AddColumn），窗口时长可由 `AppOptions` 配置；历史与补拉返回最新状态，客户端打开会话时按 messageId 校正最近 50 条；右键/长按消息打开操作菜单（回复/撤回/编辑按权限过滤），撤回与编辑均有二次确认；服务端 21 例 xUnit、前端 messageActions 14 例单测通过，撤回/编辑端到端脚本 17 项检查全部通过。已知限制：离线期间最近 50 条以外的历史修改需重新打开会话或加载历史后才更新；撤回的文件不物理删除但不再暴露 fileId；仅文本消息可编辑。会话内搜索已完成（F-19，2026-10-28）：在现有搜索面板会话内模式下融合本地即时结果——输入关键词时先同步过滤当前会话已加载消息（文本与文件名、不区分大小写、按 seq 降序、最多 100 条，0 延迟展示），同时并行请求服务端搜索以补全更早历史；服务端结果返回后切换为权威列表，服务端不可用（离线/失败）时保留本地结果并提示「仅显示本地已加载消息」；新增本地搜索纯函数 16 例单测，全局搜索与既有跳转定位行为不变。群主转让已完成（F-24，2026-10-28）：群主可在群成员面板把群主转让给本群任一成员，转让后双方角色立即互换，服务端写入 `owner.transferred` 系统消息并按收件人视角逐连接下发 `conv.updated`；原群主随即失去群资料与成员管理权限、新群主获得；非群主/非成员、目标非成员或自己、单聊均被拒绝并返回中文提示；新增 `POST /api/conversations/{conversationId}/owner`（无新库表/列/帧）；服务端 xUnit 13 例、前端 groupChat 7 例单测通过，群主转让端到端脚本 24 项检查全部通过。@提及已完成（F-25，2026-10-28）：群聊输入 @ 弹出成员候选（前缀优先过滤、↑↓/Enter/Tab 与触屏选择、Esc 关闭），选中插入「@昵称 」并保持光标位置；消息气泡内 @昵称 高亮，自己被提到时加重高亮并标「提到你」；纯前端文本约定（content.text 中的「@昵称」），服务端不解析、不新增帧/库表；新增 `core/mentions.ts` 纯函数 29 例单测通过。已知限制：历史消息按当前成员昵称匹配，成员改名后旧消息可能不再高亮；同名昵称无法区分；单聊不触发。文本转发与本地收藏已完成（F-26，2026-10-28）：消息操作菜单支持把文本消息转发到其他会话（以新消息发送，不引用、不 chain 原消息；图片/文件本期不提供转发入口）；文本/图片/文件消息可收藏为本设备 IndexedDB 快照，按账号隔离、上限 500 条、不做云端同步，原消息之后的撤回/编辑不改变收藏副本；新增 `/favorites` 收藏页（打开原会话定位、删除、空态）并在左导航与移动端底部导航加入「收藏」入口；新增 `core/favorites.ts`、`core/forward.ts`、`stores/favorites.ts` 与 38 例单测；服务端零改动（无新帧/库表）。1.1.0 统一发布已完成（F-27，2026-10-28）：三端版本号统一为 1.1.0（web/desktop/mobile package.json、`core/version.ts`、Android `versionCode=10100 / versionName=1.1.0`、服务端程序集 `<Version>1.1.0</Version>`）；Windows 安装包 `EiseChat-Setup-1.1.0.exe`（121,359,488 B，SHA256 `82fbd17c3861830d4e3686928f1c410530bd3a1fad7d938ff3a43e1e0813deec`，FileVersion/ProductVersion=1.1.0，免签名）与 Android APK `EiseChat-1.1.0.apk`（3,351,022 B，SHA256 `703656cecd29c43c483546c706f5f3d277a20592858a36db33356c7e18180d5b`，versionCode=10100、v2 签名，证书 SHA-256 与 1.0.0 一致）已产出并复制到 `data/downloads/`；服务端 6580 已重启为 1.1.0，`GET /download` 两按钮指向 1.1.0 且旧 1.0.0 包仍可下载（回滚保留）；Android 真机 `adb install -r` 覆盖安装成功（firstInstall 10-26 → lastUpdate 10-28，应用数据目录保留，启动进入登录页，登录态与收发消息需人工复验）；Windows 覆盖安装与卸载保留 `%APPDATA%` 由用户后续手动验收（本次记录为未执行）。功能与协议零改动（protocolVersion 仍 1）。
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

| 阶段 | 时间 | 目标 |
|---|---|---|
| M0 | 3~4 天 | 云服务器选型/占位域名、协议与表结构定稿、仓库骨架 |
| M1 | 第 1~1.5 周 | 服务端：邮箱验证码注册/登录、JWT、WS、seq、ack、sync、SQLite，两账号互发无丢无重 |
| M2 | 第 2 周 | Web 客户端：邮箱登录/注册、会话、单聊、发送状态、断线补拉 |
| M3 | 第 3 周 | 群聊、图片/文件、未读/已读、typing |
| M4 | 第 4 周 | Windows 安装包 + 云服务器部署（Docker Compose 一键部署/HTTPS/备份）+ 下载页 |
| M5 | 第 5 周 | Android APK 打包与真机测试 |
| M6 | 第 6~7 周 | 100 连接稳定性、备份恢复演练、10~20 人内测（Windows + Android） |

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
npm run build   # 产物 apps/web/dist（不入库；生产部署见 docs/web.md）

# Windows 壳（开发态需先启动上面的 Web dev server；安装包 M4 已产出，安装/打包见 docs/desktop.md）
cd apps/desktop && npm install && npm run dev   # Electron 加载 http://127.0.0.1:5173
npm run dist                                    # NSIS 安装包（apps/desktop/dist/EiseChat-Setup-1.1.0.exe，不入库）

# Android 壳（Web 资源打进 APK；APK 1.1.0 已产出，打包环境与安装见 docs/mobile.md）
cd apps/mobile && npm install && npm run sync
bash infra/build-apk.sh 1.1.0   # 产出 data/downloads/EiseChat-1.1.0.apk（不入库；需 JDK 21 + Android SDK）

# M3 端到端验收（一次性脚本，docs/test/ 不入库；需服务端以 Development 启动并监听 6580）
EISECHAT_PYTHON=python node docs/test/m3-e2e.mjs

# 下载页 / 安装包分发（服务端启动后；安装包放 data/downloads/，浏览器打开 http://127.0.0.1:6580/download）

# 服务器部署（Docker Compose 一键部署，详见 docs/deploy.md §3 / docs/update-deploy.md）
bash infra/package-src.sh                       # 开发机：打固定名源码包 dist/eisechat-src.zip（覆盖）
# 上传：scp dist/eisechat-src.zip chat:~/eisechat-src.zip（目标名固定），服务器解压替换 /opt/eisechat/src
sudo bash infra/docker/deploy.sh                # 服务器上执行（构建镜像 + 启动 + 健康检查）
sudo bash infra/backup.sh                       # 服务器备份（每日 cron 见部署文档）
# 备用（无 Docker 环境）：publish-linux.sh + install.sh，见 docs/deploy.md 附录
```

> 完整命令清单见 [AGENTS.md](AGENTS.md) §8；M1 服务端与 M2/M3 Web 端已完成（认证/WS/消息/补拉/登录注册/会话列表/聊天页/群聊/图片文件/已读/typing/在线状态），M4 已交付 Windows 安装包、`GET /download` 下载页与 `infra/` 部署脚本/`docs/deploy.md`；M5 已产出 Android APK、F-27 统一打包 1.1.0 已发布（`GET /download` 可下载，安装说明见 [docs/mobile.md](docs/mobile.md)）；本机两个浏览器仍可直接完成登录与互发验证（端到端验收脚本见上方 `m3-e2e.mjs`，一次性，不入库）。

## 重要边界（内部版）

- 服务器可见消息内容（不做端到端加密）；敏感信息请勿用本工具传递。
- v1 无系统级离线推送：新消息在**打开应用时**自动补拉（Android 后台可能收不到即时提醒）。
- **iPhone 本期不实现（仅计划）**：后续启用首选 PWA（添加到主屏幕），不买 Apple 开发者账号。
- 邮件验证码依赖发件通道：需配置 SMTP/免费邮件 API，并保留密码登录兜底。
- 公网入口需要基础加固：安全组最小化、HTTPS、限流；注册限制可按需开启（邮箱域名白名单/邀请码）、及时更新补丁。
- **备份必须做**：每日自动备份 + 至少一份异机/对象存储备份，上线前完成一次恢复演练。

## 下一步

1. 无需再确认：按主计划 §12 已定参数开工（邮箱白名单留空 = 不限制、云服务器 2C4G 轻量、占位域名 `chat.example.com`、真实域名后替换）。
2. M5 已收口（Android APK、下载页可下载、`infra/build-apk.sh` 与 [docs/mobile.md](docs/mobile.md)）；UI 改造已完成（UI-00~UI-05；UI-06 不单独执行，打包并入下一次统一发布）。1.1.0 统一发布已完成（F-27，2026-10-28）：Windows exe 与 Android APK 同步升到 1.1.0、`/download` 已更新、功能与协议零改动；下一步：补齐占位/后续功能（消息搜索 F-11 与会话内搜索 F-19、会话置顶/免打扰 F-10、个人资料 F-12、账号安全（改密/找回密码/登录设备）F-13、群资料（群名/群公告）F-14、群头像 F-23、会话共享文件 F-15、自定义表情 F-21、消息撤回/编辑 F-18 已完成），再做 M6 稳定性/备份恢复演练/内测，并用真机验收 APK 安装与登录（本机无真机）。部署已改为 Docker Compose 一键部署（AGENTS 2.0 / docs/deploy.md）：上传源码 → `sudo bash infra/docker/deploy.sh` → Tailscale Serve 提供 HTTPS；SMTP 与备份按部署文档在服务器上配置。
