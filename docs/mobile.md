# EriseChat Android 安装与联调说明（M5）

> 本文是 Android 客户端（Capacitor 壳 + `apps/web` 构建产物）的安装、配置、构建、签名与真机联调说明。
> 协议与表结构见 `protocol/protocol.md`、`protocol/schema.sql`；服务器部署见 `docs/deploy.md`；项目介绍以 `README.md` 为准。
> 本文最近更新：2026-11-11（同步 1.2.0 命名统一后的产物、签名与版本基线）。

## 1. 交付物

| 文件 | 说明 |
|---|---|
| `EriseChat-1.2.0.apk` | Android 安装包（自签名，侧载安装，不上架应用商店） |
| 下载地址 | 服务端 `GET /download` 下载页（`data/downloads/` 放安装包） |

- 当前产物（1.2.0 命名统一后重打，2026-11-10）：`data/downloads/EriseChat-1.2.0.apk`（3,792,395 B，SHA256 `ada9eb963b26157cb3a47f5bc1025928d846ada013d7d25f76041e105ad528cb`），versionCode 10200 / versionName 1.2.0，包名 `com.erisechat.mobile`，v2 自签名校验通过；`/download` 页面显示大小与下载链接。**每次重新打包后 SHA256 会变化，以打包脚本输出为准**。历史基线（1.1.0 / 1.0.0）仍保留在 `data/downloads/`，但旧名前缀不再被 `GET /download` 白名单识别、不作为回滚路径
- 历史基线（仅留存，不作回滚路径）：1.0.0 APK（3,301,201 B，versionCode 10000 / versionName 1.0.0）仍保留在 `data/downloads/`，文件名沿用发布时的旧前缀；1.2.0 起 `GET /download` 白名单只识别 `EriseChat-` 前缀，旧名产物不再出现在下载页（见 `docs/deploy.md` §7.3 / `docs/update-deploy.md` §11）
- 包名：`com.erisechat.mobile`（与 Windows 壳 `com.erisechat.desktop` 区分）
- 版本：`versionName` 为 `1.2.0`，`versionCode` 规则 `major*10000 + minor*100 + patch`（1.0.0 → 10000，1.2.0 → 10200）
- 最低系统：Android 8.0（API 26）；目标/编译 API 36
- 自签名安装包在部分手机上会提示「未知来源/风险应用」，需用户确认后安装

## 2. 安装与首次登录

1. 手机允许浏览器/文件管理器「安装未知应用」（设置 → 安全 → 安装未知应用）。
2. 把 `EriseChat-1.2.0.apk` 传到手机并安装（USB 数据线、局域网下载页、文件管理器三种方式见 §5.5）。
3. 打开 EriseChat；打包态登录页会显示「服务器地址」区块（未配置时自动展开），填写后点「保存并检测」：
   - 默认（USB 联调，配合 §5.5 的 adb reverse）：`http://127.0.0.1:6580`
   - 登录页「服务器地址」上方有「快速选择」下拉：内置「服务器（Tailscale HTTPS）」与「本机调试（USB 端口映射）」两项，选中后自动保存并检测，便于本地调试与服务器之间快速切换；选「自定义地址…」时仍可手动填写。预设列表可用构建期变量 `VITE_SERVER_PRESETS` 覆盖（格式见 `apps/web/.env.example`）。
   - 手机与开发机同一局域网：`http://<开发机局域网 IP>:6580`（不能用 `127.0.0.1`，那指向手机自身）
   - 互联网部署：`https://<域名>`（Caddy 自动 HTTPS，见 `docs/deploy.md`）
4. 新账号先切「注册」→「发送验证码」（开发环境固定测试码 `123456`，生产环境走真实邮件）→ 填昵称/验证码注册并登录。
5. 登录后可搜索用户发起单聊/建群、收发文本、图片、语音与文件消息；断线重连后自动补拉消息。
6. 需要应用在后台时也能收到新消息通知时，进入「设置 → 消息提醒 → 后台消息提醒」开启：首次会请求系统通知权限，授权后应用退到后台由前台服务保持一条连接，收到新消息时在通知栏提示，点击通知进入对应会话。

### 2.1 升级到 1.2.0

1.2.0 起 `applicationId` 统一为 EriseChat，系统视为新应用：**升级前需卸载旧版后再安装新版**，原来的 `adb install -r` 覆盖安装不再复用旧应用数据。服务端聊天数据不丢失；本机设置、草稿、记住密码与本地缓存会清空，首次启动需重新填写服务器地址并登录。JWT issuer/audience 同步变更，1.2.0 上线后所有用户需重新登录。

## 3. 网络与明文 HTTP（内网联调）

- `capacitor.config.ts` 设置 `server.androidScheme = 'http'`：页面 origin 为 `http://localhost`，避免 https 页面调用 http API 被 WebView 判定为混合内容。
- `AndroidManifest.xml` 开启 `android:usesCleartextTraffic="true"`，仅用于**内网联调**（http 服务器）。
- 生产环境建议使用 https 域名并在后续版本收紧明文策略；服务端数据仍以服务端为唯一事实源。

### 3.1 后台消息提醒（Android）

- 开启方式：设置 → 消息提醒 → 后台消息提醒；首次开启需允许系统通知权限（Android 13+ 为运行时权限）。关闭后常驻通知消失、原生连接断开。
- 验收状态（2026-11-09）：代码、编译与发行 APK 构建已通过；首轮真机验收**尚未执行**，目标设备为 OPPO（ColorOS 16.0.10 / Android 16），场景见 §5.7。构建成功不代表通知已在真机验证。
- 实现：原生前台服务（`ChatSocketService`，`foregroundServiceType=dataSync`）使用当前登录令牌连接 `ws`，收到 `message.new` 时发系统通知；应用在前台时由页面内提醒负责，不重复通知；免打扰会话不通知；自己发送的消息不通知。
- 连接占用服务端「每账号最多 3 条连接」中的一条；同时开启前台服务与 Web 页面时共占用 2 条，其余设备仍有 1 条可用。
- 点击通知会打开 EriseChat 并进入对应会话：冷热启动统一由原生缓存点击，Web 只消费一次，待登录与路由就绪后跳转。通知携带来源账号与服务器，来源不完整、账号或服务器已切换时直接丢弃；未登录时保留缓存，登录成功后再打开对应会话。

#### 3.1.1 平台限制

- Android 12 及以上不允许应用从后台启动前台服务，因此该能力首次开启必须由用户在前台操作（设置页）；已启动的服务在切后台后继续运行。
- Android 15 及以上对 `dataSync` 前台服务限制为每 24 小时累计最多 6 小时，超时后系统停止服务；重新回到应用前台并再次开启即可继续（页面不会自动重启服务）。
- 系统重启后不会自动恢复后台提醒（Android 15+ 不允许 `dataSync` 从 `BOOT_COMPLETED` 启动）；需要重新打开应用并在设置中开启。
- 省电模式、系统「深度休眠」或厂商清理策略可能延迟/拦截通知；应用被强制停止后不再接收，直到用户重新打开应用。
- 通知样式与优先级由系统通知渠道控制；关闭系统通知权限后应用内开关仍会显示开启，但系统不会展示通知。

## 4. 已知限制（v1）

- **后台提醒不是系统级离线推送**：需在「设置 → 消息提醒 → 后台消息提醒」主动开启（见 §3.1），由前台服务保持连接；系统重启后需重新开启，Android 15+ `dataSync` 单日 6 小时限制、强制停止/深度休眠/厂商清理下可能延迟或收不到通知；不接厂商推送、不做短信/电话提醒。
- 不做 iOS；不做应用商店上架；不做自动更新。
- 覆盖升级必须使用**同一个 keystore** 签名，否则系统拒绝安装（只能先卸载，聊天数据在服务端不受影响）。
- 应用内缓存（IndexedDB）仅作展示缓存，卸载即清空；服务端数据不受影响。
- 语音消息：首次录音时系统会请求麦克风权限（RECORD_AUDIO），拒绝后需到系统设置手动开启；录音依赖 WebView 的 `MediaRecorder`，环境声音过小或权限被拒时会提示且不发送。

## 5. 构建与签名（开发机）

### 5.1 环境

- JDK 21（推荐）：`JAVA_HOME` 指向 JDK 目录（本机为 `D:/softWare/jdk21`，Temurin 21.0.12.1）
- Android SDK：platforms `android-36`、build-tools `36.0.0`、platform-tools（Gradle 构建可能自动安装 build-tools 35）；本机原 `C:/Program Files (x86)/Android/android-sdk` 只读，复制到可写目录 `D:/softWare/android-sdk` 并接受许可
- `apps/mobile/android/local.properties`（不入库）：
  `sdk.dir=D:/softWare/android-sdk`（本机；按实际 SDK 路径填写）
- 首次构建会下载 Gradle 与 AGP 依赖（数分钟到十几分钟，缓存于 `~/.gradle`）；本机 `gradle-wrapper.properties` 使用腾讯镜像，网络受限环境可同样处理

### 5.2 keystore（禁止入库，必须离线备份）

```bash
mkdir -p infra/keys
"$JAVA_HOME/bin/keytool" -genkeypair -v \
  -keystore infra/keys/eisechat-release.jks -alias eisechat \
  -keyalg RSA -keysize 2048 -validity 10000 \
  -storepass '<随机密码>' -keypass '<随机密码>' \
  -dname "CN=EriseChat, OU=Internal, O=EriseChat, L=Local, ST=Local, C=CN"
```

`infra/keys/keystore.properties`（不入库）：

```properties
storeFile=../../../infra/keys/eisechat-release.jks
storePassword=<与上面一致>
keyAlias=eisechat
keyPassword=<与上面一致>
```

> `infra/keys/` 已在 `.gitignore` 中忽略。**务必离线备份 keystore 与密码**：丢失后无法对已安装用户做覆盖升级（只能卸载重装）。

### 5.3 打包（Windows 开发机）

> **必须用 Git Bash 执行**。本机 Git 装在 `D:\Git`，Git Bash 为 `D:\Git\bin\bash.exe`。
> 在 Windows **cmd** 里直接敲 `bash` 调用的是 WSL 转接器（`C:\Windows\System32\bash.exe`）；本机 WSL 未装 Linux 发行版，会报
> `WSL (10 - Relay) ERROR: CreateProcessCommon:817: execvpe(/bin/bash) failed: No such file or directory` —— 这不是脚本问题，换 Git Bash 即可。

方式一：开始菜单打开「Git Bash」，然后：

```bash
cd /d/Code/EriseChat
bash infra/build-apk.sh 1.2.0
```

方式二：在 cmd 中显式调用 Git Bash：

```bat
cd /d D:\Code\EriseChat
"D:\Git\bin\bash.exe" infra/build-apk.sh 1.2.0
```

脚本流程：`apps/web` 构建（含 UI 最新代码）→ `npx cap sync android` → `./gradlew assembleRelease` → 复制为
`data/downloads/EriseChat-1.2.0.apk` 并打印 SHA256。产物 `apps/mobile/android/app/build/`、`data/downloads/*.apk` 均不入库。

- 首次构建需下载 Gradle/AGP 依赖（数分钟到十几分钟，缓存于 `~/.gradle`）；之后增量打包约 **1~2 分钟**。
- 成功标志：`BUILD SUCCESSFUL in ...`，随后打印 `APK: .../data/downloads/EriseChat-1.2.0.apk`、文件大小与 SHA256。
- 脚本默认 `JAVA_HOME=D:/softWare/jdk21`（可用环境变量覆盖），SDK 路径读 `apps/mobile/android/local.properties`。
- 改完 `apps/web` 后**必须重新打包**（`cap sync` 只是把 `apps/web/dist` 拷进 Android 工程，APK 内是打包时的 Web 产物）。

版本号规则与三端（服务端 `/api/health`、Windows 安装包、APK）保持一致；发版时同时修改
`apps/desktop/package.json`、`apps/web/package.json`、`apps/mobile/android/app/build.gradle`（`versionCode` 按
`major*10000 + minor*100 + patch` 递增，例如 1.2.0 → 10200，否则覆盖安装会被系统拒绝）。

### 5.4 静态校验（可选）

校验前先设置 `JAVA_HOME`（`apksigner.bat` 依赖 java，默认 `D:/softWare/jdk21`）：

```bash
export JAVA_HOME=D:/softWare/jdk21
SDK=D:/softWare/android-sdk
"$SDK/build-tools/36.0.0/aapt2.exe" dump badging data/downloads/EriseChat-1.2.0.apk | head
"$SDK/build-tools/36.0.0/apksigner.bat" verify --verbose data/downloads/EriseChat-1.2.0.apk
unzip -l data/downloads/EriseChat-1.2.0.apk | grep assets/public/index.html
```

校验基线（1.2.0）：`package=com.erisechat.mobile`、`versionCode=10200`、`versionName=1.2.0`、`minSdkVersion=26`、`targetSdkVersion=36`、`application-label='EriseChat'`、`INTERNET` 权限；签名 `Verifies` 且 v2 scheme=true（v1 未启用，minSdk 26 无需要），证书 SHA-256 `2a5eef2be5a636ecfb021d1ad384a28d1ff48c365b678fd1435085a5adedbe3a`（沿用同一 keystore，与历史版本一致）。1.2.0 起因 `applicationId` 变更，跨版本升级需卸载旧版后安装，见 §2.1。

### 5.5 安装到手机（三种方式）

前置：手机「设置 → 关于手机 → 版本号」连点 7 次开启开发者选项 →「开发者选项」打开 **USB 调试**；自签名 APK 首次安装需允许「安装未知应用」。

**A. USB + adb（开发联调最推荐，无需传文件）**

```bat
D:\softWare\android-sdk\platform-tools\adb.exe devices
D:\softWare\android-sdk\platform-tools\adb.exe install -r D:\Code\EriseChat\data\downloads\EriseChat-1.2.0.apk
D:\softWare\android-sdk\platform-tools\adb.exe shell dumpsys package com.erisechat.mobile | findstr "versionName versionCode"
```

`devices` 应显示一行 `device`（显示 `unauthorized` 时在手机上点「允许 USB 调试」）。

看到 `Success` 即安装完成；`versionName=1.2.0`、`versionCode=10200` 为当前包。同一 1.2.0 版本内 `adb install -r` 覆盖安装会保留应用数据目录与 IndexedDB 缓存，聊天数据始终以服务端为准；**从 1.1.0 及更早版本升级时，因 `applicationId` 变更需先卸载旧版再安装，属预期行为，见 §2.1**（卸载会清本机应用数据，服务端聊天数据不受影响）。

**B. adb push 到手机，手动点安装（适合把 APK 留在手机上）**

```bat
D:\softWare\android-sdk\platform-tools\adb.exe push D:\Code\EriseChat\data\downloads\EriseChat-1.2.0.apk /sdcard/Download/
```

然后在手机「文件管理 → Download」中点开 `EriseChat-1.2.0.apk` 安装。

**C. 局域网下载页（手机与电脑同一 Wi-Fi）**

```bat
cd /d D:\Code\EriseChat\server\Chat.Server
dotnet run --urls http://0.0.0.0:6580
```

开发模式默认只监听 `127.0.0.1`（`appsettings.json` 的 `Kestrel:Endpoints:Http:Url`），
手机访问不到。注意：`dotnet run --urls ...` 会被 `appsettings.json` 里的端点配置覆盖，改用环境变量：

```bat
set Kestrel__Endpoints__Http__Url=http://0.0.0.0:6580
dotnet run
```

（Git Bash：`Kestrel__Endpoints__Http__Url=http://0.0.0.0:6580 dotnet run --project server/Chat.Server`。）
启动后用 `netstat -ano | findstr :6580` 应看到 `0.0.0.0:6580` 而不是 `127.0.0.1:6580`。

另开一个终端查看本机 IP：

```bat
ipconfig
```

记下「IPv4 地址」一行，例如 `192.168.1.23`。

- Windows 防火墙需放行 6580（首次运行会弹窗，选「允许」；或手动在「Windows Defender 防火墙 → 允许应用通过防火墙」放行 dotnet）。
- 手机浏览器打开 `http://<电脑IPv4>:6580/download` → 下载 APK → 安装。
- 从手机浏览器点 APK 安装通常比文件管理器多一步「仍然安装」确认。

**D. 其他传输方式**：微信/QQ「文件传输助手」传 APK 后点开安装亦可；只需保证 APK 完整（对比 SHA256）。

安装后核对：桌面出现 EriseChat 图标；`设置 → 应用 → EriseChat → 版本 1.2.0`。从 1.1.0 及更早版本升级需先卸载旧版（见 §2.1）；若提示「应用签名不一致」，说明 keystore 变了，只能先卸载再装（服务端聊天数据不受影响）。

### 5.6 本机联调（服务端 + 手机，USB 或局域网）

1. **启动服务端**（开发模式，监听 6580，固定验证码 `123456`）：

   ```bat
   cd /d D:\Code\EriseChat\server\Chat.Server
   dotnet run
   ```

   另开一个终端确认：`curl http://127.0.0.1:6580/api/health` 返回 `{"status":"ok",...,"db":"ok"}`。

2. **USB 联调（推荐）**：执行一次端口反向映射，让手机的 `127.0.0.1:6580` 指向电脑的 6580：

   ```bat
   D:\softWare\android-sdk\platform-tools\adb.exe reverse tcp:6580 tcp:6580
   D:\softWare\android-sdk\platform-tools\adb.exe reverse --list
   ```

   `reverse --list` 的输出里应出现 `tcp:6580 tcp:6580`。

   App 首次打开在登录页填写 `http://127.0.0.1:6580` 并「保存并检测」，配合本映射即可连接；登录后也可在「设置 → 连接与关于」更换。
   **注意**：拔插 USB、重启 adb 或电脑后映射会失效，需要重新执行；手机端提示「无法连接服务器」时先查这一步。

3. **局域网联调（不接 USB）**：服务端用 `Kestrel__Endpoints__Http__Url=http://0.0.0.0:6580` 启动（见 §5.5-C）、防火墙放行；在登录页或登录后「设置 → 连接与关于」把服务器地址改为 `http://<电脑IPv4>:6580`（不能填 `127.0.0.1`）。

4. **Tailscale 联调（手机与服务器不在同一局域网）**：服务器按 `docs/deploy.md` §3.4 用 `tailscale serve` 提供 HTTPS
   （`https://chat-server.<tailnet>.ts.net`）；手机安装 Tailscale 并**确保已连接**（Android 通知栏/应用内显示 Connected，
   设备需与服务器在同一 tailnet），然后在登录页/设置页把服务器地址填为该 HTTPS 地址。
   - 不要填 `127.0.0.1`：它指向手机自身，只在本机或 USB 端口映射（`adb reverse`）时有效。
   - MagicDNS 未开启时可用 `https://100.x.y.z` 形式访问（Tailscale 自带证书需域名，优先用 MagicDNS 域名）。
   - 登录页若提示「当前是本机回环地址」，按提示改为服务器可达地址即可。
   - 手机能打开 `https://chat-server.<tailnet>.ts.net` 但 App 提示无法连接时，先确认 Tailscale 处于连接状态且未被系统省电冻结。

5. **注册/登录**：登录页切「注册」→ 输入邮箱 →「发送验证码」→ 验证码 `123456`（服务端控制台会打印 `[email][mock] ... code=123456`，不写日志文件）→ 填昵称 → 注册并登录。

6. **启动 App（可选，免手动点图标）**：

   ```bat
   D:\softWare\android-sdk\platform-tools\adb.exe shell monkey -p com.erisechat.mobile -c android.intent.category.LAUNCHER 1
   ```

   截图排障：`adb exec-out screencap -p > D:\Code\EriseChat\docs\test\phone.png`（`docs/test/` 不入库）。

6. **查看手机日志（排障用）**：

   ```bat
   D:\softWare\android-sdk\platform-tools\adb.exe logcat -s Capacitor:E Capacitor/Console:V chromium:E
   ```

### 5.7 真机验收清单（每次打包后建议过一遍）

| # | 项目 | 期望 |
|---|---|---|
| 1 | 注册/登录 | 验证码登录、密码登录、注册三模式可用；错误提示为中文且不泄露 token |
| 2 | 消息收发 | 单聊/群聊收发文本、图片、文件；图片可预览、文件可下载 |
| 3 | 消息可靠性 | 断网/服务端重启后重连自动补拉，无丢失、无重复、seq 连续 |
| 4 | 已读/输入中/在线 | 滚到底上报已读；单聊「对方正在输入…」；联系人/群成员在线点 |
| 5 | 群聊管理 | 建群、邀请、移除、退出；成员面板可开合 |
| 6 | 键盘与安全区 | 点击输入框键盘不遮挡输入区；收起键盘后布局恢复 |
| 7 | 返回键 | 面板（群成员/表情/图片预览）打开时先关面板，再返回会话列表 |
| 8 | 底部导航 | 消息/联系人/群聊/收藏/设置五项切换正常；进入会话详情时隐藏 |
| 9 | 后台恢复 | 杀进程/切后台再打开，自动补拉期间新消息不丢 |
| 10 | 退出登录 | 退出后回到登录页，无残留会话/未读角标 |
| 11 | 语音消息 | 首次点麦克风弹出系统录音权限；允许后录音条计时，发送后双方气泡可播放；取消/Esc 不发送；拒绝权限时提示「未授权使用麦克风」且无崩溃 |
| 12 | 客户端更新 | 设置「关于 → 检查更新」可请求服务端 `/api/version`；无更新提示「已是最新版本」；用临时 9.9.9 产物验证时显示「可下载并安装」，点「安装更新」下载 APK 并校验 sha256 后出现系统安装器（Android 11+ 首次需允许「安装未知应用」）；取消安装后应用仍可正常使用 |
| 13 | 外部链接 | 消息里的 http(s) 链接点击后用系统浏览器/自定义标签页打开，应用自身 WebView 不跳走，按返回键仍回到原会话；`javascript:`、`data:` 等非 http(s) 文本不可点 |

### 5.8 一键流程速查（复制即用）

**① Git Bash：打包**（含最新 Web UI；成功后生成 `data\downloads\EriseChat-1.2.0.apk`）

```bat
cd /d/Code/EriseChat
"D:\Git\bin\bash.exe" infra/build-apk.sh 1.2.0
```

**② cmd：USB 安装到已连接的手机**（adb 在 `D:\softWare\android-sdk\platform-tools\`）

```bat
D:\softWare\android-sdk\platform-tools\adb.exe devices
D:\softWare\android-sdk\platform-tools\adb.exe install -r data\downloads\EriseChat-1.2.0.apk
```

**③ 另开终端：启动服务端**（开发模式，验证码固定 `123456`）

```bat
cd /d D:\Code\EriseChat\server\Chat.Server
dotnet run
```

**④ 回到 cmd：USB 端口反向映射**（App 服务器地址保持 `127.0.0.1:6580`）

```bat
D:\softWare\android-sdk\platform-tools\adb.exe reverse tcp:6580 tcp:6580
```

**⑤ 手机打开 EriseChat → 注册/登录 → 按 §5.7 清单验收。**

> 若走局域网而不接 USB：③ 改为 `dotnet run --urls http://0.0.0.0:6580`、放行防火墙 6580，④ 跳过，App 内服务器地址改填 `http://<电脑IPv4>:6580`。

## 6. 故障排查

| 现象 | 排查 |
|---|---|
| 在 cmd 执行 `bash infra/build-apk.sh` 报 `WSL (10 - Relay) ERROR ... execvpe(/bin/bash) failed` | cmd 的 `bash` 指向 WSL，不是 Git Bash。改用 `"D:\Git\bin\bash.exe" infra/build-apk.sh 1.2.0`，或开始菜单打开 Git Bash 执行（见 §5.3） |
| 打包报 `ERROR: 找不到 JDK` / `缺少 local.properties` | 按 §5.1 设置 `JAVA_HOME`（默认 `D:/softWare/jdk21`）与 `apps/mobile/android/local.properties` 的 `sdk.dir` |
| Gradle 下载依赖很慢/失败 | 首次构建需下载 Gradle/AGP；`gradle-wrapper.properties` 已配腾讯镜像，网络受限时检查代理 |
| `adb devices` 为空 / `unauthorized` | 打开手机开发者选项与 USB 调试；换数据线（需支持数据传输）；手机上点「允许 USB 调试」；`adb kill-server && adb start-server` 后重试 |
| `adb install` 报 `INSTALL_FAILED_UPDATE_INCOMPATIBLE` | 新旧 APK 签名不同（keystore 变化）→ 先 `adb uninstall com.erisechat.mobile` 再安装（服务端聊天数据不受影响） |
| `adb install` 报 `INSTALL_FAILED_VERSION_DOWNGRADE` | 新包 `versionCode` 比已安装的小；按 `major*10000+minor*100+patch` 递增后重打包 |
| App 登录提示「无法连接服务器」 | ① 服务端是否已启动（`curl http://127.0.0.1:6580/api/health`）；② 登录页「服务器地址」是否已填写且「保存并检测」成功（打包态仅登录页/设置页可改）；③ USB 联调是否执行过 `adb reverse tcp:6580 tcp:6580`（拔插 USB 后需重做，`adb reverse --list` 确认）；④ 局域网/Tailscale 联调时服务器是否监听可达地址（`Kestrel__Endpoints__Http__Url=http://0.0.0.0:6580` 或 `tailscale serve`）且防火墙放行；⑤ 服务器地址不能填 `127.0.0.1`（除本机/USB 映射外）；⑥ Tailscale 场景确认手机 Tailscale 已连接且与服务器同一 tailnet |
| 手机访问 `/download` 打不开 | 开发默认只监听 `127.0.0.1:6580`；按 §5.5-C 用 `Kestrel__Endpoints__Http__Url=http://0.0.0.0:6580` 启动并放行防火墙（同一 Wi-Fi），或走 Tailscale Serve 的 HTTPS 地址 |
| 页面白屏 | 确认打包前 `apps/web` 已用 `base: './'` 构建（M4-07）；重新执行 `cap sync` 后打包 |
| 安装被拦截 | 允许「安装未知应用」；确认 APK 完整（对比下载页/`sha256sum` 输出的 SHA256） |
| 收不到新消息提醒 | v1 无系统推送，打开应用会自动补拉；后台被系统冻结属预期 |
| 覆盖安装后仍是旧界面 | 打包时未包含最新 Web 产物：重新 `bash infra/build-apk.sh`（脚本内含 `npm run build` 与 `cap sync`），并核对安装包大小/SHA256 已更新 |
