# EriseChat UI 更新文档（现行）

> **状态**：现行｜**更新**：2026-11-12｜**依据**：截至 `b1e6ce7` 的最近 35 条提交 + 当前 `apps/web/src` 源码
> **硬约束**：`AGENTS.md` §14（界面与 UI 约束）｜**运行 / 构建 / 逐项验收**：`docs/web.md`
> **注意**：`docs/deprecated/` 下全部旧截图与验收包已废弃，**不得再作为 UI 判断依据**（见 §7）。

## 1. 文档定位

本文件是 UI 的**唯一现行说明文档**，回答三件事：现在长什么样、最近改了什么、还要做什么。

- 必须遵守 / 禁止的约束 → `AGENTS.md` §14
- 运行、构建、部署、逐项验收 → `docs/web.md`（尤其 §7 验收清单）
- 协议帧与表结构 → `protocol/`
- 本文件只做现状记录与索引，**不是**计划、验收报告或交互规格，不重复定义硬约束。

## 2. 最近 UI 变更（按 git 提交批次）

> 旧截图停留在 UI-00~UI-05 / UI-D 中途，与下列提交后的界面已不一致。本节依据 git 提交与当前源码，不依据截图。提交时间以 `git log` 为准，此处只按提交先后归组。

| 批次 | 代表性提交 | 变更要点 |
|---|---|---|
| UI-D P0 / P1 依赖与主题层 | `abfaf11`、`a821b20` | 引入 Naive UI / Phosphor / VueUse（均 MIT）；新增 `styles/themes/azure.css` 与 `index.html` 的 `data-ec-theme="azure"`，`core/theme.ts` 把色板映射为 Naive UI `themeOverrides`；主题层只做结构预留 |
| UI-D P2-A 信息密度 | `835da93` | 桌面导航、会话列表、消息顶栏补齐信息密度：筛选 tab（全部 / 未读 / 群聊）、发起聊天、顶栏搜索与更多入口 |
| UI-D P2-B 气泡与输入区 | `30f2486` | 气泡 14px 圆角 + 6px 尾角、时间与已读内嵌、连续消息分组、日期分隔、紧凑输入区、断线待发送提示 |
| UI-D P3 外观与浮起卡片 | `d6fae44` | 外观设置信息卡、群成员分区标题、表情面板搜索、移动端欢迎语、空状态、≥1200 桌面外层浮起卡片 |
| UI-D P4 组件库替换 | `5ca694d`、`b948788`、`9d761a8` | `NSwitch` / `NModal` / `NMessageProvider` + `ToastBridge` / `NInput` / `NDrawer` / `NCard` / `NFlex` 渐进替换，保持原 props、emit、默认插槽与焦点归还契约，调用方零改动 |
| UI-D 视觉修正 | `65518d4` → `7071b06`（回退）→ `cd711dc` | 顶栏下方浅色缝隙：把 `--ec-chat-gradient` 上移到 `.chat-page__chat` 共同容器，`.message-list` 改透明；`ChatHeader` 与 `SendBox` 保持不变 |
| F-35 媒体消息 | `50a1685`、`2469071`、`7dc873b`、`cec13af`、`f9f53ed`、`c56f4c5` | 图片 / 文件 / 语音**不再套气泡**，媒体卡走 `--ec-media-*` token、文件按类型显示矢量图标；上传队列按会话并发（全局上限 3）、不支持的文件不入队；语音播放器图标居中与圆角修正、清理未定义 token |
| F-41 群成员 | `e37cfca`、`4fe0031`、`ed7acaf`、`994c417`、`e77608a`、`f45c0ed` | 群成员资料浮窗（发消息 / 加好友）；列表多列网格后回退单列紧凑行、去分隔线、默认 15 人；在线人数并入成员标题；修复弹窗内输入框无法输入、群名上限收紧为 50 字符 |
| 真实头像 | `68a443b` | 顶栏、消息气泡、收藏页改用真实头像，不再只用首字母占位 |
| 左导航与账号入口 | `ec9012c`、`dbde6bb`、`9b3cd1d` | 左上角由「E + EriseChat」改为头像 + 昵称，点击弹 `AccountCard` 浮窗；移除底部账号卡与连接状态；头像 28→40px；紧凑栏只显示头像 |
| 输入区与气泡尺寸 | `df775dd` | 输入框默认 3 行、最高 5 行后滚动；气泡上限 `640px / 85%`；**鼠标左键长按不再弹菜单**，只保留右键，手指 / 触控笔仍保留长按 |
| 其他 | `0fbdd33`、`10a3982`、`b1e6ce7` | Android 壳外链改走系统浏览器；草稿孤儿清理与超限修复；提交信息规范改为 `类型：简述`、不写任务编号 |
| P0 优化落地 | 2026-11-12 | 加载态骨架 + `aria-busy`、动效 token 落地、术语统一「联系人」、硬编码色值回收与 token 审计（详见 §5） |
| P1 落地 | 2026-11-12 | 全局快捷键、全局搜索入口打通、外观字号 / 密度真设置、首屏 1 秒慢反馈、消息行 hover「⋯」（详见 §5） |
| P1 收口 | 2026-11-12 | 搜索统一为全局面板并加消息 / 联系人 / 群三类 tab；状态反馈抽出 `.ec-state-*` 共享样式与文案规范（§3.7） |
| P2 深色主题解冻 | 2026-11-12（仅文档） | 撤销 UI-D 冻结（`AGENTS.md` §14.2），新增完整实施规格 §9 |
| P2 深色主题实现 | 2026-11-12 | 新增 `dark.css` 色板、主题设置与首屏预置、Naive UI overrides 按主题切换、设置页开放浅色 / 深色入口；静态对比度 / 构建 / 体积校验通过（详见 §9） |
| 导航 hover 反馈 | 2026-11-12 | 左侧主导航原 hover 底色与导航底仅 1.016:1、几乎不可辨；新增导航专用 `--ec-nav-item-hover-bg` / `--ec-nav-item-hover-text` 并在两套主题分别定义，hover 时底色与文字/图标色同时变化，不引入位移 |
| 动效规则固化 | 2026-11-12（**仅规则与文档**） | `AGENTS.md` §14.6 阈值翻倍至 200 / 520 KB，新增 §14.9 固化加载反馈与动效规范；批次 4 主线定为 `motion`，不做清单收窄；不改代码，目视 / 多端验收暂缓至代码修改完成后 |
| 动效批次 4 落地 | 2026-11-12 | 引入 `motion`（走 `motion/mini` 入口，MIT，gzip +5.2 KB）；新增 `core/motion.ts` 与 `LoadingDots.vue`；补齐 `--ec-ease-in` / `--ec-ease-in-out` / `--ec-motion-stagger(-max)`；首屏骨架受控入场、三处三点加载反馈、深色首屏预置底色（详见 §5「批次 4」） |

## 3. 当前 UI 基线

### 3.1 页面与组件

- **页面**：`Chat.vue`（三栏核心）、`Login.vue`（注册 / 验证码 / 密码 / 找回密码 4 模式 + 壳内服务器地址）、`Contacts.vue` + `ContactProfile.vue`、`Settings.vue`（分区设置）、`Favorites.vue`；`Admin.vue` / `Download.vue` 为占位。
- **会话与聊天**：`ConversationList/Item`、`ChatHeader`、`MessageList/Bubble`、`SendBox`、`MessageAttachment`、`VoiceMessagePlayer`、`MessageActionMenu`、`EmojiPanel`、`ForwardDialog`、`ImagePreview`、`EmptyState`、`UnreadBadge`、`PresenceDot`、`Avatar`。
- **全局壳**：`SideNav`、`MobileTabBar`、`ConnectionBanner`、`ToastBridge`、`AccountCard`。
- **群与搜索**：`NewGroupDialog`、`GroupMembersPanel`、`MemberProfileCard`、`ConversationFilesPanel`、`SearchDialog`、`SearchInput`、`SearchResultItem`。

### 3.2 设计 token 与主题

- `styles/variables.css` 集中全部 `--ec-*`：品牌色（`#2563eb`）、聊天区三段渐变与光晕、文本三级对比、边框、媒体卡 `--ec-media-*`、Inter / YaHei 字体栈、4px 基数间距 `--ec-space-1..8`、圆角（气泡 14px + 尾角 6px）、阴影、`--ec-transition-fast/base/slow`（120 / 160 / 200ms）、四档布局宽、`--ec-safe-top/bottom`；文件末尾保留旧 `--color-*` 兼容别名。
- **动效 token（批次 4）**：`--ec-ease`（入场，强缓出）、`--ec-ease-in`（退出）、`--ec-ease-in-out`（页面内位移 / 展开收起）、`--ec-motion-stagger`（60ms）、`--ec-motion-stagger-max`（200ms）。CSS 过渡直接引用；JS 动效经 `core/motion.ts` 读取同一组 token（不另立数值），`prefers-reduced-motion` 由 `global.css` 全局降级 + 封装层二次兜底。
- U-04 后新增语义色与尺寸 token（聚焦外圈、拖拽遮罩、日期胶囊底、浮标投影、主题预览渐变、断线横幅橙系、蓝色气泡叠加色、气泡内边距与移动端圆角、命中高亮底、半透明胶囊底、`--ec-vvh` 回退基线），颜色类在 `styles/themes/azure.css` 双写；组件内不再写裸色值（`Avatar.vue` 的 JS 哈希色板与 `core/imageCompress.ts` 的 canvas 填充色属数据资产例外）。
- `styles/themes/azure.css` + `data-ec-theme="azure"` 提供「澄蓝」浅色主题，`core/theme.ts` 映射 Naive UI `themeOverrides`。
- **深色主题已实现（2026-11-12）**：新增 `styles/themes/dark.css` 覆写全部语义 token；设置页提供浅色 / 深色两个可点击入口，主题存本机 `localStorage`（`erisechat.settings.theme`），`index.html` 首屏预置避免闪烁，`App.vue` 写 `data-ec-theme` 并切换 Naive UI overrides。不做系统主题自动跟随。实施规格见 §9，约束见 `AGENTS.md` §14.2。
- 左侧主导航 hover 使用专用 token `--ec-nav-item-hover-bg` / `--ec-nav-item-hover-text`（浅色 `#dbeafe` / `#1d4ed8`，深色 `#24395c` / `#bfdbfe`），不再复用 `--ec-surface-hover`；图标为 Phosphor `currentColor`，随文字色一起变化。
- **已知层级差异**：选中态底色 `--ec-brand-050` 与导航底仍只有 1.004:1，靠左侧 3px 品牌竖条、主色文字与加粗识别；hover 底色现在比选中底略深。若需要「选中 > hover」的严格底色层级，需一并调整选中态底色，本次未改。
- `AppIcon.vue` 统一 Phosphor 线性图标；禁止用 emoji 充当结构图标、禁止位图图标。

### 3.3 响应式与多端

四档断点集中在 `variables.css`：≤800 单栏 + 底部导航 + 安全区 + 软键盘适配；801–1199 导航收为 72px 图标栏、右侧面板覆盖式；1200–1439 标准三栏（导航 172 / 列表 300 / 抽屉 288）；≥1440 宽屏放大。Web / Electron（最小 960×640、托盘、单实例）/ Capacitor（Android API 26、edge-to-edge 状态栏在 Web 层补）共用一套 `apps/web` 代码。

### 3.4 无障碍

178 处 `aria-`、79 处 `role`、40 处 `:focus-visible`；`AppDialog` / `DrawerShell` / `MessageActionMenu` / `AccountCard` 具备 Esc、焦点 trap 与归还；`prefers-reduced-motion` 降级；hover 仅在精确指针下生效。

### 3.5 体积阈值

入口共享 chunk gzip **≤200 KB**、全站 JS+CSS gzip **≤520 KB**（2026-11-12 用户裁决，由原 ≤100 / ≤260 KB 翻倍，为动效基座留出空间；硬规则见 `AGENTS.md` §14.6）。截至批次 4 动效落地实测 **92.8 KB / 263.2 KB**（引入 `motion` 后入口 +7.6 KB，其中 `motion/mini` 库体 gzip 5.2 KB；全站 +5.3 KB）。阈值放宽不等于放弃体积纪律：每次引入组件库或新依赖仍须复核并记录 gzip 实测增量。

### 3.6 已具备的交互能力

文本 / 图片 / 文件 / 语音 / 系统消息；撤回与编辑占位；链接识别与安全打开；`@` 提及高亮；引用回复与历史定位；右键 / 长按菜单（回复 / 撤回 / 编辑 / 转发 / 收藏 / 置顶 / 多选）；自定义表情与长按删除；草稿防抖与切会话 flush；未读角标 / 标为未读 / 单聊已读回执；全局搜索与会话内搜索；会话置顶、群置顶消息、免打扰；桌面与 Android 通知、提示音、托盘未读闪烁；首字母头像占位与群头像；空状态、Toast、断线横幅、友好错误文案；Enter 发送 / Shift+Enter 换行 / 输入法组合保护；贴底与「有新消息」浮标；上传进度 / 取消 / 重试、拖拽投放；图片预览缩放；typing 提示。

### 3.7 状态反馈规范（U-09）

共享样式在 `styles/global.css` 的 `.ec-state-*`，**不新增通用状态组件**（`AGENTS.md` §12.3 拒绝提前抽象）：

| 类 | 用途 |
|---|---|
| `.ec-state-text` | 加载 / 空 / 慢的提示文字，统一字号、颜色与行高 |
| `.ec-state-slow` | 慢反馈提示（居中） |
| `.ec-state-retry` | 重试 / 重连按钮，统一尺寸、圆角、hover 与焦点环 |

约定：提示一行内、只说状态与下一步操作，不写原因或实现细节（§14.1）；容器用 `role="status"` / `role="alert"`；慢反馈统一超过 1 秒才出现，避免快速加载时闪烁；失败态必须提供可点击的重试入口。已接入首屏加载慢提示、消息发送失败重发、断线横幅重连与上传队列操作按钮；空态仍由 `EmptyState.vue` 承载。

## 4. 现行约束索引

完整可执行的约束在 `AGENTS.md` §14，本表只做索引：

| 主题 | 约束要点 | 位置 |
|---|---|---|
| 界面文案 | 禁止解释性文档；只留操作标签、输入提示、状态与错误反馈 | §2.13 / §14.1 |
| Token 纪律 | 组件不写死色值与魔法数字；新增 token 必须 grep 校验使用点（写错名会静默失效） | §14.2 |
| 主题层 | 「澄蓝」为默认、深色已实现；设置页显式选择、不做系统自动跟随；新增/修改主题须满足对比度与审计前置（规格见 §9） | §14.2 |
| 图标 | `AppIcon.vue` + Phosphor 线性集；禁止 emoji 作结构图标、禁止位图图标 | §14.2 |
| 媒体消息 | 图片 / 文件 / 语音不套气泡，媒体卡颜色只走 `--ec-media-*` | §14.3 |
| 消息操作菜单 | 鼠标只右键触发；手指 / 触控笔保留长按 | §14.4 |
| 响应式 | 四档断点统一、安全区、三端一套代码 | §14.5 |
| 体积 | ≤200 KB / ≤520 KB gzip | §14.6 |
| UI 文档 | 本文件为唯一现行说明；`docs/deprecated/` 全部废弃，不作为依据 | §14.7 |
| 加载反馈与动效 | 曲线 / 时长 / 动画属性 / reduced-motion / 消息流禁令 / 依赖口径（主线 `motion`） | §14.9 |

## 5. 已知缺口与优化路线

> 本节是摘要。逐项方案、验收标准、分批顺序与约束调整提案见本地计划文档 `docs/ui-optimization-plan.md`（**计划类，不入库**）；本节不替代计划。

2026-11-12 UI 评审结论：功能覆盖已接近成熟 IM，差距集中在**反馈打磨**、**桌面效率**与**看得见的未完成感**，而非缺功能。**其中 P0 已于同日落地，见下节。**

### P0 已落地（2026-11-12）

| 项 | 落地内容 |
|---|---|
| U-01 加载态 | 全局 `.ec-skeleton` 静态骨架（不做循环动画，天然满足 reduced-motion）；`Chat.vue` 消息首屏、`ConversationList.vue` 会话列表、`Contacts.vue` 好友首屏各补骨架，容器加 `aria-busy` 与 `role="status"`；「加载更多」分页按钮文案保留 |
| U-02 动效 | `--ec-transition-*` / `--ec-ease` 用于 `ec-enter`（待发送消息、日期分隔线、置顶条）与 `ec-fade`（会话列表切换）、已读回执颜色过渡；动画只在 `prefers-reduced-motion: no-preference` 下生效；抽屉与 Toast 已由 Naive UI 接管，不重复添加 |
| U-03 术语 | `Chat.vue` / `MemberProfileCard.vue` 与 `docs/web.md` / `docs/mobile.md` / `docs/project-overview.md` 统一「联系人」；`docs/mobile.md` 底栏由「四项」更正为实际五项（含收藏）；`grep -rn "通讯录" apps/web/src` 为空 |
| U-04 token | 回收气泡内边距与移动端圆角、拖拽遮罩、聚焦外圈、日期胶囊底、浮标投影、主题预览渐变、断线横幅橙系、蓝色气泡叠加色等；新增 token 在 `variables.css` 与 `themes/azure.css` 双写；修复 6 个静默失效引用（`--ec-bg-surface`、`--ec-font-size-base`、`--ec-font-size-caption`、`--ec-radius-small`、`--ec-transition-ease`、`--ec-warning-soft`）；审计结果「未定义引用：无」 |

> 校验：`cd apps/web && npm run build`（含 `vue-tsc --noEmit`）通过；入口 chunk gzip **83.81 KB**（≤100）、全站 JS+CSS gzip **255.98 KB**（≤260）。未运行测试（§11.2），未做目视验收（U-05 待授权）。

### P1 进度（2026-11-12）

| 项 | 状态 | 落地内容 |
|---|---|---|
| 全局快捷键 | 已落地 | `App.vue` 挂全局 keydown：Ctrl/Cmd+K 全局搜索、Ctrl/Cmd+N 发起聊天、Alt+↑/↓ 切会话；输入态、IME、已有浮层与预览方向键一律让行，Esc 仍由各组件自行处理 |
| 搜索呈现 | **部分** | 打通 `openGlobal()`（原先已实现但无任何调用点）、补齐全局模式结果计数、内联搜索跳转补 `jumpMessageId` 与全局面板对齐。**剩余**：消息 / 联系人 / 群分类 tab 与独立结果面板（需接好友与会话数据源，属下一批） |
| 外观假设置 | 已落地（方案 A） | 消息文字大小（小 12 / 标准 14 / 大 16px）与会话列表密度（舒适 72 / 紧凑 56px）做成真设置，写入 `html[data-ec-font]` / `data-ec-density` 由 `variables.css` 覆写 token；设置存本机 `localStorage`，深色主题随后已实现（§9） |
| 状态族 | **部分** | 首屏加载超过 1 秒才显示「仍在加载消息…」，避免快速加载时提示闪烁；空态、断线横幅、上传失败重试、发送失败重发沿用既有实现。**剩余**：统一状态组件与文案长度规范 |
| 操作可发现性 | 已落地 | 消息行 hover / focus 露出「⋯」按钮，复用右键菜单的定位与事件链；常驻占位只切透明度、无布局位移，不引入鼠标左键长按，触屏常显 |

> P1 收口校验：`npm run build`（含 `vue-tsc --noEmit`）通过；入口 chunk gzip 85.05 KB（≤100）、全站 JS+CSS gzip 257.29 KB（≤260，余量约 2.7 KB）；token 审计无未定义引用。未运行测试（§11.2），未做目视验收。

### P2 进度

- **深色主题**：**已实现（2026-11-12）**——`AGENTS.md` §14.2 已改为「已实现 + 主题变更前置条件」，设置页开放浅色 / 深色入口，色板与切换链路见 §9。静态对比度（14 组）、token 审计、构建与体积校验已通过；四档宽度 / 缩放 / 真机目视验收仍待按 §11.4 授权执行。
- **字号与消息密度可调**：已随 U-08 方案 A 落地（见 P1 进度表），从 P2 待办中移除。
- **群已读明细 F-20**（`AGENTS.md` §10 唯一 P2 待办）：仍需协议与功能评估，未排期。

### 批次 4 动效已落地（2026-11-12）

| 项 | 落地内容 |
|---|---|
| U-15 曲线与时长 | `variables.css` 新增 `--ec-ease-in`（退出）、`--ec-ease-in-out`（页面内位移）、`--ec-motion-stagger` 60ms、`--ec-motion-stagger-max` 200ms；`ImagePreview` 缩放与 `SendBox` 位移过渡改用 `--ec-ease-in-out`，入场仍用 `--ec-ease` |
| U-16 动画基座 | 新增 `core/motion.ts`：`enter()` / `loop()` 两个入口，曲线与时长读取 CSS token（不另立数值）；`prefers-reduced-motion` 在本层兜底；句柄 `cancel()` 供卸载清理；保留 `ENGINE = 'waapi'` 零依赖降级分支。依赖 `motion` 只走 `motion/mini` 入口（gzip 5.2 KB） |
| U-17 骨架入场 | `Chat` / `ConversationList` / `Contacts` 首屏骨架逐行淡入并上移 8px，行间错峰按元素数压缩到 ≤200ms；真实消息行、会话行、好友行不加 stagger（§14.9 消息流禁令） |
| U-18 三点反馈 | 新增 `LoadingDots.vue`（`role="status"`、卸载取消在途动画），接入「加载更早消息」「文件面板加载更多」「搜索加载更多」；`min-height: 1.5em` 对齐正文行盒，避免「文案 → 三点」时按钮高度跳动；reduced-motion 下静态三点 + 文案 |
| U-19 深色不闪白 | `index.html` 首屏预置画布底色与 `color-scheme`，消除样式表加载前的白帧；主题切换仍只切 `data-ec-theme` 属性，不做全屏淡入淡出 |

> 校验：`npm run build`（含 `vue-tsc --noEmit`）通过；入口 chunk gzip **92.8 KB**、全站 JS+CSS gzip **263.2 KB**（≤200 / ≤520）；token 审计无未定义引用。**未运行测试（§11.2）**；目视 / 多端验收仍按裁决暂缓，待按 §11.4 申请。

### 范围外（不因对标而建议）

音视频通话、E2EE、iPhone 客户端、系统级离线推送、管理员页（`AGENTS.md` §2 / §5 / §10 明确排除或移出）。Reaction 若需要属协议增量，是功能决策而非 UI 债。

## 6. 待办验收清单

`docs/web.md` §7 已有 43 项功能验收；以下**目视 / 多端验收仍未执行**，建议并入 M6 内测：

- [ ] 1440 / 1200 / 900 / 390 四档宽度逐档对照当前构建（旧九图已废弃，需重新截图）
- [ ] 100% / 125% 缩放，检查设置分组卡圆角、行分割线、纵向控件对齐
- [ ] 亮色主题正文对比度 ≥4.5:1，分割线与交互状态可辨
- [ ] Android 安全区（`--ec-safe-top/bottom`）、软键盘弹出、底部导航
- [ ] Toast / 搜索框 / 弹窗的实际渲染，以及 Esc 与焦点归还交互
- [ ] F-39 后台提醒 N01~N12 真机验收
- [ ] 每次 UI 改动后 `cd apps/web && npm run build`（含 `vue-tsc --noEmit`），复核体积阈值
- [ ] 深色主题：静态对比度、token 审计与构建已通过；四档宽度 / 缩放 / Android 安全区与切换后草稿、连接状态的目视验收仍待执行

> 按 `AGENTS.md` §11.2 默认不运行测试；需要执行时按 §11.4 先申请范围与耗时。

## 7. 废弃资产登记

**2026-11-12 全部标记废弃**，清单与原因见 `docs/deprecated/README.md`：

| 归档路径 | 原路径 | 内容 |
|---|---|---|
| `docs/deprecated/ui/` | `docs/ui/` | 旧 `README.md`、`chat-theme-preview.png`、`screens-before/`、`screens-after*/`，155 文件约 11 MB |
| `docs/deprecated/EriseChat_UI_交互验收包_V1.1/` | 同名原路径 | 离线交互工作台、九张参考图、静态 / 交互 HTML、`UI-验收标准.md`，34 文件约 11 MB |

- 归档目录整体由 `.gitignore` 排除，不入库；其中 3 个 markdown 文件头部已加「已废弃」横幅。
- 废弃原因：截图停留在旧界面状态；参考包 V1.1 早期即注明「仅作视觉方向与交互路径参考，组件位置、尺寸、文案、演示数据均不直接采用」。
- **替代关系**：旧 `docs/ui/README.md` → 本文件；`UI-验收标准.md` → `docs/web.md` §7 + `AGENTS.md` §14；改造前后截图 → 不再保留基线，需要时按当前构建重新截图。
- 归档文件不删除、不改写内容（仅追加横幅），保留原始字节供追溯。

## 8. 维护约定

- 每次较大 UI 改动后更新本文件 §2（变更）与 §3（基线），保持「文档 = 当前源码」。
- 约束变更先改 `AGENTS.md` §14，再同步本文件 §4；界面文案规则变更须同时检查 §2.13。
- 新截图、对比图、验收包一律放本地且不入库（`AGENTS.md` §4），不得写回 `docs/ui/`。
- 需要废弃本文件时，同样移入 `docs/deprecated/` 并加废弃横幅，再在此路径新建现行版。

## 9. 深色主题实施规格（文档已解冻，实现待后续）

> **状态（2026-11-12）**：深色主题**已实现**。冻结条款已撤销，`Settings.vue` 开放浅色 / 深色两个可点击入口。本章保留为实施规格与验收依据；静态校验（14 组对比度、token 审计、构建与体积）已通过，四档宽度 / 缩放 / 真机目视验收仍待按 `AGENTS.md` §11.4 授权执行。
> 硬约束见 `AGENTS.md` §14.2；本章是实施规格，不替代硬规则。

### 9.1 主题变更前置条件（实现时已满足静态部分；新增或修改主题仍须逐项复核）

1. **token 审计**：已通过，`apps/web/src` 内 `--ec-*` 定义/引用对比结果为「无未定义引用」；组件内无裸色值（`Avatar.vue` 的 JS 哈希色板与 `core/imageCompress.ts` 的 canvas 填充色属数据资产例外）。
2. **主题文件**：已新增 `styles/themes/dark.css`，用 `html[data-ec-theme='dark']` 覆写 §9.2 列出的全部语义 token；`variables.css` 的 `:root` 保持浅色回退基线。
3. **对比度**：正文与背景 ≥4.5:1、组件边界 ≥3:1；14 组关键色对静态校验全部达标（正文 13.62:1、次要文字 7.87:1、白字气泡 5.17:1、组件边框 3.17:1 等）。弱分割线 `--ec-border-soft` 属装饰性、不承载边界信息，按视觉层次单独判断。
4. **构建与目视**：`npm run build` 已通过、体积阈值未越；四档宽度 + 100%/125% 缩放 + Android 安全区下两套主题的目视验收**仍待按 §11.4 授权执行**。

### 9.2 必须覆写的 token 清单

以 `styles/variables.css` / `styles/themes/azure.css` 的实际定义为准，按类别：

| 类别 | token |
|---|---|
| 品牌与状态 | `--ec-brand-050/100/500/600/700`、`--ec-success`、`--ec-online`、`--ec-danger`、`--ec-warning`、`--ec-warning-border/surface/strong`、`--ec-warning-soft` |
| 文本 | `--ec-text-primary/secondary/tertiary`、`--ec-text-inverse`、`--ec-text-on-self-soft`、`--ec-disabled` |
| 表面与背景 | `--ec-bg-canvas`、`--ec-surface`、`--ec-surface-hover`、`--ec-list-bg`、`--ec-nav-bg`、`--ec-chat-bg-a/b/c/d`、`--ec-chat-glow-a/b`、`--ec-chat-gradient`、`--ec-header-bg`、`--ec-chip-surface` |
| 边框与分隔 | `--ec-border`、`--ec-border-soft`、`--ec-border-strong`、`--ec-bubble-other-border`、`--ec-bubble-self-border` |
| 气泡与媒体 | `--ec-bubble-other`、`--ec-bubble-self`、`--ec-bubble-self-bg`、`--ec-media-surface/hover`、`--ec-media-border`、`--ec-media-icon-surface`、`--ec-media-track`、`--ec-bubble-self-overlay`、`--ec-bubble-self-overlay-soft` |
| 阴影与遮罩 | `--ec-shadow-*` 全组、`--ec-shadow-bubble/self`、`--ec-shadow-media`、`--ec-shadow-badge`、`--ec-composer-shadow`、`--ec-scrim`、`--ec-overlay`、`--ec-overlay-border`、`--ec-overlay-hover` |
| 焦点与选中 | `--ec-focus-ring`、`--ec-focus-shadow`、`--ec-row-active-bg`、`--ec-drop-overlay`、`--ec-date-badge-bg` |
| 预览示意 | `--ec-theme-preview-light/dark`（设置页色卡，两套主题下都要保持可辨） |

> 新增 token 必须同时写入 `variables.css`（回退基线）与 `themes/dark.css`（主题覆写），并 grep 校验使用点——CSS 自定义属性写错名会静默失效。尺寸 / 间距 / 圆角 / 动效类 token 不需要按主题覆写。

### 9.3 主题切换机制

- `core/theme.ts` 由静态 `ecThemeOverrides` 改为按当前主题返回 Naive UI `themeOverrides`；`App.vue` 用 computed 传给 `NConfigProvider`，避免组件库控件在深色下仍是浅色。
- `index.html` 的静态 `data-ec-theme="azure"` 改为运行时驱动：在 `stores/settings.ts` 增加 `theme: 'azure' | 'dark'`（枚举白名单校验、本机 localStorage、不同步服务端），`App.vue` watch 写 `document.documentElement.dataset.ecTheme`；`main.ts` 同时引入两套主题 CSS。
- 参考 U-08 已落地的 `html[data-ec-font]` / `data-ec-density` 覆写方式，保持「组件不感知具体取值」。
- **不做**系统主题自动跟随（避免引入 `matchMedia` 监听与首屏闪烁），仅提供显式选择；若后续要做须单独评估。

### 9.4 设置页入口规格

- 「主题模式」保留浅色与深色两张卡；移除深色卡上的「即将上线」灰显与 `aria-disabled`，改为可点击、可键盘聚焦，选中态沿用现有 `check` 图标与 `role="status"`。
- 界面文案按 §14.1 只留操作标签「浅色」「深色」，不写原理或说明；说明放 `docs/`。
- 切换后立即生效（无需刷新），且不影响已加载消息、输入草稿与连接状态。
- 入口放开必须与 §9.1 前置条件、§9.5 验收**同批代码改动**一起落地，不允许先给可点入口再补主题。

### 9.5 验收清单

- [x] 两套主题下 `--ec-*` 定义/引用审计均无未定义引用，无组件裸色值
- [x] 正文对比度 ≥4.5:1；组件边界 ≥3:1（14 组静态校验全部达标）
- [ ] 媒体卡、日期胶囊、半透明顶栏、图片预览遮罩、断线横幅、主题预览色卡在深色下不糊成一片（待目视）
- [ ] Naive UI 控件（弹窗、开关、输入框、消息提示）随主题切换（代码已接 `ecThemeOverridesFor`，待目视）
- [ ] 会话列表、聊天页、设置页、登录页、收藏页在 1440/1200/900/390 四档与 100%/125% 缩放下无错位（待目视）
- [ ] Android 安全区与状态栏、软键盘弹出在深色下正常（待真机）
- [x] `npm run build` 通过；入口 85.23 KB、全站 257.92 KB gzip，均在阈值内
- [ ] 切换主题不丢草稿、不断线、不改变已读游标（待交互验收）

### 9.6 风险与回滚

- **CSS 变量静默失效**：深色下某个 token 漏覆写不会报错，只会沿用浅色值 → 逐 token 对照 + grep 校验。
- **半透明与叠加色**：`--ec-header-bg`、`--ec-chip-surface`、`--ec-bubble-self-overlay*` 在深色底上可能失去层次 → 单独目视。
- **Naive UI 主题 overrides**：组件库控件未跟随会形成「局部浅色」→ 优先修 `core/theme.ts`。
- **体积**：新增一套主题 CSS 与设置字段后复核阈值。
- **回滚**：改动只在 `apps/web` 前端样式与设置层，无协议帧、库表、服务端改动；按提交粒度 revert 即可，不影响用户数据。
