# EiseChat UI V1.0 — UI-00 只读工程审查与改造范围清单

> **任务编号：** UI-00（只读工程审查）
> **执行日期：** 2026-10-26
> **审查基线：** `341da70`（UI 专项计划 V1.2；主计划 V2.3.5）
> **执行方式：** 只读源码 + 运行态观察 + 浏览器截图（`docs/test/ui00_capture.mjs`，一次性脚本不入库）
> **交付性质：** 只交付文档与基线截图，**未改任何产品代码**（`apps/`、`server/`、`protocol/` 均未改动）
> **输入依据：** `docs/ui-upgrade-plan.md` §12/§13/§14/§16、`AGENTS.md`、`docs/internal-plan.md`
> **关联资产：** `docs/EiseChat_UI_交互验收包_V1.1/`（参考包，只参考不复制）、`docs/ui/screens-before/`（改造前基线截图）

---

## 1. 执行摘要与结论

### 1.1 结论

- **UI-00 审查通过，可以进入 UI-01 基础层改造。** 现有工程结构（Vue 3 + Pinia + 原生 CSS）没有阻碍 UI 重做的技术债，页面数量（6 个）与组件数量（11 个）都可控。
- **改造范围集中在 `apps/web/src`**，不需要改服务端、协议、数据库、Electron 主进程与 Capacitor 配置；**不需要新增任何依赖**（图标用内联 SVG、表情用 Unicode、动画用原生 CSS）。
- **必须先解决 3 个高优先级风险**（见 §9）：①品牌色体系是绿色，目标设计是蓝白，需全量替换 token；②当前**完全没有响应式**，390×844 下聊天页横向溢出、无法使用；③导航 IA 从「两栏」变为「左侧导航 + 会话列表 + 聊天」，涉及 `Chat.vue` 结构拆分与布局重构。
- 排期维持计划 §6 的 `UI-01 ~ UI-06` 划分与估算（UI-00+01 约 1.5~3 人日），**不需要整体重估**；范围上有 3 项建议压缩（见 §13.3）。

### 1.2 现场关键数字

| 项 | 数值 | 说明 |
|---|---|---|
| 生产代码 | 约 7200 行 | `apps/web/src` 8035 行，含 9 个测试文件（829 行） |
| 页面 | 6 个 | Login / Chat / Contacts / Settings / Admin / Download |
| 组件 | 11 个 | 见 §5.3 |
| Store | 7 个 | auth / contacts（未被页面使用）/ conversations / messages / presence / settings / typing |
| 运行时依赖 | 3 个 | vue、pinia、vue-router（全部 MIT） |
| 开发依赖 | 5 个 | vite、@vitejs/plugin-vue、typescript、vue-tsc、vitest（全部 MIT/Apache-2.0） |
| 构建产物 | 232 KB | `dist/` 总计；index JS 69.9 KB、Chat chunk 28.5 KB、Chat CSS 13.5 KB |
| CSS 媒体查询 | **0 处** | `apps/web/src` 全目录无 `@media` |
| 设计 token | 16 个颜色变量 | 见 `styles/variables.css`，品牌色为绿 `#2f7d5d` |
| 第三方 UI/图标库 | 无 | 无 toast 库、无图标库、无 CSS 框架 |
| Vitest 用例 | 108 个（计划 §14 记载） | UI-01 开工前以一次实际基线运行为准 |

### 1.3 本次交付物

1. 本报告 `docs/ui/ui-00-review.md`（§2 文件清点、§3 页面映射、§4 链路边界、§5 样式与组件、§6 契约核对、§7 运行约束、§8 基线截图、§9 风险、§10 范围三档、§11 阶段对齐、§12 参考包清点与差距、§13 下一步）。
2. 基线截图 8 张 → `docs/ui/screens-before/`（桌面 6 张 + 移动 2 张，见 §8）。
3. 运行记录 → `docs/test/ui-00-tasks.md`（不入库，按 AGENTS §11.1）。
4. 索引同步：`README.md` 文档表、`docs/project-overview.md` §1、`docs/ui/README.md`、主计划附录 D。

---

## 2. 文件与模块清点（UI-00-01）

### 2.1 源码清单

| 文件 | 行数 | 职责 | 与 UI 改造的关系 |
|---|---:|---|---|
| `src/main.ts` | 53 | 挂载、Pinia/Router、会话清理、401 与致命关闭处理 | 基本不动（可能补全局提示挂载） |
| `src/App.vue` | 35 | `ConnectionBanner` + `RouterView` | 不动（UI-04 可能加全局壳） |
| `src/router/index.ts` | 46 | 路由表、file:// 时 hash 模式、登录守卫 | 路由路径不变；UI-04 可能加布局路由 |
| `src/styles/variables.css` | 51 | 全局 CSS 变量（颜色/圆角/间距/字体/阴影/头高） | **A 档必改**（品牌绿→蓝白，见 §5.1） |
| `src/styles/global.css` | 51 | reset、body 基础、链接 | 保留并小幅调整（字体、背景） |
| `src/pages/Login.vue` | 547 | 注册/验证码登录/密码登录 + 服务器地址 | 样式重写 + 结构重排（三模式布局） |
| `src/pages/Chat.vue` | 495 | 两栏聊天：会话列表 + 聊天区 + 群成员面板 | **A 档重构**（拆出导航/列表/头部） |
| `src/pages/Contacts.vue` | 452 | 联系人搜索 + 单聊/建群 | 样式重写 + 抽列表/资料组件 |
| `src/pages/Settings.vue` | 203 | 账号、服务器地址、退出、提示音/桌面通知 | 样式重写 + 分组组件 |
| `src/pages/Admin.vue` | 27 | 管理页占位（仅用户列表骨架） | **B 档**（仅样式跟随） |
| `src/pages/Download.vue` | 32 | 下载页占位（桌面/安卓安装包入口） | **B 档**（仅样式跟随） |
| `src/components/*.vue` | 11 个共 1658 行 | 见 §5.3 | 部分改造 + 部分新增 |
| `src/stores/*.ts` | 7 个共 1466 行 | 状态与链路 | **禁止改语义**（见 §4.2） |
| `src/core/*.ts` | 12 个约 1320 行 + 9 个测试 829 行 | ws/sync/http/db/文件/已读/typing/presence 等基础设施 | **禁止改语义**；允许新增纯 UI 工具 |
| `src/api/*.ts` | 6 个共 216 行 | REST 封装 | **禁止改契约** |
| `src/types/frames.d.ts` | 292 | WS 帧 + REST DTO 类型 | **禁止改字段语义** |
| `src/index.html` / `vite.config.ts` | 12 / 28 | 入口、`base: './'`、构建目标、代理 | 基本不动（须保 `./` 相对路径，见 §7） |

### 2.2 依赖清点

| 类型 | 包 | 版本 | 许可 | UI 改造是否需要新增 |
|---|---|---|---|---|
| 运行时 | vue | ^3.5.44 | MIT | 否 |
| 运行时 | pinia | ^4.1.0 | MIT | 否 |
| 运行时 | vue-router | ^5.4.0 | MIT | 否 |
| 开发 | vite | ^8.3.5 | MIT | 否 |
| 开发 | @vitejs/plugin-vue | ^6.0.10 | MIT | 否 |
| 开发 | typescript | ~5.9.3 | Apache-2.0 | 否 |
| 开发 | vue-tsc | ^3.3.13 | MIT | 否 |
| 开发 | vitest | ^5.0.5 | MIT | 否 |

**结论：** UI V1.0 全部用原生能力实现（CSS 变量、内联 SVG、Unicode Emoji、CSS 动画），不引入 UI 组件库/图标库/动画库/表情库，符合 AGENTS §2.4/§3 与计划 §16.2 第 4 条。

### 2.3 构建产物基线（改造前）

| 产物 | 大小 | 备注 |
|---|---|---|
| `dist/` 总计 | 232 KB | `npm run build` 产物 |
| `assets/index-*.js` | 69,939 B | 入口 chunk（vue + pinia + router） |
| `assets/_plugin-vue_export-helper-*.js` | 62,384 B | 共享 chunk |
| `assets/Chat-*.js` | 28,470 B | 聊天页 chunk |
| `assets/Chat-*.css` | 13,533 B | 聊天页样式（当前最大一份） |
| `assets/Login-*.js` / `.css` | 7,023 B / 3,878 B | 登录页 |

基线用途：UI-04/UI-05 对比构建体积，防 UI 改造显著膨胀（目标 ±20% 内，允许因图标内联小幅增加）。

### 2.4 现有 `core/` 文件职责（UI-02 前必读）

| 文件 | 行数 | 职责 | 允许 UI 侧做什么 |
|---|---:|---|---|
| `core/ws.ts` | 411 | WS 状态机、帧收发、心跳、重连、终端关闭码 | 不改；只订阅 `on('frame')`/状态 |
| `core/sync.ts` | 75 | 去重排序、合并、缺口检测、同步游标、未读计算 | 不改；只调用纯函数 |
| `core/http.ts` | 234 | `request`/`requestBlob`、`ApiError`、401 回调 | 不改；新增下载复用 `requestBlob` |
| `core/db.ts` | 145 | IndexedDB 缓存（会话/消息/游标） | 不改 |
| `core/config.ts` | 148 | 服务器地址键、默认协议常量、桌面注入读取 | 不改（服务器地址 UI 已存在） |
| `core/attachments.ts` | 89 | 附件校验（扩展名/MIME/大小） | 不改 |
| `core/imageCompress.ts` | 105 | 图片压缩（最长边 1280、≤300KB） | 不改 |
| `core/blobCache.ts` | 56 | 图片 blob LRU 缓存 | 不改 |
| `core/groupChat.ts` | 50 | 系统消息文案、成员排序、群名标签 | 可扩展纯函数 |
| `core/presence.ts` | 20 | 在线状态标签（在线/离线/未知） | 不改 |
| `core/readReceipt.ts` | 53 | 已读判定、是否推进已读 | 不改 |
| `core/typing.ts` | 41 | typing 标签与过期清理 | 不改 |
| `core/typingThrottle.ts` | 36 | 客户端 typing 节流（3 秒） | 不改 |

---

## 3. 页面现状与改造映射（UI-00-02）

### 3.1 五类映射总表

| 页面/模块 | 直接保留 | 修改样式 | 抽共享组件 | 新增界面 | 不在范围 |
|---|---|---|---|---|---|
| Login | 三模式逻辑、服务器地址编辑、错误码文案 | 卡片、输入框、按钮、页签 | 表单控件样式（token 化） | 品牌区/大标题 + 三模式切换外观 | 注册协议勾选、第三方登录 |
| Chat | 消息链路、发送队列、已读、typing、presence、上传流程 | 会话列表、气泡、顶栏、发送区、滚动条 | `ConversationList`、`ChatHeader`、`SideNav`、`RightDrawer` | Emoji 面板、群资料抽屉、移动端聊天头 | 消息编辑、全文搜索、多选转发 |
| Contacts | 用户搜索、发起单聊、建群、成员多选 | 搜索框、结果行、chips、按钮 | `ContactList`、`ContactProfile` | 联系人资料面板、新建群对话外观 | 通讯录分组、组织架构 |
| Settings | 账号信息、服务器地址、退出登录 | 分组卡片、开关、输入 | `SettingsSection`、`Switch` | 外观设置项（浅色/渐变） | 消息通知后端联动、深色主题 |
| Admin | 路由与占位 | 表格/按钮跟随 token | — | — | 管理功能增强（非 UI 范围） |
| Download | 路由与两个安装包入口 | 卡片、版本信息 | — | — | 自动更新 |
| 全局布局 | `App.vue` 结构、`ConnectionBanner` | 断线 banner 外观 | — | 左侧主导航（`SideNav`） | 多窗口、标签页 |

### 3.2 逐页现状

#### 3.2.1 Login（`pages/Login.vue`，547 行）

- **结构：** 单卡布局（`login-page` 居中 + `login-card` 约 360px 宽），卡内依次为：品牌标题 → 服务器地址行（Electron 壳可编辑）→ 三模式页签（注册 / 验证码登录 / 密码登录）→ 对应表单 → 提交按钮 → 错误提示。
- **逻辑：** `mode` 状态机；发送验证码带冷却倒计时；`ERROR_MESSAGES` 把服务端错误码映射为中文；成功后 `applySession` 写入 auth store 并跳转 `/chat`。
- **改造判定：** **修改样式 + 结构重排**（模式切换外观、输入框图标、主按钮渐变），逻辑不动。
- **风险：** 服务器地址行是 Electron/移动端必填项，改版时**不得删除**；`isFileShell` 判断依赖 `config.ts` 注入，不可改成运行时探测。

#### 3.2.2 Chat（`pages/Chat.vue`，495 行）

- **结构：** 两栏 grid（`280px 1fr`）：左栏顶部「消息 + 发起聊天」、下方 `ConversationItem` 列表；右栏顶部标题（单聊带 `PresenceDot` + 在线标签；群聊带成员数 + 群成员按钮）、typing 提示、`MessageList`、`SendBox`（附件按钮 + 计数器 + 发送）。群聊时右侧 `GroupMembersPanel` 抽屉。空态用 `EmptyState`。
- **逻辑（禁止改）：** `atBottom` 驱动已读推进；`markActiveConversationRead` 按 `shouldAdvanceRead` 上报；会话被移除时关闭当前会话并提示；上传→压缩→发送 Flow；`TypingThrottle` 3 秒节流；`watch(selfUserId)` 清理面板与 typing。
- **改造判定：** **结构重构 + 样式重写**：拆出 `SideNav`（一级导航，新）、`ConversationList`（列表 + 搜索/筛选，UI-02 抽）、`ChatHeader`（标题/在线/操作按钮），聊天区与发送区保留逻辑重写外观。
- **风险：** 该文件是消息链路可视化最集中的地方，拆分时**先抽组件不改数据流**；每批 `npm run build` + 手动收发验证。

#### 3.2.3 Contacts（`pages/Contacts.vue`，452 行）

- **结构：** 顶部模式切换（直接聊天 / 建群）；搜索框 + 结果列表；建群模式有群名输入 + 已选成员 chips + 创建按钮。
- **逻辑：** `usersApi.search` 搜索；`conversations.createDirect` 后跳 `/chat/:id`；`createGroup` 后跳转；错误直接展示 `ApiError.message`。
- **改造判定：** **样式重写 + 抽组件**：`ContactList`、`ContactProfile`（占位资料面板，计划 §16.3 归入 UI-03）、`NewGroupDialog`（复用现有建群逻辑）。
- **风险：** 搜索是服务端分页/上限行为，UI 改版不得改成前端全量过滤。

#### 3.2.4 Settings（`pages/Settings.vue`，203 行）

- **结构：** 账号信息行（邮箱/昵称）→ 服务器地址编辑（保存后断线重连）→ 提示音/桌面通知开关 → 退出登录。
- **逻辑：** 账号信息来自 auth store；服务器地址经 `config.setServerUrl` + `chatWs.disconnect/reconnect`；开关状态来自 settings store（**当前不持久化**，见 H-05）。
- **改造判定：** **样式重写 + 分组组件**；新增「外观」分组（浅色/渐变默认，深色禁用占位，按计划 §16 显式占位）。
- **风险：** 服务器地址保存涉及连接重建，改版不得改变先 disconnect 再 reconnect 的顺序。

#### 3.2.5 Admin（27 行）/ Download（32 行）

- **现状：** 极简占位（标题 + 说明 + 少量列表/链接），无样式体系。
- **改造判定：** **B 档**：跟随全局 token 做基础样式（标题层级、卡片、按钮），不扩展功能；计划 §2.1 要求保留路由可访问。

---

## 4. Core / Store / API 链路与边界（UI-00-03）

### 4.1 数据链路（改造必须遵守的方向）

```
WS 帧 → core/ws.ts（状态机，on('frame')）
      → stores/*.subscribeRealtime()（conversations / messages / typing / presence）
      → 组件 computed / props（只读渲染 + 用户事件 → store action）

用户操作 → 组件 emits → pages 调 store action → store 调 api/*（REST）
                                       ↘ core/ws.ts send(frame)（消息/已读/typing）
```

**规则：** 组件不直接 `new WebSocket`、不直接 `fetch`（`api/` 之外）、不自己算已读/未读（用 store 暴露的派生值）。

### 4.2 帧处理位置与 UI 可消费状态

| 帧 / 事件 | 处理位置 | UI 可消费 | 改造约束 |
|---|---|---|---|
| `message.new` / `message.ack` / `error` | `stores/messages.ts` | 消息列表、pending 状态、重发入口 | 不改去重/排序/重试语义 |
| `conv.updated`（含 `removedConversationIds`） | `stores/conversations.ts` | 会话列表、成员数、被移除关闭 | 不改移除判定 |
| `read.updated` | `stores/conversations.ts`（单聊对端已读） | 最后一条自己消息「已读/未读」 | 不改游标合并规则 |
| `typing` | `stores/typing.ts`（3 秒 TTL） | 「正在输入」提示 | 不改 TTL |
| `presence` | `stores/presence.ts`（仅收变更，无初始查询） | 在线圆点（未知=灰） | 不得伪造「未知」为在线 |
| `kicked` / 4001 / 4002 | `core/ws.ts` + `main.ts` | 登录失效/被踢提示 | 不改终端关闭处理 |
| 连接状态 `statechange` | `core/ws.ts` | `ConnectionBanner`、发送区禁用态 | 不改状态机 |

### 4.3 允许 / 禁止改动清单

| 类别 | 内容 |
|---|---|
| **禁止** | `core/ws.ts`、`core/sync.ts`、`core/config.ts` 的协议常量与状态机；store 的发送队列/幂等/重连补拉/已读/typing/presence 语义；`api/` 的路径与请求体；`types/frames.d.ts` 的服务端字段语义；路由路径（`/login`、`/chat/:id?`、`/contacts`、`/settings`、`/admin`、`/download`） |
| **允许** | 组件模板与样式；布局与响应式；组件拆分/新增；纯 UI 状态（抽屉开关、草稿、滚动位置、筛选条件）；`core/` 内新增纯显示工具（如时间格式化、emoji 插入、消息分组），并配 Vitest |
| **需先改文档** | 任何要改帧字段、REST 路径、路由语义、服务端行为的想法（先停止并报用户） |

---

## 5. 全局样式与组件重用分析（UI-00-02/03）

### 5.1 现有 token（`styles/variables.css`，51 行）

| 变量 | 当前值 | 目标设计（计划 §3.1） | 处理 |
|---|---|---|---|
| `--color-primary` | `#2f7d5d`（绿色） | `--ec-brand-600 #1769D2` / 渐变 `#3D82F6 → #2E74E8` | 新增 `--ec-*` 层并全站迁移 |
| `--color-primary-hover` | `#276a4f` | 深一档 `#1769D2` 派生 | 同上 |
| `--color-primary-soft` | `#e6f2ec` | `#EAF2FF`（选中/浅底） | 同上 |
| `--color-danger` | `#d64545` | `#C44747` | 同上 |
| `--color-success` | `#2f9e44` | `#2F9E6E` 类语义色 | 同上 |
| `--color-warning` | `#d9480f` | `#B26A00` 系 | 同上 |
| `--color-bg` | `#f6f7f8` | `#F5F7FB` | 同上 |
| `--color-surface` | `#ffffff` | `#FFFFFF` | 保留 |
| `--color-border` | `#e3e6e8` | `#E5E9F0` | 同上 |
| `--color-text` | `#1f2328` | `#1F2937` | 同上 |
| `--color-text-secondary` | `#6b7280` | `#5F6E80`（对比度修正） | 同上 |
| `--bubble-self` | `#d8f3e3`（浅绿） | 品牌蓝气泡 + 白字 | 重点 |
| `--radius` / spacing | 6/10/16px；4~32 | 计划 §3.2 圆角/间距体系 | 对齐 |
| `--header-height` | 56px | 64~72px 顶部栏（§16.5） | 调整 |

**迁移策略（建议）：** 在 `variables.css` 中**新增 `--ec-*` 变量层**（值取计划 §3.1，主按钮/选中态用对比度达标的 `#1769D2`），旧 `--color-*` 暂时保留并映射到新值，逐页替换后（UI-05 收尾）删除旧变量。这样每批可构建、可回退，避免一次性全站改名的巨大 diff。

### 5.2 全局样式

- `global.css`（51 行）：reset、`body` 背景/字号 14px/行高 1.5、链接色 —— **保留**，UI-01 中补字体栈、背景渐变、滚动条与 `::selection`。
- 现状 **无 CSS 框架、无 normalize 之外的第三方样式**；组件样式均为 `<style scoped>`，`Chat.vue` 用 `chat-page__*` BEM 前缀。
- 现状全站 **无 `@media`**（§9 H-02）。

### 5.3 现有组件表（11 个，共 1658 行）

| 组件 | 行数 | 职责 / Props / Emits | UI 改造动作 |
|---|---:|---|---|
| `Avatar.vue` | 40 | 首字母头像；`avatarFileId`（潜在 bug，见 H-04） | 改造：头像/在线点组合；修 bug |
| `ConnectionBanner.vue` | 72 | 断线/重连中横幅 | 改造样式；位置随布局调整 |
| `ConversationItem.vue` | 163 | 会话行（名称/预览/时间/未读） | 改造为列表项（含群标、在线点） |
| `EmptyState.vue` | 37 | 空态（标题/说明/插槽） | 复用，补图标位 |
| `GroupMembersPanel.vue` | 460 | 群成员抽屉（列表/邀请/移除/退出） | 改造为 `RightDrawer` 内容之一 |
| `ImagePreview.vue` | 95 | 图片全屏预览 | 保留，样式微调 |
| `MessageAttachment.vue` | 240 | 图片缩略图 / 文件卡片 / 下载 | 保留，样式跟随气泡 |
| `MessageBubble.vue` | 194 | 消息气泡（文本/系统/附件/时间/已读） | 改造：左右气泡、系统消息居中 |
| `MessageList.vue` | 176 | 滚动列表、加载更早、贴底检测 | 改造：日期分隔、滚动条、空态 |
| `PresenceDot.vue` | 49 | 在线圆点（在线/离线/未知） | 复用，尺寸与颜色跟 token |
| `SendBox.vue` | 196 | 输入框 + 附件 + 计数器 + 发送 | 改造：表情按钮、Enter 提示、移动端安全区 |

### 5.4 计划 §16.3 需要新增的组件（当前均不存在）

| 新组件 | 归属阶段 | 说明 |
|---|---|---|
| `SideNav.vue` | UI-01 | 左侧一级导航（消息/联系人/群聊/设置 + 底部账号） |
| `ChatHeader.vue` | UI-02 | 聊天区顶栏（标题/在线/成员数/操作按钮） |
| `ConversationList.vue` | UI-02 | 会话列表 + 搜索 + 筛选（全部/未读/群聊） |
| `EmojiPanel.vue` | UI-02 | Emoji 插入面板（Unicode，插入草稿不发送） |
| `GroupInfoPanel.vue` | UI-03 | 群资料抽屉（成员/邀请/角色/退出，可复用 `GroupMembersPanel` 逻辑） |
| `ContactList.vue` / `ContactProfile.vue` | UI-03 | 联系人列表与资料面板（资料为显式占位） |
| `NewGroupDialog.vue` | UI-03 | 建群对话框（复用 Contacts 现有建群逻辑） |
| `SettingsSection.vue` | UI-03 | 设置分组卡片 |
| `MobileTabBar.vue` / `MobileChatHeader.vue` | UI-04 | 移动端底部导航与聊天头（≤800px） |

### 5.5 响应式与跨端现状

- 桌面：`Chat.vue` 固定 `grid-template-columns: 280px 1fr`，其余页面为居中单列。
- **移动端（390×844）实测：两栏并排横向溢出**——侧栏与聊天区同时渲染，聊天区与输入框被推出可视区（见 `docs/ui/screens-before/07-会话列表-移动.png`、`08-聊天-移动.png`，物理像素 780×1688）。这是目前手机 APK 上体验不佳的根因之一。
- 无安全区处理（`env(safe-area-inset-*)`），无移动端底部导航。
- Electron 最小窗口 960×640（`MIN_WIDTH`/`MIN_HEIGHT`），属于 801~1199 断点。

---

## 6. 契约字段核对（UI-00-04）

### 6.1 消息与状态字段

| 字段 | 位置 | 是否支撑目标 UI | 说明 |
|---|---|---|---|
| `messageId` / `clientMsgId` / `seq` / `createdAt` | `Message` | 是 | 时间显示、去重、排序、已读判定 |
| `msgType`（text/image/file/system） | `Message` | 是 | 气泡分支 |
| `content.text` / `content.fileId/name/size/mime` / `content.event/text` | `MessageContent` | 是 | 文本/附件/系统消息渲染 |
| 发送状态（发送中/已发送/失败） | `stores/messages.ts` pending 队列 | 是 | 状态由 store 维护，UI 只读，不改状态机 |
| `lastSeq` / `lastReadSeq` / 未读 | `Conversation` + `conversations.ts` | 是 | 列表未读徽标、筛选 |

### 6.2 已读 / typing / presence

| 能力 | 字段/事件 | 现状 | 缺口 |
|---|---|---|---|
| 单聊已读回执 | `read.updated`；`peerReadSeqOf`；`readStateOf`；`lastOwnMessageOf` | 已实现并展示「已读/未读」 | 无 |
| 本端已读上报 | `markRead` / `flushReadSeq` + `shouldAdvanceRead` | 已实现（贴底且可见才推进） | 无 |
| typing | `typing` 帧 + `typingLabelOf` + 3 秒 TTL | 已实现 | 样式待改（提示条位置） |
| 在线状态 | `presence` 帧 + `presenceStateOf` | 已实现（仅变更、无初始查询） | 未知态必须在 UI 中显示「未知」而不是离线（计划 §16.3） |

### 6.3 文件 / 图片

| 能力 | 现状 | 缺口 |
|---|---|---|
| 上传（`api/files.upload`，50MB 上限，12 种扩展名） | 已实现 | 无进度条（计划标为 P1，不做） |
| 鉴权下载（`requestBlob` + `blobCache`，不裸 `img src`） | 已实现 | UI 改造不得回退为直接 URL |
| 图片发送前压缩（1280px / ≤300KB） | 已实现 | 无 |
| 气泡内缩略图 / 文件卡片 / 预览 | 已实现（`MessageAttachment`/`ImagePreview`） | 样式待改 |

### 6.4 群管理

| 能力 | 字段/接口 | 状态 |
|---|---|---|
| 群信息（名称/成员/角色/成员数） | `Conversation.title/members[]/role` | 支持 |
| 被移除 / 成员变更 | `conv.updated` 全量快照 + `removedConversationIds` | 支持（UI 已处理关闭与提示） |
| 建群 / 邀请 / 移除 / 退出 | `createGroup` / `addMembers` / `removeMember` | 支持（资料抽屉已实现） |
| 系统消息 | `msgType=system` + `content.event/text` | 支持（渲染为居中灰字为 UI-02 动作） |
| 群解散、改群名、转让群主 | 服务端不支持 | **不做**（显式占位或隐藏，不伪造入口） |

**结论：契约字段完整支撑 UI V1.0 目标页面，无需任何协议或后端改动；所有「参考包有、服务端没有」的能力一律按计划 §1.4/§16.2 处理为显式占位（灰显 +「即将上线」）或隐藏，禁止假交互。**

---

## 7. 运行态与跨端约束（UI-00-05）

| 环境 | 加载方式 | 对 UI 的硬约束 |
|---|---|---|
| Web dev | Vite `127.0.0.1:5173`，`/api`、`/ws` 代理到 `127.0.0.1:8080` | 可用 history 路由 |
| Web prod（Electron 内） | Electron 以 `file://` 加载 `resources/web-dist/index.html` | **必须 hash 路由**（`router/index.ts` 已按 `isFileProtocol` 切换）；**`vite base` 必须为 `'./'`**；资源不得用绝对路径；M4-07 曾因 `base`/history 白屏 |
| Electron 壳 | `apps/desktop`：preload 注入 `window.eisechatDesktop.serverUrl`；最小窗口 960×640；无地址栏、外链走系统浏览器 | 登录页服务器地址输入框必须保留；不做浏览器前进/后退假设；窗口尺寸回归（960×640 与 1440×900） |
| Capacitor Android | `webDir=../web/dist`，`androidScheme: 'http'`，WebView 内 `file`/`http` 混合内容，minSdk 26 | 登录页服务器地址必填；无刷新按钮（需处理已登录态）；**安全区** `env(safe-area-inset-*)`；移动端 ≤800px 专属布局（UI-04） |
| 移动端视口 | 390×844（当前实测溢出） | 断点见 §5.5/§16.5 |

**运行态证据（审查时）：** 服务端 `Chat.Server` PID 20444（`127.0.0.1:8080`）、Vite PID 43632（`127.0.0.1:5173`）；SQLite `data/chat.db` 中 users 84 / messages 691；开发固定验证码 `123456`（仅 Development，由 `MockEmailSender` 输出到控制台，不写日志）；截图使用测试账号 `ui00-a@example.com` / `ui00-b@example.com`（密码仅本地测试）。以上均未进入仓库。

---

## 8. 现状基线记录（UI-00-06）

| 截图 | 页面 | 端 | 尺寸（CSS/物理） | 关键观察 |
|---|---|---|---|---|
| `01-登录-桌面.png` | 登录 | 桌面 | 1440×900 | 单卡居中、绿品牌、三模式页签、服务器地址行 |
| `02-登录-移动.png` | 登录 | 移动 | 390×844 / 780×1688 | 卡片贴边、可用但无移动端专属布局 |
| `03-会话列表-桌面.png` | 会话 | 桌面 | 1440×900 | 280px 固定侧栏、无一级导航、列表项绿色选中态 |
| `04-聊天-桌面.png` | 聊天 | 桌面 | 1440×900 | 两栏、无顶栏工具栏、自己消息浅绿气泡、最后一条自己消息显示「未读」、输入区附件+计数器 |
| `05-联系人-桌面.png` | 联系人 | 桌面 | 1440×900 | 搜索+结果行，缺资料面板 |
| `06-设置-桌面.png` | 设置 | 桌面 | 1440×900 | 简单列表式分组 |
| `07-会话列表-移动.png` | 会话 | 移动 | 390×844 / 780×1688 | 两栏并排溢出，聊天区不可见 |
| `08-聊天-移动.png` | 聊天 | 移动 | 390×844 / 780×1688 | 输入框被推出右侧可视区，横向裁切 |

**未覆盖的截屏（诚实记录）：** 群聊资料、管理页、下载页、断线/重连状态未在本轮截图（群聊需要两账号建群；管理页需要 admin 账号；断线需要停服操作）。计划中 UI-00-06 要求 ≥6 张已满足（8 张）；上述 4 个场景在 UI-05 前后对比时补拍，不阻塞 UI-01。

**基线观察结论：** 现状视觉为「绿色品牌 + 两栏聊天 + 无导航」；目标设计为「蓝白渐变 + 一级导航 + 列表/聊天/抽屉三段」；改造属于**整体重做视觉层**，但可以按页面分阶段替换（`D-10` 浅色先做）。

---

## 9. 隐藏缺陷与风险清单（UI-00-07 / §14 第 4 项）

| 编号 | 位置 | 问题 | 等级 | 影响阶段 | 处理动作 |
|---|---|---|---|---|---|
| H-01 | `styles/variables.css` 全站 | 品牌绿色与新蓝白设计冲突 | 高 | UI-01 | 新增 `--ec-*` token 层，逐页迁移 |
| H-02 | `apps/web/src`（全目录） | 无任何 `@media`，移动端横向溢出 | 高 | UI-04 | 补断点与移动端布局（与 H-01 并行不可回避） |
| H-03 | `pages/Chat.vue` | 两栏结构无一级导航/顶栏，IA 重构集中在此文件 | 高 | UI-01/02 | 拆 `SideNav`/`ConversationList`/`ChatHeader`，先抽后改样式 |
| H-04 | `components/Avatar.vue:17` | `<img :src="avatarFileId">` 把文件 ID 当 URL 用（头像功能未启用，属潜在缺陷） | 中 | UI-01 | 改为字母头像；接头像时走鉴权 blob |
| H-05 | `stores/settings.ts` / `Settings.vue` | 提示音/桌面通知开关未持久化（注释称「M1 统一做」未实现） | 中 | UI-03 | 用 localStorage 持久化；实现桌面通知需 Electron 侧配合（占位） |
| H-06 | `stores/contacts.ts` | 联系人 store 存在但 Contacts 页面直接用 API，store 未使用 | 低 | UI-03 | 要么接入要么删除，避免死代码 |
| H-07 | `components/GroupMembersPanel.vue:126,143` | 用原生 `window.confirm`，无统一 toast/对话框体系 | 低 | UI-03 | 引入轻量自绘 `ec-dialog`（不引第三方库） |
| H-08 | 多页 | 错误处理多为直接展示 `ApiError.message`，无统一文案/样式 | 低 | UI-03 | 统一错误提示组件与文案映射 |
| H-09 | `pages/Admin.vue`、`pages/Download.vue` | 极简占位，与新视觉不一致 | 低 | UI-03 | 样式跟随 token，功能不扩展 |
| H-10 | `pages/Chat.vue` | 495 行单文件 + `chat-page__` BEM，与 `ec-` 新前缀并存 | 中 | UI-02 | 拆分组件并逐步迁移类名；删除旧样式前确认无引用 |
| H-11 | `MessageList.vue` | 无日期分组、无时间格式化（时间分散在气泡内） | 低 | UI-02 | 纯前端工具函数 + 测试 |
| H-12 | 全局 | 无自定义滚动条、焦点可见性、`prefers-reduced-motion` | 低 | UI-01 | 补全局基础样式 |
| H-13 | `docs/EiseChat_UI_交互验收包_V1.1/` | 两个 3.4MB 工作台 HTML 内容完全相同（md5 一致，合计 6.9MB 冗余） | 中 | 前置决策 | 建议去重 + 目录归一，**需用户确认后执行** |
| H-14 | 同上 | 参考资产路径未归一（`docs/` 根下，非 `docs/ui/`） | 低 | 前置决策 | 同上 |
| H-15 | `vite.config.ts` / `router` | `base: './'` + file:// hash 路由的组合是 M4-07 白屏根因，改版可能再犯 | 高 | 全阶段 | UI-04/UI-06 必须重打安装包后在 Electron 内实测；验收清单固定为一条 |
| H-16 | 测试基线 | 计划 §14 记载 108 个 Vitest 用例，本轮未实际运行 | 中 | UI-01 前 | 开工前跑一次基线记录（允许，约 1 秒） |

---

## 10. 改造范围三档清单（UI-00-07）

### A 档：必须改（UI V1.0 范围内）

- `apps/web/src/styles/variables.css`（新增 `--ec-*` token 层，全量设计变量）
- `apps/web/src/styles/global.css`（字体、背景、滚动条、焦点、动画基础）
- `apps/web/src/pages/Login.vue`、`Chat.vue`、`Contacts.vue`、`Settings.vue`
- `apps/web/src/components/`：`Avatar`、`ConnectionBanner`、`ConversationItem`、`EmptyState`、`MessageBubble`、`MessageList`、`SendBox`、`GroupMembersPanel`、`MessageAttachment`、`ImagePreview`、`PresenceDot`（改造/复用）
- 新增 `SideNav`、`ConversationList`、`ChatHeader`、`EmojiPanel`、`GroupInfoPanel`、`ContactList`、`ContactProfile`、`NewGroupDialog`、`SettingsSection`、`MobileTabBar`、`MobileChatHeader`
- `apps/web/src/router/index.ts`（仅当布局壳需要嵌套路由；否则不动）

### B 档：建议跟随（低风险、低成本）

- `pages/Admin.vue`、`pages/Download.vue`（token 化样式）
- `App.vue`（全局布局容器调整）
- `core/` 新增纯 UI 工具：`emoji.ts`（插入/计数）、`format.ts`（时间/日期分组）、`scroll.ts`（贴底工具）等，各配 Vitest

### C 档：不做 / 延后（明确不进入本次改造）

- 深色主题（仅预留 token 结构，计划 D-10）
- 群解散/改群名/转让群主（服务端不支持）
- 消息编辑/撤回/全文搜索/多选转发（P2 或未做）
- 通讯录组织架构、自定义壁纸、卡通头像（参考包以外的扩展）
- 第三方 UI/图标/动画/表情库（依赖约束）
- 修改 `core/ws.ts`、`core/sync.ts`、`api/`、`types/frames.d.ts`、`server/`、`protocol/`

---

## 11. 与 UI-01~UI-06 的任务对齐与实际文件路径（UI-00 第 3 项）

| 阶段 | 任务（计划 §13） | 实际文件路径（可照做） | 新增文件 | 验收方式 |
|---|---|---|---|---|
| UI-01 | 设计 token + 基础组件 + 三栏骨架 | `styles/variables.css`、`styles/global.css`、`components/{SideNav,ChatHeader,…}.vue` | `SideNav.vue`、`ChatHeader.vue`、`EmojiPanel.vue` 骨架 + `ec-*` token | `npm run build`；桌面 1440/1200 宽度查看无溢出 |
| UI-02 | 聊天页三段布局 + 消息区 + 导航交互 | `pages/Chat.vue`、`components/{ConversationList,ChatHeader,MessageList,MessageBubble,SendBox,EmojiPanel}.vue` | `ConversationList.vue` | build + 双账号手动收发；图片/文件/群聊回归 |
| UI-03 | 其余页面 + 抽屉 + 占位策略 | `pages/{Contacts,Settings,Login}.vue`、`components/{GroupInfoPanel,ContactList,ContactProfile,NewGroupDialog,SettingsSection}.vue` | 上述 5 个 | build + 手动逐页走访；占位项灰显可解释 |
| UI-04 | 响应式 + 移动端布局 + Electron 回归 | 全部页面样式 + `MobileTabBar.vue`、`MobileChatHeader.vue` | 2 个 | 390×844 / 800px / 1099px / 960×640 四档检查无横向溢出 |
| UI-05 | 验收 + 对比截图 + 回归 | 无代码（可能微调） | `docs/ui/screens-after/` | Vitest + e2e(申请) + 人工清单 + 对比截图 |
| UI-06 | 1.1.0 打包回归 | `apps/desktop`、`apps/mobile`（重新构建）、`server/Chat.Server/Program.cs`（download 目录） | exe/apk | 覆盖安装 + Electron 内实测 + APK 真机实测 |

> 注：UI-01 的「三栏骨架」可以不一次性搭完——建议 **UI-01 只做 token + 基础组件 + `SideNav` 外壳**（`Chat.vue` 暂不拆分），UI-02 再拆 Chat.vue，降低一次改动过大的风险。

---

## 12. 交互验收包资产清点与差距表（UI-00-09）

### 12.1 资产清点

| 资产 | 大小 / 数量 | 用途 | 使用方式 | 建议动作（待用户确认） |
|---|---|---|---|---|
| `EiseChat_UI_交互验收包_V1.1/index.html` 与 `EiseChat_UI_交互验收工作台.html` | 各 3,467,601 B，md5 相同（`0d5ef6be…`） | 离线交互工作台（内嵌 9 页 + 9 图） | 只读浏览参考 | **去重**：保留 `index.html`，删除重复文件（需确认） |
| `reference-images/` | 9 张 + 总览，约 2.4 MB | 逐图验收基准（1440×900 桌面 7 张、390×844 Android 2 张） | 视觉方向对照 | 归一至 `docs/ui/reference-images/`（需确认） |
| `reference-html/` | 9 页（25–36 KB/页） | 静态视觉参考 | 只读 | 同上 |
| `interactive-pages/` | 9 页（37–53 KB/页） | 可点击交互演示 | 只读，不复制代码 | 同上 |
| `00-交互验收工作台_预览.png` / `10-Android_交互工作台_预览.png` | 336,114 B / 230,638 B | 工作台预览图 | 只读 | 保留 |
| `UI-验收标准.md` | 7,113 B（155 行） | 逐图验收清单与完成条件 | 作为验收清单来源；数值以计划 §3.1/§16.5 为准 | 保留 |
| `README.md` | 1,957 B | 使用说明 | 只读 | 保留 |

### 12.2 演示态索引（参考包中的演示交互 → 真实工程处理）

| 场景 | 参考包演示内容 | 真实工程现状 | 处理动作 |
|---|---|---|---|
| 01 桌面聊天首页 | 点击发送 → 出现「✓ 模拟已发送」气泡 | 真实 WS 发送 + ack + 状态机已实现 | 按真实实现，禁止「模拟已发送」文案 |
| 02 Emoji 面板 | 点击表情插入草稿，不自动发送 | 无 emoji 功能 | UI-02 实现 Unicode 插入（纯前端） |
| 03 群聊资料 | 成员列表/角色/在线点（演示数据） | 服务端与 UI 均支持真实成员与会话 | 用真实数据渲染；在线状态未知态显示「未知」 |
| 04 联系人 | 搜索结果与资料面板（演示联系人） | 用户搜索已实现；资料面板不存在 | UI-03 新建 `ContactProfile`，资料字段为显式占位 |
| 05 外观设置 | 主题卡片「保存」演示 | 设置开关未持久化；无主题切换 | UI-03 实现浅色/渐变（本地持久化），深色禁用占位 |
| 06 邮箱登录 | 演示验证码 `123456`、登录演示 | 真实验证码（Development 固定码仅代码路径） | 不复制演示码文案；错误码走真实映射 |
| 07 断线重连 | 演示断线横幅与重连动画 | 真实连接状态机已实现 | 用真实 `statechange` 驱动横幅 |
| 08/09 移动端 | 底部导航 / 聊天详情（演示） | 无移动端布局（横向溢出） | UI-04 新建 `MobileTabBar`/`MobileChatHeader` |
| 参考包中所有「更多操作/撤回/全文搜索」类入口 | 演示或标注不属于范围 | 服务端/计划均不支持 | 隐藏或按 §16 显式占位，禁止假入口 |

### 12.3 参考 → 实现差距（汇总）

- 参考包提供的是 **目标视觉与交互路径**，不是可直接嵌入的代码（原型离线、演示数据、无 `@media`、无真实状态层）。
- 「有设计但无后端」的能力（群解散/资料编辑/通知设置/深色主题等）按 §1.4 显式占位：**灰显 + 说明文字，不伪造数据、不显示假成功**。
- 参考包目录与重复文件属**资产整理**问题，需用户确认后执行（见 §12.1、§13.1 P-1）。

---

## 13. 下一步实施任务建议（UI-00 第 5 项 / §14）

### 13.1 需要用户确认的前置决策

| 编号 | 决策 | 建议 |
|---|---|---|
| P-1 | 参考验收包去重（删除重复工作台 HTML）与目录归一（移到 `docs/ui/`） | 建议执行；本报告未擅自删除/移动用户资产 |
| P-2 | UI-01 是否一次性引入 `--ec-*` token 并全站迁移，还是先聊天页后其余页 | 建议「新增 token 层 + 先聊天页」，其余页随 UI-03 自然迁移 |
| P-3 | 是否接受在 UI-01 阶段 `Chat.vue` 暂不拆分（只换 token 与外壳） | 建议接受，降低风险；拆分放 UI-02 |
| P-4 | 补拍群聊/管理页/下载页/断线基线截图 | 建议 UI-02 前补拍（不影响 UI-01） |

### 13.2 UI-01 第一批建议（可直接下发指令）

1. 运行一次基线：`npm run build` + `npm run test:unit`（记录用例数与耗时，写 `docs/test/ui-01-baseline.md`，不入库）。
2. `styles/variables.css` 增加 `--ec-*` token（计划 §3.1/§3.2 全量；对比度按 §16.5 修正值）。
3. `styles/global.css` 接入字体栈、背景、滚动条、`::selection`、`prefers-reduced-motion`。
4. 新增 `components/SideNav.vue`（桌面 ≥1200 显示完整 168~176px；1200 以下收为图标栏；≤800 隐藏，UI-04 再接移动端）。
5. `Chat.vue` 接入 `SideNav` 外壳（布局 grid 变 `auto minmax(...)`），**不拆分消息逻辑**。
6. 验收：`npm run build` 通过；1440/1200 宽度无横向滚动；`git status` 干净；提交 `feat：[UI-01]-引入蓝白设计 token 与左侧导航外壳`。

### 13.3 排期校准

- 计划 §6 估算 UI-00+UI-01 1.5~3 人日；现状（6 页/11 组件/无依赖）支持该估算。
- 建议压缩：`Admin`/`Download` 只做基础样式（省约 0.3 人日）；`EmojiPanel` 用 Unicode 网格 + 最近使用（不做分类搜索），与计划一致。
- 建议增加：响应式与 Electron/APK 回归至少预留 1 人日（当前计划 UI-04+UI-06 已含，无需改总量）。

---

## 14. 审查结论与进入编码条件

### 14.1 UI-00 九项完成情况

| 编号 | 任务 | 状态 |
|---|---|---|
| UI-00-01 | 清点文件、路由、依赖与构建命令 | 完成（§2） |
| UI-00-02 | 五类映射表 | 完成（§3.1） |
| UI-00-03 | `core/`、`stores/`、`api/` 依赖方向与禁止边界 | 完成（§4） |
| UI-00-04 | 验证状态/已读/文件/群管理字段支撑性 | 完成（§6） |
| UI-00-05 | Web dev/prod、Electron、Capacitor 运行约束 | 完成（§7） |
| UI-00-06 | 改造前基线截图 ≥6 张 | 完成（8 张，§8） |
| UI-00-07 | 风险与文件变更范围 | 完成（§9/§10） |
| UI-00-08 | 运行态命令、测试账号、截图脚本记录 | 完成（`docs/test/ui-00-tasks.md`，不入库） |
| UI-00-09 | 参考包资产清点、映射、演示态索引与差距表 | 完成（§12；去重/归一带用户确认） |

### 14.2 进入 UI-01 的条件

1. 用户确认 §13.1 的 P-1~P-3（P-4 可延后）。
2. UI-01 按 §13.2 六步执行，每批 `npm run build` 通过、提交前 `git status` 干净。
3. 消息链路最高风险清单（H-01/H-02/H-03/H-15）在 UI-04/UI-06 各有一次真机/真壳回归。

### 14.3 声明

- 本次审查为**只读**：未改 `apps/`、`server/`、`protocol/` 任何一行代码；未执行测试全量运行（Vitest 108 个用例未跑，记录为待基线）。
- 截图与测试数据均来自本地开发环境与测试账号，未包含真实用户数据、token 或密钥。
- 参考验收包未删除、未移动任何文件（§13.1 P-1 待确认）。

---

## 附录 A：审查用命令与证据（可复现）

```bash
# 文件与行数清点
find apps/web/src -name '*.vue' -o -name '*.ts' -o -name '*.css' | sort
wc -l apps/web/src/**/*.ts apps/web/src/**/*.vue apps/web/src/**/*.css

# 构建基线
cd apps/web && npm run build && du -sh dist

# 响应式缺失检查（应返回 0）
grep -rn '@media' apps/web/src

# 文档基线
head -3 docs/ui-upgrade-plan.md
```

- 截图脚本：`docs/test/ui00_capture.mjs`（一次性，不入库；使用 Chrome DevTools Protocol，`width=1440&height=900` 与移动 `width=390&height=844&deviceScaleFactor=2`）。
- 运行记录：`docs/test/ui-00-tasks.md`。
- 基线截图：`docs/ui/screens-before/`。
