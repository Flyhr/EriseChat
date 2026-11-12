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
| P0 优化落地 | 2026-11-12（本轮，未提交） | 加载态骨架 + `aria-busy`、动效 token 落地、术语统一「联系人」、硬编码色值回收与 token 审计（详见 §5） |

## 3. 当前 UI 基线

### 3.1 页面与组件

- **页面**：`Chat.vue`（三栏核心）、`Login.vue`（注册 / 验证码 / 密码 / 找回密码 4 模式 + 壳内服务器地址）、`Contacts.vue` + `ContactProfile.vue`、`Settings.vue`（分区设置）、`Favorites.vue`；`Admin.vue` / `Download.vue` 为占位。
- **会话与聊天**：`ConversationList/Item`、`ChatHeader`、`MessageList/Bubble`、`SendBox`、`MessageAttachment`、`VoiceMessagePlayer`、`MessageActionMenu`、`EmojiPanel`、`ForwardDialog`、`ImagePreview`、`EmptyState`、`UnreadBadge`、`PresenceDot`、`Avatar`。
- **全局壳**：`SideNav`、`MobileTabBar`、`ConnectionBanner`、`ToastBridge`、`AccountCard`。
- **群与搜索**：`NewGroupDialog`、`GroupMembersPanel`、`MemberProfileCard`、`ConversationFilesPanel`、`SearchDialog`、`SearchInput`、`SearchResultItem`。

### 3.2 设计 token 与主题

- `styles/variables.css` 集中全部 `--ec-*`：品牌色（`#2563eb`）、聊天区三段渐变与光晕、文本三级对比、边框、媒体卡 `--ec-media-*`、Inter / YaHei 字体栈、4px 基数间距 `--ec-space-1..8`、圆角（气泡 14px + 尾角 6px）、阴影、`--ec-transition-fast/base/slow`（120 / 160 / 200ms）、四档布局宽、`--ec-safe-top/bottom`；文件末尾保留旧 `--color-*` 兼容别名。
- U-04 后新增语义色与尺寸 token（聚焦外圈、拖拽遮罩、日期胶囊底、浮标投影、主题预览渐变、断线横幅橙系、蓝色气泡叠加色、气泡内边距与移动端圆角、命中高亮底、半透明胶囊底、`--ec-vvh` 回退基线），颜色类在 `styles/themes/azure.css` 双写；组件内不再写裸色值（`Avatar.vue` 的 JS 哈希色板与 `core/imageCompress.ts` 的 canvas 填充色属数据资产例外）。
- `styles/themes/azure.css` + `data-ec-theme="azure"` 提供「澄蓝」浅色主题，`core/theme.ts` 映射 Naive UI `themeOverrides`。
- **深色主题仍冻结**：`Settings.vue:1068-1074` 灰显「即将上线」，主题层只做结构预留、设置页不提供可点击主题入口（UI-D 用户裁决，见 §4）。
- `AppIcon.vue` 统一 Phosphor 线性图标；禁止用 emoji 充当结构图标、禁止位图图标。

### 3.3 响应式与多端

四档断点集中在 `variables.css`：≤800 单栏 + 底部导航 + 安全区 + 软键盘适配；801–1199 导航收为 72px 图标栏、右侧面板覆盖式；1200–1439 标准三栏（导航 172 / 列表 300 / 抽屉 288）；≥1440 宽屏放大。Web / Electron（最小 960×640、托盘、单实例）/ Capacitor（Android API 26、edge-to-edge 状态栏在 Web 层补）共用一套 `apps/web` 代码。

### 3.4 无障碍

178 处 `aria-`、79 处 `role`、40 处 `:focus-visible`；`AppDialog` / `DrawerShell` / `MessageActionMenu` / `AccountCard` 具备 Esc、焦点 trap 与归还；`prefers-reduced-motion` 降级；hover 仅在精确指针下生效。

### 3.5 体积阈值

入口共享 chunk gzip ≤100 KB、全站 JS+CSS gzip ≤260 KB；UI-D 与后续批次实测区间约 81.0~87.4 KB / 244~257 KB。每次引入组件库或新依赖后须复核阈值。

### 3.6 已具备的交互能力

文本 / 图片 / 文件 / 语音 / 系统消息；撤回与编辑占位；链接识别与安全打开；`@` 提及高亮；引用回复与历史定位；右键 / 长按菜单（回复 / 撤回 / 编辑 / 转发 / 收藏 / 置顶 / 多选）；自定义表情与长按删除；草稿防抖与切会话 flush；未读角标 / 标为未读 / 单聊已读回执；全局搜索与会话内搜索；会话置顶、群置顶消息、免打扰；桌面与 Android 通知、提示音、托盘未读闪烁；首字母头像占位与群头像；空状态、Toast、断线横幅、友好错误文案；Enter 发送 / Shift+Enter 换行 / 输入法组合保护；贴底与「有新消息」浮标；上传进度 / 取消 / 重试、拖拽投放；图片预览缩放；typing 提示。

## 4. 现行约束索引

完整可执行的约束在 `AGENTS.md` §14，本表只做索引：

| 主题 | 约束要点 | 位置 |
|---|---|---|
| 界面文案 | 禁止解释性文档；只留操作标签、输入提示、状态与错误反馈 | §2.13 / §14.1 |
| Token 纪律 | 组件不写死色值与魔法数字；新增 token 必须 grep 校验使用点（写错名会静默失效） | §14.2 |
| 主题层 | 「澄蓝」为默认；深色主题冻结，设置页不提供可点击主题入口 | §14.2 |
| 图标 | `AppIcon.vue` + Phosphor 线性集；禁止 emoji 作结构图标、禁止位图图标 | §14.2 |
| 媒体消息 | 图片 / 文件 / 语音不套气泡，媒体卡颜色只走 `--ec-media-*` | §14.3 |
| 消息操作菜单 | 鼠标只右键触发；手指 / 触控笔保留长按 | §14.4 |
| 响应式 | 四档断点统一、安全区、三端一套代码 | §14.5 |
| 体积 | ≤100 KB / ≤260 KB gzip | §14.6 |
| UI 文档 | 本文件为唯一现行说明；`docs/deprecated/` 全部废弃，不作为依据 | §14.7 |

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

### P1 需排期

- **桌面效率**：无全局快捷键（`keydown` 只用于弹窗 Esc、图片预览、`@` 面板），建议 Ctrl+K 搜索、Ctrl+N 发起聊天、↑↓ 切会话。既然交付 Windows 安装包，这是明显短板。
- **搜索呈现**：结果塞进会话列表，无消息 / 联系人 / 群分类、无时间筛选、无结果计数，右侧仍是空状态 → 独立面板 + 分类 + 跳回原文。
- **未完成感**：`Settings.vue:1068-1074` 深色「即将上线」灰显；`:1077-1100` 三张只读外观信息卡（14px / 舒适 72px / 柔和蓝白渐变）长得像设置却点不动 → 做成真设置，或降级为普通说明文字。
- **状态族**：已有 `EmptyState` / `friendlyError` / `ConnectionBanner`，但「慢 / 重连中 / 发送失败重试」视觉层级偏弱，缺统一的重试与进度表达。
- **可发现性**：撤回 / 编辑 / 转发 / 收藏 / 多选 / 置顶全藏在右键菜单，鼠标用户无任何提示 → hover 时露一个极轻的「⋯」按钮（不引入左键长按）。

### P2 需裁决

- **深色主题解冻**：先完成 P0 的 token 审计 + 双主题对比度校验，再由用户裁决是否撤销 UI-D 冻结。
- **字号与消息密度可调**、**群已读明细 F-20**（`AGENTS.md` §10 唯一 P2 待办）。

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
