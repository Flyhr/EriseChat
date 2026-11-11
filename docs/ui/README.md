# EriseChat UI 参考资产

本目录存放 UI 改造的参考图与改造前后基线截图。**除本 README 外，本目录资产均为本地过程文件、不入库**（AGENTS.md §2.11/§4）。

| 目录/文件 | 用途 |
|---|---|
| `screens-before/` | 改造前基线截图（UI-00 产出，桌面/移动各视角） |
| `screens-after*/` | 改造后各批次对比截图（UI-02~UI-05、聊天主题） |
| 参考图（本目录或子目录） | 客户提供的视觉参考图（桌面/移动各自命名） |
| `chat-theme-preview.png` | 聊天主题「澄蓝」落地效果预览 |

约定：

- 文件命名：`<序号>-<页面>-<端>.png`，例如 `01-聊天-桌面.png`、`02-会话列表-移动.png`；
- 截图不得包含真实账号、token、验证码、服务器密钥、内网地址等敏感信息（用测试账号与脱敏数据）；
- 参考图与截图仅作视觉方向与验收对照，不以像素级复制为目标。

## 交互验收参考包

- 位置：`../EriseChat_UI_交互验收包_V1.1/`（离线交互工作台 + 9 张参考图 + 9 页静态/交互 HTML + `UI-验收标准.md`，约 11MB，本地不入库）。
- 定位：**仅作视觉方向与交互路径参考**；组件位置、尺寸、文案、演示数据均不直接采用，具体实现以当时生效的 UI 专项计划为准。
- 说明：UI 改造专项计划（`ui-upgrade-plan.md`）与 UI-00 审查报告、token 映射表等已在 2026-10-26 执行完成后清理，仅保留本目录截图与参考包作为追溯资产。

## UI-D 批次（2026-11-11 起）

按用户裁决补回参考图丢失的信息密度与组件细节，保留「澄蓝」蓝白主题为默认主题方案；主题层只做结构预留，设置页不提供可点击主题入口。分批执行，截图沿用本目录既有命名约定（`<序号>-<页面>-<端>.png`），不新建子目录。

- 主题层：`apps/web/src/styles/themes/azure.css` + `index.html` 的 `data-ec-theme="azure"`；`core/theme.ts` 把色板映射为 Naive UI `themeOverrides`。
- 组件库：`naive-ui` + `@phosphor-icons/vue` + `@vueuse/core`（均 MIT）渐进引入，后续逐步替换全部自绘组件；首个替换组件为 `AppIcon.vue`。
- 已完成：P0 依赖与兼容性验证、P1 主题层与图标基座、P2-A 导航/会话列表/会话行/顶栏/断线条、P2-B 消息气泡（14px 圆角与尾角、时间/已读内嵌、连续消息分组）与紧凑输入区、断线待发送提示、P3 外观设置信息卡、群成员分区标题、表情面板搜索、移动端欢迎语、空状态与 ≥1200 桌面外层浮起卡片。
- P4 已完成部分：`SettingsSwitch` → `NSwitch`、`AppDialog` → `NModal`，均保持原 props / emit / 默认插槽与焦点归还逻辑，调用方零改动。
- 体积阈值（P4 确立）：首屏共享 chunk gzip ≤100 KB（实测 63.2 KB）、全站 JS+CSS gzip ≤260 KB（实测 228.6 KB）。`NModal` 带来的 `AppDialog` 共享 chunk gzip 37.2 KB 为一次性组件库基座成本，后续组件替换复用该 chunk。
- P4 续批（组件替换收口）：`AppToast` → `NMessageProvider` + `ToastBridge`（`core/toast.ts` 改为 handler 桥接，Provider 就绪前缓冲、就绪后 flush，全部 `showToast` 调用方零改动；`AppToast.vue` 按 §2.9 保留文件但停用）、`SearchInput` → `NInput`（`:deep()` 保留胶囊外观）、`DrawerShell` → `NDrawer` / `NDrawerContent`（保留 open/title/close 契约，当前 0 个使用点）。
- 体积实测（续批后）：首屏共享 chunk gzip **81.0 KB**（阈值 ≤100 KB）、全站 JS+CSS gzip **244.4 KB**（阈值 ≤260 KB），余量 15.6 KB；后续再引入组件需复核阈值。
- `SettingsGroup` / `SettingsRow` 已按用户裁决（2026-11-11）彻底替换：`SettingsGroup` 的卡片容器改用 `NCard`（`:bordered=false` + `content-style` 清零内边距，scoped 规则把表面色/边框/圆角/阴影锚回 `--ec-*` 并复位组件库默认 font/color/word-break）；`SettingsRow` 主布局改用 `NFlex`（承担 `flex-direction`/`justify`/`align`/`gap`/`wrap`，`size` 数值对应 `--ec-space-3`=12 / `--ec-space-2`=8）。**外层语义节点与 `.ec-set-row` 类名必须保留**：`clickable` 时需 `<button>` 保证键盘可达，而 `NFlex` 恒渲染 `div[role=none]`；页面级 `:deep(.ec-set-row + .ec-set-row)` 也依赖该类名画行分割线。页面级分割线与 `.settings-themes + .ec-set-row` 选择器零改动，调用方 `Settings.vue` 零改动。
- 人工验收待执行：九图逐图截图对照、1440/1200/900/390 四档宽度、100%/125% 缩放、Android 安全区；本轮未做浏览器/真机验收。
- 文档边界：主题层文件路径只记在本文件与 `docs/internal-plan.md` 附录 D；`docs/web.md` 保持用户既有改动原样，未纳入本专项提交。
- 联系人两级结构与登录三模式核对后确认已具备参考图要求，本轮未改动；移动端底部导航保留现有 5 项入口（参考图为 3 项，删减会丢失已上线功能）。
- 视觉修正（背景缝隙，2026-11-11）：用户截图反馈顶栏下方有一条浅色缝隙。根因是 `.chat-page__typing`（为防列表跳动而常驻的 18px 预留区）排在 `.message-list` 之前，而渐变只挂在 `.message-list` 上。修复为把 `--ec-chat-gradient` 上移到共同容器 `.chat-page__chat`、`.message-list` 改透明；**顶栏 `ChatHeader` 与输入卡片 `SendBox` 保持不变**（区别于已回退的 `65518d4`）。
- 既有失败测试收口（2026-11-11）：`stores/settings.test.ts`（3 例）与 `core/messageActions.test.ts`（3 例）并非源码回归，而是 F-39（`androidBackgroundNotificationsEnabled`）与 F-40（多选 `select`、附件 `forward`）上线后测试期望未同步。仅更新 6 处期望值与 2 处用例名以匹配现行行为，**未改任何源码**；2 文件 25 例全部通过。`core/notify.test.ts` 使用独立 `IncomingNotifyState` 夹具，不受影响。复核：`vue-tsc`/`vite build` 通过，首屏共享 chunk gzip 81.0 KB（≤100）、全站 JS+CSS 244.3 KB（≤260）。
- 静态核验（2026-11-11，非目视验收）：响应式断点覆盖四档目标宽度（1440/1200→`≥1200` 与 `≤1199`、`900`→`801~1199`、`390`→`≤800`+`≤480`）；`ToastBridge` 三态 `useMessage` 注册与卸载清理、`SearchInput` 的 `update:value`/`focusin`/`keydown.esc`、`AppDialog` 的 Esc 与 `previousFocus` 焦点归还均在代码实现。**九图逐图对照、100%/125% 缩放、Android 安全区及 toast/搜索框/弹窗的实际渲染与交互仍待浏览器/真机目视验收**（按 §11.4 启动前需取得同意）。
- 组件替换收口（2026-11-11）：用户裁决「彻底替换」两个布局容器后，`vue-tsc`/`vite build` 通过，最小回归 4 文件 52 例通过；首屏共享 chunk gzip 81.0 KB（≤100）、全站 JS+CSS 245.0 KB（≤260，替换仅 +0.7 KB）、dist 893 KB。人工验收清单按用户裁决以对话交付、不落文件、不启动服务；重点复核设置页分组卡圆角/边框/行分割线、纵向行控件顶部对齐、以及 `NCard` 是否引入额外内边距。
